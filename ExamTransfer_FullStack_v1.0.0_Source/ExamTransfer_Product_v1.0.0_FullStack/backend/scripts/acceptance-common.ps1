Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-AcceptanceTraceId {
    return [Guid]::NewGuid().ToString('N')
}

function Write-AcceptanceResult {
    param(
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$TraceId,
        [string]$HttpStatus = 'N/A',
        [string]$Detail = ''
    )
    $state = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host "$state code=$Code httpStatus=$HttpStatus traceId=$TraceId detail=$Detail"
    if (-not $Passed) { exit 1 }
}

function Invoke-AcceptanceDotnetTest {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Filter,
        [Parameter(Mandatory)][string]$TraceId
    )
    & dotnet test $Project -c Debug --no-restore --filter $Filter
    if ($LASTEXITCODE -ne 0) {
        Write-AcceptanceResult -Passed $false -Code 'DOTNET_TEST_FAILED' -TraceId $TraceId -Detail "filter=$Filter"
    }
}
