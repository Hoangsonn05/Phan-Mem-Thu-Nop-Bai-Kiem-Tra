. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$queue = Join-Path $root 'frontend/src/ExamTransfer.Desktop/Infrastructure/SubmissionQueueStore.cs'
$recovery = Join-Path $root 'frontend/src/ExamTransfer.Desktop/Infrastructure/SubmissionRecoveryService.cs'
$required = @(
    @{ Path = $queue; Pattern = 'FileOptions.WriteThrough' },
    @{ Path = $queue; Pattern = 'Flush(true)' },
    @{ Path = $queue; Pattern = 'ProtectToken' },
    @{ Path = $recovery; Pattern = 'MissingChunks' },
    @{ Path = $recovery; Pattern = 'StoreReceiptAsync' },
    @{ Path = $recovery; Pattern = 'RemoveCompletedAsync' }
)
foreach ($check in $required) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Pattern -Quiet)) {
        Write-AcceptanceResult -Passed $false -Code 'RECOVERY_INVARIANT_MISSING' -TraceId $traceId -Detail $check.Pattern
    }
}
& dotnet build (Join-Path $root 'frontend/src/ExamTransfer.Desktop/ExamTransfer.Desktop.csproj') -c Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-AcceptanceResult -Passed $false -Code 'RECOVERY_BUILD_FAILED' -TraceId $traceId
}
Write-AcceptanceResult -Passed $true -Code 'STATIC_SUBMISSION_RECOVERY_OK' -TraceId $traceId -Detail 'source invariants and local build only; this is not an E2E result'
