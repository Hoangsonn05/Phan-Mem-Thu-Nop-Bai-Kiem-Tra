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

    Write-Host "Backend verification completed." -ForegroundColor Green
}
finally {
    Pop-Location
}

$supabaseVerifier = Join-Path $backendRoot "scripts\verify-supabase-source.ps1"
if (Test-Path -LiteralPath $supabaseVerifier) {
    & $supabaseVerifier -BackendRoot $backendRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Supabase source verification failed."
    }
}

