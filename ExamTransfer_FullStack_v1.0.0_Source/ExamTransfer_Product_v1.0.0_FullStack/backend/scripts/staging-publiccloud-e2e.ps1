param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId,
    [string]$DeviceId = "staging-$([Guid]::NewGuid().ToString('N'))"
)

. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
$baseUri = $SupabaseUrl.TrimEnd('/')
$headers = @{
    apikey = $PublishableKey
    Authorization = "Bearer $StudentJwt"
    'Content-Type' = 'application/json'
    'X-Client-Info' = 'examtransfer-staging-e2e'
}

function Invoke-Rpc([string]$Name, [hashtable]$Body) {
    $json = $Body | ConvertTo-Json -Depth 20 -Compress
    return Invoke-RestMethod -Method Post -Uri "$baseUri/rest/v1/rpc/$Name" -Headers $headers -Body $json
}

try {
    $participantId = Invoke-Rpc 'join_public_session' @{
        p_session_id = $SessionId
        p_device_id = $DeviceId
        p_machine_name = 'staging-e2e'
        p_app_version = 'staging'
        p_capability_json = @{}
    }
    $firstConnection = Invoke-Rpc 'upsert_public_device_heartbeat' @{
        p_session_id = $SessionId
        p_device_id = $DeviceId
        p_connection_state = 'Online'
        p_foreground_application = 'ExamTransfer'
        p_running_process_summary = @()
        p_app_version = 'staging'
        p_agent_version = 'staging'
    }
    $secondConnection = Invoke-Rpc 'upsert_public_device_heartbeat' @{
        p_session_id = $SessionId
        p_device_id = $DeviceId
        p_connection_state = 'Online'
        p_foreground_application = 'ExamTransfer'
        p_running_process_summary = @()
        p_app_version = 'staging'
        p_agent_version = 'staging'
    }
    if ([string]$firstConnection -ne [string]$secondConnection) { throw 'Heartbeat RPC is not idempotent.' }

    $directWriteDenied = $false
    try {
        $body = @{ status = 'Approved'; extra_time_minutes = 999; resubmit_allowed = $true } | ConvertTo-Json -Compress
        $directResponse = Invoke-WebRequest -Method Patch -Uri "$baseUri/rest/v1/session_participants?id=eq.$participantId" -Headers ($headers + @{ Prefer = 'return=representation' }) -Body $body -UseBasicParsing
        $directRows = @($directResponse.Content | ConvertFrom-Json)
        if ($directRows.Count -eq 0) { $directWriteDenied = $true }
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -in 401,403) { $directWriteDenied = $true }
    }
    if (-not $directWriteDenied) { throw 'Direct Student update was not rejected by RLS/privileges.' }

    Write-AcceptanceResult -Passed $true -Code 'STAGING_PUBLICCLOUD_E2E_OK' -TraceId $traceId -Detail 'real Auth JWT, RPC idempotency and direct-write denial verified; no secret values printed'
}
catch {
    Write-AcceptanceResult -Passed $false -Code 'STAGING_PUBLICCLOUD_E2E_FAILED' -TraceId $traceId -Detail $_.Exception.Message
}
