# Thay thế Backend v1.2.0

1. Dừng Frontend và Backend.
2. Sao lưu folder `backend` cũ và `%ProgramData%\ExamTransfer`.
3. Thay toàn bộ folder `backend` bằng bản v1.2.0.
4. Giữ folder `frontend` cùng bản vì Shared.Contracts đã mở rộng cài đặt cloud.
5. Chạy:

```powershell
.\scripts\setup.ps1
.\scripts\verify.ps1
```

6. Xác nhận local workflow trước khi bật Cloud.
7. Đẩy migration từ nguồn duy nhất `backend\supabase`.
8. Cấu hình Supabase bằng `backend\scripts\configure-supabase.ps1`.
9. Chạy Backend và kiểm tra `GET /api/v1/cloud/preflight`.

Không chép database runtime, secret key hoặc file `cloud-session.protected` vào source.
