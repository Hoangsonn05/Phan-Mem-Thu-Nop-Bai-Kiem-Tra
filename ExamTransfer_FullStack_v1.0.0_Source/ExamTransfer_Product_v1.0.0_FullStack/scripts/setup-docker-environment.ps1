[CmdletBinding()]
param(
    [string]$SupabaseUrl,
    [string]$PublishableKey,
    [string]$OrganizationId,

    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Environment = 'Development',

    [ValidateSet('UserSession', 'TrustedServer')]
    [string]$AccessMode = 'UserSession',

    [string]$PreferredIp,
    [string[]]$AllowedCidrs = @(),
    [switch]$DisableCloud,
    [switch]$DisableDiscovery,
    [switch]$Force,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-RandomBase64Key {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function Assert-SingleLineValue {
    param(
        [string]$Name,
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value -match "[`r`n]") {
        throw "$Name must be a single-line value."
    }
}

function Read-PlainTextSecret {
    param([string]$Prompt)

    $secure = Read-Host $Prompt -AsSecureString
    if ($null -eq $secure -or $secure.Length -eq 0) {
        return ''
    }

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Get-ExistingCloudConfiguration {
    $programData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData)

    if ([string]::IsNullOrWhiteSpace($programData)) {
        $programData = $env:ProgramData
    }

    if ([string]::IsNullOrWhiteSpace($programData)) {
        return $null
    }

    $runtimePath = Join-Path $programData 'ExamTransfer\config\runtime-settings.json'
    if (-not (Test-Path -LiteralPath $runtimePath)) {
        return $null
    }

    try {
        $runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
        if ($null -eq $runtime -or $null -eq $runtime.Cloud) {
            return $null
        }

        return $runtime.Cloud
    }
    catch {
        Write-Warning "Could not read existing native configuration: $runtimePath"
        return $null
    }
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$targetPath = Join-Path $projectRoot '.env.docker'

if ((Test-Path -LiteralPath $targetPath) -and -not $Force) {
    throw ".env.docker already exists: $targetPath. Use -Force to replace it."
}

$existingCloud = Get-ExistingCloudConfiguration
$cloudEnabled = -not [bool]$DisableCloud
$organizationGuid = [guid]::Empty
$organizationIdText = ''

if ($cloudEnabled) {
    if ([string]::IsNullOrWhiteSpace($SupabaseUrl) -and $null -ne $existingCloud) {
        $SupabaseUrl = [string]$existingCloud.SupabaseUrl
    }

    if ([string]::IsNullOrWhiteSpace($PublishableKey) -and $null -ne $existingCloud) {
        $PublishableKey = [string]$existingCloud.PublishableKey
        if ([string]::IsNullOrWhiteSpace($PublishableKey)) {
            $PublishableKey = [string]$existingCloud.AnonKey
        }
    }

    if ([string]::IsNullOrWhiteSpace($OrganizationId) -and $null -ne $existingCloud) {
        $OrganizationId = [string]$existingCloud.OrganizationId
        if (-not [string]::IsNullOrWhiteSpace($OrganizationId)) {
            Write-Host "Found existing ExamTransfer Organization ID: $OrganizationId" -ForegroundColor Cyan
        }
    }

    if (-not $NonInteractive) {
        if ([string]::IsNullOrWhiteSpace($SupabaseUrl)) {
            $SupabaseUrl = Read-Host 'Nhap Supabase Project URL'
        }

        if ([string]::IsNullOrWhiteSpace($PublishableKey)) {
            $PublishableKey = Read-Host 'Nhap Supabase Publishable Key'
        }

        if ([string]::IsNullOrWhiteSpace($OrganizationId)) {
            $OrganizationId = Read-Host 'Nhap ExamTransfer Organization ID (UUID, khong phai Supabase project ref)'
        }
    }

    $SupabaseUrl = ([string]$SupabaseUrl).Trim().TrimEnd('/')
    $PublishableKey = ([string]$PublishableKey).Trim()
    $OrganizationId = ([string]$OrganizationId).Trim()

    if ([string]::IsNullOrWhiteSpace($SupabaseUrl)) {
        throw 'SupabaseUrl is required when cloud is enabled.'
    }

    [Uri]$parsedUri = $null
    if (-not [Uri]::TryCreate($SupabaseUrl, [UriKind]::Absolute, [ref]$parsedUri)) {
        throw 'SupabaseUrl is invalid.'
    }

    if ($null -eq $parsedUri) {
        throw 'SupabaseUrl could not be parsed.'
    }

    if ($parsedUri.Scheme -ne 'https' -and -not $parsedUri.IsLoopback) {
        throw 'SupabaseUrl must use HTTPS except for local Supabase.'
    }

    if ([string]::IsNullOrWhiteSpace($PublishableKey)) {
        throw 'PublishableKey is required when cloud is enabled.'
    }

    if ([string]::IsNullOrWhiteSpace($OrganizationId)) {
        throw 'OrganizationId is required when cloud is enabled.'
    }

    if (-not [guid]::TryParse($OrganizationId, [ref]$organizationGuid) -or
        $organizationGuid -eq [guid]::Empty) {
        throw 'OrganizationId must be a valid non-empty UUID. It is the UUID returned by bootstrap_examtransfer_organization, not the Supabase project reference or Supabase account organization slug.'
    }

    $organizationIdText = $organizationGuid.ToString()
}

$secretKey = ''
$serviceRoleKey = ''
if ($AccessMode -eq 'TrustedServer' -and $cloudEnabled) {
    if ($NonInteractive) {
        $secretKey = [string]$env:EXAMTRANSFER_SUPABASE_SECRET_KEY
        $serviceRoleKey = [string]$env:EXAMTRANSFER_SUPABASE_SERVICE_KEY
    }
    else {
        $secretKey = Read-PlainTextSecret 'Nhap Supabase Secret Key (an khi nhap; Enter de bo qua)'
        if ([string]::IsNullOrWhiteSpace($secretKey)) {
            $serviceRoleKey = Read-PlainTextSecret 'Nhap legacy Service Role Key (an khi nhap; Enter de bo qua)'
        }
    }

    if ([string]::IsNullOrWhiteSpace($secretKey) -and
        [string]::IsNullOrWhiteSpace($serviceRoleKey)) {
        throw 'TrustedServer mode requires a Supabase secret key or service-role key.'
    }
}

foreach ($item in @{
    SupabaseUrl = [string]$SupabaseUrl
    PublishableKey = [string]$PublishableKey
    OrganizationId = [string]$organizationIdText
    PreferredIp = [string]$PreferredIp
    SecretKey = [string]$secretKey
    ServiceRoleKey = [string]$serviceRoleKey
}.GetEnumerator()) {
    Assert-SingleLineValue -Name $item.Key -Value ([string]$item.Value)
}

$tokenSigningKey = New-RandomBase64Key
$receiptSigningKey = New-RandomBase64Key
$discoveryEnabled = ([bool](-not [bool]$DisableDiscovery)).ToString().ToLowerInvariant()
$cloudEnabledText = ([bool]$cloudEnabled).ToString().ToLowerInvariant()

$lines = @(
    '# Generated by scripts/setup-docker-environment.ps1',
    '# Do not commit this file to GitHub.',
    "ASPNETCORE_ENVIRONMENT=$Environment",
    '',
    'Server__Port=5048',
    'Server__UseHttps=false',
    "Server__PreferredIp=$PreferredIp",
    '',
    "Discovery__Enabled=$discoveryEnabled",
    'Discovery__Protocol=UdpBroadcast',
    'Discovery__Port=5050',
    'Discovery__RequestMagic=EXAMTRANSFER_DISCOVER_V1',
    '',
    @($AllowedCidrs | ForEach-Object -Begin { $index = 0 } -Process {
        $line = "LanAccess__AllowedCidrs__$index=$($_.Trim())"
        $index++
        $line
    }),
    'LanAccess__TrustDockerDesktopNat=false',
    '',
    'Storage__RootPath=/data/ExamTransfer',
    'Storage__MinFreeBytes=1073741824',
    '',
    "Security__TokenSigningKey=$tokenSigningKey",
    "Security__ReceiptSigningKey=$receiptSigningKey",
    '',
    "Cloud__Enabled=$cloudEnabledText",
    "Cloud__Environment=$Environment",
    "Cloud__AccessMode=$AccessMode",
    "Cloud__SupabaseUrl=$SupabaseUrl",
    "Cloud__PublishableKey=$PublishableKey",
    "Cloud__OrganizationId=$organizationIdText",
    'Cloud__Schema=public',
    'Cloud__ExamBucket=exam-archives',
    'Cloud__SubmissionBucket=submission-archives',
    'Cloud__ExportBucket=report-exports',
    'Cloud__BackupBucket=backup-archives',
    'Cloud__UseResumableUploads=true',
    'Cloud__PersistUserSession=true',
    'Cloud__SecretKeyEnvironmentVariable=EXAMTRANSFER_SUPABASE_SECRET_KEY',
    'Cloud__ServiceRoleEnvironmentVariable=EXAMTRANSFER_SUPABASE_SERVICE_KEY',
    '',
    "EXAMTRANSFER_SUPABASE_SECRET_KEY=$secretKey",
    "EXAMTRANSFER_SUPABASE_SERVICE_KEY=$serviceRoleKey"
)

if (Test-Path -LiteralPath $targetPath) {
    $backupPath = "$targetPath.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $targetPath -Destination $backupPath -Force
    Write-Host "Existing environment file backed up to: $backupPath" -ForegroundColor Yellow
}

[IO.File]::WriteAllLines(
    $targetPath,
    $lines,
    (New-Object Text.UTF8Encoding($false)))

Write-Host "Docker environment created: $targetPath" -ForegroundColor Green
Write-Host "Access mode: $AccessMode" -ForegroundColor Cyan
Write-Host "Cloud enabled: $cloudEnabledText" -ForegroundColor Cyan
if ($cloudEnabled) {
    Write-Host "ExamTransfer Organization ID: $organizationIdText" -ForegroundColor Cyan
}
if (-not [string]::IsNullOrWhiteSpace($PreferredIp)) {
    Write-Host "LAN advertised IP: $PreferredIp" -ForegroundColor Cyan
}
Write-Host 'Secret values were not printed.' -ForegroundColor Yellow
