[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [string]$ManifestPath,
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$ExpectedHead,
    [Parameter(Mandatory)][string]$ExpectedBuildId,
    [Parameter(Mandatory)][string]$RuntimeBuildId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedIntegrationGate = 'ET-RUNTIME-FINAL-INTEGRATION-04A'
$expectedCandidateTask = 'ET-RUNTIME-FINAL-CANDIDATE-04B'
$expectedPatchChain = @(
    'ET-RUNTIME-STABILIZE-03C',
    'ET-RUNTIME-STABILIZE-03D-AUTHORITY-01',
    'ET-RUNTIME-STABILIZE-03D-R1',
    'ET-RUNTIME-STABILIZE-03E1',
    'ET-RUNTIME-STABILIZE-03E2-R1'
)

function Assert-ReleaseConsistency {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Get-CanonicalPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Child
    )
    $prefix = $Parent.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    return $Child.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Test-StringSequenceEqual {
    param([string[]]$Actual, [string[]]$Expected)
    if ($Actual.Count -ne $Expected.Count) { return $false }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not [string]::Equals($Actual[$index], $Expected[$index], [StringComparison]::Ordinal)) {
            return $false
        }
    }
    return $true
}

try {
    $candidateRootPath = Get-CanonicalPath ((Resolve-Path -LiteralPath $CandidateRoot).Path)
    $repositoryRootPath = Get-CanonicalPath ((Resolve-Path -LiteralPath $RepositoryRoot).Path)
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        $ManifestPath = Join-Path $candidateRootPath 'release-manifest.json'
    }
    $manifestPathValue = Get-CanonicalPath ([IO.Path]::GetFullPath($ManifestPath))
    Assert-ReleaseConsistency `
        -Condition (Test-PathWithin -Parent $candidateRootPath -Child $manifestPathValue) `
        -Message 'Manifest path is outside the candidate root.'
    Assert-ReleaseConsistency `
        -Condition (Test-Path -LiteralPath $manifestPathValue -PathType Leaf) `
        -Message 'Release manifest was not found.'

    $manifest = Get-Content -LiteralPath $manifestPathValue -Raw | ConvertFrom-Json
    $actualHeadOutput = @(& git -C $repositoryRootPath rev-parse HEAD 2>&1)
    Assert-ReleaseConsistency -Condition ($LASTEXITCODE -eq 0 -and $actualHeadOutput.Count -eq 1) `
        -Message 'Unable to read repository HEAD.'
    $actualHead = ([string]$actualHeadOutput[0]).Trim()
    Assert-ReleaseConsistency -Condition ($actualHead -eq $ExpectedHead) `
        -Message 'Current repository HEAD does not match the requested HEAD.'
    Assert-ReleaseConsistency -Condition ([string]$manifest.gitHead -eq $ExpectedHead) `
        -Message 'Manifest GitHead mismatch.'

    $trackedStatus = @(& git -C $repositoryRootPath status --porcelain=v1 --untracked-files=no 2>&1)
    Assert-ReleaseConsistency -Condition ($LASTEXITCODE -eq 0) `
        -Message 'Unable to verify tracked worktree status.'
    $trackedChanges = @($trackedStatus | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    Assert-ReleaseConsistency -Condition ($trackedChanges.Count -eq 0) `
        -Message 'Tracked worktree is not clean.'
    Assert-ReleaseConsistency `
        -Condition ($manifest.workingTreeDirty -is [bool] -and -not $manifest.workingTreeDirty) `
        -Message 'Manifest workingTreeDirty must be boolean false.'

    Assert-ReleaseConsistency -Condition ([string]$manifest.buildId -eq $ExpectedBuildId) `
        -Message 'Manifest BuildId mismatch.'
    Assert-ReleaseConsistency -Condition ($RuntimeBuildId -eq $ExpectedBuildId) `
        -Message 'Runtime health BuildId mismatch.'
    Assert-ReleaseConsistency `
        -Condition (-not [string]::IsNullOrWhiteSpace([string]$manifest.version)) `
        -Message 'Manifest Version is missing.'
    Assert-ReleaseConsistency `
        -Condition ([string]$manifest.semanticVersion -eq [string]$manifest.version) `
        -Message 'Manifest semantic version mismatch.'
    Assert-ReleaseConsistency -Condition ([string]$manifest.runtimeIdentifier -eq 'win-x64') `
        -Message 'Manifest RuntimeIdentifier mismatch.'
    Assert-ReleaseConsistency `
        -Condition ($manifest.selfContained -is [bool] -and $manifest.selfContained) `
        -Message 'Manifest SelfContained must be boolean true.'

    Assert-ReleaseConsistency `
        -Condition ([string]$manifest.integrationGate -eq $expectedIntegrationGate) `
        -Message 'Manifest IntegrationGate provenance mismatch.'
    Assert-ReleaseConsistency `
        -Condition ([string]$manifest.candidateTask -eq $expectedCandidateTask) `
        -Message 'Manifest CandidateTask provenance mismatch.'
    Assert-ReleaseConsistency `
        -Condition (Test-StringSequenceEqual -Actual @($manifest.patchChain) -Expected $expectedPatchChain) `
        -Message 'Manifest PatchChain provenance mismatch.'
    Assert-ReleaseConsistency `
        -Condition ($manifest.publishedE2ERan -is [bool] -and $manifest.publishedE2ERan) `
        -Message 'Published OnlyLAN E2E was not recorded as executed.'
    Assert-ReleaseConsistency `
        -Condition ([string]$manifest.publishedE2EResult -eq 'PASS') `
        -Message 'Published OnlyLAN E2E did not pass.'

    $buildStart = [DateTimeOffset]::Parse([string]$manifest.buildStartUtc)
    $buildFinish = [DateTimeOffset]::Parse([string]$manifest.buildFinishUtc)
    Assert-ReleaseConsistency -Condition ($buildFinish -ge $buildStart) `
        -Message 'Manifest build window is invalid.'

    foreach ($artifactName in @('frontend', 'server')) {
        $artifact = $manifest.$artifactName
        Assert-ReleaseConsistency -Condition ($null -ne $artifact) `
            -Message "Manifest $artifactName artifact is missing."
        $relativeFile = [string]$artifact.file
        Assert-ReleaseConsistency `
            -Condition (-not [string]::IsNullOrWhiteSpace($relativeFile) -and -not [IO.Path]::IsPathRooted($relativeFile)) `
            -Message "Manifest $artifactName path is invalid."
        $artifactPath = Get-CanonicalPath (Join-Path $candidateRootPath $relativeFile)
        Assert-ReleaseConsistency `
            -Condition (Test-PathWithin -Parent $candidateRootPath -Child $artifactPath) `
            -Message "Manifest $artifactName path escapes the candidate root."
        Assert-ReleaseConsistency -Condition (Test-Path -LiteralPath $artifactPath -PathType Leaf) `
            -Message "Manifest $artifactName file was not found."

        $file = Get-Item -LiteralPath $artifactPath
        $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
        Assert-ReleaseConsistency `
            -Condition ([string]::Equals($actualHash, [string]$artifact.sha256, [StringComparison]::OrdinalIgnoreCase)) `
            -Message "Manifest $artifactName SHA-256 mismatch."
        Assert-ReleaseConsistency -Condition ([long]$artifact.sizeBytes -eq $file.Length) `
            -Message "Manifest $artifactName size mismatch."
        Assert-ReleaseConsistency `
            -Condition ($file.LastWriteTimeUtc -ge $buildStart.UtcDateTime -and $file.LastWriteTimeUtc -le $buildFinish.UtcDateTime) `
            -Message "Manifest $artifactName timestamp is outside the build window."

        $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($artifactPath)
        if (-not [string]::IsNullOrWhiteSpace([string]$artifact.fileVersion)) {
            Assert-ReleaseConsistency `
                -Condition ([string]$artifact.fileVersion -eq ([string]$versionInfo.FileVersion).Trim()) `
                -Message "Manifest $artifactName file version mismatch."
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$artifact.productVersion)) {
            Assert-ReleaseConsistency `
                -Condition ([string]$artifact.productVersion -eq ([string]$versionInfo.ProductVersion).Trim()) `
                -Message "Manifest $artifactName product version mismatch."
        }
    }

    Write-Host 'RELEASE_CONSISTENCY: PASS' -ForegroundColor Green
}
catch {
    Write-Host 'RELEASE_CONSISTENCY: FAIL' -ForegroundColor Red
    Write-Host "REASON: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
