Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ExamTransferProjectRoot {
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $compose = Join-Path $root 'compose.yaml'
    if (-not (Test-Path -LiteralPath $compose)) {
        throw "compose.yaml not found at: $compose"
    }
    return $root
}

function Refresh-CurrentProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = @($machinePath, $userPath) -join ';'
}

function Get-KnownDockerCliPath {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Docker\Docker\resources\bin\docker.exe'),
        (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    return $null
}

function Assert-DockerCli {
    Refresh-CurrentProcessPath
    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        $knownPath = Get-KnownDockerCliPath
        if ($null -ne $knownPath) {
            $env:Path = "$(Split-Path -Parent $knownPath);$env:Path"
            $command = Get-Command docker -ErrorAction SilentlyContinue
        }
    }
    if ($null -eq $command) {
        throw "Docker CLI was not found. Open a new PowerShell window, or run .\scripts\docker-doctor.ps1 -FixUserPath. Correct command: docker --version"
    }
}

function Assert-DockerEngine {
    Assert-DockerCli
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker CLI is installed, but Docker Engine is not ready. Open Docker Desktop and wait until it reports Engine running.'
    }
    & docker compose version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Compose v2 is unavailable. Update or repair Docker Desktop.'
    }
}

function Invoke-ComposeChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $root = Get-ExamTransferProjectRoot
    Push-Location $root
    try {
        & docker compose @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
