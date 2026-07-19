param(
    [Parameter(Mandatory = $true)]
    [string]$ExcelPath,

    [int]$Limit = 0,
    [int]$Skip = 0,

    [switch]$DryRun,
    [switch]$VerifyLogin,
    [switch]$ResetExistingPassword,

    [string]$BackendRoot,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($BackendRoot)) {
    $BackendRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}
else {
    $BackendRoot = (Resolve-Path -LiteralPath $BackendRoot).Path
}

$ExcelPath = (Resolve-Path -LiteralPath $ExcelPath).Path
$project = Join-Path $BackendRoot "tools\ExamTransfer.StudentImporter\ExamTransfer.StudentImporter.csproj"
if (-not (Test-Path -LiteralPath $project)) {
    throw "Student importer project not found: $project"
}

if ([string]::IsNullOrWhiteSpace($env:EXAMTRANSFER_SUPABASE_SECRET_KEY) -and
    [string]::IsNullOrWhiteSpace($env:EXAMTRANSFER_SUPABASE_SERVICE_KEY)) {
    throw @"
Chưa có Supabase secret key trong PowerShell hiện tại.
Hãy đặt bằng:
  `$env:EXAMTRANSFER_SUPABASE_SECRET_KEY = "sb_secret_..."
Không lưu khóa này trong mã nguồn hoặc gửi qua chat.
"@
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $artifactDirectory = Join-Path $BackendRoot "artifacts"
    New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
    $ReportPath = Join-Path $artifactDirectory ("student-import-{0}.csv" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$arguments = @(
    "run",
    "--project", $project,
    "--",
    "--file", $ExcelPath,
    "--skip", $Skip.ToString(),
    "--limit", $Limit.ToString(),
    "--report", $ReportPath
)

if ($DryRun) { $arguments += "--dry-run" }
if ($VerifyLogin) { $arguments += "--verify-login" }
if ($ResetExistingPassword) { $arguments += "--reset-existing-password" }

Write-Host "Running ExamTransfer Student Importer..." -ForegroundColor Cyan
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Student importer failed with exit code $LASTEXITCODE."
}
