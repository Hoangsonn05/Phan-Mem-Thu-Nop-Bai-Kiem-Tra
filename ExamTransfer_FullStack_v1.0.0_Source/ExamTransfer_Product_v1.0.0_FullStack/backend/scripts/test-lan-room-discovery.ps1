param([string]$BaseUri)
. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId

if (-not [string]::IsNullOrWhiteSpace($BaseUri)) {
    try {
        $response = Invoke-WebRequest -Method Get -Uri "$($BaseUri.TrimEnd('/'))/api/v1/discovery/open-sessions" -Headers @{ 'X-Trace-Id' = $traceId } -UseBasicParsing
        $payload = $response.Content | ConvertFrom-Json
        if ($null -eq $payload.data) { throw 'Response không có data.' }
        Write-AcceptanceResult -Passed $true -Code 'LAN_DISCOVERY_OK' -HttpStatus ([int]$response.StatusCode) -TraceId $traceId -Detail "rooms=$(@($payload.data).Count)"
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'N/A' }
        Write-AcceptanceResult -Passed $false -Code 'LAN_DISCOVERY_FAILED' -HttpStatus $status -TraceId $traceId -Detail $_.Exception.Message
    }
}

$project = Join-Path $PSScriptRoot '../tests/ExamTransfer.Infrastructure.Tests/ExamTransfer.Infrastructure.Tests.csproj'
Invoke-AcceptanceDotnetTest -Project $project -Filter 'FullyQualifiedName~LanAccessPolicy|FullyQualifiedName~DiscoveryProtocol|FullyQualifiedName~OpenSessionDiscoveryTests|FullyQualifiedName~StudentJoinValidationTests' -TraceId $traceId
Write-AcceptanceResult -Passed $true -Code 'STATIC_LAN_DISCOVERY_TESTS_OK' -TraceId $traceId -Detail 'local tests passed; only LAN_DISCOVERY_OK above represents a live HTTP check'
