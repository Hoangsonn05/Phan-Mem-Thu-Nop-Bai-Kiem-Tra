[CmdletBinding()]
param(
    [string]$ProjectRoot = ".",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactRoot = Join-Path $ProjectRoot "artifacts\baseline-exam-workflow\$timestamp"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

$transcriptPath = Join-Path $artifactRoot "baseline-transcript.txt"
$summaryPath = Join-Path $artifactRoot "baseline-summary.txt"
$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [string]$Name,
        [string]$Result,
        [string]$Detail
    )
    $results.Add([pscustomobject]@{
            Step   = $Name
            Result = $Result
            Detail = $Detail
        })
}

function Invoke-BaselineStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [switch]$NativeCommand
    )

    $logPath = Join-Path $artifactRoot (($Name -replace '[^A-Za-z0-9._-]', '_') + ".log")
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan

    try {
        $global:LASTEXITCODE = 0
        & $Operation 2>&1 | Tee-Object -FilePath $logPath
        $exitCode = if ($NativeCommand) { $LASTEXITCODE } else { 0 }

        if ($NativeCommand -and $exitCode -ne 0) {
            Add-Result -Name $Name -Result "FAIL" -Detail "ExitCode=$exitCode; Log=$logPath"
            Write-Host "FAIL $Name (exit $exitCode)" -ForegroundColor Red
            return $false
        }

        Add-Result -Name $Name -Result "PASS" -Detail "Log=$logPath"
        Write-Host "PASS $Name" -ForegroundColor Green
        return $true
    }
    catch {
        $_ | Out-String | Tee-Object -FilePath $logPath -Append | Write-Host
        Add-Result -Name $Name -Result "BLOCKED" -Detail "$($_.Exception.Message); Log=$logPath"
        Write-Host "BLOCKED $Name" -ForegroundColor Yellow
        return $false
    }
}

$required = @(
    "global.json",
    "ExamTransfer.slnx",
    "backend\ExamTransfer.sln",
    "frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj",
    "scripts\verify.ps1",
    "frontend\scripts\verify-frontend.ps1"
)

foreach ($relative in $required) {
    $full = Join-Path $ProjectRoot $relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Required file not found: $full"
    }
}

Push-Location $ProjectRoot
try {
    Start-Transcript -Path $transcriptPath -Force | Out-Null

    Invoke-BaselineStep -Name "Environment_DotNet_Info" -NativeCommand -Operation {
        dotnet --info
    } | Out-Null

    Invoke-BaselineStep -Name "Environment_DotNet_SDKs" -NativeCommand -Operation {
        dotnet --list-sdks
    } | Out-Null

    if (Get-Command git -ErrorAction SilentlyContinue) {
        Invoke-BaselineStep -Name "Git_HEAD_Branch_Status" -NativeCommand -Operation {
            git rev-parse HEAD
            git branch --show-current
            git status --short
            git diff --name-status
            git diff --cached --name-status
        } | Out-Null
    }
    else {
        Add-Result -Name "Git_HEAD_Branch_Status" -Result "BLOCKED" -Detail "git command not found"
    }

    $restorePassed = Invoke-BaselineStep -Name "Restore_Root_Solution" -NativeCommand -Operation {
        dotnet restore ".\ExamTransfer.slnx"
    }

    $buildPassed = $false
    if ($restorePassed) {
        $buildPassed = Invoke-BaselineStep -Name "Build_Root_Solution" -NativeCommand -Operation {
            dotnet build ".\ExamTransfer.slnx" -c $Configuration --no-restore
        }
    }
    else {
        Add-Result -Name "Build_Root_Solution" -Result "BLOCKED" -Detail "Restore failed"
    }

    $backendRestorePassed = Invoke-BaselineStep -Name "Restore_Backend_Tests" -NativeCommand -Operation {
        dotnet restore ".\backend\ExamTransfer.sln"
    }

    if ($backendRestorePassed) {
        Invoke-BaselineStep -Name "Backend_All_Tests" -NativeCommand -Operation {
            dotnet test ".\backend\ExamTransfer.sln" -c $Configuration --no-restore
        } | Out-Null

        Invoke-BaselineStep -Name "Backend_Quiz_Focused_Tests" -NativeCommand -Operation {
            dotnet test ".\backend\tests\ExamTransfer.Infrastructure.Tests\ExamTransfer.Infrastructure.Tests.csproj" `
                -c $Configuration --no-restore `
                --filter "FullyQualifiedName~Quiz|FullyQualifiedName~PublicCloud|FullyQualifiedName~Session"
        } | Out-Null
    }
    else {
        Add-Result -Name "Backend_All_Tests" -Result "BLOCKED" -Detail "Backend restore failed"
        Add-Result -Name "Backend_Quiz_Focused_Tests" -Result "BLOCKED" -Detail "Backend restore failed"
    }

    Invoke-BaselineStep -Name "Frontend_Verification" -NativeCommand -Operation {
        powershell -ExecutionPolicy Bypass -File ".\frontend\scripts\verify-frontend.ps1" -Configuration $Configuration
    } | Out-Null

    $results | Format-Table -AutoSize | Out-String | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    $failed = @($results | Where-Object { $_.Result -eq "FAIL" }).Count
    $blocked = @($results | Where-Object { $_.Result -eq "BLOCKED" }).Count
    $overall = if ($failed -gt 0) { "FAIL" } elseif ($blocked -gt 0) { "BLOCKED" } else { "PASS" }

    Add-Content -LiteralPath $summaryPath -Encoding UTF8 -Value @"

OVERALL: $overall
PROJECT_ROOT: $ProjectRoot
CONFIGURATION: $Configuration
TRANSCRIPT: $transcriptPath

This script did not run database migrations, Supabase commands, Docker changes, or production-code edits.
"@

    Write-Host "`nOVERALL: $overall" -ForegroundColor $(if ($overall -eq "PASS") { "Green" } elseif ($overall -eq "FAIL") { "Red" } else { "Yellow" })
    Write-Host "Summary: $summaryPath"
    Write-Host "Transcript: $transcriptPath"

    if ($overall -ne "PASS") {
        exit 1
    }
}
finally {
    try { Stop-Transcript | Out-Null } catch {}
    Pop-Location
}
