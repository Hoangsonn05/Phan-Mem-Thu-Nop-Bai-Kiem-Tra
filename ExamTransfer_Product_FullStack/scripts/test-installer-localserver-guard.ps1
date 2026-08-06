[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$guard = Join-Path $PSScriptRoot 'installer-localserver-guard.ps1'
if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) {
    throw "Installer guard was not found: $guard"
}
$resolvedReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$publicConfigPath = Join-Path $resolvedReleaseRoot 'Client\publiccloud.runtime.json'
if (-not (Test-Path -LiteralPath $publicConfigPath -PathType Leaf)) {
    throw "Release payload public config was not found: $publicConfigPath"
}
$installerPath = Join-Path (
    Split-Path -Parent $PSScriptRoot) 'installer\ExamTransfer.iss'
$installerSource = Get-Content -LiteralPath $installerPath -Raw
$legacyPortMatches = [regex]::Matches(
    $installerSource,
    '(?m)^\s*#define MyLegacyDiscoveryPort(?:Primary|Secondary) "(\d+)"\s*$')
if ($legacyPortMatches.Count -ne 2) {
    throw 'Installer legacy discovery port contract is missing or ambiguous.'
}
$legacyDiscoveryPorts = @(
    $legacyPortMatches | ForEach-Object { [int]$_.Groups[1].Value }
)
$legacyDiscoveryPortsArgument = $legacyDiscoveryPorts -join ','

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ExamTransfer-InstallerGuard-' + [Guid]::NewGuid().ToString('N'))
$installedDirectory = Join-Path $fixtureRoot 'Installed\Server'
$unrelatedDirectory = Join-Path $fixtureRoot 'Unrelated'
$userDataDirectory = Join-Path $fixtureRoot 'UserData'
$installedExe = Join-Path $installedDirectory 'ExamTransfer.LocalServer.exe'
$unrelatedExe = Join-Path $unrelatedDirectory 'ExamTransfer.LocalServer.exe'
$userDataMarker = Join-Path $userDataDirectory 'preserve.marker'
$installedProcess = $null
$unrelatedProcess = $null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message Expected=[$Expected] Actual=[$Actual]"
    }
}

function Write-JsonFile([string]$Path, $Value) {
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}

function Invoke-RuntimeUpgrade(
    [string]$RuntimeSettingsPath,
    [string]$PublicConfigPath,
    [string]$CanonicalStorageRoot,
    [string]$LogPath) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 promotes redirected native stderr to a
        # NativeCommandError. Keep the child exit code authoritative here
        # because fail-closed cases intentionally return 44.
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $guard `
            -Mode UpgradeRuntimeSettings `
            -InstalledServerPath $installedExe `
            -RuntimeSettingsPath $RuntimeSettingsPath `
            -PublicConfigPath $PublicConfigPath `
            -CanonicalStorageRoot $CanonicalStorageRoot `
            -LegacyDiscoveryPorts $legacyDiscoveryPortsArgument `
            -MigrationLogPath $LogPath 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output | Out-String).Trim()
    }
}

try {
    New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $unrelatedDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $userDataDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination $installedExe
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination $unrelatedExe
    Set-Content -LiteralPath $userDataMarker -Value 'preserve' -Encoding ascii

    $releasePublicConfig = Get-Content -LiteralPath $publicConfigPath -Raw | ConvertFrom-Json
    $publicSupabaseUrl = ([string]$releasePublicConfig.supabaseUrl).TrimEnd('/')
    $publicOrganizationId = [string]$releasePublicConfig.organizationId
    $publicPublishableKey = [string]$releasePublicConfig.publishableKey
    $canonicalStorageRoot = '%ProgramData%/ExamTransfer'

    foreach ($role in @('Teacher', 'Student')) {
        $cleanRoot = Join-Path $fixtureRoot "Clean$role"
        $runtimeSettingsPath = Join-Path $cleanRoot 'config\runtime-settings.json'
        $migrationLogPath = Join-Path $cleanRoot 'logs\installer-runtime-settings.log'
        $result = Invoke-RuntimeUpgrade `
            $runtimeSettingsPath `
            $publicConfigPath `
            $canonicalStorageRoot `
            $migrationLogPath
        Assert-Equal 0 $result.ExitCode "$role clean runtime migration failed."
        $runtime = Get-Content -LiteralPath $runtimeSettingsPath -Raw | ConvertFrom-Json
        Assert-Equal 40550 ([int]$runtime.Discovery.Port) "$role clean discovery port."
        Assert-Equal $canonicalStorageRoot ([string]$runtime.Storage.RootPath) "$role clean storage root."
        Assert-Equal $publicSupabaseUrl ([string]$runtime.Cloud.SupabaseUrl) "$role clean SupabaseUrl."
        Assert-Equal $publicPublishableKey ([string]$runtime.Cloud.PublishableKey) "$role clean publishable key."
        Assert-Equal $publicOrganizationId ([string]$runtime.Cloud.OrganizationId) "$role clean OrganizationId."
        Assert-Equal 'Production' ([string]$runtime.Cloud.Environment) "$role clean Environment."
        Assert-Equal 'UserSession' ([string]$runtime.Cloud.AccessMode) "$role clean AccessMode."
        Assert-True (Test-Path -LiteralPath $migrationLogPath -PathType Leaf) "$role migration log missing."
        Assert-Equal 0 @(
            Get-ChildItem -LiteralPath (Split-Path -Parent $runtimeSettingsPath) `
                -Filter 'runtime-settings.backup-*.json' -ErrorAction SilentlyContinue
        ).Count "$role clean install created an unnecessary backup."
    }

    $legacyVersions = @('1.3.0', '1.3.1', '1.3.2')
    for ($index = 0; $index -lt $legacyVersions.Count; $index++) {
        $version = $legacyVersions[$index]
        $legacyRoot = Join-Path $fixtureRoot "Upgrade-$version"
        $runtimeSettingsPath = Join-Path $legacyRoot 'config\runtime-settings.json'
        $migrationLogPath = Join-Path $legacyRoot 'logs\installer-runtime-settings.log'
        $legacyPort = $legacyDiscoveryPorts[[Math]::Min($index, 1)]
        $legacyCloud = [ordered]@{
            Enabled = $true
            SupabaseUrl = "https://legacy-$($version.Replace('.', '-')).supabase.co"
            PublishableKey = "sb_publishable_$($version.Replace('.', ''))12345678901234567890"
            OrganizationId = 'c2ed9ccd-b0bc-4bbf-af8e-ea4912e6f7b2'
            Environment = "Legacy-$version"
            AccessMode = 'TrustedServer'
            UseResumableUploads = $false
        }
        Write-JsonFile $runtimeSettingsPath ([ordered]@{
            InstallVersion = $version
            Discovery = [ordered]@{ Enabled = $true; Port = $legacyPort }
            Storage = [ordered]@{
                RootPath = "D:\dev\ExamTransfer_Product_$version\backend\src\ExamTransfer.LocalServer\data"
                MinFreeBytes = 123456
            }
            Cloud = $legacyCloud
            UserData = [ordered]@{ Marker = "user-$version" }
            DeviceIdentity = [ordered]@{ Id = "device-$version"; KeyId = "key-$version" }
        })
        $originalBytes = [IO.File]::ReadAllBytes($runtimeSettingsPath)

        $result = Invoke-RuntimeUpgrade `
            $runtimeSettingsPath `
            $publicConfigPath `
            $canonicalStorageRoot `
            $migrationLogPath
        Assert-Equal 0 $result.ExitCode "Teacher upgrade $version failed."
        $runtime = Get-Content -LiteralPath $runtimeSettingsPath -Raw | ConvertFrom-Json
        Assert-Equal 40550 ([int]$runtime.Discovery.Port) "Teacher upgrade $version discovery port."
        Assert-Equal $canonicalStorageRoot ([string]$runtime.Storage.RootPath) "Teacher upgrade $version storage root."
        Assert-Equal $true ([bool]$runtime.Cloud.Enabled) "Teacher upgrade $version Cloud.Enabled."
        Assert-Equal $publicSupabaseUrl ([string]$runtime.Cloud.SupabaseUrl) "Teacher upgrade $version Cloud.SupabaseUrl."
        Assert-Equal $publicPublishableKey ([string]$runtime.Cloud.PublishableKey) "Teacher upgrade $version Cloud.PublishableKey."
        Assert-Equal $publicOrganizationId ([string]$runtime.Cloud.OrganizationId) "Teacher upgrade $version Cloud.OrganizationId."
        Assert-Equal 'Production' ([string]$runtime.Cloud.Environment) "Teacher upgrade $version Cloud.Environment."
        Assert-Equal 'UserSession' ([string]$runtime.Cloud.AccessMode) "Teacher upgrade $version Cloud.AccessMode."
        Assert-Equal $false ([bool]$runtime.Cloud.UseResumableUploads) "Teacher upgrade $version unrelated Cloud field."
        Assert-Equal "user-$version" ([string]$runtime.UserData.Marker) "Teacher upgrade $version user data."
        Assert-Equal "device-$version" ([string]$runtime.DeviceIdentity.Id) "Teacher upgrade $version device identity."
        $backups = @(
            Get-ChildItem -LiteralPath (Split-Path -Parent $runtimeSettingsPath) `
                -Filter 'runtime-settings.backup-*.json'
        )
        Assert-Equal 1 $backups.Count "Teacher upgrade $version backup count."
        Assert-True (
            [Linq.Enumerable]::SequenceEqual(
                [byte[]]$originalBytes,
                [byte[]][IO.File]::ReadAllBytes($backups[0].FullName))
        ) "Teacher upgrade $version backup was not byte-exact."
        $migrationLog = Get-Content -LiteralPath $migrationLogPath -Raw
        Assert-True (
            $migrationLog.IndexOf('CLOUD_CONFIG_CONVERGED', [StringComparison]::Ordinal) -ge 0
        ) "Teacher upgrade $version did not log cloud convergence."
        Assert-True (
            $migrationLog.IndexOf($publicPublishableKey, [StringComparison]::Ordinal) -lt 0
        ) "Teacher upgrade $version logged the raw publishable key."

        $migratedHash = (Get-FileHash -LiteralPath $runtimeSettingsPath -Algorithm SHA256).Hash
        $result = Invoke-RuntimeUpgrade `
            $runtimeSettingsPath `
            $publicConfigPath `
            $canonicalStorageRoot `
            $migrationLogPath
        Assert-Equal 0 $result.ExitCode "Teacher upgrade $version idempotence rerun failed."
        Assert-Equal `
            $migratedHash `
            (Get-FileHash -LiteralPath $runtimeSettingsPath -Algorithm SHA256).Hash `
            "Teacher upgrade $version changed on idempotence rerun."
        Assert-Equal 1 @(
            Get-ChildItem -LiteralPath (Split-Path -Parent $runtimeSettingsPath) `
                -Filter 'runtime-settings.backup-*.json'
        ).Count "Teacher upgrade $version created a second backup on no-op rerun."
    }

    $studentLegacyRoot = Join-Path $fixtureRoot 'StudentLegacy'
    $studentRuntimePath = Join-Path $studentLegacyRoot 'config\runtime-settings.json'
    Write-JsonFile $studentRuntimePath ([ordered]@{
        Discovery = [ordered]@{ Port = $legacyDiscoveryPorts[1] }
        Storage = [ordered]@{ RootPath = 'E:\ExamTransferStudentData' }
        Cloud = [ordered]@{
            Enabled = $false
            SupabaseUrl = 'https://student-legacy.supabase.co'
            PublishableKey = 'sb_publishable_legacy_student_1234567890'
            OrganizationId = '180bfa10-bcca-4b2d-993f-484ff1a96a91'
            Environment = 'StudentLegacy'
            AccessMode = 'UserSession'
        }
        UserData = [ordered]@{ Marker = 'student-user-data' }
        DeviceIdentity = [ordered]@{ Id = 'student-device' }
    })
    $result = Invoke-RuntimeUpgrade `
        $studentRuntimePath `
        $publicConfigPath `
        $canonicalStorageRoot `
        (Join-Path $studentLegacyRoot 'logs\migration.log')
    Assert-Equal 0 $result.ExitCode 'Student legacy upgrade failed.'
    $studentRuntime = Get-Content -LiteralPath $studentRuntimePath -Raw | ConvertFrom-Json
    Assert-Equal 40550 ([int]$studentRuntime.Discovery.Port) 'Student legacy port.'
    Assert-Equal 'E:\ExamTransferStudentData' ([string]$studentRuntime.Storage.RootPath) 'Custom student storage must be preserved.'
    Assert-Equal $publicSupabaseUrl ([string]$studentRuntime.Cloud.SupabaseUrl) 'Student legacy SupabaseUrl.'
    Assert-Equal $publicPublishableKey ([string]$studentRuntime.Cloud.PublishableKey) 'Student legacy PublishableKey.'
    Assert-Equal $publicOrganizationId ([string]$studentRuntime.Cloud.OrganizationId) 'Student legacy OrganizationId.'
    Assert-Equal 'student-user-data' ([string]$studentRuntime.UserData.Marker) 'Student user data.'
    Assert-Equal 'student-device' ([string]$studentRuntime.DeviceIdentity.Id) 'Student device identity.'

    $failureRoot = Join-Path $fixtureRoot 'FailClosed'
    $missingPublicRuntime = Join-Path $failureRoot 'missing-public.json'
    $result = Invoke-RuntimeUpgrade `
        $missingPublicRuntime `
        (Join-Path $failureRoot 'missing-public-config.json') `
        $canonicalStorageRoot `
        (Join-Path $failureRoot 'missing-public.log')
    Assert-Equal 44 $result.ExitCode 'Missing public config did not fail closed.'
    Assert-True (-not (Test-Path -LiteralPath $missingPublicRuntime)) 'Missing public config created runtime settings.'

    $secretPublicPath = Join-Path $failureRoot 'secret-public.json'
    Write-JsonFile $secretPublicPath ([ordered]@{
        supabaseUrl = 'https://secret.supabase.co'
        publishableKey = $publicPublishableKey
        organizationId = $publicOrganizationId
        serviceRoleKey = 'forbidden'
    })
    $result = Invoke-RuntimeUpgrade `
        (Join-Path $failureRoot 'secret-public-runtime.json') `
        $secretPublicPath `
        $canonicalStorageRoot `
        (Join-Path $failureRoot 'secret-public.log')
    Assert-Equal 44 $result.ExitCode 'Secret-bearing public config did not fail closed.'

    $secretRuntimePath = Join-Path $failureRoot 'secret-runtime.json'
    Write-JsonFile $secretRuntimePath ([ordered]@{
        Discovery = [ordered]@{ Port = $legacyDiscoveryPorts[0] }
        Storage = [ordered]@{ RootPath = 'D:\dev\ExamTransfer_Product\backend\src\ExamTransfer.LocalServer\data' }
        Cloud = [ordered]@{ SecretKey = 'forbidden-secret' }
    })
    $secretRuntimeHash = (Get-FileHash -LiteralPath $secretRuntimePath -Algorithm SHA256).Hash
    $result = Invoke-RuntimeUpgrade `
        $secretRuntimePath `
        $publicConfigPath `
        $canonicalStorageRoot `
        (Join-Path $failureRoot 'secret-runtime.log')
    Assert-Equal 44 $result.ExitCode 'Secret-bearing runtime config did not fail closed.'
    Assert-Equal `
        $secretRuntimeHash `
        (Get-FileHash -LiteralPath $secretRuntimePath -Algorithm SHA256).Hash `
        'Secret-bearing runtime config was modified.'

    foreach ($requiredInstallerContract in @(
        'UpgradeRuntimeSettings',
        'RUNTIME_SETTINGS_UPGRADE_FAILED',
        'InstallValidationExitCode := 44',
        'GetCustomSetupExitCode',
        'Check: CanLaunchClient',
        'Attribs: readonly; Flags: ignoreversion overwritereadonly uninsremovereadonly',
        'Type: files; Name: "{app}\install-role.ini"',
        'Type: dirifempty; Name: "{app}"',
        'installer-localserver-guard.log',
        'Source: "{#MyReleaseRoot}\Server\*"',
        'RunLocalServerGuard(''StopOnly''',
        'RunLocalServerGuard(''VerifyInstalledPayload''',
        'RunLocalServerGuard(''CheckDowngrade''',
        ("ExamTransfer UDP {0}" -f $legacyDiscoveryPorts[0]),
        'ExamTransfer UDP 40550',
        'protocol=UDP localport=40550',
        'protocol=TCP localport=5048')) {
        Assert-True (
            $installerSource.IndexOf(
                $requiredInstallerContract,
                [StringComparison]::Ordinal) -ge 0
        ) "Installer contract missing: $requiredInstallerContract"
    }
    foreach ($forbiddenInstallerContract in @(
        'RunLocalServerGuard(''StartAndVerify''',
        '[Types]',
        '[Components]',
        'IsStudentOnlyInstall',
        'WizardIsComponentSelected',
        'SetIniString(''Install'', ''Role''')) {
        Assert-True (
            $installerSource.IndexOf(
                $forbiddenInstallerContract,
                [StringComparison]::Ordinal) -lt 0
        ) "Installer still contains split-role contract: $forbiddenInstallerContract"
    }

    $buildReleaseSource = Get-Content -LiteralPath (
        Join-Path (Split-Path -Parent $PSScriptRoot) 'build-release.ps1') -Raw
    foreach ($requiredReleaseContract in @(
        'EXAMTRANSFER_ORGANIZATION_ID',
        'New-PublicCloudConfig',
        'Read-PublicCloudConfig',
        'post-ISCC-release-payload',
        'test-installer-public-config-clean-install.ps1',
        'installer\assets\Khoa-DT-KTMT.ico',
        'Khoa-DT-KTMT-Setup-$Version-$shortCommit.exe')) {
        Assert-True (
            $buildReleaseSource.IndexOf(
                $requiredReleaseContract,
                [StringComparison]::Ordinal) -ge 0
        ) "Release config contract missing: $requiredReleaseContract"
    }
    $publicConfigPackagingSource = Get-Content -LiteralPath (
        Join-Path $PSScriptRoot 'public-config-packaging.ps1') -Raw
    foreach ($requiredValidationContract in @(
        'PUBLICCLOUD_INVALID_URL',
        'PUBLICCLOUD_INVALID_PUBLISHABLE_KEY',
        'PUBLICCLOUD_INVALID_ORGANIZATION_ID',
        'organizationId = $parsedOrganizationId.ToString(''D'')')) {
        Assert-True (
            $publicConfigPackagingSource.IndexOf(
                $requiredValidationContract,
                [StringComparison]::Ordinal) -ge 0
        ) "Public-config validation contract missing: $requiredValidationContract"
    }
    Assert-True (
        $buildReleaseSource.IndexOf(
            'scripts\build-release.ps1',
            [StringComparison]::Ordinal) -lt 0
    ) 'Root release entry point still delegates to a second build implementation.'

    $arguments = @('-t', '127.0.0.1')
    $installedProcess = Start-Process `
        -FilePath $installedExe `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -PassThru
    $unrelatedProcess = Start-Process `
        -FilePath $unrelatedExe `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Milliseconds 500

    & powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $guard `
        -Mode StopOnly `
        -InstalledServerPath $installedExe
    if ($LASTEXITCODE -ne 0) {
        throw "StopOnly guard failed with exit code $LASTEXITCODE."
    }

    [void]$installedProcess.WaitForExit(5000)
    if (-not $installedProcess.HasExited) {
        throw 'Exact installed fixture process was not stopped.'
    }
    if ($unrelatedProcess.HasExited) {
        throw 'Unrelated same-name process was stopped.'
    }
    if (-not (Test-Path -LiteralPath $userDataMarker -PathType Leaf)) {
        throw 'User data fixture was removed.'
    }


    # VerifyInstalledPayload tests
    $verifyPayloadManifest = Join-Path $fixtureRoot 'verify-manifest.json'
    $verifyPayloadClientDir = Join-Path $fixtureRoot 'Client'
    $verifyPayloadServerDir = Join-Path $fixtureRoot 'Server'
    New-Item -ItemType Directory -Path $verifyPayloadClientDir -Force | Out-Null
    New-Item -ItemType Directory -Path $verifyPayloadServerDir -Force | Out-Null
    
    $clientFile = Join-Path $verifyPayloadClientDir 'ExamTransfer.Desktop.exe'
    $serverFile = Join-Path $verifyPayloadServerDir 'ExamTransfer.LocalServer.exe'
    
    [IO.File]::WriteAllBytes($clientFile, [byte[]]@(1, 2, 3))
    [IO.File]::WriteAllBytes($serverFile, [byte[]]@(4, 5, 6))
    
    $clientHash = (Get-FileHash -LiteralPath $clientFile -Algorithm SHA256).Hash
    $serverHash = (Get-FileHash -LiteralPath $serverFile -Algorithm SHA256).Hash
    
    $manifestObj = [ordered]@{
        buildId = '1.6.5+test'
        discoveryProtocol = 'ExamTransfer/2'
        discoveryUdpPort = 40550
        client = [ordered]@{ file = 'Client/ExamTransfer.Desktop.exe'; sha256 = $clientHash }
        server = [ordered]@{ file = 'Server/ExamTransfer.LocalServer.exe'; sha256 = $serverHash }
    }
    Write-JsonFile $verifyPayloadManifest $manifestObj
    
    $processCountBefore = @(Get-Process -Name 'ExamTransfer.LocalServer' -ErrorAction SilentlyContinue).Count
    
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath $verifyPayloadManifest -InstalledServerPath $serverFile
    if ($LASTEXITCODE -ne 0) { throw 'Valid payload failed.' }
    
    $processCountAfter = @(Get-Process -Name 'ExamTransfer.LocalServer' -ErrorAction SilentlyContinue).Count
    Assert-Equal $processCountBefore $processCountAfter 'VerifyInstalledPayload started a Local Server process.'
    
    # client sai hash
    [IO.File]::WriteAllBytes($clientFile, [byte[]]@(1, 2, 3, 4))
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath $verifyPayloadManifest -InstalledServerPath $serverFile
    Assert-Equal 43 $LASTEXITCODE 'Client hash mismatch did not fail.'
    [IO.File]::WriteAllBytes($clientFile, [byte[]]@(1, 2, 3))
    
    # server sai hash
    [IO.File]::WriteAllBytes($serverFile, [byte[]]@(4, 5, 6, 7))
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath $verifyPayloadManifest -InstalledServerPath $serverFile
    Assert-Equal 43 $LASTEXITCODE 'Server hash mismatch did not fail.'
    [IO.File]::WriteAllBytes($serverFile, [byte[]]@(4, 5, 6))
    
    # thiếu manifest
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath (Join-Path $fixtureRoot 'missing.json') -InstalledServerPath $serverFile
    Assert-Equal 43 $LASTEXITCODE 'Missing manifest did not fail.'
    
    # thiếu client
    Remove-Item -LiteralPath $clientFile -Force
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath $verifyPayloadManifest -InstalledServerPath $serverFile
    Assert-Equal 43 $LASTEXITCODE 'Missing client did not fail.'
    [IO.File]::WriteAllBytes($clientFile, [byte[]]@(1, 2, 3))
    
    # thiếu server
    Remove-Item -LiteralPath $serverFile -Force
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath $verifyPayloadManifest -InstalledServerPath $serverFile
    Assert-Equal 43 $LASTEXITCODE 'Missing server did not fail.'
    [IO.File]::WriteAllBytes($serverFile, [byte[]]@(4, 5, 6))
    
    # json lỗi
    [IO.File]::WriteAllText($verifyPayloadManifest, "{ invalid json }")
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath $verifyPayloadManifest -InstalledServerPath $serverFile
    Assert-Equal 43 $LASTEXITCODE 'Invalid JSON manifest did not fail.'
    
    # thiếu buildId
    $manifestObj.Remove('buildId')
    Write-JsonFile $verifyPayloadManifest $manifestObj
    & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Mode VerifyInstalledPayload -ManifestPath $verifyPayloadManifest -InstalledServerPath $serverFile
    Assert-Equal 43 $LASTEXITCODE 'Missing buildId did not fail.'
    
    Write-Host 'PASS code=INSTALLER_VERIFY_PAYLOAD all payload validation contracts passed.' -ForegroundColor Green

    Write-Host 'PASS code=INSTALLER_EXACT_PATH_PROCESS_GUARD unrelated_same_name=preserved user_data=preserved' -ForegroundColor Green
    Write-Host 'PASS code=RUNTIME_SETTINGS_CLEAN_INSTALL role=unified port=40550 storage=programdata cloud=release-payload fixture-config=not-used' -ForegroundColor Green
    Write-Host 'PASS code=RUNTIME_SETTINGS_UPGRADE versions=1.3.0,1.3.1,1.3.2 backup=exact cloud=release-converged user_data=preserved device=preserved' -ForegroundColor Green
    Write-Host 'PASS code=RUNTIME_SETTINGS_IDEMPOTENT student_legacy=normalized custom_storage=preserved secrets=fail-closed' -ForegroundColor Green
    Write-Host 'PASS code=INSTALLER_STATIC_CONTRACT unified=client+server role_source=authenticated_profile autostart=disabled firewall=TCP5048+UDP40550 legacy_udp=removed' -ForegroundColor Green
}
finally {
    foreach ($process in @($installedProcess, $unrelatedProcess)) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            [void]$process.WaitForExit(5000)
        }
        if ($null -ne $process) {
            $process.Dispose()
        }
    }

    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedFixture).StartsWith(
            'ExamTransfer-InstallerGuard-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
