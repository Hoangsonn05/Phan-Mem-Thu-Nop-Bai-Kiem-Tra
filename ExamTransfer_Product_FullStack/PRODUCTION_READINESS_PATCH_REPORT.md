# Báo cáo vá Production Readiness – bản 16

## Kết luận

Mã nguồn đã được vá để loại bỏ các blocker tĩnh còn lại trước bước backup và
cập nhật Supabase production. Bản này **chưa được phép tự tuyên bố đã sẵn sàng
production chỉ bằng kiểm tra mã nguồn**. Trên máy Windows triển khai phải chạy
`check-production-update-readiness.ps1` với đầy đủ Docker, LAN discovery,
Supabase local, pgTAP, remote preflight/dry-run và backup verification.

## Các lỗi đã vá

- Loại `.env.docker` thật và bản sao project bị trùng khỏi gói nguồn.
- Khóa script push Supabase cũ để không thể push khi chưa có backup/readiness.
- Thêm script production update có cổng bảo vệ bằng:
  - Project Ref xác nhận chính xác.
  - Backup đã xác minh.
  - Báo cáo `BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE`.
  - Preflight và `db push --dry-run` chạy lại ngay trước write.
  - Secret HMAC cho Edge Function phải tồn tại.
- Sửa migration upgrade test:
  - Bỏ cờ không hợp lệ `db reset --sql-paths`.
  - Reset đến migration trước PublicCloud.
  - Nạp fixture dữ liệu cũ qua `psql`.
  - Chạy migration còn lại và pgTAP kiểm tra nâng cấp.
- Sửa backup database:
  - Schema, data, roles.
  - Schema và data của migration history.
  - Manifest, SHA-256 và ZIP.
- Sửa backup Storage:
  - Dùng đúng bucket `report-exports` và `backup-archives`.
  - Sao lưu toàn bộ bucket được phát hiện, không chỉ bucket hardcode.
  - Object được lưu bằng đường dẫn băm an toàn; manifest giữ tên gốc.
  - Pagination, retry, kích thước và SHA-256.
- Sửa backup verifier tương thích manifest mới và chống path traversal.
- Sửa preflight không đánh nhầm file LAN cũ thành PublicCloud blocker.
- Readiness Docker dùng Compose project/volume/port cô lập, không đụng volume
  backend đang sử dụng.
- Readiness tạo báo cáo JSON để script update production xác minh lại.
- Bổ sung kiểm tra Preferred IP/CIDR dựa trên adapter vật lý.
- Backup và báo cáo mặc định được lưu ngoài repository; `.gitignore` và
  `.dockerignore` chặn env, backup, Supabase temp/branch metadata.

## Kiểm tra đã thực hiện trong môi trường phân tích

- JSON: parse thành công.
- XML/XAML/CSPROJ/SLNX: parse thành công.
- YAML/Compose: parse thành công.
- SQL: kiểm tra cân bằng quote, dollar quote, comment và ngoặc thành công.
- PowerShell: kiểm tra cấu trúc chuỗi, here-string, comment và ngoặc thành công.
- Không còn `.env.docker` thật trong gói nguồn.
- Không còn shadow project ở root.
- Các script backup, verifier, readiness, preflight và production update đều có
  trong đúng thư mục.

Đây là kiểm tra tĩnh, không thay thế parser PowerShell chính thức, `dotnet test`,
Docker runtime, PostgreSQL thật hoặc Supabase CLI.

## Các gate bắt buộc trên máy triển khai

```powershell
.\backend\scripts\check-production-update-readiness.ps1 `
  -RunDockerGates `
  -RunLanDiscoveryGate `
  -RunSupabaseLocalGates
```

Chỉ khi output là `READY_FOR_BACKUP` mới chạy backup.

Sau backup và link đúng project, chạy lại:

```powershell
.\backend\scripts\check-production-update-readiness.ps1 `
  -RunDockerGates `
  -RunLanDiscoveryGate `
  -RunSupabaseLocalGates `
  -AllowRemoteReadAndDryRun `
  -ProjectRef "uythsrpriegwwdwnbisi" `
  -ConfirmProjectRef "uythsrpriegwwdwnbisi" `
  -BackupSetPath "<THU_MUC_BACKUP_SET>"
```

Chỉ khi output là `BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE` mới dùng
`apply-supabase-production-update.ps1`.

## Trạng thái hiện tại

**READY_FOR_RUNTIME_GATES**

Chưa có kết quả runtime trong môi trường phân tích vì không có .NET SDK, Docker,
Supabase CLI, PowerShell Windows, credentials hoặc quyền project.
