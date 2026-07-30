param(
    [switch]$SkipSupabase
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Script
    )

    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Script
}

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode."
    }
}

Invoke-Step "Restore backend" {
    Invoke-Native "dotnet" @("restore", (Join-Path $root "backend/ExamTransfer.sln"))
}

Invoke-Step "Build backend" {
    Invoke-Native "dotnet" @("build", (Join-Path $root "backend/ExamTransfer.sln"), "-c", "Release", "--no-restore")
}

Invoke-Step "Test backend" {
    Invoke-Native "dotnet" @("test", (Join-Path $root "backend/ExamTransfer.sln"), "-c", "Release", "--no-build")
}

Invoke-Step "Restore frontend" {
    Invoke-Native "dotnet" @("restore", (Join-Path $root "frontend/src/ExamTransfer.Desktop/ExamTransfer.Desktop.csproj"))
}

Invoke-Step "Build frontend" {
    Invoke-Native "dotnet" @("build", (Join-Path $root "frontend/src/ExamTransfer.Desktop/ExamTransfer.Desktop.csproj"), "-c", "Release", "--no-restore")
}

Invoke-Step "Contract surface check" {
    $contracts = Join-Path $root "backend/src/ExamTransfer.Shared.Contracts"
    $required = @(
        "AccountLoginRequest",
        "AccountLoginResultDto",
        "StudentIdentityConfirmRequest",
        "CurrentAccountDto",
        "AccountHeartbeatRequest",
        "AccountHeartbeatResponse",
        "LogoutRequest",
        "ACCOUNT_ALREADY_ACTIVE",
        "PARTICIPANT_ACCOUNT_MISMATCH"
    )

    foreach ($term in $required) {
        $hit = Select-String -Path (Join-Path $contracts "*.cs") -Pattern $term -SimpleMatch -Quiet
        if (-not $hit) {
            throw "Missing contract term: $term"
        }
    }
}

if (-not $SkipSupabase) {
    $supabase = Get-Command npx -ErrorAction SilentlyContinue
    if ($null -eq $supabase) {
        Write-Warning "npx not found; skipping Supabase db reset/lint/test."
    }
    else {
        Push-Location (Join-Path $root "backend")
        try {
            Invoke-Step "Supabase db reset" {
                Invoke-Native "npx" @("supabase", "db", "reset")
            }
            Invoke-Step "Supabase db lint" {
                Invoke-Native "npx" @("supabase", "db", "lint", "--local", "--level", "warning")
            }
            Invoke-Step "Supabase db test" {
                Invoke-Native "npx" @("supabase", "test", "db")
            }
        }
        finally {
            Pop-Location
        }
    }
}

Write-Host "Pre-Supabase verification completed." -ForegroundColor Green
