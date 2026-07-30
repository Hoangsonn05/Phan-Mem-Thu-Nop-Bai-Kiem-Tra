# Release Notes — Source v1.0.0

## Frontend

- Thay toàn bộ route sản phẩm dùng màn hình chung bằng View/ViewModel chuyên biệt.
- Bổ sung stateful mock để chạy xuyên suốt các workflow nghiệp vụ.
- Bổ sung typed HTTP verbs, streaming download, chunk upload và bearer token.
- Nối real REST/SignalR cho các module chính.
- Bổ sung workflow Giáo viên và Học sinh theo plan.
- Giữ Light/Dark design system, navigation an toàn, error recovery và logging.
- Loại bỏ nội dung hiển thị mang tính demo/beta/mock.

## Backend

- Giữ đầy đủ modular monolith, REST v1, SignalR, SQLite/file storage, submission, export, grading, control, backup và cloud adapter.
- Sửa các chuỗi tiếng Việt bị lỗi encoding trong auth/grading response.

## Packaging

- Đổi version source thành 1.0.0.
- Xóa build artifacts/runtime files.
- Regenerate source manifest và checksum ZIP.
