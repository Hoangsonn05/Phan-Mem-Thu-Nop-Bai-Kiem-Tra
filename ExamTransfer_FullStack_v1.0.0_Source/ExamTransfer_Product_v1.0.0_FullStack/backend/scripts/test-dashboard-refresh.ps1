param(
    [string]$BaseUrl = "http://localhost:5048",
    [PSCredential]$Credential
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd('/')
$classPrefix = "DASHREFRESH-"

function Get-DashboardCredential {
    if ($Credential) { return $Credential }

    $account = $env:EXAMTRANSFER_ADMIN_ACCOUNT
    $password = $env:EXAMTRANSFER_ADMIN_PASSWORD
    if (-not [string]::IsNullOrWhiteSpace($account) -and -not [string]::IsNullOrWhiteSpace($password)) {
        return New-Object Management.Automation.PSCredential(
            $account,
            (ConvertTo-SecureString $password -AsPlainText -Force))
    }

    return Get-Credential -Message "ExamTransfer teacher/admin account (password is never printed)"
}

function Invoke-DashboardApi {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [object]$Body,
        [string]$Token,
        [Parameter(Mandatory = $true)][string]$Step
    )

    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $headers.Authorization = "Bearer $Token"
    }

    $parameters = @{
        Uri             = "$BaseUrl/$($Path.TrimStart('/'))"
        Method          = $Method
        Headers         = $headers
        UseBasicParsing = $true
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 10 -Compress
    }

    try {
        $response = Invoke-WebRequest @parameters
        $payload = $response.Content | ConvertFrom-Json
        $apiCode = if ($payload.error -and $payload.error.code) { [string]$payload.error.code } else { "OK" }
        $traceId = if ($payload.traceId) { [string]$payload.traceId } else { "UNAVAILABLE" }
        if (-not $payload.success) {
            Write-Host "FAIL [$Step] HTTP $([int]$response.StatusCode) API $apiCode traceId=$traceId"
            throw "API reported failure: $apiCode"
        }

        Write-Host "PASS [$Step] HTTP $([int]$response.StatusCode) API $apiCode traceId=$traceId"
        return [pscustomobject]@{
            HttpStatus = [int]$response.StatusCode
            Payload    = $payload
        }
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

function Assert-DashboardStep {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    if (-not $Condition) {
        Write-Host "FAIL [$Step] HTTP N/A API ASSERTION_FAILED traceId=UNAVAILABLE"
        throw $FailureMessage
    }

    Write-Host "PASS [$Step] HTTP N/A API OK traceId=UNAVAILABLE"
}

$loginCredential = Get-DashboardCredential
$plainPassword = $null
$token = $null
$createdClassId = $null
$createdClassCode = $null
$archived = $false

try {
    $deviceId = "dashboard-refresh-$([Guid]::NewGuid().ToString('N'))"
    $plainPassword = $loginCredential.GetNetworkCredential().Password
    $login = Invoke-DashboardApi -Method POST -Path "api/v1/auth/login" -Body @{
        account     = $loginCredential.UserName
        password    = $plainPassword
        deviceId    = $deviceId
        machineName = $env:COMPUTERNAME
        appVersion  = "dashboard-refresh-test/1.0"
    } -Step "Login"
    $token = $login.Payload.data.accessToken
    Assert-DashboardStep (-not [string]::IsNullOrWhiteSpace($token)) "Validate login token" "Login succeeded without an access token."
    $plainPassword = $null

    $initial = Invoke-DashboardApi -Method GET -Path "api/v1/dashboard/summary" -Token $token -Step "Read initial dashboard"
    $initialCount = [int]$initial.Payload.data.classCount

    $suffix = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $createdClassCode = "$classPrefix$suffix"
    $created = Invoke-DashboardApi -Method POST -Path "api/v1/classes" -Token $token -Body @{
        name        = "Dashboard refresh $suffix"
        code        = $createdClassCode
        schoolYear  = "2026-2027"
        description = "Created only by test-dashboard-refresh.ps1"
    } -Step "Create prefixed class"
    $createdClassId = [string]$created.Payload.data.id

    $afterCreate = Invoke-DashboardApi -Method GET -Path "api/v1/dashboard/summary" -Token $token -Step "Read dashboard after create"
    Assert-DashboardStep ([int]$afterCreate.Payload.data.classCount -eq $initialCount + 1) "Validate class count increased" "Dashboard classCount did not increase by exactly one."

    $classes = Invoke-DashboardApi -Method GET -Path "api/v1/classes?page=1&pageSize=200" -Token $token -Step "List classes after create"
    $createdListItem = $classes.Payload.data.items | Where-Object { [string]$_.id -eq $createdClassId -and [string]$_.code -eq $createdClassCode }
    Assert-DashboardStep ($null -ne $createdListItem) "Validate class exists" "The newly created prefixed class was not returned by GET /api/v1/classes."

    [void](Invoke-DashboardApi -Method DELETE -Path "api/v1/classes/$createdClassId" -Token $token -Step "Archive prefixed class")
    $archived = $true

    $afterArchive = Invoke-DashboardApi -Method GET -Path "api/v1/dashboard/summary" -Token $token -Step "Read dashboard after archive"
    Assert-DashboardStep ([int]$afterArchive.Payload.data.classCount -eq $initialCount) "Validate class count restored" "Dashboard classCount did not return to its initial value."

    Write-Host "DASHBOARD REFRESH PASS"
}
finally {
    $plainPassword = $null

    if (-not $archived -and -not [string]::IsNullOrWhiteSpace($createdClassId) -and -not [string]::IsNullOrWhiteSpace($token)) {
        try {
            $detail = Invoke-DashboardApi -Method GET -Path "api/v1/classes/$createdClassId" -Token $token -Step "Validate cleanup target"
            $actualCode = [string]$detail.Payload.data.code
            if ($actualCode.StartsWith($classPrefix, [StringComparison]::Ordinal)) {
                [void](Invoke-DashboardApi -Method DELETE -Path "api/v1/classes/$createdClassId" -Token $token -Step "Cleanup prefixed class")
            }
            else {
                Write-Host "FAIL [Cleanup prefixed class] HTTP N/A API CLEANUP_REFUSED traceId=UNAVAILABLE"
                Write-Host "      Cleanup refused because the class code does not use prefix $classPrefix."
            }
        }
        catch {
            Write-Host "FAIL [Cleanup prefixed class] HTTP N/A API CLEANUP_FAILED traceId=UNAVAILABLE"
        }
    }
}
