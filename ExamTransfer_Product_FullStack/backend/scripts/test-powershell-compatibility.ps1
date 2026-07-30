[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$repositoryRoot = (& git -C $projectRoot rev-parse --show-toplevel).Trim()
$compatibilityScript = Join-Path $projectRoot 'scripts\powershell-compat.ps1'
. $compatibilityScript

$expected = 'ExamTransfer_FullStack_v1.0.0_Source/ExamTransfer_Product_v1.0.0_FullStack'
$actual = (Get-RelativePathCompat -BasePath $repositoryRoot -TargetPath $projectRoot).Replace('\', '/')
if ($actual -cne $expected) {
    throw "Relative path mismatch. Expected '$expected'; actual '$actual'."
}
Write-Host "PASS relative path canonical=$actual" -ForegroundColor Green

$unicodeTarget = Join-Path $repositoryRoot 'Thư mục có khoảng trắng\fixture.txt'
$unicodeRelative = Get-RelativePathCompat -BasePath $repositoryRoot -TargetPath $unicodeTarget
if ($unicodeRelative -notmatch 'Thư mục có khoảng trắng') {
    throw "Unicode/space path was not preserved: $unicodeRelative"
}
Write-Host 'PASS relative path supports spaces and Vietnamese characters' -ForegroundColor Green

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "examtransfer-powershell-compat-$([Guid]::NewGuid().ToString('N'))"
$utf8Path = Join-Path $tempRoot 'utf8-no-bom.json'
try {
    $content = '{"message":"Tiếng Việt có dấu"}'
    Write-Utf8NoBomFile -Path $utf8Path -Content $content
    $bytes = [IO.File]::ReadAllBytes($utf8Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw 'UTF-8 output unexpectedly contains a BOM.'
    }
    if ([IO.File]::ReadAllText($utf8Path, [Text.Encoding]::UTF8) -cne $content) {
        throw 'UTF-8 output did not round-trip.'
    }
Write-Host 'PASS UTF-8 no-BOM round-trip' -ForegroundColor Green
} finally {
    $resolved = Resolve-Path -LiteralPath $tempRoot -ErrorAction SilentlyContinue
    if ($resolved -and $resolved.Path.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved.Path -Recurse -Force
    }
}

$dnsDefault = Add-SupabaseDnsResolverArguments -Arguments @('migration', 'list', '--db-url', 'postgres://example')
if (($dnsDefault -join '|') -cne 'migration|list|--db-url|postgres://example|--dns-resolver|https') {
    throw 'Supabase DNS arguments did not default to https.'
}
$dnsNative = Add-SupabaseDnsResolverArguments -Arguments @('db', 'push', '--dry-run') -DnsResolver 'native'
if (($dnsNative -join '|') -cne 'db|push|--dry-run|--dns-resolver|native') {
    throw 'Supabase DNS arguments did not retain native.'
}
try {
    Add-SupabaseDnsResolverArguments -Arguments @('migration', 'list') -DnsResolver 'invalid' | Out-Null
    throw 'Supabase DNS arguments accepted an invalid resolver.'
} catch [System.Management.Automation.ParameterBindingException] {
}
Write-Host 'PASS Supabase DNS arguments default to https, accept native, and reject invalid values' -ForegroundColor Green

$preflightScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-public-cloud-production-preflight.ps1') -Raw
foreach ($required in @(
    "@('migration', 'list', '--db-url', `$ConnectionString)",
    "@('db', 'push', '--db-url', `$ConnectionString, '--dry-run')",
    'Add-SupabaseDnsResolverArguments',
    '-SensitiveValues @($ConnectionString)')) {
    if ($preflightScript -notmatch [regex]::Escape($required)) {
        throw "Preflight DNS regression is missing: $required"
    }
}
if ($preflightScript -match "@\('migration', 'list', '--linked'\)|@\('db', 'push', '--linked', '--dry-run'\)") {
    throw 'Preflight DNS regression still uses linked database commands.'
}
Write-Host 'PASS production preflight uses db-url and DNS resolver argument arrays without logging the connection string' -ForegroundColor Green

function Test-OptionalDumpArgumentsBinding {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [AllowEmptyCollection()][string[]]$DumpArguments = @()
    )

    return [pscustomobject]@{ FileName = $FileName; DumpArguments = @($DumpArguments) }
}

$emptyDump = Test-OptionalDumpArgumentsBinding -FileName 'schema.sql' -DumpArguments @()
$defaultDump = Test-OptionalDumpArgumentsBinding -FileName 'schema.sql'
$dataDump = Test-OptionalDumpArgumentsBinding -FileName 'data.sql' -DumpArguments @('--data-only', '--use-copy')
$rolesDump = Test-OptionalDumpArgumentsBinding -FileName 'roles.sql' -DumpArguments @('--role-only')
$historyDump = Test-OptionalDumpArgumentsBinding -FileName 'migration-history-data.sql' -DumpArguments @('--schema', 'supabase_migrations')
if ($emptyDump.DumpArguments.Count -ne 0 -or $defaultDump.DumpArguments.Count -ne 0 -or
    ($dataDump.DumpArguments -join '|') -cne '--data-only|--use-copy' -or
    ($rolesDump.DumpArguments -join '|') -cne '--role-only' -or
    ($historyDump.DumpArguments -join '|') -cne '--schema|supabase_migrations') {
    throw 'Optional dump argument binding did not preserve empty or populated argument arrays.'
}
$backupScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup-supabase-production.ps1') -Raw
foreach ($required in @(
    '[AllowEmptyCollection()]',
    '[string[]]$DumpArguments = @()',
    "@('db', 'dump', '--db-url', `$DatabaseUrl, '-f', `$target)",
    'Add-SupabaseDnsResolverArguments -DnsResolver $DnsResolver')) {
    if ($backupScript -notmatch [regex]::Escape($required)) {
        throw "Production database backup regression is missing: $required"
    }
}
$backupWrapper = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup-supabase-production-all.ps1') -Raw
if ($backupWrapper -notmatch [regex]::Escape('-DnsResolver $DnsResolver') -or
    $backupWrapper -notmatch [regex]::Escape('-DatabaseUrl $DatabaseUrl') -or
    $backupWrapper -notmatch [regex]::Escape('-ServiceRoleKey $ServiceRoleKey')) {
    throw 'Backup wrapper no longer forwards the database URL, DNS resolver, or service-role key.'
}
Write-Host 'PASS optional backup dump arguments bind in PowerShell 5.1 and DNS wrapper forwarding is preserved' -ForegroundColor Green

$nativeFixture = Join-Path $tempRoot 'native-command-fixture.ps1'
try {
    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    $fixture = @'
param([int]$ExitCode, [string]$Secret)
[Console]::Out.WriteLine("stdout-captured")
[Console]::Error.WriteLine("stderr-captured postgres://postgres:$Secret@db.example.test:5432/postgres SUPABASE_ACCESS_TOKEN=$Secret")
exit $ExitCode
'@
    Write-Utf8NoBomFile -Path $nativeFixture -Content $fixture
    $shell = (Get-Process -Id $PID).Path
    $secret = 'sbp_regression-secret-value-123456789'

    $success = Invoke-NativeCommandCaptured -Command $shell `
        -Arguments @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $nativeFixture, '-ExitCode', '0', '-Secret', $secret) `
        -SensitiveValues @($secret)
    if ($success.ExitCode -ne 0 -or
        $success.OutputText -notmatch 'stdout-captured' -or
        $success.OutputText -notmatch 'stderr-captured') {
        throw 'Native success fixture did not preserve stdout, stderr, and exit code.'
    }
    if ($success.OutputText -match [regex]::Escape($secret) -or
        $success.OutputText -match 'postgres(?:ql)?://') {
        throw 'Native success fixture leaked a secret or PostgreSQL connection URL.'
    }
    Write-Host 'PASS native stderr with exit code 0 is captured without failure' -ForegroundColor Green

    try {
        Invoke-NativeCommandCaptured -Command $shell `
            -Arguments @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $nativeFixture, '-ExitCode', '1', '-Secret', $secret) `
            -SensitiveValues @($secret) | Out-Null
        throw 'Native failure fixture unexpectedly passed.'
    } catch {
        if ($_.Exception.Data['ExitCode'] -ne 1) {
            throw "Native failure fixture did not retain exit code 1. message=$($_.Exception.Message)"
        }
        if ($_.Exception.Message -match [regex]::Escape($secret) -or
            $_.Exception.Message -match 'postgres(?:ql)?://') {
            throw 'Native failure diagnostic leaked a secret or PostgreSQL connection URL.'
        }
        if ($_.Exception.Message -notmatch 'command=' -or
            $_.Exception.Message -notmatch 'exitCode=1' -or
            $_.Exception.Message -notmatch 'stderr-captured') {
            throw 'Native failure diagnostic omitted command, exit code, or captured output.'
        }
    }
    Write-Host 'PASS native stderr with exit code 1 fails with safe diagnostic and retained exit code' -ForegroundColor Green
} finally {
    $resolved = Resolve-Path -LiteralPath $tempRoot -ErrorAction SilentlyContinue
    if ($resolved -and $resolved.Path.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved.Path -Recurse -Force
    }
}

Write-Host "PASS PowerShell compatibility version=$($PSVersionTable.PSVersion)" -ForegroundColor Green
