param(
    [Parameter(Mandatory = $true)]
    [string]$SupabaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$PublishableKey,

    [Parameter(Mandatory = $true)]
    [guid]$OrganizationId,

    [ValidateSet("Development", "Staging", "Production")]
    [string]$Environment = "Development",

    [ValidateSet("UserSession", "TrustedServer")]
    [string]$AccessMode = "UserSession",

    [string]$SecretKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$uri = $null
if (-not [Uri]::TryCreate($SupabaseUrl, [UriKind]::Absolute, [ref]$uri)) {
    throw "SupabaseUrl is not a valid absolute URL."
}
if ($uri.Scheme -ne "https" -and -not $uri.IsLoopback) {
    throw "SupabaseUrl must use HTTPS, except for local Supabase."
}
if ([string]::IsNullOrWhiteSpace($PublishableKey)) {
    throw "PublishableKey is required."
}

$programData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::CommonApplicationData)
if ([string]::IsNullOrWhiteSpace($programData)) {
    $programData = $env:ProgramData
}
if ([string]::IsNullOrWhiteSpace($programData)) {
    throw "Cannot resolve ProgramData."
}

$configDirectory = Join-Path $programData "ExamTransfer\config"
$configPath = Join-Path $configDirectory "runtime-settings.json"
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

$payload = [ordered]@{}
if (Test-Path -LiteralPath $configPath) {
    $existing = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    foreach ($property in $existing.PSObject.Properties) {
        $payload[$property.Name] = $property.Value
    }
}

$payload["Cloud"] = [ordered]@{
        Enabled = $true
        AccessMode = $AccessMode
        Environment = $Environment
        SupabaseUrl = $SupabaseUrl.TrimEnd('/')
        PublishableKey = $PublishableKey.Trim()
        OrganizationId = $OrganizationId.ToString()
        SecretKeyEnvironmentVariable = "EXAMTRANSFER_SUPABASE_SECRET_KEY"
        ServiceRoleEnvironmentVariable = "EXAMTRANSFER_SUPABASE_SERVICE_KEY"
        Schema = "public"
        ExamBucket = "exam-archives"
        SubmissionBucket = "submission-archives"
        ExportBucket = "report-exports"
        BackupBucket = "backup-archives"
        UseResumableUploads = $true
        StandardUploadThresholdBytes = 6291456
        TusChunkSizeBytes = 6291456
        WorkerBatchSize = 20
        WorkerIntervalSeconds = 10
        LeaseMinutes = 10
        AuthRefreshSkewSeconds = 120
        PersistUserSession = $true
    }

$json = $payload | ConvertTo-Json -Depth 8
$tempPath = "$configPath.tmp"
[IO.File]::WriteAllText($tempPath, $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tempPath -Destination $configPath -Force

if (-not [string]::IsNullOrWhiteSpace($SecretKey)) {
    $env:EXAMTRANSFER_SUPABASE_SECRET_KEY = $SecretKey
}

Write-Host "Supabase non-secret configuration written to:" -ForegroundColor Green
Write-Host $configPath
Write-Host "Organization ID: $OrganizationId"
Write-Host "Environment: $Environment"

if ($AccessMode -eq "TrustedServer" -and [string]::IsNullOrWhiteSpace($SecretKey)) {
    Write-Warning "TrustedServer requires EXAMTRANSFER_SUPABASE_SECRET_KEY before starting the backend."
}
elseif ($AccessMode -eq "TrustedServer") {
    Write-Host "Secret key set for this PowerShell process only." -ForegroundColor Yellow
}
else {
    Write-Host "UserSession mode selected; secret key is not required." -ForegroundColor Green
}
