param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$frontendProject = Join-Path $root 'frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'
$backendSolution = Join-Path $root 'backend\ExamTransfer.sln'
$backendProject = Join-Path $root 'backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj'
$frontendVerify = Join-Path $root 'frontend\scripts\verify-frontend.ps1'
$installerScript = Join-Path $root 'installer\ExamTransfer.iss'
$releaseRoot = Join-Path $root 'artifacts\release'
$clientOutput = Join-Path $releaseRoot 'Client'
$serverOutput = Join-Path $releaseRoot 'Server'
$installerOutput = Join-Path $root 'artifacts\installer'

function Require-File([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Không tìm thấy file bắt buộc: $Path"
    }
}

function Find-InnoCompiler {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) }

    if ($candidates.Count -eq 0) {
        throw 'Không tìm thấy Inno Setup 6. Hãy cài Inno Setup 6 rồi chạy lại.'
    }

    return $candidates[0]
}

Require-File $frontendProject
Require-File $backendSolution
Require-File $backendProject
Require-File $frontendVerify
Require-File $installerScript

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'Không tìm thấy dotnet. Hãy cài .NET SDK 10.'
}

Write-Host "=== ExamTransfer release $Version ===" -ForegroundColor Cyan
Write-Host "Project root: $root"
dotnet --version

if (Test-Path $releaseRoot) {
    Remove-Item $releaseRoot -Recurse -Force
}
if (Test-Path $installerOutput) {
    Remove-Item $installerOutput -Recurse -Force
}
New-Item -ItemType Directory -Path $clientOutput -Force | Out-Null
New-Item -ItemType Directory -Path $serverOutput -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

Write-Host "\n[1/6] Restore backend..." -ForegroundColor Yellow
dotnet restore $backendSolution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore thất bại.' }

if (-not $SkipTests) {
    Write-Host "\n[2/6] Test backend Release..." -ForegroundColor Yellow
    dotnet test $backendSolution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Backend test thất bại. Dừng tạo bản phát hành.' }

    Write-Host "\n[3/6] Verify frontend..." -ForegroundColor Yellow
    powershell -ExecutionPolicy Bypass -File $frontendVerify
    if ($LASTEXITCODE -ne 0) { throw 'Frontend verify thất bại. Dừng tạo bản phát hành.' }
}
else {
    Write-Warning 'Đã bỏ qua test theo tham số -SkipTests. Không dùng tùy chọn này cho bản phát hành chính thức.'
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
    -o $clientOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish frontend thất bại.' }

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
    -o $serverOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish Local Server thất bại.' }

Require-File (Join-Path $clientOutput 'ExamTransfer.Desktop.exe')
Require-File (Join-Path $serverOutput 'ExamTransfer.LocalServer.exe')

Write-Host "\n[6/6] Build installer..." -ForegroundColor Yellow
$iscc = Find-InnoCompiler
& $iscc "/DMyAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Biên dịch installer thất bại.' }

$installer = Join-Path $installerOutput "ExamTransfer-Setup-$Version.exe"
Require-File $installer

$hash = Get-FileHash $installer -Algorithm SHA256
$hashFile = "$installer.sha256.txt"
"$($hash.Hash)  $([IO.Path]::GetFileName($installer))" | Set-Content -Path $hashFile -Encoding ascii

Write-Host "\nBUILD THÀNH CÔNG" -ForegroundColor Green
Write-Host "Installer : $installer"
Write-Host "SHA-256  : $($hash.Hash)"
Write-Host "Hash file: $hashFile"
