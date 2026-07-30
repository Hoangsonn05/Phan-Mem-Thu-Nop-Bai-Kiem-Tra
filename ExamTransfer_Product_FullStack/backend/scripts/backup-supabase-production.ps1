[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,

    [Parameter(Mandatory)]
    [string]$Confirmation,

    [string]$OutputRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ExamTransfer-Private-Backups\Supabase'),

    [string]$BackupSetPath,

    [string]$DatabaseUrl = $env:SUPABASE_DB_URL,

    [ValidateSet('native', 'https')]
    [string]$DnsResolver = 'https'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory)][Security.SecureString]$Value)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

if ($Confirmation -cne "BACKUP DATABASE $ProjectRef") {
    throw "Confirmation mismatch. Supply exactly: BACKUP DATABASE $ProjectRef"
}
if ([string]::IsNullOrWhiteSpace($DatabaseUrl)) {
    $secureUrl = Read-Host 'Supabase PostgreSQL connection URL (input is hidden and never saved)' -AsSecureString
    $DatabaseUrl = Convert-SecureStringToPlainText $secureUrl
}
if ([string]::IsNullOrWhiteSpace($DatabaseUrl)) {
    throw 'SUPABASE_DB_URL or a securely entered PostgreSQL connection URL is required.'
}
if ($DatabaseUrl -notmatch '^postgres(ql)?://') {
    throw 'SUPABASE_DB_URL must be a PostgreSQL connection URL.'
}
$decodedUrl = [Uri]::UnescapeDataString($DatabaseUrl)
if ($decodedUrl.IndexOf($ProjectRef, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'The PostgreSQL connection URL does not contain the confirmed Project Ref. Use the Direct connection or Session pooler URL copied from this exact Supabase project.'
}
if (-not (Get-Command supabase -ErrorAction SilentlyContinue)) {
    throw 'Supabase CLI is required.'
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required by supabase db dump.'
}
Invoke-NativeCommandCaptured -Command 'docker' -Arguments @('info') | Out-Null

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$setPath = if ([string]::IsNullOrWhiteSpace($BackupSetPath)) {
    Join-Path $OutputRoot "$ProjectRef\$timestamp-UTC"
} else {
    [IO.Path]::GetFullPath($BackupSetPath)
}
$backupDir = Join-Path $setPath 'database'
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

function Invoke-SupabaseDump {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [AllowEmptyCollection()]
        [string[]]$DumpArguments = @()
    )

    $target = Join-Path $backupDir $FileName
    $arguments = Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver `
        -Arguments (@('db', 'dump', '--db-url', $DatabaseUrl, '-f', $target) + @($DumpArguments))
    Invoke-NativeCommandCaptured -Command 'supabase' -Arguments $arguments `
        -SensitiveValues @($DatabaseUrl) -FailureContext "dnsResolver=$DnsResolver" | Out-Null
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        (Get-Item -LiteralPath $target).Length -le 0) {
        throw "Database dump is empty: $FileName"
    }
    return $target
}

try {
    $schemaFile = Invoke-SupabaseDump -FileName 'schema.sql' -DumpArguments @()
    $dataFile = Invoke-SupabaseDump -FileName 'data.sql' -DumpArguments @(
        '--data-only', '--use-copy',
        '-x', 'storage.buckets_vectors',
        '-x', 'storage.vector_indexes'
    )
    $rolesFile = Invoke-SupabaseDump -FileName 'roles.sql' -DumpArguments @('--role-only')
    $migrationSchemaFile = Invoke-SupabaseDump -FileName 'migration-history-schema.sql' -DumpArguments @(
        '--schema', 'supabase_migrations'
    )
    $migrationDataFile = Invoke-SupabaseDump -FileName 'migration-history-data.sql' -DumpArguments @(
        '--data-only', '--use-copy', '--schema', 'supabase_migrations'
    )

    $cliVersionPath = Join-Path $backupDir 'supabase-cli-version.txt'
    $cliVersion = (Invoke-NativeCommandCaptured -Command 'supabase' -Arguments @('--version')).OutputText
    if ([string]::IsNullOrWhiteSpace($cliVersion)) {
        throw 'Could not record the Supabase CLI version.'
    }
    Write-Utf8NoBomFile -Path $cliVersionPath -Content ($cliVersion + [Environment]::NewLine)

    $files = @($schemaFile, $dataFile, $rolesFile, $migrationSchemaFile, $migrationDataFile, $cliVersionPath) | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [ordered]@{
            name = $item.Name
            bytes = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $manifest = [ordered]@{
        formatVersion = 2
        kind = 'ExamTransferSupabaseDatabaseBackup'
        projectRef = $ProjectRef
        createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        tool = 'supabase db dump'
        connectionIdentityCheck = 'Project Ref was present in the confirmed PostgreSQL URL'
        files = @($files)
    }
    $manifestPath = Join-Path $backupDir 'database-manifest.json'
    Write-Utf8NoBomFile -Path $manifestPath -Content ($manifest | ConvertTo-Json -Depth 6)

    $archivePath = Join-Path (Split-Path $backupDir -Parent) 'database-backup.zip'
    Compress-Archive -Path (Join-Path $backupDir '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force
    if (-not (Test-Path -LiteralPath $archivePath) -or (Get-Item $archivePath).Length -le 0) {
        throw 'Database backup archive was not created.'
    }
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $(Split-Path $archivePath -Leaf)" |
        Set-Content -LiteralPath "$archivePath.sha256" -Encoding ascii

    Write-Host "DATABASE_BACKUP_COMPLETE path=$archivePath" -ForegroundColor Green
    [pscustomobject]@{
        Kind = 'Database'
        ProjectRef = $ProjectRef
        Directory = $backupDir
        Archive = $archivePath
        Sha256 = $archiveHash
    }
} catch {
    Write-Error "DATABASE_BACKUP_INCOMPLETE path=$backupDir reason=$($_.Exception.Message)"
    throw
} finally {
    $DatabaseUrl = $null
    $decodedUrl = $null
}
