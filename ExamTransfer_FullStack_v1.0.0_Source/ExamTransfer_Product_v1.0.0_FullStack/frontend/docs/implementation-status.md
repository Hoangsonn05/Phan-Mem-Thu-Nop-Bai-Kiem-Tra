# Frontend Implementation Status v1.1.0

## Kết luận kiểm tra mã nguồn

Frontend đã có View và ViewModel chuyên biệt cho toàn bộ menu Giáo viên/Học sinh. Hiện tượng tiêu đề menu đổi nhưng nội dung không cập nhật không phải vì các trang không tồn tại; nguyên nhân là sự kiện `PropertyChanged` được phát với tên helper method thay vì `CurrentPage`.

## Sửa lỗi điều hướng

File: `src/ExamTransfer.Desktop/ViewModels/MainViewModel.cs`

Trước sửa:

```csharp
Set(ref page, value);
```

Do lệnh nằm trong `SetCurrentPageWithoutDisposing`, `CallerMemberName` tạo notification tên `SetCurrentPageWithoutDisposing`. `ContentControl` bind `CurrentPage` không nhận được notification và giữ nguyên màn hình trước đó.

Sau sửa:

```csharp
Set(ref page, value, nameof(CurrentPage));
```

## Màn hình và thao tác đã có

- Lớp học: tạo/cập nhật/lưu trữ lớp, thêm/cập nhật/xóa học sinh, import, export, xem chi tiết.
- Bài kiểm tra: tạo, cập nhật, phát hành, nhân bản, lưu trữ, upload/download/xóa file đề.
- Phòng thi: tạo, mở, bắt đầu, tạm dừng/tiếp tục/kết thúc và làm mới.
- Phòng chờ: duyệt/từ chối, duyệt hàng loạt, gửi thông báo, khóa/mở nhận người, bắt đầu phiên.
- Giám sát: snapshot phiên, SignalR, gửi tin, cộng giờ, pause/resume/end.
- Thu bài: xem attempt, tải file, từ chối và cho nộp lại.
- Export: tạo job, theo dõi, tải và mở kết quả.
- Chấm bài: tải bài, lưu điểm/nhận xét, trả kết quả, export bảng điểm.
- Kiểm soát thi: lưu policy, xem/ack vi phạm, xử lý thiết bị.
- Lịch sử/Audit: tải dữ liệu, lọc và export.
- Backup/Restore: tạo, xác minh, tải và khôi phục.
- Cài đặt: mạng, lưu trữ, truyền file, cloud và diagnostics.
- Học sinh: kết nối, chờ duyệt, nhận đề, workspace, nộp bài, biên nhận, lịch sử và cài đặt.

## Hotfix biên dịch đi kèm

- `GlobalUsings.cs` cho System.IO và System.Net.Http.
- Namespace `ExamTransfer.Desktop.Core` cho `FrontendLogger`.
- `TryGuidAt` luôn gán out parameter.
- Mode `None` không tải nhầm menu Học sinh.

## Xác minh tĩnh

- 29 file XAML parse hợp lệ.
- 24 DataTemplate ViewModel → View.
- Tất cả command được bind trong XAML có property command tương ứng trong C#.
- Không thay đổi Backend hoặc Shared.Contracts.

## Xác minh bắt buộc trên Windows

```powershell
.\frontend\scripts\verify-frontend.ps1
.\scripts\run-backend.ps1
.\scripts\run-frontend.ps1 -ApiUrl "http://localhost:5048"
```

Sau khi mở app, kiểm tra từng menu và xác nhận vùng nội dung thay đổi theo tiêu đề.
