# Source Validation — v1.0.0

## Kiểm tra tĩnh đã thực hiện

- Parse hợp lệ toàn bộ XAML/XML, project file, props và solution `.slnx`.
- Parse hợp lệ toàn bộ JSON.
- Đối chiếu `x:Class` với code-behind.
- Đối chiếu mọi `ProjectReference` với file đích.
- Đối chiếu toàn bộ `StaticResource` với resource key hiện có.
- Kiểm tra cân bằng delimiter/string/comment cơ bản cho toàn bộ C# source.
- Đối chiếu 22 route sidebar với ViewModel chuyên biệt và DataTemplate tương ứng.
- Đối chiếu 73 lời gọi REST/file transfer của Frontend với 97 route Controller Backend; không phát hiện endpoint lệch sau khi bỏ query string và chuẩn hóa route parameter.
- Kiểm tra `BackendClient` và `MockBackendClient` triển khai đủ `IBackendClient`.
- Không còn route production dùng `WorkflowPageView`, `FeaturePageView` hoặc `ListPageViewModel`.
- Không còn endpoint `/mock` trong ViewModel production.
- Không còn nhãn demo/beta/mock trong giao diện người dùng.
- Không còn đường dẫn máy phát triển, `bin`, `obj`, `.vs`, `TestResults`, database runtime hoặc log runtime.
- Sửa các chuỗi tiếng Việt bị lỗi encoding trong Backend auth/grading response.

## Thống kê source

- 184 file trước khi regenerate manifest.
- 104 file C#.
- 29 file XAML.
- 25 View XAML trong Frontend.
- 97 route Controller Backend.

## Giới hạn xác minh

Môi trường đóng gói không có .NET SDK và không chạy Windows, nên chưa thể chạy Roslyn build WPF hoặc smoke test cửa sổ thực tế trong phiên đóng gói này. Trước khi dùng làm binary release, chạy trên Windows:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\setup.ps1
.\scripts\verify.ps1
.\scripts\run-frontend.ps1 -UseMock $true
```

Kiểm tra real mode:

```powershell
.\scripts\run-backend.ps1
.\scripts\run-frontend.ps1 -UseMock $false -ApiUrl "http://localhost:5048"
```
