param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('StopOnly', 'StopAndPreflight', 'StartAndVerify')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$InstalledServerPath,

    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serverPort = 5048
$discoveryPort = 40550
$protocol = 'ExamTransfer/2'
$expectedServerName = 'ExamTransfer.LocalServer.exe'

function Resolve-ExactPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Installed server path is required.'
    }
    return [IO.Path]::GetFullPath($Path)
}

function Get-ExactInstalledServerProcess([string]$ExactPath) {
    @(Get-CimInstance Win32_Process -Filter "Name='$expectedServerName'" -ErrorAction Stop |
        Where-Object {
            $_.ExecutablePath -and
            [string]::Equals(
                [IO.Path]::GetFullPath([string]$_.ExecutablePath),
                $ExactPath,
                [StringComparison]::OrdinalIgnoreCase)
        })
}

function Stop-ExactInstalledServer([string]$ExactPath) {
    $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    foreach ($process in @(Get-ExactInstalledServerProcess $ExactPath)) {
        $processId = [int]$process.ProcessId
        & $taskkill /PID $processId
        $requestExit = $LASTEXITCODE
        if ($requestExit -ne 0) {
            Write-Warning "Graceful stop request failed for exact installed Local Server PID $processId (exit $requestExit)."
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
                break
            }
            Start-Sleep -Milliseconds 200
        }

        $stillExact = @(Get-ExactInstalledServerProcess $ExactPath |
            Where-Object { [int]$_.ProcessId -eq $processId })
        if ($stillExact.Count -gt 0) {
            & $taskkill /F /PID $processId
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to stop exact installed Local Server PID $processId."
            }
        }
    }
}

function Get-ProcessPathSafe([int]$ProcessId) {
    try {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
        if ($process -and $process.ExecutablePath) {
            return [string]$process.ExecutablePath
        }
    }
    catch {
    }
    return '<unavailable>'
}

function Assert-PortsAvailable {
    $tcpOwners = @(Get-NetTCPConnection -LocalPort $serverPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($tcpOwners.Count -gt 0) {
        foreach ($ownerId in $tcpOwners) {
            [Console]::Error.WriteLine(
                "PORT_CONFLICT_TCP_5048 PID=$ownerId PATH=$(Get-ProcessPathSafe ([int]$ownerId))")
        }
        exit 41
    }

    $udpOwners = @(Get-NetUDPEndpoint -LocalPort $discoveryPort -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($udpOwners.Count -gt 0) {
        foreach ($ownerId in $udpOwners) {
            [Console]::Error.WriteLine(
                "PORT_CONFLICT_UDP_40550 PID=$ownerId PATH=$(Get-ProcessPathSafe ([int]$ownerId))")
        }
        exit 42
    }
}

function Read-Manifest([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release manifest not found: $Path"
    }
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $manifest.buildId -or
        $manifest.discoveryProtocol -ne $protocol -or
        [int]$manifest.discoveryUdpPort -ne $discoveryPort) {
        throw 'Release manifest identity is invalid.'
    }
    return $manifest
}

function Assert-InstalledHashes($Manifest, [string]$ManifestFile) {
    $installRoot = Split-Path -Parent $ManifestFile
    $clientPath = Join-Path $installRoot ([string]$Manifest.client.file -replace '/', '\')
    $serverPath = Join-Path $installRoot ([string]$Manifest.server.file -replace '/', '\')
    foreach ($entry in @(
        @{ Path = $clientPath; Hash = [string]$Manifest.client.sha256; Name = 'client' },
        @{ Path = $serverPath; Hash = [string]$Manifest.server.sha256; Name = 'server' }
    )) {
        if (-not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) {
            throw "Installed $($entry.Name) binary is missing: $($entry.Path)"
        }
        $actualHash = (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $entry.Hash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed $($entry.Name) hash does not match release-manifest.json."
        }
    }
}

function Start-AndVerify(
    [string]$ExactPath,
    [string]$ReleaseManifestPath) {
    if (-not (Test-Path -LiteralPath $ExactPath -PathType Leaf)) {
        throw "Installed Local Server is missing: $ExactPath"
    }
    $manifest = Read-Manifest $ReleaseManifestPath
    Assert-InstalledHashes $manifest $ReleaseManifestPath
    Assert-PortsAvailable

    $started = Start-Process `
        -FilePath $ExactPath `
        -WorkingDirectory (Split-Path -Parent $ExactPath) `
        -WindowStyle Hidden `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($started.HasExited) {
            throw "Installed Local Server exited before verification (exit $($started.ExitCode))."
        }
        try {
            $health = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$serverPort/health" `
                -Method Get `
                -TimeoutSec 2
            $identityResponse = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$serverPort/api/v1/discovery/identity" `
                -Method Get `
                -TimeoutSec 2
            $identity = $identityResponse.data
            if ($health.buildId -eq $manifest.buildId -and
                $health.protocol -eq $protocol -and
                [int]$health.discoveryPort -eq $discoveryPort -and
                $identity.buildId -eq $manifest.buildId -and
                $identity.protocol -eq $protocol -and
                [int]$identity.discoveryPort -eq $discoveryPort) {
                Write-Host "ExamTransfer Local Server verified. BuildId=$($manifest.buildId); Protocol=$protocol; UDP=$discoveryPort"
                return
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'Installed Local Server identity did not match release-manifest.json.'
}

$exactServerPath = Resolve-ExactPath $InstalledServerPath

try {
    switch ($Mode) {
        'StopOnly' {
            Stop-ExactInstalledServer $exactServerPath
        }
        'StopAndPreflight' {
            Stop-ExactInstalledServer $exactServerPath
            Assert-PortsAvailable
        }
        'StartAndVerify' {
            Start-AndVerify $exactServerPath ([IO.Path]::GetFullPath($ManifestPath))
        }
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 43
}

exit 0
