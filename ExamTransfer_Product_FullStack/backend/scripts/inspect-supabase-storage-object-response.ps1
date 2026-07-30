[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{20}$')]
    [string]$ProjectRef,

    [Parameter(Mandatory)]
    [string]$Confirmation,

    [string]$ServiceRoleKey = $env:SUPABASE_SERVICE_ROLE_KEY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

if ($Confirmation -cne "INSPECT STORAGE OBJECTS $ProjectRef") {
    throw "Confirmation mismatch. Supply exactly: INSPECT STORAGE OBJECTS $ProjectRef"
}
if ([string]::IsNullOrWhiteSpace($ServiceRoleKey)) {
    $ServiceRoleKey = $env:EXAMTRANSFER_SUPABASE_SERVICE_KEY
}
if ([string]::IsNullOrWhiteSpace($ServiceRoleKey)) {
    $secureKey = Read-Host 'Supabase service-role key (input is hidden and never saved)' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try { $ServiceRoleKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
if ([string]::IsNullOrWhiteSpace($ServiceRoleKey)) {
    throw 'A service-role key is required for Storage object inspection.'
}

function Write-ObjectShape {
    param([Parameter(Mandatory)][object]$Shape)

    Write-Host "TOP_LEVEL_TYPE=$($Shape.TopLevelType)"
    Write-Host "TOP_LEVEL_PROPERTIES=$($Shape.TopLevelProperties -join ',')"
    Write-Host "TOP_LEVEL_IS_ARRAY=$($Shape.TopLevelIsArray)"
    Write-Host "SOURCE=$($Shape.SourceName)"
    Write-Host "SOURCE_TYPE=$($Shape.SourceType)"
    Write-Host "SOURCE_COUNT=$($Shape.SourceCount)"
    Write-Host "SOURCE_CONTAINS_NESTED_ARRAY=$($Shape.SourceContainsNestedArray)"
    Write-Host "NESTED_ARRAY_COUNT=$($Shape.NestedArrayCount)"
    Write-Host "EMPTY_BUCKET=$($Shape.SourceCount -eq 0)"
    Write-Host "STORAGE_API_ERROR_SHAPE=$($Shape.StorageApiErrorShape)"
    if ($Shape.StorageApiErrorShape) {
        Write-Host "ERROR_PROPERTIES=$($Shape.TopLevelProperties -join ',')"
    }
    if (-not $Shape.StorageApiErrorShape -and -not $Shape.TopLevelIsArray -and
        $Shape.SourceName -eq 'top-level' -and $Shape.TopLevelProperties.Count -gt 0 -and
        $Shape.TopLevelProperties -notcontains 'name') {
        Write-Host 'STORAGE_OBJECT_RESPONSE_UNSUPPORTED=True'
    }
    foreach ($item in @($Shape.Items)) {
        Write-Host "ITEM_$($item.Index)_TYPE=$($item.Type)"
        Write-Host "ITEM_$($item.Index)_PROPERTIES=$($item.Properties -join ',')"
        Write-Host "ITEM_$($item.Index)_HAS_NAME=$($item.HasName)"
        Write-Host "ITEM_$($item.Index)_HAS_ID=$($item.HasId)"
        Write-Host "ITEM_$($item.Index)_HAS_METADATA=$($item.HasMetadata)"
        Write-Host "ITEM_$($item.Index)_METADATA_TYPE=$($item.MetadataType)"
        Write-Host "ITEM_$($item.Index)_METADATA_PROPERTIES=$($item.MetadataProperties -join ',')"
        Write-Host "ITEM_$($item.Index)_METADATA_HAS_SIZE=$($item.MetadataHasSize)"
    }
}

function Write-SafeInspectorFailure {
    param(
        [Parameter(Mandatory)][System.Management.Automation.ErrorRecord]$ErrorRecord,
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][ValidateSet('HTTP_REQUEST', 'NORMALIZATION', 'INSPECTION')][string]$FailureKind,
        [AllowNull()][object]$BucketIndex,
        [bool]$BucketSelected,
        [AllowNull()][object]$RawTopLevelType,
        [AllowNull()][object]$RawTopLevelIsArray,
        [AllowNull()][object]$RawTopLevelCount,
        [string[]]$RawPropertyNames = @(),
        [string[]]$SensitiveValues = @()
    )

    # Diagnostics must never replace the original exception. Every optional
    # field is guarded, and any diagnostic-only failure is swallowed.
    try {
        $exception = if ($null -eq $ErrorRecord) { $null } else { $ErrorRecord.Exception }
        $invocationInfo = if ($null -eq $ErrorRecord) { $null } else { $ErrorRecord.InvocationInfo }
        $errorCode = if ($null -eq $exception) { $null } else { $exception.Data['ErrorCode'] }
        $hasSafeErrorCode = -not [string]::IsNullOrWhiteSpace([string]$errorCode)

        Write-Host "$FailureKind=FAIL"
        Write-Host "STAGE=$Stage"
        Write-Host "BUCKET_SELECTED=$BucketSelected"
        if ($null -ne $BucketIndex) {
            Write-Host "BUCKET_INDEX=$BucketIndex"
        }
        Write-Host "EXCEPTION_TYPE=$(if ($null -eq $exception) { '<unavailable>' } else { $exception.GetType().FullName })"

        $safeFullyQualifiedErrorId = if ($FailureKind -eq 'HTTP_REQUEST' -or -not $hasSafeErrorCode) {
            '<withheld-to-prevent-response-value-disclosure>'
        } else {
            Protect-NativeCommandOutput -Text ([string]$ErrorRecord.FullyQualifiedErrorId) `
                -SensitiveValues $SensitiveValues
        }
        Write-Host "FULLY_QUALIFIED_ERROR_ID=$safeFullyQualifiedErrorId"

        $scriptName = if ($null -eq $invocationInfo) { '<unavailable>' } else {
            Protect-NativeCommandOutput -Text ([string]$invocationInfo.ScriptName) `
                -SensitiveValues $SensitiveValues
        }
        Write-Host "SCRIPT_NAME=$scriptName"
        Write-Host "SCRIPT_LINE_NUMBER=$(if ($null -eq $invocationInfo) { 0 } else { $invocationInfo.ScriptLineNumber })"

        $safePositionMessage = if ($null -eq $invocationInfo -or
            $FailureKind -eq 'HTTP_REQUEST' -or -not $hasSafeErrorCode) {
            '<withheld-to-prevent-response-value-disclosure>'
        } else {
            Protect-NativeCommandOutput -Text ([string]$invocationInfo.PositionMessage) `
                -SensitiveValues $SensitiveValues
        }
        Write-Host "POSITION_MESSAGE=$safePositionMessage"

        if ($hasSafeErrorCode) {
            Write-Host "ERROR_CODE=$(Protect-NativeCommandOutput -Text ([string]$errorCode) -SensitiveValues $SensitiveValues)"
        }
        $safeMessage = switch ($FailureKind) {
            'HTTP_REQUEST' {
                'Storage request failed; provider response text was withheld.'
            }
            'NORMALIZATION' {
                'Storage response normalization failed; response values were withheld.'
            }
            default {
                'Storage response inspection failed; response values were withheld.'
            }
        }
        Write-Host "MESSAGE=$safeMessage"

        if ($null -ne $RawTopLevelType) {
            Write-Host "RAW_TOP_LEVEL_TYPE=$RawTopLevelType"
            Write-Host "RAW_TOP_LEVEL_IS_ARRAY=$RawTopLevelIsArray"
            Write-Host "RAW_TOP_LEVEL_COUNT=$RawTopLevelCount"
            Write-Host "RAW_PROPERTY_NAMES=$($RawPropertyNames -join ',')"
        }
    } catch {
        try {
            Write-Host 'DIAGNOSTIC_STATUS=PARTIAL'
        } catch {
            # No diagnostic failure may replace the original exception.
        }
    }
}

$limit = 3
$offset = 0
$stage = 'INITIALIZE'
$bucketIndex = $null
$bucketName = $null
$bucketSelected = $false
$rawResponse = $null
$rawShape = $null
$rawTopLevelType = $null
$rawTopLevelIsArray = $null
$rawTopLevelCount = $null
$rawPropertyNames = @()
$headers = $null
try {
    $headers = @{ apikey = $ServiceRoleKey; Authorization = "Bearer $ServiceRoleKey" }
    $stage = 'READ_BUCKETS_REQUEST'
    $bucketResponse = Invoke-RestMethod -Method Get `
        -Uri "https://$ProjectRef.supabase.co/storage/v1/bucket" -Headers $headers
    $stage = 'NORMALIZE_BUCKETS'
    $rawResponse = $bucketResponse
    $rawShape = Get-SafeStorageResponseShape -InputObject $bucketResponse
    $rawTopLevelType = $rawShape.TopLevelType
    $rawTopLevelIsArray = $rawShape.TopLevelIsArray
    $rawTopLevelCount = $rawShape.TopLevelCount
    $rawPropertyNames = @($rawShape.TopLevelProperties)
    $bucketItems = @(ConvertTo-StorageBucketItems -Response $bucketResponse)

    for ($index = 0; $index -lt $bucketItems.Count; $index++) {
        $stage = 'ITERATE_BUCKET'
        $bucketIndex = $index
        $bucketName = $null
        $bucketSelected = $false
        $rawResponse = $null
        $rawShape = $null
        $rawTopLevelType = $null
        $rawTopLevelIsArray = $null
        $rawTopLevelCount = $null
        $rawPropertyNames = @()
        $bucket = $bucketItems[$index]
        $bucketSelected = $true
        $bucketProperties = @($bucket.PSObject.Properties | ForEach-Object { $_.Name })
        if ($bucketProperties -notcontains 'name') {
            $exception = New-StorageCollectionException `
                -ErrorCode 'STORAGE_BUCKET_ITEM_NAME_MISSING' -PropertyNames $bucketProperties
            throw $exception
        }
        $bucketName = [string]$bucket.PSObject.Properties['name'].Value
        if ([string]::IsNullOrWhiteSpace($bucketName)) {
            throw (New-StorageCollectionException -ErrorCode 'STORAGE_BUCKET_ITEM_NAME_EMPTY')
        }

        $body = @{ prefix = ''; limit = $limit; offset = $offset; sortBy = @{ column = 'name'; order = 'asc' } } |
            ConvertTo-Json -Depth 4
        $stage = 'READ_OBJECTS_REQUEST'
        $objectResponse = Invoke-RestMethod -Method Post `
            -Uri "https://$ProjectRef.supabase.co/storage/v1/object/list/$([Uri]::EscapeDataString($bucketName))" `
            -Headers $headers -ContentType 'application/json' -Body $body
        $stage = 'NORMALIZE_OBJECTS'
        $rawResponse = $objectResponse
        $rawShape = Get-SafeStorageObjectResponseShape -InputObject $objectResponse
        $rawTopLevelType = $rawShape.TopLevelType
        $rawTopLevelIsArray = $rawShape.TopLevelIsArray
        $rawTopLevelCount = $rawShape.TopLevelCount
        $rawPropertyNames = @($rawShape.TopLevelProperties)
        $objectItems = @(ConvertTo-StorageObjectItems -Response $objectResponse)
        $stage = 'INSPECT_OBJECT_ITEMS'
        $shape = Get-SafeStorageObjectResponseShape -InputObject $objectItems

        Write-Host "BUCKET_INDEX=$index"
        Write-Host 'HTTP_REQUEST=SUCCESS'
        Write-Host "PAGINATION_LIMIT=$limit"
        Write-Host "PAGE_OFFSET=$offset"
        Write-Host "RAW_SOURCE_CONTAINS_NESTED_ARRAY=$($rawShape.SourceContainsNestedArray)"
        Write-Host "NORMALIZED_SOURCE_CONTAINS_NESTED_ARRAY=$($shape.SourceContainsNestedArray)"
        Write-ObjectShape -Shape $shape
    }
    $stage = 'COMPLETE'
} catch {
    $originalErrorRecord = $_
    $originalException = $_.Exception
    $failureKind = switch ($stage) {
        'READ_BUCKETS_REQUEST' { 'HTTP_REQUEST' }
        'READ_OBJECTS_REQUEST' { 'HTTP_REQUEST' }
        'NORMALIZE_BUCKETS' { 'NORMALIZATION' }
        'NORMALIZE_OBJECTS' { 'NORMALIZATION' }
        default { 'INSPECTION' }
    }
    $sensitiveValues = @($ServiceRoleKey)
    if (-not [string]::IsNullOrWhiteSpace($bucketName)) {
        $sensitiveValues += $bucketName
    }
    try {
        Write-SafeInspectorFailure -ErrorRecord $originalErrorRecord -Stage $stage `
            -FailureKind $failureKind -BucketIndex $bucketIndex -BucketSelected $bucketSelected `
            -RawTopLevelType $rawTopLevelType -RawTopLevelIsArray $rawTopLevelIsArray `
            -RawTopLevelCount $rawTopLevelCount -RawPropertyNames $rawPropertyNames `
            -SensitiveValues $sensitiveValues
    } catch {
        # Write-SafeInspectorFailure is defensive, but the original exception
        # remains authoritative even if the host itself rejects diagnostics.
    }
    $safeException = New-Object System.Exception(
        "Storage object inspection failed. stage=$stage kind=$failureKind; see sanitized diagnostics above.",
        $originalException)
    $safeException.Data['Stage'] = $stage
    $safeException.Data['FailureKind'] = $failureKind
    if ($null -ne $originalException.Data['ErrorCode']) {
        $safeException.Data['ErrorCode'] = $originalException.Data['ErrorCode']
    }
    throw $safeException
} finally {
    $ServiceRoleKey = $null
    $headers = $null
    $bucketName = $null
    $rawResponse = $null
}
