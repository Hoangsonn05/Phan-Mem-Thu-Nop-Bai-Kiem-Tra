[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

$fixtures = @(
    @{ Name = 'empty-array'; Response = @(); Count = 0 },
    @{ Name = 'one-item-array'; Response = @([pscustomobject]@{ name = 'hidden'; id = 'hidden' }); Count = 1 },
    @{ Name = 'multi-item-array'; Response = @(
            [pscustomobject]@{ name = 'hidden-a' },
            [pscustomobject]@{ name = 'hidden-b' },
            [pscustomobject]@{ name = 'hidden-c' }
        ); Count = 3 },
    @{ Name = 'single-object'; Response = [pscustomobject]@{ name = 'hidden'; id = 'hidden' }; Count = 1 },
    @{ Name = 'data-wrapper'; Response = [pscustomobject]@{ data = @([pscustomobject]@{ name = 'hidden' }) }; Count = 1 },
    @{ Name = 'objects-wrapper'; Response = [pscustomobject]@{ objects = @([pscustomobject]@{ name = 'hidden' }) }; Count = 1 },
    @{ Name = 'null'; Response = $null; Count = 0 }
)

foreach ($fixture in $fixtures) {
    $items = @(ConvertTo-StorageObjectItems -Response $fixture.Response)
    if ($items.Count -ne $fixture.Count -or @($items | Where-Object { $_ -is [array] }).Count -ne 0) {
        throw "Storage object collection fixture failed: $($fixture.Name)"
    }
    if ($items.Count -gt 0 -and $items[0] -isnot [pscustomobject]) {
        throw "Storage object collection fixture did not emit PSCustomObject: $($fixture.Name)"
    }
    Write-Host "PASS storage object response shape=$($fixture.Name)"
}

$nestedItemArray = [object[]]@([pscustomobject]@{ name = 'hidden' })
$nestedResponse = New-Object object[] 1
$nestedResponse[0] = $nestedItemArray
$failureFixtures = @(
    @{ Name = 'api-error'; Response = [pscustomobject]@{ code = 'Unauthorized'; message = 'hidden' } },
    @{ Name = 'unknown-object'; Response = [pscustomobject]@{ unexpected = $true } },
    @{ Name = 'nested-array'; Response = $nestedResponse }
)
foreach ($failure in $failureFixtures) {
    try {
        @(ConvertTo-StorageObjectItems -Response $failure.Response) | Out-Null
        throw "Unsupported Storage object fixture unexpectedly succeeded: $($failure.Name)"
    } catch {
        if ($_.Exception.Message -notmatch 'STORAGE_OBJECT_(API_ERROR|RESPONSE_UNSUPPORTED|NESTED_COLLECTION_UNSUPPORTED)') {
            throw
        }
    }
}
Write-Host 'PASS storage object error, unsupported-shape, and nested-array regressions'

$secret = 'service-role-object-shape-secret'
$safe = Protect-NativeCommandOutput -Text "Authorization: Bearer $secret apikey=$secret object=hidden" `
    -SensitiveValues @($secret)
if ($safe -match [regex]::Escape($secret)) { throw 'Object diagnostic redaction leaked a credential.' }
Write-Host 'PASS storage object response fixture redaction' -ForegroundColor Green

$inspectorPath = Join-Path $PSScriptRoot 'inspect-supabase-storage-object-response.ps1'
$mockProjectRef = 'aaaaaaaaaaaaaaaaaaaa'
$privateBucketName = 'private-bucket-name-regression'
$privateObjectPath = 'private/object/path-regression.txt'
$databasePassword = 'database-password-regression'
$databaseUrl = "postgresql://user:$databasePassword@example.test/database"
$global:StorageInspectorMockContext = @{
    Mode = 'success'
    ProjectRef = $mockProjectRef
    Secret = $secret
    PrivateBucketName = $privateBucketName
    PrivateObjectPath = $privateObjectPath
    DatabasePassword = $databasePassword
    DatabaseUrl = $databaseUrl
}
function global:Invoke-RestMethod {
    [CmdletBinding()]
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [string]$ContentType,
        [string]$Body
    )

    $mockContext = $global:StorageInspectorMockContext
    if ($Uri -notlike "https://$($mockContext.ProjectRef).supabase.co/storage/v1/*") {
        throw 'MOCK_STORAGE_UNEXPECTED_URI'
    }
    if ($mockContext.Mode -eq 'read-buckets-failure' -and $Method -eq 'Get') {
        throw "MOCK_READ_BUCKETS_FAILURE Authorization: Bearer $($mockContext.Secret) apikey=$($mockContext.Secret) DATABASE_PASSWORD=$($mockContext.DatabasePassword) url=$($mockContext.DatabaseUrl) bucket=$($mockContext.PrivateBucketName) object=$($mockContext.PrivateObjectPath)"
    }
    if ($Method -eq 'Get') {
        if ($mockContext.Mode -eq 'normalize-buckets-failure') {
            return [pscustomobject]@{ unexpected = $true }
        }
        $buckets = [object[]]@(1..5 | ForEach-Object {
            [pscustomobject]@{ name = "$($mockContext.PrivateBucketName)-$_"; id = "hidden-$_" }
        })
        Write-Output -NoEnumerate $buckets
        return
    }
    if ($mockContext.Mode -eq 'read-objects-failure') {
        throw "MOCK_READ_OBJECTS_FAILURE Authorization: Bearer $($mockContext.Secret) apikey=$($mockContext.Secret) DATABASE_PASSWORD=$($mockContext.DatabasePassword) url=$($mockContext.DatabaseUrl) bucket=$($mockContext.PrivateBucketName) object=$($mockContext.PrivateObjectPath)"
    }
    if ($mockContext.Mode -eq 'normalize-objects-nested') {
        $nestedItems = [object[]]@([pscustomobject]@{ name = 'hidden-object' })
        $nestedResponse = New-Object object[] 1
        $nestedResponse[0] = $nestedItems
        Write-Output -NoEnumerate $nestedResponse
        return
    }
    if ($mockContext.Mode -eq 'normalize-objects-api-error') {
        return [pscustomobject]@{
            code = 'StorageApiError'
            message = "hidden $($mockContext.Secret) $($mockContext.PrivateBucketName) $($mockContext.PrivateObjectPath)"
        }
    }
    if ($mockContext.Mode -eq 'inspect-items-failure') {
        $item = [pscustomobject]@{ name = 'hidden-object'; id = 'hidden-object-id' }
        $item | Add-Member -MemberType ScriptProperty -Name metadata -Value {
            throw "MOCK_INSPECT_ITEMS_FAILURE $($global:StorageInspectorMockContext.PrivateObjectPath)"
        }
        $objects = [object[]]@($item)
        Write-Output -NoEnumerate $objects
        return
    }
    $objects = [object[]]@(
        [pscustomobject]@{ name = 'hidden-1'; id = 'hidden-1' },
        [pscustomobject]@{ name = 'hidden-2'; id = 'hidden-2' },
        [pscustomobject]@{ name = 'hidden-3'; id = 'hidden-3' }
    )
    Write-Output -NoEnumerate $objects
}

function Invoke-MockedInspector {
    param(
        [Parameter(Mandatory)][string]$Mode,
        [Parameter(Mandatory)][bool]$ExpectFailure
    )

    $global:StorageInspectorMockContext.Mode = $Mode
    $informationRecords = @()
    $failed = $false
    $caughtError = $null
    try {
        & $inspectorPath -ProjectRef $mockProjectRef `
            -Confirmation "INSPECT STORAGE OBJECTS $mockProjectRef" `
            -ServiceRoleKey $secret -InformationVariable informationRecords
    } catch {
        $failed = $true
        $caughtError = $_
    }
    if ($failed -ne $ExpectFailure) {
        throw "Mocked inspector failure state mismatch. mode=$Mode expected=$ExpectFailure actual=$failed"
    }
    $outputText = (@($informationRecords | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
    return [pscustomobject]@{
        OutputText = $outputText
        ErrorRecord = $caughtError
    }
}

$mockSuccess = Invoke-MockedInspector -Mode 'success' -ExpectFailure $false
if (@([regex]::Matches($mockSuccess.OutputText, '(?m)^BUCKET_INDEX=')).Count -ne 5 -or
    $mockSuccess.OutputText -match 'SOURCE_CONTAINS_NESTED_ARRAY=True') {
    throw 'Mock Invoke-RestMethod Object[] regression did not inspect five normalized buckets.'
}
Write-Host 'PASS mock Invoke-RestMethod Object[] preserves five buckets without nested collections'

$requestFailure = Invoke-MockedInspector -Mode 'read-buckets-failure' -ExpectFailure $true
if ($requestFailure.OutputText -notmatch 'HTTP_REQUEST=FAIL' -or
    $requestFailure.OutputText -notmatch 'STAGE=READ_BUCKETS_REQUEST' -or
    $requestFailure.OutputText -notmatch 'BUCKET_SELECTED=False' -or
    $requestFailure.OutputText -match 'NORMALIZATION=FAIL') {
    throw 'Inspector request failure was assigned to the wrong stage.'
}
if ($null -eq $requestFailure.ErrorRecord.Exception.InnerException -or
    $requestFailure.ErrorRecord.Exception.InnerException.Message -notmatch 'MOCK_READ_BUCKETS_FAILURE') {
    throw 'Inspector request failure did not preserve the original exception as InnerException.'
}
Write-Host 'PASS inspector pre-bucket and READ_BUCKETS_REQUEST preserve the original exception'

$bucketNormalizationFailure = Invoke-MockedInspector -Mode 'normalize-buckets-failure' -ExpectFailure $true
if ($bucketNormalizationFailure.OutputText -notmatch 'NORMALIZATION=FAIL' -or
    $bucketNormalizationFailure.OutputText -notmatch 'STAGE=NORMALIZE_BUCKETS' -or
    $bucketNormalizationFailure.OutputText -match 'HTTP_REQUEST=FAIL') {
    throw 'Inspector bucket normalization failure was assigned to the wrong stage.'
}
Write-Host 'PASS inspector bucket normalization failure stage'

$objectRequestFailure = Invoke-MockedInspector -Mode 'read-objects-failure' -ExpectFailure $true
if ($objectRequestFailure.OutputText -notmatch 'HTTP_REQUEST=FAIL' -or
    $objectRequestFailure.OutputText -notmatch 'STAGE=READ_OBJECTS_REQUEST' -or
    $objectRequestFailure.OutputText -notmatch 'BUCKET_SELECTED=True' -or
    $objectRequestFailure.OutputText -match 'NORMALIZATION=FAIL') {
    throw 'Inspector object request failure was assigned to the wrong stage.'
}
if ($null -eq $objectRequestFailure.ErrorRecord.Exception.InnerException -or
    $objectRequestFailure.ErrorRecord.Exception.InnerException.Message -notmatch 'MOCK_READ_OBJECTS_FAILURE') {
    throw 'Inspector object request failure did not preserve the original exception as InnerException.'
}
Write-Host 'PASS inspector READ_OBJECTS_REQUEST stage and selected-bucket diagnostic'

$objectNormalizationFailure = Invoke-MockedInspector -Mode 'normalize-objects-nested' -ExpectFailure $true
if ($objectNormalizationFailure.OutputText -notmatch 'NORMALIZATION=FAIL' -or
    $objectNormalizationFailure.OutputText -notmatch 'STAGE=NORMALIZE_OBJECTS' -or
    $objectNormalizationFailure.OutputText -notmatch 'ERROR_CODE=STORAGE_OBJECT_NESTED_COLLECTION_UNSUPPORTED' -or
    $objectNormalizationFailure.OutputText -match 'HTTP_REQUEST=FAIL' -or
    $objectNormalizationFailure.ErrorRecord.Exception.Data['ErrorCode'] -cne 'STORAGE_OBJECT_NESTED_COLLECTION_UNSUPPORTED') {
    throw 'Inspector object normalization failure was assigned to the wrong stage.'
}
Write-Host 'PASS inspector nested-object normalization stage and custom error code'

$apiErrorFailure = Invoke-MockedInspector -Mode 'normalize-objects-api-error' -ExpectFailure $true
if ($apiErrorFailure.OutputText -notmatch 'NORMALIZATION=FAIL' -or
    $apiErrorFailure.OutputText -notmatch 'ERROR_CODE=STORAGE_OBJECT_API_ERROR' -or
    $apiErrorFailure.OutputText -match 'HTTP_REQUEST=FAIL' -or
    $apiErrorFailure.ErrorRecord.Exception.Data['ErrorCode'] -cne 'STORAGE_OBJECT_API_ERROR') {
    throw 'Inspector Storage API error was not preserved as a normalization failure.'
}
Write-Host 'PASS inspector Storage API error remains a normalization error'

$inspectionFailure = Invoke-MockedInspector -Mode 'inspect-items-failure' -ExpectFailure $true
if ($inspectionFailure.OutputText -notmatch 'INSPECTION=FAIL' -or
    $inspectionFailure.OutputText -notmatch 'STAGE=INSPECT_OBJECT_ITEMS' -or
    $inspectionFailure.OutputText -match 'HTTP_REQUEST=FAIL|NORMALIZATION=FAIL' -or
    $null -eq $inspectionFailure.ErrorRecord.Exception.InnerException -or
    $inspectionFailure.ErrorRecord.Exception.InnerException.Message -notmatch 'MOCK_INSPECT_ITEMS_FAILURE') {
    throw 'Inspector item inspection failure did not preserve its stage and original exception.'
}
Write-Host 'PASS inspector item inspection failure stage'

$diagnosticOutput = @(
    $requestFailure.OutputText
    $bucketNormalizationFailure.OutputText
    $objectRequestFailure.OutputText
    $objectNormalizationFailure.OutputText
    $apiErrorFailure.OutputText
    $inspectionFailure.OutputText
) -join [Environment]::NewLine
foreach ($forbiddenValue in @($secret, $databasePassword, $databaseUrl, $privateBucketName, $privateObjectPath)) {
    if ($diagnosticOutput -match [regex]::Escape($forbiddenValue)) {
        throw 'Inspector stage diagnostics leaked a protected value.'
    }
}
if ($diagnosticOutput -match '(?i)\b(?:Authorization|apikey)\b') {
    throw 'Inspector stage diagnostics leaked a protected credential header name.'
}
Write-Host 'PASS inspector aggregate stage diagnostic redaction'

Remove-Item -LiteralPath Function:\global:Invoke-RestMethod -ErrorAction SilentlyContinue
Remove-Variable -Name StorageInspectorMockContext -Scope Global -ErrorAction SilentlyContinue

Write-Host 'PASS code=STORAGE_OBJECT_RESPONSE_SHAPE_FIXTURES_OK detail=fixtures inspect property names only; no remote request was made' -ForegroundColor Green
