# Trạng thái Backend v1.2.0 Supabase-ready

## Hoàn thành trong mã nguồn

- SQLite/file storage local-first và durable outbox.
- Supabase PostgreSQL projection bắt buộc `organization_id`.
- RLS/GRANT cho metadata và private Storage theo organization.
- `UserSession` dùng publishable key + JWT Admin/Teacher.
- `TrustedServer` tùy chọn dùng secret key qua environment variable.
- Access/refresh token cache được bảo vệ bằng ASP.NET Data Protection.
- Refresh token nền, logout khi đổi project/tenant/mode.
- Standard upload file nhỏ và TUS resumable 6 MiB cho file lớn.
- Checkpoint upload URL/offset trong SQLite, retry/lease/coalescing.
- Cập nhật `SyncStatus` và `CloudObjectPath` về local entity.
- Cloud backup catalog, streaming download, checksum validation và restore.
- Một nguồn migration duy nhất: `backend/supabase`.
- Scripts configure, bootstrap, push/lint/test, source verify và API smoke test.

## Cần kiểm chứng với Supabase project thật

- `supabase db push`, linked lint và pgTAP.
- Auth user + organization bootstrap.
- Metadata projection cho từng module.
- TUS resume sau mất mạng và restart.
- Cloud backup download/restore.
- Key/session revocation và recovery.

Supabase không thuộc critical path của phòng thi. Cloud lỗi không được làm hỏng luồng LAN.
