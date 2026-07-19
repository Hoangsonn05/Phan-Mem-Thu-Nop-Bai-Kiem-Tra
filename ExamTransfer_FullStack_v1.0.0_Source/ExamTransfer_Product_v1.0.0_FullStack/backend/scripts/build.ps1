param(
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$backendRoot = Split-Path $PSScriptRoot -Parent
$solutionFile = Join-Path $backendRoot "ExamTransfer.sln"

Push-Location $backendRoot
try {
    dotnet restore $solutionFile
    if ($LASTEXITCODE -ne 0) { throw "Backend restore failed." }

    dotnet build $solutionFile -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Backend build failed." }

    Write-Host "Backend build completed." -ForegroundColor Green
}
finally {
    Pop-Location
}
