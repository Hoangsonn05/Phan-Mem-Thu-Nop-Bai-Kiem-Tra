. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
$migration = Join-Path $PSScriptRoot '../supabase/migrations/20260722141147_public_classes_device_control.sql'
$required = @(
    'request_public_class_enrollment',
    'set_public_class_enrollment_code',
    'close_public_class_enrollment',
    'trg_sync_approved_enrollment_member',
    'classes_public_or_authorized_select',
    'sessions_staff_or_public_member_select',
    'join_public_session',
    'init_public_submission',
    'finalize_public_submission',
    'upsert_public_device_heartbeat',
    'report_public_violation',
    'ack_public_device_command',
    'start_public_quiz_attempt',
    'save_public_quiz_answers',
    'finalize_public_quiz_attempt',
    "set search_path = ''",
    'public-submission-archives',
    "extension = 'broadcast'",
    "extension = 'presence'"
)
foreach ($pattern in $required) {
    if (-not (Select-String -LiteralPath $migration -SimpleMatch $pattern -Quiet)) {
        Write-AcceptanceResult -Passed $false -Code 'PUBLIC_CLASS_CONTRACT_MISSING' -TraceId $traceId -Detail $pattern
    }
}
foreach ($forbidden in @(
    'create policy participants_public_owner_insert',
    'create policy submissions_public_owner_insert',
    'create policy submission_files_public_owner_insert',
    'create policy violations_public_owner_insert',
    'create policy device_connections_owner_insert',
    'create policy command_results_device_insert',
    'create policy device_commands_staff_insert'
)) {
    if (Select-String -LiteralPath $migration -SimpleMatch $forbidden -Quiet) {
        Write-AcceptanceResult -Passed $false -Code 'PUBLIC_CLASS_DIRECT_WRITE_POLICY_FOUND' -TraceId $traceId -Detail $forbidden
    }
}
Write-AcceptanceResult -Passed $true -Code 'STATIC_PUBLIC_CLASS_SOURCE_OK' -TraceId $traceId -Detail 'static contract only; run pgTAP or staging-publiccloud-e2e.ps1 against a real project'
