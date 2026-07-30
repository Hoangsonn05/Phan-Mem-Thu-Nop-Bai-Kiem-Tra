[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$rootBuild = Join-Path $root 'build-release.ps1'
$sharedValidation = Join-Path $PSScriptRoot 'public-config-packaging.ps1'

foreach ($requiredFile in @($rootBuild, $sharedValidation)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required packaging source was not found: $requiredFile"
    }
}
. $sharedValidation

function Invoke-InvalidBuildPreflight(
    [string]$Name,
    [string]$Url,
    [string]$Key,
    [string]$OrganizationId,
    [string]$ExpectedCode) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')
    $psi.Arguments =
        '-NoProfile -ExecutionPolicy Bypass -File "' + $rootBuild +
        '" -Version 0.0.0 -SkipTests'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['EXAMTRANSFER_SUPABASE_URL'] = $Url
    $psi.EnvironmentVariables['EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY'] = $Key
    $psi.EnvironmentVariables['EXAMTRANSFER_ORGANIZATION_ID'] = $OrganizationId

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $combined = $stdout + [Environment]::NewLine + $stderr

        if ($process.ExitCode -eq 0) {
            throw "Packaging preflight case unexpectedly passed: $Name"
        }
        if ($combined.IndexOf($ExpectedCode, [StringComparison]::Ordinal) -lt 0) {
            throw "Packaging preflight case returned the wrong error: $Name"
        }
        if ($combined.IndexOf(
                '=== ExamTransfer release',
                [StringComparison]::Ordinal) -ge 0 -or
            $combined.IndexOf(
                'Build installer',
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Packaging preflight reached build or ISCC work: $Name"
        }
    }
    finally {
        $process.Dispose()
    }

    Write-Host "PASS public-config pre-ISCC rejection case=$Name"
}

$validUrl = 'https://packaging-gate.supabase.co'
$validKey = 'sb_publishable_12345678901234567890'
$validOrganizationId = '7bce49ea-6b33-4be0-ab90-3835f0f75a54'

$cases = @(
    @{
        Name = 'organization-empty'
        Url = $validUrl
        Key = $validKey
        OrganizationId = ''
        Code = 'PUBLICCLOUD_INVALID_ORGANIZATION_ID'
    },
    @{
        Name = 'organization-not-uuid'
        Url = $validUrl
        Key = $validKey
        OrganizationId = 'not-a-uuid'
        Code = 'PUBLICCLOUD_INVALID_ORGANIZATION_ID'
    },
    @{
        Name = 'organization-project-ref'
        Url = $validUrl
        Key = $validKey
        OrganizationId = 'abcdefghijklmnopqrst'
        Code = 'PUBLICCLOUD_INVALID_ORGANIZATION_ID'
    },
    @{
        Name = 'organization-slug'
        Url = $validUrl
        Key = $validKey
        OrganizationId = 'examtransfer-production'
        Code = 'PUBLICCLOUD_INVALID_ORGANIZATION_ID'
    },
    @{
        Name = 'url-not-https'
        Url = 'http://packaging-gate.supabase.co'
        Key = $validKey
        OrganizationId = $validOrganizationId
        Code = 'PUBLICCLOUD_INVALID_URL'
    },
    @{
        Name = 'publishable-key-empty'
        Url = $validUrl
        Key = ''
        OrganizationId = $validOrganizationId
        Code = 'PUBLICCLOUD_INVALID_PUBLISHABLE_KEY'
    }
)

foreach ($case in $cases) {
    Invoke-InvalidBuildPreflight `
        -Name $case.Name `
        -Url $case.Url `
        -Key $case.Key `
        -OrganizationId $case.OrganizationId `
        -ExpectedCode $case.Code
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ExamTransfer-PublicConfigUnit-' + [Guid]::NewGuid().ToString('N'))
try {
    $roundtripPath = Join-Path $temporaryRoot 'publiccloud.runtime.json'
    $expected = New-PublicCloudConfig `
        -SupabaseUrl $validUrl `
        -PublishableKey $validKey `
        -OrganizationId $validOrganizationId
    Write-PublicCloudConfig -Path $roundtripPath -Config $expected
    $actual = Read-PublicCloudConfig -Path $roundtripPath
    Assert-PublicCloudConfigEqual `
        -Expected $expected `
        -Actual $actual `
        -Stage 'unit-roundtrip'
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTarget = [IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTarget.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTarget).StartsWith(
            'ExamTransfer-PublicConfigUnit-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$unexpectedInstaller = Join-Path $root 'artifacts\installer\ExamTransfer-Setup-0.0.0.exe'
if (Test-Path -LiteralPath $unexpectedInstaller -PathType Leaf) {
    throw 'Invalid preflight reached ISCC and created an installer.'
}

Write-Host 'PASS public-config packaging preflight cases=6 valid-roundtrip=verified ISCC=not-reached' -ForegroundColor Green
