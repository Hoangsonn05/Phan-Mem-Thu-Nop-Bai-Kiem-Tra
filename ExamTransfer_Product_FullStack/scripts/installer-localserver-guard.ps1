param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('CheckDowngrade', 'StopOnly', 'StopAndPreflight', 'StartAndVerify', 'UpgradeRuntimeSettings')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$InstalledServerPath,

    [string]$ManifestPath,

    [string]$RuntimeSettingsPath,

    [string]$PublicConfigPath,

    [string]$CanonicalStorageRoot = '%ProgramData%/ExamTransfer',

    [string]$LegacyDiscoveryPorts,

    [string]$MigrationLogPath,

    [string]$DiagnosticLogPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serverPort = 5048
$discoveryPort = 40550
$protocol = 'ExamTransfer/2'
$expectedServerName = 'ExamTransfer.LocalServer.exe'

function Write-MigrationLog([string]$Code, [string]$Message) {
    $line = '{0} {1} {2}' -f [DateTime]::UtcNow.ToString('o'), $Code, $Message
    Write-Host $line
    if ([string]::IsNullOrWhiteSpace($MigrationLogPath)) {
        return
    }

    $logDirectory = Split-Path -Parent $MigrationLogPath
    if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
        [IO.Directory]::CreateDirectory($logDirectory) | Out-Null
    }
    [IO.File]::AppendAllText(
        $MigrationLogPath,
        $line + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}

function Protect-DiagnosticText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return '<no diagnostic>'
    }
    $protected = $Text -replace '(?i)\b(sb_secret_)[A-Za-z0-9_-]+', '$1[REDACTED]'
    $protected = $protected -replace '(?i)\b(Bearer\s+)[A-Za-z0-9._-]+', '$1[REDACTED]'
    $protected = $protected -replace '(?i)\b(password|secret|access_token|refresh_token)\s*[:=]\s*\S+', '$1=[REDACTED]'
    if ($protected.Length -gt 2000) {
        return $protected.Substring(0, 2000) + '...[truncated]'
    }
    return $protected
}

function Write-GuardLog([string]$Code, [string]$Message) {
    $safeMessage = Protect-DiagnosticText $Message
    $line = '{0} {1} {2}' -f [DateTime]::UtcNow.ToString('o'), $Code, $safeMessage
    if ([string]::IsNullOrWhiteSpace($DiagnosticLogPath)) {
        return
    }

    $logDirectory = Split-Path -Parent $DiagnosticLogPath
    if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
        [IO.Directory]::CreateDirectory($logDirectory) | Out-Null
    }
    [IO.File]::AppendAllText(
        $DiagnosticLogPath,
        $line + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}

function Resolve-ExactPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Installed server path is required.'
    }
    return [IO.Path]::GetFullPath($Path)
}

function Test-PublishableKey([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -match '(?i)service_role|sb_secret_|placeholder|change[-_ ]?me|example') {
        return $false
    }
    if ($Value.StartsWith('sb_publishable_', [StringComparison]::Ordinal)) {
        return $Value.Length -gt 31
    }

    $segments = $Value.Split('.')
    if ($segments.Count -ne 3) {
        return $false
    }
    try {
        $payload = $segments[1].Replace('-', '+').Replace('_', '/')
        $payload = $payload.PadRight(
            $payload.Length + ((4 - ($payload.Length % 4)) % 4),
            '=')
        $json = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($payload)) | ConvertFrom-Json
        return [string]$json.role -ceq 'anon'
    }
    catch {
        return $false
    }
}

function Read-PublicConfig([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'PUBLIC_CONFIG_MISSING: publiccloud.runtime.json was not found.'
    }

    try {
        $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw 'PUBLIC_CONFIG_INVALID_JSON: publiccloud.runtime.json is not valid JSON.'
    }
    if ($null -eq $config -or $config -isnot [pscustomobject]) {
        throw 'PUBLIC_CONFIG_INVALID_ROOT: publiccloud.runtime.json must contain an object.'
    }

    $allowed = @('supabaseUrl', 'publishableKey', 'organizationId')
    foreach ($property in $config.PSObject.Properties) {
        if ($allowed -cnotcontains $property.Name) {
            throw "PUBLIC_CONFIG_FORBIDDEN_FIELD: $($property.Name)"
        }
    }
    foreach ($required in $allowed) {
        if ($null -eq $config.PSObject.Properties[$required] -or
            [string]::IsNullOrWhiteSpace([string]$config.$required)) {
            throw "PUBLIC_CONFIG_REQUIRED_FIELD_MISSING: $required"
        }
    }

    $uri = $null
    if (-not [Uri]::TryCreate(
            ([string]$config.supabaseUrl).Trim(),
            [UriKind]::Absolute,
            [ref]$uri) -or
        $uri.Scheme -cne 'https') {
        throw 'PUBLIC_CONFIG_INVALID_URL: SupabaseUrl must use HTTPS.'
    }
    if (-not (Test-PublishableKey ([string]$config.publishableKey).Trim())) {
        throw 'PUBLIC_CONFIG_INVALID_PUBLISHABLE_KEY: secret and service-role keys are rejected.'
    }
    $organizationId = [guid]::Empty
    if (-not [guid]::TryParse(
            ([string]$config.organizationId).Trim(),
            [ref]$organizationId) -or
        $organizationId -eq [guid]::Empty) {
        throw 'PUBLIC_CONFIG_INVALID_ORGANIZATION_ID: OrganizationId must be a non-empty UUID.'
    }

    return [pscustomobject]@{
        SupabaseUrl = $uri.AbsoluteUri.TrimEnd('/')
        PublishableKey = ([string]$config.publishableKey).Trim()
        OrganizationId = $organizationId.ToString('D')
    }
}

function Assert-NoEmbeddedSecrets($Node, [string]$JsonPath = '$') {
    if ($null -eq $Node) {
        return
    }
    if ($Node -is [pscustomobject]) {
        foreach ($property in $Node.PSObject.Properties) {
            $name = [string]$property.Name
            if ($name -notmatch '(?i)EnvironmentVariable$' -and
                $name -match '(?i)^(ServiceRoleKey|SecretKey|DatabasePassword|AccessToken|RefreshToken|Jwt)$') {
                throw "RUNTIME_CONFIG_FORBIDDEN_SECRET_FIELD: $JsonPath.$name"
            }
            Assert-NoEmbeddedSecrets $property.Value "$JsonPath.$name"
        }
        return
    }
    if ($Node -is [Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            Assert-NoEmbeddedSecrets $Node[$key] "$JsonPath.$key"
        }
        return
    }
    if ($Node -is [Collections.IEnumerable] -and $Node -isnot [string]) {
        $index = 0
        foreach ($item in $Node) {
            Assert-NoEmbeddedSecrets $item "$JsonPath[$index]"
            $index++
        }
        return
    }
    if ($Node -is [string] -and $Node.StartsWith('sb_secret_', [StringComparison]::OrdinalIgnoreCase)) {
        throw "RUNTIME_CONFIG_FORBIDDEN_SECRET_VALUE: $JsonPath"
    }
}

function Ensure-ObjectProperty($Parent, [string]$Name, [ref]$Changed) {
    $property = $Parent.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        $value = [pscustomobject]@{}
        if ($null -eq $property) {
            $Parent | Add-Member -NotePropertyName $Name -NotePropertyValue $value
        }
        else {
            $property.Value = $value
        }
        $Changed.Value = $true
        return $value
    }
    if ($property.Value -isnot [pscustomobject]) {
        throw "RUNTIME_CONFIG_INVALID_SECTION: $Name must be an object."
    }
    return $property.Value
}

function Set-MissingProperty(
    $Parent,
    [string]$Name,
    $Value,
    [ref]$Changed) {
    $property = $Parent.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Parent | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
        $Changed.Value = $true
        return
    }
    if ($null -eq $property.Value -or
        ($property.Value -is [string] -and
            [string]::IsNullOrWhiteSpace([string]$property.Value))) {
        $property.Value = $Value
        $Changed.Value = $true
    }
}

function Set-AuthoritativeProperty(
    $Parent,
    [string]$Name,
    $Value,
    [ref]$Changed) {
    $property = $Parent.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Parent | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
        $Changed.Value = $true
        return
    }

    $equal = if ($Value -is [string]) {
        [string]::Equals(
            [string]$property.Value,
            [string]$Value,
            [StringComparison]::Ordinal)
    }
    else {
        [object]::Equals($property.Value, $Value)
    }
    if (-not $equal) {
        $property.Value = $Value
        $Changed.Value = $true
    }
}

function Get-ConfigurationHost([string]$Value) {
    $uri = $null
    if ([Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) {
        return $uri.Host
    }
    return '<missing>'
}

function Assert-RuntimeCloudConverged($Document, $PublicConfig) {
    $cloudProperty = $Document.PSObject.Properties['Cloud']
    if ($null -eq $cloudProperty -or $cloudProperty.Value -isnot [pscustomobject]) {
        throw 'RUNTIME_CONFIG_CLOUD_INVALID: Cloud must be an object.'
    }
    $cloud = $cloudProperty.Value
    foreach ($expected in @(
        @{ Name = 'Enabled'; Value = $true },
        @{ Name = 'SupabaseUrl'; Value = $PublicConfig.SupabaseUrl },
        @{ Name = 'PublishableKey'; Value = $PublicConfig.PublishableKey },
        @{ Name = 'OrganizationId'; Value = $PublicConfig.OrganizationId },
        @{ Name = 'Environment'; Value = 'Production' },
        @{ Name = 'AccessMode'; Value = 'UserSession' })) {
        $property = $cloud.PSObject.Properties[$expected.Name]
        if ($null -eq $property -or
            -not [string]::Equals(
                [string]$property.Value,
                [string]$expected.Value,
                [StringComparison]::Ordinal)) {
            throw "RUNTIME_CONFIG_CLOUD_MISMATCH: Cloud.$($expected.Name)"
        }
    }
    if ($null -ne $cloud.PSObject.Properties['AnonKey']) {
        throw 'RUNTIME_CONFIG_CLOUD_MISMATCH: legacy Cloud.AnonKey must be removed.'
    }
}

function Test-SourceBoundStoragePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $true
    }
    if ([string]::Equals(
            $Path.Trim().TrimEnd('\', '/'),
            $CanonicalStorageRoot.Trim().TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $normalized = $Path.Trim().Replace('/', '\')
    return $normalized -match '(?i)(\\backend\\src\\ExamTransfer\.LocalServer(?:\\|$)|\\ExamTransfer\.LocalServer\\bin\\(?:Debug|Release)(?:\\|$)|\\artifacts\\(?:release|[^\\]+-audit|[^\\]+-fix)(?:\\|$)|\\ExamTransfer_(?:FullStack|Product)[^\\]*(?:\\|$))'
}

function Resolve-LegacyDiscoveryPorts {
    if ([string]::IsNullOrWhiteSpace($LegacyDiscoveryPorts)) {
        throw 'RUNTIME_CONFIG_LEGACY_PORTS_MISSING: LegacyDiscoveryPorts is required.'
    }

    $ports = @()
    foreach ($token in $LegacyDiscoveryPorts.Split(',')) {
        $port = 0
        if (-not [int]::TryParse($token.Trim(), [ref]$port) -or
            $port -lt 1 -or
            $port -gt 65535 -or
            $port -eq $discoveryPort) {
            throw "RUNTIME_CONFIG_LEGACY_PORT_INVALID: $token"
        }
        $ports += $port
    }
    if ($ports.Count -eq 0 -or @($ports | Select-Object -Unique).Count -ne $ports.Count) {
        throw 'RUNTIME_CONFIG_LEGACY_PORTS_INVALID: ports must be non-empty and unique.'
    }
    return $ports
}

function Write-RuntimeSettingsAtomically(
    [string]$Path,
    $Document,
    [bool]$ExistingFile,
    [string]$RollbackPath,
    $PublicConfig) {
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = Join-Path $directory (
        '.runtime-settings.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $replacementBackupPath = Join-Path $directory (
        '.runtime-settings.' + [Guid]::NewGuid().ToString('N') + '.replaced')
    try {
        $json = $Document | ConvertTo-Json -Depth 100
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            (New-Object Text.UTF8Encoding($false)))
        $temporaryDocument = Get-Content -LiteralPath $temporaryPath -Raw |
            ConvertFrom-Json
        Assert-NoEmbeddedSecrets $temporaryDocument
        Assert-RuntimeCloudConverged $temporaryDocument $PublicConfig
        if ($ExistingFile) {
            [IO.File]::Replace(
                $temporaryPath,
                $Path,
                $replacementBackupPath,
                $true)
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }

        $writtenDocument = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        Assert-NoEmbeddedSecrets $writtenDocument
        Assert-RuntimeCloudConverged $writtenDocument $PublicConfig
    }
    catch {
        if ($ExistingFile -and
            -not [string]::IsNullOrWhiteSpace($RollbackPath) -and
            (Test-Path -LiteralPath $RollbackPath -PathType Leaf)) {
            $rollbackTemporaryPath = Join-Path $directory (
                '.runtime-settings.' + [Guid]::NewGuid().ToString('N') + '.rollback')
            try {
                [IO.File]::Copy($RollbackPath, $rollbackTemporaryPath, $true)
                [IO.File]::Replace($rollbackTemporaryPath, $Path, $null, $true)
            }
            finally {
                if (Test-Path -LiteralPath $rollbackTemporaryPath -PathType Leaf) {
                    Remove-Item -LiteralPath $rollbackTemporaryPath -Force -ErrorAction SilentlyContinue
                }
            }
        }
        elseif (-not $ExistingFile -and (Test-Path -LiteralPath $Path -PathType Leaf)) {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $replacementBackupPath -PathType Leaf) {
            Remove-Item -LiteralPath $replacementBackupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Upgrade-RuntimeSettings {
    if ([string]::IsNullOrWhiteSpace($RuntimeSettingsPath)) {
        throw 'RUNTIME_CONFIG_PATH_MISSING: RuntimeSettingsPath is required.'
    }
    if ([string]::IsNullOrWhiteSpace($CanonicalStorageRoot)) {
        throw 'RUNTIME_CONFIG_STORAGE_TARGET_MISSING: CanonicalStorageRoot is required.'
    }

    $publicConfig = Read-PublicConfig $PublicConfigPath
    $existingFile = Test-Path -LiteralPath $RuntimeSettingsPath -PathType Leaf
    if ($existingFile) {
        try {
            $document = Get-Content -LiteralPath $RuntimeSettingsPath -Raw |
                ConvertFrom-Json
        }
        catch {
            throw 'RUNTIME_CONFIG_INVALID_JSON: runtime-settings.json is not valid JSON.'
        }
        if ($null -eq $document -or $document -isnot [pscustomobject]) {
            throw 'RUNTIME_CONFIG_INVALID_ROOT: runtime-settings.json must contain an object.'
        }
    }
    else {
        $document = [pscustomobject]@{}
    }

    Assert-NoEmbeddedSecrets $document
    $changed = -not $existingFile
    $legacyPorts = @(Resolve-LegacyDiscoveryPorts)

    $discovery = Ensure-ObjectProperty $document 'Discovery' ([ref]$changed)
    $portProperty = $discovery.PSObject.Properties['Port']
    if ($null -eq $portProperty -or $null -eq $portProperty.Value) {
        Set-MissingProperty $discovery 'Port' $discoveryPort ([ref]$changed)
    }
    else {
        $configuredPort = 0
        if (-not [int]::TryParse([string]$portProperty.Value, [ref]$configuredPort)) {
            throw 'RUNTIME_CONFIG_DISCOVERY_PORT_INVALID: Discovery.Port must be numeric.'
        }
        if ($configuredPort -in $legacyPorts) {
            $portProperty.Value = $discoveryPort
            $changed = $true
        }
        elseif ($configuredPort -ne $discoveryPort) {
            throw "RUNTIME_CONFIG_DISCOVERY_PORT_UNSUPPORTED: $configuredPort"
        }
    }

    $storage = Ensure-ObjectProperty $document 'Storage' ([ref]$changed)
    $rootPathProperty = $storage.PSObject.Properties['RootPath']
    if ($null -eq $rootPathProperty) {
        $storage | Add-Member -NotePropertyName 'RootPath' -NotePropertyValue $CanonicalStorageRoot
        $changed = $true
    }
    elseif (Test-SourceBoundStoragePath ([string]$rootPathProperty.Value)) {
        $rootPathProperty.Value = $CanonicalStorageRoot
        $changed = $true
    }

    $cloud = Ensure-ObjectProperty $document 'Cloud' ([ref]$changed)
    $oldUrlProperty = $cloud.PSObject.Properties['SupabaseUrl']
    $oldOrganizationProperty = $cloud.PSObject.Properties['OrganizationId']
    $oldKeyProperty = $cloud.PSObject.Properties['PublishableKey']
    $oldSupabaseUrl = if ($null -eq $oldUrlProperty) { '' } else { [string]$oldUrlProperty.Value }
    $oldOrganizationId = if ($null -eq $oldOrganizationProperty) { '' } else { [string]$oldOrganizationProperty.Value }
    $oldPublishableKey = if ($null -eq $oldKeyProperty) { '' } else { [string]$oldKeyProperty.Value }
    $cloudIdentityChanged = -not [string]::Equals(
            $oldSupabaseUrl.TrimEnd('/'),
            $publicConfig.SupabaseUrl,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $oldOrganizationId,
            $publicConfig.OrganizationId,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $oldPublishableKey,
            $publicConfig.PublishableKey,
            [StringComparison]::Ordinal)

    Set-AuthoritativeProperty $cloud 'Enabled' $true ([ref]$changed)
    Set-AuthoritativeProperty $cloud 'SupabaseUrl' $publicConfig.SupabaseUrl ([ref]$changed)
    Set-AuthoritativeProperty $cloud 'PublishableKey' $publicConfig.PublishableKey ([ref]$changed)
    Set-AuthoritativeProperty $cloud 'OrganizationId' $publicConfig.OrganizationId ([ref]$changed)
    Set-AuthoritativeProperty $cloud 'Environment' 'Production' ([ref]$changed)
    Set-AuthoritativeProperty $cloud 'AccessMode' 'UserSession' ([ref]$changed)
    if ($null -ne $cloud.PSObject.Properties['AnonKey']) {
        $cloud.PSObject.Properties.Remove('AnonKey')
        $changed = $true
    }
    Assert-NoEmbeddedSecrets $document

    if (-not $changed) {
        Write-MigrationLog 'RUNTIME_SETTINGS_UNCHANGED' 'No migration was required.'
        return
    }

    $backupPath = $null
    if ($existingFile) {
        $timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
        $backupPath = Join-Path (
            Split-Path -Parent $RuntimeSettingsPath) (
            'runtime-settings.backup-' + $timestamp + '.json')
        Copy-Item -LiteralPath $RuntimeSettingsPath -Destination $backupPath
    }

    Write-RuntimeSettingsAtomically `
        $RuntimeSettingsPath `
        $document `
        $existingFile `
        $backupPath `
        $publicConfig
    $backupMessage = if ($null -eq $backupPath) {
        'clean-config-created'
    }
    else {
        'backup=' + [IO.Path]::GetFileName($backupPath)
    }
    Write-MigrationLog 'RUNTIME_SETTINGS_UPGRADED' $backupMessage
    if ($cloudIdentityChanged) {
        Write-MigrationLog `
            'CLOUD_CONFIG_CONVERGED' `
            ("oldHost=$(Get-ConfigurationHost $oldSupabaseUrl); " +
             "newHost=$(Get-ConfigurationHost $publicConfig.SupabaseUrl); " +
             "oldOrganizationId=$oldOrganizationId; " +
             "newOrganizationId=$($publicConfig.OrganizationId)")
    }
}

function Get-ExactInstalledServerProcess([string]$ExactPath) {
    @(Get-CimInstance Win32_Process -Filter "Name='$expectedServerName'" -ErrorAction Stop |
        Where-Object {
            $_.ExecutablePath -and
            [string]::Equals(
                [IO.Path]::GetFullPath([string]$_.ExecutablePath),
                $ExactPath,
                [StringComparison]::OrdinalIgnoreCase)
        })
}

function Stop-ExactInstalledServer([string]$ExactPath) {
    $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    foreach ($process in @(Get-ExactInstalledServerProcess $ExactPath)) {
        $processId = [int]$process.ProcessId
        & $taskkill /PID $processId
        $requestExit = $LASTEXITCODE
        if ($requestExit -ne 0) {
            Write-Warning "Graceful stop request failed for exact installed Local Server PID $processId (exit $requestExit)."
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
                break
            }
            Start-Sleep -Milliseconds 200
        }

        $stillExact = @(Get-ExactInstalledServerProcess $ExactPath |
            Where-Object { [int]$_.ProcessId -eq $processId })
        if ($stillExact.Count -gt 0) {
            & $taskkill /F /PID $processId
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to stop exact installed Local Server PID $processId."
            }
        }
    }
}

function Get-ProcessPathSafe([int]$ProcessId) {
    try {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
        if ($process -and $process.ExecutablePath) {
            return [string]$process.ExecutablePath
        }
    }
    catch {
    }
    return '<unavailable>'
}

function Assert-PortsAvailable {
    $tcpOwners = @(Get-NetTCPConnection -LocalPort $serverPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($tcpOwners.Count -gt 0) {
        foreach ($ownerId in $tcpOwners) {
            $msg = "PORT_CONFLICT_TCP_5048 PID=$ownerId PATH=$(Get-ProcessPathSafe ([int]$ownerId))"
            [Console]::Error.WriteLine($msg)
            Write-GuardLog 'PORT_CONFLICT' $msg
        }
        exit 41
    }

    $udpOwners = @(Get-NetUDPEndpoint -LocalPort $discoveryPort -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($udpOwners.Count -gt 0) {
        foreach ($ownerId in $udpOwners) {
            $msg = "PORT_CONFLICT_UDP_40550 PID=$ownerId PATH=$(Get-ProcessPathSafe ([int]$ownerId))"
            [Console]::Error.WriteLine($msg)
            Write-GuardLog 'PORT_CONFLICT' $msg
        }
        exit 42
    }
}

function Read-Manifest([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release manifest not found: $Path"
    }
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $manifest.buildId -or
        $manifest.discoveryProtocol -ne $protocol -or
        [int]$manifest.discoveryUdpPort -ne $discoveryPort) {
        throw 'Release manifest identity is invalid.'
    }
    return $manifest
}

function Assert-InstalledHashes($Manifest, [string]$ManifestFile) {
    $installRoot = Split-Path -Parent $ManifestFile
    $clientPath = Join-Path $installRoot ([string]$Manifest.client.file -replace '/', '\')
    $serverPath = Join-Path $installRoot ([string]$Manifest.server.file -replace '/', '\')
    foreach ($entry in @(
        @{ Path = $clientPath; Hash = [string]$Manifest.client.sha256; Name = 'client' },
        @{ Path = $serverPath; Hash = [string]$Manifest.server.sha256; Name = 'server' }
    )) {
        if (-not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) {
            throw "Installed $($entry.Name) binary is missing: $($entry.Path)"
        }
        $actualHash = (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $entry.Hash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed $($entry.Name) hash does not match release-manifest.json."
        }
    }
}

function Start-AndVerify(
    [string]$ExactPath,
    [string]$ReleaseManifestPath) {
    if (-not (Test-Path -LiteralPath $ExactPath -PathType Leaf)) {
        throw "Installed Local Server is missing: $ExactPath"
    }
    $manifest = Read-Manifest $ReleaseManifestPath
    Assert-InstalledHashes $manifest $ReleaseManifestPath
    Assert-PortsAvailable

    $serverErrorPath = if ([string]::IsNullOrWhiteSpace($DiagnosticLogPath)) {
        Join-Path ([IO.Path]::GetTempPath()) (
            'ExamTransfer-InstallerGuard-' + [Guid]::NewGuid().ToString('N') + '.stderr.log')
    }
    else {
        $diagnosticDirectory = Split-Path -Parent $DiagnosticLogPath
        [IO.Directory]::CreateDirectory($diagnosticDirectory) | Out-Null
        Join-Path $diagnosticDirectory 'installer-localserver.stderr.log'
    }
    $started = Start-Process `
        -FilePath $ExactPath `
        -WorkingDirectory (Split-Path -Parent $ExactPath) `
        -WindowStyle Hidden `
        -RedirectStandardError $serverErrorPath `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    $attempt = 0
    $lastDiagnostic = 'health and identity endpoints did not become ready'
    while ([DateTime]::UtcNow -lt $deadline) {
        $attempt++
        if ($started.HasExited) {
            $serverDiagnostic = if (Test-Path -LiteralPath $serverErrorPath -PathType Leaf) {
                (Get-Content -LiteralPath $serverErrorPath -Tail 20 | Out-String).Trim()
            }
            else {
                '<stderr unavailable>'
            }
            throw "LOCAL_SERVER_EXITED_BEFORE_VERIFICATION: exit=$($started.ExitCode); stderr=$(Protect-DiagnosticText $serverDiagnostic)"
        }
        try {
            $health = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$serverPort/health" `
                -Method Get `
                -TimeoutSec 2
            $identityResponse = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$serverPort/api/v1/discovery/identity" `
                -Method Get `
                -TimeoutSec 2
            $identity = $identityResponse.data
            if ($health.buildId -eq $manifest.buildId -and
                $health.protocol -eq $protocol -and
                [int]$health.discoveryPort -eq $discoveryPort -and
                $identity.buildId -eq $manifest.buildId -and
                $identity.protocol -eq $protocol -and
                [int]$identity.discoveryPort -eq $discoveryPort) {
                Write-GuardLog `
                    'INSTALLER_GUARD_VERIFIED' `
                    "BuildId=$($manifest.buildId); Protocol=$protocol; UDP=$discoveryPort"
                Write-Host "ExamTransfer Local Server verified. BuildId=$($manifest.buildId); Protocol=$protocol; UDP=$discoveryPort"
                return
            }
            $lastDiagnostic =
                "identity mismatch healthBuild=$($health.buildId) identityBuild=$($identity.buildId) healthProtocol=$($health.protocol) identityProtocol=$($identity.protocol)"
        }
        catch {
            $lastDiagnostic = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 500
    }
    throw "LOCAL_SERVER_IDENTITY_TIMEOUT: attempts=$attempt; last=$lastDiagnostic"
}

function Check-Downgrade([string]$PackageManifestPath, [string]$ExactServerPath) {
    if ([string]::IsNullOrWhiteSpace($PackageManifestPath) -or -not (Test-Path -LiteralPath $PackageManifestPath -PathType Leaf)) {
        exit 46
    }
    
    try {
        $package = Get-Content -LiteralPath $PackageManifestPath -Raw | ConvertFrom-Json
        if ($null -eq $package -or $null -eq $package.semanticVersion -or $null -eq $package.builtAtUtc) {
            exit 46
        }
    }
    catch {
        exit 46
    }
    
    $installedManifestPath = Join-Path (Split-Path -Parent $ExactServerPath) '..\release-manifest.json'
    $installedManifestPath = [IO.Path]::GetFullPath($installedManifestPath)
    
    if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) {
        return
    }
    
    try {
        $installed = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
        if ($null -eq $installed -or $null -eq $installed.semanticVersion -or $null -eq $installed.builtAtUtc) {
            Write-GuardLog 'INSTALLED_MANIFEST_INVALID' 'The installed release-manifest.json is missing required fields.'
            exit 46
        }
    }
    catch {
        Write-GuardLog 'INSTALLED_MANIFEST_INVALID' 'The installed release-manifest.json could not be parsed.'
        exit 46
    }
    
    if ([string]::Equals($package.buildId, $installed.buildId, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    
    $packageVer = [version]$package.semanticVersion
    $installedVer = [version]$installed.semanticVersion
    
    if ($packageVer -gt $installedVer) {
        return
    }
    
    if ($packageVer -lt $installedVer) {
        Write-GuardLog 'INSTALLER_DOWNGRADE_BLOCKED' "Package=$($package.semanticVersion) Installed=$($installed.semanticVersion)"
        [Console]::WriteLine("INSTALLER_DOWNGRADE_BLOCKED`n`nInstalled:`n- $($installed.semanticVersion)`n- $($installed.buildId)`n- $($installed.builtAtUtc)`n`nPackage:`n- $($package.semanticVersion)`n- $($package.buildId)`n- $($package.builtAtUtc)")
        exit 45
    }
    
    $packageTime = [DateTime]::Parse($package.builtAtUtc, $null, [System.Globalization.DateTimeStyles]::RoundtripKind)
    $installedTime = [DateTime]::Parse($installed.builtAtUtc, $null, [System.Globalization.DateTimeStyles]::RoundtripKind)
    
    if ($packageTime -gt $installedTime) {
        return
    }
    
    Write-GuardLog 'INSTALLER_DOWNGRADE_BLOCKED' "Package timestamp is older or equal."
    [Console]::WriteLine("INSTALLER_DOWNGRADE_BLOCKED`n`nInstalled:`n- $($installed.semanticVersion)`n- $($installed.buildId)`n- $($installed.builtAtUtc)`n`nPackage:`n- $($package.semanticVersion)`n- $($package.buildId)`n- $($package.builtAtUtc)")
    exit 45
}

$exactServerPath = Resolve-ExactPath $InstalledServerPath

try {
    switch ($Mode) {
        'CheckDowngrade' {
            Check-Downgrade ([IO.Path]::GetFullPath($ManifestPath)) $exactServerPath
        }
        'StopOnly' {
            Stop-ExactInstalledServer $exactServerPath
        }
        'StopAndPreflight' {
            Stop-ExactInstalledServer $exactServerPath
            Assert-PortsAvailable
        }
        'StartAndVerify' {
            Start-AndVerify $exactServerPath ([IO.Path]::GetFullPath($ManifestPath))
        }
        'UpgradeRuntimeSettings' {
            Upgrade-RuntimeSettings
        }
    }
}
catch {
    $message = $_.Exception.Message
    [Console]::Error.WriteLine("INSTALLER_GUARD_FAILED: $message")
    if ($null -ne $Global:GuardLogPath) {
        Write-GuardLog 'INSTALLER_GUARD_FAILED' $message
    }
    exit 43
    if ($Mode -eq 'UpgradeRuntimeSettings') {
        try {
            Write-MigrationLog 'RUNTIME_SETTINGS_FAILED' $message
        }
        catch {
        }
        exit 44
    }
    try {
        Write-GuardLog 'INSTALLER_GUARD_FAILED' $message
    }
    catch {
    }
    exit 43
}

exit 0
