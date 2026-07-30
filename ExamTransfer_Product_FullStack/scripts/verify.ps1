param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Write-Host $Description -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

$solutionFile = Join-Path $ProjectRoot "ExamTransfer.slnx"
if (-not (Test-Path -LiteralPath $solutionFile)) {
    throw "Solution file not found: $solutionFile"
}

Invoke-CheckedCommand -Description "Restoring solution" -Command {
    dotnet restore $solutionFile
}

Invoke-CheckedCommand -Description "Building solution" -Command {
    dotnet build $solutionFile -c $Configuration --no-restore
}

Write-Host "Source verification completed." -ForegroundColor Green
