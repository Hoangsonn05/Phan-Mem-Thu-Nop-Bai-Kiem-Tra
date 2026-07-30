param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherJwt,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId,
    [string]$DeviceId="device-$([Guid]::NewGuid().ToString('N'))"
)
. "$PSScriptRoot/acceptance-common.ps1"
$traceId=New-AcceptanceTraceId
try {
    $base=$SupabaseUrl.TrimEnd('/');$studentHeaders=@{apikey=$PublishableKey;Authorization="Bearer $StudentJwt";'Content-Type'='application/json'};$teacherHeaders=@{apikey=$PublishableKey;Authorization="Bearer $TeacherJwt";'Content-Type'='application/json'}
    function StudentRpc([string]$name,[hashtable]$body){Invoke-RestMethod -Method Post -Uri "$base/rest/v1/rpc/$name" -Headers $studentHeaders -Body ($body|ConvertTo-Json -Depth 20 -Compress)}
    $null=StudentRpc 'join_public_session' @{p_session_id=$SessionId;p_device_id=$DeviceId;p_machine_name='staging';p_app_version='staging';p_capability_json=@{}}
    $null=StudentRpc 'upsert_public_device_heartbeat' @{p_session_id=$SessionId;p_device_id=$DeviceId;p_connection_state='Online';p_foreground_application='ExamTransfer';p_running_process_summary=@();p_app_version='staging';p_agent_version='staging'}
    $issued=Invoke-RestMethod -Method Post -Uri "$base/functions/v1/issue-public-device-command" -Headers $teacherHeaders -Body (@{sessionId=$SessionId;deviceId=$DeviceId;commandType='ShowWarning';payload=@{message='staging acceptance'};ttlSeconds=120}|ConvertTo-Json -Depth 10 -Compress)
    if (-not $issued.commandId) { throw 'Edge Function did not issue a command id.' }
    $commands=@(Invoke-RestMethod -Method Get -Uri "$base/rest/v1/public_device_commands?select=command_id,signature&command_id=eq.$($issued.commandId)" -Headers $studentHeaders)
    if ($commands.Count -ne 1 -or ([string]$commands[0].signature).Length -ne 64) { throw 'Signed command is not visible to its device.' }
    $received=StudentRpc 'ack_public_device_command' @{p_command_id=$issued.commandId;p_device_id=$DeviceId;p_status='Received';p_error_code=$null;p_error_message=$null}
    $executed=StudentRpc 'ack_public_device_command' @{p_command_id=$issued.commandId;p_device_id=$DeviceId;p_status='Executed';p_error_code=$null;p_error_message=$null}
    if ([string]$received -ne 'Received' -or [string]$executed -ne 'Executed') { throw 'Command result transition failed.' }
    Write-AcceptanceResult -Passed $true -Code 'DEVICE_COMMAND_OK' -TraceId $traceId -Detail "live signed Edge command and ACK workflow passed command=$($issued.commandId)"
} catch { Write-AcceptanceResult -Passed $false -Code 'DEVICE_COMMAND_FAILED' -TraceId $traceId -Detail $_.Exception.Message }
