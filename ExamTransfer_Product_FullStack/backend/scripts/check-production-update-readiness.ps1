[CmdletBinding()]
param(
    [switch]$RunDockerGates,
    [switch]$RunLanDiscoveryGate,
    [string]$LanBroadcastAddress = '255.255.255.255',
    [switch]$RunSupabaseLocalGates,
    [switch]$AllowRemoteReadAndDryRun,
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,
    [string]$ConfirmProjectRef,
    [string]$BackupSetPath,
    [string]$ReportPath,
    [ValidateSet('native', 'https')]
    [string]$DnsResolver = 'https'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectRoot = (Resolve-Path (Join-Path $backendRoot '..')).Path
$repositoryRoot = (& git -C $projectRoot rev-parse --show-toplevel).Trim()
. (Join-Path $projectRoot 'scripts\powershell-compat.ps1')
$results = [System.Collections.Generic.List[object]]::new()
$mandatorySkipped = $false
$readinessFixLogRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
) 'ExamTransfer-Private-Reports\Readiness-Fix'
[IO.Directory]::CreateDirectory($readinessFixLogRoot) | Out-Null

function Protect-ReadinessLogText {
    param([AllowEmptyString()][string]$Text)

    if ($null -eq $Text) { return '' }
    $safe = $Text -replace '(?i)(postgres(?:ql)?://[^:\s/]+:)[^@\s]+(@)', '$1<redacted>$2'
    $safe = $safe -replace '(?i)((?:password|access[_-]?token|refresh[_-]?token|service[_-]?role[_-]?key|hmac[_-]?secret|token[_-]?signing[_-]?key|receipt[_-]?signing[_-]?key)\s*[:=]\s*)\S+', '$1<redacted>'
    return $safe
}

function Get-ReadinessLogPath {
    param([Parameter(Mandatory)][string]$Name)

    $fileName = switch ($Name) {
        'Git structure' { 'git-structure.log' }
        'Frontend build and verification' { 'frontend-verification.log' }
        'Backup bucket and PowerShell compatibility' { 'backup-compatibility.log' }
        'Docker runtime health' { 'docker-runtime-health.log' }
        'Docker LAN discovery' { 'docker-lan-discovery.log' }
        'Supabase legacy migration upgrade' { 'supabase-migration-upgrade.log' }
        default {
            $slug = ($Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
            "gate-$slug.log"
        }
    }
    return Join-Path $readinessFixLogRoot $fileName
}

function Write-ReadinessLog {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines
    )

    $safeLines = @($Lines | ForEach-Object { Protect-ReadinessLogText "$_" })
    $payload = (@(
        "--- $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK') ---"
        $safeLines
    ) -join [Environment]::NewLine) + [Environment]::NewLine
    [IO.File]::AppendAllText($Path, $payload, [Text.Encoding]::UTF8)
}

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Detail)
    $script:results.Add([pscustomobject]@{ Gate = $Name; Status = $Status; Detail = $Detail })
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'SKIP' { 'Yellow' }
        default { 'Red' }
    }
    Write-Host ("{0,-6} {1} - {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
}

function Invoke-Gate {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Operation
    )
    $logPath = Get-ReadinessLogPath $Name
    $captured = [Collections.Generic.List[string]]::new()
    $failure = $null
    $exitCode = 0
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 represents native stderr as ErrorRecord objects.
        # Capture those records without turning successful Docker/Supabase progress
        # output into a terminating PowerShell error; the native exit code remains
        # authoritative and explicit PowerShell throws still terminate.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        & $Operation *>&1 | ForEach-Object { $captured.Add("$_") }
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { throw "Process exited with code $exitCode." }
    } catch {
        $failure = $_
        $exitCode = if ($LASTEXITCODE -is [int]) { $LASTEXITCODE } else { 1 }
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    foreach ($line in $captured) { Write-Host $line }
    Write-ReadinessLog -Path $logPath -Lines @(
        "GATE: $Name"
        "EXIT_CODE: $exitCode"
        "OPERATION: $($Operation.ToString().Trim())"
        $captured)

    if ($null -eq $failure) {
        Add-Result $Name 'PASS' "completed; log=$logPath"
        return $true
    }

    $lastError = @($captured | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1)
    $lastErrorText = if ($lastError.Count -eq 0) { $failure.Exception.Message } else { $lastError[0] }
    Add-Result $Name 'FAIL' "$($failure.Exception.Message) Log: $logPath Last error: $(Protect-ReadinessLogText $lastErrorText)"
    return $false
}

function Invoke-PowerShellFile {
    param([string]$File, [string[]]$Arguments = @())
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    $leaf = Split-Path $File -Leaf
    $logName = switch ($leaf) {
        'configure-docker-lan.ps1' { 'docker-lan-config.log' }
        'verify-frontend.ps1' { 'frontend-verification.log' }
        'test-backup-verifier-local.ps1' { 'backup-compatibility.log' }
        'test-docker-lan-discovery-integration.ps1' { 'docker-lan-discovery.log' }
        'test-public-cloud-migration-upgrade.ps1' { 'supabase-migration-upgrade.log' }
        default { "child-$($leaf -replace '\.ps1$','.log')" }
    }
    $logPath = Join-Path $readinessFixLogRoot $logName
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $shell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $File @Arguments *>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    foreach ($line in $output) { Write-Host "$line" }
    Write-ReadinessLog -Path $logPath -Lines @(
        "SCRIPT: $File"
        "ARGUMENTS: $($Arguments -join ' ')"
        "EXIT_CODE: $exitCode"
        @($output | ForEach-Object { "$_" }))
    if ($exitCode -ne 0) {
        $lastError = @($output | ForEach-Object { "$_" } | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        } | Select-Object -Last 1)
        $lastErrorText = if ($lastError.Count -eq 0) { 'No child output was captured.' } else { $lastError[0] }
        throw "$File exited with code $exitCode. Log: $logPath Last error: $(Protect-ReadinessLogText $lastErrorText)"
    }
}

function Write-ReadinessReport {
    param([Parameter(Mandatory)][string]$FinalStatus)

    $target = $ReportPath
    if ([string]::IsNullOrWhiteSpace($target)) {
        $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
        $directory = Join-Path $documents 'ExamTransfer-Private-Reports\Production-Readiness'
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $refPart = if ([string]::IsNullOrWhiteSpace($ProjectRef)) { 'local' } else { $ProjectRef }
        $target = Join-Path $directory ("readiness-{0}-{1}.json" -f $refPart,(Get-Date -Format 'yyyyMMdd-HHmmss'))
    } else {
        $target = [IO.Path]::GetFullPath($target)
        $directory = Split-Path $target -Parent
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
    }

    $payload = [ordered]@{
        formatVersion = 1
        kind = 'ExamTransferProductionReadiness'
        finalStatus = $FinalStatus
        projectRef = $ProjectRef
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        repositoryRoot = $repositoryRoot
        projectRoot = $projectRoot
        mandatorySkipped = $mandatorySkipped
        gates = @($results)
    }
    Write-Utf8NoBomFile -Path $target -Content ($payload | ConvertTo-Json -Depth 8)
    Write-Host "READINESS_REPORT path=$target" -ForegroundColor Cyan
    return $target
}

Push-Location $projectRoot
try {
    Invoke-Gate 'Git structure' {
        $statusLines = @(& git status --short)
        $deleted = @($statusLines | Where-Object {
            $_.Length -ge 2 -and $_.Substring(0, 2).Contains('D') -and
            $_ -match 'ExamTransfer_Product_v1\.0\.0_FullStack'
        })
        $relativeProject = (Get-RelativePathCompat -BasePath $repositoryRoot -TargetPath $projectRoot).Replace('\','/')
        $trackedCount = [int](& git -C $repositoryRoot ls-files -- "$relativeProject/**" | Measure-Object).Count
        if ($deleted.Count -gt 0 -or $trackedCount -eq 0) {
            throw 'Canonical source is deleted/untracked or contains no tracked files.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'backend\ExamTransfer.sln'))) {
            throw 'Canonical backend solution is missing.'
        }
        $shadowProject = Join-Path $repositoryRoot 'ExamTransfer_Product_v1.0.0_FullStack'
        if ((Test-Path -LiteralPath $shadowProject) -and
            ([IO.Path]::GetFullPath($shadowProject) -cne [IO.Path]::GetFullPath($projectRoot))) {
            throw "A shadow project directory exists outside the canonical source path: $shadowProject"
        }
    } | Out-Null

    Invoke-Gate 'Secret and backup tracking' {
        $forbiddenTracked = @(& git ls-files -- '.env' '.env.*' '*.dump' '*.backup' '*.sql.backup' 'backend/backups/**')
        $forbiddenTracked = @($forbiddenTracked | Where-Object {
            $_ -notmatch '(^|/)\.env(\.docker)?\.example$'
        })
        if ($forbiddenTracked.Count -gt 0) {
            throw "$($forbiddenTracked.Count) forbidden environment/backup file(s) are tracked."
        }

        & git grep -I -l -E '(SUPABASE_SERVICE_ROLE_KEY|SUPABASE_DB_URL|EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET|Security__TokenSigningKey|Security__ReceiptSigningKey)=[^[:space:]]{8,}' -- . ':(exclude)*.example' | Out-Null
        if ($LASTEXITCODE -eq 0) { throw 'A likely secret assignment exists in tracked non-example content.' }
        if ($LASTEXITCODE -ne 1) { throw 'Tracked-secret scan failed.' }

        & git check-ignore --quiet .env.docker
        if ($LASTEXITCODE -ne 0) { throw '.env.docker is not ignored.' }
        & git check-ignore --quiet backend/backups/supabase-production/probe.dump
        if ($LASTEXITCODE -ne 0) { throw 'Supabase backup output is not ignored.' }
    } | Out-Null

    Invoke-Gate 'Migration safety static checks' {
        $migration = Get-Content -LiteralPath (
            Join-Path $backendRoot 'supabase\migrations\20260722141147_public_classes_device_control.sql') -Raw
        if ($migration -match '(?is)create\s+unique\s+index[^;]+submission_files\s*\(\s*submission_id\s*\)\s*;') {
            throw 'Dangerous global submission_files(submission_id) unique index remains.'
        }
        if ($migration -notmatch "(?is)ux_public_submission_single_file.+?submission_files\s*\(\s*submission_id\s*\).+?where\s+source_mode\s*=\s*'PublicCloud'") {
            throw 'PublicCloud-only partial single-file index was not found.'
        }
        $preflight = Get-Content -LiteralPath (
            Join-Path $backendRoot 'supabase\preflight\public_cloud_production_legacy_preflight.sql') -Raw
        foreach ($term in @('BLOCKER', 'supabase_migrations', 'submission_files', 'organization_id', 'storage.buckets', 'report-exports', 'backup-archives')) {
            if ($preflight -notmatch [regex]::Escape($term)) { throw "Preflight check is missing: $term" }
        }
        if ($preflight -match "'exports'|'backups'") {
            throw 'Preflight still contains obsolete Storage bucket IDs exports/backups.'
        }
    } | Out-Null

    Invoke-Gate '.NET restore' { & dotnet restore (Join-Path $projectRoot 'ExamTransfer.slnx') } | Out-Null
    Invoke-Gate 'Backend build and tests' {
        & dotnet build (Join-Path $backendRoot 'ExamTransfer.sln') -c Debug --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }
        & dotnet test (Join-Path $backendRoot 'ExamTransfer.sln') -c Debug --no-build
    } | Out-Null
    Invoke-Gate 'Frontend build and verification' {
        Invoke-PowerShellFile (Join-Path $projectRoot 'frontend\scripts\verify-frontend.ps1') @(
            '-Configuration', 'Debug')
    } | Out-Null

    Invoke-Gate 'Backup script static validation' {
        foreach ($name in @(
            'backup-supabase-production.ps1',
            'backup-supabase-storage.ps1',
            'backup-supabase-production-all.ps1',
            'verify-supabase-production-backup.ps1',
            'apply-supabase-production-update.ps1',
            'repair-supabase-production-student-role.ps1',
            'test-supabase-production-student-role-repair-guard.ps1')) {
            $path = Join-Path $PSScriptRoot $name
            if (-not (Test-Path -LiteralPath $path)) { throw "Missing $name" }
            $tokens = $null
            $parseErrors = $null
            [void][Management.Automation.Language.Parser]::ParseFile(
                $path, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -gt 0) { throw "$name has PowerShell parser errors." }
        }
    } | Out-Null

    Invoke-Gate 'PowerShell 5.1 compatibility regression' {
        Invoke-PowerShellFile (Join-Path $PSScriptRoot 'test-powershell-compatibility.ps1')
    } | Out-Null

    Invoke-Gate 'Backup verifier local regression' {
        Invoke-PowerShellFile (Join-Path $PSScriptRoot 'test-backup-verifier-local.ps1')
    } | Out-Null

    Invoke-Gate 'Production student role repair guard regression' {
        Invoke-PowerShellFile (
            Join-Path $PSScriptRoot 'test-supabase-production-student-role-repair-guard.ps1')
    } | Out-Null

    Invoke-Gate 'Production write guard' {
        $legacyPush = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'push-supabase-schema.ps1') -Raw
        if ($legacyPush -notmatch 'legacy remote-write script is intentionally disabled') {
            throw 'Legacy push-supabase-schema.ps1 is not safely disabled.'
        }
        $apply = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'apply-supabase-production-update.ps1') -Raw
        foreach ($term in @(
            'BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE',
            'verify-supabase-production-backup.ps1',
            'test-public-cloud-production-preflight.ps1',
            'Invoke-NativeCommandCaptured',
            "@('db', 'push', '--db-url', `$ConnectionString)",
            'Add-SupabaseDnsResolverArguments',
            'EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET')) {
            if ($apply -notmatch [regex]::Escape($term)) {
                throw "Production update guard is missing: $term"
            }
        }
        $upgrade = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-public-cloud-migration-upgrade.ps1') -Raw
        if ($upgrade -match '(?i)--sql-paths') {
            throw 'Migration upgrade test still uses unsupported db reset --sql-paths.'
        }
        if ($upgrade -notmatch '(?i)psql.+legacy_public_cloud_fixture|Invoke-SqlFile') {
            throw 'Migration upgrade test does not explicitly load the legacy fixture through psql.'
        }
    } | Out-Null

    Invoke-Gate 'Backup bucket and PowerShell compatibility' {
        $storageBackup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup-supabase-storage.ps1') -Raw
        foreach ($bucket in @('exam-archives','submission-archives','public-submission-archives','report-exports','backup-archives')) {
            if ($storageBackup -notmatch [regex]::Escape($bucket)) { throw "Storage backup omits bucket: $bucket" }
        }
        if ($storageBackup -match "'exports'|'backups'") { throw 'Storage backup contains obsolete bucket IDs.' }
        foreach ($name in @('backup-supabase-production.ps1','backup-supabase-storage.ps1','backup-supabase-production-all.ps1')) {
            $unsupportedEncodingPattern = '(?i)-Encoding\s+' + 'utf8' + 'NoBOM\b'
            if ((Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw) -match $unsupportedEncodingPattern) {
                throw "$name uses a PowerShell 7-only UTF-8 no-BOM encoding token."
            }
        }
    } | Out-Null

    Invoke-Gate 'Edge Function static checks' {
        foreach ($name in @(
            'verify-public-submission-archive',
            'issue-public-device-command',
            'get-public-exam-file-url')) {
            if (-not (Test-Path -LiteralPath (Join-Path $backendRoot "supabase\functions\$name\index.ts"))) {
                throw "Edge Function is missing: $name"
            }
        }
        $verifier = Get-Content -LiteralPath (
            Join-Path $backendRoot 'supabase\functions\verify-public-submission-archive\index.ts') -Raw
        if ($verifier -match '(?i)\.from\(["'']submission_files["'']\)\s*\.update') {
            throw 'Archive verifier directly updates submission_files.'
        }
    } | Out-Null

    if ($RunDockerGates) {
        Invoke-Gate 'Docker compose config' { & docker compose config --quiet } | Out-Null
        Invoke-Gate 'Docker no-cache build' { & docker compose build --no-cache } | Out-Null
        Invoke-Gate 'Docker backend tests' { & docker compose run --rm backend-tests } | Out-Null
        Invoke-Gate 'Docker runtime health' {
            $localEnvPath = Join-Path $projectRoot '.env.docker'
            if (-not (Test-Path -LiteralPath $localEnvPath)) { throw '.env.docker is missing.' }
            $safeValues = @{}
            Get-Content -LiteralPath $localEnvPath | ForEach-Object {
                if ($_ -match '^([^#=]+)=(.*)$') { $safeValues[$matches[1]] = $matches[2] }
            }
            $preferredIp = [string]$safeValues['Server__PreferredIp']
            $allowedCidr = [string]$safeValues['LanAccess__AllowedCidrs__0']
            if ([string]::IsNullOrWhiteSpace($preferredIp) -or [string]::IsNullOrWhiteSpace($allowedCidr)) {
                throw 'Run configure-docker-lan.ps1 before the runtime gate.'
            }
            Invoke-PowerShellFile (Join-Path $projectRoot 'scripts\configure-docker-lan.ps1') @(
                '-PreferredIp', $preferredIp,
                '-AllowedCidr', $allowedCidr,
                '-EnvironmentFile', $localEnvPath,
                '-NonInteractive',
                '-ValidateOnly')

            $tempDirectory = Join-Path ([IO.Path]::GetTempPath()) "examtransfer-readiness-$([Guid]::NewGuid().ToString('N'))"
            $tempEnv = Join-Path $tempDirectory '.env.docker'
            New-Item -ItemType Directory -Path $tempDirectory | Out-Null
            $keyBytes = New-Object byte[] 32
            $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
            $testKey = [Convert]::ToBase64String($keyBytes)
            [IO.File]::WriteAllLines($tempEnv, @(
                'ASPNETCORE_ENVIRONMENT=Development',
                'Server__Port=5048',
                'Server__UseHttps=false',
                "Server__PreferredIp=$preferredIp",
                'Discovery__Enabled=true',
                'Discovery__Protocol=UdpBroadcast',
                'Discovery__Port=40550',
                'Discovery__RequestMagic=EXAMTRANSFER_DISCOVER_V1',
                "LanAccess__AllowedCidrs__0=$allowedCidr",
                'LanAccess__TrustDockerDesktopNat=false',
                'Storage__RootPath=/data/ExamTransfer',
                'Storage__MinFreeBytes=1',
                "Security__TokenSigningKey=$testKey",
                "Security__ReceiptSigningKey=$testKey",
                'Cloud__Enabled=false'
            ), (New-Utf8NoBomEncoding))

            # Run the health gate in an isolated Compose project. Never recreate the
            # normal ExamTransfer container or reuse its production-like local volumes.
            $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
            $gateProject = "examtransfer-readiness-$suffix"
            $gateContainer = "$gateProject-backend"
            $gateDataVolume = "$gateProject-data"
            $gateRuntimeVolume = "$gateProject-runtime"
            $hostTcpPort = Get-Random -Minimum 21000 -Maximum 28000
            $hostUdpPort = Get-Random -Minimum 28001 -Maximum 35000
            $previousEnvironment = @{
                EXAMTRANSFER_ENV_FILE = $env:EXAMTRANSFER_ENV_FILE
                EXAMTRANSFER_DATA_VOLUME = $env:EXAMTRANSFER_DATA_VOLUME
                EXAMTRANSFER_RUNTIME_VOLUME = $env:EXAMTRANSFER_RUNTIME_VOLUME
                EXAMTRANSFER_CONTAINER_NAME = $env:EXAMTRANSFER_CONTAINER_NAME
                EXAMTRANSFER_TCP_PORT = $env:EXAMTRANSFER_TCP_PORT
                EXAMTRANSFER_UDP_PORT = $env:EXAMTRANSFER_UDP_PORT
            }
            try {
                $env:EXAMTRANSFER_ENV_FILE = $tempEnv
                $env:EXAMTRANSFER_DATA_VOLUME = $gateDataVolume
                $env:EXAMTRANSFER_RUNTIME_VOLUME = $gateRuntimeVolume
                $env:EXAMTRANSFER_CONTAINER_NAME = $gateContainer
                $env:EXAMTRANSFER_TCP_PORT = [string]$hostTcpPort
                $env:EXAMTRANSFER_UDP_PORT = [string]$hostUdpPort

                & docker compose -p $gateProject up -d --build backend
                if ($LASTEXITCODE -ne 0) { throw 'Isolated Docker readiness container failed to start.' }

                $health = $null
                for ($attempt = 0; $attempt -lt 45; $attempt++) {
                    try {
                        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$hostTcpPort/health" -TimeoutSec 3
                        break
                    } catch { Start-Sleep -Seconds 1 }
                }
                if ($null -eq $health -or $health.status -eq 'Unhealthy') {
                    throw 'Isolated Docker health report is unavailable or Unhealthy.'
                }
                if ($health.sqlite.status -ne 'Healthy' -or $health.volumeWritable.status -ne 'Healthy' -or
                    $health.dataProtectionKeys.status -ne 'Healthy') {
                    throw 'A critical persistent runtime component is not Healthy.'
                }
                if ($health.advertisedAddress -cne $preferredIp) {
                    throw 'Docker health does not advertise the configured Windows host IP.'
                }
            } finally {
                if ($gateProject.StartsWith('examtransfer-readiness-', [StringComparison]::Ordinal)) {
                    & docker compose -p $gateProject down -v *> $null
                }
                foreach ($name in $previousEnvironment.Keys) {
                    [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
                }
                $resolvedTemp = Resolve-Path -LiteralPath $tempDirectory -ErrorAction SilentlyContinue
                if ($resolvedTemp -and $resolvedTemp.Path.StartsWith(
                    [IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
                    Remove-Item -LiteralPath $resolvedTemp.Path -Recurse -Force
                }
            }
        } | Out-Null
        Invoke-Gate 'Docker persistence' {
            Invoke-PowerShellFile (Join-Path $projectRoot 'scripts\test-docker-persistence.ps1') @('-Cleanup')
        } | Out-Null
    } else {
        $mandatorySkipped = $true
        Add-Result 'Docker build, tests, health, persistence' 'SKIP' 'rerun with -RunDockerGates'
    }

    if ($RunLanDiscoveryGate) {
        Invoke-Gate 'Docker LAN discovery' {
            Invoke-PowerShellFile (Join-Path $projectRoot 'scripts\test-docker-lan-discovery-integration.ps1')
        } | Out-Null
    } else {
        $mandatorySkipped = $true
        Add-Result 'Docker LAN advertised IP and discovery' 'SKIP' 'rerun with -RunLanDiscoveryGate while a LanOnly room is accepting'
    }

    if ($RunSupabaseLocalGates) {
        Invoke-Gate 'Supabase legacy migration upgrade' {
            Invoke-PowerShellFile (Join-Path $PSScriptRoot 'test-public-cloud-migration-upgrade.ps1')
        } | Out-Null
        Invoke-Gate 'Supabase empty reset and pgTAP' {
            Push-Location $backendRoot
            try {
                & supabase db reset --local
                if ($LASTEXITCODE -ne 0) { throw 'supabase db reset failed.' }
                & supabase test db --local
                if ($LASTEXITCODE -ne 0) { throw 'supabase test db failed.' }
                & supabase db lint --local --level warning
            } finally {
                Pop-Location
            }
        } | Out-Null
    } else {
        $mandatorySkipped = $true
        Add-Result 'Supabase local reset, pgTAP, lint, upgrade' 'SKIP' 'rerun with -RunSupabaseLocalGates'
    }

    $remotePassed = $false
    if ($AllowRemoteReadAndDryRun) {
        if ([string]::IsNullOrWhiteSpace($ProjectRef) -or $ConfirmProjectRef -cne $ProjectRef) {
            Add-Result 'Production read-only preflight and dry-run' 'FAIL' 'ProjectRef and exact ConfirmProjectRef are required'
        } else {
            $remotePassed = Invoke-Gate 'Production read-only preflight and dry-run' {
                Invoke-PowerShellFile (Join-Path $PSScriptRoot 'test-public-cloud-production-preflight.ps1') @(
                    '-ProjectRef', $ProjectRef,
                    '-ConfirmProjectRef', $ConfirmProjectRef,
                    '-AllowRemoteReadAndDryRun',
                    '-NonInteractive',
                    '-DnsResolver', $DnsResolver)
            }
        }
    } else {
        Add-Result 'Production read-only preflight and dry-run' 'SKIP' 'not authorized; no remote command was run'
    }

    $backupVerified = $false
    if (-not [string]::IsNullOrWhiteSpace($BackupSetPath)) {
        if ([string]::IsNullOrWhiteSpace($ProjectRef)) {
            Add-Result 'Production backup verification' 'FAIL' 'ProjectRef is required with BackupSetPath'
        } else {
            $backupVerified = Invoke-Gate 'Production backup verification' {
                Invoke-PowerShellFile (Join-Path $PSScriptRoot 'verify-supabase-production-backup.ps1') @(
                    '-ProjectRef', $ProjectRef,
                    '-BackupSetPath', $BackupSetPath)
            }
        }
    } else {
        Add-Result 'Production backup verification' 'SKIP' 'no production backup was supplied'
    }

    Invoke-Gate 'Git diff check' { & git diff --check } | Out-Null

    $failed = @($results | Where-Object Status -eq 'FAIL').Count
    Write-Host ''
    Write-Host 'Readiness summary' -ForegroundColor Cyan
    $results | Format-Table -AutoSize | Out-Host

    if ($failed -gt 0 -or $mandatorySkipped) {
        [void](Write-ReadinessReport -FinalStatus 'NOT_READY')
        Write-Host 'NOT_READY' -ForegroundColor Red
        exit 2
    }
    if ($backupVerified -and $remotePassed) {
        [void](Write-ReadinessReport -FinalStatus 'BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE')
        Write-Host 'BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE' -ForegroundColor Green
        exit 0
    }
    [void](Write-ReadinessReport -FinalStatus 'READY_FOR_BACKUP')
    Write-Host 'READY_FOR_BACKUP' -ForegroundColor Green
    exit 0
} finally {
    Pop-Location
}
