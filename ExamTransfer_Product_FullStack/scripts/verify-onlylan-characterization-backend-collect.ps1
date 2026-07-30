[CmdletBinding()]
param(
    [switch]$RunPublishedE2E,
    [ValidateRange(1, 10)]
    [int]$Repeat = 3,
    [string]$ServerDirectory,
    [switch]$RunFullSolutionBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $projectRoot 'ExamTransfer.slnx'
$testProject = Join-Path $projectRoot 'backend\tests\ExamTransfer.Infrastructure.Tests\ExamTransfer.Infrastructure.Tests.csproj'
$dbMigratorProject = Join-Path $projectRoot 'backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj'
$localServerProject = Join-Path $projectRoot 'backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj'
$testClientProject = Join-Path $projectRoot 'backend\tests\ExamTransfer.OnlyLan.TestClient\ExamTransfer.OnlyLan.TestClient.csproj'
$e2eScript = Join-Path $PSScriptRoot 'test-published-onlylan-e2e.ps1'

$backendProjects = @(
    $testProject,
    $dbMigratorProject,
    $localServerProject,
    $testClientProject
)

$required = @($backendProjects + $e2eScript)
if ($RunFullSolutionBuild) {
    $required += $solution
}
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required verification input was not found: $path"
    }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found on PATH.'
}

$failures = New-Object System.Collections.Generic.List[string]

function Invoke-DotNetFatal {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Invoke-DotNetGate {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$PassCode,
        [Parameter(Mandatory)][string]$FailCode,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "BEGIN gate=$Name" -ForegroundColor Cyan
    & dotnet @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-Host "PASS code=$PassCode" -ForegroundColor Green
        return $true
    }

    $script:failures.Add("$FailCode(exit=$exitCode)")
    Write-Host "FAIL code=$FailCode exit=$exitCode" -ForegroundColor Red
    return $false
}

Push-Location $projectRoot
try {
    foreach ($project in $backendProjects) {
        Invoke-DotNetFatal `
            -Arguments @('restore', $project) `
            -FailureMessage "Restore failed: $project"
    }

    foreach ($project in $backendProjects) {
        Invoke-DotNetFatal `
            -Arguments @('build', $project, '-c', 'Release', '--no-restore') `
            -FailureMessage "Release backend build failed: $project"
    }
    Write-Host 'PASS code=ONLYLAN_BACKEND_CHARACTERIZATION_BUILD' -ForegroundColor Green

    $characterizationFilter = 'FullyQualifiedName~OnlyLanWorkflowCharacterizationTests|FullyQualifiedName~OnlyLanCharacterizationHarnessContractTests'
    [void](Invoke-DotNetGate `
        -Name 'OnlyLAN targeted characterization' `
        -PassCode 'ONLYLAN_CHARACTERIZATION_TARGETED' `
        -FailCode 'ONLYLAN_CHARACTERIZATION_TARGETED_FAILED' `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build', '--filter', $characterizationFilter))

    $authFilter = 'FullyQualifiedName~AccountAuthFlowTests|FullyQualifiedName~SupabaseIdentityLoginTests|FullyQualifiedName~StudentParticipantScopeTests'
    [void](Invoke-DotNetGate `
        -Name 'Login and participant authorization freeze' `
        -PassCode 'ONLYLAN_LOGIN_AUTH_FREEZE' `
        -FailCode 'ONLYLAN_LOGIN_AUTH_FREEZE_FAILED' `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build', '--filter', $authFilter))

    $publicCloudFilter = 'FullyQualifiedName~FinalCloudSourceCompatibilityTests|FullyQualifiedName~PublicCloudTeacherMutationTests|FullyQualifiedName~PublicCloudTeacherMutationRoutingTests|FullyQualifiedName~PublicCloudPullProjectionTests|FullyQualifiedName~PublicCloudOutboxLoopPreventionTests|FullyQualifiedName~PublicCloudCursorTransactionTests|FullyQualifiedName~PublicCloudMigrationSafetyTests|FullyQualifiedName~CloudOwnershipTests'
    [void](Invoke-DotNetGate `
        -Name 'PublicCloud authority regression' `
        -PassCode 'ONLYLAN_PUBLICCLOUD_FREEZE' `
        -FailCode 'ONLYLAN_PUBLICCLOUD_FREEZE_FAILED' `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build', '--filter', $publicCloudFilter))

    [void](Invoke-DotNetGate `
        -Name 'Full Infrastructure regression' `
        -PassCode 'ONLYLAN_INFRASTRUCTURE_REGRESSION' `
        -FailCode 'ONLYLAN_INFRASTRUCTURE_REGRESSION_FAILED' `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build'))

    if ($RunFullSolutionBuild) {
        Invoke-DotNetFatal `
            -Arguments @('restore', $solution) `
            -FailureMessage 'Full solution restore failed.'

        [void](Invoke-DotNetGate `
            -Name 'Full solution Release build' `
            -PassCode 'ONLYLAN_FULL_SOLUTION_BUILD' `
            -FailCode 'ONLYLAN_FULL_SOLUTION_BUILD_FAILED' `
            -Arguments @('build', $solution, '-c', 'Release', '--no-restore'))
    }
    else {
        Write-Host 'SKIP code=ONLYLAN_FULL_SOLUTION_BUILD reason=not_requested' -ForegroundColor Yellow
    }

    if ($RunPublishedE2E) {
        Write-Host 'BEGIN gate=Published OnlyLAN E2E' -ForegroundColor Cyan
        $arguments = @('-ExecutionPolicy', 'Bypass', '-File', $e2eScript, '-Repeat', $Repeat)
        if (-not [string]::IsNullOrWhiteSpace($ServerDirectory)) {
            $arguments += @('-ServerDirectory', $ServerDirectory)
        }
        & powershell @arguments
        $e2eExitCode = $LASTEXITCODE
        if ($e2eExitCode -eq 0) {
            Write-Host "PASS code=ONLYLAN_PUBLISHED_E2E repeat=$Repeat" -ForegroundColor Green
        }
        else {
            $failures.Add("ONLYLAN_PUBLISHED_E2E_FAILED(exit=$e2eExitCode)")
            Write-Host "FAIL code=ONLYLAN_PUBLISHED_E2E_FAILED exit=$e2eExitCode repeat=$Repeat" -ForegroundColor Red
        }
    }

    if ($failures.Count -eq 0) {
        Write-Host "PASS code=ONLYLAN_CHARACTERIZATION_VERIFICATION_COMPLETE publishedE2E=$RunPublishedE2E fullSolution=$RunFullSolutionBuild" -ForegroundColor Green
        exit 0
    }

    Write-Host '------------------------------------------------------------' -ForegroundColor Red
    Write-Host "FAIL code=ONLYLAN_CHARACTERIZATION_VERIFICATION_INCOMPLETE failureCount=$($failures.Count)" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "FAILURE $failure" -ForegroundColor Red
    }
    exit 1
}
finally {
    Pop-Location
}
