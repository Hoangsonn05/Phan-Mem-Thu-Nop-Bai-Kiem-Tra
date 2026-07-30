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

function Invoke-DotNetChecked {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

Push-Location $projectRoot
try {
    # Restore and build only the backend/test projects required by the
    # OnlyLAN characterization gate. Frontend compilation is intentionally
    # outside this task and must not hide the backend test result.
    foreach ($project in $backendProjects) {
        Invoke-DotNetChecked `
            -Arguments @('restore', $project) `
            -FailureMessage "Restore failed: $project"
    }

    foreach ($project in $backendProjects) {
        Invoke-DotNetChecked `
            -Arguments @('build', $project, '-c', 'Release', '--no-restore') `
            -FailureMessage "Release backend build failed: $project"
    }
    Write-Host 'PASS code=ONLYLAN_BACKEND_CHARACTERIZATION_BUILD' -ForegroundColor Green

    $characterizationFilter = 'FullyQualifiedName~OnlyLanWorkflowCharacterizationTests|FullyQualifiedName~OnlyLanCharacterizationHarnessContractTests'
    Invoke-DotNetChecked `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build', '--filter', $characterizationFilter) `
        -FailureMessage 'OnlyLAN characterization tests failed.'
    Write-Host 'PASS code=ONLYLAN_CHARACTERIZATION_TARGETED' -ForegroundColor Green

    $authFilter = 'FullyQualifiedName~AccountAuthFlowTests|FullyQualifiedName~SupabaseIdentityLoginTests|FullyQualifiedName~StudentParticipantScopeTests'
    Invoke-DotNetChecked `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build', '--filter', $authFilter) `
        -FailureMessage 'Login/participant authorization freeze tests failed.'
    Write-Host 'PASS code=ONLYLAN_LOGIN_AUTH_FREEZE' -ForegroundColor Green

    $publicCloudFilter = 'FullyQualifiedName~FinalCloudSourceCompatibilityTests|FullyQualifiedName~PublicCloudTeacherMutationTests|FullyQualifiedName~PublicCloudTeacherMutationRoutingTests|FullyQualifiedName~PublicCloudPullProjectionTests|FullyQualifiedName~PublicCloudOutboxLoopPreventionTests|FullyQualifiedName~PublicCloudCursorTransactionTests|FullyQualifiedName~PublicCloudMigrationSafetyTests|FullyQualifiedName~CloudOwnershipTests'
    Invoke-DotNetChecked `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build', '--filter', $publicCloudFilter) `
        -FailureMessage 'PublicCloud authority regression tests failed.'
    Write-Host 'PASS code=ONLYLAN_PUBLICCLOUD_FREEZE' -ForegroundColor Green

    Invoke-DotNetChecked `
        -Arguments @('test', $testProject, '-c', 'Release', '--no-build') `
        -FailureMessage 'Full backend Infrastructure regression failed.'
    Write-Host 'PASS code=ONLYLAN_INFRASTRUCTURE_REGRESSION' -ForegroundColor Green

    if ($RunFullSolutionBuild) {
        Invoke-DotNetChecked `
            -Arguments @('restore', $solution) `
            -FailureMessage 'Full solution restore failed.'
        Invoke-DotNetChecked `
            -Arguments @('build', $solution, '-c', 'Release', '--no-restore') `
            -FailureMessage 'Full solution build failed. This is a separate release gate, not an OnlyLAN characterization failure.'
        Write-Host 'PASS code=ONLYLAN_FULL_SOLUTION_BUILD' -ForegroundColor Green
    }
    else {
        Write-Host 'SKIP code=ONLYLAN_FULL_SOLUTION_BUILD reason=not_requested' -ForegroundColor Yellow
    }

    if ($RunPublishedE2E) {
        $arguments = @('-ExecutionPolicy', 'Bypass', '-File', $e2eScript, '-Repeat', $Repeat)
        if (-not [string]::IsNullOrWhiteSpace($ServerDirectory)) {
            $arguments += @('-ServerDirectory', $ServerDirectory)
        }
        & powershell @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Published OnlyLAN E2E failed.' }
        Write-Host "PASS code=ONLYLAN_PUBLISHED_E2E repeat=$Repeat" -ForegroundColor Green
    }

    Write-Host "PASS code=ONLYLAN_CHARACTERIZATION_VERIFICATION_COMPLETE publishedE2E=$RunPublishedE2E fullSolution=$RunFullSolutionBuild" -ForegroundColor Green
}
finally {
    Pop-Location
}
