[CmdletBinding()]
param(
    [switch]$ConfigureDockerDesktopNat,
    [string]$EnvironmentFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator, then rerun this script.'
}

$rules = @(
    @{ Name = 'ExamTransfer Docker Backend TCP 5048'; Protocol = 'TCP'; Port = 5048 },
    @{ Name = 'ExamTransfer Docker Discovery UDP 40550'; Protocol = 'UDP'; Port = 40550 }
)

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        New-NetFirewallRule `
            -DisplayName $rule.Name `
            -Direction Inbound `
            -Action Allow `
            -Protocol $rule.Protocol `
            -LocalPort $rule.Port `
            -Profile Private `
            -RemoteAddress LocalSubnet | Out-Null
        Write-Host "Created firewall rule: $($rule.Name)" -ForegroundColor Green
    } else {
        if (@($existing).Count -ne 1) {
            throw "More than one firewall rule has the same display name: $($rule.Name)"
        }
        $existing | Set-NetFirewallRule -Enabled True -Profile Private -Action Allow
        $existing | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
        $existing | Get-NetFirewallPortFilter |
            Set-NetFirewallPortFilter -Protocol $rule.Protocol -LocalPort $rule.Port
        Write-Host "Updated firewall rule: $($rule.Name)" -ForegroundColor Green
    }
}

if ($ConfigureDockerDesktopNat) {
    function Set-EnvironmentEntry {
        param([string[]]$Lines, [string]$Name, [string]$Value)
        $result = [Collections.Generic.List[string]]::new()
        $found = $false
        foreach ($line in $Lines) {
            if ($line -match "^\s*$([regex]::Escape($Name))=") {
                if (-not $found) {
                    $result.Add("$Name=$Value")
                    $found = $true
                }
            } else {
                $result.Add($line)
            }
        }
        if (-not $found) { $result.Add("$Name=$Value") }
        return $result.ToArray()
    }

    $projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) {
        $EnvironmentFile = Join-Path $projectRoot '.env.docker'
    }
    $EnvironmentFile = [IO.Path]::GetFullPath($EnvironmentFile)
    if (-not (Test-Path -LiteralPath $EnvironmentFile)) {
        throw "Docker environment file was not found: $EnvironmentFile"
    }

    Push-Location $projectRoot
    try {
        $containerId = [string](& docker compose ps --quiet backend 2>$null | Select-Object -First 1)
        $containerId = $containerId.Trim()
        if ([string]::IsNullOrWhiteSpace($containerId)) {
            throw 'Start the backend container once, then rerun this command so its exact Docker gateway can be detected.'
        }
        $gatewayCandidates = @(& docker inspect --format '{{range .NetworkSettings.Networks}}{{println .Gateway}}{{end}}' $containerId 2>$null) |
            ForEach-Object { "$_".Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
        if ($LASTEXITCODE -ne 0 -or $gatewayCandidates.Count -ne 1) {
            throw 'Could not resolve exactly one Docker gateway for the running backend container.'
        }
    } finally {
        Pop-Location
    }

    [Net.IPAddress]$gateway = $null
    if (-not [Net.IPAddress]::TryParse($gatewayCandidates[0], [ref]$gateway) -or
        $gateway.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw "Docker gateway is not a valid IPv4 address: $($gatewayCandidates[0])"
    }
    $octets = $gateway.GetAddressBytes()
    $private = $octets[0] -eq 10 -or
        ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or
        ($octets[0] -eq 192 -and $octets[1] -eq 168)
    if (-not $private -or [Net.IPAddress]::IsLoopback($gateway)) {
        throw "Refusing to trust a non-private Docker gateway: $gateway"
    }

    $lines = Get-Content -LiteralPath $EnvironmentFile
    $lines = @($lines | Where-Object { $_ -notmatch '^\s*LanAccess__TrustedDockerGatewayCidrs__\d+=' })
    $lines = Set-EnvironmentEntry $lines 'LanAccess__TrustDockerDesktopNat' 'true'
    $lines = Set-EnvironmentEntry $lines 'LanAccess__TrustedDockerGatewayCidrs__0' "$gateway/32"
    [IO.File]::WriteAllLines($EnvironmentFile, $lines, (New-Object Text.UTF8Encoding($false)))

    Write-Host "Configured an exact trusted Docker gateway: $gateway/32" -ForegroundColor Green
    Write-Host "Environment file: $EnvironmentFile (secret values were not printed)" -ForegroundColor Yellow
    Write-Host 'Restart the backend, then rerun the LAN discovery integration gate.' -ForegroundColor Yellow
}
