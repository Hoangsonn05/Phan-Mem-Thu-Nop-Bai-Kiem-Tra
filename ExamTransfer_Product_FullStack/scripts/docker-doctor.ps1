[CmdletBinding()]
param(
    [switch]$FixUserPath,
    [switch]$StartDockerDesktop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')

Write-Host '========================================' -ForegroundColor Cyan
Write-Host ' EXAMTRANSFER - DOCKER DOCTOR' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host 'Correct version command: docker --version' -ForegroundColor Yellow
Write-Host 'The spelling docker --vesion is incorrect.' -ForegroundColor Yellow

Refresh-CurrentProcessPath
$dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
$knownDockerPath = Get-KnownDockerCliPath

if ($null -eq $dockerCommand -and $null -ne $knownDockerPath) {
    $dockerBin = Split-Path -Parent $knownDockerPath
    $env:Path = "$dockerBin;$env:Path"
    Write-Host "Docker CLI found outside PATH: $knownDockerPath" -ForegroundColor Yellow

    if ($FixUserPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $entries = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($entries -notcontains $dockerBin) {
            $newPath = (@($entries) + $dockerBin) -join ';'
            [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
            Write-Host 'Docker CLI folder was added to the current user PATH.' -ForegroundColor Green
            Write-Host 'Close and reopen PowerShell after this script.' -ForegroundColor Yellow
        }
    }

    $dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
}

if ($null -eq $dockerCommand) {
    Write-Host 'FAILED: docker.exe was not found.' -ForegroundColor Red
    Write-Host 'Repair or reinstall Docker Desktop, then open a new PowerShell window.' -ForegroundColor Yellow
    exit 1
}

Write-Host "Docker CLI: $($dockerCommand.Source)" -ForegroundColor Green
& docker --version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nWSL status:" -ForegroundColor Cyan
if (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
    & wsl.exe --version
    & wsl.exe -l -v
} else {
    Write-Host 'WSL command was not found. Docker Desktop WSL 2 backend may not work.' -ForegroundColor Yellow
}

& docker info *> $null
if ($LASTEXITCODE -ne 0 -and $StartDockerDesktop) {
    $desktopCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\Docker Desktop.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Docker\Docker\Docker Desktop.exe'),
        (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    if ($desktopCandidates.Count -gt 0) {
        Write-Host "`nStarting Docker Desktop..." -ForegroundColor Yellow
        Start-Process -FilePath $desktopCandidates[0] -WindowStyle Hidden | Out-Null
        for ($attempt = 1; $attempt -le 60; $attempt++) {
            Start-Sleep -Seconds 2
            & docker info *> $null
            if ($LASTEXITCODE -eq 0) { break }
        }
    }
}

& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nFAILED: Docker CLI works, but Docker Engine is not ready." -ForegroundColor Red
    Write-Host 'Open Docker Desktop and wait for Engine running, then rerun this script.' -ForegroundColor Yellow
    Write-Host 'Also check: wsl --update, virtualization in BIOS/UEFI, and Docker Desktop > Settings > General > Use WSL 2 based engine.' -ForegroundColor Yellow
    exit 2
}

Write-Host "`nDocker Engine: READY" -ForegroundColor Green
& docker compose version
if ($LASTEXITCODE -ne 0) {
    Write-Host 'FAILED: Docker Compose v2 is unavailable.' -ForegroundColor Red
    exit 3
}

Write-Host "`nDocker is ready for ExamTransfer." -ForegroundColor Green
