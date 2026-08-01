# Bàn giao frontend của Người B

COMMIT GỐC (`BASE_COMMIT`): `db55788ece6973e321c9cad06904dc997db0cb6a`

COMMIT ĐẦU NHÁNH B (`B_HEAD`): `686d77eb2ed6ea6fc444c43292b348c01b3a7453`

NHÁNH (`BRANCH`): `work/person-b-frontend`

KHO MÃ TỪ XA (`REMOTE`): `https://github.com/ManhTien-360cm/Phan-Mem-Thu-Nop-Bai-Kiem-Tra.git`

COMMIT B-01: `da3a381e8ba4b4136844cc4b1b7e7ae8ec5509ef` — `fix(frontend): giải thích mã phòng công khai không rõ ràng -B01`

COMMIT B-02: `d43b2b5f7aa53c95210b2729a17ccde173d30f89` — `fix(frontend-dashboard): dừng đếm ngược phiên đã kết thúc -B02`

COMMIT B-03: `bd75c074b81764ffaeb88b74ff7d898f7991cc89` — `feat(submissions-ui): thêm lựa chọn nhiều bài nộp -B03`

COMMIT B-04: `4444b5ce7aaf6db5618273a3c4e4fb036daeee0e` — `feat(submissions-ui): tải các bài nộp đã chọn -B04`

COMMIT B-05: `ddc741a851eeeb5c0e20afa0d03d8b6c841b4fb2` — `feat(frontend-notifications): thêm trung tâm thông báo dạng popup -B05`

COMMIT B-06: `f5018e665ea7fd41bf47424ac3aa60f067e0bdb0` — `feat(frontend-notifications): chuyển sự kiện học sinh vào popup -B06`

COMMIT B-07: `8e96935bfb9a711e213917f518668e10b99e8bcf` — `feat(grading-ui): hoàn thiện chấm bài tự luận và tệp -B07`

COMMIT B-08: `8799284356e7bb2d06d77b9210b7ec4c48a10694` — `feat(grading-ui): thêm xem lại và chấm điểm trắc nghiệm -B08`

COMMIT B-09: `686d77eb2ed6ea6fc444c43292b348c01b3a7453` — `feat(student-results-ui): thêm trang kết quả đã trả -B09`

TỆP ĐÃ THAY ĐỔI: Có 39 đường dẫn duy nhất từ `BASE_COMMIT` đến B-09, gồm 37 đường dẫn mã nguồn/kiểm thử frontend và 2 đường dẫn tài liệu của Người B. Tệp bàn giao này là đường dẫn duy nhất thứ 40 trong commit tài liệu cuối. Kết quả `git diff --name-status BASE_COMMIT..B_HEAD` không chứa đường dẫn backend hoặc cơ sở dữ liệu.

BUILD DEBUG FRONTEND: ĐẠT — `dotnet build .\ExamTransfer.slnx -c Debug --no-restore`; 0 cảnh báo, 0 lỗi.

BUILD RELEASE FRONTEND: ĐẠT — `dotnet build .\ExamTransfer.slnx -c Release --no-restore`; 0 cảnh báo, 0 lỗi.

KIỂM THỬ FRONTEND: BỊ CHẶN — Lần chạy kiểm thử Release hoàn tất với 299 ca đạt, 2 ca lỗi, 0 ca bỏ qua, tổng cộng 301 ca. Cả hai lỗi đều là lỗi môi trường WPF đã tồn tại và được ghi nhận tại mốc ban đầu:

- `BulkArchiveCheckboxWpfTests.RealCheckboxWpfClick_ImmediatelyUpdatesExactRowsCountAndHeader`
- `StudentConnectWpfTests.PublicCloudRoomCode_RealControlUpdatesImmediatelyAndSurvivesModeToggle`
- Nguyên nhân gốc: quá trình khởi tạo tĩnh của WPF đi đến `MS.Internal.FontCache.Util` và ném `System.UriFormatException` trước khi logic frontend cần kiểm thử được chạy.

DỮ LIỆU BACKEND CẦN CÓ: Danh sách kết quả đã trả (`Returned`) hợp nhất và được phân quyền theo người dự thi cho tài khoản đã xác thực ở cả OnlyLAN và PublicCloud; số lần làm bài và `ReturnedAtUtc` chính xác từ nguồn có thẩm quyền; kết quả bài tự luận/tệp và tải tệp đính kèm trên PublicCloud; định danh người dự thi/kết quả trong các gói sự kiện thời gian thực khi trả hoặc mở lại kết quả. Chi tiết đầy đủ nằm trong `B_INTEGRATION_REQUIREMENTS.md`.

GIỚI HẠN ĐÃ BIẾT:

- Hai ca kiểm thử khởi tạo WPF ở mốc ban đầu không đạt trong môi trường máy hiện tại.
- B-09 chưa thể cung cấp đầy đủ lịch sử tài khoản hoặc kết quả bài tự luận/tệp trên PublicCloud cho đến khi có các hợp đồng backend/PublicCloud đã nêu trong tài liệu.
- Tín hiệu thời gian thực trả/mở lại điểm bài tệp ở OnlyLAN hiện không mang đủ định danh người nhận để làm mới an toàn theo từng người dự thi.
- Tín hiệu chấm điểm trắc nghiệm trên PublicCloud chưa cung cấp đầy đủ gói thông báo đã chuẩn hóa.

DỮ LIỆU GIẢ TRONG MÔI TRƯỜNG PRODUCTION: KHÔNG CÓ

TỆP BACKEND ĐÃ CHẠM TỚI: KHÔNG CÓ

TỆP CƠ SỞ DỮ LIỆU ĐÃ CHẠM TỚI: KHÔNG CÓ

TRUY CẬP TRỰC TIẾP SUPABASE/CLOUDOBJECTPATH TỪ VIEWMODEL CỦA NGƯỜI B: KHÔNG CÓ

KẾT QUẢ: BỊ CHẶN — Phần triển khai frontend của Người B, bố cục, mô hình trình bày, xác thực dữ liệu, trạng thái lệnh, cô lập tài khoản, cơ chế chặn phản hồi cũ và kiểm thử bằng dịch vụ giả đã hoàn tất. Chưa thể kết luận ĐẠT cuối cùng do hai lỗi môi trường WPF ở mốc ban đầu và các hợp đồng kết quả backend/PublicCloud còn thiếu đã được ghi rõ trong tài liệu.
