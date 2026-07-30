[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,

    [Parameter(Mandatory)]
    [string]$Confirmation,

    [string]$OutputRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ExamTransfer-Private-Backups\Supabase'),

    [string]$BackupSetPath,

    [string]$ServiceRoleKey = $env:SUPABASE_SERVICE_ROLE_KEY,

    [ValidateRange(1, 10)]
    [int]$MaxRetries = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

if ([string]::IsNullOrWhiteSpace($ServiceRoleKey)) {
    $ServiceRoleKey = $env:EXAMTRANSFER_SUPABASE_SERVICE_KEY
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][string]$Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

if ($Confirmation -cne "BACKUP STORAGE $ProjectRef") {
    throw "Confirmation mismatch. Supply exactly: BACKUP STORAGE $ProjectRef"
}
if ([string]::IsNullOrWhiteSpace($ServiceRoleKey)) {
    $secureKey = Read-Host 'Supabase service-role key (input is hidden and never saved)' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try { $ServiceRoleKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
if ([string]::IsNullOrWhiteSpace($ServiceRoleKey)) {
    throw 'A service-role key is required for a complete Storage backup.'
}

$supabaseUrl = "https://$ProjectRef.supabase.co"
$headers = @{
    apikey = $ServiceRoleKey
    Authorization = "Bearer $ServiceRoleKey"
}
$requiredBeforeUpdateBuckets = @(
    'exam-archives',
    'submission-archives',
    'report-exports',
    'backup-archives'
)
$knownApplicationBuckets = @($requiredBeforeUpdateBuckets + 'public-submission-archives')
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$setPath = if ([string]::IsNullOrWhiteSpace($BackupSetPath)) {
    Join-Path $OutputRoot "$ProjectRef\$timestamp-UTC"
} else {
    [IO.Path]::GetFullPath($BackupSetPath)
}
$backupDir = Join-Path $setPath 'storage'
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory)][scriptblock]$Operation,
        [Parameter(Mandatory)][string]$Description
    )

    for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
        try { return & $Operation }
        catch {
            if ($attempt -eq $MaxRetries) { throw }
            $delay = [int][Math]::Min(8, [Math]::Pow(2, $attempt - 1))
            Write-Warning "$Description failed (attempt $attempt/$MaxRetries); retrying in $delay second(s)."
            Start-Sleep -Seconds $delay
        }
    }
}

function ConvertTo-StoragePath {
    param([Parameter(Mandatory)][string]$ObjectPath)
    return (($ObjectPath -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
}

function Get-StorageObjects {
    param(
        [Parameter(Mandatory)][string]$Bucket,
        [string]$Prefix = ''
    )

    $offset = 0
    $limit = 1000
    do {
        $body = @{
            prefix = $Prefix
            limit = $limit
            offset = $offset
            sortBy = @{ column = 'name'; order = 'asc' }
        } | ConvertTo-Json -Depth 4
        $uri = "$supabaseUrl/storage/v1/object/list/$([Uri]::EscapeDataString($Bucket))"
        $page = @(Invoke-WithRetry -Description "List Storage prefix $Bucket/$Prefix" -Operation {
            Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/json' -Body $body
        })

        foreach ($entry in $page) {
            $name = [string]$entry.name
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $path = if ([string]::IsNullOrWhiteSpace($Prefix)) { $name } else { "$Prefix/$name" }
            if ($null -eq $entry.id -and $null -eq $entry.metadata) {
                Get-StorageObjects -Bucket $Bucket -Prefix $path
            } else {
                [pscustomobject]@{
                    Bucket = $Bucket
                    Path = $path
                    ExpectedSize = if ($entry.metadata -and $null -ne $entry.metadata.size) {
                        [long]$entry.metadata.size
                    } else { $null }
                }
            }
        }
        $offset += $page.Count
    } while ($page.Count -eq $limit)
}

function Get-LocalObjectPath {
    param([Parameter(Mandatory)][string]$Bucket, [Parameter(Mandatory)][string]$ObjectPath)
    $bucketHash = (Get-TextSha256 $Bucket).Substring(0, 20)
    $objectHash = Get-TextSha256 ($Bucket + "`n" + $ObjectPath)
    $relative = "objects\bucket-$bucketHash\$objectHash.blob"
    $full = [IO.Path]::GetFullPath((Join-Path $backupDir $relative))
    $rootFull = [IO.Path]::GetFullPath($backupDir).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Resolved Storage backup path escaped the backup directory.'
    }
    return [pscustomobject]@{ Relative = $relative; Full = $full }
}

$records = [System.Collections.Generic.List[object]]::new()
$errors = [System.Collections.Generic.List[object]]::new()
try {
    $buckets = @(Invoke-WithRetry -Description 'List Storage buckets' -Operation {
        Invoke-RestMethod -Method Get -Uri "$supabaseUrl/storage/v1/bucket" -Headers $headers
    })
    $existingNames = @($buckets | ForEach-Object { [string]$_.name } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    $missingRequired = @($requiredBeforeUpdateBuckets | Where-Object { $existingNames -notcontains $_ })

    # Back up every user-created bucket returned by the project, not just the
    # buckets currently known to ExamTransfer. This prevents a future/custom
    # bucket from being silently omitted.
    foreach ($bucket in $existingNames) {
        foreach ($object in @(Get-StorageObjects -Bucket $bucket)) {
            $mapped = Get-LocalObjectPath -Bucket $bucket -ObjectPath $object.Path
            New-Item -ItemType Directory -Path (Split-Path $mapped.Full -Parent) -Force | Out-Null
            $encodedPath = ConvertTo-StoragePath -ObjectPath $object.Path
            $downloadUri = "$supabaseUrl/storage/v1/object/authenticated/$([Uri]::EscapeDataString($bucket))/$encodedPath"

            try {
                Invoke-WithRetry -Description "Download Storage object $bucket/$($object.Path)" -Operation {
                    Invoke-WebRequest -UseBasicParsing -Method Get -Uri $downloadUri -Headers $headers -OutFile $mapped.Full
                } | Out-Null
                $item = Get-Item -LiteralPath $mapped.Full
                if ($null -ne $object.ExpectedSize -and $item.Length -ne $object.ExpectedSize) {
                    throw "Size mismatch: expected $($object.ExpectedSize), downloaded $($item.Length)."
                }
                $records.Add([ordered]@{
                    bucket = $bucket
                    path = $object.Path
                    localRelativePath = $mapped.Relative.Replace('\','/')
                    bytes = $item.Length
                    sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                })
            } catch {
                $errors.Add([ordered]@{
                    bucket = $bucket
                    path = $object.Path
                    error = $_.Exception.Message
                })
            }
        }
    }

    $manifest = [ordered]@{
        formatVersion = 2
        kind = 'ExamTransferSupabaseStorageBackup'
        projectRef = $ProjectRef
        createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        requiredBeforeUpdateBuckets = $requiredBeforeUpdateBuckets
        knownApplicationBuckets = $knownApplicationBuckets
        discoveredBuckets = $existingNames
        missingRequiredBuckets = $missingRequired
        expectedObjectCount = $records.Count + $errors.Count
        downloadedObjectCount = $records.Count
        objectCount = $records.Count
        errorCount = $errors.Count
        objects = @($records)
        errors = @($errors)
    }
    $manifestPath = Join-Path $backupDir 'storage-manifest.json'
    Write-Utf8NoBomFile -Path $manifestPath -Content ($manifest | ConvertTo-Json -Depth 8)

    if ($missingRequired.Count -gt 0) {
        throw "Required pre-update Storage bucket(s) are missing: $($missingRequired -join ', '). Run production preflight before migration."
    }
    if ($errors.Count -gt 0) {
        throw "$($errors.Count) Storage object(s) could not be backed up."
    }

    $archivePath = Join-Path (Split-Path $backupDir -Parent) 'storage-backup.zip'
    Compress-Archive -Path (Join-Path $backupDir '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force
    if (-not (Test-Path -LiteralPath $archivePath) -or (Get-Item $archivePath).Length -le 0) {
        throw 'Storage backup archive was not created.'
    }
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $(Split-Path $archivePath -Leaf)" |
        Set-Content -LiteralPath "$archivePath.sha256" -Encoding ascii

    Write-Host "STORAGE_BACKUP_COMPLETE buckets=$($existingNames.Count) objects=$($records.Count) path=$archivePath" -ForegroundColor Green
    [pscustomobject]@{
        Kind = 'Storage'
        ProjectRef = $ProjectRef
        Directory = $backupDir
        Archive = $archivePath
        Sha256 = $archiveHash
        ObjectCount = $records.Count
    }
} catch {
    Write-Error "STORAGE_BACKUP_INCOMPLETE path=$backupDir reason=$($_.Exception.Message)"
    throw
} finally {
    $ServiceRoleKey = $null
    $headers = $null
}
