[CmdletBinding()]
param(
    [switch]$FixDockerPath,
    [switch]$StartDockerDesktop,
    [switch]$ConfigureFirewall,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$doctor = Join-Path $PSScriptRoot 'docker-doctor.ps1'
$environmentSetup = Join-Path $PSScriptRoot 'setup-docker-environment.ps1'
$buildScript = Join-Path $PSScriptRoot 'docker-build-backend.ps1'
$startScript = Join-Path $PSScriptRoot 'docker-start-backend.ps1'
$firewallScript = Join-Path $PSScriptRoot 'setup-docker-firewall.ps1'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$envPath = Join-Path $projectRoot '.env.docker'

$doctorArgs = @()
if ($FixDockerPath) { $doctorArgs += '-FixUserPath' }
if ($StartDockerDesktop) { $doctorArgs += '-StartDockerDesktop' }
& powershell -NoProfile -ExecutionPolicy Bypass -File $doctor @doctorArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path -LiteralPath $envPath)) {
    Write-Host "`n.env.docker does not exist. Starting interactive environment setup..." -ForegroundColor Yellow
    & powershell -NoProfile -ExecutionPolicy Bypass -File $environmentSetup
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($ConfigureFirewall) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $firewallScript
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $SkipBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& powershell -NoProfile -ExecutionPolicy Bypass -File $startScript
exit $LASTEXITCODE
