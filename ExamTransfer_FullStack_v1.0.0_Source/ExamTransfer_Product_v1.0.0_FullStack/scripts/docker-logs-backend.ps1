[CmdletBinding()]
param(
    [int]$Tail = 200,
    [switch]$Follow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker-common.ps1')

Assert-DockerEngine
$arguments = @('logs', '--tail', $Tail.ToString())
if ($Follow) { $arguments += '--follow' }
$arguments += 'backend'
Invoke-ComposeChecked -Arguments $arguments
