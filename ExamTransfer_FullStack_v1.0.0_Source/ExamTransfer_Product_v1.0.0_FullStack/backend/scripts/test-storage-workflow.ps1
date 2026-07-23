param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId,
    [Parameter(Mandatory)][ValidateScript({Test-Path -LiteralPath $_ -PathType Leaf})][string]$ArchivePath
)
. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
try {
    $base=$SupabaseUrl.TrimEnd('/'); $headers=@{apikey=$PublishableKey;Authorization="Bearer $StudentJwt";'Content-Type'='application/json'}
    $file=Get-Item -LiteralPath $ArchivePath; $sha=(Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant(); $key="storage-$traceId"
    $init=@{p_session_id=$SessionId;p_idempotency_key=$key;p_file_name=$file.Name;p_size_bytes=$file.Length;p_sha256=$sha}|ConvertTo-Json -Compress
    $submissionId=Invoke-RestMethod -Method Post -Uri "$base/rest/v1/rpc/init_public_submission" -Headers $headers -Body $init
    $rows=@(Invoke-RestMethod -Method Get -Uri "$base/rest/v1/submission_files?select=id,cloud_object_path&submission_id=eq.$submissionId" -Headers $headers)
    if ($rows.Count -ne 1) { throw 'Expected exactly one PublicCloud submission file.' }
    $objectPath=($rows[0].cloud_object_path -split '/' | ForEach-Object {[Uri]::EscapeDataString($_)}) -join '/'
    $uploadHeaders=$headers.Clone();$uploadHeaders['x-upsert']='false';$uploadHeaders['Content-Type']='application/octet-stream'
    Invoke-WebRequest -Method Post -Uri "$base/storage/v1/object/public-submission-archives/$objectPath" -Headers $uploadHeaders -InFile $ArchivePath -UseBasicParsing | Out-Null
    $final=@{submissionId=$submissionId;idempotencyKey=$key}|ConvertTo-Json -Compress
    $receipt=Invoke-RestMethod -Method Post -Uri "$base/functions/v1/verify-public-submission-archive" -Headers $headers -Body $final
    $retry=Invoke-RestMethod -Method Post -Uri "$base/functions/v1/verify-public-submission-archive" -Headers $headers -Body $final
    if (-not $receipt.receiptCode -or [string]$receipt.receiptCode -ne [string]$retry.receiptCode) { throw 'Finalize receipt is absent or not idempotent.' }
    Write-AcceptanceResult -Passed $true -Code 'STORAGE_WORKFLOW_OK' -TraceId $traceId -Detail "live immutable upload, Edge verification and idempotent receipt passed submission=$submissionId"
} catch { Write-AcceptanceResult -Passed $false -Code 'STORAGE_WORKFLOW_FAILED' -TraceId $traceId -Detail $_.Exception.Message }
