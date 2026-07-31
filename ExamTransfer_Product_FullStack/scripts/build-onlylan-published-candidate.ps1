[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$CandidateOutput,
    [ValidateRange(1, 10)]
    [int]$PublishedE2ERepeat = 3,
    [switch]$LoadFunctionsOnly,
    [switch]$PreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$integrationGate = 'ET-RUNTIME-FINAL-INTEGRATION-04A'
$candidateTask = 'ET-RUNTIME-FINAL-CANDIDATE-04B'
$patchChain = @(
    'ET-RUNTIME-STABILIZE-03C',
    'ET-RUNTIME-STABILIZE-03D-AUTHORITY-01',
    'ET-RUNTIME-STABILIZE-03D-R1',
    'ET-RUNTIME-STABILIZE-03E1',
    'ET-RUNTIME-STABILIZE-03E2-R1'
)

function Write-FinalCandidateBlock {
    param(
        [Parameter(Mandatory)][string]$Reason,
        [string[]]$Detail = @()
    )

    Write-Host 'RESULT: BLOCKED' -ForegroundColor Red
    Write-Host "REASON: $Reason" -ForegroundColor Red
    foreach ($line in $Detail) {
        Write-Host "BLOCKING_PATH: $line" -ForegroundColor Red
    }
    throw $Reason
}

function Get-FinalCandidateCanonicalPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $pathRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $pathRoot
    }
    return $fullPath.TrimEnd([char[]]@('\', '/'))
}

function Test-FinalCandidatePathWithin {
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Child
    )

    $prefix = $Parent.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    return $Child.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-FinalCandidateNoReparsePoint {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$CandidatePath
    )

    $cursor = $CandidatePath
    while (Test-FinalCandidatePathWithin -Parent $ProjectRoot -Child $cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-FinalCandidateBlock `
                    -Reason 'UNSAFE_CANDIDATE_OUTPUT_PATH' `
                    -Detail @("reparse-point:$cursor")
            }
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [string]::Equals($parent, $cursor, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $cursor = Get-FinalCandidateCanonicalPath $parent
    }
}

function Resolve-FinalCandidatePaths {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [string]$CandidateOutput
    )

    if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) {
        Write-FinalCandidateBlock `
            -Reason 'UNSAFE_CANDIDATE_OUTPUT_PATH' `
            -Detail @("project-root-not-found:$ProjectRoot")
    }

    $resolvedProjectRoot = Get-FinalCandidateCanonicalPath (
        (Resolve-Path -LiteralPath $ProjectRoot).Path)
    $artifactsRoot = Get-FinalCandidateCanonicalPath (
        (Join-Path $resolvedProjectRoot 'artifacts'))
    $candidateArtifactsRoot = Get-FinalCandidateCanonicalPath (
        (Join-Path $artifactsRoot 'onlylan-published-e2e'))
    $rawCandidate = if ([string]::IsNullOrWhiteSpace($CandidateOutput)) {
        Join-Path $candidateArtifactsRoot 'candidate'
    }
    elseif ([IO.Path]::IsPathRooted($CandidateOutput)) {
        $CandidateOutput
    }
    else {
        Join-Path $resolvedProjectRoot $CandidateOutput
    }
    $candidateRoot = Get-FinalCandidateCanonicalPath $rawCandidate
    $scratchRoot = Get-FinalCandidateCanonicalPath (
        (Join-Path $candidateRoot 'smoke'))
    $driveRoot = Get-FinalCandidateCanonicalPath (
        ([IO.Path]::GetPathRoot($candidateRoot)))

    $unsafe =
        [string]::Equals($candidateRoot, $resolvedProjectRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidateRoot, $artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidateRoot, $candidateArtifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidateRoot, $driveRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-FinalCandidatePathWithin -Parent $candidateArtifactsRoot -Child $candidateRoot)
    if ($unsafe) {
        Write-FinalCandidateBlock `
            -Reason 'UNSAFE_CANDIDATE_OUTPUT_PATH' `
            -Detail @($candidateRoot)
    }

    Assert-FinalCandidateNoReparsePoint `
        -ProjectRoot $resolvedProjectRoot `
        -CandidatePath $candidateRoot
    Assert-FinalCandidateNoReparsePoint `
        -ProjectRoot $resolvedProjectRoot `
        -CandidatePath $scratchRoot

    return [pscustomobject]@{
        ProjectRoot = $resolvedProjectRoot
        ArtifactsRoot = $artifactsRoot
        CandidateArtifactsRoot = $candidateArtifactsRoot
        CandidateRoot = $candidateRoot
        ScratchRoot = $scratchRoot
    }
}

function Assert-FinalCandidateTrackedWorktreeClean {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $status = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=no 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git tracked-worktree check failed with exit code $exitCode."
    }

    $dirty = @($status | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($dirty.Count -gt 0) {
        Write-FinalCandidateBlock `
            -Reason 'TRACKED_WORKTREE_NOT_CLEAN' `
            -Detail @($dirty | ForEach-Object { [string]$_ })
    }

    Write-Host 'PASS tracked worktree clean (untracked files ignored).' -ForegroundColor Green
}

function Assert-FinalCandidatePublishedE2ESucceeded {
    param([Parameter(Mandatory)][int]$ExitCode)

    if ($ExitCode -ne 0) {
        Write-Host 'RESULT: FAIL' -ForegroundColor Red
        Write-Host 'REASON: PUBLISHED_ONLYLAN_E2E_FAILED' -ForegroundColor Red
        throw 'PUBLISHED_ONLYLAN_E2E_FAILED'
    }
}

function Write-FinalCandidateManifest {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Manifest,
        [Parameter(Mandatory)][string]$Path
    )

    $json = $Manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-TrimmedFileVersion {
    param(
        [Parameter(Mandatory)][Diagnostics.FileVersionInfo]$VersionInfo,
        [ValidateSet('FileVersion', 'ProductVersion')][string]$Property
    )

    $value = [string]$VersionInfo.$Property
    return $value.Trim()
}

if ($LoadFunctionsOnly) {
    return
}

$effectiveProjectRoot = if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    Join-Path $PSScriptRoot '..'
}
else {
    $ProjectRoot
}
$projectRootPath = Get-FinalCandidateCanonicalPath (
    (Resolve-Path -LiteralPath $effectiveProjectRoot).Path)

$gitRootOutput = @(& git -C $projectRootPath rev-parse --show-toplevel 2>&1)
if ($LASTEXITCODE -ne 0 -or $gitRootOutput.Count -ne 1) {
    throw 'Unable to resolve the Git repository root.'
}
$gitRoot = Get-FinalCandidateCanonicalPath ([string]$gitRootOutput[0])
$gitHeadOutput = @(& git -C $gitRoot rev-parse HEAD 2>&1)
if ($LASTEXITCODE -ne 0 -or $gitHeadOutput.Count -ne 1) {
    throw 'Unable to resolve the Git HEAD.'
}
$gitHead = ([string]$gitHeadOutput[0]).Trim()
if ($gitHead -notmatch '^[a-fA-F0-9]{40}$') {
    throw "Invalid Git HEAD: $gitHead"
}

# This gate must remain before path cleanup, restore, build, publish and manifest creation.
Assert-FinalCandidateTrackedWorktreeClean -RepositoryRoot $gitRoot
$paths = Resolve-FinalCandidatePaths `
    -ProjectRoot $projectRootPath `
    -CandidateOutput $CandidateOutput

Write-Host "PROJECT_ROOT: $($paths.ProjectRoot)"
Write-Host "GIT_ROOT: $gitRoot"
Write-Host "GIT_HEAD: $gitHead"
Write-Host "CANDIDATE_OUTPUT: $($paths.CandidateRoot)"

if ($PreflightOnly) {
    Write-Host 'PASS final candidate preflight only; no cleanup/build/publish executed.' -ForegroundColor Green
    return
}

$propsFile = Join-Path $paths.ProjectRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsFile -PathType Leaf)) {
    throw "Directory.Build.props was not found: $propsFile"
}
$propsXml = [xml](Get-Content -LiteralPath $propsFile -Raw)
$semanticVersion = [string]$propsXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($semanticVersion)) {
    throw 'Semantic version was not found in Directory.Build.props.'
}
$assemblyVersion = "$semanticVersion.0"
$buildStartUtc = [DateTimeOffset]::UtcNow
$buildTimestampText = $buildStartUtc.ToString('yyyyMMddTHHmmssZ')
$buildId = "$semanticVersion+$($gitHead.Substring(0, 8))-onlylan-final.$buildTimestampText"

if (Test-Path -LiteralPath $paths.CandidateRoot) {
    Remove-Item -LiteralPath $paths.CandidateRoot -Recurse -Force
}

$clientOutput = Join-Path $paths.CandidateRoot 'Client'
$serverOutput = Join-Path $paths.CandidateRoot 'Server'
$logOutput = Join-Path $paths.CandidateRoot 'build-logs'
$manifestPath = Join-Path $paths.CandidateRoot 'release-manifest.json'
foreach ($directory in @($clientOutput, $serverOutput, $logOutput, $paths.ScratchRoot)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}

$aggregateScript = Join-Path $paths.ProjectRoot 'scripts\verify-onlylan-characterization-backend-collect.ps1'
$publishedE2EScript = Join-Path $paths.ProjectRoot 'scripts\test-published-onlylan-e2e.ps1'
$consistencyScript = Join-Path $paths.ProjectRoot 'scripts\release-consistency.ps1'
$frontendProject = Join-Path $paths.ProjectRoot 'frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'
$backendProject = Join-Path $paths.ProjectRoot 'backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj'
foreach ($requiredFile in @(
    $aggregateScript,
    $publishedE2EScript,
    $consistencyScript,
    $frontendProject,
    $backendProject)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required final candidate input was not found: $requiredFile"
    }
}

Write-Host 'Running pre-publish automated gates...'
& powershell -NoProfile -ExecutionPolicy Bypass -File $aggregateScript
if ($LASTEXITCODE -ne 0) {
    throw 'Pre-publish verification failed.'
}

& dotnet restore $frontendProject
if ($LASTEXITCODE -ne 0) { throw 'Frontend restore failed.' }
& dotnet restore $backendProject
if ($LASTEXITCODE -ne 0) { throw 'Backend restore failed.' }

& dotnet publish $frontendProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$semanticVersion `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:ExamTransferSemanticVersion=$semanticVersion `
    -p:ExamTransferBuildId=$buildId `
    -p:ExamTransferGitCommit=$gitHead `
    -p:ExamTransferWorkingTreeDirty=false `
    -o $clientOutput `
    2>&1 | Tee-Object -FilePath (Join-Path $logOutput 'publish-frontend.log')
if ($LASTEXITCODE -ne 0) { throw 'Frontend publish failed.' }

& dotnet publish $backendProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$semanticVersion `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:ExamTransferSemanticVersion=$semanticVersion `
    -p:ExamTransferBuildId=$buildId `
    -p:ExamTransferGitCommit=$gitHead `
    -p:ExamTransferWorkingTreeDirty=false `
    -o $serverOutput `
    2>&1 | Tee-Object -FilePath (Join-Path $logOutput 'publish-backend.log')
if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }

$clientExe = Join-Path $clientOutput 'ExamTransfer.Desktop.exe'
$serverExe = Join-Path $serverOutput 'ExamTransfer.LocalServer.exe'
foreach ($artifact in @($clientExe, $serverExe)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Published artifact was not created: $artifact"
    }
}

$clientFile = Get-Item -LiteralPath $clientExe
$serverFile = Get-Item -LiteralPath $serverExe
$clientVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($clientExe)
$serverVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($serverExe)
$manifest = [ordered]@{
    version = $semanticVersion
    semanticVersion = $semanticVersion
    buildId = $buildId
    gitHead = $gitHead
    gitCommit = $gitHead
    workingTreeDirty = $false
    integrationGate = $integrationGate
    candidateTask = $candidateTask
    patchChain = $patchChain
    buildStartUtc = $buildStartUtc.ToString('O')
    buildFinishUtc = $null
    runtimeIdentifier = 'win-x64'
    runtime = 'win-x64'
    selfContained = $true
    discoveryProtocol = 'ExamTransfer/2'
    discoveryTcpPort = 5048
    discoveryUdpPort = 40550
    publishedE2ERan = $false
    publishedE2EResult = 'PENDING'
    frontend = [ordered]@{
        file = 'Client/ExamTransfer.Desktop.exe'
        sizeBytes = $clientFile.Length
        sha256 = (Get-FileHash -LiteralPath $clientExe -Algorithm SHA256).Hash
        fileVersion = Get-TrimmedFileVersion $clientVersion FileVersion
        productVersion = Get-TrimmedFileVersion $clientVersion ProductVersion
    }
    server = [ordered]@{
        file = 'Server/ExamTransfer.LocalServer.exe'
        sizeBytes = $serverFile.Length
        sha256 = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash
        fileVersion = Get-TrimmedFileVersion $serverVersion FileVersion
        productVersion = Get-TrimmedFileVersion $serverVersion ProductVersion
    }
}
Write-FinalCandidateManifest -Manifest $manifest -Path $manifestPath

$tcpOwner = Get-NetTCPConnection -LocalPort 5048 -State Listen -ErrorAction SilentlyContinue
$udpOwner = Get-NetUDPEndpoint -LocalPort 40550 -ErrorAction SilentlyContinue
if ($tcpOwner -or $udpOwner) {
    throw 'Runtime identity smoke requires TCP 5048 and UDP 40550 to be free.'
}

$stdoutLog = Join-Path $paths.ScratchRoot 'server.stdout.log'
$stderrLog = Join-Path $paths.ScratchRoot 'server.stderr.log'
$savedEnvironment = @{}
$environmentUpdates = @{
    'DOTNET_ENVIRONMENT' = 'Testing'
    'EXAMTRANSFER_ALLOW_TEST_FIXTURE' = '1'
    'Storage__RootPath' = $paths.ScratchRoot
    'EXAMTRANSFER_Storage__RootPath' = $paths.ScratchRoot
    'Cloud__Enabled' = 'false'
    'EXAMTRANSFER_Cloud__Enabled' = 'false'
    'Server__Port' = '5048'
    'Discovery__Enabled' = 'true'
    'Discovery__Port' = '40550'
}
$serverProcess = $null
$healthBuildId = $null
try {
    foreach ($name in $environmentUpdates.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environmentUpdates[$name], 'Process')
    }

    $serverProcess = Start-Process `
        -FilePath $serverExe `
        -WorkingDirectory $paths.ScratchRoot `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog

    $healthSuccess = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        if ($serverProcess.HasExited) {
            throw "Published server exited before health was ready. ExitCode=$($serverProcess.ExitCode)"
        }
        try {
            $health = Invoke-RestMethod `
                -Uri 'http://127.0.0.1:5048/health' `
                -Method Get `
                -ErrorAction Stop
            $healthBuildId = [string]$health.buildId
            if ($healthBuildId -ne $buildId) {
                throw "Identity mismatch: $healthBuildId != $buildId"
            }
            if ($health.protocol -ne 'ExamTransfer/2' -or
                [int]$health.discoveryPort -ne 40550 -or
                $health.backendRuntime.code -ne 'BACKEND_RUNTIME_READY' -or
                $health.udpDiscovery.code -ne 'UDP_DISCOVERY_LISTENING') {
                throw 'Published runtime health contract mismatch.'
            }
            $healthSuccess = $true
            break
        }
        catch {
            if ($_.Exception.Message -match 'mismatch') { throw }
        }
    }
    if (-not $healthSuccess) {
        throw 'Published runtime health/BuildId verification timed out.'
    }
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        [void]$serverProcess.WaitForExit(5000)
    }
    foreach ($name in $environmentUpdates.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}

Write-Host 'Running Published OnlyLAN E2E against the current candidate...'
& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $publishedE2EScript `
    -ServerDirectory $serverOutput `
    -ClientDirectory $clientOutput `
    -Repeat $PublishedE2ERepeat
$publishedE2EExitCode = $LASTEXITCODE
Assert-FinalCandidatePublishedE2ESucceeded -ExitCode $publishedE2EExitCode

$manifest['publishedE2ERan'] = $true
$manifest['publishedE2EResult'] = 'PASS'
$manifest['buildFinishUtc'] = [DateTimeOffset]::UtcNow.ToString('O')
Write-FinalCandidateManifest -Manifest $manifest -Path $manifestPath

& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $consistencyScript `
    -CandidateRoot $paths.CandidateRoot `
    -ManifestPath $manifestPath `
    -RepositoryRoot $gitRoot `
    -ExpectedHead $gitHead `
    -ExpectedBuildId $buildId `
    -RuntimeBuildId $healthBuildId
if ($LASTEXITCODE -ne 0) {
    throw 'RELEASE_CONSISTENCY_FAILED'
}

Write-Host 'RESULT: PASS' -ForegroundColor Green
Write-Host 'REASON: FINAL_CANDIDATE_CONTRACT_COMPLETE' -ForegroundColor Green
Write-Host "ATOMIC_BUILD_ID: $buildId"
Write-Host "CANDIDATE_OUTPUT: $($paths.CandidateRoot)"
