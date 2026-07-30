param(
    [Parameter(Mandatory)][string]$LocalServerUrl,
    [Parameter(Mandatory)][string]$TeacherToken,
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherJwt,
    [Parameter(Mandatory)][Guid]$SessionId,
    [Parameter(Mandatory)][Guid]$ApproveParticipantId,
    [Parameter(Mandatory)][Guid]$RejectParticipantId,
    [Parameter(Mandatory)][Guid]$ExtraTimeParticipantId,
    [Parameter(Mandatory)][Guid]$ResubmitParticipantId,
    [Parameter(Mandatory)][Guid]$SubmissionId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$traceId = [Guid]::NewGuid().ToString('N')
$localHeaders = @{ Authorization = "Bearer $TeacherToken" }
$cloudHeaders = @{ apikey = $PublishableKey; Authorization = "Bearer $TeacherJwt" }
$base = $LocalServerUrl.TrimEnd('/')
$cloud = $SupabaseUrl.TrimEnd('/')

function Invoke-LocalMutation([string]$Path, [object]$Body) {
    Invoke-RestMethod -Method Post -Uri "$base/$Path" -Headers $localHeaders `
        -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress)
}
function Read-CloudRow([string]$Table, [Guid]$Id, [string]$Select) {
    $uri = "$cloud/rest/v1/$Table?id=eq.$Id&select=$Select"
    $rows = @(Invoke-RestMethod -Method Get -Uri $uri -Headers $cloudHeaders)
    if ($rows.Count -ne 1) { throw "Expected one $Table row. traceId=$traceId" }
    return $rows[0]
}

try {
    $before = Read-CloudRow 'session_participants' $ApproveParticipantId 'status,cloud_version'
    $approveMutationRequestId = [Guid]::NewGuid()
    Invoke-LocalMutation "api/v1/sessions/$SessionId/participants/$ApproveParticipantId/approve" @{
        mutationRequestId = $approveMutationRequestId
    } | Out-Null
    $after = Read-CloudRow 'session_participants' $ApproveParticipantId 'status,cloud_version'
    if ($after.status -ne 'Approved' -or [long]$after.cloud_version -le [long]$before.cloud_version) {
        throw 'Approve did not commit an authoritative newer cloud row.'
    }

    $rejectMutationRequestId = [Guid]::NewGuid()
    Invoke-LocalMutation "api/v1/sessions/$SessionId/participants/$RejectParticipantId/reject" @{
        reason = 'Acceptance rejection'
        mutationRequestId = $rejectMutationRequestId
    } | Out-Null
    if ((Read-CloudRow 'session_participants' $RejectParticipantId 'status').status -ne 'Rejected') {
        throw 'Reject did not reach Supabase.'
    }

    $extraBefore = Read-CloudRow 'session_participants' $ExtraTimeParticipantId 'extra_time_minutes,cloud_version'
    $extraTimeMutationRequestId = [Guid]::NewGuid()
    Invoke-LocalMutation "api/v1/sessions/$SessionId/participants/$ExtraTimeParticipantId/extra-time" @{
        minutes = 5
        reason = 'Acceptance accommodation'
        mutationRequestId = $extraTimeMutationRequestId
    } | Out-Null
    $extraAfter = Read-CloudRow 'session_participants' $ExtraTimeParticipantId 'extra_time_minutes,cloud_version'
    if ([int]$extraAfter.extra_time_minutes -ne ([int]$extraBefore.extra_time_minutes + 5) -or
        [long]$extraAfter.cloud_version -le [long]$extraBefore.cloud_version) {
        throw 'Extra time did not increase server state and cloud_version.'
    }

    $resubmitMutationRequestId = [Guid]::NewGuid()
    Invoke-LocalMutation "api/v1/participants/$ResubmitParticipantId/allow-resubmit" @{
        reason = 'Acceptance retry'
        mutationRequestId = $resubmitMutationRequestId
    } | Out-Null
    if (-not (Read-CloudRow 'session_participants' $ResubmitParticipantId 'resubmit_allowed').resubmit_allowed) {
        throw 'Resubmit permission did not reach Supabase.'
    }

    $submissionRejectMutationRequestId = [Guid]::NewGuid()
    Invoke-LocalMutation "api/v1/submissions/$SubmissionId/reject" @{
        reason = 'Acceptance rejection'
        mutationRequestId = $submissionRejectMutationRequestId
    } | Out-Null
    if ((Read-CloudRow 'submissions' $SubmissionId 'status').status -ne 'Rejected') {
        throw 'Submission rejection did not reach Supabase.'
    }
}
catch {
    throw "Teacher mutation acceptance failed without exposing credentials. traceId=$traceId error=$($_.Exception.Message)"
}

Write-Host "PASS code=PUBLIC_CLOUD_TEACHER_MUTATIONS_OK traceId=$traceId detail=approve,reject,extra-time,resubmit,submission-reject reached authoritative cloud rows"
