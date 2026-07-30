param(
    [string]$ApiBaseUrl = "http://localhost:5048",
    [PSCredential]$Credential,
    [string]$StatePath = (Join-Path $PSScriptRoot "core-workflow-state.json"),
    [switch]$VerifyAfterRestart,
    [switch]$Cleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')

function Get-CredentialSafe {
    if ($Credential) { return $Credential }
    $account = $env:EXAMTRANSFER_ADMIN_ACCOUNT
    $password = $env:EXAMTRANSFER_ADMIN_PASSWORD
    if (-not [string]::IsNullOrWhiteSpace($account) -and -not [string]::IsNullOrWhiteSpace($password)) {
        return New-Object Management.Automation.PSCredential($account, (ConvertTo-SecureString $password -AsPlainText -Force))
    }
    return Get-Credential -Message "ExamTransfer teacher/admin account (password is never printed)"
}

function Invoke-CoreApi {
    param([string]$Method, [string]$Path, [object]$Body, [string]$Token, [string]$Step)
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $parameters = @{ Uri = "$ApiBaseUrl/$($Path.TrimStart('/'))"; Method = $Method; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 12 -Compress
    }
    try {
        $response = Invoke-WebRequest @parameters
        $payload = $response.Content | ConvertFrom-Json
        $apiCode = if ($payload.error) { $payload.error.code } else { "OK" }
        Write-Host "PASS [$Step] HTTP $([int]$response.StatusCode) API $apiCode traceId=$($payload.traceId)"
        if (-not $payload.success) { throw "API reported failure: $apiCode" }
        return $payload
    }
    catch {
        $status = "NETWORK"
        $code = "UNAVAILABLE"
        $trace = "UNAVAILABLE"
        $message = $_.Exception.Message
        $failureText = $null

        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
        }

        # Windows PowerShell 5.1 usually exposes an HTTP error body here.
        if ($_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            $failureText = $_.ErrorDetails.Message
        }
        elseif ($_.Exception.Response) {
            try {
                $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                $failureText = $reader.ReadToEnd()
            }
            catch { }
        }

        if (-not [string]::IsNullOrWhiteSpace($failureText)) {
            try {
                $failure = $failureText | ConvertFrom-Json
                if ($failure.error) {
                    if ($failure.error.code) { $code = [string]$failure.error.code }
                    if ($failure.error.message) { $message = [string]$failure.error.message }
                }
                elseif ($failure.title) {
                    $code = "MODEL_VALIDATION_FAILED"
                    $message = [string]$failure.title
                }
                if ($failure.traceId) { $trace = [string]$failure.traceId }
            }
            catch {
                $message = $failureText
            }
        }

        Write-Host "FAIL [$Step] HTTP $status API $code traceId=$trace"
        Write-Host "      $message"
        throw
    }
}

function Login-Core {
    param([PSCredential]$LoginCredential, [string]$DeviceId)
    $plainPassword = $LoginCredential.GetNetworkCredential().Password
    try {
        $login = Invoke-CoreApi POST "api/v1/auth/login" @{
            account     = $LoginCredential.UserName
            password    = $plainPassword
            deviceId    = $DeviceId
            machineName = $env:COMPUTERNAME
            appVersion  = "acceptance-script/1.0"
        } $null "Login"
        if (-not $login.data.accessToken) { throw "Login succeeded without an access token." }
        return $login.data.accessToken
    }
    finally {
        $plainPassword = $null
    }
}

$health = Invoke-WebRequest -Uri "$ApiBaseUrl/health" -UseBasicParsing -TimeoutSec 10
if ([int]$health.StatusCode -ne 200) { throw "Backend health check failed." }
Write-Host "PASS [Health] HTTP $([int]$health.StatusCode)"

$loginCredential = Get-CredentialSafe
$state = $null
if ($VerifyAfterRestart) {
    if (-not (Test-Path -LiteralPath $StatePath)) { throw "State file not found: $StatePath" }
    $state = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
    $token = Login-Core $loginCredential $state.deviceId
    $class = Invoke-CoreApi GET "api/v1/classes/$($state.classId)" $null $token "Verify class after restart"
    $exam = Invoke-CoreApi GET "api/v1/exams/$($state.examId)" $null $token "Verify exam after restart"
    $session = Invoke-CoreApi GET "api/v1/sessions/$($state.sessionId)" $null $token "Verify session after restart"
    if ($exam.data.status -ne "Published") { throw "Exam status after restart is $($exam.data.status), expected Published." }
    if ($session.data.summary.status -ne "Finished") { throw "Session status after restart is $($session.data.summary.status), expected Finished." }
    Write-Host "PASS [VerifyAfterRestart] IDs and final states persisted."
}
else {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $prefix = "ACCEPTANCE-$timestamp"
    $deviceId = "acceptance-$([Guid]::NewGuid().ToString('N'))"
    $token = Login-Core $loginCredential $deviceId

    $class = Invoke-CoreApi POST "api/v1/classes" @{ name = "$prefix Class"; code = $prefix; schoolYear = "2026-2027"; description = "Core workflow acceptance" } $token "Create class"
    $classId = $class.data.id
    [void](Invoke-CoreApi GET "api/v1/classes/$classId" $null $token "Reload class")

    $exam = Invoke-CoreApi POST "api/v1/exams" @{
        classId = $classId; title = "$prefix Exam"; subject = "Acceptance"; description = "Core workflow"; durationMinutes = 30
        fileRule = @{ allowedExtensions = @(".txt"); maxFileSizeBytes = 1048576; maxTotalSizeBytes = 2097152; maxFileCount = 2; autoZip = $false; requireAtLeastOneFile = $true }
    } $token "Create linked exam"
    $examId = $exam.data.id

    $testFile = Join-Path ([IO.Path]::GetTempPath()) "$prefix.txt"
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($testFile, "ExamTransfer acceptance $timestamp", $utf8NoBom)
    try {
        $hash = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($testFile)))).Replace('-', '').ToLowerInvariant()
        $fileInfo = Get-Item -LiteralPath $testFile
        $init = Invoke-CoreApi POST "api/v1/exams/$examId/files/init" @{ fileName = $fileInfo.Name; sizeBytes = $fileInfo.Length; sha256 = $hash; mimeType = "text/plain" } $token "Initialize exam upload"
        $headers = @{ Authorization = "Bearer $token" }
        $chunkResponse = Invoke-WebRequest -Uri "$ApiBaseUrl/api/v1/exams/$examId/files/$($init.data.fileId)/chunks/0" -Method Put -Headers $headers -InFile $testFile -ContentType "application/octet-stream" -UseBasicParsing
        $chunkPayload = $chunkResponse.Content | ConvertFrom-Json
        Write-Host "PASS [Upload chunk] HTTP $([int]$chunkResponse.StatusCode) API OK traceId=$($chunkPayload.traceId)"
        $finalized = Invoke-CoreApi POST "api/v1/exams/$examId/files/$($init.data.fileId)/finalize" @{ sha256 = $hash } $token "Finalize and verify SHA-256"
        if ($finalized.data.sha256 -ne $hash) { throw "Finalized SHA-256 mismatch." }
    }
    finally {
        Remove-Item -LiteralPath $testFile -Force -ErrorAction SilentlyContinue
    }

    $published = Invoke-CoreApi POST "api/v1/exams/$examId/publish" @{} $token "Publish exam"
    if ($published.data.status -ne "Published") { throw "Publish response did not return Published." }
    $examList = Invoke-CoreApi GET "api/v1/exams" $null $token "Reload published exams"
    if (-not ($examList.data.items | Where-Object { $_.id -eq $examId -and $_.status -eq "Published" })) { throw "Published exam not found in GET exams." }

    $createdSession = Invoke-CoreApi POST "api/v1/sessions" @{ examId = $examId; classId = $classId; plannedStartUtc = [DateTimeOffset]::UtcNow.AddMinutes(2).ToString("o", [Globalization.CultureInfo]::InvariantCulture); settingsJson = "{}"; autoApprove = $false; capacity = 40; customRoomCode = $null } $token "Create session"
    $sessionId = $createdSession.data.summary.id
    foreach ($transition in @(
            @{ action = "open"; label = "Open" }, @{ action = "distribute"; label = "Distribute" }, @{ action = "start"; label = "Start" },
            @{ action = "pause"; label = "Pause" }, @{ action = "resume"; label = "Resume" }, @{ action = "collect"; label = "Collect" })) {
        [void](Invoke-CoreApi POST "api/v1/sessions/$sessionId/$($transition.action)" @{} $token $transition.label)
    }
    [void](Invoke-CoreApi POST "api/v1/sessions/$sessionId/end" @{ force = $false; reason = "Acceptance completed" } $token "End")

    $state = [ordered]@{ prefix = $prefix; classId = $classId; examId = $examId; sessionId = $sessionId; deviceId = $deviceId; createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    $state | ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding UTF8
    Write-Host "PASS [State] $StatePath"
}

if ($Cleanup) {
    if (-not $state.prefix.StartsWith("ACCEPTANCE-")) { throw "Cleanup refused: state prefix is not an acceptance prefix." }
    $cleanupClass = Invoke-CoreApi GET "api/v1/classes/$($state.classId)" $null $token "Validate cleanup class"
    $cleanupExam = Invoke-CoreApi GET "api/v1/exams/$($state.examId)" $null $token "Validate cleanup exam"
    if (-not $cleanupClass.data.code.StartsWith($state.prefix) -or -not $cleanupExam.data.title.StartsWith($state.prefix)) {
        throw "Cleanup refused: current class/exam do not both match the acceptance prefix."
    }
    [void](Invoke-CoreApi POST "api/v1/exams/$($state.examId)/archive" @{} $token "Archive acceptance exam")
    [void](Invoke-CoreApi DELETE "api/v1/classes/$($state.classId)" $null $token "Archive acceptance class")
    Write-Host "PASS [Cleanup] Archived only acceptance class/exam; no account or physical data was deleted."
}

Write-Host "CORE WORKFLOW PASS"

