[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'repair-supabase-production-student-role.ps1'
$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
$expectedProjectRef = 'uythsrpriegwwdwnbisi'
$expectedStudentId = '377641cf-7457-413b-bbcd-1e030e8d85f6'

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "Production student role repair guard is missing: $Description"
    }
}

function Invoke-ExpectedGuardFailure {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$ExpectedPattern,
        [Parameter(Mandatory)][string]$Description
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(
            & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass `
                -File $scriptPath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -eq 0) {
        throw "$Description unexpectedly succeeded."
    }
    $outputText = ($output | Out-String)
    if ($outputText -notmatch $ExpectedPattern) {
        throw "$Description failed for the wrong reason. output=$outputText"
    }
}

if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw 'Production student role repair script is missing.'
}

$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object { $_.Message }) -join '; '
    throw "Production student role repair script has parser errors: $details"
}

$source = Get-Content -LiteralPath $scriptPath -Raw
foreach ($required in @(
    @{ Pattern = [regex]::Escape($expectedProjectRef); Description = 'fixed Project Ref' },
    @{ Pattern = '516543f3-ca00-480e-87ca-683243ffdc0b'; Description = 'fixed organization ID' },
    @{ Pattern = [regex]::Escape($expectedStudentId); Description = 'fixed student UID' },
    @{ Pattern = '23174800117'; Description = 'fixed student code' },
    @{ Pattern = '\$AllowRemoteReadOnly'; Description = 'explicit read-only authorization' },
    @{ Pattern = '\$AllowProductionRoleFix'; Description = 'explicit write authorization' },
    @{ Pattern = '\$MaintenanceWindowConfirmed'; Description = 'maintenance-window gate' },
    @{ Pattern = '\$CloudDisabledConfirmed'; Description = 'Cloud-disabled gate' },
    @{ Pattern = '\$LocalServersStoppedConfirmed'; Description = 'Local Server stop gate' },
    @{ Pattern = 'BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE'; Description = 'readiness status gate' },
    @{ Pattern = 'verify-supabase-production-backup\.ps1'; Description = 'backup verifier' },
    @{ Pattern = 'DATABASE_AND_EDGE_FUNCTIONS_UPDATED'; Description = 'migration report gate' },
    @{ Pattern = 'supabase\\\.temp\\project-ref'; Description = 'linked checkout gate' },
    @{ Pattern = "schema_version is distinct from 23"; Description = 'schema 23 check' },
    @{ Pattern = '20260729002024'; Description = 'schema 23 migration-history check' },
    @{ Pattern = 'save_public_quiz_grade\(uuid,numeric,text,bigint,uuid\)'; Description = 'save-grade RPC check' },
    @{ Pattern = 'return_public_quiz_grade\(uuid,text,bigint,uuid\)'; Description = 'return-grade RPC check' },
    @{ Pattern = 'reopen_public_quiz_grade\(uuid,text,bigint,uuid\)'; Description = 'reopen-grade RPC check' },
    @{ Pattern = "set local lock_timeout = '5s'"; Description = 'bounded profile lock wait' },
    @{ Pattern = '(?s)from public\.profiles\s+where id = v_id\s+for update'; Description = 'single-profile row lock' },
    @{ Pattern = "(?s)update public\.profiles\s+set role = 'Student'.+where id = v_id\s+and role = 'Teacher'"; Description = 'compare-and-set role update' },
    @{ Pattern = '(?s)update public\.user_login_sessions.+where user_id = v_id\s+and revoked_at is null'; Description = 'active login-session revocation' },
    @{ Pattern = 'ROLE_FIX_POSTCONDITION_FAILED'; Description = 'profile postcondition' },
    @{ Pattern = 'SESSION_REVOCATION_POSTCONDITION_FAILED'; Description = 'session postcondition' },
    @{ Pattern = 'Invoke-NativeCommandCaptured'; Description = 'redacting native runner' },
    @{ Pattern = 'SensitiveValues @\(\$ConnectionString\)'; Description = 'connection-string redaction' },
    @{ Pattern = 'db\.\$expectedProjectRef\.supabase\.co'; Description = 'direct database host binding' },
    @{ Pattern = '\.pooler\.supabase\.com'; Description = 'pooler host binding' },
    @{ Pattern = '"postgres\.\$expectedProjectRef"'; Description = 'pooler user binding' }
)) {
    Assert-Contains `
        -Text $source `
        -Pattern $required.Pattern `
        -Description $required.Description
}

foreach ($forbiddenPattern in @(
    '(?is)\bupdate\s+auth\.users\b',
    '(?is)\binsert\s+into\s+auth\.users\b',
    '(?is)\bdelete\s+from\s+auth\.users\b',
    '(?is)\btruncate\s+(?:table\s+)?auth\.',
    '(?is)\bupdate\s+public\.profiles\b(?![\s\S]{0,220}\bwhere\s+id\s*=\s*v_id\b)',
    '(?is)\bupdate\s+public\.profiles\b[\s\S]{0,260}\borganization_id\s*=',
    '(?is)\bupdate\s+public\.profiles\b[\s\S]{0,260}\busername\s*=',
    '(?is)\bupdate\s+public\.profiles\b[\s\S]{0,260}\bstudent_code\s*=',
    '(?is)\bupdate\s+public\.profiles\b[\s\S]{0,260}\bdisplay_name\s*=',
    '(?is)\bupdate\s+public\.profiles\b[\s\S]{0,260}\bdate_of_birth\s*='
)) {
    if ($source -match $forbiddenPattern) {
        throw "Production student role repair contains forbidden SQL pattern: $forbiddenPattern"
    }
}

$inspectBlock = [regex]::Match(
    $source,
    '(?s)\$inspectSql\s*=\s*@''(?<sql>.*?)''@\s*\r?\n\s*\$applySql')
if (-not $inspectBlock.Success) {
    throw 'Could not isolate the read-only inspection SQL block.'
}
$inspectSql = $inspectBlock.Groups['sql'].Value
Assert-Contains `
    -Text $inspectSql `
    -Pattern 'set transaction read only' `
    -Description 'read-only SQL transaction mode'
if ($inspectSql -match '(?is)\b(?:insert|update|delete|truncate|alter|drop|create|grant|revoke)\s') {
    throw 'The read-only inspection SQL block contains a database mutation statement.'
}

$applyBlock = [regex]::Match(
    $source,
    '(?s)\$applySql\s*=\s*@''(?<sql>.*?)''@\s*\r?\n\s*if\s*\(\$AllowRemoteReadOnly\)')
if (-not $applyBlock.Success) {
    throw 'Could not isolate the production role repair SQL block.'
}
$applySql = $applyBlock.Groups['sql'].Value
if ([regex]::Matches(
    $applySql,
    '(?is)\bupdate\s+public\.profiles\b').Count -ne 1) {
    throw 'Production role repair must contain exactly one profiles update.'
}
if ([regex]::Matches(
    $applySql,
    '(?is)\bupdate\s+public\.user_login_sessions\b').Count -ne 1) {
    throw 'Production role repair must contain exactly one login-session update.'
}

# These probes stop before connection validation or native command discovery, so
# they are deterministic local tests and cannot contact Supabase.
Invoke-ExpectedGuardFailure `
    -Arguments @(
        '-ProjectRef', $expectedProjectRef,
        '-ConfirmProjectRef', $expectedProjectRef) `
    -ExpectedPattern 'No remote action is authorized' `
    -Description 'Invocation without an authorization switch'

Invoke-ExpectedGuardFailure `
    -Arguments @(
        '-ProjectRef', $expectedProjectRef,
        '-ConfirmProjectRef', $expectedProjectRef,
        '-AllowRemoteReadOnly',
        '-AllowProductionRoleFix') `
    -ExpectedPattern 'Choose exactly one operation' `
    -Description 'Invocation with mutually exclusive authorization switches'

Invoke-ExpectedGuardFailure `
    -Arguments @(
        '-ProjectRef', 'abcdefghijklmnopqrst',
        '-ConfirmProjectRef', 'abcdefghijklmnopqrst',
        '-AllowRemoteReadOnly') `
    -ExpectedPattern 'refuses every Supabase project' `
    -Description 'Read-only invocation for the wrong project'

Invoke-ExpectedGuardFailure `
    -Arguments @(
        '-ProjectRef', $expectedProjectRef,
        '-ConfirmProjectRef', $expectedProjectRef,
        '-AllowRemoteReadOnly',
        '-ConnectionString',
        "postgresql://postgres:dummy@evil.invalid/database?project=$expectedProjectRef") `
    -ExpectedPattern 'host/user does not identify' `
    -Description 'Read-only invocation with a foreign database host'

Invoke-ExpectedGuardFailure `
    -Arguments @(
        '-ProjectRef', $expectedProjectRef,
        '-ConfirmProjectRef', $expectedProjectRef,
        '-AllowProductionRoleFix',
        '-MaintenanceWindowConfirmed',
        '-CloudDisabledConfirmed',
        '-LocalServersStoppedConfirmed',
        '-Confirmation', 'WRONG CONFIRMATION') `
    -ExpectedPattern 'Confirmation mismatch' `
    -Description 'Write invocation without the exact typed confirmation'

Write-Host (
    'PASS code=PRODUCTION_STUDENT_ROLE_REPAIR_GUARD_OK ' +
    'detail=parser, exact-target SQL, write gates, and local fail-closed probes passed') `
    -ForegroundColor Green
