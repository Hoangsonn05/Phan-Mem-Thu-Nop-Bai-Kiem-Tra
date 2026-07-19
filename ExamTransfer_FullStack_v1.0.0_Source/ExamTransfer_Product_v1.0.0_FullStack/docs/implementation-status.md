# Trạng thái triển khai ExamTransfer v1.0.0

## Frontend

- Toàn bộ mục sidebar Giáo viên và Học sinh đã được ánh xạ sang View/ViewModel chuyên biệt; không còn dùng trang workflow chung cho các route sản phẩm chính.
- Các workflow lớp/học sinh, bài kiểm tra/file đề, phòng thi/phòng chờ, giám sát, thu bài, export, chấm bài, kiểm soát thi, lịch sử/audit, backup và settings đã có command thực.
- Phía Học sinh đã có join, waiting, current exam, download, workspace, submission, receipt, history và settings.
- `IBackendClient` hỗ trợ GET/POST/PUT/DELETE, upload chunk, download streaming và bearer token.
- Mock backend là stateful in-memory implementation dùng cùng Shared.Contracts với real mode.
- Realtime monitor dùng SignalR và refresh snapshot sau event.
- Global exception logging, navigation rollback và trang lỗi phục hồi đã được giữ lại.
- Giao diện dùng design system Light/Dark hiện tại, không hiển thị nhãn demo/beta/mock trong UI sản phẩm.

## Backend

- Modular monolith với Domain/Application/Infrastructure/LocalServer/Shared.Contracts.
- REST API v1, response envelope, trace ID, Swagger và bearer token LAN.
- SQLite/EF Core, local file storage, WAL, optimistic row version và audit.
- Class/Student, import preview/commit và export.
- Exam/File upload chunk, finalize hash, manifest và version.
- Session/Participant state machine, join, approve, message, extra time và heartbeat.
- SignalR hub và UDP discovery.
- Submission init/chunk/status/finalize/receipt, attempt append-only, reject và resubmit.
- Export background jobs, grading, control policy/violation, history/audit, backup/restore, settings và cloud sync adapter.
- Supabase schema, RLS, bucket definitions và sync queue đã có trong `database/supabase`.

## Giới hạn cần hardening trước production diện rộng

- Import XLSX hiện cần adapter Open XML hoàn chỉnh; CSV đã theo đúng workflow preview/commit.
- Supabase binary storage upload cần kiểm thử tích hợp với project thật và credential production.
- Student Agent hiện là protocol/capability baseline; cần triển khai và kiểm thử policy Windows trên thiết bị mục tiêu.
- HTTPS LAN, certificate pinning, installer, firewall rule, signing, load/recovery test cần thực hiện trên Windows.
- Dependency SQLite có cảnh báo bảo mật trong baseline package; cần nâng phiên bản và chạy regression test trước phát hành chính thức.

## Xác minh trong gói nguồn này

- Đã kiểm tra tĩnh cấu trúc project reference, XAML/XML, JSON, resource key, x:Class/code-behind và manifest.
- Không còn route sản phẩm chính trỏ tới `FeaturePageView` hoặc `WorkflowPageView`.
- Không còn endpoint `/mock` trong ViewModel production.
- Môi trường đóng gói không có .NET SDK/Windows, vì vậy build WPF và smoke test runtime phải chạy bằng `scripts/verify.ps1` trên máy Windows trước khi phát hành binary.
