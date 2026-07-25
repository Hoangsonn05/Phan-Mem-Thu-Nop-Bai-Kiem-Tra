Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')

Assert-DockerEngine
Invoke-ComposeChecked -Arguments @('down')
Write-Host 'ExamTransfer Docker services stopped. Named volumes were preserved.' -ForegroundColor Green
