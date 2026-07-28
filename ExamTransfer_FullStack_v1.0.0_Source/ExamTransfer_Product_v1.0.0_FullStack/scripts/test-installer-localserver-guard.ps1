[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$guard = Join-Path $PSScriptRoot 'installer-localserver-guard.ps1'
if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) {
    throw "Installer guard was not found: $guard"
}

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

try {
    New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $unrelatedDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $userDataDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination $installedExe
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination $unrelatedExe
    Set-Content -LiteralPath $userDataMarker -Value 'preserve' -Encoding ascii

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

    Write-Host 'PASS code=INSTALLER_EXACT_PATH_PROCESS_GUARD unrelated_same_name=preserved user_data=preserved' -ForegroundColor Green
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
