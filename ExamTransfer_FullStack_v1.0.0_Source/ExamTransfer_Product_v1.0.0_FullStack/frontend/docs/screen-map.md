# Screen Map v1.0

| ID | Màn hình/Workflow | View | ViewModel |
|---|---|---|---|
| SH-01/02 | Khởi động, chọn chế độ, system status | `WelcomeView`, App Shell | `WelcomeViewModel`, `MainViewModel` |
| SH-03 | Cloud/local sync status | `SettingsPageView` | `SettingsPageViewModel` |
| T-01 | Tổng quan | `DashboardView` | `DashboardViewModel` |
| T-02/03/04 | Lớp, chi tiết lớp, học sinh, import/export | `ClassManagementView` | `ClassManagementViewModel` |
| T-05/06/07 | Danh sách/biên tập bài kiểm tra và file đề | `ExamManagementView` | `ExamManagementViewModel` |
| T-08/09 | Danh sách và thiết lập phòng thi | `SessionManagementView` | `SessionManagementViewModel` |
| T-10 | Phòng chờ | `LobbyView` | `LobbyViewModel` |
| T-11 | Giám sát trực tiếp | `LiveMonitorView` | `LiveMonitorViewModel` |
| T-12/13 | Thu bài và chi tiết attempt | `SubmissionCenterView` | `SubmissionCenterViewModel` |
| T-14 | Export | `ExportCenterView` | `ExportCenterViewModel` |
| T-15/16 | Hàng đợi và chi tiết chấm bài | `GradingCenterView` | `GradingCenterViewModel` |
| T-17 | Kiểm soát phòng thi | `ControlCenterView` | `ControlCenterViewModel` |
| T-18/19 | Lịch sử và Audit | `HistoryAuditView` | `HistoryAuditViewModel` |
| T-20 | Backup/Restore | `BackupCenterView` | `BackupCenterViewModel` |
| T-21 | Cài đặt | `SettingsPageView` | `SettingsPageViewModel` |
| S-01/02 | Tìm server và nhập thông tin tham gia | `StudentConnectView` | `StudentConnectViewModel` |
| S-03 | Phòng chờ học sinh | `StudentWaitingView` | `StudentWaitingViewModel` |
| S-04 | Kỳ thi hiện tại | `StudentExamView` | `StudentExamViewModel` |
| S-05 | Nhận đề | `StudentDownloadView` | `StudentDownloadViewModel` |
| S-06 | Workspace section inside current exam | `StudentExamView` | `StudentExamViewModel` |
| S-07 | Nộp bài | `StudentSubmissionView` | `StudentSubmissionViewModel` |
| S-08 | Biên nhận | `StudentReceiptView` | `StudentReceiptViewModel` |
| S-09 | Lịch sử cục bộ | `StudentHistoryView` | `StudentHistoryViewModel` |
| S-10 | Cài đặt học sinh | `StudentSettingsView` | `StudentSettingsViewModel` |

Các màn hình chi tiết được thể hiện trong panel bên phải, tab hoặc section của View chuyên biệt để giữ state và tránh tạo thêm top-level sidebar route.
