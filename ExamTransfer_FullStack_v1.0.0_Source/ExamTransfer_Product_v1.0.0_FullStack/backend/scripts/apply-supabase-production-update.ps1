[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,

    [Parameter(Mandatory)]
    [string]$ConfirmProjectRef,

    [Parameter(Mandatory)]
    [string]$BackupSetPath,

    [Parameter(Mandatory)]
    [string]$ReadinessReportPath,

    [Parameter(Mandatory)]
    [string]$Confirmation,

    [switch]$AllowProductionUpdate,
    [switch]$MaintenanceWindowConfirmed,
    [switch]$CloudDisabledConfirmed,
    [switch]$LocalServersStoppedConfirmed,

    [ValidateRange(1, 24)]
    [int]$MaximumReadinessAgeHours = 4,

    [string]$ReportDirectory,

    [string]$ConnectionString = $env:SUPABASE_DB_URL,

    [ValidateSet('native', 'https')]
    [string]$DnsResolver = 'https'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$requiredFunctions = @(
    'verify-public-submission-archive',
    'issue-public-device-command',
    'get-public-exam-file-url'
)

function Invoke-PowerShellFile {
    param([string]$File, [string[]]$Arguments = @())
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with code $LASTEXITCODE." }
}

if (-not $AllowProductionUpdate) {
    throw 'Production writes are disabled. Pass -AllowProductionUpdate only inside an approved maintenance window.'
}
if (-not $MaintenanceWindowConfirmed -or -not $CloudDisabledConfirmed -or -not $LocalServersStoppedConfirmed) {
    throw 'Confirm the maintenance window, Cloud disabled, and all production Local Servers stopped before any write.'
}
if ($ConfirmProjectRef -cne $ProjectRef) {
    throw 'Project Ref confirmation failed.'
}
if ($Confirmation -cne "UPDATE SUPABASE PRODUCTION $ProjectRef") {
    throw "Confirmation mismatch. Supply exactly: UPDATE SUPABASE PRODUCTION $ProjectRef"
}
if (-not (Get-Command supabase -ErrorAction SilentlyContinue)) {
    throw 'Supabase CLI is required.'
}
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'Set SUPABASE_DB_URL or pass -ConnectionString. The value is never printed or saved.'
}
if ([Uri]::UnescapeDataString($ConnectionString).IndexOf($ProjectRef, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'SUPABASE_DB_URL does not contain the confirmed Project Ref.'
}

$linkedRefPath = Join-Path $backendRoot 'supabase\.temp\project-ref'
if (-not (Test-Path -LiteralPath $linkedRefPath -PathType Leaf)) {
    throw 'This checkout is not linked to a Supabase project.'
}
$linkedRef = (Get-Content -LiteralPath $linkedRefPath -Raw).Trim()
if ($linkedRef -cne $ProjectRef) {
    throw "Linked Project Ref does not match. linked=$linkedRef requested=$ProjectRef"
}

$readinessPath = (Resolve-Path -LiteralPath $ReadinessReportPath).Path
$readiness = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json
if ($readiness.kind -cne 'ExamTransferProductionReadiness' -or
    $readiness.finalStatus -cne 'BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE') {
    throw 'Readiness report is not approved for a production update.'
}
if ($readiness.projectRef -cne $ProjectRef) {
    throw 'Readiness report Project Ref does not match.'
}
$readinessCreatedAt = [DateTimeOffset]::Parse([string]$readiness.createdAtUtc)
if (([DateTimeOffset]::UtcNow - $readinessCreatedAt).TotalHours -gt $MaximumReadinessAgeHours) {
    throw "Readiness report is older than $MaximumReadinessAgeHours hour(s). Rerun all gates."
}
if (@($readiness.gates | Where-Object Status -ne 'PASS').Count -gt 0) {
    throw 'Readiness report contains a gate that is not PASS.'
}

Invoke-PowerShellFile (Join-Path $PSScriptRoot 'verify-supabase-production-backup.ps1') @(
    '-ProjectRef', $ProjectRef,
    '-BackupSetPath', (Resolve-Path -LiteralPath $BackupSetPath).Path)

# Re-run the remote read-only preflight and migration dry-run immediately before
# the write. This closes the gap between the readiness report and deployment.
Invoke-PowerShellFile (Join-Path $PSScriptRoot 'test-public-cloud-production-preflight.ps1') @(
    '-ProjectRef', $ProjectRef,
    '-ConfirmProjectRef', $ConfirmProjectRef,
    '-AllowRemoteReadAndDryRun',
    '-NonInteractive',
    '-ConnectionString', $ConnectionString,
    '-DnsResolver', $DnsResolver)

$secretArguments = Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver `
    -Arguments @('secrets', 'list', '--project-ref', $ProjectRef)
$secretResult = Invoke-NativeCommandCaptured -Command 'supabase' `
    -Arguments $secretArguments -FailureContext "dnsResolver=$DnsResolver"
if ($secretResult.OutputText -notmatch '(?m)\bEXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET\b') {
    throw 'Required Edge Function secret EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET is missing.'
}

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $ReportDirectory = Join-Path $documents 'ExamTransfer-Private-Reports\Production-Updates'
}
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$reportPath = Join-Path $ReportDirectory ("production-update-{0}-{1}.log" -f $ProjectRef,(Get-Date -Format 'yyyyMMdd-HHmmss'))
$report = [Collections.Generic.List[string]]::new()
$report.Add("projectRef=$ProjectRef")
$report.Add("startedAtUtc=$([DateTimeOffset]::UtcNow.ToString('o'))")
$report.Add("backupSetPath=$((Resolve-Path -LiteralPath $BackupSetPath).Path)")
$report.Add("readinessReportPath=$readinessPath")

Push-Location $backendRoot
try {
    Write-Host 'Applying pending Supabase migrations...' -ForegroundColor Yellow
    $pushArguments = Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver `
        -Arguments @('db', 'push', '--db-url', $ConnectionString)
    $pushResult = Invoke-NativeCommandCaptured -Command 'supabase' `
        -Arguments $pushArguments -SensitiveValues @($ConnectionString) `
        -FailureContext "dnsResolver=$DnsResolver"
    $report.Add('[db-push]')
    $report.Add($pushResult.OutputText)

    Write-Host 'Linting the linked database...' -ForegroundColor Yellow
    $lintArguments = Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver `
        -Arguments @('db', 'lint', '--db-url', $ConnectionString, '--level', 'warning', '--fail-on', 'error')
    $lintResult = Invoke-NativeCommandCaptured -Command 'supabase' `
        -Arguments $lintArguments -SensitiveValues @($ConnectionString) `
        -FailureContext "dnsResolver=$DnsResolver"
    $report.Add('[db-lint]')
    $report.Add($lintResult.OutputText)

    foreach ($functionName in $requiredFunctions) {
        Write-Host "Deploying Edge Function: $functionName" -ForegroundColor Yellow
        $functionArguments = Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver `
            -Arguments @('functions', 'deploy', $functionName, '--project-ref', $ProjectRef)
        $functionResult = Invoke-NativeCommandCaptured -Command 'supabase' `
            -Arguments $functionArguments -FailureContext "dnsResolver=$DnsResolver"
        $report.Add("[function-$functionName]")
        $report.Add($functionResult.OutputText)
    }

    $report.Add("completedAtUtc=$([DateTimeOffset]::UtcNow.ToString('o'))")
    $report.Add('result=DATABASE_AND_EDGE_FUNCTIONS_UPDATED')
    [IO.File]::WriteAllLines($reportPath, $report, (New-Object Text.UTF8Encoding($false)))

    Write-Host 'DATABASE_AND_EDGE_FUNCTIONS_UPDATED' -ForegroundColor Green
    Write-Host "Report: $reportPath" -ForegroundColor Cyan
    Write-Warning 'Keep Cloud disabled until Realtime Private Channels Only and every live acceptance test pass.'
} catch {
    $report.Add("failedAtUtc=$([DateTimeOffset]::UtcNow.ToString('o'))")
    $report.Add("result=UPDATE_INCOMPLETE")
    $report.Add("reason=$($_.Exception.Message)")
    [IO.File]::WriteAllLines($reportPath, $report, (New-Object Text.UTF8Encoding($false)))
    Write-Error "UPDATE_INCOMPLETE report=$reportPath reason=$($_.Exception.Message)"
    throw
} finally {
    Pop-Location
}
