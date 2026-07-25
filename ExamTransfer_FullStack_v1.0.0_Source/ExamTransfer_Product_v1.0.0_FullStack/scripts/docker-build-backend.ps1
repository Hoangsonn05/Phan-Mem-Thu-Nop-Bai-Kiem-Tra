[CmdletBinding()]
param(
    [switch]$NoCache,
    [switch]$Pull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')

Assert-DockerEngine
$arguments = @('build')
if ($NoCache) { $arguments += '--no-cache' }
if ($Pull) { $arguments += '--pull' }
$arguments += 'backend'

Write-Host 'Building ExamTransfer backend Docker image...' -ForegroundColor Cyan
Invoke-ComposeChecked -Arguments $arguments
Write-Host 'Backend Docker image built successfully.' -ForegroundColor Green
