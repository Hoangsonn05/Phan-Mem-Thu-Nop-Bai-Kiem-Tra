# ExamTransfer Full Stack v1.0.0

ExamTransfer là ứng dụng Windows để gửi đề, nhận bài, quản lý phòng kiểm tra và chấm bài trong mạng LAN. Hệ thống vận hành theo kiến trúc local-first: máy giáo viên chạy Local Server, máy học sinh kết nối qua REST/SignalR và dữ liệu phòng thi vẫn được lưu cục bộ khi Internet gián đoạn.

## Cấu trúc mã nguồn

```text
ExamTransfer_Product_v1.0.0_FullStack/
├── frontend/    # WPF desktop, chế độ Giáo viên và Học sinh
├── backend/     # ASP.NET Core, Domain, Application, Infrastructure, Agent
├── database/    # SQLite documentation và Supabase migration/config
├── docs/        # Plan, mapping, integration, release notes
├── scripts/     # Setup, verify và run
├── ExamTransfer.slnx
└── global.json
```

## Các nhóm chức năng

### Giáo viên

- Dashboard và trạng thái hệ thống.
- Quản lý lớp, học sinh, import/export CSV.
- Tạo, cập nhật, phát hành và lưu trữ bài kiểm tra.
- Quản lý file đề, upload theo chunk, hash và version.
- Tạo phòng thi, mở phòng, bắt đầu, tạm dừng, tiếp tục, thu bài và kết thúc.
- Duyệt học sinh, nhắn tin và cộng thời gian.
- Giám sát realtime trạng thái kết nối, tải đề và nộp bài.
- Quản lý attempt, từ chối bài, cho nộp lại và tải file.
- Export dữ liệu, manifest, biên nhận và audit.
- Chấm điểm, rubric, nhận xét và trả kết quả.
- Cấu hình policy kiểm soát thi và xử lý vi phạm.
- Lịch sử, audit log, backup/restore và cài đặt.

### Học sinh

- Tìm máy chủ hoặc nhập IP/cổng/mã phòng.
- Gửi thông tin tham gia và chờ duyệt.
- Xem kỳ thi hiện tại theo thời gian máy chủ.
- Tải đề, tiếp tục tải và xác minh SHA-256.
- Chọn thư mục làm bài và theo dõi file.
- Nộp bài theo chunk, resume, finalize và nhận biên nhận.
- Xem lịch sử cục bộ và cấu hình profile/thư mục.

## Yêu cầu

- Windows 10/11 x64.
- .NET SDK theo `global.json`.
- PowerShell 5.1 trở lên.

## Thiết lập lần đầu

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\setup.ps1
```

## Kiểm tra source

```powershell
.\scripts\verify.ps1
```

## Chạy với Backend thật

PowerShell thứ nhất:

```powershell
.\scripts\run-backend.ps1
```

PowerShell thứ hai:

```powershell
.\scripts\run-frontend.ps1 -ApiUrl "http://localhost:5048"
```

Địa chỉ mặc định:

- Health: `http://localhost:5048/health`
- Swagger: `http://localhost:5048/swagger`
- REST: `http://localhost:5048/api/v1`
- SignalR: `http://localhost:5048/hubs/exam`

## Database và cloud

- SQLite và file system là nguồn dữ liệu chính khi vận hành phòng thi.
- Supabase PostgreSQL/Auth/Storage được đặt sau adapter và sync queue, không nằm trên critical path của LAN.
- Frontend không truy cập trực tiếp SQLite, Supabase hoặc đường dẫn file vật lý.

## Tài liệu

- `docs/plans/PLAN_FRONTEND.docx`
- `docs/plans/PLAN_BACKEND.docx`
- `docs/frontend-backend-feature-matrix.md`
- `docs/integration.md`
- `docs/implementation-status.md`
- `frontend/docs/design-system.md`
- `backend/docs/openapi/examtransfer-api-v1.yaml`
