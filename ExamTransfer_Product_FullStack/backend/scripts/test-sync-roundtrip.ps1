param(
    [Parameter(Mandatory)][string]$LocalServerUrl,
    [Parameter(Mandatory)][string]$TeacherToken,
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherJwt,
    [Parameter(Mandatory)][Guid]$LocalSessionId,
    [Parameter(Mandatory)][ValidateSet('session_participants','submissions','submission_files','violations','quiz_attempts','quiz_answers','public_device_connections','public_device_commands','public_device_command_results','class_enrollment_requests','class_members')][string]$CloudEntityName,
    [Parameter(Mandatory)][string]$CloudEntityId
)
. "$PSScriptRoot/acceptance-common.ps1"
$traceId=New-AcceptanceTraceId
try {
    $local=$LocalServerUrl.TrimEnd('/');$cloud=$SupabaseUrl.TrimEnd('/');$localHeaders=@{Authorization="Bearer $TeacherToken";'Content-Type'='application/json'};$cloudHeaders=@{apikey=$PublishableKey;Authorization="Bearer $TeacherJwt"}
    Invoke-RestMethod -Method Post -Uri "$local/api/v1/cloud/sync" -Headers $localHeaders -Body '{}' | Out-Null
    $pushed=$false
    1..20 | ForEach-Object {
        if (-not $pushed) {
            Start-Sleep -Milliseconds 500
            $rows=@(Invoke-RestMethod -Method Get -Uri "$cloud/rest/v1/exam_sessions?select=id,access_mode,cloud_version&id=eq.$LocalSessionId" -Headers $cloudHeaders)
            if ($rows.Count -eq 1 -and $rows[0].access_mode -eq 'PublicCloud' -and [long]$rows[0].cloud_version -gt 0) {$pushed=$true}
        }
    }
    if (-not $pushed) { throw 'Local-owned PublicCloud session was not pushed with a cloud_version.' }
    Invoke-RestMethod -Method Post -Uri "$local/api/public-cloud/pull" -Headers $localHeaders -Body '{}' | Out-Null
    $snapshot=Invoke-RestMethod -Method Get -Uri "$local/api/public-cloud/snapshot/$CloudEntityName?afterCloudVersion=0&limit=500" -Headers $localHeaders
    $match=@($snapshot.rows|Where-Object {$_.cloudEntityId -eq $CloudEntityId})
    if ($match.Count -ne 1 -or [long]$match[0].cloudVersion -le 0) { throw 'Cloud-owned row was not pulled into SQLite replica.' }
    Write-AcceptanceResult -Passed $true -Code 'SYNC_ROUNDTRIP_OK' -TraceId $traceId -Detail "local session push and cloud-owned pull verified"
} catch { Write-AcceptanceResult -Passed $false -Code 'SYNC_ROUNDTRIP_FAILED' -TraceId $traceId -Detail $_.Exception.Message }
