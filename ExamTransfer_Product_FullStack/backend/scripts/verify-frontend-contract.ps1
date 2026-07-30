param(
    [string]$FrontendRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$backendRoot = Split-Path $PSScriptRoot -Parent
$controllersRoot = Join-Path $backendRoot "src\ExamTransfer.LocalServer\Controllers"

$requiredFragments = @(
    '[Route("api/v1/classes")]',
    'imports/preview',
    'imports/commit',
    '[Route("api/v1/exams")]',
    'files/{fileId:guid}/chunks/{index:int}',
    '[Route("api/v1/sessions")]',
    '[HttpPost("join")]',
    'participants/bulk-approve',
    '[HttpPost("submissions/init")]',
    'submissions/{id:guid}/finalize',
    '[Route("api/v1/exports")]',
    '[Route("api/v1/grading")]',
    'control-policy',
    '[Route("api/v1/backups")]',
    '[HttpGet("settings")]',
    'audit-logs',
    'history/sessions'
)

$controllerText = (Get-ChildItem $controllersRoot -Filter *.cs -Recurse |
    Get-Content -Raw) -join "`n"

$missing = @()
foreach ($fragment in $requiredFragments) {
    if (-not $controllerText.Contains($fragment)) {
        $missing += $fragment
    }
}

if ($missing.Count -gt 0) {
    throw "Missing backend route fragments: $($missing -join ', ')"
}

if (-not [string]::IsNullOrWhiteSpace($FrontendRoot)) {
    $frontendSource = Join-Path $FrontendRoot "src\ExamTransfer.Desktop"
    if (-not (Test-Path $frontendSource)) {
        throw "Frontend source not found: $frontendSource"
    }

    $productionText = (Get-ChildItem $frontendSource -Filter *.cs -Recurse |
        Get-Content -Raw) -join "`n"

    if ($productionText -match '/mock(?:/|\b)') {
        throw "Production frontend contains a /mock endpoint."
    }
}

Write-Host "Frontend/backend route contract verification completed." -ForegroundColor Green
