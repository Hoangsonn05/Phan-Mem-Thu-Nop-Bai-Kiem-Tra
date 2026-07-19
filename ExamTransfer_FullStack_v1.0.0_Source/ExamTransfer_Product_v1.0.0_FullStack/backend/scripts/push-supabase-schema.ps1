param(
    [string]$BackendRoot,
    [Parameter(Mandatory = $true)]
    [string]$ProjectRef,
    [switch]$IncludeSeed,
    [switch]$RunDatabaseTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# $PSScriptRoot is reliable after the param block has finished binding.
# Do not use it as a default value directly inside param(...), because it can
# be empty while PowerShell is evaluating parameter defaults.
if ([string]::IsNullOrWhiteSpace($BackendRoot)) {
    $BackendRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}
else {
    $BackendRoot = (Resolve-Path -LiteralPath $BackendRoot).Path
}

function Invoke-Checked {
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

if (-not (Get-Command supabase -ErrorAction SilentlyContinue)) {
    throw "Supabase CLI is not available. Install it using an official Supabase-supported method, then run this script again."
}

$configPath = Join-Path $BackendRoot "supabase\config.toml"
$migrationPath = Join-Path $BackendRoot "supabase\migrations"
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Supabase config not found: $configPath"
}
if (-not (Test-Path -LiteralPath $migrationPath)) {
    throw "Supabase migrations not found: $migrationPath"
}

Push-Location $BackendRoot
try {
    Invoke-Checked -Description "Linking Supabase project" -Command {
        supabase link --project-ref $ProjectRef
    }

    if ($IncludeSeed) {
        Invoke-Checked -Description "Pushing schema and seed" -Command {
            supabase db push --include-seed
        }
    }
    else {
        Invoke-Checked -Description "Pushing schema migrations" -Command {
            supabase db push
        }
    }

    Invoke-Checked -Description "Running linked database lint" -Command {
        supabase db lint --linked --level warning
    }

    if ($RunDatabaseTests) {
        Invoke-Checked -Description "Running Supabase database tests" -Command {
            supabase test db
        }
    }
}
finally {
    Pop-Location
}

Write-Host "Supabase schema deployment completed." -ForegroundColor Green
