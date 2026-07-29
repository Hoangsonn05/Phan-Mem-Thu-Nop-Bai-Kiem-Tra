param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Keep one authoritative release implementation. This root entry point exists
# for compatibility with existing release commands and must never duplicate the
# public-config generation logic in scripts\build-release.ps1.
$canonicalBuildScript = Join-Path $PSScriptRoot 'scripts\build-release.ps1'
if (-not (Test-Path -LiteralPath $canonicalBuildScript -PathType Leaf)) {
    throw "Canonical release build script was not found: $canonicalBuildScript"
}

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    $canonicalBuildScript,
    '-Version',
    $Version
)
if ($SkipTests) {
    $arguments += '-SkipTests'
}

& powershell @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Canonical release build failed with exit code $LASTEXITCODE."
}
