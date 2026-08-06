[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$guard = Join-Path $PSScriptRoot 'installer-localserver-guard.ps1'
if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) {
    throw "Installer guard was not found: $guard"
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('ExamTransfer-DowngradeTest-' + [Guid]::NewGuid().ToString('N'))
$installedDirectory = Join-Path $fixtureRoot 'Installed\Server'
$installedExe = Join-Path $installedDirectory 'ExamTransfer.LocalServer.exe'
$installedManifest = Join-Path $fixtureRoot 'Installed\release-manifest.json'
$packageManifest = Join-Path $fixtureRoot 'package-manifest.json'

function Write-JsonFile([string]$Path, $Value) {
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}

function Invoke-DowngradeGuard([string]$InstalledManifestPath, [string]$PackageManifestPath) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $guard `
            -Mode CheckDowngrade `
            -InstalledServerPath $installedExe `
            -ManifestPath $PackageManifestPath 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output | Out-String).Trim()
    }
}

try {
    New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination $installedExe

    # Test: Clean Install (No installed manifest)
    Write-JsonFile $packageManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+12345'; builtAtUtc = '2026-08-01T10:00:00Z' }
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 0) { throw 'Downgrade Guard blocked clean install.' }

    # Setup baseline installed manifest
    Write-JsonFile $installedManifest @{ semanticVersion = '1.6.4'; buildId = '1.6.4+old'; builtAtUtc = '2026-08-01T08:00:00Z' }

    # Test: Upgrade 1.6.4 -> 1.6.5
    Write-JsonFile $packageManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+new'; builtAtUtc = '2026-08-01T10:00:00Z' }
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 0) { throw 'Downgrade Guard blocked upgrade.' }

    # Test: Reinstall same Build ID
    Write-JsonFile $packageManifest @{ semanticVersion = '1.6.4'; buildId = '1.6.4+old'; builtAtUtc = '2026-08-01T08:00:00Z' }
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 0) { throw 'Downgrade Guard blocked reinstall with same buildId.' }

    # Test: Downgrade 1.6.5 -> 1.6.4
    Write-JsonFile $installedManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+cur'; builtAtUtc = '2026-08-01T12:00:00Z' }
    Write-JsonFile $packageManifest @{ semanticVersion = '1.6.4'; buildId = '1.6.4+old'; builtAtUtc = '2026-08-01T08:00:00Z' }
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 45) { throw 'Downgrade Guard failed to block version downgrade.' }

    # Test: Same version, newer package timestamp
    Write-JsonFile $installedManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+old'; builtAtUtc = '2026-08-01T10:00:00Z' }
    Write-JsonFile $packageManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+new'; builtAtUtc = '2026-08-01T12:00:00Z' }
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 0) { throw 'Downgrade Guard blocked same-version newer timestamp.' }

    # Test: Same version, older package timestamp
    Write-JsonFile $installedManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+cur'; builtAtUtc = '2026-08-01T12:00:00Z' }
    Write-JsonFile $packageManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+old'; builtAtUtc = '2026-08-01T10:00:00Z' }
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 45) { throw 'Downgrade Guard failed to block same-version older timestamp.' }
    
    # Test: Invalid Installed Manifest
    Set-Content -LiteralPath $installedManifest -Value "invalid-json"
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 46) { throw 'Downgrade Guard failed to detect invalid installed manifest.' }

    # Test: Invalid Package Manifest
    Write-JsonFile $installedManifest @{ semanticVersion = '1.6.5'; buildId = '1.6.5+cur'; builtAtUtc = '2026-08-01T12:00:00Z' }
    Set-Content -LiteralPath $packageManifest -Value "invalid-json"
    $res = Invoke-DowngradeGuard $installedManifest $packageManifest
    if ($res.ExitCode -ne 46) { throw 'Downgrade Guard failed to detect invalid package manifest.' }

    Write-Host 'PASS - Downgrade guard correctly handles upgrades, identical build reinstall, downgrades, and invalid manifests.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
