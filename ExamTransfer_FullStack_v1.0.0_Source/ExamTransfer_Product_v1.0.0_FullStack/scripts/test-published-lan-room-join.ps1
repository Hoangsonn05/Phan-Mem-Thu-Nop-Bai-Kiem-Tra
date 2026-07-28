[CmdletBinding()]
param(
    [string]$ServerDirectory,
    [int]$TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ServerDirectory)) {
    $ServerDirectory = Join-Path $projectRoot 'artifacts\release\Server'
}
$serverDirectoryPath = (Resolve-Path -LiteralPath $ServerDirectory).Path
$serverExe = Join-Path $serverDirectoryPath 'ExamTransfer.LocalServer.exe'
$manifestPath = Join-Path (Split-Path -Parent $serverDirectoryPath) 'release-manifest.json'
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    throw "Published Local Server executable was not found: $serverExe"
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Published release manifest was not found: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.discoveryProtocol -ne 'ExamTransfer/2' -or
    [int]$manifest.discoveryUdpPort -ne 40550 -or
    [string]::IsNullOrWhiteSpace([string]$manifest.buildId)) {
    throw 'Published release manifest has invalid discovery/build identity.'
}
$publishedServerHash = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash
if (-not [string]::Equals(
        $publishedServerHash,
        [string]$manifest.server.sha256,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Published Local Server SHA-256 does not match release-manifest.json.'
}

$tcpOwner = Get-NetTCPConnection -LocalPort 5048 -State Listen -ErrorAction SilentlyContinue
$udpOwner = Get-NetUDPEndpoint -LocalPort 40550 -ErrorAction SilentlyContinue
if ($tcpOwner -or $udpOwner) {
    throw 'Published smoke requires TCP 5048 and UDP 40550 to be free before it starts its isolated Local Server.'
}

$virtualPattern = 'VMware|VMnet|Hyper-V|vEthernet|WSL|Docker|VPN|TAP|TUN|Loopback|Virtual'
$interfaceMetrics = @{}
Get-NetIPInterface -AddressFamily IPv4 -ConnectionState Connected -ErrorAction Stop |
    ForEach-Object { $interfaceMetrics[[int]$_.InterfaceIndex] = [int]$_.InterfaceMetric }
$candidate = Get-NetIPConfiguration -ErrorAction Stop |
    Where-Object {
        $_.NetAdapter.Status -eq 'Up' -and
        $_.IPv4Address -and
        $_.IPv4DefaultGateway -and
        ($_.InterfaceAlias + ' ' + $_.NetAdapter.InterfaceDescription) -notmatch $virtualPattern -and
        $_.IPv4Address.IPAddress -notlike '169.254.*'
    } |
    Sort-Object `
        @{ Expression = { if ($_.NetAdapter.NdisPhysicalMedium -match 'Wireless') { 0 } else { 1 } } }, `
        @{ Expression = { $interfaceMetrics[[int]$_.InterfaceIndex] } } |
    Select-Object -First 1
if ($null -eq $candidate) {
    throw 'No active physical Wi-Fi/Ethernet IPv4 interface with a gateway is available for published LAN smoke.'
}

$localAddress = [Net.IPAddress]::Parse([string]$candidate.IPv4Address.IPAddress)
$prefixLength = [int]$candidate.IPv4Address.PrefixLength
if ($prefixLength -lt 1 -or $prefixLength -gt 30) {
    throw "Physical interface prefix length cannot produce a directed broadcast: /$prefixLength"
}

function Get-DirectedBroadcast([Net.IPAddress]$Address, [int]$Prefix) {
    $bytes = $Address.GetAddressBytes()
    [uint64]$value = ([uint64]$bytes[0] -shl 24) -bor
        ([uint64]$bytes[1] -shl 16) -bor
        ([uint64]$bytes[2] -shl 8) -bor
        [uint64]$bytes[3]
    [uint64]$allIpv4Bits = 4294967295
    [uint64]$mask = (($allIpv4Bits -shl (32 - $Prefix)) -band $allIpv4Bits)
    [uint64]$broadcast = (($value -band $mask) -bor ((-bnot $mask) -band $allIpv4Bits))
    $result = [byte[]]@(
        [byte](($broadcast -shr 24) -band 255),
        [byte](($broadcast -shr 16) -band 255),
        [byte](($broadcast -shr 8) -band 255),
        [byte]($broadcast -band 255))
    return [Net.IPAddress]::new($result)
}

$broadcastAddress = Get-DirectedBroadcast $localAddress $prefixLength
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$smokeRoot = Join-Path $projectRoot "artifacts\lan-room-join-fix\$timestamp\published-smoke"
$storageRoot = Join-Path $smokeRoot 'storage'
$tokenFile = Join-Path $smokeRoot 'account-token.tmp'
$stdoutLog = Join-Path $smokeRoot 'server.stdout.log'
$stderrLog = Join-Path $smokeRoot 'server.stderr.log'
New-Item -ItemType Directory -Path $storageRoot -Force | Out-Null

$savedEnvironment = @{}
$environmentUpdates = @{
    'DOTNET_ENVIRONMENT' = 'Testing'
    'EXAMTRANSFER_ALLOW_TEST_FIXTURE' = '1'
    'Storage__RootPath' = $storageRoot
    'EXAMTRANSFER_Storage__RootPath' = $storageRoot
    'Server__Port' = '5048'
    'Server__PreferredIp' = $localAddress.ToString()
    'Discovery__Enabled' = 'true'
    'Discovery__Port' = '40550'
}
foreach ($name in $environmentUpdates.Keys) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environmentUpdates[$name], 'Process')
}

$serverProcess = $null
try {
    & dotnet run `
        --project (Join-Path $projectRoot 'backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj') `
        -c Release `
        --no-build `
        -- `
        --seed-lan-discovery-fixture `
        --account-token-file $tokenFile
    if ($LASTEXITCODE -ne 0) { throw 'Published smoke fixture seeding failed.' }
    if (-not (Test-Path -LiteralPath $tokenFile -PathType Leaf)) {
        throw 'Published smoke fixture did not create its protected-scope account token handoff.'
    }

    $serverProcess = Start-Process `
        -FilePath $serverExe `
        -WorkingDirectory $serverDirectoryPath `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog

    $health = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($serverProcess.HasExited) {
            throw "Published Local Server exited before health was ready. ExitCode=$($serverProcess.ExitCode)"
        }
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5048/health' -TimeoutSec 2
            if ($health.backendRuntime.code -eq 'BACKEND_RUNTIME_READY' -and
                $health.udpDiscovery.code -eq 'UDP_DISCOVERY_LISTENING' -and
                $health.buildId -eq $manifest.buildId -and
                $health.protocol -eq 'ExamTransfer/2' -and
                [int]$health.discoveryPort -eq 40550) {
                break
            }
        } catch {
            $health = $null
        }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $health -or $health.udpDiscovery.code -ne 'UDP_DISCOVERY_LISTENING') {
        throw 'Published Local Server did not report both HTTP and UDP readiness.'
    }

    $requestId = [Guid]::NewGuid().ToString('N')
    $requestJson = @{
        protocol = 'ExamTransfer/2'
        requestId = $requestId
        roomCode = 'DOCKERDISC'
    } | ConvertTo-Json -Compress
    $requestBytes = [Text.Encoding]::UTF8.GetBytes($requestJson)
    $udp = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $udp.EnableBroadcast = $true
        $udp.Client.Bind([Net.IPEndPoint]::new($localAddress, 0))
        $udp.Client.ReceiveTimeout = $TimeoutSeconds * 1000
        [void]$udp.Send($requestBytes, $requestBytes.Length, [Net.IPEndPoint]::new($broadcastAddress, 40550))
        [void]$udp.Send($requestBytes, $requestBytes.Length, [Net.IPEndPoint]::new($localAddress, 40550))
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $payload = $udp.Receive([ref]$remote)
    } finally {
        $udp.Dispose()
    }
    $discovery = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
    if ($discovery.protocol -ne 'ExamTransfer/2' -or
        $discovery.requestId -ne $requestId -or
        $discovery.buildId -ne $manifest.buildId -or
        [int]$discovery.discoveryPort -ne 40550) {
        throw 'Published UDP discovery response failed protocol/nonce validation.'
    }
    if ($discovery.address -ne $localAddress.ToString()) {
        throw "Published UDP discovery advertised '$($discovery.address)' instead of '$localAddress'."
    }
    $rooms = @($discovery.sessions | Where-Object {
        $_.roomCode -eq 'DOCKERDISC' -and
        ([string]$_.sessionState -eq 'Waiting' -or [string]$_.sessionState -eq '1') -and
        ([string]$_.accessMode -eq 'LanOnly' -or [string]$_.accessMode -eq '0')
    })
    if ($rooms.Count -ne 1) {
        throw 'Published UDP room-code resolver did not return the exact LanOnly Waiting fixture.'
    }

    $identityEnvelope = Invoke-RestMethod `
        -Uri "http://$localAddress`:5048/api/v1/discovery/identity" `
        -TimeoutSec 3
    if (-not $identityEnvelope.success -or
        $identityEnvelope.data.serverId -ne $discovery.serverId -or
        $identityEnvelope.data.product -ne 'ExamTransfer.LocalServer' -or
        $identityEnvelope.data.buildId -ne $manifest.buildId -or
        $identityEnvelope.data.protocol -ne 'ExamTransfer/2' -or
        [int]$identityEnvelope.data.discoveryPort -ne 40550) {
        throw 'Published HTTP identity does not match the UDP server identity.'
    }

    $accountToken = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
    $headers = @{ Authorization = "Bearer $accountToken" }
    $joinPayload = @{
        roomCode = 'DOCKERDISC'
        studentCode = 'SMOKE001'
        displayName = 'Published smoke student'
        className = $null
        deviceId = 'published-smoke-device'
        machineName = $env:COMPUTERNAME
        appVersion = [string]$manifest.semanticVersion
        nonce = [Guid]::NewGuid().ToString('N')
    }
    $joinRequest = $joinPayload | ConvertTo-Json -Compress
    $serializedJoin = $joinRequest | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$serializedJoin.nonce) -or
        ([string]$serializedJoin.nonce -notmatch '^[a-f0-9]{32}$') -or
        ($serializedJoin.PSObject.Properties.Name -contains 'requestId')) {
        throw 'Published HTTP join body contract requires nonce and forbids requestId.'
    }
    try {
        $join = Invoke-RestMethod `
            -Method Post `
            -Uri "http://$localAddress`:5048/api/v1/sessions/join" `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body $joinRequest `
            -TimeoutSec 5
    } catch {
        $validationBody = [string]$_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($validationBody)) {
            $validationBody = [string]$_.Exception.Message
        }
        $validationBody = $validationBody `
            -replace '(?i)("?(?:accessToken|refreshToken|password|authorization|apikey)"?\s*[:=]\s*")[^"]+', '$1<redacted>' `
            -replace '(?i)Bearer\s+[A-Za-z0-9._~-]+', 'Bearer <redacted>'
        if ($validationBody.Length -gt 1024) {
            $validationBody = $validationBody.Substring(0, 1024)
        }
        throw "Published HTTP join failed. Redacted validation response: $validationBody"
    }
    if (-not $join.success -or
        $join.data.status -ne 'PendingApproval' -or
        [string]::IsNullOrWhiteSpace([string]$join.data.participantId)) {
        throw 'Published HTTP join did not create a PendingApproval participant.'
    }

    Write-Host "PASS code=PUBLISHED_LAN_ROOM_JOIN_OK endpoint=http://$localAddress`:5048 room=DOCKERDISC participantStatus=$($join.data.status) buildId=$($manifest.buildId) udp=40550" -ForegroundColor Green
} finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        [void]$serverProcess.WaitForExit(5000)
    }
    if (Test-Path -LiteralPath $tokenFile -PathType Leaf) {
        Remove-Item -LiteralPath $tokenFile -Force
    }
    foreach ($name in $environmentUpdates.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
