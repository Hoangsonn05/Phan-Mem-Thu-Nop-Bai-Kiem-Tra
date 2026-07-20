[CmdletBinding(DefaultParameterSetName='Run')]
param(
    [string]$BaseUrl = 'http://localhost:5048',
    [string]$Account,
    [string]$StatePath,

    [Parameter(ParameterSetName='Run')]
    [switch]$Run,

    [Parameter(ParameterSetName='Verify')]
    [switch]$VerifyAfterRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-SecureStringPlain {
    param([Security.SecureString]$Secure)
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Invoke-ExamApi {
    param(
        [Parameter(Mandatory=$true)][string]$Method,
        [Parameter(Mandatory=$true)][string]$Path,
        [object]$Body,
        [string]$Token
    )
    $uri = $BaseUrl.TrimEnd('/') + '/' + $Path.TrimStart('/')
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($Token)) { $headers.Authorization = "Bearer $Token" }
    try {
        $params = @{
            Uri = $uri
            Method = $Method
            Headers = $headers
            ContentType = 'application/json'
        }
        if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 12 -Compress) }
        $response = Invoke-RestMethod @params
        if (-not $response.success) {
            throw "API_FAIL code=$($response.error.code) message=$($response.error.message) traceId=$($response.traceId)"
        }
        return $response
    }
    catch {
        if ($_.ErrorDetails.Message) {
            try {
                $errorPayload = $_.ErrorDetails.Message | ConvertFrom-Json
                throw "HTTP/API failure: code=$($errorPayload.error.code) message=$($errorPayload.error.message) traceId=$($errorPayload.traceId)"
            } catch {
                if ($_.Exception.Message -like 'HTTP/API failure:*') { throw }
            }
        }
        throw
    }
}


function Invoke-ExamChunk {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][byte[]]$Bytes,
        [Parameter(Mandatory=$true)][string]$Token
    )
    $uri = $BaseUrl.TrimEnd('/') + '/' + $Path.TrimStart('/')
    try {
        $response = Invoke-RestMethod -Uri $uri -Method Put -Headers @{ Authorization = "Bearer $Token" } -ContentType 'application/octet-stream' -Body $Bytes
        if (-not $response.success) {
            throw "API_FAIL code=$($response.error.code) message=$($response.error.message) traceId=$($response.traceId)"
        }
        return $response
    }
    catch {
        if ($_.ErrorDetails.Message) {
            try {
                $errorPayload = $_.ErrorDetails.Message | ConvertFrom-Json
                throw "HTTP/API failure: code=$($errorPayload.error.code) message=$($errorPayload.error.message) traceId=$($errorPayload.traceId)"
            } catch {
                if ($_.Exception.Message -like 'HTTP/API failure:*') { throw }
            }
        }
        throw
    }
}

function Pass([string]$Step,[string]$Detail) { Write-Host "[PASS] $Step - $Detail" -ForegroundColor Green }

if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $StatePath = Join-Path (Get-Location).Path 'backend\artifacts\core-workflow-state.json'
}

$health = Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + '/health') -Method Get
Pass 'Health' ($health | ConvertTo-Json -Compress)

if ([string]::IsNullOrWhiteSpace($Account)) { $Account = Read-Host 'Admin/Teacher account (email)' }
$passwordSecure = Read-Host 'Password' -AsSecureString
$password = ConvertFrom-SecureStringPlain $passwordSecure
$token = $null
$deviceId = 'ACCEPTANCE-' + [Guid]::NewGuid().ToString('N')
try {
    $login = Invoke-ExamApi -Method POST -Path 'api/v1/auth/login' -Body @{
        account = $Account
        password = $password
        deviceId = $deviceId
        machineName = $env:COMPUTERNAME
        appVersion = 'acceptance-script-1.0'
    }
    $token = $login.data.accessToken
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Login succeeded but no access token was returned.' }
    Pass 'Login' "role=$($login.data.role) user=$($login.data.displayName)"

    if ($VerifyAfterRestart) {
        if (-not (Test-Path -LiteralPath $StatePath)) { throw "State file not found: $StatePath" }
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        $class = Invoke-ExamApi -Method GET -Path "api/v1/classes/$($state.classId)" -Token $token
        Pass 'Class persists after restart' "id=$($class.data.id) code=$($class.data.code)"
        $exam = Invoke-ExamApi -Method GET -Path "api/v1/exams/$($state.examId)" -Token $token
        if ($exam.data.status -ne 'Published') { throw "Exam status after restart is $($exam.data.status), expected Published." }
        Pass 'Published exam persists after restart' "id=$($exam.data.id) status=$($exam.data.status)"
        $session = Invoke-ExamApi -Method GET -Path "api/v1/sessions/$($state.sessionId)" -Token $token
        if ($session.data.summary.status -ne 'Finished') { throw "Session status after restart is $($session.data.summary.status), expected Finished." }
        Pass 'Finished session persists after restart' "id=$($session.data.summary.id) status=$($session.data.summary.status)"
        return
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $prefix = "ACCEPTANCE-$stamp"

    $classResponse = Invoke-ExamApi -Method POST -Path 'api/v1/classes' -Token $token -Body @{
        name = "$prefix Class"
        code = $prefix
        schoolYear = '2026-2027'
        description = 'Automated acceptance workflow; safe test data.'
    }
    $classId = $classResponse.data.id
    Pass 'Create class' "id=$classId"

    $classGet = Invoke-ExamApi -Method GET -Path "api/v1/classes/$classId" -Token $token
    Pass 'Read class back' "code=$($classGet.data.code) rowVersion=$($classGet.data.rowVersion)"

    $examResponse = Invoke-ExamApi -Method POST -Path 'api/v1/exams' -Token $token -Body @{
        classId = $classId
        title = "$prefix Exam"
        subject = 'Acceptance Testing'
        description = 'Acceptance workflow with a real uploaded exam file.'
        durationMinutes = 30
        fileRule = @{
            allowedExtensions = @('.txt','.pdf','.docx','.zip')
            maxFileSizeBytes = 104857600
            maxTotalSizeBytes = 524288000
            maxFileCount = 20
            autoZip = $false
            requireAtLeastOneFile = $true
        }
    }
    $examId = $examResponse.data.id
    Pass 'Create exam linked to class' "id=$examId classId=$($examResponse.data.classId)"

    $testBytes = [Text.Encoding]::UTF8.GetBytes("ExamTransfer acceptance file $prefix`r`n")
    $sha256 = [BitConverter]::ToString(([Security.Cryptography.SHA256]::Create().ComputeHash($testBytes))).Replace('-','').ToLowerInvariant()
    $initFile = Invoke-ExamApi -Method POST -Path "api/v1/exams/$examId/files/init" -Token $token -Body @{
        fileName = "$prefix.txt"
        sizeBytes = $testBytes.Length
        sha256 = $sha256
        mimeType = 'text/plain'
        chunkSizeBytes = $null
    }
    $fileId = $initFile.data.fileId
    $chunkSize = [int]$initFile.data.chunkSizeBytes
    $totalChunks = [int]$initFile.data.totalChunks
    for ($index = 0; $index -lt $totalChunks; $index++) {
        $offset = $index * $chunkSize
        $count = [Math]::Min($chunkSize, $testBytes.Length - $offset)
        $chunkBytes = New-Object byte[] $count
        [Array]::Copy($testBytes, $offset, $chunkBytes, 0, $count)
        [void](Invoke-ExamChunk -Path "api/v1/exams/$examId/files/$fileId/chunks/$index" -Bytes $chunkBytes -Token $token)
    }
    $finalizeFile = Invoke-ExamApi -Method POST -Path "api/v1/exams/$examId/files/$fileId/finalize" -Token $token -Body @{ sha256 = $sha256 }
    if ($finalizeFile.data.sha256 -ne $sha256) { throw 'Finalized exam file SHA-256 mismatch.' }
    Pass 'Upload/finalize exam file' "fileId=$fileId sha256=$sha256"

    $publish = Invoke-ExamApi -Method POST -Path "api/v1/exams/$examId/publish" -Token $token -Body @{}
    if ($publish.data.status -ne 'Published') { throw "Publish returned status $($publish.data.status)." }
    Pass 'Publish exam' "status=$($publish.data.status)"

    $publishedList = Invoke-ExamApi -Method GET -Path 'api/v1/exams?status=Published&page=1&pageSize=200' -Token $token
    $listed = @($publishedList.data.items | Where-Object { $_.id -eq $examId })
    if ($listed.Count -ne 1) { throw 'Published exam is not present exactly once in the published exam list.' }
    Pass 'Published exam selectable for room' "found=$($listed.Count)"

    $sessionResponse = Invoke-ExamApi -Method POST -Path 'api/v1/sessions' -Token $token -Body @{
        examId = $examId
        classId = $classId
        plannedStartUtc = [DateTimeOffset]::UtcNow.AddMinutes(5).ToString('o')
        settingsJson = '{"autoApprove":false}'
        autoApprove = $false
        capacity = 36
        customRoomCode = $null
    }
    $sessionId = $sessionResponse.data.summary.id
    Pass 'Create room' "id=$sessionId roomCode=$($sessionResponse.data.summary.roomCode)"

    $steps = @(
        @{ Action='open'; Expected='Waiting' },
        @{ Action='distribute'; Expected='Distributing' },
        @{ Action='start'; Expected='InProgress' },
        @{ Action='pause'; Expected='Paused' },
        @{ Action='resume'; Expected='InProgress' },
        @{ Action='collect'; Expected='Collecting' }
    )
    foreach ($step in $steps) {
        $transition = Invoke-ExamApi -Method POST -Path "api/v1/sessions/$sessionId/$($step.Action)" -Token $token -Body @{}
        $actual = $transition.data.summary.status
        if ($actual -ne $step.Expected) { throw "$($step.Action) returned $actual, expected $($step.Expected)." }
        Pass "Session $($step.Action)" "status=$actual"
    }

    $end = Invoke-ExamApi -Method POST -Path "api/v1/sessions/$sessionId/end" -Token $token -Body @{
        force = $false
        reason = $null
    }
    if ($end.data.summary.status -ne 'Finished') { throw "End returned $($end.data.summary.status)." }
    Pass 'Session end' 'status=Finished'

    $state = [pscustomobject]@{
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        prefix = $prefix
        classId = $classId
        examId = $examId
        sessionId = $sessionId
        roomCode = $sessionResponse.data.summary.roomCode
    }
    $stateDir = Split-Path -Parent $StatePath
    if (-not [string]::IsNullOrWhiteSpace($stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    Pass 'Save restart-verification state' $StatePath
    Write-Host "`nStop and restart backend, then run:" -ForegroundColor Cyan
    Write-Host "powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -VerifyAfterRestart -BaseUrl `"$BaseUrl`" -StatePath `"$StatePath`"" -ForegroundColor White
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        try {
            [void](Invoke-ExamApi -Method POST -Path 'api/v1/auth/logout' -Token $token -Body @{
                deviceId = $deviceId
                reason = 'acceptance_script_completed'
            })
            Pass 'Logout acceptance account session' $deviceId
        } catch {
            Write-Warning "Could not logout acceptance session: $($_.Exception.Message)"
        }
    }
    $password = $null
    $token = $null
    Remove-Variable passwordSecure -ErrorAction SilentlyContinue
}

