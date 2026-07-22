# HƯỚNG DẪN BUILD VÀ CẬP NHẬT EXAMTRANSFER

## 1. Phương án phát hành được chọn

- Biên dịch bình thường ở chế độ `Release`.
- Chưa làm rối hoặc mã hóa mã nguồn vì dự án đang ở quy mô đồ án.
- Frontend được publish dạng `self-contained`, máy người dùng không cần cài .NET.
- Frontend được gom thành file thực thi chính `ExamTransfer.Desktop.exe`.
- Local Server được publish thành một thư mục chạy độc lập.
- Inno Setup đóng frontend và Local Server thành **một file cài đặt duy nhất**.
- Khi có phiên bản mới, người dùng chạy installer mới trực tiếp lên bản cũ, **không gỡ cài đặt**.
- Dữ liệu không đặt trong thư mục chương trình nên không bị installer ghi đè.

File thành phẩm:

```text
ExamTransfer-Setup-x.y.z.exe
```

Trong đó `x.y.z` là phiên bản, ví dụ:

```text
ExamTransfer-Setup-1.2.0.exe
ExamTransfer-Setup-1.2.1.exe
ExamTransfer-Setup-1.3.0.exe
```

---

## 2. Cách dữ liệu được bảo vệ khi cập nhật

File chương trình được cài vào:

```text
C:\Program Files\ExamTransfer
```

Dữ liệu của Local Server được lưu ngoài thư mục chương trình:

```text
C:\ProgramData\ExamTransfer
```

Các phần được giữ lại khi cài đè:

- SQLite database.
- File đề.
- File bài nộp.
- Biên nhận.
- Điểm và dữ liệu chấm.
- File export.
- Backup.
- Log.
- Cấu hình runtime.
- Hàng đợi đồng bộ Supabase.

Cấu hình runtime nằm tại:

```text
C:\ProgramData\ExamTransfer\config\runtime-settings.json
C:\ProgramData\ExamTransfer\config\cloud-settings.json
```

Dữ liệu trên máy học sinh được lưu tại:

```text
%LocalAppData%\ExamTransfer
```

Bao gồm:

- Phiên đăng nhập.
- Hàng đợi nộp bài.
- Đáp án trắc nghiệm chưa đồng bộ.
- Log frontend.

Installer không được có lệnh xóa hai thư mục `ProgramData` và `LocalAppData` nói trên.

---

## 3. Điều kiện để cập nhật không cần gỡ bản cũ

Mọi phiên bản installer phải giữ nguyên:

```ini
AppId={{724D43BD-E4C5-4927-A3CF-8AC292F03D21}
```

Không được tạo GUID mới cho mỗi phiên bản.

Cũng phải giữ ổn định:

```ini
AppName=ExamTransfer
DefaultDirName={autopf}\ExamTransfer
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
```

Mỗi phiên bản mới chỉ thay đổi:

```text
1.2.0 → 1.2.1 → 1.3.0
```

Cùng `AppId` giúp Inno Setup nhận ra đây là cùng một phần mềm, dùng lại thư mục cài đặt và nối bản cập nhật vào mục gỡ cài đặt hiện có.

---

## 4. Công cụ cần cài trên máy dùng để build

### 4.1. .NET SDK

Dự án có `global.json` yêu cầu:

```text
.NET SDK 10.0.100
```

`rollForward` cho phép dùng bản feature mới hơn phù hợp, ví dụ `10.0.3xx`.

Kiểm tra:

```powershell
dotnet --version
dotnet --list-sdks
```

### 4.2. Inno Setup 6

Cài Inno Setup 6 để tạo file installer `.exe`.

Sau khi cài, file compiler thường nằm tại:

```text
C:\Program Files (x86)\Inno Setup 6\ISCC.exe
```

### 4.3. Windows

Nên build trên Windows 10 hoặc Windows 11 x64.

---

## 5. Chuẩn bị ba file hỗ trợ

Tại thư mục gốc của dự án:

```text
ExamTransfer_Product_v1.0.0_FullStack
```

đặt file:

```text
build-release.ps1
```

Tạo thư mục:

```text
installer
```

Bên trong đặt:

```text
installer\ExamTransfer.iss
```

Cấu trúc sau khi thêm:

```text
ExamTransfer_Product_v1.0.0_FullStack\
├── backend\
├── frontend\
├── installer\
│   └── ExamTransfer.iss
├── artifacts\
├── build-release.ps1
├── ExamTransfer.slnx
└── global.json
```

Hai file mẫu đi kèm tài liệu này:

- `build-release.ps1`
- `ExamTransfer.iss`

---

# PHẦN A — BUILD THÀNH PHẨM LẦN ĐẦU

## 6. Kiểm tra mã nguồn trước khi build

Mở PowerShell tại thư mục gốc dự án.

Ví dụ:

```powershell
cd "D:\MMO\PhanMemNopThuBaiKiemTra\ExamTransfer_FullStack_v1.0.0_Source\ExamTransfer_Product_v1.0.0_FullStack"
```

Cho phép chạy script trong cửa sổ hiện tại:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
```

Kiểm tra thay đổi chưa commit:

```powershell
git status --short
```

Không build nhầm từ một thư mục mã nguồn cũ.

---

## 7. Đặt phiên bản đầu tiên

Bản mã nguồn hiện tại dùng phiên bản frontend và backend:

```text
1.2.0
```

### 7.1. Frontend

Mở:

```text
frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj
```

Kiểm tra:

```xml
<Version>1.2.0</Version>
<AssemblyVersion>1.2.0.0</AssemblyVersion>
<FileVersion>1.2.0.0</FileVersion>
```

### 7.2. Backend

Mở:

```text
backend\Directory.Build.props
```

Kiểm tra:

```xml
<Version>1.2.0</Version>
<AssemblyVersion>1.2.0.0</AssemblyVersion>
<FileVersion>1.2.0.0</FileVersion>
```

### 7.3. Phiên bản chung ở thư mục gốc

Mở:

```text
Directory.Build.props
```

Nên đổi phiên bản cũ `1.0.0` thành cùng phiên bản đang phát hành:

```xml
<Version>1.2.0</Version>
<AssemblyVersion>1.2.0.0</AssemblyVersion>
<FileVersion>1.2.0.0</FileVersion>
```

Việc này giúp tránh trường hợp các project phụ hiển thị phiên bản khác nhau.

---

## 8. Chạy build bằng một lệnh

Tại thư mục gốc dự án, chạy:

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-release.ps1" -Version "1.2.0"
```

Script sẽ tự thực hiện:

1. Kiểm tra `dotnet`.
2. Kiểm tra Inno Setup.
3. Xóa thư mục kết quả build cũ.
4. Restore backend.
5. Chạy toàn bộ backend test ở chế độ Release.
6. Chạy kiểm tra frontend.
7. Publish frontend cho `win-x64`.
8. Publish Local Server cho `win-x64`.
9. Tạo installer bằng Inno Setup.
10. Tạo mã SHA-256 cho installer.

Không dùng `-SkipTests` cho bản đem nộp hoặc phát hành.

---

## 9. Kết quả sau khi build

Frontend:

```text
artifacts\release\Client\ExamTransfer.Desktop.exe
```

Local Server:

```text
artifacts\release\Server\ExamTransfer.LocalServer.exe
```

Installer:

```text
artifacts\installer\ExamTransfer-Setup-1.2.0.exe
```

Mã kiểm tra file:

```text
artifacts\installer\ExamTransfer-Setup-1.2.0.exe.sha256.txt
```

File cần gửi cho người dùng là:

```text
ExamTransfer-Setup-1.2.0.exe
```

Không gửi riêng thư mục `Client` hoặc `Server` cho người dùng bình thường.

---

## 10. Cài đặt trên máy giáo viên

Chạy installer bằng quyền quản trị.

Chọn:

```text
Máy giáo viên - Giao diện và Local Server
```

Installer sẽ:

- Cài frontend.
- Cài Local Server.
- Tạo shortcut.
- Cho phép chọn tự chạy Local Server khi đăng nhập Windows.
- Mở TCP 5048.
- Mở UDP 5050.

Sau khi cài:

1. Mở `ExamTransfer Local Server`.
2. Mở trình duyệt và kiểm tra:

```text
http://localhost:5048/health
```

3. Nếu nhận được trạng thái `ok`, mở ExamTransfer.
4. Đăng nhập giáo viên.
5. Kiểm tra lớp, bài kiểm tra và cài đặt.

---

## 11. Cài đặt trên máy học sinh

Chạy cùng installer.

Chọn:

```text
Máy học sinh - Chỉ cài giao diện
```

Máy học sinh không cần cài Local Server.

Sau khi cài:

1. Mở ExamTransfer.
2. Đăng nhập học sinh.
3. Quét máy giáo viên trong LAN hoặc nhập IP thủ công.
4. Nhập mã phòng.
5. Gửi yêu cầu tham gia.

---

## 12. Kiểm thử bản thành phẩm

Không chỉ kiểm tra trên máy lập trình.

Nên thử trên:

- Một máy giáo viên không cài .NET.
- Ít nhất hai máy học sinh không cài .NET.
- Cùng mạng LAN.

Kiểm tra tối thiểu:

1. Cài mới.
2. Local Server khởi động.
3. `/health` trả về thành công.
4. Giáo viên đăng nhập.
5. Học sinh đăng nhập.
6. Máy học sinh tìm thấy máy giáo viên.
7. Tạo lớp.
8. Tạo bài kiểm tra.
9. Mở phòng.
10. Học sinh tham gia.
11. Tải đề.
12. Nộp bài.
13. Làm trắc nghiệm.
14. Tắt Internet nhưng giữ LAN.
15. Khởi động lại ứng dụng.
16. Kiểm tra dữ liệu cũ vẫn còn.

---

# PHẦN B — BUILD LẠI KHI CÓ UPDATE

## 13. Quy tắc đặt phiên bản

Dùng dạng:

```text
MAJOR.MINOR.PATCH
```

Ví dụ:

| Loại thay đổi | Phiên bản |
|---|---|
| Vá lỗi nhỏ | `1.2.0` → `1.2.1` |
| Thêm hoặc cải thiện chức năng | `1.2.1` → `1.3.0` |
| Thay đổi lớn, có thể không tương thích | `1.3.0` → `2.0.0` |

Không phát hành hai file khác nhau cùng mang một phiên bản.

---

## 14. Các bước mỗi lần cập nhật

Ví dụ nâng từ `1.2.0` lên `1.2.1`.

### Bước 1 — Hoàn thành sửa mã nguồn

Kiểm tra:

```powershell
git status --short
```

Xác nhận đang đứng đúng nhánh và đúng bản mã nguồn.

### Bước 2 — Tăng phiên bản frontend

Mở:

```text
frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj
```

Đổi thành:

```xml
<Version>1.2.1</Version>
<AssemblyVersion>1.2.1.0</AssemblyVersion>
<FileVersion>1.2.1.0</FileVersion>
```

### Bước 3 — Tăng phiên bản backend

Mở:

```text
backend\Directory.Build.props
```

Đổi thành:

```xml
<Version>1.2.1</Version>
<AssemblyVersion>1.2.1.0</AssemblyVersion>
<FileVersion>1.2.1.0</FileVersion>
```

### Bước 4 — Đồng bộ phiên bản chung

Mở:

```text
Directory.Build.props
```

Đổi thành:

```xml
<Version>1.2.1</Version>
<AssemblyVersion>1.2.1.0</AssemblyVersion>
<FileVersion>1.2.1.0</FileVersion>
```

### Bước 5 — Kiểm tra migration Supabase

Chỉ thực hiện nếu bản cập nhật có file migration mới trong:

```text
backend\supabase\migrations
```

Push migration lên project Supabase trước khi phát hành installer:

```powershell
powershell -ExecutionPolicy Bypass -File ".\backend\scripts\push-supabase-schema.ps1" -ProjectRef "MA_PROJECT_REF"
```

Không cần chạy bước này nếu update chỉ sửa frontend hoặc logic local và không thay đổi schema Supabase.

### Bước 6 — Tạo installer mới

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-release.ps1" -Version "1.2.1"
```

Kết quả:

```text
artifacts\installer\ExamTransfer-Setup-1.2.1.exe
```

### Bước 7 — Cài thử lên bản cũ

Chuẩn bị một máy đang cài `1.2.0`.

Không gỡ bản cũ.

Chạy:

```text
ExamTransfer-Setup-1.2.1.exe
```

Installer phải nhận lại loại cài đặt trước đó:

- Máy giáo viên vẫn chọn frontend + Local Server.
- Máy học sinh vẫn chỉ chọn frontend.

Kiểm tra dữ liệu cũ còn nguyên.

### Bước 8 — Phát hành

Chỉ gửi file mới sau khi cài đè thử thành công.

---

## 15. Người dùng cập nhật như thế nào

Hướng dẫn người dùng:

1. Không gỡ phần mềm cũ.
2. Không xóa `C:\ProgramData\ExamTransfer`.
3. Kết thúc mọi phòng thi đang chạy.
4. Đóng ExamTransfer.
5. Đóng ExamTransfer Local Server.
6. Tải installer phiên bản mới.
7. Chạy installer bằng quyền quản trị.
8. Giữ nguyên loại máy đã chọn trước đó.
9. Cài trực tiếp lên thư mục cũ.
10. Mở lại Local Server và ExamTransfer.

Inno Setup sử dụng cùng `AppId`, nên nhận đây là bản cập nhật của phần mềm hiện tại thay vì một sản phẩm mới.

---

## 16. Sao lưu trước khi cập nhật máy giáo viên

Không cập nhật khi đang có phòng thi hoạt động.

Cách tốt nhất:

1. Mở mục Backup trong phần mềm.
2. Tạo một bản backup mới.
3. Chờ trạng thái hoàn tất.
4. Đóng frontend và Local Server.
5. Chạy installer mới.

Nếu không dùng được chức năng Backup:

1. Đóng hoàn toàn Local Server.
2. Sao chép thư mục:

```text
C:\ProgramData\ExamTransfer
```

sang nơi an toàn, ví dụ:

```text
D:\ExamTransfer-Backup\pre-update-1.2.0-to-1.2.1
```

Không sao chép database trong khi Local Server vẫn đang ghi dữ liệu.

---

## 17. Database local khi cập nhật

Khi Local Server mới khởi động, dự án gọi bộ khởi tạo database:

```text
DbInitializer.InitializeAsync(...)
```

Bộ khởi tạo có nhiệm vụ:

- Tạo database nếu chưa có.
- Tạo bảng còn thiếu.
- Bổ sung thay đổi schema local đã được lập trình.
- Giữ dữ liệu hiện có.

Sau update phải kiểm tra:

```text
http://localhost:5048/health
```

và đăng nhập kiểm tra dữ liệu thật.

Nếu bản cập nhật có thay đổi database lớn, phải kiểm thử trên bản sao database cũ trước khi phát hành.

---

## 18. Những việc tuyệt đối không làm khi tạo update

Không thay đổi `AppId`.

Không thay đổi thư mục mặc định sang một thư mục mới theo phiên bản, ví dụ:

```text
C:\Program Files\ExamTransfer 1.2.1
```

Không đặt database trong:

```text
C:\Program Files\ExamTransfer
```

Không thêm lệnh xóa:

```text
C:\ProgramData\ExamTransfer
%LocalAppData%\ExamTransfer
```

Không yêu cầu người dùng gỡ bản cũ trước khi update.

Không phát hành update khi chưa thử cài đè trên bản cũ.

Không update khi phòng thi đang hoạt động.

Không dùng `-SkipTests` cho bản phát hành chính thức.

---

## 19. Nếu build thất bại

### Không tìm thấy .NET SDK

Kiểm tra:

```powershell
dotnet --list-sdks
```

Dự án cần .NET SDK 10 phù hợp với `global.json`.

### Không tìm thấy Inno Setup

Kiểm tra:

```powershell
Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
```

### Test backend lỗi

Chạy riêng:

```powershell
dotnet test ".\backend\ExamTransfer.sln" -c Release
```

Không bỏ test để ép tạo installer, trừ khi chỉ tạo bản tạm để kiểm tra nội bộ.

### Frontend verify lỗi

Chạy:

```powershell
powershell -ExecutionPolicy Bypass -File ".\frontend\scripts\verify-frontend.ps1"
```

### Installer không ghi đè bản cũ

Kiểm tra hai file `.iss` có dùng cùng:

```ini
AppId={{724D43BD-E4C5-4927-A3CF-8AC292F03D21}
```

Kiểm tra bản cũ và bản mới đều được cài ở chế độ admin, x64.

---

## 20. Checklist phát hành

### Trước build

- [ ] Đúng thư mục mã nguồn mới nhất.
- [ ] Không còn lỗi build.
- [ ] Backend test pass.
- [ ] Frontend verify pass.
- [ ] Phiên bản frontend đã tăng.
- [ ] Phiên bản backend đã tăng.
- [ ] Phiên bản chung đã tăng.
- [ ] Supabase migration đã push nếu có.

### Sau build

- [ ] Có `ExamTransfer-Setup-x.y.z.exe`.
- [ ] Có file SHA-256.
- [ ] Cài mới trên máy sạch thành công.
- [ ] Cài đè trên bản cũ thành công.
- [ ] Database cũ còn nguyên.
- [ ] File đề và bài nộp còn nguyên.
- [ ] Cấu hình Local Server còn nguyên.
- [ ] Máy học sinh vẫn kết nối được.
- [ ] Firewall TCP 5048 hoạt động.
- [ ] UDP 5050 hoạt động.
- [ ] `/health` trả về thành công.

---

## 21. Lệnh sử dụng thường xuyên

### Build lần đầu

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-release.ps1" -Version "1.2.0"
```

### Build bản vá

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-release.ps1" -Version "1.2.1"
```

### Build phiên bản nâng cấp chức năng

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-release.ps1" -Version "1.3.0"
```

### Chỉ build thử nội bộ, bỏ qua test

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-release.ps1" -Version "1.3.0" -SkipTests
```

Không dùng lệnh cuối cho bản gửi người dùng.

---

## 22. Tóm tắt quy trình update

```text
Sửa mã nguồn
→ tăng phiên bản frontend/backend
→ push migration Supabase nếu có
→ chạy build-release.ps1
→ nhận installer mới
→ thử cài mới
→ thử cài đè lên phiên bản cũ
→ kiểm tra database và file cũ
→ phát hành installer
→ người dùng chạy installer mới, không gỡ bản cũ
```

Phương án này chưa có chức năng tự tải update trong ứng dụng. Người dùng vẫn phải tải installer mới, nhưng không cần xóa hoặc gỡ phần mềm cũ.
