[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

if (-not (Get-Command supabase -ErrorAction SilentlyContinue)) {
    throw 'Supabase CLI is required.'
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required for the local Supabase upgrade test.'
}

$fixturePath = Join-Path $backendRoot 'supabase\upgrade-tests\legacy_public_cloud_fixture.sql'
$verificationPath = Join-Path $backendRoot 'supabase\upgrade-tests\verify_legacy_public_cloud_upgrade.sql'
$legacyTarget = '202607220001'

function Protect-DatabaseUrl {
    param([Parameter(Mandatory)][string]$DatabaseUrl)

    return ($DatabaseUrl -replace '(?<=://[^:]+:)[^@]+(?=@)', '***')
}

function Assert-SupabaseHelp {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string[]]$RequiredTokens
    )

    Write-Host "COMMAND supabase $($Arguments -join ' ')"
    $result = Invoke-NativeCommandCaptured -Command 'supabase' -Arguments $Arguments
    $helpText = $result.OutputText
    $exitCode = $result.ExitCode
    Write-Host "EXIT command=supabase $($Arguments -join ' ') code=$exitCode"

    if ($exitCode -ne 0) {
        throw "Supabase CLI help failed for '$($Arguments -join ' ')' (exit=$exitCode)."
    }
    foreach ($token in $RequiredTokens) {
        if ($helpText -notmatch [regex]::Escape($token)) {
            throw "Supabase CLI '$($Arguments -join ' ')' does not support required option $token."
        }
    }
    Write-Host "PASS CLI options command=supabase $($Arguments -join ' ') required=$($RequiredTokens -join ',')"
}

function Invoke-SupabasePassthrough {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )

    Write-Host "COMMAND supabase $($Arguments -join ' ')"
    $result = Invoke-NativeCommandCaptured -Command 'supabase' -Arguments $Arguments
    $result.Output | ForEach-Object { Write-Host ([string]$_) }
    $exitCode = $result.ExitCode
    Write-Host "EXIT command=supabase $($Arguments -join ' ') code=$exitCode"

    if ($exitCode -ne 0) {
        throw "$Description failed (exit=$exitCode)."
    }
}

function Invoke-SqlFile {
    param(
        [Parameter(Mandatory)][string]$DatabaseUrl,
        [Parameter(Mandatory)][string]$SqlPath,
        [Parameter(Mandatory)][string]$Description
    )

    $maskedDatabaseUrl = Protect-DatabaseUrl -DatabaseUrl $DatabaseUrl
    if (Get-Command psql -ErrorAction SilentlyContinue) {
        Write-Host "COMMAND psql $maskedDatabaseUrl -X -v ON_ERROR_STOP=1 -f `"$SqlPath`""
        & psql $DatabaseUrl -X -v ON_ERROR_STOP=1 -f $SqlPath
        $exitCode = $LASTEXITCODE
        Write-Host "EXIT command=psql file=$SqlPath code=$exitCode"
        if ($exitCode -ne 0) { throw "$Description failed through local psql." }
        return
    }

    $sqlDirectory = (Resolve-Path (Split-Path $SqlPath -Parent)).Path
    $sqlFileName = Split-Path $SqlPath -Leaf
    $dockerDatabaseUrl = $DatabaseUrl `
        -replace '@127\.0\.0\.1:', '@host.docker.internal:' `
        -replace '@localhost:', '@host.docker.internal:'
    $mount = "type=bind,source=$sqlDirectory,target=/work,readonly"

    Write-Host "COMMAND docker run --rm --mount <fixture-directory> postgres:17-alpine psql $maskedDatabaseUrl -X -v ON_ERROR_STOP=1 -f /work/$sqlFileName"
    & docker run --rm `
        --add-host 'host.docker.internal:host-gateway' `
        --mount $mount `
        postgres:17-alpine `
        psql $dockerDatabaseUrl -X -v ON_ERROR_STOP=1 -f "/work/$sqlFileName"
    $exitCode = $LASTEXITCODE
    Write-Host "EXIT command=docker-psql file=$SqlPath code=$exitCode"
    if ($exitCode -ne 0) { throw "$Description failed through Docker psql." }
}

function Get-LocalSupabaseStatus {
    param([switch]$AllowUnavailable)

    Write-Host 'COMMAND supabase status --output json'
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 promotes native stderr (including harmless
        # "Stopped services" diagnostics) to ErrorRecord objects. The CLI exit
        # code and JSON stdout are authoritative for this read-only command.
        $ErrorActionPreference = 'Continue'
        $statusText = (& supabase status --output json 2>$null | Out-String).Trim()
        $statusExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    Write-Host "EXIT command=supabase status --output json code=$statusExitCode"

    if ($statusExitCode -ne 0) {
        if ($AllowUnavailable) {
            return $null
        }
        throw "Could not obtain local Supabase status (exit=$statusExitCode)."
    }
    if ([string]::IsNullOrWhiteSpace($statusText)) {
        throw 'Could not obtain the local Supabase database URL.'
    }

    try {
        return ($statusText | ConvertFrom-Json)
    } catch {
        throw 'Local Supabase status did not return valid JSON.'
    }
}

Push-Location $backendRoot
try {
    Assert-SupabaseHelp -Arguments @('db','reset','--help') `
        -RequiredTokens @('--local','--version','--no-seed')
    Assert-SupabaseHelp -Arguments @('migration','up','--help') `
        -RequiredTokens @('--local')
    Assert-SupabaseHelp -Arguments @('status','--help') `
        -RequiredTokens @('--output')

    $status = Get-LocalSupabaseStatus -AllowUnavailable
    if ($null -eq $status) {
        # The upgrade test needs only local Postgres. Excluding unrelated
        # services reduces RAM use and host-port conflicts.
        Invoke-SupabasePassthrough `
            -Arguments @('start','--exclude','edge-runtime,gotrue,imgproxy,kong,logflare,mailpit,postgres-meta,postgrest,realtime,storage-api,studio,supavisor,vector') `
            -Description 'Starting local Supabase Postgres'
    }

    # Reset exactly to the final migration before PublicCloud. The current
    # Supabase CLI has no supported reset flag for a one-off fixture, so the legacy
    # fixture is loaded explicitly through psql after the reset.
    Write-Host "PRE_UPGRADE_MIGRATION_TARGET=$legacyTarget"
    Invoke-SupabasePassthrough `
        -Arguments @('db','reset','--local','--version',$legacyTarget,'--no-seed') `
        -Description 'Resetting to the pre-PublicCloud schema'

    $status = Get-LocalSupabaseStatus
    $databaseUrl = [string]$status.DB_URL
    if ([string]::IsNullOrWhiteSpace($databaseUrl)) {
        throw 'Local Supabase status did not include DB_URL.'
    }
    Write-Host "LOCAL_DB_URL=$(Protect-DatabaseUrl -DatabaseUrl $databaseUrl)"

    Write-Host "FIXTURE path=$fixturePath"
    Invoke-SqlFile -DatabaseUrl $databaseUrl -SqlPath $fixturePath `
        -Description 'Loading the legacy PublicCloud upgrade fixture'
    Write-Host 'PASS fixture load and pre-upgrade schema assertions'

    $expectedMigrations = @(
        Get-ChildItem -LiteralPath (Join-Path $backendRoot 'supabase\migrations') -File -Filter '*.sql' |
            ForEach-Object {
                [pscustomobject]@{
                    File = $_
                    Version = $_.BaseName.Split('_')[0]
                }
            } |
            Where-Object { [string]::CompareOrdinal($_.Version, $legacyTarget) -gt 0 } |
            Sort-Object Version |
            ForEach-Object { $_.Version }
    )
    Write-Host "EXPECTED_UPGRADE_MIGRATIONS=$($expectedMigrations -join ',')"
    Invoke-SupabasePassthrough `
        -Arguments @('migration','up','--local') `
        -Description 'Applying PublicCloud migrations to the legacy fixture'

    # Run the focused verification file through pgTAP. The CLI wraps this test
    # in a transaction and returns a non-zero exit code on assertion failure.
    Write-Host "VERIFY_QUERY path=$verificationPath"
    Invoke-SupabasePassthrough `
        -Arguments @('test','db','--local',$verificationPath) `
        -Description 'Legacy PublicCloud upgrade assertions'

    Invoke-SupabasePassthrough `
        -Arguments @('db','lint','--local','--level','warning') `
        -Description 'Linting the upgraded legacy schema'

    Write-Host 'PASS code=PUBLIC_CLOUD_LEGACY_UPGRADE_OK detail=legacy organization, teacher, student, class member, LAN multi-file submission, partial indexes, RLS, bucket, cursor, RPCs, pgTAP and lint verified' -ForegroundColor Green
} finally {
    Pop-Location
}
