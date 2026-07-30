param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Configuration = "Debug"
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
$migratorProject = Join-Path $ProjectRoot "backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj"

foreach ($path in @($solutionFile, $migratorProject)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required file not found: $path"
    }
}

Invoke-CheckedCommand -Description "Restoring solution" -Command {
    dotnet restore $solutionFile
}

Invoke-CheckedCommand -Description "Running database migrator" -Command {
    dotnet run --project $migratorProject -c $Configuration
}
