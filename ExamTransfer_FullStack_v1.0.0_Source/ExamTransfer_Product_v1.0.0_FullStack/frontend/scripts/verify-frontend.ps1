param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ArtifactsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$frontendRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $frontendRoot 'src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'
$ownsArtifactsPath = [string]::IsNullOrWhiteSpace($ArtifactsPath)
if ($ownsArtifactsPath) {
    $ArtifactsPath = Join-Path ([IO.Path]::GetTempPath()) "examtransfer-frontend-verify-$([Guid]::NewGuid().ToString('N'))"
}
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)

function Invoke-FrontendCheck {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$File,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][scriptblock]$Operation
    )

    try {
        $global:LASTEXITCODE = 0
        & $Operation
        if ($LASTEXITCODE -ne 0) {
            throw "Native command exited with code $LASTEXITCODE."
        }
        Write-Host "PASS $Name file=$File" -ForegroundColor Green
    } catch {
        Write-Host "FAIL $Name file=$File expected=$Expected actual=$($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

try {
    Invoke-FrontendCheck `
        -Name 'frontend restore' `
        -File $project `
        -Expected 'NuGet restore succeeds in the isolated artifacts directory' `
        -Operation {
            & dotnet restore $project "-p:ArtifactsPath=$ArtifactsPath"
        }

    Invoke-FrontendCheck `
        -Name 'frontend build' `
        -File $project `
        -Expected "$Configuration WPF build succeeds without writing to the running application's bin directory" `
        -Operation {
            & dotnet build $project -c $Configuration --no-restore "-p:ArtifactsPath=$ArtifactsPath"
        }

    $outputDirectory = Join-Path $ArtifactsPath "bin\ExamTransfer.Desktop\$($Configuration.ToLowerInvariant())"
    $desktopAssembly = Join-Path $outputDirectory 'ExamTransfer.Desktop.dll'
    $desktopExecutable = Join-Path $outputDirectory 'ExamTransfer.Desktop.exe'
    Invoke-FrontendCheck `
        -Name 'frontend output verification' `
        -File $outputDirectory `
        -Expected 'ExamTransfer.Desktop.dll and ExamTransfer.Desktop.exe both exist' `
        -Operation {
            if (-not (Test-Path -LiteralPath $desktopAssembly) -or
                -not (Test-Path -LiteralPath $desktopExecutable)) {
                throw "Missing output. dll=$(Test-Path -LiteralPath $desktopAssembly); exe=$(Test-Path -LiteralPath $desktopExecutable)"
            }
        }

    Write-Host "PASS frontend verification configuration=$Configuration artifacts=$ArtifactsPath" -ForegroundColor Green
} finally {
    if ($ownsArtifactsPath -and (Test-Path -LiteralPath $ArtifactsPath)) {
        $resolvedArtifacts = (Resolve-Path -LiteralPath $ArtifactsPath).Path
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedArtifacts.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an artifacts directory outside TEMP: $resolvedArtifacts"
        }
        Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
    }
}
