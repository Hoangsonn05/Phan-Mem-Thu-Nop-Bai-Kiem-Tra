param(
    [string]$ApiUrl = "http://localhost:5048",
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$frontendProject = Join-Path $ProjectRoot "frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj"

if (-not (Test-Path -LiteralPath $frontendProject)) {
    throw "Frontend project not found: $frontendProject"
}

$env:EXAMTRANSFER_API = $ApiUrl

Write-Host "Starting frontend. ApiUrl=$env:EXAMTRANSFER_API" -ForegroundColor Cyan
dotnet run --project $frontendProject -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Frontend exited with code $LASTEXITCODE."
}
