[CmdletBinding()]
param(
    [switch]$NoCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')

Assert-DockerEngine
if ($NoCache) {
    Invoke-ComposeChecked -Arguments @('build', '--no-cache', 'backend-tests')
} else {
    Invoke-ComposeChecked -Arguments @('build', 'backend-tests')
}
Invoke-ComposeChecked -Arguments @('run', '--rm', 'backend-tests')
Write-Host 'Backend Docker test run completed successfully.' -ForegroundColor Green
