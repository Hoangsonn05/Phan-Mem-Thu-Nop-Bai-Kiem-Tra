param(
    [string]$BaseUrl = "http://localhost:5048",
    [Parameter(Mandatory = $true)][string]$TeacherToken,
    [Parameter(Mandatory = $true)][string]$StudentAccountToken,
    [Parameter(Mandatory = $true)][string]$ParticipantToken,
    [Guid]$DraftExamId,
    [Guid]$ActiveSessionId
)

$ErrorActionPreference = "Stop"
$apiRoot = $BaseUrl.TrimEnd('/')
$teacherHeaders = @{ Authorization = "Bearer $TeacherToken" }
$studentHeaders = @{ Authorization = "Bearer $StudentAccountToken"; "X-Exam-Session-Token" = $ParticipantToken }

if ($DraftExamId -ne [Guid]::Empty) {
    $samplePath = Join-Path $PSScriptRoot "..\samples\quiz-import.sample.json"
    $body = @{
        fileName = [IO.Path]::GetFileName($samplePath)
        base64Content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($samplePath))
    } | ConvertTo-Json
    $imported = Invoke-RestMethod -Method Post -Uri "$apiRoot/api/v1/exams/$DraftExamId/quiz/import" -Headers $teacherHeaders -ContentType "application/json" -Body $body
    if (-not $imported.success -or $imported.data.questionCount -ne 2) { throw "Quiz import acceptance failed." }
    Write-Host "PASS import: $($imported.data.questionCount) questions"
}

if ($ActiveSessionId -ne [Guid]::Empty) {
    $attemptResponse = Invoke-RestMethod -Method Post -Uri "$apiRoot/api/v1/student/quiz/sessions/$ActiveSessionId/attempt" -Headers $studentHeaders -ContentType "application/json" -Body "{}"
    if (-not $attemptResponse.success) { throw "Quiz attempt start failed." }
    $attempt = $attemptResponse.data
    $answers = @()
    foreach ($question in $attempt.questions) {
        $choiceIds = if ($question.multiple) { @($question.choices[0].id, $question.choices[2].id) } else { @($question.choices[1].id) }
        $answers += @{ questionId = $question.id; choiceIds = $choiceIds; revision = 1; clientUpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O") }
    }
    $syncBody = @{ answers = $answers } | ConvertTo-Json -Depth 8
    $synced = Invoke-RestMethod -Method Put -Uri "$apiRoot/api/v1/student/quiz/attempts/$($attempt.id)/answers" -Headers $studentHeaders -ContentType "application/json" -Body $syncBody
    if (-not $synced.success -or $synced.data.answers.Count -ne $attempt.questions.Count) { throw "Quiz answer sync failed." }
    $finalBody = @{ idempotencyKey = [Guid]::NewGuid().ToString("N"); clientFinalizedAtUtc = [DateTimeOffset]::UtcNow.ToString("O") } | ConvertTo-Json
    $finalized = Invoke-RestMethod -Method Post -Uri "$apiRoot/api/v1/student/quiz/attempts/$($attempt.id)/finalize" -Headers $studentHeaders -ContentType "application/json" -Body $finalBody
    if (-not $finalized.success -or $finalized.data.status -ne "Finalized") { throw "Quiz finalize failed." }
    Write-Host "PASS student flow: score $($finalized.data.score)/$($finalized.data.maxScore)"
}

if ($DraftExamId -eq [Guid]::Empty -and $ActiveSessionId -eq [Guid]::Empty) {
    throw "Provide DraftExamId, ActiveSessionId, or both."
}
