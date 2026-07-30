# ExamTransfer Design System v1.0

## 1. Định hướng

Phong cách: **calm enterprise productivity** dành cho môi trường giáo dục và vận hành phòng thi. Giao diện ưu tiên độ tin cậy, mật độ thông tin vừa phải, trạng thái rõ và thao tác nhanh hơn hiệu ứng trang trí.

## 2. Color tokens

### Light

| Token | Giá trị | Mục đích |
|---|---:|---|
| App background | `#F4F7FB` | Nền ứng dụng |
| Surface | `#FFFFFF` | Card, panel |
| Text primary | `#0F172A` | Tiêu đề, dữ liệu chính |
| Text secondary | `#475569` | Nội dung phụ |
| Primary | `#2563EB` | CTA, active navigation |
| Accent | `#0891B2` | Thông tin truyền file/realtime |
| Success | `#15803D` | Hoàn tất, online, hợp lệ |
| Warning | `#B45309` | Chờ xử lý, nộp muộn |
| Danger | `#B91C1C` | Lỗi, hành động nguy hiểm |

Dark theme dùng palette riêng, không đảo màu cơ học. Các brush được khai báo trong `Themes/Palette.*.xaml`.

## 3. Typography

- Font hệ thống: Segoe UI.
- Icon: Segoe Fluent Icons, không dùng emoji làm icon cấu trúc.
- Type scale: 12 / 14 / 15 / 18 / 24 / 30 / 42.
- Đồng hồ và mã kỹ thuật: Consolas để giữ độ rộng chữ số ổn định.

## 4. Spacing & shape

- Nhịp 4/8 px.
- Khoảng cách chính: 8, 12, 16, 20, 24, 32.
- Corner radius: 9 cho control, 12–14 cho card, 18–22 cho hero.
- Border 1 px thay cho shadow trên phần lớn card để giảm tải render.

## 5. Motion

- Entrance duration: 180 ms.
- Chỉ animate `Opacity` và `TranslateTransform.Y`.
- Mỗi trang chỉ chạy một lần khi load.
- Tự bỏ animation khi `SystemParameters.ClientAreaAnimation` bị tắt.
- Không animation theo từng hàng DataGrid và không animation width/height.

## 6. Performance

- Không thêm ảnh nền, custom font hoặc UI package nặng.
- DataGrid và ListBox bật recycling virtualization.
- ViewModel có timer phải implement `IDisposable`; shell dispose khi đổi trang.
- Theme đổi bằng ResourceDictionary, không tạo lại toàn bộ cửa sổ.
- Không dùng blur trên shell; shadow chỉ dành cho một số hero quan trọng.

## 7. Accessibility

- Text/body hướng tới contrast tối thiểu 4.5:1.
- Trạng thái gồm icon/text/pill, không chỉ dựa vào màu.
- Button tối thiểu khoảng 42 px, focus border rõ.
- Native keyboard navigation được giữ nguyên.
- Tooltip cho nút icon-only.

## 8. Component chính

- `Card`, `SubtleCard`, `StatusPill`.
- `PrimaryButton`, `SecondaryButton`, `GhostButton`, `DangerButton`.
- Styled `TextBox`, `ComboBox`, `DataGrid`, `ProgressBar`.
- `ToneBrushConverter` cho semantic status.
- `EntranceAnimation` cho chuyển trang nhẹ.

## 9. Quy tắc tiếp tục phát triển

1. Không thêm hex màu trực tiếp vào View trừ hero/illustration đặc biệt.
2. DTO và trạng thái lấy từ Shared.Contracts; UI không tự kết luận nghiệp vụ.
3. Hành động nguy hiểm phải có confirm dialog khi nối logic thật.
4. Danh sách trên 50 mục phải virtualize.
5. Chỉ thêm animation khi nó giải thích thay đổi trạng thái.
