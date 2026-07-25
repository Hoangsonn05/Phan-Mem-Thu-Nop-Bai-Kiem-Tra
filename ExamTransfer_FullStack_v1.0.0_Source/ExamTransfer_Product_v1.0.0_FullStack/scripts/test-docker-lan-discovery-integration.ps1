[CmdletBinding()]
param(
    [string]$ExpectedHostIp,
    [int]$HostTcpPort = 15048,
    [int]$HostUdpPort = 15050,
    [switch]$UseHostNetwork
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')
Assert-DockerEngine

function Get-EnvironmentValue([string]$Path, [string]$Name) {
    $line = Get-Content -LiteralPath $Path |
        Where-Object { $_ -match "^\s*$([regex]::Escape($Name))=" } |
        Select-Object -Last 1
    if ($null -eq $line) { return $null }
    return ($line -split '=', 2)[1].Trim()
}

function Assert-DockerNatFirewall {
    $required = @(
        @{ Name = 'ExamTransfer Docker Backend TCP 5048'; Protocol = 'TCP'; Port = '5048' },
        @{ Name = 'ExamTransfer Docker Discovery UDP 5050'; Protocol = 'UDP'; Port = '5050' }
    )
    foreach ($expected in $required) {
        $rule = Get-NetFirewallRule -DisplayName $expected.Name -ErrorAction Stop
        if (@($rule).Count -ne 1 -or -not $rule.Enabled -or
            $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or
            "$($rule.Profile)" -ne 'Private') {
            throw "Firewall rule is not narrowly enabled for Private inbound traffic: $($expected.Name)"
        }
        $address = $rule | Get-NetFirewallAddressFilter
        if (@($address.RemoteAddress) -notcontains 'LocalSubnet') {
            throw "Firewall rule is not limited to LocalSubnet: $($expected.Name)"
        }
        $port = $rule | Get-NetFirewallPortFilter
        if ("$($port.Protocol)" -ne $expected.Protocol -or "$($port.LocalPort)" -ne $expected.Port) {
            throw "Firewall protocol/port mismatch: $($expected.Name)"
        }
    }
}

$projectRoot = Get-ExamTransferProjectRoot
$localEnvironment = Join-Path $projectRoot '.env.docker'
if (-not (Test-Path -LiteralPath $localEnvironment)) { throw '.env.docker is missing.' }
if ([string]::IsNullOrWhiteSpace($ExpectedHostIp)) {
    $ExpectedHostIp = Get-EnvironmentValue $localEnvironment 'Server__PreferredIp'
}
$allowedCidr = Get-EnvironmentValue $localEnvironment 'LanAccess__AllowedCidrs__0'
$trustDockerNat = (Get-EnvironmentValue $localEnvironment 'LanAccess__TrustDockerDesktopNat') -eq 'true'
$trustedDockerGateways = @(Get-Content -LiteralPath $localEnvironment |
    Where-Object { $_ -match '^\s*LanAccess__TrustedDockerGatewayCidrs__\d+=' } |
    ForEach-Object { ($_ -split '=', 2)[1].Trim() } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$dockerNetworkName = $null
if ([string]::IsNullOrWhiteSpace($ExpectedHostIp) -or [string]::IsNullOrWhiteSpace($allowedCidr)) {
    throw 'Preferred host IP and AllowedCidrs__0 must be configured.'
}
if ($trustDockerNat) {
    if ($trustedDockerGateways.Count -eq 0) {
        throw 'Docker Desktop NAT trust is enabled without an exact trusted gateway CIDR.'
    }
    Assert-DockerNatFirewall

    Push-Location $projectRoot
    try {
        $backendContainerId = [string](& docker compose ps --quiet backend 2>$null | Select-Object -First 1)
        $backendContainerId = $backendContainerId.Trim()
        if ([string]::IsNullOrWhiteSpace($backendContainerId)) {
            throw 'The backend container must be running so the production Docker network can be validated.'
        }
        $networkCandidates = @(& docker inspect --format '{{range $name, $network := .NetworkSettings.Networks}}{{println $name}}{{end}}' $backendContainerId 2>$null) |
            ForEach-Object { "$_".Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
        if ($LASTEXITCODE -ne 0 -or $networkCandidates.Count -ne 1) {
            throw 'Could not resolve exactly one production Docker network for the backend container.'
        }
        $dockerNetworkName = $networkCandidates[0]
    } finally {
        Pop-Location
    }
}

$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$containerName = "examtransfer-discovery-$suffix"
$runtimeVolume = "$containerName-runtime"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) $containerName
$storageRoot = Join-Path $tempRoot 'storage'
$environmentPath = Join-Path $tempRoot '.env.docker'
New-Item -ItemType Directory -Path $storageRoot -Force | Out-Null

$keyBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
$testKey = [Convert]::ToBase64String($keyBytes)
[IO.File]::WriteAllLines($environmentPath, @(
    'ASPNETCORE_ENVIRONMENT=Testing',
    "Server__Port=$HostTcpPort",
    'Server__UseHttps=false',
    "Server__PreferredIp=$ExpectedHostIp",
    'Discovery__Enabled=true',
    'Discovery__Protocol=UdpBroadcast',
    "Discovery__Port=$HostUdpPort",
    'Discovery__RequestMagic=EXAMTRANSFER_DISCOVER_V1',
    "LanAccess__AllowedCidrs__0=$allowedCidr",
    'Storage__RootPath=/data/ExamTransfer',
    'Storage__MinFreeBytes=1',
    "Security__TokenSigningKey=$testKey",
    "Security__ReceiptSigningKey=$testKey",
    'Cloud__Enabled=false',
    "LanAccess__TrustDockerDesktopNat=$($trustDockerNat.ToString().ToLowerInvariant())"
), (New-Object Text.UTF8Encoding($false)))
if ($trustDockerNat) {
    $environmentLines = [Collections.Generic.List[string]]::new()
    $environmentLines.AddRange([string[]](Get-Content -LiteralPath $environmentPath))
    for ($index = 0; $index -lt $trustedDockerGateways.Count; $index++) {
        $environmentLines.Add("LanAccess__TrustedDockerGatewayCidrs__$index=$($trustedDockerGateways[$index])")
    }
    [IO.File]::WriteAllLines($environmentPath, $environmentLines, (New-Object Text.UTF8Encoding($false)))
}

$previous = @{
    DOTNET_ENVIRONMENT = $env:DOTNET_ENVIRONMENT
    EXAMTRANSFER_ALLOW_TEST_FIXTURE = $env:EXAMTRANSFER_ALLOW_TEST_FIXTURE
    EXAMTRANSFER_Storage__RootPath = $env:EXAMTRANSFER_Storage__RootPath
}
try {
    $env:DOTNET_ENVIRONMENT = 'Testing'
    $env:EXAMTRANSFER_ALLOW_TEST_FIXTURE = '1'
    $env:EXAMTRANSFER_Storage__RootPath = $storageRoot
    & dotnet run --project (Join-Path $projectRoot 'backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj') `
        --configuration Debug --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the empty isolated LAN discovery database.' }

    $mount = "$($storageRoot):/data/ExamTransfer"
    $dockerArguments = @('run', '--detach', '--name', $containerName, '--env-file', $environmentPath)
    if ($UseHostNetwork) {
        $dockerArguments += @('--network', 'host')
    } else {
        if ($trustDockerNat) {
            $dockerArguments += @('--network', $dockerNetworkName)
        }
        $dockerArguments += @(
            '--publish', "${HostTcpPort}:${HostTcpPort}/tcp",
            '--publish', "${HostUdpPort}:${HostUdpPort}/udp")
    }
    $dockerArguments += @(
        '--volume', $mount,
        '--volume', "${runtimeVolume}:/usr/share/ExamTransfer",
        'examtransfer-backend:local')
    & docker @dockerArguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Isolated LAN discovery container failed to start.' }

    $health = $null
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$HostTcpPort/health" -TimeoutSec 2
            break
        } catch { Start-Sleep -Seconds 1 }
    }
    if ($null -eq $health -or $health.status -eq 'Unhealthy') {
        throw 'Isolated LAN discovery container did not become ready.'
    }

    & powershell -NoLogo -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'test-docker-lan-discovery.ps1') `
        -ExpectedHostIp $ExpectedHostIp `
        -DiscoveryPort $HostUdpPort `
        -TimeoutSeconds 8 `
        -TestProtocolOnly
    if ($LASTEXITCODE -ne 0) { throw 'UDP protocol-only discovery probe failed.' }

    & docker stop $containerName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the isolated container before fixture seeding.' }
    & dotnet run --project (Join-Path $projectRoot 'backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj') `
        --configuration Debug --no-build -- --seed-lan-discovery-fixture
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated open-room fixture.' }
    & docker start $containerName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not restart the isolated discovery container.' }

    $health = $null
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$HostTcpPort/health" -TimeoutSec 2
            break
        } catch { Start-Sleep -Seconds 1 }
    }
    if ($null -eq $health -or $health.status -eq 'Unhealthy') {
        throw 'Isolated LAN discovery container did not recover after fixture seeding.'
    }

    & powershell -NoLogo -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'test-docker-lan-discovery.ps1') `
        -ExpectedHostIp $ExpectedHostIp `
        -DiscoveryPort $HostUdpPort `
        -TimeoutSeconds 8 `
        -RequireOpenSession
    if ($LASTEXITCODE -ne 0) { throw 'UDP open-room discovery probe failed.' }

    if ($trustDockerNat) {
        $openSessionsUrl = "http://${ExpectedHostIp}:$HostTcpPort/api/v1/discovery/open-sessions"
        $openSessions = Invoke-RestMethod -Uri $openSessionsUrl -TimeoutSec 8
        Write-Host 'PASS Open session discovery through advertised LAN endpoint' -ForegroundColor Green
    } else {
        $openSessionsJson = & docker exec $containerName curl --fail --silent --show-error `
            "http://127.0.0.1:$HostTcpPort/api/v1/discovery/open-sessions"
        if ($LASTEXITCODE -ne 0) { throw 'Open-session endpoint failed inside the isolated container.' }
        $openSessions = $openSessionsJson | ConvertFrom-Json
        Write-Host 'PASS Open session discovery through container loopback' -ForegroundColor Green
    }
    $rooms = @($openSessions.data)
    if ($rooms.Count -ne 1 -or $rooms[0].roomCode -cne 'DOCKERDISC' -or
        $rooms[0].accessMode -cne 'LanOnly') {
        throw "Open-room fixture mismatch. count=$($rooms.Count)"
    }
    Write-Host 'PASS Open session fixture room=DOCKERDISC accessMode=LanOnly' -ForegroundColor Green
    Write-Host 'INFO Outside-LAN policy is unit-tested; multi-device runtime policy was not tested on this machine.' -ForegroundColor Yellow
    Write-Host "PASS code=DOCKER_LAN_DISCOVERY_INTEGRATION_OK container=$containerName" -ForegroundColor Green
} finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
    if ($containerName.StartsWith('examtransfer-discovery-', [StringComparison]::Ordinal)) {
        & docker rm --force $containerName *> $null
        & docker volume rm $runtimeVolume *> $null
    }
    $resolvedTemp = Resolve-Path -LiteralPath $tempRoot -ErrorAction SilentlyContinue
    if ($resolvedTemp -and $resolvedTemp.Path.StartsWith(
        [IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemp.Path -Recurse -Force
    }
}
