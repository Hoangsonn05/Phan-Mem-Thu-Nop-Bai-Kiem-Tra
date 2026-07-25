[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,

    [Parameter(Mandatory)]
    [string]$BackupSetPath,

    [ValidateRange(1, 168)]
    [int]$MaximumAgeHours = 24
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$issues = [System.Collections.Generic.List[string]]::new()

function Test-Archive {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Checksum
    )

    if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
        $issues.Add("Missing archive: $Archive")
        return
    }
    if ((Get-Item -LiteralPath $Archive).Length -le 0) {
        $issues.Add("Empty archive: $Archive")
        return
    }
    if (-not (Test-Path -LiteralPath $Checksum -PathType Leaf)) {
        $issues.Add("Missing checksum: $Checksum")
        return
    }
    $expected = ((Get-Content -LiteralPath $Checksum -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        $issues.Add("Checksum mismatch: $Archive")
    }
}

try {
    $resolved = (Resolve-Path -LiteralPath $BackupSetPath).Path
    $setManifestPath = Join-Path $resolved 'backup-set-manifest.json'
    if (-not (Test-Path -LiteralPath $setManifestPath -PathType Leaf)) {
        throw 'backup-set-manifest.json is missing.'
    }
    $setChecksumPath = Join-Path $resolved 'backup-set-manifest.sha256'
    if (-not (Test-Path -LiteralPath $setChecksumPath -PathType Leaf)) {
        $issues.Add('Backup-set manifest checksum is missing.')
    } else {
        $expectedSetHash = ((Get-Content -LiteralPath $setChecksumPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
        $actualSetHash = (Get-FileHash -LiteralPath $setManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expectedSetHash -ne $actualSetHash) {
            $issues.Add('Backup-set manifest checksum does not match.')
        }
    }
    $setManifest = Get-Content -LiteralPath $setManifestPath -Raw | ConvertFrom-Json
    if ($setManifest.projectRef -cne $ProjectRef) {
        $issues.Add('Project ref in the backup-set manifest does not match.')
    }
    $createdAt = [DateTimeOffset]::Parse([string]$setManifest.createdAtUtc)
    if (([DateTimeOffset]::UtcNow - $createdAt).TotalHours -gt $MaximumAgeHours) {
        $issues.Add("Backup is older than $MaximumAgeHours hour(s).")
    }

    $databaseManifestPath = Join-Path $resolved 'database\database-manifest.json'
    if (-not (Test-Path -LiteralPath $databaseManifestPath -PathType Leaf)) {
        $issues.Add('Database manifest is missing.')
    } else {
        $databaseManifest = Get-Content -LiteralPath $databaseManifestPath -Raw | ConvertFrom-Json
        if ($databaseManifest.projectRef -cne $ProjectRef) {
            $issues.Add('Project ref in database manifest does not match.')
        }
        foreach ($required in @('schema.sql', 'data.sql', 'roles.sql', 'migration-history-schema.sql', 'migration-history-data.sql')) {
            $record = @($databaseManifest.files | Where-Object name -eq $required)
            $path = Join-Path (Split-Path $databaseManifestPath -Parent) $required
            if ($record.Count -ne 1 -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                $issues.Add("Database backup file is missing from disk or manifest: $required")
                continue
            }
            $item = Get-Item -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($item.Length -le 0 -or $item.Length -ne [long]$record[0].bytes -or
                $hash -cne [string]$record[0].sha256) {
                $issues.Add("Database backup validation failed: $required")
            }
        }
    }

    $storageManifestPath = Join-Path $resolved 'storage\storage-manifest.json'
    if (-not (Test-Path -LiteralPath $storageManifestPath -PathType Leaf)) {
        $issues.Add('Storage manifest is missing.')
    } else {
        $storageManifest = Get-Content -LiteralPath $storageManifestPath -Raw | ConvertFrom-Json
        if ($storageManifest.projectRef -cne $ProjectRef) {
            $issues.Add('Project ref in Storage manifest does not match.')
        }
        if ([int]$storageManifest.errorCount -ne 0) {
            $issues.Add('Storage manifest contains backup errors.')
        }
        if ($null -ne $storageManifest.missingRequiredBuckets -and
            @($storageManifest.missingRequiredBuckets).Count -gt 0) {
            $issues.Add('Required pre-update Storage bucket(s) were missing during backup.')
        }
        if ([int]$storageManifest.downloadedObjectCount -ne [int]$storageManifest.expectedObjectCount) {
            $issues.Add('Storage expected/downloaded object counts do not match.')
        }
        if (@($storageManifest.objects).Count -ne [int]$storageManifest.objectCount) {
            $issues.Add('Storage object count does not match the manifest.')
        }
        $storageRoot = [IO.Path]::GetFullPath((Split-Path $storageManifestPath -Parent))
        $storageRootPrefix = $storageRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($record in @($storageManifest.objects)) {
            $relative = if ($null -ne $record.localRelativePath -and
                -not [string]::IsNullOrWhiteSpace([string]$record.localRelativePath)) {
                ([string]$record.localRelativePath) -replace '/', [IO.Path]::DirectorySeparatorChar
            } else {
                Join-Path ([string]$record.bucket) (([string]$record.path) -replace '/', [IO.Path]::DirectorySeparatorChar)
            }
            $path = [IO.Path]::GetFullPath((Join-Path $storageRoot $relative))
            if (-not $path.StartsWith($storageRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $issues.Add("Storage manifest path escaped the backup directory: $($record.bucket)/$($record.path)")
                continue
            }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                $issues.Add("Storage object missing: $($record.bucket)/$($record.path)")
                continue
            }
            $item = Get-Item -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($item.Length -ne [long]$record.bytes -or $hash -cne [string]$record.sha256) {
                $issues.Add("Storage object validation failed: $($record.bucket)/$($record.path)")
            }
        }
    }

    Test-Archive -Archive (Join-Path $resolved 'database-backup.zip') `
        -Checksum (Join-Path $resolved 'database-backup.zip.sha256')
    Test-Archive -Archive (Join-Path $resolved 'storage-backup.zip') `
        -Checksum (Join-Path $resolved 'storage-backup.zip.sha256')

    if ($issues.Count -gt 0) {
        $issues | ForEach-Object { Write-Warning $_ }
        Write-Host 'BACKUP_INVALID' -ForegroundColor Red
        exit 2
    }

    Write-Host 'BACKUP_READY' -ForegroundColor Green
    exit 0
} catch {
    Write-Warning $_.Exception.Message
    Write-Host 'BACKUP_INCOMPLETE' -ForegroundColor Red
    exit 1
}
