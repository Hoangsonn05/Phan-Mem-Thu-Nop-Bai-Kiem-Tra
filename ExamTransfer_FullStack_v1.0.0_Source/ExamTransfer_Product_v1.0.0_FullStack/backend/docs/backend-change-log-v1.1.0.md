# Backend change log v1.1.0

## Mục tiêu

Khớp Backend với toàn bộ màn hình và workflow chi tiết của Frontend ExamTransfer, đồng thời giữ SQLite/local storage là nguồn chính khi thi trong LAN và chuẩn bị projection để đồng bộ Supabase.

## Thay đổi chính

- Bổ sung import CSV/XLSX không phụ thuộc Microsoft Office.
- Hoàn thiện outbox cho toàn bộ entity/file có thể đồng bộ cloud.
- Bổ sung Supabase adapter cho PostgREST, Auth và Storage.
- Bổ sung migration cloud đầy đủ, index, RLS và bucket backup.
- Hoàn thiện export receipt/naming pattern/progress/cancel.
- Hoàn thiện backup mã hóa và restore sau khởi động lại.
- Persist các cài đặt ảnh hưởng startup vào `%ProgramData%`.
- Sửa version file đề đã phát hành theo cơ chế copy-forward an toàn.
- Bổ sung realtime event khi đề được cập nhật/phát hành.
- Tăng validation cho tạo/cập nhật phòng và duyệt hàng loạt.

## Không thay đổi contract

- Không đổi tên DTO/enum.
- Không đổi route REST v1 đang được Frontend sử dụng.
- Không đổi Hub path và tên realtime event.
- `ContractInfo.SchemaVersion` giữ nguyên vì không có breaking change.
