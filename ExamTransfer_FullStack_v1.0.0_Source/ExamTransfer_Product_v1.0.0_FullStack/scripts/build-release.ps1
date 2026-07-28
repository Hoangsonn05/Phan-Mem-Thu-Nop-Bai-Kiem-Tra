param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$frontendProject = Join-Path $root 'frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'
$backendSolution = Join-Path $root 'backend\ExamTransfer.sln'
$backendProject = Join-Path $root 'backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj'
$frontendVerify = Join-Path $root 'frontend\scripts\verify-frontend.ps1'
$installerScript = Join-Path $root 'installer\ExamTransfer.iss'
$releaseRoot = Join-Path $root 'artifacts\release'
$clientOutput = Join-Path $releaseRoot 'Client'
$serverOutput = Join-Path $releaseRoot 'Server'
$publicCloudConfig = Join-Path $clientOutput 'publiccloud.runtime.json'
$releaseManifest = Join-Path $releaseRoot 'release-manifest.json'
$installerOutput = Join-Path $root 'artifacts\installer'

function Require-File([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
}

function Find-InnoCompiler {
    $candidates = @(@(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) })

    if ($candidates.Count -eq 0) {
        throw 'Inno Setup 6 was not found. Install Inno Setup 6 and retry.'
    }

    return $candidates[0]
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
        $payload = $payload.PadRight($payload.Length + ((4 - ($payload.Length % 4)) % 4), '=')
        $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
        return [string]$json.role -ceq 'anon'
    } catch {
        return $false
    }
}

Require-File $frontendProject
Require-File $backendSolution
Require-File $backendProject
Require-File $frontendVerify
Require-File $installerScript

$publicCloudUrl = [string]$env:EXAMTRANSFER_SUPABASE_URL
$publicCloudKey = [string]$env:EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY
$parsedPublicCloudUrl = $null
if ([string]::IsNullOrWhiteSpace($publicCloudUrl) -or
    -not [Uri]::TryCreate($publicCloudUrl.Trim(), [UriKind]::Absolute, [ref]$parsedPublicCloudUrl) -or
    $parsedPublicCloudUrl.Scheme -cne 'https') {
    throw 'PUBLICCLOUD_INVALID_URL: official installer build requires EXAMTRANSFER_SUPABASE_URL with HTTPS.'
}
if (-not (Test-PublishableKey $publicCloudKey.Trim())) {
    throw 'PUBLICCLOUD_INVALID_PUBLISHABLE_KEY: supply a publishable or legacy anon key; secret/service-role/placeholder values are rejected.'
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'dotnet was not found. Install .NET SDK 10.'
}

Write-Host "=== ExamTransfer release $Version ===" -ForegroundColor Cyan
Write-Host "Project root: $root"
dotnet --version

$gitCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $gitCommit) {
    throw 'Unable to resolve the Git commit for release identity.'
}
$workingTreeDirty = [bool](& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine whether the Git working tree is dirty.'
}
$dirtyMarker = if ($workingTreeDirty) { 'dirty' } else { 'clean' }
$buildTimestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$buildId = "$Version+$($gitCommit.Substring(0, 8))-$dirtyMarker.$buildTimestamp"
Write-Host "Build ID: $buildId"

foreach ($publishOutput in @($clientOutput, $serverOutput)) {
    if (Test-Path $publishOutput -PathType Container) {
        Remove-Item -LiteralPath $publishOutput -Recurse -Force
    }
}
if (Test-Path $releaseManifest -PathType Leaf) {
    Remove-Item -LiteralPath $releaseManifest -Force
}
$installer = Join-Path $installerOutput "ExamTransfer-Setup-$Version.exe"
$installerHashFile = "$installer.sha256.txt"
foreach ($oldInstallerOutput in @($installer, $installerHashFile)) {
    if (Test-Path $oldInstallerOutput -PathType Leaf) {
        Remove-Item -LiteralPath $oldInstallerOutput -Force
    }
}
New-Item -ItemType Directory -Path $clientOutput -Force | Out-Null
New-Item -ItemType Directory -Path $serverOutput -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

Write-Host "\n[1/6] Restore backend..." -ForegroundColor Yellow
dotnet restore $backendSolution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

if (-not $SkipTests) {
    Write-Host "\n[2/6] Test backend Release..." -ForegroundColor Yellow
    dotnet test $backendSolution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed. Release creation stopped.' }

    Write-Host "\n[3/6] Verify frontend..." -ForegroundColor Yellow
    powershell -ExecutionPolicy Bypass -File $frontendVerify -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Frontend verification failed. Release creation stopped.' }
}
else {
    Write-Warning 'Tests were skipped by -SkipTests. Do not use this option for an official release.'
}

$assemblyVersion = "$Version.0"

Write-Host "\n[4/6] Publish frontend WPF..." -ForegroundColor Yellow
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
    -p:ExamTransferWorkingTreeDirty=$($workingTreeDirty.ToString().ToLowerInvariant()) `
    -o $clientOutput
if ($LASTEXITCODE -ne 0) { throw 'Frontend publish failed.' }

$publicConfigDocument = [ordered]@{
    supabaseUrl = $publicCloudUrl.Trim().TrimEnd('/')
    publishableKey = $publicCloudKey.Trim()
}
[IO.File]::WriteAllText(
    $publicCloudConfig,
    ($publicConfigDocument | ConvertTo-Json -Depth 2),
    (New-Object Text.UTF8Encoding($false)))
Require-File $publicCloudConfig
$publicCloudConfigHash = (Get-FileHash $publicCloudConfig -Algorithm SHA256).Hash

Write-Host "\n[5/6] Publish Local Server..." -ForegroundColor Yellow
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
    -p:ExamTransferWorkingTreeDirty=$($workingTreeDirty.ToString().ToLowerInvariant()) `
    -o $serverOutput
if ($LASTEXITCODE -ne 0) { throw 'Local Server publish failed.' }

Require-File (Join-Path $clientOutput 'ExamTransfer.Desktop.exe')
Require-File (Join-Path $serverOutput 'ExamTransfer.LocalServer.exe')

$clientHash = (Get-FileHash (Join-Path $clientOutput 'ExamTransfer.Desktop.exe') -Algorithm SHA256).Hash
$serverHash = (Get-FileHash (Join-Path $serverOutput 'ExamTransfer.LocalServer.exe') -Algorithm SHA256).Hash
$manifest = [ordered]@{
    semanticVersion = $Version
    buildId = $buildId
    gitCommit = $gitCommit
    workingTreeDirty = $workingTreeDirty
    buildTimestampUtc = $buildTimestamp
    discoveryProtocol = 'ExamTransfer/2'
    discoveryUdpPort = 40550
    client = [ordered]@{
        file = 'Client/ExamTransfer.Desktop.exe'
        sha256 = $clientHash
    }
    server = [ordered]@{
        file = 'Server/ExamTransfer.LocalServer.exe'
        sha256 = $serverHash
    }
    publicCloudConfig = [ordered]@{
        file = 'Client/publiccloud.runtime.json'
        sha256 = $publicCloudConfigHash
        classification = 'publishable-client-config'
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $releaseManifest -Encoding utf8
Copy-Item -LiteralPath $releaseManifest -Destination (Join-Path $clientOutput 'release-manifest.json')
Copy-Item -LiteralPath $releaseManifest -Destination (Join-Path $serverOutput 'release-manifest.json')
Require-File $releaseManifest

Write-Host "\n[6/6] Build installer..." -ForegroundColor Yellow
$iscc = Find-InnoCompiler
& $iscc "/DMyAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Require-File $installer

$hash = Get-FileHash $installer -Algorithm SHA256
"$($hash.Hash)  $([IO.Path]::GetFileName($installer))" | Set-Content -Path $installerHashFile -Encoding ascii

Write-Host "\nBUILD SUCCEEDED" -ForegroundColor Green
Write-Host "Installer : $installer"
Write-Host "SHA-256  : $($hash.Hash)"
Write-Host "Hash file: $installerHashFile"
Write-Host "Manifest : $releaseManifest"
