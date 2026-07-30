param(
    [string]$ApiUrl = "http://localhost:5048",
    [Parameter(Mandatory = $true)]
    [string]$TeacherToken,
    [switch]$TriggerSync
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$headers = @{ Authorization = "Bearer $TeacherToken" }

function Invoke-ExamTransferGet {
    param([Parameter(Mandatory = $true)][string]$Path)
    Invoke-RestMethod -Method Get -Uri ($ApiUrl.TrimEnd('/') + $Path) -Headers $headers
}

Write-Host "Checking local backend health" -ForegroundColor Cyan
$health = Invoke-RestMethod -Method Get -Uri ($ApiUrl.TrimEnd('/') + "/health")
$health | ConvertTo-Json -Depth 8

Write-Host "Checking Supabase preflight" -ForegroundColor Cyan
$preflight = Invoke-ExamTransferGet "/api/v1/cloud/preflight"
$preflight | ConvertTo-Json -Depth 8

Write-Host "Checking Supabase user session" -ForegroundColor Cyan
$session = Invoke-ExamTransferGet "/api/v1/cloud/auth/session"
$session | ConvertTo-Json -Depth 8

Write-Host "Checking cloud queue status" -ForegroundColor Cyan
$status = Invoke-ExamTransferGet "/api/v1/cloud/sync/status"
$status | ConvertTo-Json -Depth 8

if ($TriggerSync) {
    Write-Host "Triggering retry for failed cloud queue items" -ForegroundColor Cyan
    Invoke-RestMethod `
        -Method Post `
        -Uri ($ApiUrl.TrimEnd('/') + "/api/v1/cloud/sync") `
        -Headers $headers | ConvertTo-Json -Depth 8
}

Write-Host "Supabase API smoke test completed." -ForegroundColor Green
