param(
    [switch]$FullStack,
    [switch]$FrontendOnly,
    [string]$SupabaseUrl,
    [string]$PublishableKey,
    [guid]$OrganizationId,
    [switch]$OpenAdminFirewall,
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$reportDirectory = Join-Path $projectRoot "TestResults\setup"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$report = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param([string]$Component, [string]$Status, [string]$Detail)
    $item = [pscustomobject]@{ component=$Component; status=$Status; detail=$Detail; timestampUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    $report.Add($item)
    Write-Host "$Status [$Component] $Detail"
}

function Invoke-Checked {
    param([string]$Component, [scriptblock]$Action)
    try {
        & $Action
        if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
        Add-Result $Component "FOUND" "Completed successfully"
    }
    catch {
        Add-Result $Component "FAILED" $_.Exception.Message
        throw
    }
}

if ($FullStack -and $FrontendOnly) { throw "Choose either -FullStack or -FrontendOnly, not both." }
if (-not $FullStack -and -not $FrontendOnly) { $FullStack = $true }

$winget = Get-Command winget -ErrorAction SilentlyContinue
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    Add-Result "Git" "FOUND" (& git --version)
}
elseif ($winget) {
    Add-Result "Git" "MISSING" "git was not found on PATH."
    & winget install --id Git.Git -e --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -eq 0) { Add-Result "Git" "INSTALLED" "Git.Git installed globally; reopen the terminal if PATH has not refreshed." }
    else { Add-Result "Git" "FAILED" "WinGet returned exit code $LASTEXITCODE"; throw "Git installation failed." }
}
else {
    Add-Result "Git" "MISSING" "git was not found on PATH."
    Add-Result "Git" "MANUAL" "WinGet is unavailable; install Git.Git globally."
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$hasSdk10 = $false
if ($dotnet) { $hasSdk10 = [bool]((& dotnet --list-sdks) | Where-Object { $_ -match '^10\.' }) }
if ($hasSdk10) {
    Add-Result ".NET SDK 10" "FOUND" ((& dotnet --list-sdks | Where-Object { $_ -match '^10\.' }) -join '; ')
}
elseif ($winget) {
    Add-Result ".NET SDK 10" "MISSING" "No globally installed 10.x SDK was found."
    & winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -eq 0) { Add-Result ".NET SDK 10" "INSTALLED" "Microsoft.DotNet.SDK.10 installed globally; reopen the terminal before continuing." }
    else { Add-Result ".NET SDK 10" "FAILED" "WinGet returned exit code $LASTEXITCODE"; throw ".NET SDK installation failed." }
}
else {
    Add-Result ".NET SDK 10" "MISSING" "No globally installed 10.x SDK was found."
    Add-Result ".NET SDK 10" "MANUAL" "WinGet is unavailable; install Microsoft.DotNet.SDK.10 globally."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Add-Result "Build" "MANUAL" "dotnet is not available in this process; reopen the terminal after installation and rerun setup."
} else {
    try {
        $nuget = Invoke-WebRequest -Uri "https://api.nuget.org/v3/index.json" -UseBasicParsing -TimeoutSec 15
        Add-Result "NuGet HTTPS" "FOUND" "HTTP $([int]$nuget.StatusCode) on TCP 443"
    } catch {
        Add-Result "NuGet HTTPS" "FAILED" $_.Exception.Message
        throw
    }

    if ($SupabaseUrl) {
        $uri = New-Object Uri($SupabaseUrl)
        try {
            $tcp = Test-NetConnection -ComputerName $uri.Host -Port 443 -WarningAction SilentlyContinue
            if ($tcp.TcpTestSucceeded) { Add-Result "Supabase HTTPS" "FOUND" "$($uri.Host):443 reachable" }
            else { Add-Result "Supabase HTTPS" "FAILED" "$($uri.Host):443 is not reachable" }
        } catch { Add-Result "Supabase HTTPS" "FAILED" $_.Exception.Message }
    } else {
        Add-Result "Supabase HTTPS" "MANUAL" "No SupabaseUrl supplied; cloud connectivity was not tested."
    }

    $solution = Join-Path $projectRoot "ExamTransfer.slnx"
    Invoke-Checked "Restore" { dotnet restore $solution }
    if ($FrontendOnly) {
        $frontend = Join-Path $projectRoot "frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj"
        Invoke-Checked "Frontend build" { dotnet build $frontend -c $Configuration --no-restore }
    } else {
        $backendSolution = Join-Path $projectRoot "backend\ExamTransfer.sln"
        Invoke-Checked "Backend build" { dotnet build $backendSolution -c $Configuration --no-restore }
        Invoke-Checked "Backend tests" { dotnet test $backendSolution -c $Configuration --no-build }
        $frontendVerifier = Join-Path $projectRoot "frontend\scripts\verify-frontend.ps1"
        Invoke-Checked "Frontend verify" { powershell -ExecutionPolicy Bypass -File $frontendVerifier -Configuration $Configuration }
        $migrator = Join-Path $projectRoot "backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj"
        Invoke-Checked "Local database migrate" { dotnet run --project $migrator -c $Configuration --no-build }
    }
}

if ($SupabaseUrl -and $PublishableKey -and $OrganizationId -ne [guid]::Empty) {
    $configure = Join-Path $projectRoot "backend\scripts\configure-supabase.ps1"
    & powershell -ExecutionPolicy Bypass -File $configure -SupabaseUrl $SupabaseUrl -PublishableKey $PublishableKey -OrganizationId $OrganizationId
    if ($LASTEXITCODE -eq 0) { Add-Result "Supabase config" "FOUND" "Runtime settings configured without a secret key." }
    else { Add-Result "Supabase config" "FAILED" "configure-supabase.ps1 returned $LASTEXITCODE" }
} else {
    Add-Result "Supabase config" "MANUAL" "Supply SupabaseUrl, PublishableKey and OrganizationId together to configure cloud sync. No secret key is requested."
}

if ($OpenAdminFirewall) {
    try {
        if (-not (Get-NetFirewallRule -DisplayName "ExamTransfer TCP 5048" -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName "ExamTransfer TCP 5048" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5048 | Out-Null
        }
        if (-not (Get-NetFirewallRule -DisplayName "ExamTransfer UDP 5050" -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName "ExamTransfer UDP 5050" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5050 | Out-Null
        }
        Add-Result "Firewall" "FOUND" "Inbound TCP 5048 and UDP 5050 rules are present."
    } catch {
        Add-Result "Firewall" "MANUAL" "Run an elevated PowerShell to add TCP 5048 and UDP 5050: $($_.Exception.Message)"
    }
} else {
    Add-Result "Firewall" "MANUAL" "Use -OpenAdminFirewall from elevated PowerShell on the teacher/admin host."
}

Add-Result "Optional tools" "MANUAL" "Visual Studio, Node.js, Docker, PostgreSQL, SQL Server and Supabase CLI are not required for app tests. Supabase CLI is maintainer-only."
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonPath = Join-Path $reportDirectory "setup-$stamp.json"
$textPath = Join-Path $reportDirectory "setup-$stamp.txt"
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$report | ForEach-Object { "$($_.status) [$($_.component)] $($_.detail)" } | Set-Content -LiteralPath $textPath -Encoding UTF8
Write-Host "Setup reports: $jsonPath ; $textPath"
