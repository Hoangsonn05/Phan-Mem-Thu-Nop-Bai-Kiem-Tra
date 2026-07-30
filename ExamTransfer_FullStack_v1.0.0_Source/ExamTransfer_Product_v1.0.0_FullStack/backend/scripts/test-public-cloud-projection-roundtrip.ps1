param(
    [Parameter(Mandatory)][string]$LocalServerUrl,
    [Parameter(Mandatory)][string]$TeacherToken,
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId,
    [Parameter(Mandatory)][string]$DeviceId,
    [string]$MachineName = 'acceptance-device',
    [string]$AppVersion = 'acceptance'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$traceId = [Guid]::NewGuid().ToString('N')
$localHeaders = @{ Authorization = "Bearer $TeacherToken" }
$studentHeaders = @{ apikey = $PublishableKey; Authorization = "Bearer $StudentJwt" }
$base = $LocalServerUrl.TrimEnd('/')
$cloud = $SupabaseUrl.TrimEnd('/')

try {
    $joinBody = @{
        p_session_id = $SessionId
        p_device_id = $DeviceId
        p_machine_name = $MachineName
        p_app_version = $AppVersion
        p_capability_json = @{}
    } | ConvertTo-Json -Compress
    $participantId = Invoke-RestMethod -Method Post -Uri "$cloud/rest/v1/rpc/join_public_session" `
        -Headers $studentHeaders -ContentType 'application/json' -Body $joinBody
    $participantId = [Guid]([string]$participantId).Trim('"')

    Invoke-RestMethod -Method Post -Uri "$base/api/public-cloud/pull" -Headers $localHeaders | Out-Null
    $localBefore = Invoke-RestMethod -Method Get -Uri "$base/api/v1/sessions/$SessionId" -Headers $localHeaders
    $projectedBefore = @($localBefore.data.participants) | Where-Object { $_.id -eq $participantId }
    if ($projectedBefore.Count -ne 1 -or $projectedBefore[0].status -ne 'PendingApproval') {
        throw 'Pending participant was not visible through the teacher session service.'
    }

    $mutationRequestId = [Guid]::NewGuid()
    $approveBody = @{
        mutationRequestId = $mutationRequestId
    } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post `
        -Uri "$base/api/v1/sessions/$SessionId/participants/$participantId/approve" `
        -Headers $localHeaders -ContentType 'application/json' -Body $approveBody | Out-Null
    $cloudRows = @(Invoke-RestMethod -Method Get `
        -Uri "$cloud/rest/v1/session_participants?id=eq.$participantId&select=status,cloud_version" `
        -Headers $studentHeaders)
    if ($cloudRows.Count -ne 1 -or $cloudRows[0].status -ne 'Approved' -or [long]$cloudRows[0].cloud_version -le 0) {
        throw 'Teacher approval was not authoritative in Supabase.'
    }

    Invoke-RestMethod -Method Post -Uri "$base/api/public-cloud/pull" -Headers $localHeaders | Out-Null
    $localAfter = Invoke-RestMethod -Method Get -Uri "$base/api/v1/sessions/$SessionId" -Headers $localHeaders
    $projectedAfter = @($localAfter.data.participants) | Where-Object { $_.id -eq $participantId }
    if ($projectedAfter.Count -ne 1 -or $projectedAfter[0].status -ne 'Approved') {
        throw 'Approved participant was not projected back into the teacher business view.'
    }

    $studentRows = @(Invoke-RestMethod -Method Get `
        -Uri "$cloud/rest/v1/session_participants?id=eq.$participantId&select=status" `
        -Headers $studentHeaders)
    if ($studentRows.Count -ne 1 -or $studentRows[0].status -ne 'Approved') {
        throw 'Student cannot read the authoritative Approved state.'
    }
}
catch {
    throw "Projection roundtrip failed without exposing credentials. traceId=$traceId error=$($_.Exception.Message)"
}

Write-Host "PASS code=PUBLIC_CLOUD_PROJECTION_ROUNDTRIP_OK traceId=$traceId detail=pending cloud row,pull,teacher RPC,cloud version,pull,teacher view,student view"
