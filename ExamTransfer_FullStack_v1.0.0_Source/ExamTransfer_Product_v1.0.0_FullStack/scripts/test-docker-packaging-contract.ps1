[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$composePath = Join-Path $root 'compose.yaml'
$dockerfilePath = Join-Path $root 'backend\Dockerfile'
$environmentExamplePath = Join-Path $root '.env.docker.example'
$dockerCommonPath = Join-Path $root 'scripts\docker-common.ps1'
$dockerDoctorPath = Join-Path $root 'scripts\docker-doctor.ps1'
$dockerDiscoveryIntegrationPath =
    Join-Path $root 'scripts\test-docker-lan-discovery-integration.ps1'
$deploymentGuidePath = Join-Path $root 'docs\docker-deployment.md'

foreach ($path in @(
    $composePath,
    $dockerfilePath,
    $environmentExamplePath,
    $dockerCommonPath,
    $dockerDoctorPath,
    $dockerDiscoveryIntegrationPath,
    $deploymentGuidePath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Docker contract file is missing: $path"
    }
}

$compose = Get-Content -LiteralPath $composePath -Raw
$dockerfile = Get-Content -LiteralPath $dockerfilePath -Raw
$environmentExample = Get-Content -LiteralPath $environmentExamplePath -Raw
$dockerCommon = Get-Content -LiteralPath $dockerCommonPath -Raw
$dockerDoctor = Get-Content -LiteralPath $dockerDoctorPath -Raw
$dockerDiscoveryIntegration =
    Get-Content -LiteralPath $dockerDiscoveryIntegrationPath -Raw
$deploymentGuide = Get-Content -LiteralPath $deploymentGuidePath -Raw
$legacyDiscoveryPort = [string](5000 + 50)

$expectedPortMapping = '"${EXAMTRANSFER_UDP_PORT:-40550}:40550/udp"'
if (-not $compose.Contains($expectedPortMapping)) {
    throw "compose.yaml must publish host UDP 40550 to container UDP 40550: $expectedPortMapping"
}
if ($compose.Contains("EXAMTRANSFER_UDP_PORT:-$legacyDiscoveryPort") -or
    $compose.Contains(":$legacyDiscoveryPort/udp")) {
    throw "compose.yaml still contains the legacy UDP $legacyDiscoveryPort mapping."
}
if (-not $dockerfile.Contains('EXPOSE 40550/udp')) {
    throw 'backend/Dockerfile must expose UDP 40550.'
}
if (-not $environmentExample.Contains('Discovery__Port=40550')) {
    throw '.env.docker.example must configure discovery UDP 40550.'
}

$perUserDockerCli = 'Programs\DockerDesktop\resources\bin\docker.exe'
$perUserDockerDesktop = 'Programs\DockerDesktop\Docker Desktop.exe'
if (-not $dockerCommon.Contains($perUserDockerCli)) {
    throw 'docker-common.ps1 does not recognize the per-user DockerDesktop CLI path.'
}
if (-not $dockerDoctor.Contains($perUserDockerDesktop)) {
    throw 'docker-doctor.ps1 does not recognize the per-user DockerDesktop application path.'
}
if (-not $dockerDoctor.Contains('-WindowStyle Hidden')) {
    throw 'docker-doctor.ps1 must start Docker Desktop as a hidden background helper.'
}
if (-not $dockerDiscoveryIntegration.Contains(
        '$containerDiscoveryPort = 40550') -or
    -not $dockerDiscoveryIntegration.Contains(
        'Discovery__Port=$containerDiscoveryPort') -or
    -not $dockerDiscoveryIntegration.Contains(
        '${HostUdpPort}:${containerDiscoveryPort}/udp') -or
    $dockerDiscoveryIntegration.Contains(
        'Discovery__Port=$HostUdpPort')) {
    throw 'Docker LAN discovery integration must keep container discovery on fixed UDP 40550.'
}
if ($deploymentGuide.Contains('UDP `' + $legacyDiscoveryPort + '`') -or
    $deploymentGuide.Contains('0.0.0.0:' + $legacyDiscoveryPort) -or
    $deploymentGuide.Contains('UDP ' + $legacyDiscoveryPort)) {
    throw "docs/docker-deployment.md still presents legacy UDP $legacyDiscoveryPort as the active Docker port."
}

Write-Host 'PASS code=DOCKER_PACKAGING_CONTRACT udp=40550 per_user_desktop=supported' -ForegroundColor Green
