param(
    [object]$UseMock = $false,
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

$useMockValue = $false

if ($UseMock -is [bool]) {
    $useMockValue = [bool]$UseMock
}
elseif ($UseMock -is [int]) {
    $useMockValue = [bool]$UseMock
}
else {
    $useMockText = $UseMock.ToString()
    if (-not [bool]::TryParse($useMockText, [ref]$useMockValue)) {
        throw "UseMock must be true or false."
    }
}

$env:EXAMTRANSFER_USE_MOCK = $useMockValue.ToString().ToLowerInvariant()
$env:EXAMTRANSFER_API = $ApiUrl

Write-Host "Starting frontend. UseMock=$env:EXAMTRANSFER_USE_MOCK ApiUrl=$env:EXAMTRANSFER_API" -ForegroundColor Cyan
dotnet run --project $frontendProject -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Frontend exited with code $LASTEXITCODE."
}
