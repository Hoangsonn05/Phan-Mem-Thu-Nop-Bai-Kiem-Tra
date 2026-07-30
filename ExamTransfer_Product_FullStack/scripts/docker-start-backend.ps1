[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Recreate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')

Assert-DockerEngine
$root = Get-ExamTransferProjectRoot
$envPath = Join-Path $root '.env.docker'
if (-not (Test-Path -LiteralPath $envPath)) {
    throw 'Missing .env.docker. Run .\scripts\setup-docker-environment.ps1 first.'
}

$arguments = @('up', '-d')
if ($Build) { $arguments += '--build' }
if ($Recreate) { $arguments += '--force-recreate' }
$arguments += 'backend'

Invoke-ComposeChecked -Arguments $arguments

$healthUrl = 'http://localhost:5048/health'
Write-Host "Checking backend health: $healthUrl" -ForegroundColor Cyan
for ($attempt = 1; $attempt -le 45; $attempt++) {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 3
        if ($response.status -in @('Healthy', 'Degraded')) {
            Write-Host "ExamTransfer backend is responding with status $($response.status)." -ForegroundColor Green
            Write-Host 'API: http://localhost:5048' -ForegroundColor Green
            Write-Host 'Swagger: http://localhost:5048/swagger' -ForegroundColor Green
            exit 0
        }
    } catch {
        Start-Sleep -Seconds 2
    }
}

Write-Host 'Backend did not become healthy. Recent logs:' -ForegroundColor Red
Invoke-ComposeChecked -Arguments @('logs', '--tail', '150', 'backend')
exit 1
