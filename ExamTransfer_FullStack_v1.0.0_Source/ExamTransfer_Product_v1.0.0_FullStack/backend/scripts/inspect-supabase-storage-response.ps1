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

if ($Confirmation -cne "INSPECT STORAGE $ProjectRef") {
    throw "Confirmation mismatch. Supply exactly: INSPECT STORAGE $ProjectRef"
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
    throw 'A service-role key is required for Storage inspection.'
}

try {
    $headers = @{ apikey = $ServiceRoleKey; Authorization = "Bearer $ServiceRoleKey" }
    $response = Invoke-WebRequest -UseBasicParsing -Method Get `
        -Uri "https://$ProjectRef.supabase.co/storage/v1/bucket" -Headers $headers
    $payload = if ([string]::IsNullOrWhiteSpace($response.Content)) { $null } else { $response.Content | ConvertFrom-Json }
    $shape = Get-SafeStorageResponseShape -InputObject $payload

    Write-Host 'HTTP_REQUEST=SUCCESS'
    Write-Host "TOP_LEVEL_TYPE=$($shape.TopLevelType)"
    Write-Host "TOP_LEVEL_PROPERTIES=$($shape.TopLevelProperties -join ',')"
    Write-Host "TOP_LEVEL_IS_ARRAY=$($shape.TopLevelIsArray)"
    Write-Host "SOURCE=$($shape.SourceName)"
    Write-Host "SOURCE_TYPE=$($shape.SourceType)"
    Write-Host "SOURCE_COUNT=$($shape.SourceCount)"
    Write-Host "STORAGE_API_ERROR_SHAPE=$($shape.StorageApiErrorShape)"
    foreach ($item in @($shape.Items)) {
        Write-Host "ITEM_$($item.Index)_TYPE=$($item.Type)"
        Write-Host "ITEM_$($item.Index)_PROPERTIES=$($item.Properties -join ',')"
        Write-Host "ITEM_$($item.Index)_HAS_NAME=$($item.HasName)"
        Write-Host "ITEM_$($item.Index)_HAS_DATA=$($item.HasData)"
        Write-Host "ITEM_$($item.Index)_HAS_BUCKETS=$($item.HasBuckets)"
        Write-Host "ITEM_$($item.Index)_HAS_CODE=$($item.HasCode)"
        Write-Host "ITEM_$($item.Index)_HAS_MESSAGE=$($item.HasMessage)"
    }
} catch {
    Write-Host 'HTTP_REQUEST=FAIL'
    throw 'Storage bucket inspection request failed without disclosing response or credentials.'
} finally {
    $ServiceRoleKey = $null
    $headers = $null
}
