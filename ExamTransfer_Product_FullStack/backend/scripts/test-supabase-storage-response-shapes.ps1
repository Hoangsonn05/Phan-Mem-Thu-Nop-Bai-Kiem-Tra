[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\powershell-compat.ps1')

$fixtures = @(
    @{ Name = 'direct-array'; Json = '[{"id":"a","name":"bucket-a"}]'; Source = 'top-level'; Count = 1; Error = $false },
    @{ Name = 'data-wrapper'; Json = '{"data":[{"id":"a","name":"bucket-a"}]}'; Source = 'data'; Count = 1; Error = $false },
    @{ Name = 'buckets-wrapper'; Json = '{"buckets":[{"id":"a","name":"bucket-a"}]}'; Source = 'buckets'; Count = 1; Error = $false },
    @{ Name = 'single-bucket'; Json = '{"id":"a","name":"bucket-a"}'; Source = 'top-level'; Count = 1; Error = $false },
    @{ Name = 'api-error'; Json = '{"code":"Unauthorized","message":"Invalid API key"}'; Source = 'top-level'; Count = 1; Error = $true },
    @{ Name = 'null'; Json = 'null'; Source = 'top-level'; Count = 0; Error = $false },
    @{ Name = 'unknown-string'; Json = '"unknown"'; Source = 'top-level'; Count = 1; Error = $false }
)

foreach ($fixture in $fixtures) {
    $shape = Get-SafeStorageResponseShape -InputObject ($fixture.Json | ConvertFrom-Json)
    if ($shape.SourceName -cne $fixture.Source -or $shape.SourceCount -ne $fixture.Count -or
        $shape.StorageApiErrorShape -ne $fixture.Error) {
        throw "Storage response shape fixture failed: $($fixture.Name)"
    }
    if ($fixture.Name -in @('direct-array', 'data-wrapper', 'buckets-wrapper', 'single-bucket') -and
        -not @($shape.Items)[0].HasName) {
        throw "Storage response shape fixture did not detect the name property: $($fixture.Name)"
    }
    Write-Host "PASS storage response shape=$($fixture.Name)"
}

$bucketFixtures = @(
    @{ Name = 'direct-array-five'; Response = @(
            [pscustomobject]@{ name = 'hidden-1' },
            [pscustomobject]@{ name = 'hidden-2' },
            [pscustomobject]@{ name = 'hidden-3' },
            [pscustomobject]@{ name = 'hidden-4' },
            [pscustomobject]@{ name = 'hidden-5' }
        ); Count = 5 },
    @{ Name = 'single-bucket'; Response = [pscustomobject]@{ name = 'hidden' }; Count = 1 },
    @{ Name = 'data-wrapper'; Response = [pscustomobject]@{
            data = @([pscustomobject]@{ name = 'hidden' })
        }; Count = 1 },
    @{ Name = 'buckets-wrapper'; Response = [pscustomobject]@{
            buckets = @([pscustomobject]@{ name = 'hidden' })
        }; Count = 1 },
    @{ Name = 'null'; Response = $null; Count = 0 }
)
foreach ($fixture in $bucketFixtures) {
    $items = @(ConvertTo-StorageBucketItems -Response $fixture.Response)
    if ($items.Count -ne $fixture.Count -or @($items | Where-Object { $_ -is [array] }).Count -ne 0) {
        throw "Storage bucket collection fixture failed: $($fixture.Name)"
    }
    if ($items.Count -gt 0 -and $items[0] -isnot [pscustomobject]) {
        throw "Storage bucket collection fixture did not emit PSCustomObject: $($fixture.Name)"
    }
    Write-Host "PASS storage bucket collection=$($fixture.Name)"
}

$nestedBucketItems = [object[]]@([pscustomobject]@{ name = 'hidden' })
$nestedBucketResponse = New-Object object[] 1
$nestedBucketResponse[0] = $nestedBucketItems
$bucketFailureFixtures = @(
    @{ Name = 'api-error'; Response = [pscustomobject]@{ statusCode = 403; error = 'hidden' } },
    @{ Name = 'unknown-object'; Response = [pscustomobject]@{ unexpected = $true } },
    @{ Name = 'nested-array'; Response = $nestedBucketResponse }
)
foreach ($failure in $bucketFailureFixtures) {
    try {
        @(ConvertTo-StorageBucketItems -Response $failure.Response) | Out-Null
        throw "Unsupported Storage bucket fixture unexpectedly succeeded: $($failure.Name)"
    } catch {
        if ($_.Exception.Message -notmatch 'STORAGE_BUCKET_(API_ERROR|RESPONSE_UNSUPPORTED|NESTED_COLLECTION_UNSUPPORTED)') {
            throw
        }
    }
}
Write-Host 'PASS storage bucket API error, unsupported-shape, and nested-array regressions'

$secret = 'service-role-regression-secret-value'
$safeDiagnostic = Protect-NativeCommandOutput `
    -Text "Authorization: Bearer $secret apikey=$secret SUPABASE_DB_URL=postgres://user:$secret@example.test/db" `
    -SensitiveValues @($secret)
if ($safeDiagnostic -match [regex]::Escape($secret) -or $safeDiagnostic -match 'postgres(?:ql)?://') {
    throw 'Storage diagnostic redaction leaked a credential or database URL.'
}
$wrapperScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup-supabase-production-all.ps1') -Raw
foreach ($field in @('scriptName', 'lineNumber', 'positionMessage', 'exceptionType', 'innerException')) {
    if ($wrapperScript -notmatch [regex]::Escape($field)) {
        throw "Backup wrapper diagnostic is missing: $field"
    }
}
Write-Host 'PASS storage diagnostic redaction and wrapper error context' -ForegroundColor Green

Write-Host 'PASS code=STORAGE_RESPONSE_SHAPE_FIXTURES_OK detail=fixtures inspect property names only; no remote request was made' -ForegroundColor Green
