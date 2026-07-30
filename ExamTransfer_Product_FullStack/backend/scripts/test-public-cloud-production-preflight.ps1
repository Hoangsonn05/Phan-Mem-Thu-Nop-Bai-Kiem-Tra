[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectRef,
    [string]$ConnectionString = $env:SUPABASE_DB_URL,
    [string]$ConfirmProjectRef,
    [string]$ReportDirectory,
    [switch]$AllowRemoteReadAndDryRun,
    [switch]$NonInteractive,
    [ValidateSet('native', 'https')]
    [string]$DnsResolver = 'https'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$traceId = [Guid]::NewGuid().ToString('N')
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sqlPath = Join-Path $backendRoot 'supabase\preflight\public_cloud_production_legacy_preflight.sql'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

if (-not $AllowRemoteReadAndDryRun) {
    throw 'Remote preflight is disabled by default. Rerun with -AllowRemoteReadAndDryRun only after the user authorizes read-only checks and db push --dry-run.'
}
if (-not (Get-Command supabase -ErrorAction SilentlyContinue)) { throw 'Supabase CLI is required.' }
if ([string]::IsNullOrWhiteSpace($ConnectionString)) { throw 'Set SUPABASE_DB_URL or pass -ConnectionString. The value is never printed or saved.' }
if ($ProjectRef -notmatch '^[a-z0-9]{20}$') { throw 'ProjectRef format is invalid.' }
if ([Uri]::UnescapeDataString($ConnectionString).IndexOf($ProjectRef, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'SUPABASE_DB_URL does not contain the confirmed Project Ref.'
}
if (-not (Get-Command psql -ErrorAction SilentlyContinue) -and
    -not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Install PostgreSQL psql or run Docker; one is required for read-only preflight SQL.'
}

$linkedRefPath = Join-Path $backendRoot 'supabase\.temp\project-ref'
if (-not (Test-Path -LiteralPath $linkedRefPath)) { throw 'This checkout is not linked to a Supabase project.' }
$linkedRef = (Get-Content -LiteralPath $linkedRefPath -Raw).Trim()
if ($linkedRef -ne $ProjectRef) { throw "Linked project ref does not match the requested ProjectRef. linked=$linkedRef requested=$ProjectRef" }

if ([string]::IsNullOrWhiteSpace($ConfirmProjectRef) -and -not $NonInteractive) {
    $ConfirmProjectRef = Read-Host "Type the Project Ref '$ProjectRef' to confirm read-only preflight and dry-run"
}
if ($ConfirmProjectRef -ne $ProjectRef) { throw 'Project Ref confirmation failed.' }

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $ReportDirectory = Join-Path $documents 'ExamTransfer-Private-Reports\Supabase-Preflight'
}
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$reportPath = Join-Path $ReportDirectory ("preflight-{0}-{1}.log" -f $ProjectRef,(Get-Date -Format 'yyyyMMdd-HHmmss'))

$report = [Collections.Generic.List[string]]::new()
$report.Add("traceId=$traceId")
$report.Add("projectRef=$ProjectRef")
$report.Add("startedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))")

Push-Location $backendRoot
try {
    $migrationArguments = Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver `
        -Arguments @('migration', 'list', '--db-url', $ConnectionString)
    $migrationResult = Invoke-NativeCommandCaptured -Command 'supabase' `
        -Arguments $migrationArguments -SensitiveValues @($ConnectionString) `
        -FailureContext "dnsResolver=$DnsResolver"
    $report.Add('[migration-list]')
    $report.Add($migrationResult.OutputText)

    if (Get-Command psql -ErrorAction SilentlyContinue) {
        $preflightResult = Invoke-NativeCommandCaptured -Command 'psql' `
            -Arguments @($ConnectionString, '-X', '-v', 'ON_ERROR_STOP=1', '-f', $sqlPath) `
            -SensitiveValues @($ConnectionString)
    } else {
        Invoke-NativeCommandCaptured -Command 'docker' -Arguments @('info') | Out-Null
        $sqlDirectory = (Resolve-Path (Split-Path $sqlPath -Parent)).Path
        $sqlFileName = Split-Path $sqlPath -Leaf
        $mount = "type=bind,source=$sqlDirectory,target=/work,readonly"
        $preflightResult = Invoke-NativeCommandCaptured -Command 'docker' `
            -Arguments @('run', '--rm', '--mount', $mount, 'postgres:17-alpine',
                'psql', $ConnectionString, '-X', '-v', 'ON_ERROR_STOP=1', '-f', "/work/$sqlFileName") `
            -SensitiveValues @($ConnectionString)
    }
    $safePreflight = $preflightResult.OutputText
    $report.Add('[legacy-preflight]')
    $report.Add($safePreflight)

    $dryRunArguments = Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver `
        -Arguments @('db', 'push', '--db-url', $ConnectionString, '--dry-run')
    $dryRunResult = Invoke-NativeCommandCaptured -Command 'supabase' `
        -Arguments $dryRunArguments -SensitiveValues @($ConnectionString) `
        -FailureContext "dnsResolver=$DnsResolver"
    $report.Add('[db-push-dry-run]')
    $report.Add($dryRunResult.OutputText)

    $passCount = ([regex]::Matches($safePreflight, 'PASS\|')).Count
    $warningCount = ([regex]::Matches($safePreflight, 'WARNING\|')).Count
    $blockerCount = ([regex]::Matches($safePreflight, 'BLOCKER\|')).Count
    $report.Add("summary=PASS:$passCount WARNING:$warningCount BLOCKER:$blockerCount")
    [IO.File]::WriteAllLines($reportPath, $report, (New-Object Text.UTF8Encoding($false)))

    Write-Host "Project Ref: $ProjectRef" -ForegroundColor Cyan
    Write-Host "PASS=$passCount WARNING=$warningCount BLOCKER=$blockerCount" -ForegroundColor Cyan
    Write-Host "Report: $reportPath" -ForegroundColor Cyan
    if ($blockerCount -gt 0) {
        Write-Error "BLOCKER found. Do not apply migrations. traceId=$traceId"
        exit 2
    }
    Write-Host "PASS code=PUBLIC_CLOUD_PRODUCTION_PREFLIGHT_OK traceId=$traceId dnsResolver=$DnsResolver detail=read-only SQL and db push --dry-run completed; no database push performed" -ForegroundColor Green
} finally {
    Pop-Location
}
