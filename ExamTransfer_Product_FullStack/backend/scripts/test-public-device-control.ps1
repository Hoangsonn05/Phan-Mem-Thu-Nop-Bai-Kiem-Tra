. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
$project = Join-Path $PSScriptRoot '../tests/ExamTransfer.Infrastructure.Tests/ExamTransfer.Infrastructure.Tests.csproj'
Invoke-AcceptanceDotnetTest -Project $project -Filter 'FullyQualifiedName~PublicDeviceCommandProcessorTests' -TraceId $traceId
Write-AcceptanceResult -Passed $true -Code 'STATIC_PUBLIC_DEVICE_CONTROL_OK' -TraceId $traceId -Detail 'local signature and Agent journal tests only; this is not a Supabase E2E result'
