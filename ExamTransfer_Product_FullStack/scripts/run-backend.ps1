param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$backendProject = Join-Path $ProjectRoot "backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj"

if (-not (Test-Path -LiteralPath $backendProject)) {
    throw "Backend project not found: $backendProject"
}

dotnet run --project $backendProject -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Backend exited with code $LASTEXITCODE."
}
