# Yêu cầu tích hợp của Người B

## B-06 — Gói thông báo thời gian thực dành cho học sinh

Đối tượng `StudentRealtimeNotification` đã được frontend chuẩn hóa hiện chỉ chứa `SessionId`, `EventName`, `Revision`, `ParticipantId` không bắt buộc và payload cho `TimeExtended` / `PublicCloudProjectionUpdated`. Bộ chuyển đổi thông báo có thể làm mới trạng thái có thẩm quyền một cách an toàn và hiển thị thông báo chung. Tuy nhiên, những người phụ trách lớp truyền tải cần giữ lại các trường gói và payload sau để giao diện có thể cung cấp đầy đủ định danh và nội dung sự kiện mà không phải suy đoán:

- `EventId`: ổn định giữa các lần thử lại và giống nhau trên OnlyLAN/PublicCloud.
- `EventName`: một trong các tên hợp đồng `RealtimeEvents` hiện có.
- `SessionId`.
- `ParticipantId`: người nhận rõ ràng cho các sự kiện theo phạm vi người dự thi.
- `SubmissionId`: bắt buộc đối với `SubmissionRejected` và kết quả chấm bài tệp.
- `ResultId`: bắt buộc đối với kết quả được trả hoặc mở lại.
- `Reason`: bắt buộc đối với thông báo từ chối hoặc mở lại.
- `Message`: bắt buộc đối với `TeacherMessageReceived`.
- `OccurredAtUtc`.
- `Revision`.

Các thiếu sót trong việc phát sự kiện được ghi nhận ở mã nguồn hiện tại:

- OnlyLAN không phát sự kiện thời gian thực sau `ResubmitAllowed`.
- Việc từ chối người dự thi ở OnlyLAN được lưu và ghi nhật ký kiểm tra dưới dạng `ParticipantRejected`, nhưng không có sự kiện thời gian thực nào được phát cho người dự thi.
- Việc mở lại điểm bài tệp được ghi nhật ký kiểm tra dưới dạng `GradeReopened`, nhưng không có sự kiện hợp đồng thời gian thực nào được phát.
- Tín hiệu chấm điểm trắc nghiệm PublicCloud đến frontend dưới dạng chuỗi tên sự kiện, không có các trường gói đã chuẩn hóa nêu trên.
- Quá trình phân tích OnlyLAN tổng quát làm mất `RealtimeEnvelope.EventId`, `RealtimeEnvelope.OccurredAtUtc` và phần lớn trường payload có kiểu cụ thể.

Cho đến khi hợp đồng chuẩn hóa được bổ sung, B-06 sử dụng khóa chống trùng cục bộ có tính xác định, chỉ gồm phiên, người dự thi, bản sửa đổi đã xác thực và tên sự kiện hiện có; thời điểm client nhận sự kiện được dùng làm mốc thời gian của popup. Frontend không bao giờ xem dữ liệu điểm/lý do/nội dung thông báo thời gian thực là dữ liệu có thẩm quyền và không hiển thị điểm nháp.

Các task bị ảnh hưởng: thông báo thời gian thực B-06 và làm mới kết quả cá nhân B-09. Phần công việc truyền tải/dịch vụ còn thiếu thuộc trách nhiệm của người phụ trách backend và PublicCloud; Người B không sửa mã backend, migration, RPC, RLS hoặc đối tượng cơ sở dữ liệu. Trung tâm thông báo frontend, bộ chuyển đổi, bộ bảo vệ người dự thi/phiên, chống trùng, định tuyến vòng đời và trạng thái lệnh đã hoàn tất. Phạm vi kiểm thử bằng dịch vụ giả và tại ranh giới truyền tải có trong `StudentRealtimeNotificationRoutingTests.cs` và `PublicCloudTimelineTests.cs`.

Tại thời điểm B-06 chưa có trang kết quả bài tệp dành cho học sinh. B-09 hiện đã cung cấp trang frontend đó và sử dụng thao tác đọc điểm OnlyLAN hiện có, được phân quyền theo người dự thi, cho bài nộp đang hoạt động đã biết. Endpoint lịch sử tài khoản hợp nhất và hợp đồng đọc bài tự luận/tệp tương ứng trên PublicCloud vẫn chưa có. Vì vậy, `GradeReturned` có thể hiển thị popup chung, còn việc làm mới danh sách an toàn theo người dự thi vẫn phụ thuộc vào các trường chuẩn hóa bên dưới.

## B-09 — Kết quả đã trả của tài khoản đã xác thực

Trang kết quả phải tải danh sách hợp nhất cho tài khoản đã xác thực mà không nhận hoặc gửi `StudentId` do bên gọi cung cấp. Các hợp đồng hiện tại chỉ cho phép truy vấn kết quả khi đã biết bài nộp/lần làm bài, vì vậy không thể cung cấp lịch sử tài khoản hoặc điền đáng tin cậy tất cả trường mà B-09 yêu cầu.

Hợp đồng có thẩm quyền bắt buộc cho cả OnlyLAN và PublicCloud:

- chỉ liệt kê kết quả ở trạng thái `Returned` thuộc tài khoản đã xác thực;
- mã kết quả, mã phiên, mã người dự thi, tên bài thi và loại hình phân phối;
- số lần làm bài chính xác từ nguồn có thẩm quyền;
- điểm, điểm tối đa, nhận xét và `ReturnedAtUtc`;
- phần xem lại từng câu trắc nghiệm, gồm lựa chọn đã chọn/đáp án đúng và số điểm đạt được;
- tệp đính kèm của điểm đã trả với URL tải xuống được phân quyền theo người dự thi;
- cùng một DTO có ngữ nghĩa thống nhất cho kết quả trắc nghiệm và tự luận/tệp ở cả hai chế độ.

`StudentQuizReviewDto` hiện không có trạng thái chấm điểm hoặc `ReturnedAtUtc`. `CorrectAnswersVisible` tạm thời được dùng làm tín hiệu an toàn cho kết quả đã trả; chỉ riêng `ScoreVisible` là chưa đủ vì chính sách có thể cho phép hiển thị điểm trước khi giáo viên trả kết quả. Endpoint điểm bài tệp hiện có chỉ hoạt động khi bên gọi đã biết mã bài nộp hiện tại, còn client desktop PublicCloud chưa có hợp đồng đọc/tải kết quả bài tự luận/tệp tương ứng.

Các sự kiện kết quả thời gian thực phải chứa mã `participant` của người nhận và mã kết quả cho `GradeReturned`, `QuizGradeReturned`, thao tác mở lại bài tệp/trắc nghiệm và `ResultReopened`. `GradeReturned` của OnlyLAN hiện thiếu định danh người dự thi, còn thao tác mở lại điểm bài tệp không có sự kiện chuẩn hóa. Nếu thiếu các trường này, trang kết quả không thể làm mới an toàn cho một học sinh đồng thời loại bỏ sự kiện của học sinh khác trong cùng phiên.

Các task bị ảnh hưởng: kết quả bài tự luận/tệp PublicCloud của B-09, danh sách lịch sử kết quả đã trả, liên kết số lần làm bài/thời điểm trả có thẩm quyền, tải tệp đính kèm và làm mới thời gian thực an toàn theo người dự thi. Các phương thức API/dịch vụ còn thiếu gồm một phương thức tương đương `GET /api/v1/student/results` cho OnlyLAN, được phân quyền theo người dự thi; một thao tác client/RPC PublicCloud tương ứng; và một phương thức tải tệp đính kèm có cùng ngữ nghĩa quyền sở hữu. DTO kết quả đã trả hợp nhất phải bổ sung `ResultId`, `SessionId`, `ParticipantId`, `ExamTitle`, `DeliveryType`, `AttemptNumber`, `GradingStatus`, `Score`, `MaxScore`, `GeneralComment`, `ReturnedAtUtc`, phần xem lại câu hỏi trắc nghiệm và thông tin mô tả tệp đính kèm.

Bố cục frontend B-09, mô hình trình bày, bộ lọc chỉ lấy trạng thái `Returned`, các trạng thái đang tải/trống/lỗi/thử lại, cô lập tài khoản, bảo vệ khỏi phản hồi cũ, điều hướng, trạng thái lệnh và xử lý thời gian thực có nhận biết người dự thi đã hoàn tất. Các kiểm thử bằng dịch vụ giả cho OnlyLAN/PublicCloud, bài trắc nghiệm/tệp, tệp đính kèm, đăng xuất, chuyển tài khoản, phản hồi cũ, lỗi/thử lại và thời gian thực nằm trong `StudentResultsTests.cs`. Người B chủ ý không triển khai hoặc thay đổi endpoint backend, DTO, migration cơ sở dữ liệu, RPC, chính sách RLS hoặc cơ chế dùng dữ liệu giả dự phòng trong production.
