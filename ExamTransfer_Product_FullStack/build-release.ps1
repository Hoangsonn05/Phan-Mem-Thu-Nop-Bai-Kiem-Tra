param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$frontendProject = Join-Path $root 'frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'
$backendSolution = Join-Path $root 'backend\ExamTransfer.sln'
$backendProject = Join-Path $root 'backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj'
$frontendVerify = Join-Path $root 'frontend\scripts\verify-frontend.ps1'
$installerScript = Join-Path $root 'installer\ExamTransfer.iss'
$appIcon = Join-Path $root 'installer\assets\Khoa-DT-KTMT.ico'
$releaseRoot = Join-Path $root 'artifacts\release'
$clientOutput = Join-Path $releaseRoot 'Client'
$serverOutput = Join-Path $releaseRoot 'Server'
$publicCloudConfig = Join-Path $clientOutput 'publiccloud.runtime.json'
$releaseManifest = Join-Path $releaseRoot 'release-manifest.json'
$installerOutput = Join-Path $root 'artifacts\installer'
$publicConfigPackaging = Join-Path $root 'scripts\public-config-packaging.ps1'
$canonicalLayoutTests = Join-Path $root 'scripts\test-canonical-release-layout.ps1'
$publicConfigPackagingTests = Join-Path $root 'scripts\test-public-config-packaging.ps1'
$dockerPackagingContractTests = Join-Path $root 'scripts\test-docker-packaging-contract.ps1'
$installerGuardTests = Join-Path $root 'scripts\test-installer-localserver-guard.ps1'
$installerCleanInstallTest = Join-Path $root 'scripts\test-installer-public-config-clean-install.ps1'
$installerMetadataHelper = Join-Path $root 'scripts\installer-version-metadata.ps1'
$installerMetadataTests = Join-Path $root 'scripts\test-installer-version-metadata.ps1'
$releaseArtifactValidator = Join-Path $root 'scripts\validate-release-artifacts.ps1'

function Require-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
}

function Assert-CanonicalIcon([string]$Path) {
    Require-File $Path
    if (-not [string]::Equals(
            [IO.Path]::GetExtension($Path),
            '.ico',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "RELEASE_ICON_INVALID_EXTENSION: $Path"
    }

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6 -or
        [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
        throw 'RELEASE_ICON_INVALID_FORMAT: canonical icon is not a valid ICO container.'
    }

    $count = [BitConverter]::ToUInt16($bytes, 4)
    if ($count -lt 1 -or $bytes.Length -lt 6 + (16 * $count)) {
        throw 'RELEASE_ICON_INVALID_FORMAT: canonical icon directory is truncated.'
    }

    $dimensions = [Collections.Generic.HashSet[int]]::new()
    for ($index = 0; $index -lt $count; $index++) {
        $offset = 6 + (16 * $index)
        $width = if ($bytes[$offset] -eq 0) { 256 } else { [int]$bytes[$offset] }
        $height = if ($bytes[$offset + 1] -eq 0) { 256 } else { [int]$bytes[$offset + 1] }
        if ($width -eq $height) {
            [void]$dimensions.Add($width)
        }
    }
    foreach ($requiredSize in @(16, 32, 48, 256)) {
        if (-not $dimensions.Contains($requiredSize)) {
            throw "RELEASE_ICON_MISSING_SIZE: canonical icon lacks ${requiredSize}x${requiredSize}."
        }
    }
}

function Find-InnoCompiler {
    $candidates = @(@(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })

    if ($candidates.Count -eq 0) {
        throw 'Inno Setup 6 was not found. Install Inno Setup 6 and retry.'
    }

    return $candidates[0]
}

foreach ($requiredFile in @(
    $frontendProject,
    $backendSolution,
    $backendProject,
    $frontendVerify,
    $installerScript,
    $publicConfigPackaging,
    $canonicalLayoutTests,
    $publicConfigPackagingTests,
    $dockerPackagingContractTests,
    $installerGuardTests,
    $installerCleanInstallTest,
    $installerMetadataHelper,
    $installerMetadataTests,
    $releaseArtifactValidator)) {
    Require-File $requiredFile
}
Assert-CanonicalIcon $appIcon
. $publicConfigPackaging
. $installerMetadataHelper

# Fail before restore, publish, cleanup, or ISCC when release identity or public
# client configuration is incomplete.
$assemblyVersion = ConvertTo-WindowsNumericVersion -SemanticVersion $Version
$expectedPublicCloudConfig = New-PublicCloudConfig `
    -SupabaseUrl ([string]$env:EXAMTRANSFER_SUPABASE_URL) `
    -PublishableKey ([string]$env:EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY) `
    -OrganizationId ([string]$env:EXAMTRANSFER_ORGANIZATION_ID)

$gitCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $gitCommit) {
    throw 'Unable to resolve the Git commit for release identity.'
}
$statusOutput = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine whether the Git working tree is dirty.'
}
if (@($statusOutput | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_)
    }).Count -ne 0) {
    throw 'RELEASE_WORKTREE_DIRTY: commit the release source and remove unrelated worktree changes before building an official installer.'
}
if ($SkipTests) {
    throw 'RELEASE_SKIP_TESTS_FORBIDDEN: -SkipTests cannot create an official installer.'
}

# Subprocess cases prove invalid public configuration stops before build work.
& powershell -NoProfile -ExecutionPolicy Bypass -File $canonicalLayoutTests
if ($LASTEXITCODE -ne 0) {
    throw 'Canonical release layout tests failed before release creation.'
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $publicConfigPackagingTests
if ($LASTEXITCODE -ne 0) {
    throw 'Public-config packaging preflight tests failed before release creation.'
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $dockerPackagingContractTests
if ($LASTEXITCODE -ne 0) {
    throw 'Docker packaging contract tests failed before release creation.'
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $installerMetadataTests
if ($LASTEXITCODE -ne 0) {
    throw 'Installer version metadata producer tests failed before release creation.'
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'dotnet was not found. Install .NET SDK 10.'
}

$buildTimestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$buildId = "$Version+$($gitCommit.Substring(0, 8))-clean.$buildTimestamp"
Write-Host "=== Khoa-DT-KTMT release $Version ===" -ForegroundColor Cyan
Write-Host "Project root: $root"
Write-Host "Build ID: $buildId"
Write-Host "Application icon: $appIcon"
dotnet --version

foreach ($publishOutput in @($clientOutput, $serverOutput)) {
    if (Test-Path -LiteralPath $publishOutput -PathType Container) {
        Remove-Item -LiteralPath $publishOutput -Recurse -Force
    }
}
if (Test-Path -LiteralPath $releaseManifest -PathType Leaf) {
    Remove-Item -LiteralPath $releaseManifest -Force
}
$shortCommit = $gitCommit.Substring(0, 8)
$installer = Join-Path $installerOutput "Khoa-DT-KTMT-Setup-$Version-$shortCommit.exe"
$installerHashFile = "$installer.sha256.txt"
if (Test-Path -LiteralPath $installerOutput -PathType Container) {
    foreach ($oldExe in @(Get-ChildItem -LiteralPath $installerOutput -Filter "*.exe" -File)) {
        Remove-Item -LiteralPath $oldExe.FullName -Force
    }
    foreach ($oldHash in @(Get-ChildItem -LiteralPath $installerOutput -Filter "*.sha256.txt" -File)) {
        Remove-Item -LiteralPath $oldHash.FullName -Force
    }
}
New-Item -ItemType Directory -Path $clientOutput -Force | Out-Null
New-Item -ItemType Directory -Path $serverOutput -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

Write-Host "\n[1/8] Restore backend..." -ForegroundColor Yellow
dotnet restore $backendSolution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

Write-Host "\n[2/8] Test backend Release..." -ForegroundColor Yellow
dotnet test $backendSolution -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed. Release creation stopped.' }

Write-Host "\n[3/8] Verify frontend..." -ForegroundColor Yellow
powershell -NoProfile -ExecutionPolicy Bypass -File $frontendVerify -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Frontend verification failed. Release creation stopped.' }

Write-Host "\n[4/8] Publish frontend WPF..." -ForegroundColor Yellow
dotnet publish $frontendProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:ExamTransferSemanticVersion=$Version `
    -p:ExamTransferBuildId=$buildId `
    -p:ExamTransferGitCommit=$gitCommit `
    -p:ExamTransferWorkingTreeDirty=false `
    "-p:ApplicationIcon=$appIcon" `
    -o $clientOutput
if ($LASTEXITCODE -ne 0) { throw 'Frontend publish failed.' }

Write-PublicCloudConfig -Path $publicCloudConfig -Config $expectedPublicCloudConfig
Require-File $publicCloudConfig
$verifiedPublicCloudConfig = Read-PublicCloudConfig -Path $publicCloudConfig
Assert-PublicCloudConfigEqual `
    -Expected $expectedPublicCloudConfig `
    -Actual $verifiedPublicCloudConfig `
    -Stage 'generated-release-payload'
$publicCloudConfigHash = (Get-FileHash $publicCloudConfig -Algorithm SHA256).Hash

Write-Host "\n[5/8] Publish Local Server..." -ForegroundColor Yellow
dotnet publish $backendProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:ExamTransferSemanticVersion=$Version `
    -p:ExamTransferBuildId=$buildId `
    -p:ExamTransferGitCommit=$gitCommit `
    -p:ExamTransferWorkingTreeDirty=false `
    -o $serverOutput
if ($LASTEXITCODE -ne 0) { throw 'Local Server publish failed.' }

$clientExe = Join-Path $clientOutput 'ExamTransfer.Desktop.exe'
$serverExe = Join-Path $serverOutput 'ExamTransfer.LocalServer.exe'
Require-File $clientExe
Require-File $serverExe

$clientHash = (Get-FileHash $clientExe -Algorithm SHA256).Hash
$serverHash = (Get-FileHash $serverExe -Algorithm SHA256).Hash
$manifest = [ordered]@{
    semanticVersion   = $Version
    buildId           = $buildId
    gitCommit         = $gitCommit
    shortCommit       = $shortCommit
    workingTreeDirty  = $false
    builtAtUtc        = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    buildTimestampUtc = $buildTimestamp
    discoveryProtocol = 'ExamTransfer/2'
    discoveryUdpPort  = 40550
    client            = [ordered]@{
        file   = 'Client/ExamTransfer.Desktop.exe'
        sha256 = $clientHash
    }
    server            = [ordered]@{
        file   = 'Server/ExamTransfer.LocalServer.exe'
        sha256 = $serverHash
    }
    publicCloudConfig = [ordered]@{
        file           = 'Client/publiccloud.runtime.json'
        sha256         = $publicCloudConfigHash
        classification = 'publishable-client-config'
    }
    installer         = [ordered]@{
        file           = "Khoa-DT-KTMT-Setup-$Version-$shortCommit.exe"
        fileVersion    = $assemblyVersion
        productVersion = $Version
        productName    = 'Khoa-DT-KTMT'
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $releaseManifest -Encoding utf8
Copy-Item -LiteralPath $releaseManifest -Destination (Join-Path $clientOutput 'release-manifest.json')
Copy-Item -LiteralPath $releaseManifest -Destination (Join-Path $serverOutput 'release-manifest.json')
Require-File $releaseManifest

$preIsccManifest = Get-Content -LiteralPath $releaseManifest -Raw | ConvertFrom-Json
if ([string]$preIsccManifest.gitCommit -ne $gitCommit) { throw 'PRE_ISCC_VALIDATION_FAILED: Git commit mismatch.' }
if ($preIsccManifest.workingTreeDirty -ne $false) { throw 'PRE_ISCC_VALIDATION_FAILED: Working tree dirty.' }
if (-not (Test-Path -LiteralPath $clientExe -PathType Leaf) -or
    -not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    throw 'PRE_ISCC_VALIDATION_FAILED: Client or Server output is missing.'
}
$actualClientHash = (Get-FileHash $clientExe -Algorithm SHA256).Hash
$actualServerHash = (Get-FileHash $serverExe -Algorithm SHA256).Hash
if (-not [string]::Equals($preIsccManifest.client.sha256, $actualClientHash, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($preIsccManifest.server.sha256, $actualServerHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PRE_ISCC_VALIDATION_FAILED: Client or Server hash mismatch.'
}
$clientVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($clientExe)
$serverVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($serverExe)
$clientProductVersionNormalized = ConvertFrom-InstallerVersionText -Value $clientVersionInfo.ProductVersion -MetadataName 'ProductVersion'
$serverProductVersionNormalized = ConvertFrom-InstallerVersionText -Value $serverVersionInfo.ProductVersion -MetadataName 'ProductVersion'
if ($clientProductVersionNormalized -ne $Version -or $serverProductVersionNormalized -ne $Version) {
    throw 'PRE_ISCC_VALIDATION_FAILED: Semantic version mismatch.'
}

Write-Host "\n[6/8] Verify installer guard with release public config..." -ForegroundColor Yellow
& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $installerGuardTests `
    -ReleaseRoot $releaseRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Installer guard tests failed with the generated release public config.'
}

Write-Host "\n[7/8] Build installer..." -ForegroundColor Yellow
$iscc = Find-InnoCompiler
& $iscc "/DMyAppVersion=$Version" "/DMyAppShortCommit=$shortCommit" "/DMyAppIcon=$appIcon" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Require-File $installer

$installerMetadata = Assert-InstallerVersionMetadata `
    -InstallerPath $installer `
    -ExpectedVersion $Version
Write-Host "Installer FileVersion: $($installerMetadata.FileVersionRaw)"
Write-Host "Installer ProductVersion: $($installerMetadata.ProductVersionRaw)"

$postIsccPublicCloudConfig = Read-PublicCloudConfig -Path $publicCloudConfig
Assert-PublicCloudConfigEqual `
    -Expected $expectedPublicCloudConfig `
    -Actual $postIsccPublicCloudConfig `
    -Stage 'post-ISCC-release-payload'
$postIsccPublicCloudConfigHash = (Get-FileHash $publicCloudConfig -Algorithm SHA256).Hash
if (-not [string]::Equals(
        $publicCloudConfigHash,
        $postIsccPublicCloudConfigHash,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PUBLICCLOUD_CONFIG_CHANGED_DURING_ISCC: release payload hash changed.'
}

Write-Host "\n[8/8] Clean-install packaged public config..." -ForegroundColor Yellow
& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $installerCleanInstallTest `
    -Version $Version `
    -ReleaseRoot $releaseRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Installer clean-install public-config acceptance failed.'
}

$finalPublicCloudConfig = Read-PublicCloudConfig -Path $publicCloudConfig
Assert-PublicCloudConfigEqual `
    -Expected $expectedPublicCloudConfig `
    -Actual $finalPublicCloudConfig `
    -Stage 'post-clean-install-release-payload'

$hash = Get-FileHash $installer -Algorithm SHA256
"$($hash.Hash)  $([IO.Path]::GetFileName($installer))" |
    Set-Content -LiteralPath $installerHashFile -Encoding ascii

& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $releaseArtifactValidator `
    -ExpectedVersion $Version `
    -ExpectedHead $gitCommit `
    -ExpectedBuildId $buildId `
    -RepositoryRoot $root `
    -ReleaseRoot $releaseRoot `
    -InstallerPath $installer `
    -HashFilePath $installerHashFile
if ($LASTEXITCODE -ne 0) {
    throw 'Release artifact identity validation failed.'
}

Write-Host "\nBUILD SUCCEEDED" -ForegroundColor Green
Write-Host "Installer : $installer"
Write-Host "SHA-256  : $($hash.Hash)"
Write-Host "Hash file: $installerHashFile"
Write-Host "Manifest : $releaseManifest"
