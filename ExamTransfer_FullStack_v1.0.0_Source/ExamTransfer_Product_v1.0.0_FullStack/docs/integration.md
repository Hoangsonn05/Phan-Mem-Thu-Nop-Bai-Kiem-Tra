# Frontend + Backend Integration v1.0

1. Frontend tham chiếu trực tiếp `backend/src/ExamTransfer.Shared.Contracts` cho enum, DTO, request, response và event.
2. REST base path là `/api/v1`; lỗi dùng `ApiResponse<T>` và có `traceId`.
3. SignalR hub là `/hubs/exam`; sau reconnect Frontend lấy lại REST snapshot để tránh mất event.
4. Frontend không truy cập trực tiếp SQLite, Supabase hoặc file path vật lý của Backend.
5. SQLite/local storage là nguồn chính khi phòng thi hoạt động; cloud sync không chặn luồng LAN.
6. API access tập trung trong `IBackendClient`, hỗ trợ GET/POST/PUT/DELETE, chunk upload và streaming download.
7. Mock mode và real mode dùng chung DTO/request/interface; ViewModel không cần đổi khi chuyển môi trường.
8. Mỗi top-level route có ViewModel và View riêng. Các màn hình chi tiết được thể hiện trong panel nghiệp vụ của module tương ứng thay vì trang blueprint dùng chung.
9. Giáo viên real mode dùng bearer token từ `EXAMTRANSFER_TEACHER_TOKEN`; development fallback là token local của Backend.
10. Học sinh nhận session token từ join response và `StudentSessionState` gắn token đó cho các request phiên.

## Luồng tích hợp chính

```text
Teacher: Class → Exam → Exam Files → Session → Lobby → Live Monitor
         → Submission Center → Export/Grading/History

Student: Connect/Join → Waiting → Current Exam → Download → Workspace
         → Init/Chunk/Finalize → Receipt → Local History
```
