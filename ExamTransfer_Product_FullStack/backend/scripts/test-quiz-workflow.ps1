param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId
)
. "$PSScriptRoot/acceptance-common.ps1"
$traceId=New-AcceptanceTraceId
try {
    $base=$SupabaseUrl.TrimEnd('/');$headers=@{apikey=$PublishableKey;Authorization="Bearer $StudentJwt";'Content-Type'='application/json'}
    function Rpc([string]$name,[hashtable]$body){Invoke-RestMethod -Method Post -Uri "$base/rest/v1/rpc/$name" -Headers $headers -Body ($body|ConvertTo-Json -Depth 20 -Compress)}
    $attemptId=Rpc 'start_public_quiz_attempt' @{p_session_id=$SessionId;p_idempotency_key="quiz-start-$traceId"}
    $attempts=@(Invoke-RestMethod -Method Get -Uri "$base/rest/v1/quiz_attempts?select=snapshot_json&id=eq.$attemptId" -Headers $headers)
    if ($attempts.Count -ne 1 -or @($attempts[0].snapshot_json).Count -eq 0) { throw 'Quiz snapshot is missing.' }
    $revision=1
    foreach($question in @($attempts[0].snapshot_json)) {
        $choice=@($question.choices)[0].id
        $accepted=Rpc 'save_public_quiz_answers' @{p_attempt_id=$attemptId;p_question_id=$question.id;p_choice_ids=@($choice);p_revision=$revision;p_client_updated_at=[DateTimeOffset]::UtcNow.ToString('O')}
        if ([long]$accepted -lt $revision) { throw 'Quiz revision was not accepted.' }
        $revision++
    }
    $finalKey="quiz-final-$traceId";$score1=Rpc 'finalize_public_quiz_attempt' @{p_attempt_id=$attemptId;p_idempotency_key=$finalKey};$score2=Rpc 'finalize_public_quiz_attempt' @{p_attempt_id=$attemptId;p_idempotency_key=$finalKey}
    if ([decimal]$score1 -ne [decimal]$score2) { throw 'Quiz finalize is not idempotent.' }
    Write-AcceptanceResult -Passed $true -Code 'QUIZ_WORKFLOW_OK' -TraceId $traceId -Detail "live quiz RPC workflow passed attempt=$attemptId score=$score1"
} catch { Write-AcceptanceResult -Passed $false -Code 'QUIZ_WORKFLOW_FAILED' -TraceId $traceId -Detail $_.Exception.Message }
