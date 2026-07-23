param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherOrServiceJwt
)
. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
try {
    $headers = @{ apikey=$PublishableKey; Authorization="Bearer $TeacherOrServiceJwt"; 'Content-Type'='application/json' }
    $result = Invoke-RestMethod -Method Post -Uri "$($SupabaseUrl.TrimEnd('/'))/rest/v1/rpc/get_examtransfer_cloud_capabilities" -Headers $headers -Body '{}'
    $required = @('join_public_session','init_public_submission','finalize_public_submission','upsert_public_device_heartbeat','ack_public_device_command','start_public_quiz_attempt','save_public_quiz_answers','finalize_public_quiz_attempt','verify_public_submission_archive','get_public_exam_file_download')
    if ([int]$result.schemaVersion -ne 14) { throw "Expected schema 14; received $($result.schemaVersion)." }
    foreach ($rpc in $required) { if ($result.criticalRpcs -notcontains $rpc) { throw "Missing RPC $rpc." } }
    foreach ($bucket in @('exam-archives','public-submission-archives')) { if ($result.buckets -notcontains $bucket) { throw "Missing bucket $bucket." } }
    Write-AcceptanceResult -Passed $true -Code 'CLOUD_SCHEMA_VERSION_OK' -TraceId $traceId -Detail 'live capability RPC reports schema 14, critical RPCs and buckets'
} catch { Write-AcceptanceResult -Passed $false -Code 'CLOUD_SCHEMA_VERSION_FAILED' -TraceId $traceId -Detail $_.Exception.Message }
