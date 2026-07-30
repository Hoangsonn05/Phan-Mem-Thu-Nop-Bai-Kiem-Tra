[CmdletBinding()]
param(
    [int]$HostTcpPort = 15048,
    [int]$HostUdpPort = 15050,
    [switch]$Cleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')
Assert-DockerEngine

$projectRoot = Get-ExamTransferProjectRoot
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$projectName = "examtransfer-persist-$suffix"
$dataVolume = "$projectName-data"
$runtimeVolume = "$projectName-runtime"
$containerName = "$projectName-backend"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) $projectName
$envPath = Join-Path $tempRoot '.env.docker'
New-Item -ItemType Directory -Path $tempRoot | Out-Null

$keyBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
$signingKey = [Convert]::ToBase64String($keyBytes)
$envLines = @(
    'ASPNETCORE_ENVIRONMENT=Development',
    'Server__Port=5048',
    'Server__UseHttps=false',
    'Server__PreferredIp=',
    'Discovery__Enabled=false',
    'Storage__RootPath=/data/ExamTransfer',
    'Storage__MinFreeBytes=1',
    "Security__TokenSigningKey=$signingKey",
    "Security__ReceiptSigningKey=$signingKey",
    'Cloud__Enabled=false'
)
[IO.File]::WriteAllLines($envPath, $envLines, (New-Object Text.UTF8Encoding($false)))

$previous = @{
    EXAMTRANSFER_ENV_FILE = $env:EXAMTRANSFER_ENV_FILE
    EXAMTRANSFER_DATA_VOLUME = $env:EXAMTRANSFER_DATA_VOLUME
    EXAMTRANSFER_RUNTIME_VOLUME = $env:EXAMTRANSFER_RUNTIME_VOLUME
    EXAMTRANSFER_CONTAINER_NAME = $env:EXAMTRANSFER_CONTAINER_NAME
    EXAMTRANSFER_TCP_PORT = $env:EXAMTRANSFER_TCP_PORT
    EXAMTRANSFER_UDP_PORT = $env:EXAMTRANSFER_UDP_PORT
}
$env:EXAMTRANSFER_ENV_FILE = $envPath
$env:EXAMTRANSFER_DATA_VOLUME = $dataVolume
$env:EXAMTRANSFER_RUNTIME_VOLUME = $runtimeVolume
$env:EXAMTRANSFER_CONTAINER_NAME = $containerName
$env:EXAMTRANSFER_TCP_PORT = [string]$HostTcpPort
$env:EXAMTRANSFER_UDP_PORT = [string]$HostUdpPort

try {
    Push-Location $projectRoot
    try {
        & docker compose -p $projectName up -d backend
        if ($LASTEXITCODE -ne 0) { throw 'Isolated persistence container failed to start.' }

        $healthUrl = "http://127.0.0.1:$HostTcpPort/health"
        $health = $null
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2; break } catch { Start-Sleep -Seconds 1 }
        }
        if ($null -eq $health) { throw 'Isolated persistence container did not become ready.' }

        & docker exec $containerName sh -c "mkdir -p /data/ExamTransfer/sessions/persistence-probe/receipts /data/ExamTransfer/config /data/ExamTransfer/database && printf receipt > /data/ExamTransfer/sessions/persistence-probe/receipts/probe.receipt && printf cursor > /data/ExamTransfer/config/public-cloud-cursor.probe"
        if ($LASTEXITCODE -ne 0) { throw 'Could not create isolated persistence probes.' }
        $databaseHashBefore = (& docker exec $containerName sha256sum /data/ExamTransfer/database/exam-transfer.db).Split(' ')[0]
        $keyHashBefore = (& docker exec $containerName sh -c "sha256sum /usr/share/ExamTransfer/keys/key-*.xml | sort | sha256sum").Split(' ')[0]

        & docker compose -p $projectName restart backend
        if ($LASTEXITCODE -ne 0) { throw 'Isolated persistence container restart failed.' }
        $health = $null
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2; break } catch { Start-Sleep -Seconds 1 }
        }
        if ($null -eq $health) { throw 'Container did not recover after restart.' }

        foreach ($path in @(
            '/data/ExamTransfer/database/exam-transfer.db',
            '/data/ExamTransfer/sessions/persistence-probe/receipts/probe.receipt',
            '/data/ExamTransfer/config/public-cloud-cursor.probe')) {
            & docker exec $containerName test -s $path
            if ($LASTEXITCODE -ne 0) { throw "Persistent artifact disappeared after restart: $path" }
        }
        $keyFiles = @(& docker exec $containerName find /usr/share/ExamTransfer/keys -type f)
        if ($LASTEXITCODE -ne 0 -or $keyFiles.Count -eq 0) {
            throw 'Data Protection key files disappeared after restart.'
        }
        $databaseHashAfter = (& docker exec $containerName sha256sum /data/ExamTransfer/database/exam-transfer.db).Split(' ')[0]
        $keyHashAfter = (& docker exec $containerName sh -c "sha256sum /usr/share/ExamTransfer/keys/key-*.xml | sort | sha256sum").Split(' ')[0]
        if ($keyHashBefore -ne $keyHashAfter) { throw 'Data Protection key ring changed across restart.' }

        Write-Host "PASS code=DOCKER_PERSISTENCE_OK project=$projectName volume=$dataVolume" -ForegroundColor Green
        Write-Host "Validated SQLite, Data Protection keys, receipt path and pull-cursor path across restart. sqliteHashChanged=$($databaseHashBefore -ne $databaseHashAfter)" -ForegroundColor Green
    } finally {
        Pop-Location
    }
} finally {
    if ($Cleanup) {
        if (-not $projectName.StartsWith('examtransfer-persist-', [StringComparison]::Ordinal)) {
            throw "Refusing cleanup for unexpected project name: $projectName"
        }
        Push-Location $projectRoot
        try { & docker compose -p $projectName down -v } finally { Pop-Location }
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    } else {
        Write-Host "Isolated test stack retained. Clean it with: docker compose -p $projectName down -v" -ForegroundColor Yellow
        Write-Host "Temporary environment file retained at: $envPath" -ForegroundColor Yellow
    }
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
