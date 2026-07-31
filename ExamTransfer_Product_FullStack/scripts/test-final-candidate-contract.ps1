[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$candidateScript = Join-Path $PSScriptRoot 'build-onlylan-published-candidate.ps1'
$consistencyScript = Join-Path $PSScriptRoot 'release-consistency.ps1'
$script:passed = 0
$script:failed = 0
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
$harnessRoot = Join-Path $tempBase ("examtransfer-final-candidate-contract-{0}" -f [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($harnessRoot) | Out-Null

function Assert-Test {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) {
        $script:failed++
        throw "TEST FAILED: $Name"
    }
    $script:passed++
    Write-Host "PASS: $Name" -ForegroundColor Green
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Name)
    $message = $null
    try { & $Action } catch { $message = $_.Exception.Message }
    Assert-Test `
        -Condition (-not [string]::IsNullOrWhiteSpace($message) -and $message -match $Pattern) `
        -Name $Name
}

function New-TestGitRepository {
    $root = Join-Path $harnessRoot ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'tracked.txt'), 'tracked')
    & git -C $root init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Test repository init failed.' }
    & git -C $root add tracked.txt
    if ($LASTEXITCODE -ne 0) { throw 'Test repository add failed.' }
    & git -C $root -c user.name=ContractHarness -c user.email=contract@example.invalid commit --quiet -m initial
    if ($LASTEXITCODE -ne 0) { throw 'Test repository commit failed.' }
    return $root
}

function New-ConsistencyFixture {
    param(
        [switch]$DirtyManifest,
        [switch]$StaleProvenance,
        [ValidateSet('PASS', 'PENDING', 'FAIL')][string]$E2EResult = 'PASS',
        [bool]$E2ERan = $true
    )

    $repositoryRoot = New-TestGitRepository
    $candidateRoot = Join-Path $repositoryRoot 'artifacts\onlylan-published-e2e\candidate'
    $clientDirectory = Join-Path $candidateRoot 'Client'
    $serverDirectory = Join-Path $candidateRoot 'Server'
    [IO.Directory]::CreateDirectory($clientDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($serverDirectory) | Out-Null
    $clientPath = Join-Path $clientDirectory 'ExamTransfer.Desktop.exe'
    $serverPath = Join-Path $serverDirectory 'ExamTransfer.LocalServer.exe'
    [IO.File]::WriteAllText($clientPath, 'frontend-fixture')
    [IO.File]::WriteAllText($serverPath, 'backend-fixture')
    $head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    $buildId = '1.2.0+fixture-onlylan-final.20260731T000000Z'
    $manifest = [ordered]@{
        version = '1.2.0'
        semanticVersion = '1.2.0'
        buildId = $buildId
        gitHead = $head
        gitCommit = $head
        workingTreeDirty = [bool]$DirtyManifest
        integrationGate = if ($StaleProvenance) { 'ET-LAN-MODULE-REFACTOR-01D' } else { 'ET-RUNTIME-FINAL-INTEGRATION-04A' }
        candidateTask = 'ET-RUNTIME-FINAL-CANDIDATE-04B'
        patchChain = @(
            'ET-RUNTIME-STABILIZE-03C',
            'ET-RUNTIME-STABILIZE-03D-AUTHORITY-01',
            'ET-RUNTIME-STABILIZE-03D-R1',
            'ET-RUNTIME-STABILIZE-03E1',
            'ET-RUNTIME-STABILIZE-03E2-R1'
        )
        buildStartUtc = [DateTimeOffset]::UtcNow.AddSeconds(-10).ToString('O')
        buildFinishUtc = [DateTimeOffset]::UtcNow.AddSeconds(10).ToString('O')
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        publishedE2ERan = $E2ERan
        publishedE2EResult = $E2EResult
        frontend = [ordered]@{
            file = 'Client/ExamTransfer.Desktop.exe'
            sizeBytes = (Get-Item -LiteralPath $clientPath).Length
            sha256 = (Get-FileHash -LiteralPath $clientPath -Algorithm SHA256).Hash
            fileVersion = ''
            productVersion = ''
        }
        server = [ordered]@{
            file = 'Server/ExamTransfer.LocalServer.exe'
            sizeBytes = (Get-Item -LiteralPath $serverPath).Length
            sha256 = (Get-FileHash -LiteralPath $serverPath -Algorithm SHA256).Hash
            fileVersion = ''
            productVersion = ''
        }
    }
    $manifestPath = Join-Path $candidateRoot 'release-manifest.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        RepositoryRoot = $repositoryRoot
        CandidateRoot = $candidateRoot
        ManifestPath = $manifestPath
        ServerPath = $serverPath
        Head = $head
        BuildId = $buildId
    }
}

function Invoke-ConsistencyFixture {
    param(
        [Parameter(Mandatory)]$Fixture,
        [string]$ExpectedHead = $Fixture.Head,
        [string]$ExpectedBuildId = $Fixture.BuildId,
        [string]$RuntimeBuildId = $Fixture.BuildId
    )
    $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $consistencyScript `
        -CandidateRoot $Fixture.CandidateRoot `
        -ManifestPath $Fixture.ManifestPath `
        -RepositoryRoot $Fixture.RepositoryRoot `
        -ExpectedHead $ExpectedHead `
        -ExpectedBuildId $ExpectedBuildId `
        -RuntimeBuildId $RuntimeBuildId 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine)
    }
}

try {
    . $candidateScript -LoadFunctionsOnly

    $cleanRepository = New-TestGitRepository
    Assert-FinalCandidateTrackedWorktreeClean -RepositoryRoot $cleanRepository
    Assert-Test -Condition $true -Name 'clean tracked worktree passes'
    [IO.File]::WriteAllText((Join-Path $cleanRepository 'ET-REPORT.md'), 'untracked report')
    Assert-FinalCandidateTrackedWorktreeClean -RepositoryRoot $cleanRepository
    Assert-Test -Condition $true -Name 'untracked report does not make tracked gate dirty'

    $dirtyRepository = New-TestGitRepository
    [IO.File]::AppendAllText((Join-Path $dirtyRepository 'tracked.txt'), '-modified')
    Assert-Throws `
        -Action { Assert-FinalCandidateTrackedWorktreeClean -RepositoryRoot $dirtyRepository } `
        -Pattern 'TRACKED_WORKTREE_NOT_CLEAN' `
        -Name 'modified tracked file is blocked'

    $pathProject = Join-Path $harnessRoot 'path-project'
    [IO.Directory]::CreateDirectory($pathProject) | Out-Null
    $validPaths = Resolve-FinalCandidatePaths -ProjectRoot $pathProject -CandidateOutput 'artifacts\onlylan-published-e2e\candidate-r1'
    Assert-Test `
        -Condition ($validPaths.CandidateRoot.EndsWith('candidate-r1', [StringComparison]::OrdinalIgnoreCase)) `
        -Name 'candidate child path passes containment'
    $sentinel = Join-Path $pathProject 'sentinel.txt'
    [IO.File]::WriteAllText($sentinel, 'do-not-delete')
    Assert-Throws { Resolve-FinalCandidatePaths -ProjectRoot $pathProject -CandidateOutput $pathProject } 'UNSAFE_CANDIDATE_OUTPUT_PATH' 'repository root is rejected'
    Assert-Throws { Resolve-FinalCandidatePaths -ProjectRoot $pathProject -CandidateOutput (Join-Path $pathProject 'artifacts') } 'UNSAFE_CANDIDATE_OUTPUT_PATH' 'artifacts root is rejected'
    Assert-Throws { Resolve-FinalCandidatePaths -ProjectRoot $pathProject -CandidateOutput ([IO.Path]::GetPathRoot($pathProject)) } 'UNSAFE_CANDIDATE_OUTPUT_PATH' 'drive root is rejected'
    Assert-Throws { Resolve-FinalCandidatePaths -ProjectRoot $pathProject -CandidateOutput 'artifacts\onlylan-published-e2e\..\..\escape' } 'UNSAFE_CANDIDATE_OUTPUT_PATH' 'relative traversal escape is rejected'
    Assert-Throws { Resolve-FinalCandidatePaths -ProjectRoot $pathProject -CandidateOutput (Join-Path $harnessRoot 'outside') } 'UNSAFE_CANDIDATE_OUTPUT_PATH' 'outside path is rejected'
    Assert-Test -Condition (Test-Path -LiteralPath $sentinel -PathType Leaf) -Name 'unsafe paths do not delete files'

    $candidateSource = Get-Content -LiteralPath $candidateScript -Raw
    Assert-Test -Condition ($candidateSource -notmatch 'ET-LAN-MODULE-REFACTOR-01D|ET-LAN-PUBLISHED-CANDIDATE-BUILD-ID-ATOMIC-01') -Name 'stale candidate provenance is absent'
    Assert-Test -Condition ($candidateSource -match 'ET-RUNTIME-FINAL-INTEGRATION-04A' -and $candidateSource -match 'ET-RUNTIME-FINAL-CANDIDATE-04B') -Name 'final provenance is present'
    Assert-Test -Condition ($candidateSource -match 'git\s+-C\s+\$gitRoot\s+rev-parse\s+HEAD') -Name 'HEAD is read from Git'
    Assert-Test -Condition (([regex]::Matches($candidateSource, '\$buildId\s*=')).Count -eq 1) -Name 'BuildId is generated once'
    Assert-Test -Condition (([regex]::Matches($candidateSource, '-p:ExamTransferBuildId=\$buildId')).Count -eq 2) -Name 'same BuildId is passed to frontend and backend'
    Assert-Test -Condition (([regex]::Matches($candidateSource, '-File\s+\$publishedE2EScript')).Count -eq 1) -Name 'Published E2E is invoked exactly once'
    $publishIndex = $candidateSource.LastIndexOf('& dotnet publish $backendProject', [StringComparison]::Ordinal)
    $healthIndex = $candidateSource.IndexOf('Invoke-RestMethod `', [StringComparison]::Ordinal)
    $e2eIndex = $candidateSource.IndexOf('-File $publishedE2EScript', [StringComparison]::Ordinal)
    $consistencyIndex = $candidateSource.IndexOf('-File $consistencyScript', [StringComparison]::Ordinal)
    $passIndex = $candidateSource.LastIndexOf("Write-Host 'RESULT: PASS'", [StringComparison]::Ordinal)
    Assert-Test -Condition ($publishIndex -ge 0 -and $publishIndex -lt $healthIndex -and $healthIndex -lt $e2eIndex -and $e2eIndex -lt $consistencyIndex -and $consistencyIndex -lt $passIndex) -Name 'publish health E2E consistency PASS order is fixed'
    Assert-Test -Condition ($candidateSource -match '-ServerDirectory\s+\$serverOutput' -and $candidateSource -match '-ClientDirectory\s+\$clientOutput') -Name 'Published E2E receives current candidate frontend and backend'
    $cleanGateIndex = $candidateSource.IndexOf('Assert-FinalCandidateTrackedWorktreeClean -RepositoryRoot $gitRoot', [StringComparison]::Ordinal)
    $safePathIndex = $candidateSource.IndexOf('Resolve-FinalCandidatePaths', $cleanGateIndex, [StringComparison]::Ordinal)
    $removeIndex = $candidateSource.IndexOf('Remove-Item -LiteralPath $paths.CandidateRoot', [StringComparison]::Ordinal)
    Assert-Test -Condition ($cleanGateIndex -lt $safePathIndex -and $safePathIndex -lt $removeIndex) -Name 'dirty and containment gates precede exact cleanup'
    $manifestCleanIndex = $candidateSource.IndexOf('workingTreeDirty = $false', [StringComparison]::Ordinal)
    Assert-Test -Condition ($manifestCleanIndex -gt $cleanGateIndex) -Name 'manifest clean value is written only after the real tracked gate'
    Assert-FinalCandidatePublishedE2ESucceeded -ExitCode 0
    Assert-Test -Condition $true -Name 'Published E2E zero exit continues'
    Assert-Throws { Assert-FinalCandidatePublishedE2ESucceeded -ExitCode 9 } 'PUBLISHED_ONLYLAN_E2E_FAILED' 'Published E2E failure propagates'

    $valid = New-ConsistencyFixture
    $manifestHashBefore = (Get-FileHash -LiteralPath $valid.ManifestPath -Algorithm SHA256).Hash
    $validResult = Invoke-ConsistencyFixture -Fixture $valid
    $manifestHashAfter = (Get-FileHash -LiteralPath $valid.ManifestPath -Algorithm SHA256).Hash
    Assert-Test -Condition ($validResult.ExitCode -eq 0 -and $validResult.Output -match 'RELEASE_CONSISTENCY: PASS') -Name 'valid release consistency fixture passes'
    Assert-Test -Condition ($manifestHashBefore -eq $manifestHashAfter) -Name 'release consistency does not mutate manifest'

    $buildMismatch = New-ConsistencyFixture
    $result = Invoke-ConsistencyFixture -Fixture $buildMismatch -ExpectedBuildId 'wrong-build-id'
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'BuildId mismatch fails consistency'
    $headMismatch = New-ConsistencyFixture
    $result = Invoke-ConsistencyFixture -Fixture $headMismatch -ExpectedHead ('0' * 40)
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'HEAD mismatch fails consistency'
    $hashMismatch = New-ConsistencyFixture
    [IO.File]::AppendAllText($hashMismatch.ServerPath, '-tampered')
    $result = Invoke-ConsistencyFixture -Fixture $hashMismatch
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'artifact hash mismatch fails consistency'
    $dirtyManifest = New-ConsistencyFixture -DirtyManifest
    $result = Invoke-ConsistencyFixture -Fixture $dirtyManifest
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'dirty manifest fails consistency'
    $dirtyTracked = New-ConsistencyFixture
    [IO.File]::AppendAllText((Join-Path $dirtyTracked.RepositoryRoot 'tracked.txt'), '-modified')
    $result = Invoke-ConsistencyFixture -Fixture $dirtyTracked
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'dirty tracked worktree fails consistency'
    $staleProvenance = New-ConsistencyFixture -StaleProvenance
    $result = Invoke-ConsistencyFixture -Fixture $staleProvenance
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'stale provenance fails consistency'
    $pendingE2E = New-ConsistencyFixture -E2EResult PENDING -E2ERan $false
    $result = Invoke-ConsistencyFixture -Fixture $pendingE2E
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'E2E not-run pending state fails consistency'
    $failedE2E = New-ConsistencyFixture -E2EResult FAIL
    $result = Invoke-ConsistencyFixture -Fixture $failedE2E
    Assert-Test -Condition ($result.ExitCode -ne 0 -and $result.Output -match 'RELEASE_CONSISTENCY: FAIL') -Name 'E2E failed state fails consistency'

    Write-Host "FINAL_CANDIDATE_CONTRACT_TESTS: PASS ($script:passed assertions)" -ForegroundColor Green
}
finally {
    $resolvedHarnessRoot = [IO.Path]::GetFullPath($harnessRoot)
    $expectedPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar + 'examtransfer-final-candidate-contract-'
    if ($resolvedHarnessRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedHarnessRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedHarnessRoot -Recurse -Force
    }
}
