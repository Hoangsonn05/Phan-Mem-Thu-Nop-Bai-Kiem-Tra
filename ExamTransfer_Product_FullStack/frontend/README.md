# ExamTransfer Frontend v1.1.0 — Replacement Folder

Thư mục này được thiết kế để thay thế trực tiếp thư mục `frontend` trong ExamTransfer Full Stack v1.0.0.

## Lỗi điều hướng đã sửa

Khi người dùng chọn một menu, `PageTitle` thay đổi nhưng vùng nội dung không cập nhật. Nguyên nhân là helper điều hướng gọi:

```csharp
Set(ref page, value);
```

Từ một method tên `SetCurrentPageWithoutDisposing`, nên `CallerMemberName` phát sự kiện `PropertyChanged` sai tên. `ContentControl` đang bind `CurrentPage` vì vậy không nhận được thông báo cập nhật.

Bản này phát đúng:

```csharp
Set(ref page, value, nameof(CurrentPage));
```

## Các màn hình chức năng có sẵn

- Giáo viên: Dashboard, lớp/học sinh/import, bài kiểm tra/file đề, phòng thi, phòng chờ, giám sát trực tiếp, thu bài, export, chấm bài, kiểm soát thi, lịch sử/audit, backup/restore, cài đặt.
- Học sinh: kết nối, phòng chờ, kỳ thi hiện tại, nhận đề, thư mục làm bài, nộp bài, biên nhận, lịch sử cục bộ, cài đặt.

Các workflow chi tiết được đặt trong View chuyên biệt hoặc panel bên phải để giữ trạng thái khi thao tác.

## Cách thay thế

1. Đóng ứng dụng.
2. Đổi tên thư mục `frontend` hiện tại thành `frontend_backup`.
3. Chép thư mục `frontend` này vào thư mục gốc dự án.
4. Chạy:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\frontend\scripts\verify-frontend.ps1
.\scripts\run-backend.ps1
.\scripts\run-frontend.ps1 -ApiUrl "http://localhost:5048"
```

## Các hotfix đi kèm

- Bổ sung `GlobalUsings.cs` cho `Stream`, `FileInfo`, `FileSystemWatcher`, `HttpClient`, `HttpResponseMessage`.
- Bổ sung namespace cho `FrontendLogger`.
- Sửa `TryGuidAt(..., out Guid value)` để luôn gán out parameter.
- Sửa mode `None` không tự sinh menu Học sinh.
- Không thay đổi Backend hoặc Shared.Contracts.

## Supabase v1.2.0

Trang Cài đặt Giáo viên hỗ trợ cấu hình Project URL, publishable key, Organization ID, môi trường, chế độ UserSession/TrustedServer, kiểm tra preflight, đăng nhập/đăng xuất và yêu cầu đồng bộ. Secret key không được nhập hoặc lưu trong frontend.
