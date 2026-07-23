. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
$project = Join-Path $PSScriptRoot '../tests/ExamTransfer.Infrastructure.Tests/ExamTransfer.Infrastructure.Tests.csproj'
Invoke-AcceptanceDotnetTest -Project $project -Filter 'FullyQualifiedName~LanAndSubmissionPolicyTests|FullyQualifiedName~TeacherExamFile' -TraceId $traceId
Write-AcceptanceResult -Passed $true -Code 'STATIC_SUBMISSION_POLICY_OK' -TraceId $traceId -Detail 'local policy tests only; this is not a Supabase E2E result'
