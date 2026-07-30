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
- Supabase schema, RLS, RPC, Storage, Realtime policy và pgTAP có nguồn duy nhất trong `backend/supabase`; `database/supabase` chỉ còn README legacy.

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
# Bản vá luồng thi 2026-07-22

Đã triển khai và kiểm thử tự động:

- Địa chỉ IP/cổng sinh viên nhập được dùng làm endpoint thật; token không được gửi sang origin khác. Quét LAN dùng UDP discovery có magic/protocol validation và deduplicate.
- Heartbeat sinh viên chạy nền một vòng duy nhất, retry theo backoff và không xóa phiên khi server tạm mất. SignalR sinh viên kết nối bằng participant token và làm mới deadline/trạng thái từ event.
- Tải đề dùng HTTP Range với file `.partial`, xác minh SHA-256 và chỉ thay file đích khi hash đúng.
- Hàng đợi nộp file lưu tại LocalAppData, token được bảo vệ bằng Windows DPAPI, giữ idempotency key, tiếp tục theo `MissingChunks`, rồi xóa queue sau receipt.
- Deadline được trả riêng cho participant và bộ đếm sinh viên ưu tiên deadline đã cộng giờ.
- Endpoint kết quả sinh viên yêu cầu đồng thời account token và participant token cùng user, đúng session/participant, mật khẩu đã đổi và grade đã được trả.
- `FileSubmission` được giữ nguyên. `MultipleChoice` có bảng/domain/API riêng, import JSON/CSV/XLSX cấu trúc chính thức, snapshot không chứa answer key, local answer outbox theo revision, finalize idempotent và chấm trên server.
- Có migration SQLite schema version 4 và Supabase migration/RLS/grant/index cho quiz.

Đã xác nhận bằng build và test trong repository; chưa xác nhận trong môi trường ngoài repository:

- UDP broadcast, SignalR reconnect, HTTP Range và DPAPI cần smoke test trên ít nhất hai máy Windows thật cùng LAN.
- Migration Supabase đã được viết cùng pgTAP test nhưng chưa được push vào project Supabase từ lượt vá này.
- Script `backend/scripts/test-multiple-choice-flow.ps1` cần token và exam/session thật để chạy acceptance HTTP đầu-cuối.
