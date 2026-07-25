# HƯỚNG DẪN CLONE VÀ SETUP DOCKER LẦN ĐẦU CHO EXAMTRANSFER

Tài liệu này dành cho thành viên vừa clone repository về máy Windows và muốn chạy **backend bằng Docker**, còn **frontend WPF vẫn chạy trực tiếp trên Windows**.

## 1. Mô hình chạy sau khi tích hợp Docker

```text
Frontend WPF trên Windows
        |
        | http://localhost:5048
        v
Backend ASP.NET Core trong Docker
        |
        +-- SQLite và file cục bộ trong Docker named volume
        +-- Supabase Cloud qua Internet
```

Docker hóa các phần sau:

- Backend `ExamTransfer.LocalServer`.
- .NET 10 SDK dùng để restore/build.
- ASP.NET Core Runtime dùng để chạy.
- Backend test runner.
- SQLite, file bài thi, bài nộp, backup và khóa Data Protection được giữ bằng Docker named volume.

Không Docker hóa frontend WPF.

## 2. Yêu cầu trên máy tester

- Windows 10/11 64-bit.
- Docker Desktop đã cài, đang dùng Linux containers và WSL 2 backend.
- Git đã cài.
- Có Internet trong lần build đầu để tải .NET image và NuGet packages.
- Có Supabase Project URL, Publishable Key và ExamTransfer Organization ID.

Không bắt buộc cài .NET SDK để chạy backend bằng Docker. Nếu build frontend từ source thì vẫn cần .NET 10/Visual Studio có WPF workload.

## 3. Kiểm tra Docker đúng cách

Lệnh đúng là:

```powershell
docker --version
```

Không phải:

```powershell
docker --vesion
```

Tiếp tục kiểm tra:

```powershell
docker compose version
docker info
wsl --version
wsl -l -v
```

Nếu PowerShell báo không nhận ra `docker`, chạy từ thư mục gốc dự án:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-doctor.ps1" -FixUserPath -StartDockerDesktop
```

Sau khi script sửa PATH, hãy đóng PowerShell và mở lại nếu script yêu cầu.

## 4. Clone repository lần đầu

```powershell
cd "D:\git"
git clone https://github.com/Hoangsonn05/Phan-Mem-Thu-Nop-Bai-Kiem-Tra.git
cd ".\Phan-Mem-Thu-Nop-Bai-Kiem-Tra\ExamTransfer_FullStack_v1.0.0_Source\ExamTransfer_Product_v1.0.0_FullStack"
```

Đảm bảo thư mục hiện tại có các file:

```text
compose.yaml
.env.docker.example
backend\Dockerfile
scripts\setup-docker-first-run.ps1
```

## 5. Cách setup tự động lần đầu

Từ thư mục chứa `compose.yaml`, chạy:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\setup-docker-first-run.ps1" -FixDockerPath -StartDockerDesktop
```

Script sẽ:

1. Kiểm tra Docker CLI, Docker Engine, Compose và WSL.
2. Tạo `.env.docker` nếu chưa tồn tại.
3. Hỏi Supabase URL, Publishable Key và Organization ID.
4. Tự sinh hai khóa ký bảo mật ổn định.
5. Build Docker image backend.
6. Khởi động container.
7. Kiểm tra `http://localhost:5048/health`.

Để mở firewall cho máy khác trong cùng LAN, mở PowerShell bằng **Run as administrator** rồi chạy:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\setup-docker-first-run.ps1" -ConfigureFirewall
```

Nếu chỉ test trên cùng một máy thì chưa cần mở firewall.

## 6. Chạy backend những lần sau

Khởi động bằng image đã build:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-start-backend.ps1"
```

Nếu vừa cập nhật source và cần build lại:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-start-backend.ps1" -Build
```

Hoặc build riêng:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-build-backend.ps1"
```

Kiểm tra trực tiếp:

```powershell
Invoke-RestMethod "http://localhost:5048/health"
```

Swagger:

```text
http://localhost:5048/swagger
```

## 7. Chạy frontend WPF

Backend Docker ánh xạ cổng ra Windows tại `http://localhost:5048`, đúng với địa chỉ mặc định của frontend.

Chạy frontend bằng cách hiện có:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\run-frontend.ps1"
```

Hoặc chạy file `.exe` đã publish.

## 8. Xem log, dừng backend và chạy test

Xem log gần nhất:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-logs-backend.ps1"
```

Theo dõi log liên tục:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-logs-backend.ps1" -Follow
```

Dừng backend:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-stop-backend.ps1"
```

`docker-stop-backend.ps1` chỉ dừng container và **không xóa dữ liệu**.

Chạy backend tests trong Docker:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-test-backend.ps1"
```

## 9. Cập nhật source sau này mà không clone lại

```powershell
git status
git pull
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-start-backend.ps1" -Build
```

Không cần cài lại Docker hoặc tạo lại `.env.docker` nếu cấu hình không thay đổi.

Khi `.env.docker.example` có trường mới, bổ sung trường đó vào `.env.docker` hoặc chạy lại script với `-Force` sau khi đã backup key cũ.

## 10. Dữ liệu nào được giữ lại

Hai named volume được tạo:

```text
examtransfer_data
examtransfer_runtime
```

Trong đó:

- `examtransfer_data`: SQLite, bài thi, bài nộp, temporary, exports và backup.
- `examtransfer_runtime`: Data Protection keys và runtime settings của backend Linux container.

Lệnh sau giữ nguyên volume:

```powershell
docker compose down
```

Cảnh báo: lệnh sau xóa container **và xóa toàn bộ dữ liệu local trong volume**:

```powershell
docker compose down -v
```

Không chạy `down -v` khi chưa backup.

## 11. Lưu ý kiểm thử LAN

Docker Desktop chạy container qua lớp mạng ảo. Cổng TCP `5048` và UDP `5050` đã được publish ra Windows, nhưng auto-discovery UDP có thể kém ổn định hơn chạy backend native.

Để máy học sinh kết nối ổn định:

1. Xác định IPv4 của máy giáo viên bằng `ipconfig`.
2. Đặt `Server__PreferredIp` trong `.env.docker`, ví dụ:

```env
Server__PreferredIp=192.168.1.10
```

3. Mở firewall TCP 5048 và UDP 5050 cho Private/LocalSubnet.
4. Nếu auto-discovery không thấy phòng, nhập trực tiếp địa chỉ:

```text
http://192.168.1.10:5048
```

5. Sau khi sửa `.env.docker`, tạo lại container:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-start-backend.ps1" -Recreate
```

## 12. Các lỗi thường gặp

### `docker` is not recognized

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-doctor.ps1" -FixUserPath
```

Sau đó đóng và mở lại PowerShell.

### Docker CLI có nhưng `docker info` lỗi

- Mở Docker Desktop.
- Chờ trạng thái Engine running.
- Chạy `wsl --update` trong PowerShell Administrator.
- Kiểm tra Docker Desktop > Settings > General > Use WSL 2 based engine.
- Kiểm tra Virtualization đã bật trong Task Manager > Performance > CPU.

### Cổng 5048 đang bị chiếm

```powershell
Get-NetTCPConnection -LocalPort 5048 -ErrorAction SilentlyContinue
```

Dừng backend native hoặc ứng dụng đang giữ cổng trước khi chạy container.

### `.env.docker` chưa tồn tại

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\setup-docker-environment.ps1"
```

### Backend không healthy

```powershell
docker compose ps
powershell -ExecutionPolicy Bypass -File ".\scripts\docker-logs-backend.ps1" -Tail 300
```
