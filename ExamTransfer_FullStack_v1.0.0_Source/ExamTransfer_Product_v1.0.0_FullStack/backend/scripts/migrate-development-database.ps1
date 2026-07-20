param(
    [string]$SourceDatabase = (Join-Path $PSScriptRoot "..\src\ExamTransfer.LocalServer\data\database\exam-transfer.db"),
    [string]$DestinationDatabase = (Join-Path $env:ProgramData "ExamTransfer\database\exam-transfer.db"),
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$source = [IO.Path]::GetFullPath($SourceDatabase)
$destination = [IO.Path]::GetFullPath($DestinationDatabase)
Write-Host "SOURCE      $source"
Write-Host "DESTINATION $destination"

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Source database does not exist. Nothing was changed: $source"
}

$sourceInfo = Get-Item -LiteralPath $source
Write-Host "SOURCE INFO size=$($sourceInfo.Length) modified=$($sourceInfo.LastWriteTimeUtc.ToString('o'))"

if (Test-Path -LiteralPath $destination) {
    $destinationInfo = Get-Item -LiteralPath $destination
    Write-Host "DEST INFO   size=$($destinationInfo.Length) modified=$($destinationInfo.LastWriteTimeUtc.ToString('o'))"
    throw "Both databases exist. Refusing to merge or overwrite. Compare them manually and choose one explicitly."
}

if (-not $Execute) {
    Write-Host "PREVIEW PASS: destination is absent. Re-run with -Execute to back up the source and copy it once."
    exit 0
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "$source.before-programdata-migration-$timestamp.bak"
Copy-Item -LiteralPath $source -Destination $backup
Write-Host "BACKUP      $backup"

$destinationDirectory = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
$temporary = "$destination.copying-$timestamp"
Copy-Item -LiteralPath $source -Destination $temporary
if ((Get-Item -LiteralPath $temporary).Length -ne $sourceInfo.Length) {
    Remove-Item -LiteralPath $temporary -Force
    throw "Copied database size does not match source. Backup remains at $backup"
}
Move-Item -LiteralPath $temporary -Destination $destination
Write-Host "MIGRATION PASS: copied database without modifying the source."

