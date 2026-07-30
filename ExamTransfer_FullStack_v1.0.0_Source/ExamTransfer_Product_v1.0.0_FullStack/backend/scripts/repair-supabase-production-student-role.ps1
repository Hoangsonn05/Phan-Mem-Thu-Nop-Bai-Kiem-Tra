[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,

    [Parameter(Mandatory)]
    [string]$ConfirmProjectRef,

    [string]$BackupSetPath,
    [string]$ReadinessReportPath,
    [string]$ProductionUpdateReportPath,
    [string]$Confirmation,

    [switch]$AllowRemoteReadOnly,
    [switch]$AllowProductionRoleFix,
    [switch]$MaintenanceWindowConfirmed,
    [switch]$CloudDisabledConfirmed,
    [switch]$LocalServersStoppedConfirmed,

    [ValidateRange(1, 168)]
    [int]$MaximumBackupAgeHours = 24,

    [ValidateRange(1, 24)]
    [int]$MaximumReadinessAgeHours = 4,

    [ValidateRange(1, 24)]
    [int]$MaximumUpdateReportAgeHours = 4,

    [string]$ReportDirectory,
    [string]$ConnectionString = $env:SUPABASE_DB_URL
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

# This is deliberately a one-profile repair, not a general account-editing tool.
$expectedProjectRef = 'uythsrpriegwwdwnbisi'
$expectedOrganizationId = '516543f3-ca00-480e-87ca-683243ffdc0b'
$expectedStudentId = '377641cf-7457-413b-bbcd-1e030e8d85f6'
$expectedStudentCode = '23174800117'
$requiredSchemaVersion = 23
$requiredMigrationVersion = '20260729002024'
$requiredConfirmation = "FIX SUPABASE STUDENT ROLE $expectedProjectRef $expectedStudentId"
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Invoke-PowerShellFile {
    param(
        [Parameter(Mandatory)][string]$File,
        [string[]]$Arguments = @()
    )

    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File exited with code $LASTEXITCODE."
    }
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description is required for a production role repair."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description does not exist."
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description is required for a production role repair."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description does not exist."
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-SamePath {
    param(
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Description
    )

    $trim = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $expectedFull = [IO.Path]::GetFullPath($Expected).TrimEnd($trim)
    $actualFull = [IO.Path]::GetFullPath($Actual).TrimEnd($trim)
    if (-not $expectedFull.Equals($actualFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description does not match the approved production-update evidence."
    }
}

function Get-RequiredReportValue {
    param(
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][string]$Name
    )

    $matches = @($Lines | Where-Object { $_ -match "^$([regex]::Escape($Name))=(.*)$" })
    if ($matches.Count -ne 1) {
        throw "Production update report must contain exactly one $Name entry."
    }
    return ($matches[0] -replace "^$([regex]::Escape($Name))=", '')
}

function Assert-RecentTimestamp {
    param(
        [Parameter(Mandatory)][DateTimeOffset]$Timestamp,
        [Parameter(Mandatory)][int]$MaximumAgeHours,
        [Parameter(Mandatory)][string]$Description
    )

    $age = [DateTimeOffset]::UtcNow - $Timestamp
    if ($age.TotalMinutes -lt -5) {
        throw "$Description has a timestamp too far in the future."
    }
    if ($age.TotalHours -gt $MaximumAgeHours) {
        throw "$Description is older than $MaximumAgeHours hour(s)."
    }
}

function Invoke-RoleRepairSql {
    param([Parameter(Mandatory)][string]$Sql)

    $temporaryRoot = Join-Path (
        [IO.Path]::GetTempPath()
    ) "examtransfer-production-role-fix-$([Guid]::NewGuid().ToString('N'))"
    $sqlPath = Join-Path $temporaryRoot 'guarded-role-fix.sql'
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    [IO.File]::WriteAllText($sqlPath, $Sql, (New-Utf8NoBomEncoding))

    try {
        if (Get-Command psql -ErrorAction SilentlyContinue) {
            return Invoke-NativeCommandCaptured -Command 'psql' `
                -Arguments @(
                    $ConnectionString,
                    '-X',
                    '-v', 'ON_ERROR_STOP=1',
                    '-tA',
                    '-f', $sqlPath) `
                -SensitiveValues @($ConnectionString) `
                -FailureContext "projectRef=$ProjectRef"
        }

        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            throw 'PostgreSQL psql or a running Docker Engine is required.'
        }
        Invoke-NativeCommandCaptured -Command 'docker' -Arguments @('info') | Out-Null
        $mount = "type=bind,source=$temporaryRoot,target=/work,readonly"
        return Invoke-NativeCommandCaptured -Command 'docker' `
            -Arguments @(
                'run', '--rm',
                '--mount', $mount,
                'postgres:17-alpine',
                'psql', $ConnectionString,
                '-X',
                '-v', 'ON_ERROR_STOP=1',
                '-tA',
                '-f', '/work/guarded-role-fix.sql') `
            -SensitiveValues @($ConnectionString) `
            -FailureContext "projectRef=$ProjectRef"
    } finally {
        $resolvedTemporaryRoot = Resolve-Path -LiteralPath $temporaryRoot -ErrorAction SilentlyContinue
        if ($resolvedTemporaryRoot) {
            $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
            $resolvedPath = [IO.Path]::GetFullPath($resolvedTemporaryRoot.Path)
            if ($resolvedPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
                (Split-Path $resolvedPath -Leaf).StartsWith(
                    'examtransfer-production-role-fix-',
                    [StringComparison]::Ordinal)) {
                Remove-Item -LiteralPath $resolvedPath -Recurse -Force
            }
        }
    }
}

if ($AllowRemoteReadOnly -and $AllowProductionRoleFix) {
    throw 'Choose exactly one operation: remote read-only inspection or production role repair.'
}
if (-not $AllowRemoteReadOnly -and -not $AllowProductionRoleFix) {
    throw 'No remote action is authorized. Pass -AllowRemoteReadOnly for inspection or the guarded production-write switches for repair.'
}
if ($ProjectRef -cne $expectedProjectRef) {
    throw 'This one-profile repair refuses every Supabase project except its compiled production target.'
}
if ($ConfirmProjectRef -cne $expectedProjectRef) {
    throw 'Project Ref confirmation failed.'
}

if ($AllowProductionRoleFix) {
    if (-not $MaintenanceWindowConfirmed -or
        -not $CloudDisabledConfirmed -or
        -not $LocalServersStoppedConfirmed) {
        throw 'Confirm the maintenance window, Cloud disabled, and every production Local Server stopped before any write.'
    }
    if ($Confirmation -cne $requiredConfirmation) {
        throw "Confirmation mismatch. Supply exactly: $requiredConfirmation"
    }
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'Set SUPABASE_DB_URL or pass -ConnectionString. The value is never printed or saved.'
}
if ($ConnectionString -match '[\r\n]') {
    throw 'SUPABASE_DB_URL contains an invalid newline.'
}
if ([Uri]::UnescapeDataString($ConnectionString).IndexOf(
    $expectedProjectRef,
    [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'SUPABASE_DB_URL does not contain the fixed production Project Ref.'
}
try {
    $databaseUri = New-Object Uri($ConnectionString)
} catch {
    throw 'SUPABASE_DB_URL must be an absolute PostgreSQL URL.'
}
if (-not $databaseUri.IsAbsoluteUri -or
    @('postgres','postgresql') -notcontains $databaseUri.Scheme.ToLowerInvariant()) {
    throw 'SUPABASE_DB_URL must use the postgres or postgresql scheme.'
}
$directDatabaseHost = "db.$expectedProjectRef.supabase.co"
$databaseHost = $databaseUri.DnsSafeHost
$databaseUser = [Uri]::UnescapeDataString(
    (($databaseUri.UserInfo -split ':', 2)[0]))
$isDirectTarget = $databaseHost.Equals(
    $directDatabaseHost,
    [StringComparison]::OrdinalIgnoreCase)
$isPoolerTarget = $databaseHost.EndsWith(
    '.pooler.supabase.com',
    [StringComparison]::OrdinalIgnoreCase) -and
    $databaseUser.Equals(
        "postgres.$expectedProjectRef",
        [StringComparison]::Ordinal)
if (-not $isDirectTarget -and -not $isPoolerTarget) {
    throw 'SUPABASE_DB_URL host/user does not identify the fixed production project.'
}

$linkedRefPath = Join-Path $backendRoot 'supabase\.temp\project-ref'
if (-not (Test-Path -LiteralPath $linkedRefPath -PathType Leaf)) {
    throw 'This checkout is not linked to a Supabase project.'
}
$linkedRef = (Get-Content -LiteralPath $linkedRefPath -Raw).Trim()
if ($linkedRef -cne $expectedProjectRef) {
    throw 'The linked Supabase project is not the fixed production target.'
}

$inspectSql = @'
begin;
set transaction read only;
set local lock_timeout = '5s';
set local statement_timeout = '30s';

do $role_inspection$
declare
  v_id constant uuid := '377641cf-7457-413b-bbcd-1e030e8d85f6';
  v_org constant uuid := '516543f3-ca00-480e-87ca-683243ffdc0b';
  v_code constant text := '23174800117';
  v_schema_version integer;
  v_profile public.profiles%rowtype;
begin
  select schema_version
    into v_schema_version
  from public.examtransfer_cloud_meta
  where id = 1;

  if v_schema_version is distinct from 23 then
    raise exception 'SCHEMA_VERSION_23_REQUIRED';
  end if;
  if not exists (
    select 1
    from supabase_migrations.schema_migrations
    where version::text = '20260729002024'
  ) then
    raise exception 'SCHEMA_23_MIGRATION_HISTORY_MISSING';
  end if;
  if to_regprocedure(
       'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)'
     ) is null
     or to_regprocedure(
       'public.return_public_quiz_grade(uuid,text,bigint,uuid)'
     ) is null
     or to_regprocedure(
       'public.reopen_public_quiz_grade(uuid,text,bigint,uuid)'
     ) is null then
    raise exception 'SCHEMA_23_GRADING_RPC_MISSING';
  end if;

  select *
    into v_profile
  from public.profiles
  where id = v_id;

  if not found then
    raise exception 'PROFILE_NOT_FOUND';
  end if;
  if v_profile.organization_id is distinct from v_org then
    raise exception 'ORGANIZATION_MISMATCH';
  end if;
  if v_profile.role is distinct from 'Teacher' then
    raise exception 'EXPECTED_TEACHER_ROLE_MISSING';
  end if;
  if btrim(coalesce(v_profile.username, '')) <> v_code
     or btrim(coalesce(v_profile.student_code, '')) <> v_code
     or btrim(coalesce(v_profile.display_name, '')) = ''
     or v_profile.date_of_birth is null
     or v_profile.is_active is distinct from true then
    raise exception 'STUDENT_REQUIRED_FIELDS_INVALID';
  end if;
end
$role_inspection$;

select 'ROLE_FIX_INSPECTION_OK';
commit;
'@

$applySql = @'
begin;
set local lock_timeout = '5s';
set local statement_timeout = '30s';

do $role_fix$
declare
  v_id constant uuid := '377641cf-7457-413b-bbcd-1e030e8d85f6';
  v_org constant uuid := '516543f3-ca00-480e-87ca-683243ffdc0b';
  v_code constant text := '23174800117';
  v_schema_version integer;
  v_profile public.profiles%rowtype;
  v_profile_count integer;
begin
  select schema_version
    into v_schema_version
  from public.examtransfer_cloud_meta
  where id = 1;

  if v_schema_version is distinct from 23 then
    raise exception 'SCHEMA_VERSION_23_REQUIRED';
  end if;
  if not exists (
    select 1
    from supabase_migrations.schema_migrations
    where version::text = '20260729002024'
  ) then
    raise exception 'SCHEMA_23_MIGRATION_HISTORY_MISSING';
  end if;
  if to_regprocedure(
       'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)'
     ) is null
     or to_regprocedure(
       'public.return_public_quiz_grade(uuid,text,bigint,uuid)'
     ) is null
     or to_regprocedure(
       'public.reopen_public_quiz_grade(uuid,text,bigint,uuid)'
     ) is null then
    raise exception 'SCHEMA_23_GRADING_RPC_MISSING';
  end if;

  select *
    into v_profile
  from public.profiles
  where id = v_id
  for update;

  if not found then
    raise exception 'PROFILE_NOT_FOUND';
  end if;
  if v_profile.organization_id is distinct from v_org then
    raise exception 'ORGANIZATION_MISMATCH';
  end if;
  if v_profile.role is distinct from 'Teacher' then
    raise exception 'EXPECTED_TEACHER_ROLE_MISSING';
  end if;
  if btrim(coalesce(v_profile.username, '')) <> v_code
     or btrim(coalesce(v_profile.student_code, '')) <> v_code
     or btrim(coalesce(v_profile.display_name, '')) = ''
     or v_profile.date_of_birth is null
     or v_profile.is_active is distinct from true then
    raise exception 'STUDENT_REQUIRED_FIELDS_INVALID';
  end if;

  update public.profiles
  set role = 'Student',
      updated_at = now()
  where id = v_id
    and role = 'Teacher';
  get diagnostics v_profile_count = row_count;
  if v_profile_count <> 1 then
    raise exception 'ROLE_UPDATE_LOST_RACE';
  end if;

  update public.user_login_sessions
  set revoked_at = coalesce(revoked_at, now()),
      revoke_reason = coalesce(
        revoke_reason,
        'role_changed_teacher_to_student')
  where user_id = v_id
    and revoked_at is null;

  if not exists (
    select 1
    from public.profiles
    where id = v_id
      and organization_id = v_org
      and role = 'Student'
      and btrim(coalesce(username, '')) = v_code
      and btrim(coalesce(student_code, '')) = v_code
      and btrim(coalesce(display_name, '')) <> ''
      and date_of_birth is not null
      and is_active is true
  ) then
    raise exception 'ROLE_FIX_POSTCONDITION_FAILED';
  end if;
  if exists (
    select 1
    from public.user_login_sessions
    where user_id = v_id
      and revoked_at is null
  ) then
    raise exception 'SESSION_REVOCATION_POSTCONDITION_FAILED';
  end if;
end
$role_fix$;

select 'ROLE_FIX_APPLIED';
commit;
'@

if ($AllowRemoteReadOnly) {
    $inspectionResult = Invoke-RoleRepairSql -Sql $inspectSql
    if ($inspectionResult.OutputText -notmatch '(?m)^ROLE_FIX_INSPECTION_OK$') {
        throw 'The read-only inspection did not return its expected success marker.'
    }
    Write-Host (
        "PASS code=PRODUCTION_STUDENT_ROLE_INSPECTION_OK projectRef=$ProjectRef " +
        "profileId=$expectedStudentId detail=read-only; no row changed") -ForegroundColor Green
    exit 0
}

$resolvedBackupSet = Resolve-RequiredDirectory `
    -Path $BackupSetPath `
    -Description 'BackupSetPath'
$resolvedReadiness = Resolve-RequiredFile `
    -Path $ReadinessReportPath `
    -Description 'ReadinessReportPath'
$resolvedUpdateReport = Resolve-RequiredFile `
    -Path $ProductionUpdateReportPath `
    -Description 'ProductionUpdateReportPath'

$readiness = Get-Content -LiteralPath $resolvedReadiness -Raw | ConvertFrom-Json
if ($readiness.kind -cne 'ExamTransferProductionReadiness' -or
    $readiness.finalStatus -cne 'BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE') {
    throw 'Readiness report is not approved for a production update.'
}
if ($readiness.projectRef -cne $expectedProjectRef) {
    throw 'Readiness report Project Ref does not match the fixed production target.'
}
if ($readiness.mandatorySkipped -eq $true -or
    @($readiness.gates | Where-Object { $_.Status -cne 'PASS' }).Count -gt 0) {
    throw 'Readiness report contains a skipped or non-PASS mandatory gate.'
}
$readinessCreatedAt = [DateTimeOffset]::Parse([string]$readiness.createdAtUtc)
Assert-RecentTimestamp `
    -Timestamp $readinessCreatedAt `
    -MaximumAgeHours $MaximumReadinessAgeHours `
    -Description 'Readiness report'

Invoke-PowerShellFile `
    -File (Join-Path $PSScriptRoot 'verify-supabase-production-backup.ps1') `
    -Arguments @(
        '-ProjectRef', $expectedProjectRef,
        '-BackupSetPath', $resolvedBackupSet,
        '-MaximumAgeHours', [string]$MaximumBackupAgeHours)

$updateReportLines = @(Get-Content -LiteralPath $resolvedUpdateReport)
$updateProjectRef = Get-RequiredReportValue -Lines $updateReportLines -Name 'projectRef'
$updateResult = Get-RequiredReportValue -Lines $updateReportLines -Name 'result'
$updateBackupSet = Get-RequiredReportValue -Lines $updateReportLines -Name 'backupSetPath'
$updateReadiness = Get-RequiredReportValue -Lines $updateReportLines -Name 'readinessReportPath'
$updateCompletedAtText = Get-RequiredReportValue -Lines $updateReportLines -Name 'completedAtUtc'
if ($updateProjectRef -cne $expectedProjectRef -or
    $updateResult -cne 'DATABASE_AND_EDGE_FUNCTIONS_UPDATED') {
    throw 'Production update report does not prove a successful update of the fixed target.'
}
Assert-SamePath `
    -Expected $resolvedBackupSet `
    -Actual $updateBackupSet `
    -Description 'Backup path'
Assert-SamePath `
    -Expected $resolvedReadiness `
    -Actual $updateReadiness `
    -Description 'Readiness path'
$updateCompletedAt = [DateTimeOffset]::Parse($updateCompletedAtText)
Assert-RecentTimestamp `
    -Timestamp $updateCompletedAt `
    -MaximumAgeHours $MaximumUpdateReportAgeHours `
    -Description 'Production update report'
if ($updateCompletedAt -lt $readinessCreatedAt) {
    throw 'Production update report predates its readiness report.'
}

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $ReportDirectory = Join-Path $documents 'ExamTransfer-Private-Reports\Production-Role-Fixes'
}
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$traceId = [Guid]::NewGuid().ToString('N')
$reportPath = Join-Path $ReportDirectory (
    "student-role-fix-{0}-{1}.log" -f $expectedProjectRef,(Get-Date -Format 'yyyyMMdd-HHmmss'))
$report = [Collections.Generic.List[string]]::new()
$report.Add('kind=ExamTransferProductionStudentRoleRepair')
$report.Add("traceId=$traceId")
$report.Add("projectRef=$expectedProjectRef")
$report.Add("profileId=$expectedStudentId")
$report.Add("organizationId=$expectedOrganizationId")
$report.Add("studentCode=$expectedStudentCode")
$report.Add("requiredSchemaVersion=$requiredSchemaVersion")
$report.Add("requiredMigrationVersion=$requiredMigrationVersion")
$report.Add("startedAtUtc=$([DateTimeOffset]::UtcNow.ToString('o'))")
$report.Add("backupSetPath=$resolvedBackupSet")
$report.Add("readinessReportPath=$resolvedReadiness")
$report.Add("productionUpdateReportPath=$resolvedUpdateReport")

try {
    $applyResult = Invoke-RoleRepairSql -Sql $applySql
    if ($applyResult.OutputText -notmatch '(?m)^ROLE_FIX_APPLIED$') {
        throw 'The transaction did not return its expected success marker.'
    }

    $report.Add("completedAtUtc=$([DateTimeOffset]::UtcNow.ToString('o'))")
    $report.Add('result=PROFILE_ROLE_CHANGED_TEACHER_TO_STUDENT_AND_SESSIONS_REVOKED')
    Write-Utf8NoBomFile -Path $reportPath -Content (
        ($report -join [Environment]::NewLine) + [Environment]::NewLine)

    Write-Host 'PROFILE_ROLE_CHANGED_TEACHER_TO_STUDENT_AND_SESSIONS_REVOKED' -ForegroundColor Green
    Write-Host "Report: $reportPath" -ForegroundColor Cyan
    Write-Warning (
        'Review Supabase Auth app metadata through the supported Auth Admin API or Dashboard. ' +
        'The protected Auth database tables were intentionally left untouched.')
} catch {
    $safeReason = Protect-NativeCommandOutput `
        -Text $_.Exception.Message `
        -SensitiveValues @($ConnectionString)
    $report.Add("failedAtUtc=$([DateTimeOffset]::UtcNow.ToString('o'))")
    $report.Add('result=ROLE_FIX_ROLLED_BACK_OR_NOT_STARTED')
    $report.Add("reason=$safeReason")
    Write-Utf8NoBomFile -Path $reportPath -Content (
        ($report -join [Environment]::NewLine) + [Environment]::NewLine)
    Write-Error "ROLE_FIX_FAILED report=$reportPath reason=$safeReason"
    throw
}
