param(
    [string]$RepositoryRoot = "D:\MMO\PhanMemNopThuBaiKiemTra",
    [string]$InstallRoot = "$env:ProgramFiles\ExamTransfer"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-File([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "MISSING $Name: $Path"
    }
}

function Add-Check(
    [System.Collections.Generic.List[object]]$Rows,
    [string]$Name,
    [object]$Expected,
    [object]$Actual
) {
    $pass = [string]$Expected -eq [string]$Actual
    $Rows.Add([pscustomobject]@{
        Check    = $Name
        Result   = if ($pass) { "PASS" } else { "FAIL" }
        Expected = [string]$Expected
        Actual   = [string]$Actual
    })
}

$projectRoot = Join-Path $RepositoryRoot "ExamTransfer_Product_FullStack"
$releaseRoot = Join-Path $projectRoot "artifacts\release"
$releaseManifestPath = Join-Path $releaseRoot "release-manifest.json"
$installedManifestPath = Join-Path $InstallRoot "release-manifest.json"

Require-File $releaseManifestPath "release manifest"
Require-File $installedManifestPath "installed manifest"

$head = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
    throw "Không đọc được Git HEAD."
}

$release = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
$installed = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json

$releaseClient = Join-Path $releaseRoot ([string]$release.client.file -replace "/", "\")
$releaseServer = Join-Path $releaseRoot ([string]$release.server.file -replace "/", "\")
$installedClient = Join-Path $InstallRoot ([string]$installed.client.file -replace "/", "\")
$installedServer = Join-Path $InstallRoot ([string]$installed.server.file -replace "/", "\")

Require-File $releaseClient "release client"
Require-File $releaseServer "release server"
Require-File $installedClient "installed client"
Require-File $installedServer "installed server"

$releaseManifestHash = (Get-FileHash -LiteralPath $releaseManifestPath -Algorithm SHA256).Hash
$installedManifestHash = (Get-FileHash -LiteralPath $installedManifestPath -Algorithm SHA256).Hash
$releaseClientHash = (Get-FileHash -LiteralPath $releaseClient -Algorithm SHA256).Hash
$releaseServerHash = (Get-FileHash -LiteralPath $releaseServer -Algorithm SHA256).Hash
$installedClientHash = (Get-FileHash -LiteralPath $installedClient -Algorithm SHA256).Hash
$installedServerHash = (Get-FileHash -LiteralPath $installedServer -Algorithm SHA256).Hash

$rows = [System.Collections.Generic.List[object]]::new()

Add-Check $rows "Release manifest -> Git HEAD" $head ([string]$release.gitCommit)
Add-Check $rows "Installed manifest -> Git HEAD" $head ([string]$installed.gitCommit)
Add-Check $rows "Installed manifest = release manifest" $releaseManifestHash $installedManifestHash
Add-Check $rows "Installed BuildId = release BuildId" ([string]$release.buildId) ([string]$installed.buildId)
Add-Check $rows "Release client hash = manifest" ([string]$release.client.sha256) $releaseClientHash
Add-Check $rows "Installed client hash = manifest" ([string]$installed.client.sha256) $installedClientHash
Add-Check $rows "Installed client = release client" $releaseClientHash $installedClientHash
Add-Check $rows "Release server hash = manifest" ([string]$release.server.sha256) $releaseServerHash
Add-Check $rows "Installed server hash = manifest" ([string]$installed.server.sha256) $installedServerHash
Add-Check $rows "Installed server = release server" $releaseServerHash $installedServerHash

$clientVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedClient)
$serverVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedServer)
Add-Check $rows "Client ProductVersion" ([string]$installed.semanticVersion) ([string]$clientVersion.ProductVersion).Trim()
Add-Check $rows "Server ProductVersion" ([string]$installed.semanticVersion) ([string]$serverVersion.ProductVersion).Trim()

$healthStatus = "NOT_RUNNING"
try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:5048/health" -TimeoutSec 2
    $healthStatus = [string]$health.buildId
    Add-Check $rows "Running Local Server BuildId" ([string]$installed.buildId) $healthStatus
}
catch {
    $rows.Add([pscustomobject]@{
        Check    = "Running Local Server BuildId"
        Result   = "INFO"
        Expected = [string]$installed.buildId
        Actual   = "Local Server chưa chạy hoặc health endpoint chưa sẵn sàng"
    })
}

$rows | Format-Table -AutoSize -Wrap

$failed = @($rows | Where-Object Result -eq "FAIL")
Write-Host ""
Write-Host "SOURCE HEAD       : $head"
Write-Host "RELEASE BUILD ID  : $($release.buildId)"
Write-Host "INSTALLED BUILD ID: $($installed.buildId)"
Write-Host "INSTALLED CLIENT  : $installedClient"
Write-Host "INSTALLED SERVER  : $installedServer"

if ($failed.Count -eq 0) {
    Write-Host ""
    Write-Host "VERDICT: PASS - Bản đã cài trùng hoàn toàn với release được build từ Git HEAD hiện tại." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "VERDICT: FAIL - Bản đã cài không trùng release/HEAD hiện tại." -ForegroundColor Red
exit 1
