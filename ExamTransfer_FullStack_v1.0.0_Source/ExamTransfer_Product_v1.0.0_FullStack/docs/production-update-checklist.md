# Checklist cập nhật Supabase production

Checklist này chỉ dành cho lượt được người dùng cho phép thao tác production.
Không dùng nó để tự link, push, deploy, sửa migration history hoặc dữ liệu.

## Điều kiện đầu vào

- Readiness local trả `READY_FOR_BACKUP`.
- Docker LAN discovery gate qua cả UDP và HTTP; nếu Docker Desktop NAT thay IP
  nguồn thì hai firewall rule `Private` + `LocalSubnet` đã được xác minh và chỉ
  gateway `/32` của container được tin.
- Có đúng Project Ref production và người vận hành xác nhận bằng tay.
- Có `SUPABASE_DB_URL` và service-role key qua environment/secure prompt;
  không ghi chúng vào file/log.
- Có cửa sổ bảo trì, người phụ trách rollback và vị trí backup riêng tư ngoài
  repository.

## Thứ tự bắt buộc

1. Đặt `Cloud__Enabled=false`.
2. Dừng Local Server production để đóng băng outbox/pull cursor.
3. Kiểm tra Project Ref đã link; nếu sai hoặc chưa link thì dừng.
4. Chạy production legacy preflight chỉ đọc.
5. Chạy `supabase migration list --linked`.
6. Chạy `supabase db push --linked --dry-run`.
7. Backup database và toàn bộ Storage bucket đang tồn tại:

   ```powershell
   .\backend\scripts\backup-supabase-production-all.ps1 `
     -ProjectRef '<project-ref>' `
     -Confirmation 'BACKUP ALL <project-ref>'
   ```

8. Chạy `verify-supabase-production-backup.ps1` với thư mục backup vừa tạo.
9. Chỉ tiếp tục khi output chính xác là `BACKUP_READY`.
10. Xác nhận secret `EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET` đã tồn tại bằng
    `supabase secrets list --project-ref <project-ref>`.
11. Chạy lại readiness đầy đủ với `-AllowRemoteReadAndDryRun` và
    `-BackupSetPath`. Chỉ tiếp tục khi output là
    `BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE` và có file báo cáo JSON.
12. Người được ủy quyền chạy duy nhất script có cổng bảo vệ:

    ```powershell
    .\backend\scripts\apply-supabase-production-update.ps1 `
      -ProjectRef '<project-ref>' `
      -ConfirmProjectRef '<project-ref>' `
      -BackupSetPath '<thu-muc-backup-set>' `
      -ReadinessReportPath '<readiness-report.json>' `
      -Confirmation 'UPDATE SUPABASE PRODUCTION <project-ref>' `
      -AllowProductionUpdate `
      -MaintenanceWindowConfirmed `
      -CloudDisabledConfirmed `
      -LocalServersStoppedConfirmed
    ```

    Script tự xác minh lại backup, chạy lại preflight/dry-run, chạy `db push`,
    lint và deploy ba Edge Functions. Script cũ
    `push-supabase-schema.ps1` đã bị khóa để tránh push nhầm.
13. Bật Realtime `Private channels only` trong Dashboard.
14. Chạy toàn bộ acceptance script với tenant/user/class/session/file có nhãn
    `TEST`; xác nhận RLS, RPC, Storage, Realtime, retry và projection.
15. Chỉ bật lại `Cloud__Enabled=true` sau khi toàn bộ acceptance đạt.
16. Theo dõi Local Server logs, outbox, pull cursor, quarantine/failure queue
    và schema capability trước khi kết thúc cửa sổ bảo trì.

Không push trước backup. Database dump không chứa object Storage, vì vậy cả
database ZIP và Storage ZIP đều bắt buộc.

## Chọn nhánh migration `20260722141147`

Không suy đoán trạng thái remote:

- Nếu `supabase migration list` cho biết `20260722141147` chưa áp dụng, source
  hiện tại tạo ngay partial index chỉ cho `source_mode='PublicCloud'`.
- Nếu remote đã áp dụng migration đó, không repair hoặc viết lại history.
  Migration `20260722161450` là forward fix: bỏ global index nếu tồn tại và tạo
  partial index, không xóa dữ liệu.

Preflight phải không có `BLOCKER`. Nếu có dữ liệu nhiều file trước khi migration
cũ còn pending, orphan/cross-organization reference, duplicate cloud ID hoặc
object path sai thì dừng và lập phương án dữ liệu được duyệt.

## Backup và rollback

Output mặc định:

```text
%USERPROFILE%\Documents\ExamTransfer-Private-Backups\Supabase
```

Backup set gồm schema, data, roles, migration history, object Storage, manifest
và SHA-256. Giữ một bản sao ngoài máy triển khai và kiểm thử restore trong môi
trường cô lập. Nếu update lỗi, giữ Cloud tắt; không sửa migration history bằng
`migration repair`. Khôi phục backup đã xác minh hoặc triển khai forward-fix đã
review, rồi chạy lại acceptance trước khi bật Cloud.


## Backup script requirements

- Run on Windows PowerShell 5.1 or PowerShell 7 with Docker Desktop and Supabase CLI.
- `SUPABASE_DB_URL` must be copied from the exact confirmed Project Ref; the script rejects a URL that does not contain that ref.
- Storage backup downloads every bucket returned by the project and requires the legacy application buckets `exam-archives`, `submission-archives`, `report-exports`, and `backup-archives` before migration.
- Object files are stored under hashed local paths; `storage-manifest.json` preserves the original bucket and object names for verification/restore tooling.
