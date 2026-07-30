[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5048',
    [string]$Account = 'admin@gmail.com',
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-SecureStringPlain {
    param([Security.SecureString]$Secure)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Invoke-ExamApi {
    param(
        [Parameter(Mandatory=$true)][string]$Method,
        [Parameter(Mandatory=$true)][string]$Path,
        [object]$Body,
        [string]$Token
    )

    $parameters = @{
        Uri = $BaseUrl.TrimEnd('/') + '/' + $Path.TrimStart('/')
        Method = $Method
        Headers = @{}
        ContentType = 'application/json'
    }
    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $parameters.Headers.Authorization = "Bearer $Token"
    }
    if ($null -ne $Body) {
        $parameters.Body = $Body | ConvertTo-Json -Depth 12 -Compress
    }

    try {
        $response = Invoke-RestMethod @parameters
        if (-not $response.success) {
            throw "API_FAIL code=$($response.error.code) message=$($response.error.message) traceId=$($response.traceId)"
        }
        return $response
    }
    catch {
        $errorDetails = $_.ErrorDetails
        if ($null -ne $errorDetails -and -not [string]::IsNullOrWhiteSpace($errorDetails.Message)) {
            try {
                $payload = $errorDetails.Message | ConvertFrom-Json
                throw "HTTP/API failure: code=$($payload.error.code) message=$($payload.error.message) traceId=$($payload.traceId)"
            }
            catch {
                if ($_.Exception.Message -like 'HTTP/API failure:*') { throw }
            }
        }
        throw
    }
}

function Write-Pass {
    param([string]$Step, [string]$Detail)
    Write-Host "[PASS] $Step - $Detail" -ForegroundColor Green
}

$health = Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + '/health') -Method Get
Write-Pass 'Published server health' "status=$($health.status) buildId=$($health.buildId)"

$securePassword = Read-Host 'Password' -AsSecureString
$password = ConvertFrom-SecureStringPlain $securePassword
$token = $null
$deviceId = 'CLOUD-ACCEPTANCE-' + [Guid]::NewGuid().ToString('N')
$machineName = if ([string]::IsNullOrWhiteSpace($env:COMPUTERNAME)) {
    [Environment]::MachineName
} else {
    $env:COMPUTERNAME
}

try {
    $login = Invoke-ExamApi -Method POST -Path 'api/v1/auth/login' -Body @{
        account = $Account
        password = $password
        deviceId = $deviceId
        machineName = $machineName
        appVersion = 'published-cloud-acceptance-1.0'
    }
    $token = $login.data.accessToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'Login succeeded but no access token was returned.'
    }
    Write-Pass 'Admin login' "role=$($login.data.role) user=$($login.data.displayName)"

    $session = (Invoke-ExamApi -Method GET -Path 'api/v1/cloud/auth/session' -Token $token).data
    if (-not $session.authenticated) {
        throw 'Application login did not establish an authenticated cloud session.'
    }
    Write-Pass 'Cloud session handoff' "email=$($session.email) role=$($session.role) organizationId=$($session.organizationId)"

    $preflight = (Invoke-ExamApi -Method GET -Path 'api/v1/cloud/preflight' -Token $token).data
    if (-not $preflight.enabled -or -not $preflight.configured -or -not $preflight.reachable -or -not $preflight.canSynchronize) {
        $errors = @($preflight.errors) -join '; '
        $warnings = @($preflight.warnings) -join '; '
        throw "Cloud preflight failed: enabled=$($preflight.enabled) configured=$($preflight.configured) reachable=$($preflight.reachable) authenticated=$($preflight.authenticated) canSynchronize=$($preflight.canSynchronize) errors=$errors warnings=$warnings"
    }
    Write-Pass 'Cloud preflight' "reachable=$($preflight.reachable) keyMode=$($preflight.keyMode) accessMode=$($preflight.accessMode)"

    $initial = (Invoke-ExamApi -Method GET -Path 'api/v1/cloud/sync/status' -Token $token).data
    Write-Host "[INFO] Initial sync status=$($initial.status) pending=$($initial.pendingItems) lastError=$($initial.lastError)"

    [void](Invoke-ExamApi -Method POST -Path 'api/v1/cloud/sync' -Token $token)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(10, $TimeoutSeconds))
    $status = $initial
    do {
        Start-Sleep -Seconds 2
        $status = (Invoke-ExamApi -Method GET -Path 'api/v1/cloud/sync/status' -Token $token).data
        Write-Host "[INFO] Sync status=$($status.status) pending=$($status.pendingItems)"
        if ($status.status -eq 'Synced' -and [int]$status.pendingItems -eq 0) { break }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ($status.status -ne 'Synced' -or [int]$status.pendingItems -ne 0) {
        throw "Cloud sync did not complete: status=$($status.status) pending=$($status.pendingItems) lastError=$($status.lastError)"
    }

    Write-Pass 'SQLite to Supabase queue' "status=$($status.status) pending=$($status.pendingItems) lastSuccessUtc=$($status.lastSuccessUtc)"
}
finally {
    $password = $null
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        try {
            [void](Invoke-ExamApi -Method POST -Path 'api/v1/auth/logout' -Token $token -Body @{
                deviceId = $deviceId
            })
            Write-Pass 'Logout' 'local and cloud sessions cleared'
        }
        catch {
            Write-Warning "Logout cleanup failed: $($_.Exception.Message)"
        }
    }
}
