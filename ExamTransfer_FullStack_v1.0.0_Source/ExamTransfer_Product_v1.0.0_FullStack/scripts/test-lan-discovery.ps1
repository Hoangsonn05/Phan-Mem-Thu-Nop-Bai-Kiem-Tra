[CmdletBinding()]
param(
    [int]$Port = 40550,
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "=== TEST UDP LAN DISCOVERY LISTENER (Port $Port) ===" -ForegroundColor Cyan
Write-Host "Đang lắng nghe gói tin UDP Broadcast trong $TimeoutSeconds giây..."

$udpClient = New-Object System.Net.Sockets.UdpClient($Port)
$udpClient.EnableBroadcast = $true
$remoteEP = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)

$asyncResult = $udpClient.BeginReceive($null, $null)
$success = $asyncResult.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))

if (-not $success) {
    $udpClient.Close()
    Write-Error "[FAIL] Hết $TimeoutSeconds giây không nhận được gói tin Discovery nào qua UDP cổng $Port."
    exit 1
}

$bytes = $udpClient.EndReceive($asyncResult, [ref]$remoteEP)
$jsonText = [System.Text.Encoding]::UTF8.GetString($bytes)
$udpClient.Close()

Write-Host "`n[SUCCESS] Đã nhận gói tin từ IP: $($remoteEP.Address):$($remoteEP.Port)" -ForegroundColor Green
Write-Host "Nội dung JSON Payload:" -ForegroundColor Yellow
Write-Host $jsonText

try {
    $json = $jsonText | ConvertFrom-Json
    if ($json.service -and $json.server_ip -and $json.port -and $json.room_code) {
        Write-Host "`n[PASS] Payload hợp lệ! Server IP: $($json.server_ip), Port: $($json.port), Mã Phòng: $($json.room_code)" -ForegroundColor Green
        exit 0
    } else {
        Write-Error "[FAIL] Gói tin JSON thiếu các trường bắt buộc (service, server_ip, port, room_code)."
        exit 1
    }
} catch {
    Write-Error "[FAIL] Gói tin nhận được không phải là định dạng JSON hợp lệ."
    exit 1
}
