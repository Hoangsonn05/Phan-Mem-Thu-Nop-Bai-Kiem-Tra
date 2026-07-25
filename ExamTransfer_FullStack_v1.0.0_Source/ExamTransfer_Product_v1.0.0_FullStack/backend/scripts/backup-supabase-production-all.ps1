[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,

    [Parameter(Mandatory)]
    [string]$Confirmation,

    [string]$OutputRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ExamTransfer-Private-Backups\Supabase'),

    [string]$DatabaseUrl = $env:SUPABASE_DB_URL,

    [string]$ServiceRoleKey = $env:SUPABASE_SERVICE_ROLE_KEY,

    [ValidateSet('native', 'https')]
    [string]$DnsResolver = 'https'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

if ([string]::IsNullOrWhiteSpace($ServiceRoleKey)) {
    $ServiceRoleKey = $env:EXAMTRANSFER_SUPABASE_SERVICE_KEY
}

if ($Confirmation -cne "BACKUP ALL $ProjectRef") {
    throw "Confirmation mismatch. Supply exactly: BACKUP ALL $ProjectRef"
}

function Get-SafeBackupExceptionDiagnostic {
    param([Parameter(Mandatory)][System.Management.Automation.ErrorRecord]$ErrorRecord)

    $exception = $ErrorRecord.Exception
    $inner = if ($null -eq $exception.InnerException) { '' } else { $exception.InnerException.Message }
    return [ordered]@{
        scriptName = Split-Path -Leaf $ErrorRecord.InvocationInfo.ScriptName
        lineNumber = $ErrorRecord.InvocationInfo.ScriptLineNumber
        positionMessage = Protect-NativeCommandOutput -Text ([string]$ErrorRecord.InvocationInfo.PositionMessage) `
            -SensitiveValues @($DatabaseUrl, $ServiceRoleKey)
        exceptionType = $exception.GetType().FullName
        message = Protect-NativeCommandOutput -Text $exception.Message `
            -SensitiveValues @($DatabaseUrl, $ServiceRoleKey)
        innerException = Protect-NativeCommandOutput -Text $inner `
            -SensitiveValues @($DatabaseUrl, $ServiceRoleKey)
    }
}

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$setPath = Join-Path $OutputRoot "$ProjectRef\$timestamp-UTC"
New-Item -ItemType Directory -Path $setPath -Force | Out-Null

try {
    & (Join-Path $PSScriptRoot 'backup-supabase-production.ps1') `
        -ProjectRef $ProjectRef `
        -Confirmation "BACKUP DATABASE $ProjectRef" `
        -OutputRoot $OutputRoot `
        -BackupSetPath $setPath `
        -DatabaseUrl $DatabaseUrl `
        -DnsResolver $DnsResolver | Out-Host

    & (Join-Path $PSScriptRoot 'backup-supabase-storage.ps1') `
        -ProjectRef $ProjectRef `
        -Confirmation "BACKUP STORAGE $ProjectRef" `
        -OutputRoot $OutputRoot `
        -BackupSetPath $setPath `
        -ServiceRoleKey $ServiceRoleKey | Out-Host

    $manifest = [ordered]@{
        formatVersion = 1
        kind = 'ExamTransferSupabaseBackupSet'
        projectRef = $ProjectRef
        createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        includes = @('database', 'storage')
    }
    $setManifestPath = Join-Path $setPath 'backup-set-manifest.json'
    Write-Utf8NoBomFile -Path $setManifestPath -Content ($manifest | ConvertTo-Json -Depth 4)
    $setManifestHash = (Get-FileHash -LiteralPath $setManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setManifestHash  backup-set-manifest.json" |
        Set-Content -LiteralPath (Join-Path $setPath 'backup-set-manifest.sha256') -Encoding ascii

    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'verify-supabase-production-backup.ps1') `
        -ProjectRef $ProjectRef `
        -BackupSetPath $setPath
    if ($LASTEXITCODE -ne 0) { throw 'Backup verification did not return BACKUP_READY.' }

    Write-Host "BACKUP_SET_READY path=$setPath" -ForegroundColor Green
} catch {
    $diagnostic = Get-SafeBackupExceptionDiagnostic -ErrorRecord $_
    Write-Error ('BACKUP_SET_INCOMPLETE diagnostic=' + ($diagnostic | ConvertTo-Json -Compress))
    throw
} finally {
    $DatabaseUrl = $null
    $ServiceRoleKey = $null
}
