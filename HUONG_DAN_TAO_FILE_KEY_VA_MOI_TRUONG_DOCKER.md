# HƯỚNG DẪN TẠO FILE KEY VÀ MÔI TRƯỜNG DOCKER CHO EXAMTRANSFER

## 1. Vì sao Docker cần file cấu hình riêng?

Backend chạy native trên Windows hiện có thể đọc cấu hình tại:

```text
C:\ProgramData\ExamTransfer\config\runtime-settings.json
```

Backend trong Docker là một tiến trình Linux tách biệt, nên không tự nhìn thấy file trên Windows đó. Dự án sử dụng file:

```text
.env.docker
```

để truyền cấu hình vào container bằng .NET environment variables.

File `.env.docker` chứa thông tin cấu hình và có thể chứa secret, vì vậy file này đã được thêm vào `.gitignore` và không được commit lên GitHub.

Repository chỉ lưu file mẫu:

```text
.env.docker.example
```

## 2. Các thông tin cần chuẩn bị

### Bắt buộc khi dùng Supabase Cloud

1. **Supabase Project URL**

Ví dụ:

```text
https://abcdefghijk.supabase.co
```

2. **Supabase Publishable Key**

Dùng publishable key của project. Đây là key được backend dùng trong chế độ phiên người dùng.

3. **ExamTransfer Organization ID**

Đây là GUID tổ chức của dữ liệu ExamTransfer, ví dụ:

```text
11111111-2222-3333-4444-555555555555
```

Không nhầm Organization ID này với Supabase organization slug hoặc Supabase project reference.

### Chỉ dùng trên máy backend tin cậy

- `EXAMTRANSFER_SUPABASE_SECRET_KEY`
- `EXAMTRANSFER_SUPABASE_SERVICE_KEY` cũ nếu project vẫn dùng legacy service-role key.

Không đưa secret key cho máy học sinh, frontend-only tester hoặc người không có quyền quản trị dữ liệu.

## 3. Chế độ nên dùng

### UserSession — khuyến nghị cho test thông thường

```text
Cloud__AccessMode=UserSession
```

- Không yêu cầu secret/service-role key.
- Đăng nhập Supabase theo tài khoản người dùng.
- Giảm nguy cơ lộ quyền quản trị database.

### TrustedServer — chỉ dành cho backend quản trị tin cậy

```text
Cloud__AccessMode=TrustedServer
```

- Yêu cầu secret key hoặc service-role key.
- Chỉ dùng trên máy giáo viên/server do bạn quản lý.
- Không gửi `.env.docker` của máy này cho thành viên khác.

## 4. Cách tạo tự động — khuyến nghị

Từ thư mục chứa `compose.yaml`, chạy:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\setup-docker-environment.ps1"
```

Script sẽ hỏi:

- Supabase Project URL.
- Supabase Publishable Key.
- ExamTransfer Organization ID.

Script tự động sinh:

- `Security__TokenSigningKey`.
- `Security__ReceiptSigningKey`.

Hai khóa này phải giữ ổn định giữa các lần tạo lại container để token và biên nhận hoạt động nhất quán.

### Tạo bằng tham số

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\setup-docker-environment.ps1" `
  -SupabaseUrl "https://YOUR_PROJECT_REF.supabase.co" `
  -PublishableKey "YOUR_PUBLISHABLE_KEY" `
  -OrganizationId "YOUR-ORGANIZATION-GUID" `
  -AccessMode UserSession `
  -Environment Development
```

Lưu ý: không nên truyền secret key trực tiếp trên command line vì nó có thể nằm trong PowerShell history. Với `TrustedServer`, chạy script tương tác để secret được nhập ở chế độ ẩn.

### Ghi đè file cũ

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\setup-docker-environment.ps1" -Force
```

Script sẽ backup file cũ trước khi thay thế. Tuy nhiên, việc sinh khóa ký mới có thể làm token/receipt cũ không còn xác thực được. Chỉ dùng `-Force` khi bạn hiểu tác động hoặc đã sao lưu khóa cũ.

## 5. Cấu trúc file `.env.docker`

Các trường chính:

```env
ASPNETCORE_ENVIRONMENT=Development

Server__Port=5048
Server__UseHttps=false
Server__PreferredIp=

Discovery__Enabled=true
Discovery__Port=5050

Storage__RootPath=/data/ExamTransfer
Storage__MinFreeBytes=1073741824

Security__TokenSigningKey=BASE64_RANDOM_KEY
Security__ReceiptSigningKey=BASE64_RANDOM_KEY

Cloud__Enabled=true
Cloud__Environment=Development
Cloud__AccessMode=UserSession
Cloud__SupabaseUrl=https://YOUR_PROJECT_REF.supabase.co
Cloud__PublishableKey=YOUR_PUBLISHABLE_KEY
Cloud__OrganizationId=YOUR_ORGANIZATION_GUID

EXAMTRANSFER_SUPABASE_SECRET_KEY=
EXAMTRANSFER_SUPABASE_SERVICE_KEY=
```

.NET chuyển dấu `__` thành dấu `:`. Ví dụ:

```text
Cloud__SupabaseUrl
```

được backend đọc như:

```text
Cloud:SupabaseUrl
```

## 6. Thiết lập LAN

Backend trong container có IP nội bộ riêng. Không dùng IP container để quảng bá cho máy học sinh.

Trên máy giáo viên, chạy:

```powershell
ipconfig
```

Lấy IPv4 của Wi-Fi/Ethernet, ví dụ `192.168.1.10`, rồi sửa:

```env
Server__PreferredIp=192.168.1.10
```

Tạo lại container:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-start-backend.ps1" -Recreate
```

Mở firewall bằng PowerShell Administrator:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\setup-docker-firewall.ps1"
```

Máy học sinh có thể kết nối trực tiếp:

```text
http://192.168.1.10:5048
```

## 7. Kiểm tra cấu hình đã vào container chưa

Không in toàn bộ environment vì có thể làm lộ key. Chỉ kiểm tra các trường không bí mật:

```powershell
docker compose exec backend printenv ASPNETCORE_ENVIRONMENT
docker compose exec backend printenv Server__Port
docker compose exec backend printenv Storage__RootPath
docker compose exec backend printenv Cloud__Enabled
```

Kiểm tra health:

```powershell
Invoke-RestMethod "http://localhost:5048/health"
```

Kiểm tra log:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-logs-backend.ps1" -Tail 200
```

## 8. Quy tắc bảo mật bắt buộc

Không commit các file sau:

```text
.env
.env.docker
.env.production
*.env.local
```

Không đưa key vào:

- `Dockerfile`.
- `compose.yaml`.
- source code C#.
- ảnh chụp màn hình gửi công khai.
- issue công khai trên GitHub.

Chỉ commit:

```text
.env.docker.example
```

Nếu secret từng bị commit, xóa file khỏi Git là chưa đủ. Phải thu hồi/rotate key trong Supabase.

## 9. Khi chuyển Supabase project A sang B

Cần cập nhật ít nhất:

```env
Cloud__SupabaseUrl=URL_PROJECT_B
Cloud__PublishableKey=PUBLISHABLE_KEY_PROJECT_B
Cloud__OrganizationId=ORGANIZATION_ID_TUONG_UNG
EXAMTRANSFER_SUPABASE_SECRET_KEY=SECRET_KEY_PROJECT_B
```

Sau đó:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-start-backend.ps1" -Recreate
```

Việc đổi `.env.docker` chỉ đổi nơi backend kết nối. Schema/migrations và dữ liệu cần được chuẩn bị trên project B trước khi test luồng thật.
