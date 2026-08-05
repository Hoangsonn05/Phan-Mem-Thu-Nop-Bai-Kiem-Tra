[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$expectedBuild = [IO.Path]::GetFullPath((Join-Path $root 'build-release.ps1'))
$expectedInstaller = [IO.Path]::GetFullPath((Join-Path $root 'installer\ExamTransfer.iss'))
$expectedIcon = [IO.Path]::GetFullPath((Join-Path $root 'installer\assets\Khoa-DT-KTMT.ico'))

function Assert-OneCanonicalFile(
    [IO.FileInfo[]]$Files,
    [string]$ExpectedPath,
    [string]$Label) {
    if ($Files.Count -ne 1 -or
        -not [string]::Equals(
            $Files[0].FullName,
            $ExpectedPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        $actual = @($Files | ForEach-Object FullName) -join '; '
        throw "CANONICAL_RELEASE_LAYOUT_INVALID: $Label expected=$ExpectedPath actual=$actual"
    }
}

$buildScripts = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'build-release*.ps1')
$installerScripts = @(Get-ChildItem -LiteralPath (Join-Path $root 'installer') -Recurse -File -Filter '*.iss')
$icons = @(Get-ChildItem -LiteralPath (Join-Path $root 'installer') -Recurse -File -Filter '*.ico')

Assert-OneCanonicalFile $buildScripts $expectedBuild 'build-script'
Assert-OneCanonicalFile $installerScripts $expectedInstaller 'installer-script'
Assert-OneCanonicalFile $icons $expectedIcon 'installer-icon'

$buildSource = Get-Content -LiteralPath $expectedBuild -Raw
foreach ($contract in @(
    '$root = $PSScriptRoot',
    'installer\assets\Khoa-DT-KTMT.ico',
    '"-p:ApplicationIcon=$appIcon"',
    'Khoa-DT-KTMT-Setup-$Version.exe',
    'RELEASE_WORKTREE_DIRTY')) {
    if ($buildSource.IndexOf($contract, [StringComparison]::Ordinal) -lt 0) {
        throw "CANONICAL_BUILD_CONTRACT_MISSING: $contract"
    }
}
if ($buildSource.IndexOf('scripts\build-release.ps1', [StringComparison]::Ordinal) -ge 0) {
    throw 'CANONICAL_BUILD_IS_WRAPPER: root build delegates to a second implementation.'
}

$installerSource = Get-Content -LiteralPath $expectedInstaller -Raw
foreach ($contract in @(
    '#define MyAppName "Khoa-DT-KTMT"',
    '#define MyAppPublisher "Khoa-DT-KTMT"',
    'SetupIconFile={#MyAppIcon}',
    'OutputBaseFilename=Khoa-DT-KTMT-Setup-{#MyAppVersion}',
    'AppId={#MyAppId}',
    'ExamTransfer.Desktop.exe',
    'ExamTransfer.LocalServer.exe')) {
    if ($installerSource.IndexOf($contract, [StringComparison]::Ordinal) -lt 0) {
        throw "CANONICAL_INSTALLER_CONTRACT_MISSING: $contract"
    }
}

Write-Host 'PASS canonical-release-layout build=1 iss=1 ico=1 identity=preserved' -ForegroundColor Green
