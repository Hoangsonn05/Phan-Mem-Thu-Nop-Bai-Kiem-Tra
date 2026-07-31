$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 1. Verification of HEAD
$gitCommit = (& git rev-parse HEAD).Trim()
if ($gitCommit.Length -lt 8) { throw "Invalid Git commit." }
$shortCommit = $gitCommit.Substring(0, 8)

# 2. Get Semantic Version from Directory.Build.props
$propsFile = ".\Directory.Build.props"
if (-not (Test-Path $propsFile)) { throw "Directory.Build.props not found" }
$propsXml = [xml](Get-Content $propsFile)
$semanticVersion = $propsXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($semanticVersion)) {
    throw "Semantic version not found in Directory.Build.props"
}
$assemblyVersion = "$semanticVersion.0"

# 3. Create Atomic BuildId
$buildTimestampUtc = [DateTimeOffset]::UtcNow
$buildTimestampText = $buildTimestampUtc.ToString("yyyyMMddTHHmmssZ")
$buildId = "$semanticVersion+$shortCommit-onlylan-e2e.$buildTimestampText"

# 4. Pre-publish verification
Write-Host "Running pre-publish verification..."
& powershell -ExecutionPolicy Bypass -File ".\scripts\verify-onlylan-characterization-backend-collect.ps1"
if ($LASTEXITCODE -ne 0) { throw "Pre-publish verification failed." }

# 5. Clear and create candidate dir
$candidateRoot = ".\artifacts\onlylan-published-e2e\candidate"
$manifestPath = Join-Path $candidateRoot "release-manifest.json"

$oldBuildId = $null
$oldGitCommit = $null
$oldSha256 = $null

if (Test-Path $manifestPath -PathType Leaf) {
    try {
        $oldManifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $oldBuildId = $oldManifest.buildId
        $oldGitCommit = $oldManifest.gitCommit
        $oldSha256 = $oldManifest.server.sha256
    } catch {
        $oldBuildId = "<UNREADABLE>"
    }
}
Write-Host "OLD_BAD_MANIFEST_BUILD_ID: $oldBuildId"

if (Test-Path $candidateRoot) {
    Remove-Item -LiteralPath $candidateRoot -Recurse -Force
}

$serverOutput = Join-Path $candidateRoot "Server"
$logOutput = Join-Path $candidateRoot "build-logs"

New-Item -ItemType Directory -Force -Path $serverOutput | Out-Null
New-Item -ItemType Directory -Force -Path $logOutput | Out-Null

# 6. Publish
$serverProject = ".\backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj"
& dotnet restore $serverProject
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

& dotnet publish $serverProject `
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
  -p:ExamTransferGitCommit=$gitCommit `
  -p:ExamTransferWorkingTreeDirty=false `
  -o $serverOutput `
  2>&1 | Tee-Object -FilePath (Join-Path $logOutput "publish.log")

if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# 7. Hashing
$serverExe = Join-Path $serverOutput "ExamTransfer.LocalServer.exe"
if (-not (Test-Path $serverExe -PathType Leaf)) { throw "ExamTransfer.LocalServer.exe was not created." }
$serverFile = Get-Item $serverExe
$serverExeHash = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash
$serverVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($serverExe)
$serverFileVersion = $serverVersionInfo.FileVersion.Trim()
$serverProductVersion = $serverVersionInfo.ProductVersion.Trim()

$dlls = @("ExamTransfer.LocalServer.dll", "ExamTransfer.Infrastructure.dll", "ExamTransfer.Application.dll", "ExamTransfer.Domain.dll")
$identityFiles = @()
foreach ($dll in $dlls) {
    $dllPath = Join-Path $serverOutput $dll
    if (Test-Path $dllPath -PathType Leaf) {
        $dllHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash
        $identityFiles += @{
            file = "Server/$dll"
            sha256 = $dllHash
        }
    }
}

# 8. Manifest
$manifest = [ordered]@{
    semanticVersion   = $semanticVersion
    buildId           = $buildId
    gitCommit         = $gitCommit
    workingTreeDirty  = $false
    buildTimestampUtc = $buildTimestampUtc.ToString("O")
    runtime           = "win-x64"
    selfContained     = $true
    discoveryProtocol = "ExamTransfer/2"
    discoveryUdpPort  = 40550
    sourceTask        = "ET-LAN-MODULE-REFACTOR-01D"
    candidateTask     = "ET-LAN-PUBLISHED-CANDIDATE-BUILD-ID-ATOMIC-01"
    server = [ordered]@{
        file = "Server/ExamTransfer.LocalServer.exe"
        sizeBytes = $serverFile.Length
        fileVersion = $serverFileVersion
        productVersion = $serverProductVersion
        sha256 = $serverExeHash
    }
    identityFiles = $identityFiles
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

# Validate manifest content
$writtenManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($writtenManifest.buildId -ne $buildId) { throw "Manifest build ID mismatch." }

# 9. Runtime Smoke Test
$smokeRoot = ".\artifacts\onlylan-published-e2e\candidate-smoke"
if (Test-Path $smokeRoot) { Remove-Item -LiteralPath $smokeRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

$tcpProcess = Get-NetTCPConnection -LocalPort 5048 -State Listen -ErrorAction SilentlyContinue
if ($tcpProcess) { throw "TCP Port 5048 in use." }
$udpProcess = Get-NetUDPEndpoint -LocalPort 40550 -ErrorAction SilentlyContinue
if ($udpProcess) { throw "UDP Port 40550 in use." }

$stdoutLog = Join-Path $smokeRoot "server.stdout.log"
$stderrLog = Join-Path $smokeRoot "server.stderr.log"

$savedEnvironment = @{}
$environmentUpdates = @{
    'DOTNET_ENVIRONMENT' = 'Testing'
    'EXAMTRANSFER_ALLOW_TEST_FIXTURE' = '1'
    'Storage__RootPath' = (Resolve-Path $smokeRoot).Path
    'EXAMTRANSFER_Storage__RootPath' = (Resolve-Path $smokeRoot).Path
    'Discovery__Enabled' = 'true'
    'Discovery__Port' = '40550'
}

try {
    foreach ($name in $environmentUpdates.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environmentUpdates[$name], 'Process')
    }

    $serverProcess = Start-Process `
        -FilePath $serverExe `
        -WorkingDirectory $smokeRoot `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog
        
    $healthSuccess = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        try {
            $response = Invoke-RestMethod -Uri "http://127.0.0.1:5048/health" -Method Get -ErrorAction Stop
            
            $healthBuildId = $response.buildId
            $healthProtocol = $response.protocol
            $healthDiscoveryPort = $response.discoveryPort
            $backendRuntimeCode = $response.backendRuntime.code
            $udpDiscoveryCode = $response.udpDiscovery.code
            
            Write-Host "EXPECTED_BUILD_ID: $buildId"
            Write-Host "MANIFEST_BUILD_ID: $($writtenManifest.buildId)"
            Write-Host "RUNTIME_BUILD_ID: $healthBuildId"
            
            if ($healthBuildId -ne $buildId) { throw "Identity mismatch: $healthBuildId != $buildId" }
            if ($healthProtocol -ne "ExamTransfer/2") { throw "Protocol mismatch: $healthProtocol" }
            if ([int]$healthDiscoveryPort -ne 40550) { throw "Discovery Port mismatch: $healthDiscoveryPort" }
            if ($backendRuntimeCode -ne "BACKEND_RUNTIME_READY") { throw "Backend Code mismatch: $backendRuntimeCode" }
            if ($udpDiscoveryCode -ne "UDP_DISCOVERY_LISTENING") { throw "UDP Code mismatch: $udpDiscoveryCode" }
            
            $healthSuccess = $true
            break
        } catch {
            if ($_.Exception.Message -match "Identity mismatch" -or $_.Exception.Message -match "mismatch") {
                throw $_
            }
            # Otherwise, keep trying (server might be starting up)
        }
    }
    
    if (-not $healthSuccess) { throw "Health endpoint failed or timed out." }

} finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
        $serverProcess.WaitForExit(5000)
    }
    foreach ($name in $environmentUpdates.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
Write-Host "All tests passed successfully!"
Write-Host "ATOMIC_BUILD_ID: $buildId"
