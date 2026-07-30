# Quy trình bài kiểm tra hoàn chỉnh

## Tạo bài tự luận / nộp file

Trong tab **Bài kiểm tra**, chọn **Tự luận / nộp file**. Có thể lưu nháp khi chưa có file. Trước khi phát hành, tải lên ít nhất một file đề hoàn tất nếu quy tắc file yêu cầu. Tùy chọn **Áp dụng quy trình giám sát** quyết định session có snapshot `None` hay `Standard`; các giới hạn và định dạng file nộp của học sinh không thay đổi.

## Tạo bài trắc nghiệm

Chọn **Trắc nghiệm**, chọn chính sách **Hiện điểm sau khi nộp** hoặc để tắt nhằm ẩn điểm, rồi chọn nguồn `.docx` hoặc `.pdf`.

Nguồn hợp lệ có từ 1 đến 500 câu. Mỗi câu có 2 đến 10 lựa chọn A–J và một dòng đáp án ngay sau các lựa chọn, ví dụ:

```text
1_1_1: Nội dung câu hỏi?
A. Lựa chọn A
B. Lựa chọn B
C. Lựa chọn C
D. Lựa chọn D
Đáp án đúng: A. Lựa chọn A
```

Nhiều đáp án dùng dấu phẩy, chấm phẩy hoặc `|`, ví dụ `Đáp án đúng: A; C`. Nội dung ghi sau nhãn đáp án phải khớp lựa chọn. PDF phải có lớp văn bản có thể chọn; hệ thống không chạy OCR ngầm.

Nút xem trước chỉ đọc và kiểm tra, không thay câu hỏi hay phát outbox. Sau khi hết lỗi, dùng **Commit bộ câu hỏi**. Nếu đề đã có câu, UI yêu cầu xác nhận replace. File Word/PDF nguồn được lưu riêng với SHA-256 và chỉ giáo viên được đọc; file này không nằm trong manifest học sinh.

Trắc nghiệm luôn dùng giám sát `Standard`. Không thể đổi loại bài, chính sách điểm hoặc giám sát sau khi phát hành, có session hoặc có attempt; hãy dùng chức năng nhân bản.

## Luồng học sinh và kết quả

**Kỳ thi hiện tại** và **Thi trắc nghiệm** dùng chung `StudentExamFlowCoordinator`. Coordinator kiểm tra phiên, trạng thái duyệt, loại đề và attempt trước khi điều hướng. Attempt mới chỉ được tạo sau xác nhận; attempt đang làm được resume.

Khi chính sách là `ShowAfterSubmission`, điểm chỉ xuất hiện sau finalize. Khi là `Hidden`, student DTO/RPC trả `ScoreVisible=false` và `Score=null`. Đáp án đúng không được đưa vào snapshot hoặc DTO học sinh.

## Xác minh và triển khai

Chạy từ thư mục gốc:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-complete-exam-workflow.ps1 -Configuration Release
```

Verifier không reset database và không đẩy production. Khi local Supabase có sẵn, script chỉ chạy migration tiến tới rồi pgTAP; nếu không có, summary ghi `SQL_RUNTIME_PENDING`.

Các migration mới cần áp dụng theo đúng thứ tự là:

```text
backend/supabase/migrations/20260725174327_complete_exam_workflow.sql
backend/supabase/migrations/20260725181858_complete_exam_workflow_submission_timeline.sql
```

Trước production phải sao lưu, chạy preflight/migration/pgTAP/RLS trên staging phù hợp, kiểm tra rollback và có xác nhận triển khai. Không dùng verifier như phê duyệt production.
