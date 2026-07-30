param(
    [Parameter(Mandatory = $true)]
    [string]$StudentCode,

    [string]$BaseUrl = "http://localhost:5048"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-PlainText([Security.SecureString]$SecureValue) {
    $credential = New-Object System.Net.NetworkCredential("", $SecureValue)
    return $credential.Password
}

function Invoke-Status {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body = $null
    )

    try {
        $parameters = @{
            Method = $Method
            Uri = $Uri
            Headers = $Headers
            UseBasicParsing = $true
        }
        if ($null -ne $Body) {
            $parameters.ContentType = "application/json"
            $parameters.Body = ($Body | ConvertTo-Json -Depth 10)
        }

        $response = Invoke-WebRequest @parameters
        return [int]$response.StatusCode
    }
    catch {
        if ($null -ne $_.Exception.Response) {
            return [int]$_.Exception.Response.StatusCode
        }
        throw
    }
}

function Assert-Status {
    param(
        [string]$Name,
        [int]$Actual,
        [int[]]$Expected
    )

    if ($Expected -contains $Actual) {
        Write-Host "[PASS] $Name -> HTTP $Actual" -ForegroundColor Green
        return
    }

    throw "[FAIL] $Name -> HTTP $Actual; expected: $($Expected -join ', ')"
}

$BaseUrl = $BaseUrl.TrimEnd('/')
$securePassword = Read-Host "Nhap mat khau cua sinh vien $StudentCode" -AsSecureString
$password = ConvertTo-PlainText $securePassword

try {
    $deviceId = "PERMISSION-TEST-" + [Guid]::NewGuid().ToString("N")
    $loginBody = @{
        account = $StudentCode.Trim()
        password = $password
        deviceId = $deviceId
        machineName = $env:COMPUTERNAME
        appVersion = "stage6-permission-test"
    } | ConvertTo-Json

    $login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/login" -ContentType "application/json" -Body $loginBody

    if (-not $login.success -or [string]::IsNullOrWhiteSpace($login.data.accessToken)) {
        throw "Dang nhap that bai: $($login.error.message)"
    }

    $headers = @{ Authorization = "Bearer $($login.data.accessToken)" }
    $me = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/auth/me" -Headers $headers
    if (-not $me.success) {
        throw "Khong doc duoc /auth/me."
    }

    Write-Host "Tai khoan: $($me.data.displayName) - $($me.data.studentCode)" -ForegroundColor Cyan
    Write-Host "MustChangePassword: $($me.data.mustChangePassword)" -ForegroundColor Cyan

    Assert-Status "Student khong duoc xem danh sach lop" (Invoke-Status Get "$BaseUrl/api/v1/classes" $headers) @(403)
    Assert-Status "Student khong duoc xem diagnostics" (Invoke-Status Get "$BaseUrl/api/v1/system/diagnostics" $headers) @(403)
    Assert-Status "Student khong duoc tao phong thi" (Invoke-Status Post "$BaseUrl/api/v1/sessions" $headers @{}) @(403)

    $resultProbe = Invoke-Status Get "$BaseUrl/api/v1/student/submissions/00000000-0000-0000-0000-000000000000/grade" $headers

    if ($me.data.mustChangePassword) {
        Assert-Status "Chua doi mat khau thi Student API bi khoa" $resultProbe @(403)
    }
    else {
        Assert-Status "Da doi mat khau thi Student policy cho phep di tiep" $resultProbe @(404)
    }

    Write-Host "Kiem thu phan quyen Student hoan thanh." -ForegroundColor Green
}
finally {
    $password = $null
    Remove-Variable securePassword -ErrorAction SilentlyContinue
}
