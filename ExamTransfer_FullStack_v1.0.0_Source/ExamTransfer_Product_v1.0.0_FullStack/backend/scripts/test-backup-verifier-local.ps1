[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRef = 'abcdefghijklmnopqrst'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "examtransfer-backup-verify-$([Guid]::NewGuid().ToString('N'))"
$setPath = Join-Path $testRoot 'set'
$databasePath = Join-Path $setPath 'database'
$storagePath = Join-Path $setPath 'storage'
$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

function Write-ArchiveAndHash([string]$Source, [string]$Archive) {
    Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $Archive -Force
    $hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $Archive -Leaf)" | Set-Content -LiteralPath "$Archive.sha256" -Encoding ascii
}

try {
    New-Item -ItemType Directory -Path $databasePath, (Join-Path $storagePath 'exam-archives') -Force | Out-Null
    $databaseFiles = @()
    foreach ($name in @('schema.sql', 'data.sql', 'roles.sql', 'migration-history-schema.sql', 'migration-history-data.sql')) {
        $path = Join-Path $databasePath $name
        "local validation fixture for $name" | Set-Content -LiteralPath $path -Encoding utf8
        $item = Get-Item -LiteralPath $path
        $databaseFiles += [ordered]@{
            name = $name
            bytes = $item.Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    [ordered]@{
        kind = 'ExamTransferSupabaseDatabaseBackup'
        projectRef = $projectRef
        files = $databaseFiles
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $databasePath 'database-manifest.json') -Encoding utf8

    $objectRelativePath = 'objects/bucket-local/fixture.blob'
    $objectPath = Join-Path $storagePath ($objectRelativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path $objectPath -Parent) -Force | Out-Null
    'local storage fixture' | Set-Content -LiteralPath $objectPath -Encoding utf8
    $object = Get-Item -LiteralPath $objectPath
    [ordered]@{
        kind = 'ExamTransferSupabaseStorageBackup'
        projectRef = $projectRef
        expectedObjectCount = 1
        downloadedObjectCount = 1
        objectCount = 1
        errorCount = 0
        missingRequiredBuckets = @()
        discoveredBuckets = @('exam-archives','submission-archives','report-exports','backup-archives')
        objects = @([ordered]@{
            bucket = 'exam-archives'
            path = 'fixture.zip'
            localRelativePath = $objectRelativePath
            bytes = $object.Length
            sha256 = (Get-FileHash -LiteralPath $objectPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
        errors = @()
    } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $storagePath 'storage-manifest.json') -Encoding utf8

    Write-ArchiveAndHash $databasePath (Join-Path $setPath 'database-backup.zip')
    Write-ArchiveAndHash $storagePath (Join-Path $setPath 'storage-backup.zip')

    $setManifestPath = Join-Path $setPath 'backup-set-manifest.json'
    [ordered]@{
        kind = 'ExamTransferSupabaseBackupSet'
        projectRef = $projectRef
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        includes = @('database', 'storage')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $setManifestPath -Encoding utf8
    $setHash = (Get-FileHash -LiteralPath $setManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setHash  backup-set-manifest.json" |
        Set-Content -LiteralPath (Join-Path $setPath 'backup-set-manifest.sha256') -Encoding ascii

    & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'verify-supabase-production-backup.ps1') `
        -ProjectRef $projectRef -BackupSetPath $setPath
    if ($LASTEXITCODE -ne 0) { throw 'Valid local backup fixture was rejected.' }

    Add-Content -LiteralPath (Join-Path $setPath 'database-backup.zip') -Value 'corruption'
    & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'verify-supabase-production-backup.ps1') `
        -ProjectRef $projectRef -BackupSetPath $setPath
    if ($LASTEXITCODE -eq 0) { throw 'Corrupted local backup fixture was accepted.' }

    Write-Host 'PASS code=BACKUP_VERIFIER_LOCAL_OK detail=valid set accepted; corrupted archive rejected' -ForegroundColor Green
} finally {
    if ((Resolve-Path -LiteralPath $testRoot -ErrorAction SilentlyContinue).Path.StartsWith(
        [IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

