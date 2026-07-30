# Frontend–Backend Feature Matrix v1.0

| Module | Frontend | REST/Realtime chính |
|---|---|---|
| System | Welcome/Main/Dashboard | `/health`, `/api/v1/system/status`, `/api/v1/dashboard/summary` |
| Classes | ClassManagement | `/api/v1/classes`, students CRUD, import preview/commit, export |
| Exams | ExamManagement | `/api/v1/exams`, publish/clone/archive, file init/chunk/finalize/content |
| Sessions | SessionManagement | `/api/v1/sessions`, open/start/pause/resume/collect/end |
| Lobby | Lobby | participant approve/reject/bulk-approve, messages |
| Live Monitor | LiveMonitor | session snapshot, extra time, messages, `/hubs/exam` |
| Submissions | SubmissionCenter | session submissions, detail, reject, allow-resubmit, file content |
| Exports | ExportCenter | `/api/v1/exports`, status/cancel/download |
| Grading | GradingCenter | queue, grade GET/PUT, return/reopen, attachment, gradebook export |
| Exam Control | ControlCenter | control policy, apply, device status, violations/actions/export |
| History/Audit | HistoryAudit | history sessions, audit logs, audit export |
| Backup/Settings | BackupCenter/Settings | backups create/validate/restore, settings GET/PUT, cloud sync |
| Student Join | StudentConnect/Waiting | session join, session detail, heartbeat |
| Exam Download | StudentDownload | exam manifest and file content/range |
| Workspace | StudentWorkspace | local folder watcher and file validation |
| Student Submission | StudentSubmission | init, chunk upload, status, finalize |
| Receipt/History | StudentReceipt/History | receipt, current participant state and local history store |

## Contract rules

- UUID string/`Guid` cho ID nghiệp vụ.
- Thời gian trao đổi bằng UTC.
- Backend là nguồn trạng thái Submitted, LateSubmitted, Returned và Applied.
- File transfer dùng chunk plan, hash và finalize.
- Frontend hiển thị lỗi từ `ApiError` và giữ `traceId` trong log.
