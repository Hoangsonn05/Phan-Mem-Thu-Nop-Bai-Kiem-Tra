# Triển khai backend bằng Docker

## Ranh giới kiến trúc

Docker chỉ đóng gói Local Server. SQLite và file runtime vẫn là nguồn chính của
`LanOnly`; Supabase vẫn là nguồn chính của dữ liệu học sinh/thiết bị/bài nộp
trong `PublicCloud`. Không ghi dữ liệu bền vững vào lớp filesystem của
container.

Hai named volume mặc định:

- `examtransfer_data` gắn vào `/data/ExamTransfer`: SQLite, đề, bài nộp,
  receipt, audit/outbox, pull cursor và cache runtime.
- `examtransfer_runtime` gắn vào `/usr/share/ExamTransfer`: Data Protection
  keys và dữ liệu runtime cần ổn định qua restart.

Không chạy `docker compose down -v` đối với stack đang sử dụng. Script
`test-docker-persistence.ps1` dùng project/volume cô lập và chỉ xóa đúng volume
thử nghiệm khi có `-Cleanup`.

## Cấu hình LAN

1. Sao chép `.env.docker.example` thành `.env.docker`, hoặc chạy
   `scripts/setup-docker-environment.ps1`.
2. Chạy `scripts/configure-docker-lan.ps1`. Chỉ chọn Wi-Fi/Ethernet vật lý đang
   hoạt động; script loại loopback, Docker, Hyper-V, VPN và virtual adapter.
3. Kiểm tra:

   - `Server__PreferredIp`: IPv4 của Windows host, không phải dải bridge
     Docker `172.16.x`–`172.18.x`, loopback hoặc public IP.
   - `LanAccess__AllowedCidrs__N`: một hoặc nhiều CIDR private, không dùng
     `0.0.0.0/0`.
   - TCP `5048` và UDP `40550` không bị tiến trình khác chiếm.

Container bind UDP trên `0.0.0.0:40550`, nhưng response discovery chỉ được phát
khi có phòng LanOnly đang mở và `PreferredIp` hợp lệ. Backend không tự chọn IP
bridge làm fallback.

Khi có phòng đang nhận học sinh, chạy:

```powershell
.\scripts\test-docker-lan-discovery.ps1 -BroadcastAddress 192.168.1.255
```

Script xác nhận một response duy nhất, endpoint quảng bá là IP host, rồi gọi
`/health` qua TCP endpoint đó.

## Docker Desktop NAT và firewall

Port publishing trên Docker Desktop có thể làm `RemoteIpAddress` mà ứng dụng
nhìn thấy khác IP máy học sinh. Ứng dụng không tin `X-Forwarded-For` và không
mặc định cho phép mọi request từ Docker gateway.

Cấu hình an toàn mặc định:

```text
LanAccess__TrustDockerDesktopNat=false
```

Nếu kiểm thử thực tế chứng minh Docker Desktop thay source IP:

1. Giới hạn Windows Firewall cho TCP 5048 và UDP 40550 bằng profile `Private`
   và `LocalSubnet`.
2. Thêm đúng CIDR gateway Docker vào
   `LanAccess__TrustedDockerGatewayCidrs__N`.
3. Bật `LanAccess__TrustDockerDesktopNat=true`.
4. Chạy lại discovery từ một máy LAN được phép và thử từ mạng không được phép.

Không dùng CIDR public/broad để né NAT. Host networking không phải mặc định:
trên Docker Desktop nó cần phiên bản/hỗ trợ phù hợp, không dùng port mapping và
có các giới hạn riêng; chỉ đổi chiến lược sau một lượt kiểm chứng riêng.

Nếu gate HTTP LAN trả `403` do Docker Desktop NAT, mở PowerShell bằng quyền
Administrator và chạy lệnh sau khi backend container đã được khởi động:

```powershell
.\scripts\setup-docker-firewall.ps1 -ConfigureDockerDesktopNat
```

Script tạo/cập nhật đúng hai rule TCP 5048 và UDP 40550, chỉ cho phép
`Private` + `LocalSubnet`, đọc gateway từ chính container đang chạy, rồi chỉ
ghi CIDR `/32` vào `.env.docker`. Script không in secret. Sau đó restart backend
và chạy:

```powershell
.\scripts\test-docker-lan-discovery-integration.ps1
```

Gate này kiểm tra cả UDP response và HTTP `open-sessions` qua endpoint được
quảng bá. Nếu firewall chưa được giới hạn đúng, gate không chấp nhận cấu hình
tin NAT.

## Build, runtime và health

```powershell
docker compose config --quiet
docker compose build --no-cache
docker compose run --rm backend-tests
docker compose up -d backend
docker compose ps
docker compose logs --no-color backend
Invoke-RestMethod http://localhost:5048/health
```

`/health` trả `Healthy`, `Degraded` hoặc `Unhealthy`, gồm SQLite, volume,
Data Protection keys, UDP, advertised IP, CIDR, cấu hình Supabase và worker.
Endpoint không gọi remote Supabase và không trả secret. Cloud tắt hoặc chưa
preflight làm trạng thái `Degraded`; SQLite/volume/key không ghi được làm
`Unhealthy`.

## Secret và build context

`.env.docker` bị Git ignore và không nằm trong Docker build context. Không đưa
service-role key, database password, HMAC secret, signing key, JWT hoặc backup
vào repository/ZIP. Nếu `.env.docker` từng được chia sẻ, lập kế hoạch rotate
token signing key, receipt signing key, HMAC secret và Supabase secret/service
key; không in hoặc tự rotate các giá trị đó trong quy trình chẩn đoán.
