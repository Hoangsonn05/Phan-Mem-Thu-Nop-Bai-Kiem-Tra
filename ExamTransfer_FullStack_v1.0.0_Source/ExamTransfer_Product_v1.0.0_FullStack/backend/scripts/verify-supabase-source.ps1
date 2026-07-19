param(
    [string]$BackendRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$requiredMigrations = @(
    "202607110001_initial_schema.sql",
    "202607140002_full_frontend_backend_schema.sql",
    "202607150003_security_hardening.sql",
    "202607150004_cloud_bootstrap_helpers.sql",
    "202607150005_user_session_storage_policies.sql",
    "202607150006_production_readiness.sql"
)

foreach ($migration in $requiredMigrations) {
    $path = Join-Path $BackendRoot "supabase\migrations\$migration"
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing Supabase migration: $path"
    }
}

$duplicateSupabase = Join-Path (Split-Path $BackendRoot -Parent) "database\supabase"
if (Test-Path -LiteralPath $duplicateSupabase) {
    throw "Duplicate Supabase migration source detected: $duplicateSupabase"
}

$adapter = Get-Content -LiteralPath (
    Join-Path $BackendRoot "src\ExamTransfer.Infrastructure\Cloud\SupabaseCloudAdapter.cs") -Raw
foreach ($requiredText in @(
    "UserSession",
    "TrustedServer",
    "upload/resumable",
    "RefreshSessionAsync",
    "organization_id",
    "Admin",
    "Teacher")) {
    if ($adapter -notmatch [Regex]::Escape($requiredText)) {
        throw "Supabase adapter is missing required capability: $requiredText"
    }
}

Write-Host "Supabase source verification passed." -ForegroundColor Green
