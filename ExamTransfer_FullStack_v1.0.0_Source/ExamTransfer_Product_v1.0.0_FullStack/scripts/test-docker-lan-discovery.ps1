[CmdletBinding()]
param(
    [string]$ExpectedHostIp,
    [int]$DiscoveryPort = 5050,
    [int]$TimeoutSeconds = 5,
    [switch]$TestProtocolOnly,
    [switch]$RequireOpenSession
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($TestProtocolOnly -and $RequireOpenSession) {
    throw 'Choose either -TestProtocolOnly or -RequireOpenSession, not both.'
}
$mustHaveOpenSession = $RequireOpenSession -or -not $TestProtocolOnly

function Get-EnvironmentValue([string]$Path, [string]$Name) {
    $line = Get-Content -LiteralPath $Path |
        Where-Object { $_ -match "^\s*$([regex]::Escape($Name))=" } |
        Select-Object -Last 1
    if ($null -eq $line) { return $null }
    return ($line -split '=', 2)[1].Trim()
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$environmentPath = Join-Path $projectRoot '.env.docker'
if ([string]::IsNullOrWhiteSpace($ExpectedHostIp)) {
    if (-not (Test-Path -LiteralPath $environmentPath)) { throw '.env.docker is missing.' }
    $ExpectedHostIp = Get-EnvironmentValue $environmentPath 'Server__PreferredIp'
}

[Net.IPAddress]$expected = $null
if (-not [Net.IPAddress]::TryParse($ExpectedHostIp, [ref]$expected) -or
    $expected.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
    [Net.IPAddress]::IsLoopback($expected)) {
    throw "ExpectedHostIp must be a non-loopback IPv4 address: $ExpectedHostIp"
}
$bytes = $expected.GetAddressBytes()
if ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 18) {
    throw "ExpectedHostIp looks like a Docker bridge address: $ExpectedHostIp"
}

$client = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
try {
    $client.EnableBroadcast = $true
    $client.Client.ReceiveTimeout = $TimeoutSeconds * 1000
    $request = [Text.Encoding]::UTF8.GetBytes('EXAMTRANSFER_DISCOVER_V1')
    [void]$client.Send($request, $request.Length, [Net.IPEndPoint]::new([Net.IPAddress]::Broadcast, $DiscoveryPort))

    $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
    $payload = $client.Receive([ref]$remote)
    $response = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
    Write-Host "PASS UDP discovery remote=$($remote.Address):$($remote.Port)" -ForegroundColor Green
    if ($response.address -ne $expected.ToString()) {
        $safeRawResponse = [Text.Encoding]::UTF8.GetString($payload)
        throw "Discovery advertised '$($response.address)' instead of Windows host '$expected'. Raw response: $safeRawResponse"
    }
    Write-Host "PASS Advertised IP address=$($response.address) port=$($response.port)" -ForegroundColor Green
    if ($mustHaveOpenSession -and [int]$response.activeRoomCount -le 0) {
        throw 'Discovery returned a response without an open LanOnly room.'
    }
    if (-not $mustHaveOpenSession -and [int]$response.activeRoomCount -ne 0) {
        throw "Protocol-only fixture unexpectedly contains $($response.activeRoomCount) open room(s)."
    }

    $healthUrl = "http://$($response.address):$($response.port)/health"
    $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec $TimeoutSeconds
    if ($health.advertisedAddress -ne $expected.ToString()) {
        throw "Health advertisedAddress mismatch at $healthUrl."
    }
    Write-Host "PASS TCP health url=$healthUrl status=$($health.status)" -ForegroundColor Green

    $duplicate = $false
    $client.Client.ReceiveTimeout = 750
    try {
        [void]$client.Receive([ref]$remote)
        $duplicate = $true
    } catch [Net.Sockets.SocketException] {
        if ($_.Exception.SocketErrorCode -ne [Net.Sockets.SocketError]::TimedOut) { throw }
    }
    if ($duplicate) { throw 'More than one discovery response was received for one request.' }
    Write-Host 'PASS UDP single-response invariant' -ForegroundColor Green
    $mode = if ($mustHaveOpenSession) { 'RequireOpenSession' } else { 'ProtocolOnly' }
    Write-Host "PASS code=DOCKER_LAN_DISCOVERY_OK mode=$mode advertised=$expected health=$($health.status)" -ForegroundColor Green
} finally {
    $client.Dispose()
}
