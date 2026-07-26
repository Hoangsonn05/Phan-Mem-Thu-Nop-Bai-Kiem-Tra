param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'powershell-compat.ps1')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactRoot = Join-Path $projectRoot "artifacts\complete-exam-workflow\$stamp"
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$results = New-Object 'System.Collections.Generic.List[object]'

function Invoke-Gate {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Operation,
        [bool]$Required = $true
    )
    try {
        & $Operation
        $results.Add([pscustomobject]@{ Gate = $Name; Result = 'PASS'; Required = $Required; Detail = '' })
        Write-Host "PASS $Name" -ForegroundColor Green
    } catch {
        $results.Add([pscustomobject]@{
            Gate = $Name
            Result = if ($Required) { 'FAIL' } else { 'PENDING' }
            Required = $Required
            Detail = $_.Exception.Message
        })
        Write-Host "$(if ($Required) { 'FAIL' } else { 'PENDING' }) $Name $($_.Exception.Message)" -ForegroundColor $(if ($Required) { 'Red' } else { 'Yellow' })
    }
}

function Invoke-NativeGate {
    param([string]$Command, [string[]]$Arguments, [string]$Context)
    $result = Invoke-NativeCommandCaptured -Command $Command -Arguments $Arguments -FailureContext $Context
    if (-not [string]::IsNullOrWhiteSpace($result.OutputText)) {
        Write-Host $result.OutputText
    }
}

Push-Location $projectRoot
try {
    Invoke-Gate 'git diff check' {
        Invoke-NativeGate 'git' @('diff', '--check') 'working tree whitespace validation'
    }
    Invoke-Gate 'restore solution' {
        Invoke-NativeGate 'dotnet' @('restore', 'ExamTransfer.slnx') 'solution restore'
    }
    Invoke-Gate 'targeted complete-workflow tests' {
        Invoke-NativeGate 'dotnet' @(
            'test',
            'backend/tests/ExamTransfer.Infrastructure.Tests/ExamTransfer.Infrastructure.Tests.csproj',
            '-c', $Configuration,
            '--no-restore',
            '--filter',
            'FullyQualifiedName~QuizDocumentImportTests|FullyQualifiedName~QuizWorkflowTests|FullyQualifiedName~DbInitializerQuizTests|FullyQualifiedName~ExamPolicies_AreTypedNormalizedAndImmutableAfterSession|FullyQualifiedName~ExamDuration_IsEditableOnlyBeforePublishSessionOrAttempt_AndCloneRemainsEditable|FullyQualifiedName~CloneMultipleChoice_CopiesIndependentSourceAndEnqueuesCompleteOrderedCloudGraph|FullyQualifiedName~FinalCloudSourceCompatibilityTests'
        ) 'backend targeted tests'
        Invoke-NativeGate 'dotnet' @(
            'test',
            'frontend/tests/ExamTransfer.Desktop.Tests/ExamTransfer.Desktop.Tests.csproj',
            '-c', $Configuration,
            '--no-restore',
            '--filter',
            'FullyQualifiedName~StudentExamFlowCoordinatorTests|FullyQualifiedName~StudentTimelineViewModelTests|FullyQualifiedName~ExamManagementViewModelTests'
        ) 'frontend flow and ET-01 tests'
    }
    Invoke-Gate 'Release solution build' {
        Invoke-NativeGate 'dotnet' @('build', 'ExamTransfer.slnx', '-c', $Configuration, '--no-restore') 'solution build'
    }
    Invoke-Gate 'full backend tests' {
        Invoke-NativeGate 'dotnet' @('test', 'backend/ExamTransfer.sln', '-c', $Configuration, '--no-build', '--no-restore') 'backend regression'
    }
    Invoke-Gate 'full frontend tests' {
        Invoke-NativeGate 'dotnet' @(
            'test',
            'frontend/tests/ExamTransfer.Desktop.Tests/ExamTransfer.Desktop.Tests.csproj',
            '-c', $Configuration,
            '--no-build',
            '--no-restore'
        ) 'frontend regression'
    }
    Invoke-Gate 'frontend isolated verification' {
        & (Join-Path $projectRoot 'frontend\scripts\verify-frontend.ps1') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "verify-frontend exited with $LASTEXITCODE." }
    }
    Invoke-Gate 'Supabase source security scan' {
        & (Join-Path $projectRoot 'backend\scripts\verify-supabase-source.ps1')
        if ($LASTEXITCODE -ne 0) { throw "verify-supabase-source exited with $LASTEXITCODE." }
        $migration = Get-Content (Join-Path $projectRoot 'backend\supabase\migrations\20260725174327_complete_exam_workflow.sql') -Raw
        $finalMigration = Get-Content (Join-Path $projectRoot 'backend\supabase\migrations\20260726064745_final_remaining_quiz_source_cloud_version.sql') -Raw
        foreach ($requiredText in @(
            'revoke select on public.quiz_attempts from authenticated',
            'create or replace function public.get_public_quiz_attempt',
            'set search_path = ''''',
            'alter table public.quiz_import_sources force row level security'
        )) {
            if ($migration.IndexOf($requiredText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Missing security invariant: $requiredText"
            }
        }
        foreach ($requiredText in @(
            'add column if not exists cloud_version bigint not null default 0',
            'set schema_version = 18',
            'create or replace function public.get_examtransfer_cloud_capabilities'
        )) {
            if ($finalMigration.IndexOf($requiredText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Missing final workflow invariant: $requiredText"
            }
        }
        $diff = (Invoke-NativeCommandCaptured -Command 'git' -Arguments @('diff', '--') -FailureContext 'secret scan').OutputText
        if ($diff -match '(?i)(service_role_key|supabase_service_role_key|postgres(?:ql)?://[^\s]+:[^\s]+@|eyJ[A-Za-z0-9_-]{20,}\.)') {
            throw 'Potential credential material detected in the working diff.'
        }
    }

    $sqlAvailable = $false
    try {
        $docker = Invoke-NativeCommandCaptured -Command 'docker' -Arguments @('info', '--format', '{{.ServerVersion}}') -FailureContext 'Docker availability'
        Push-Location (Join-Path $projectRoot 'backend')
        try {
            $status = Invoke-NativeCommandCaptured -Command 'supabase' -Arguments @('status') -FailureContext 'local Supabase status'
        } finally {
            Pop-Location
        }
        $sqlAvailable = $docker.ExitCode -eq 0 -and $status.ExitCode -eq 0
    } catch {
        $sqlAvailable = $false
    }
    if ($sqlAvailable) {
        Invoke-Gate 'local migration up without reset' {
            Push-Location (Join-Path $projectRoot 'backend')
            try {
                Invoke-NativeGate 'supabase' @('migration', 'up', '--local') 'forward-only local migrations'
            } finally {
                Pop-Location
            }
        }
        Invoke-Gate 'local pgTAP' {
            Push-Location (Join-Path $projectRoot 'backend')
            try {
                Invoke-NativeGate 'supabase' @('test', 'db', '--local') 'local pgTAP suite'
            } finally {
                Pop-Location
            }
        }
    } else {
        $results.Add([pscustomobject]@{
            Gate = 'local migration and pgTAP'
            Result = 'SQL_RUNTIME_PENDING'
            Required = $false
            Detail = 'Docker or local Supabase database is unavailable.'
        })
        Write-Host 'SQL_RUNTIME_PENDING local migration and pgTAP' -ForegroundColor Yellow
    }
} finally {
    Pop-Location
}

$summaryPath = Join-Path $artifactRoot 'verification-summary.txt'
$summary = ($results | Format-Table -AutoSize | Out-String).TrimEnd()
Write-Utf8NoBomFile -Path $summaryPath -Content $summary
Write-Host "Summary: $summaryPath"

if ($results | Where-Object { $_.Required -and $_.Result -eq 'FAIL' }) {
    exit 1
}
exit 0
