Set-StrictMode -Version Latest

function Get-RelativePathCompat {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $baseFullPath = [IO.Path]::GetFullPath($BasePath)
    $targetFullPath = [IO.Path]::GetFullPath($TargetPath)
    $separator = [IO.Path]::DirectorySeparatorChar

    if (-not $baseFullPath.EndsWith([string]$separator)) {
        $baseFullPath += $separator
    }

    $baseUri = [Uri]$baseFullPath
    $targetUri = [Uri]$targetFullPath
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)

    return [Uri]::UnescapeDataString($relativeUri.ToString()).Replace('/', $separator)
}

function New-Utf8NoBomEncoding {
    [CmdletBinding()]
    param()

    return New-Object Text.UTF8Encoding($false)
}

function Write-Utf8NoBomFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    [IO.File]::WriteAllText($Path, $Content, (New-Utf8NoBomEncoding))
}

function Add-SupabaseDnsResolverArguments {
    [CmdletBinding()]
    param(
        [string[]]$Arguments = @(),

        [ValidateSet('native', 'https')]
        [string]$DnsResolver = 'https'
    )

    return @($Arguments) + @('--dns-resolver', $DnsResolver)
}

function Protect-NativeCommandOutput {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string]$Text,

        [string[]]$SensitiveValues = @()
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }

    $protected = $Text
    foreach ($sensitiveValue in @($SensitiveValues)) {
        if (-not [string]::IsNullOrEmpty($sensitiveValue)) {
            $protected = $protected.Replace($sensitiveValue, '[REDACTED]')
            try {
                $decodedValue = [Uri]::UnescapeDataString($sensitiveValue)
                if (-not [string]::IsNullOrEmpty($decodedValue)) {
                    $protected = $protected.Replace($decodedValue, '[REDACTED]')
                }
            } catch {
                # The exact value was still removed; decoding is best-effort only.
            }
        }
    }

    $protected = $protected -replace '(?i)\bpostgres(?:ql)?://[^\s''"]+', '[REDACTED_POSTGRES_URL]'
    $protected = $protected -replace '(?i)\b(?:SUPABASE_DB_PASSWORD|PGPASSWORD|DATABASE_PASSWORD|DB_PASSWORD|SERVICE_ROLE_KEY|SUPABASE_SERVICE_ROLE_KEY|EXAMTRANSFER_SUPABASE_SERVICE_KEY|SUPABASE_ACCESS_TOKEN|ACCESS_TOKEN|HMAC_SECRET|EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET)\s*[:=]\s*[^\s,;]+', '$1=[REDACTED]'
    $protected = $protected -replace '(?i)\b(?:authorization|apikey)\s*[:=]\s*[^\s,;]+', '$1=[REDACTED]'
    $protected = $protected -replace '\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\b', '[REDACTED_JWT]'
    $protected = $protected -replace '\bsbp_[A-Za-z0-9_-]{12,}\b', '[REDACTED_ACCESS_TOKEN]'
    $protected = $protected -replace '\bsupabase_(?:secret|service_role)_[A-Za-z0-9_-]{12,}\b', '[REDACTED_SUPABASE_KEY]'
    return $protected
}

function Get-SafeStorageResponseShape {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$InputObject
    )

    function Get-PropertyNames {
        param([AllowNull()][object]$Value)
        if ($null -eq $Value -or $Value -is [array]) { return @() }
        return @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    }

    $topLevelIsArray = $InputObject -is [array]
    $topLevelProperties = Get-PropertyNames -Value $InputObject
    $source = $InputObject
    $sourceName = 'top-level'
    if (-not $topLevelIsArray -and $topLevelProperties -contains 'data') {
        $source = $InputObject.PSObject.Properties['data'].Value
        $sourceName = 'data'
    } elseif (-not $topLevelIsArray -and $topLevelProperties -contains 'buckets') {
        $source = $InputObject.PSObject.Properties['buckets'].Value
        $sourceName = 'buckets'
    }

    $items = New-Object 'System.Collections.Generic.List[object]'
    if ($null -ne $source) {
        if ($source -is [array]) {
            foreach ($item in $source) {
                [void]$items.Add($item)
            }
        } else {
            [void]$items.Add($source)
        }
    }
    $itemSummaries = @()
    $itemCount = $items.Count
    $nestedArrayCount = 0
    foreach ($item in $items) {
        if ($item -is [array]) { $nestedArrayCount++ }
    }
    for ($index = 0; $index -lt [Math]::Min(3, $itemCount); $index++) {
        $item = $items[$index]
        $itemProperties = Get-PropertyNames -Value $item
        $itemSummaries += [pscustomobject]@{
            Index = $index
            Type = if ($null -eq $item) { '<null>' } else { $item.GetType().FullName }
            Properties = $itemProperties
            HasName = $itemProperties -contains 'name'
            HasData = $itemProperties -contains 'data'
            HasBuckets = $itemProperties -contains 'buckets'
            HasCode = $itemProperties -contains 'code'
            HasMessage = $itemProperties -contains 'message'
        }
    }

    return [pscustomobject]@{
        TopLevelType = if ($null -eq $InputObject) { '<null>' } else { $InputObject.GetType().FullName }
        TopLevelProperties = $topLevelProperties
        TopLevelIsArray = $topLevelIsArray
        TopLevelCount = if ($null -eq $InputObject) { 0 } elseif ($topLevelIsArray) { $InputObject.Count } else { 1 }
        SourceName = $sourceName
        SourceType = if ($null -eq $source) { '<null>' } else { $source.GetType().FullName }
        SourceCount = $itemCount
        SourceContainsNestedArray = $nestedArrayCount -gt 0
        NestedArrayCount = $nestedArrayCount
        StorageApiErrorShape = (-not $topLevelIsArray -and
            $topLevelProperties -contains 'code' -and $topLevelProperties -contains 'message')
        Items = $itemSummaries
    }
}

function Get-SafeStorageObjectResponseShape {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$InputObject
    )

    function Get-PropertyNames {
        param([AllowNull()][object]$Value)
        if ($null -eq $Value -or $Value -is [array]) { return @() }
        return @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    }

    $topLevelIsArray = $InputObject -is [array]
    $topLevelProperties = Get-PropertyNames -Value $InputObject
    $source = $InputObject
    $sourceName = 'top-level'
    if (-not $topLevelIsArray -and $topLevelProperties -contains 'data') {
        $source = $InputObject.PSObject.Properties['data'].Value
        $sourceName = 'data'
    } elseif (-not $topLevelIsArray -and $topLevelProperties -contains 'objects') {
        $source = $InputObject.PSObject.Properties['objects'].Value
        $sourceName = 'objects'
    }

    $items = New-Object 'System.Collections.Generic.List[object]'
    if ($null -ne $source) {
        if ($source -is [array]) {
            foreach ($item in $source) {
                [void]$items.Add($item)
            }
        } else {
            [void]$items.Add($source)
        }
    }
    $itemCount = $items.Count
    $nestedArrayCount = 0
    foreach ($item in $items) {
        if ($item -is [array]) { $nestedArrayCount++ }
    }
    $itemSummaries = @()
    for ($index = 0; $index -lt [Math]::Min(3, $itemCount); $index++) {
        $item = $items[$index]
        $itemProperties = Get-PropertyNames -Value $item
        $metadata = if ($itemProperties -contains 'metadata') { $item.PSObject.Properties['metadata'].Value } else { $null }
        $metadataProperties = Get-PropertyNames -Value $metadata
        $itemSummaries += [pscustomobject]@{
            Index = $index
            Type = if ($null -eq $item) { '<null>' } else { $item.GetType().FullName }
            Properties = $itemProperties
            HasName = $itemProperties -contains 'name'
            HasId = $itemProperties -contains 'id'
            HasMetadata = $itemProperties -contains 'metadata'
            MetadataType = if ($null -eq $metadata) { '<null>' } else { $metadata.GetType().FullName }
            MetadataProperties = $metadataProperties
            MetadataHasSize = $metadataProperties -contains 'size'
        }
    }

    return [pscustomobject]@{
        TopLevelType = if ($null -eq $InputObject) { '<null>' } else { $InputObject.GetType().FullName }
        TopLevelProperties = $topLevelProperties
        TopLevelIsArray = $topLevelIsArray
        TopLevelCount = if ($null -eq $InputObject) { 0 } elseif ($topLevelIsArray) { $InputObject.Count } else { 1 }
        SourceName = $sourceName
        SourceType = if ($null -eq $source) { '<null>' } else { $source.GetType().FullName }
        SourceCount = $itemCount
        SourceContainsNestedArray = $nestedArrayCount -gt 0
        NestedArrayCount = $nestedArrayCount
        StorageApiErrorShape = (-not $topLevelIsArray -and
            ($topLevelProperties -contains 'code' -or $topLevelProperties -contains 'message' -or
             $topLevelProperties -contains 'statusCode' -or $topLevelProperties -contains 'error'))
        Items = $itemSummaries
    }
}

function New-StorageCollectionException {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorCode,

        [string[]]$PropertyNames = @()
    )

    $details = if (@($PropertyNames).Count -eq 0) { '' } else { " properties=$($PropertyNames -join ',')" }
    $exception = New-Object System.InvalidOperationException("$ErrorCode$details")
    $exception.Data['ErrorCode'] = $ErrorCode
    return $exception
}

function ConvertTo-StorageBucketItems {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Response
    )

    if ($null -eq $Response) { return }

    $responseIsArray = $Response -is [array]
    $propertyNames = if ($responseIsArray) {
        @()
    } else {
        @($Response.PSObject.Properties | ForEach-Object { $_.Name })
    }
    if (-not $responseIsArray -and
        ($propertyNames -contains 'code' -or $propertyNames -contains 'message' -or
         $propertyNames -contains 'statusCode' -or $propertyNames -contains 'error')) {
        throw (New-StorageCollectionException -ErrorCode 'STORAGE_BUCKET_API_ERROR' -PropertyNames $propertyNames)
    }

    $source = $Response
    if (-not $responseIsArray -and $propertyNames -contains 'data') {
        $source = $Response.PSObject.Properties['data'].Value
    } elseif (-not $responseIsArray -and $propertyNames -contains 'buckets') {
        $source = $Response.PSObject.Properties['buckets'].Value
    } elseif (-not $responseIsArray -and -not ($propertyNames -contains 'name')) {
        throw (New-StorageCollectionException -ErrorCode 'STORAGE_BUCKET_RESPONSE_UNSUPPORTED' -PropertyNames $propertyNames)
    }

    if ($null -eq $source) { return }
    $sourcePropertyNames = if ($source -is [array]) {
        @()
    } else {
        @($source.PSObject.Properties | ForEach-Object { $_.Name })
    }
    if (-not ($source -is [array]) -and
        ($sourcePropertyNames -contains 'code' -or $sourcePropertyNames -contains 'message' -or
         $sourcePropertyNames -contains 'statusCode' -or $sourcePropertyNames -contains 'error')) {
        throw (New-StorageCollectionException -ErrorCode 'STORAGE_BUCKET_API_ERROR' -PropertyNames $sourcePropertyNames)
    }

    if ($source -is [array]) {
        foreach ($item in $source) {
            if ($item -is [array]) {
                throw (New-StorageCollectionException -ErrorCode 'STORAGE_BUCKET_NESTED_COLLECTION_UNSUPPORTED')
            }
            Write-Output $item
        }
        return
    }
    Write-Output $source
}

function ConvertTo-StorageObjectItems {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Response
    )

    if ($null -eq $Response) { return }

    $responseIsArray = $Response -is [array]
    $propertyNames = if ($responseIsArray) {
        @()
    } else {
        @($Response.PSObject.Properties | ForEach-Object { $_.Name })
    }
    if (-not $responseIsArray -and
        ($propertyNames -contains 'code' -or $propertyNames -contains 'message' -or
         $propertyNames -contains 'statusCode' -or $propertyNames -contains 'error')) {
        throw (New-StorageCollectionException -ErrorCode 'STORAGE_OBJECT_API_ERROR' -PropertyNames $propertyNames)
    }

    $source = $Response
    if (-not $responseIsArray -and $propertyNames -contains 'data') {
        $source = $Response.PSObject.Properties['data'].Value
    } elseif (-not $responseIsArray -and $propertyNames -contains 'objects') {
        $source = $Response.PSObject.Properties['objects'].Value
    } elseif (-not $responseIsArray -and -not ($propertyNames -contains 'name')) {
        throw (New-StorageCollectionException -ErrorCode 'STORAGE_OBJECT_RESPONSE_UNSUPPORTED' -PropertyNames $propertyNames)
    }

    if ($null -eq $source) { return }
    $sourcePropertyNames = if ($source -is [array]) {
        @()
    } else {
        @($source.PSObject.Properties | ForEach-Object { $_.Name })
    }
    if (-not ($source -is [array]) -and
        ($sourcePropertyNames -contains 'code' -or $sourcePropertyNames -contains 'message' -or
         $sourcePropertyNames -contains 'statusCode' -or $sourcePropertyNames -contains 'error')) {
        throw (New-StorageCollectionException -ErrorCode 'STORAGE_OBJECT_API_ERROR' -PropertyNames $sourcePropertyNames)
    }

    if ($source -is [array]) {
        foreach ($item in $source) {
            if ($item -is [array]) {
                throw (New-StorageCollectionException -ErrorCode 'STORAGE_OBJECT_NESTED_COLLECTION_UNSUPPORTED')
            }
            Write-Output $item
        }
        return
    }
    Write-Output $source
}

function Invoke-NativeCommandCaptured {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [string[]]$Arguments = @(),

        [string[]]$SensitiveValues = @(),

        [string]$FailureContext,

        [ValidateRange(1, 65536)]
        [int]$FailureOutputLimit = 4096
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 promotes native stderr to ErrorRecord objects
        # when redirecting 2>&1. Continue locally so the native exit code remains
        # authoritative; the caller's preference is restored in all cases.
        $ErrorActionPreference = 'Continue'
        $captured = @(& $Command @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $safeOutput = @($captured | ForEach-Object {
        Protect-NativeCommandOutput -Text ([string]$_) -SensitiveValues $SensitiveValues
    })
    $safeText = ($safeOutput | Out-String).Trim()

    if ($exitCode -ne 0) {
        $excerpt = $safeText
        if ($excerpt.Length -gt $FailureOutputLimit) {
            $excerpt = $excerpt.Substring(0, $FailureOutputLimit) + '...[truncated]'
        }
        if ([string]::IsNullOrWhiteSpace($excerpt)) {
            $excerpt = '<no output>'
        }

        $contextText = if ([string]::IsNullOrWhiteSpace($FailureContext)) { '' } else { " $FailureContext" }
        $exception = New-Object System.Exception(
            "Native command failed. command=$Command exitCode=$exitCode$contextText output=$excerpt")
        $exception.Data['Command'] = $Command
        $exception.Data['ExitCode'] = [int]$exitCode
        throw $exception
    }

    return [pscustomobject]@{
        Command = $Command
        ExitCode = [int]$exitCode
        Output = $safeOutput
        OutputText = $safeText
    }
}
