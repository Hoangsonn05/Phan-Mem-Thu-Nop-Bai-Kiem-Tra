param(
    [Parameter(Mandatory = $true)]
    [string]$StudentCode,

    [string]$ConfigPath = "C:\ProgramData\ExamTransfer\config\runtime-settings.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Khong tim thay runtime settings: $ConfigPath"
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$url = [string]$config.Cloud.SupabaseUrl
$key = [string]$config.Cloud.PublishableKey
$domain = [string]$config.Auth.StudentEmailDomain

if ([string]::IsNullOrWhiteSpace($url) -or [string]::IsNullOrWhiteSpace($key)) {
    throw "Runtime settings thieu SupabaseUrl hoac PublishableKey."
}
if ([string]::IsNullOrWhiteSpace($domain)) {
    $domain = "students.examtransfer.local"
}

$securePassword = Read-Host "Nhap mat khau cua sinh vien $StudentCode" -AsSecureString
$credential = New-Object System.Net.NetworkCredential("", $securePassword)
$password = $credential.Password

try {
    $email = "$($StudentCode.Trim())@$($domain.Trim().TrimStart('@'))".ToLowerInvariant()
    $authHeaders = @{ apikey = $key; Authorization = "Bearer $key" }
    $session = Invoke-RestMethod -Method Post -Uri "$($url.TrimEnd('/'))/auth/v1/token?grant_type=password" -Headers $authHeaders -ContentType "application/json" -Body (@{ email = $email; password = $password } | ConvertTo-Json)

    $userHeaders = @{ apikey = $key; Authorization = "Bearer $($session.access_token)" }
    $profiles = Invoke-RestMethod -Method Get -Uri "$($url.TrimEnd('/'))/rest/v1/profiles?select=id,student_code,display_name,role,must_change_password" -Headers $userHeaders

    $profileArray = @($profiles)
    if ($profileArray.Count -ne 1) {
        throw "RLS loi: Student doc duoc $($profileArray.Count) profile, mong doi dung 1."
    }
    if ([string]$profileArray[0].student_code -ne $StudentCode.Trim()) {
        throw "RLS loi: profile tra ve khong phai cua sinh vien dang nhap."
    }

    Write-Host "[PASS] Student chi doc duoc profile cua chinh minh." -ForegroundColor Green

    $updateHeaders = @{
        apikey = $key
        Authorization = "Bearer $($session.access_token)"
        Prefer = "return=representation"
    }

    try {
        $updated = Invoke-RestMethod -Method Patch -Uri "$($url.TrimEnd('/'))/rest/v1/profiles?id=eq.$($session.user.id)" -Headers $updateHeaders -ContentType "application/json" -Body (@{ display_name = "RLS SHOULD BLOCK" } | ConvertTo-Json)
        $updatedRows = @($updated)
        if ($updatedRows.Count -gt 0) {
            throw "RLS loi: Student co the sua truc tiep public.profiles."
        }

        Write-Host "[PASS] Student khong the sua truc tiep public.profiles (0 dong duoc cap nhat)." -ForegroundColor Green
    }
    catch {
        if ($_.Exception.Message -like "RLS loi:*") { throw }
        Write-Host "[PASS] Student khong the sua truc tiep public.profiles (HTTP bi tu choi)." -ForegroundColor Green
    }
}
finally {
    $password = $null
    $credential = $null
    Remove-Variable securePassword -ErrorAction SilentlyContinue
}
