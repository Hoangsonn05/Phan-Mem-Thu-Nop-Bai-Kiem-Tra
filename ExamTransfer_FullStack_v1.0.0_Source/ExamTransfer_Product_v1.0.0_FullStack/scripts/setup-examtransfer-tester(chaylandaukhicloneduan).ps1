[CmdletBinding()]
param(
    [ValidateSet('FullStack','FrontendOnly')]
    [string]$Mode = 'FullStack',

    [string]$ProjectRoot,

    [string]$SupabaseUrl,
    [string]$PublishableKey,
    [string]$OrganizationId,

    [string]$ApiUrl = 'http://localhost:5048',

    [switch]$ConfigureFirewall,
    [switch]$InstallSupabaseCli,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$KeepSdkPolicy,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Results = New-Object System.Collections.Generic.List[object]
$script:ManualNotes = New-Object System.Collections.Generic.List[string]

function Add-Result {
    param([string]$Component,[string]$Status,[string]$Detail)
    $item = [pscustomobject]@{
        Component = $Component
        Status = $Status
        Detail = $Detail
        TimeUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $script:Results.Add($item)
    $color = switch ($Status) {
        'FOUND' { 'Green' }
        'INSTALLED' { 'Green' }
        'PASS' { 'Green' }
        'MANUAL' { 'Yellow' }
        'SKIPPED' { 'DarkYellow' }
        'MISSING' { 'Yellow' }
        default { 'Red' }
    }
    Write-Host ('[{0}] {1}: {2}' -f $Status,$Component,$Detail) -ForegroundColor $color
}

function Test-CommandExists {
    param([Parameter(Mandatory=$true)][string]$Name)
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory=$true)][string]$Description,
        [Parameter(Mandatory=$true)][scriptblock]$Command
    )
    Write-Host "`n== $Description ==" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Refresh-ProcessPath {
    $machine = [Environment]::GetEnvironmentVariable('Path','Machine')
    $user = [Environment]::GetEnvironmentVariable('Path','User')
    $env:Path = "$machine;$user"
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory=$true)][string]$Id,
        [Parameter(Mandatory=$true)][string]$Component
    )
    if (-not (Test-CommandExists 'winget')) {
        Add-Result $Component 'MANUAL' 'WinGet is unavailable. Install Microsoft App Installer from Microsoft Store, reopen PowerShell, then rerun.'
        $script:ManualNotes.Add('Install Microsoft App Installer/WinGet manually, then rerun setup.')
        return $false
    }

    try {
        & winget install --id $Id -e --source winget --accept-package-agreements --accept-source-agreements --silent
        if ($LASTEXITCODE -ne 0) {
            throw "winget exited with $LASTEXITCODE"
        }
        Refresh-ProcessPath
        Add-Result $Component 'INSTALLED' "Installed globally using WinGet package $Id."
        return $true
    }
    catch {
        Add-Result $Component 'MANUAL' "Automatic install failed: $($_.Exception.Message). Install package $Id manually."
        $script:ManualNotes.Add("Install $Component manually (WinGet ID: $Id).")
        return $false
    }
}

function Get-DotNetSdkMajors {
    if (-not (Test-CommandExists 'dotnet')) { return @() }
    $lines = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0) { return @() }
    $majors = @()
    foreach ($line in $lines) {
        if ($line -match '^([0-9]+)\.') { $majors += [int]$matches[1] }
    }
    return @($majors | Select-Object -Unique)
}

$isWindowsPlatform = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if (-not $isWindowsPlatform) {
    throw 'This setup script supports Windows only because the frontend is WPF.'
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $candidate = Split-Path -Parent $PSScriptRoot
    if (Test-Path -LiteralPath (Join-Path $candidate 'ExamTransfer.slnx')) {
        $ProjectRoot = $candidate
    }
    elseif (Test-Path -LiteralPath (Join-Path (Get-Location).Path 'ExamTransfer.slnx')) {
        $ProjectRoot = (Get-Location).Path
    }
    else {
        throw 'Cannot detect ProjectRoot. Pass -ProjectRoot pointing to the folder containing ExamTransfer.slnx.'
    }
}

$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

if (-not $KeepSdkPolicy) {
    foreach ($globalJsonPath in @(
        (Join-Path $ProjectRoot 'global.json'),
        (Join-Path $ProjectRoot 'backend\global.json')
    )) {
        if (-not (Test-Path -LiteralPath $globalJsonPath)) { continue }
        try {
            $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
            if ($null -ne $globalJson.sdk -and
                [string]$globalJson.sdk.version -match '^10\.' -and
                [string]$globalJson.sdk.rollForward -ne 'latestFeature') {
                $backupPath = $globalJsonPath + '.setup-backup'
                if (-not (Test-Path -LiteralPath $backupPath)) {
                    Copy-Item -LiteralPath $globalJsonPath -Destination $backupPath -Force
                }
                $globalJson.sdk.rollForward = 'latestFeature'
                if ($globalJson.sdk.PSObject.Properties.Name -contains 'allowPrerelease') {
                    $globalJson.sdk.allowPrerelease = $false
                } else {
                    $globalJson.sdk | Add-Member -NotePropertyName allowPrerelease -NotePropertyValue $false
                }
                $globalJson | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $globalJsonPath -Encoding UTF8
                Add-Result 'SDK roll-forward policy' 'INSTALLED' "Normalized to latestFeature in $globalJsonPath; backup: $backupPath"
            } else {
                Add-Result 'SDK roll-forward policy' 'FOUND' "$globalJsonPath is compatible with global .NET 10 SDK installation."
            }
        } catch {
            Add-Result 'SDK roll-forward policy' 'MANUAL' "Could not inspect/update $globalJsonPath: $($_.Exception.Message)"
        }
    }
} else {
    Add-Result 'SDK roll-forward policy' 'SKIPPED' 'Kept repository global.json unchanged by parameter.'
}

$solution = Join-Path $ProjectRoot 'ExamTransfer.slnx'
$backendSolution = Join-Path $ProjectRoot 'backend\ExamTransfer.sln'
$frontendProject = Join-Path $ProjectRoot 'frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'
$configScript = Join-Path $ProjectRoot 'backend\scripts\configure-supabase.ps1'
$migratorProject = Join-Path $ProjectRoot 'backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj'
$reportDir = Join-Path $ProjectRoot 'artifacts\setup'
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

Add-Result 'ProjectRoot' 'FOUND' $ProjectRoot

$requiredFiles = @($solution,$frontendProject)
if ($Mode -eq 'FullStack') { $requiredFiles += @($backendSolution,$migratorProject) }
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Required file not found: $file" }
}

$os = Get-CimInstance Win32_OperatingSystem
$arch = [Environment]::Is64BitOperatingSystem
Add-Result 'Windows' 'FOUND' ("{0}; 64-bit={1}" -f $os.Caption,$arch)
if (-not $arch) { throw 'ExamTransfer requires 64-bit Windows.' }

if (Test-CommandExists 'winget') {
    Add-Result 'WinGet' 'FOUND' ((& winget --version) -join ' ')
} else {
    Add-Result 'WinGet' 'MANUAL' 'Not found. Microsoft App Installer must be installed manually before automatic package installation.'
}

if (Test-CommandExists 'git') {
    Add-Result 'Git' 'FOUND' ((& git --version) -join ' ')
} else {
    [void](Install-WingetPackage -Id 'Git.Git' -Component 'Git')
}

$sdkMajors = Get-DotNetSdkMajors
if ($sdkMajors -contains 10) {
    $sdks = (& dotnet --list-sdks) -join '; '
    Add-Result '.NET SDK 10' 'FOUND' $sdks
} else {
    [void](Install-WingetPackage -Id 'Microsoft.DotNet.SDK.10' -Component '.NET SDK 10')
    $sdkMajors = Get-DotNetSdkMajors
    if (-not ($sdkMajors -contains 10)) {
        Add-Result '.NET SDK 10' 'MANUAL' 'A global .NET 10 SDK is still unavailable. Install the latest .NET 10 SDK x64 and reopen PowerShell.'
    }
}

if ($InstallSupabaseCli) {
    if (Test-CommandExists 'supabase') {
        Add-Result 'Supabase CLI' 'FOUND' ((& supabase --version) -join ' ')
    } else {
        Add-Result 'Supabase CLI' 'MANUAL' 'Optional maintainer tool is missing. Install via an official Supabase-supported Windows method. It is not required to run the app.'
        $script:ManualNotes.Add('Optional: install Supabase CLI only on maintainer machines that push migrations.')
    }
} else {
    Add-Result 'Supabase CLI' 'SKIPPED' 'Not required for tester runtime; only needed for migration/maintenance.'
}

try {
    $nugetTest = Test-NetConnection 'api.nuget.org' -Port 443 -WarningAction SilentlyContinue
    if ($nugetTest.TcpTestSucceeded) { Add-Result 'NuGet HTTPS' 'PASS' 'api.nuget.org:443 reachable.' }
    else { Add-Result 'NuGet HTTPS' 'MANUAL' 'api.nuget.org:443 is not reachable; restore may fail due to firewall/proxy.' }
} catch {
    Add-Result 'NuGet HTTPS' 'MANUAL' "Connectivity check failed: $($_.Exception.Message)"
}

if ($Mode -eq 'FullStack') {
    $runtimeConfig = Join-Path $env:ProgramData 'ExamTransfer\config\runtime-settings.json'
    $haveConfigArgs = -not [string]::IsNullOrWhiteSpace($SupabaseUrl) -and
                      -not [string]::IsNullOrWhiteSpace($PublishableKey) -and
                      -not [string]::IsNullOrWhiteSpace($OrganizationId)

    if ($haveConfigArgs) {
        if (-not (Test-Path -LiteralPath $configScript)) { throw "Configuration script not found: $configScript" }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $configScript `
            -SupabaseUrl $SupabaseUrl `
            -PublishableKey $PublishableKey `
            -OrganizationId $OrganizationId `
            -AccessMode 'UserSession' `
            -Environment 'Development'
        if ($LASTEXITCODE -ne 0) { throw 'Supabase runtime configuration failed.' }
        Add-Result 'Supabase runtime config' 'PASS' $runtimeConfig
    }
    elseif (Test-Path -LiteralPath $runtimeConfig) {
        Add-Result 'Supabase runtime config' 'FOUND' $runtimeConfig
    }
    else {
        Add-Result 'Supabase runtime config' 'MANUAL' 'Missing SupabaseUrl, PublishableKey and ExamTransfer OrganizationId. Rerun with these parameters. Never use a secret key on tester clients.'
        $script:ManualNotes.Add('Provide -SupabaseUrl, -PublishableKey and -OrganizationId, or copy only runtime-settings.json from a trusted configured backend machine.')
    }

    if (-not [string]::IsNullOrWhiteSpace($SupabaseUrl)) {
        try {
            $uri = [Uri]$SupabaseUrl
            $cloudTest = Test-NetConnection $uri.Host -Port 443 -WarningAction SilentlyContinue
            if ($cloudTest.TcpTestSucceeded) { Add-Result 'Supabase HTTPS' 'PASS' "$($uri.Host):443 reachable." }
            else { Add-Result 'Supabase HTTPS' 'MANUAL' "$($uri.Host):443 unreachable; authentication/cloud sync will fail." }
        } catch {
            Add-Result 'Supabase HTTPS' 'FAILED' $_.Exception.Message
        }
    }
}

if ($ConfigureFirewall) {
    if (-not (Test-IsAdministrator)) {
        Add-Result 'Windows Firewall' 'MANUAL' 'Rerun PowerShell as Administrator to open TCP 5048 and UDP 5050.'
    } else {
        foreach ($rule in @(
            @{ Name='ExamTransfer Backend TCP 5048'; Protocol='TCP'; Port=5048 },
            @{ Name='ExamTransfer Discovery UDP 5050'; Protocol='UDP'; Port=5050 }
        )) {
            $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
            if ($null -eq $existing) {
                New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Protocol $rule.Protocol -LocalPort $rule.Port | Out-Null
                Add-Result 'Windows Firewall' 'INSTALLED' "$($rule.Protocol) $($rule.Port) inbound rule created."
            } else {
                Add-Result 'Windows Firewall' 'FOUND' "$($rule.Protocol) $($rule.Port) rule already exists."
            }
        }
    }
} else {
    Add-Result 'Windows Firewall' 'SKIPPED' 'Use -ConfigureFirewall as Administrator for multi-machine LAN testing.'
}

if (-not $SkipBuild) {
    Push-Location $ProjectRoot
    try {
        Invoke-NativeChecked 'Restore solution' { dotnet restore $solution }
        if ($Mode -eq 'FullStack') {
            Invoke-NativeChecked 'Build backend' { dotnet build $backendSolution -c Debug --no-restore }
            Invoke-NativeChecked 'Initialize/migrate local SQLite' { dotnet run --project $migratorProject -c Debug }
            if (-not $SkipTests) {
                Invoke-NativeChecked 'Run backend automated tests' { dotnet test $backendSolution -c Debug --no-build }
            } else {
                Add-Result 'Backend tests' 'SKIPPED' 'Skipped by parameter.'
            }
        }
        Invoke-NativeChecked 'Build frontend' { dotnet build $frontendProject -c Debug --no-restore }
        Add-Result 'Build' 'PASS' "$Mode build completed."
    }
    finally {
        Pop-Location
    }
} else {
    Add-Result 'Build' 'SKIPPED' 'Skipped by parameter.'
}

if ($Mode -eq 'FrontendOnly') {
    if ($ApiUrl -eq 'http://localhost:5048') {
        Add-Result 'Frontend API URL' 'MANUAL' 'FrontendOnly normally needs the teacher/backend machine IP, e.g. -ApiUrl http://192.168.1.10:5048.'
    } else {
        try {
            $apiUri = [Uri]$ApiUrl
            $port = if ($apiUri.IsDefaultPort) { 80 } else { $apiUri.Port }
            $apiTest = Test-NetConnection $apiUri.Host -Port $port -WarningAction SilentlyContinue
            if ($apiTest.TcpTestSucceeded) { Add-Result 'Frontend API URL' 'PASS' "$ApiUrl reachable." }
            else { Add-Result 'Frontend API URL' 'MANUAL' "$ApiUrl unreachable. Check backend/firewall/LAN." }
        } catch {
            Add-Result 'Frontend API URL' 'FAILED' $_.Exception.Message
        }
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $reportDir "setup-report-$timestamp.json"
$txtPath = Join-Path $reportDir "setup-report-$timestamp.txt"
$report = [pscustomobject]@{
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Computer = $env:COMPUTERNAME
    User = $env:USERNAME
    Mode = $Mode
    ProjectRoot = $ProjectRoot
    Results = @($script:Results)
    ManualNotes = @($script:ManualNotes)
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$script:Results | Format-Table -AutoSize | Out-String | Set-Content -LiteralPath $txtPath -Encoding UTF8

Write-Host "`nSetup report:" -ForegroundColor Cyan
Write-Host $jsonPath
Write-Host $txtPath

$failures = @($script:Results | Where-Object { $_.Status -eq 'FAILED' })
$manual = @($script:Results | Where-Object { $_.Status -eq 'MANUAL' })
if ($failures.Count -gt 0) {
    Write-Host "Setup completed with $($failures.Count) failure(s)." -ForegroundColor Red
    exit 1
}
if ($manual.Count -gt 0) {
    Write-Host "Setup completed, but $($manual.Count) manual action(s) remain." -ForegroundColor Yellow
    exit 2
}
Write-Host 'Setup completed successfully.' -ForegroundColor Green
exit 0
