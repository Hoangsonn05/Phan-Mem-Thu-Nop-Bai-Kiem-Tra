[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$installerSource = Join-Path $root 'installer\ExamTransfer.iss'
$sharedValidation = Join-Path $PSScriptRoot 'public-config-packaging.ps1'
$metadataHelper = Join-Path $PSScriptRoot 'installer-version-metadata.ps1'
$resolvedReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$releasePublicConfigPath = Join-Path $resolvedReleaseRoot 'Client\publiccloud.runtime.json'
$releaseManifestPath = Join-Path $resolvedReleaseRoot 'release-manifest.json'

foreach ($requiredFile in @(
    $installerSource,
    $sharedValidation,
    $metadataHelper,
    $releasePublicConfigPath,
    $releaseManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required clean-install input was not found: $requiredFile"
    }
}
. $sharedValidation
. $metadataHelper

function Find-InnoCompiler {
    $candidates = @(@(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($candidates.Count -eq 0) {
        throw 'Inno Setup 6 was not found.'
    }
    return $candidates[0]
}

function Invoke-InstallerProcess(
    [string]$Path,
    [string[]]$Arguments,
    [string]$Operation) {
    $process = Start-Process `
        -FilePath $Path `
        -ArgumentList $Arguments `
        -WindowStyle Hidden `
        -PassThru
    $process | Wait-Process
    try {
        if ($process.ExitCode -ne 0) {
            throw "$Operation failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

$expectedConfig = Read-PublicCloudConfig -Path $releasePublicConfigPath
$expectedConfigHash = (Get-FileHash `
    -LiteralPath $releasePublicConfigPath `
    -Algorithm SHA256).Hash
$expectedManifestHash = (Get-FileHash `
    -LiteralPath $releaseManifestPath `
    -Algorithm SHA256).Hash

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ExamTransfer-PublicConfigInstall-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $fixtureRoot 'App'
$runtimeRoot = Join-Path $fixtureRoot 'Data'
$storageRoot = Join-Path $runtimeRoot 'Storage'
$outputRoot = Join-Path $fixtureRoot 'Installer'
$installLog = Join-Path $fixtureRoot 'install.log'
$upgradeLog = Join-Path $fixtureRoot 'upgrade.log'
$uninstallLog = Join-Path $fixtureRoot 'uninstall.log'
$manifestContent = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
$shortCommit = $manifestContent.gitCommit.Substring(0, 8)
$testInstaller = Join-Path $outputRoot "Khoa-DT-KTMT-Setup-$Version-$shortCommit.exe"
$uninstaller = Join-Path $installRoot 'unins000.exe'

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $testAppId = '{{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $isccArguments = @(
        "/DMyAppVersion=$Version",
        "/DMyAppShortCommit=$shortCommit",
        "/DMyAppId=$testAppId",
        "/DMyDefaultDirName=$installRoot",
        '/DMyClientShortcutName=Khoa-DT-KTMT Public Config Acceptance',
        "/DMyOutputDir=$outputRoot",
        "/DMyReleaseRoot=$resolvedReleaseRoot",
        '/DMyPrivilegesRequired=lowest',
        "/DMyRuntimeSettingsRoot=$runtimeRoot",
        "/DMyCanonicalStorageRoot=$($storageRoot.Replace('\', '/'))",
        '/DMyDisableFirewall=1',
        '/DMyDisableLegacyCleanup=1',
        $installerSource
    )
    & (Find-InnoCompiler) @isccArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Public-config acceptance installer compilation failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $testInstaller -PathType Leaf)) {
        throw "Public-config acceptance installer was not created: $testInstaller"
    }

    $installerMetadata = Assert-InstallerVersionMetadata `
        -InstallerPath $testInstaller `
        -ExpectedVersion $Version
    Write-Host (
        'PASS clean-install fixture metadata ' +
        "fileVersion=$($installerMetadata.FileVersionRaw) " +
        "productVersion=$($installerMetadata.ProductVersionRaw)") -ForegroundColor Green

    Invoke-InstallerProcess `
        -Path $testInstaller `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            "/LOG=$installLog") `
        -Operation 'Clean install'

    $installedPublicConfigPath = Join-Path $installRoot 'Client\publiccloud.runtime.json'
    if (-not (Test-Path -LiteralPath $installedPublicConfigPath -PathType Leaf)) {
        throw 'Installed publiccloud.runtime.json is missing.'
    }
    $installedHash = (Get-FileHash `
        -LiteralPath $installedPublicConfigPath `
        -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $expectedConfigHash,
            $installedHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed publiccloud.runtime.json is not byte-identical to the release payload.'
    }
    $installedConfig = Read-PublicCloudConfig -Path $installedPublicConfigPath
    Assert-PublicCloudConfigEqual `
        -Expected $expectedConfig `
        -Actual $installedConfig `
        -Stage 'installed-public-config'

    $installedManifestPath = Join-Path $installRoot 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) {
        throw 'Installed release-manifest.json is missing.'
    }
    $installedManifestHash = (Get-FileHash `
        -LiteralPath $installedManifestPath `
        -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $expectedManifestHash,
            $installedManifestHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed release-manifest.json is not byte-identical to the release payload.'
    }

    $runtimeSettingsPath = Join-Path $runtimeRoot 'config\runtime-settings.json'
    if (-not (Test-Path -LiteralPath $runtimeSettingsPath -PathType Leaf)) {
        throw 'Clean install did not create runtime-settings.json.'
    }
    $runtimeSettings = Get-Content -LiteralPath $runtimeSettingsPath -Raw | ConvertFrom-Json
    foreach ($mapping in @(
        @{ Name = 'supabaseUrl'; RuntimeName = 'SupabaseUrl' },
        @{ Name = 'publishableKey'; RuntimeName = 'PublishableKey' },
        @{ Name = 'organizationId'; RuntimeName = 'OrganizationId' })) {
        if (-not [string]::Equals(
                [string]$expectedConfig.($mapping.Name),
                [string]$runtimeSettings.Cloud.($mapping.RuntimeName),
                [StringComparison]::Ordinal)) {
            throw "Clean-install runtime config mismatch: $($mapping.RuntimeName)"
        }
    }
    if ((-not $runtimeSettings.Cloud.Enabled) -or
        ([string]$runtimeSettings.Cloud.Environment -cne 'Production') -or
        ([string]$runtimeSettings.Cloud.AccessMode -cne 'UserSession')) {
        throw 'Clean-install runtime Cloud mode fields are invalid.'
    }

    $legacyRuntime = [ordered]@{
        Discovery = [ordered]@{ Enabled = $true; Port = 40550 }
        Storage = [ordered]@{
            RootPath = $storageRoot
            MinFreeBytes = 987654321
        }
        Cloud = [ordered]@{
            Enabled = $false
            SupabaseUrl = 'https://project-old.supabase.co'
            PublishableKey = 'sb_publishable_old_upgrade_fixture_1234567890'
            OrganizationId = [Guid]::NewGuid().ToString('D')
            Environment = 'Legacy'
            AccessMode = 'TrustedServer'
            UseResumableUploads = $false
        }
        Database = [ordered]@{ Path = 'database\preserve.db' }
        DeviceIdentity = [ordered]@{ Id = 'upgrade-device-preserved' }
        UserPreferences = [ordered]@{ Theme = 'dark' }
    }
    [IO.File]::WriteAllText(
        $runtimeSettingsPath,
        ($legacyRuntime | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
    $legacyRuntimeBytes = [IO.File]::ReadAllBytes($runtimeSettingsPath)

    Invoke-InstallerProcess `
        -Path $testInstaller `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            "/LOG=$upgradeLog") `
        -Operation 'Upgrade install'

    $upgradedRuntime = Get-Content -LiteralPath $runtimeSettingsPath -Raw | ConvertFrom-Json
    foreach ($mapping in @(
        @{ Name = 'supabaseUrl'; RuntimeName = 'SupabaseUrl' },
        @{ Name = 'publishableKey'; RuntimeName = 'PublishableKey' },
        @{ Name = 'organizationId'; RuntimeName = 'OrganizationId' })) {
        if (-not [string]::Equals(
                [string]$expectedConfig.($mapping.Name),
                [string]$upgradedRuntime.Cloud.($mapping.RuntimeName),
                [StringComparison]::Ordinal)) {
            throw "Upgrade runtime config mismatch: $($mapping.RuntimeName)"
        }
    }
    if (([string]$upgradedRuntime.Database.Path -cne 'database\preserve.db') -or
        ([string]$upgradedRuntime.DeviceIdentity.Id -cne 'upgrade-device-preserved') -or
        ([string]$upgradedRuntime.UserPreferences.Theme -cne 'dark') -or
        ([long]$upgradedRuntime.Storage.MinFreeBytes -ne 987654321) -or
        [bool]$upgradedRuntime.Cloud.UseResumableUploads) {
        throw 'Upgrade did not preserve non-authoritative runtime settings.'
    }
    $upgradeBackups = @(Get-ChildItem `
        -LiteralPath (Split-Path -Parent $runtimeSettingsPath) `
        -Filter 'runtime-settings.backup-*.json')
    if ($upgradeBackups.Count -ne 1 -or
        -not [Linq.Enumerable]::SequenceEqual(
            [byte[]]$legacyRuntimeBytes,
            [byte[]][IO.File]::ReadAllBytes($upgradeBackups[0].FullName))) {
        throw 'Upgrade backup is missing or not byte-exact.'
    }

    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw 'Clean-install uninstaller was not created.'
    }
    Invoke-InstallerProcess `
        -Path $uninstaller `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            "/LOG=$uninstallLog") `
        -Operation 'Clean-install cleanup'

    Write-Host (
        'PASS installer public-config clean-install ' +
        'release-payload=byte-identical manifest=byte-identical ' +
        'runtime-settings=clean+upgrade-converged backup=byte-exact ' +
        'fixture-config=not-used') -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try {
            Invoke-InstallerProcess `
                -Path $uninstaller `
                -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
                -Operation 'Fallback clean-install cleanup'
        }
        catch {
            Write-Warning $_.Exception.Message
        }
    }

    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedFixture).StartsWith(
            'ExamTransfer-PublicConfigInstall-',
            [StringComparison]::Ordinal)) {
        # Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
