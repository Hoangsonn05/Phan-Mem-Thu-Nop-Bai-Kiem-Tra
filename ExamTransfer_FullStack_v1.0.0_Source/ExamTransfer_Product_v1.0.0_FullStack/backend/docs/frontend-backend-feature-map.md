# Bản đồ chức năng Frontend ↔ Backend

| Khu vực Frontend | Backend service/controller | Endpoint chính | Realtime/background |
|---|---|---|---|
| Tổng quan giáo viên | `SystemService`, `SystemController` | `GET dashboard/summary`, `GET system/status` | Cloud/status polling |
| Lớp học và học sinh | `ClassService`, `ClassesController` | CRUD `classes`, CRUD `students`, import preview/commit, export | Audit + Supabase outbox |
| Bài kiểm tra và file đề | `ExamService`, `ExamsController` | CRUD `exams`, publish/clone/archive, file init/chunk/finalize/content | `ExamPublished`, `ExamUpdated`, Storage outbox |
| Danh sách/thiết lập phòng thi | `SessionService`, `SessionsController` | CRUD/list/open/distribute/start/pause/resume/collect/end/cancel | `SessionStateChanged` |
| Phòng chờ giáo viên | `SessionService` | join, participant snapshot, approve/reject/bulk approve, message | participant/realtime events |
| Giám sát trực tiếp | `SessionService`, `HeartbeatWorker`, `ExamHub` | session snapshot, heartbeat, extra-time, message | SignalR sequence/snapshot recovery |
| Thu bài/chi tiết bài nộp | `SubmissionService`, `SubmissionsController` | session submissions, detail, file content, reject, allow-resubmit | submission events |
| Xuất dữ liệu | `ExportService`, `ExportWorker`, `ExportsController` | create/status/cancel/download | Background ZIP + outbox |
| Chấm bài | `GradeService`, `GradingController` | queue/detail/save/return/reopen/attachments/gradebook | `GradeReturned` + outbox |
| Kiểm soát phòng thi | `ControlService`, `ControlController` | policy/status/violations/ack/action | policy/violation realtime |
| Lịch sử và Audit | `HistoryService`, `HistoryController` | history session/detail, audit search/export | Audit append-only |
| Sao lưu/khôi phục | `BackupService`, `BackupsController`, `RestoreBootstrap` | create/list/validate/restore/download | Restart-safe restore + Storage outbox |
| Cài đặt/Cloud | `SystemService`, `SystemController` | settings, diagnostics, cloud status/trigger | Runtime JSON + Cloud worker |
| Học sinh kết nối/phòng chờ | `SessionService`, `SessionsController` | join, participant state, heartbeat | SignalR participant events |
| Nhận đề | `ExamService`, `ExamsController` | manifest, file content with Range | download status events |
| Nộp bài | `SubmissionService`, `SubmissionsController` | init/chunk/status/finalize/receipt | transfer/submission events |
| Biên nhận/lịch sử/kết quả | `SubmissionService`, `StudentController` | receipt, submission detail, returned grade/attachments | `GradeReturned` |

## Nguyên tắc dữ liệu

- SQLite và local file storage là nguồn chính trong phiên LAN.
- Supabase chỉ nhận projection qua outbox; cloud lỗi không rollback thao tác LAN đã thành công.
- File đề, bài nộp, export và backup được đưa lên bucket tương ứng.
- `organization_id` được thêm vào payload cloud khi `Cloud.OrganizationId` được cấu hình.
