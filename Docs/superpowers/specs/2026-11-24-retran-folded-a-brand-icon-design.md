# ReTran — thiết kế icon A dải gấp mềm

Ngày: 2026-11-24
Trạng thái: Đã duyệt

## Mục đích

Thay icon `.NET` mặc định, chữ `R` trên tray/chip menu nổi và dấu chấm trước tên ReTran bằng monogram đã chọn: **N — A dải gấp mềm**. Bốn vị trí phải cùng một nhận diện nhưng được tối giản theo kích thước hiển thị.

## Phạm vi

1. **MAUI app icon:** nền Ink `#07201E`; dải A mềm Ivory `#EFEAD9` với mặt gấp xám và nhấn Amber `#FFB020`; giữ vòng cung và ba mốc giờ của phương án N.
2. **Tray icon 32×32:** cùng monogram trên nền bo góc; bỏ ba mốc giờ để tránh nhiễu pixel; giữ vòng cung Amber và dải A mềm.
3. **Topbar:** SVG khoảng 22×22 ngay trước chữ `ReTran`, thay dấu chấm 16×16 hiện tại. Trạng thái running vẫn do chip Live/Stopped bên phải thể hiện.
4. **Menu nổi thu gọn:** thay glyph `R` trong chip 44×44 bằng monogram rút gọn; giữ nguyên kích thước chip, nền, viền, shadow, vị trí và hit-test.

Không đổi splash screen, favicon, overlay OBS, màu trạng thái hoặc layout/hit-test ngoài phần thay hình ảnh brand.

## Các hướng đã cân nhắc

- **Dùng nguyên app icon ở mọi kích thước:** loại vì các mốc giờ và mặt gấp nhỏ sẽ nhòe ở 16–32 px.
- **Dùng font glyph cho tray/chip:** loại vì không tái hiện được monogram và phụ thuộc font hệ thống.
- **Dùng bitmap cho mọi bề mặt native:** loại vì cần thêm pipeline resource/scale; hiện tray và menu đã có renderer GDI+.
- **Hướng chọn:** SVG cho MAUI/topbar; một renderer GDI+ dùng chung cho tray và chip thu gọn. Bản native dùng cùng hệ tọa độ và màu với SVG nhưng lược bớt chi tiết.

## Thiết kế triển khai

- Cập nhật `Resources/AppIcon/appicon.svg`, `appiconfg.svg` và màu `MauiIcon` trong `ReTran.App.csproj`.
- Thêm renderer nội bộ cho brand mark trong App; `TrayIconSource` và `FloatingMenuRenderer.DrawChip` cùng gọi renderer này. Không thay đổi cách sở hữu/hủy `HICON` của tray.
- Thay markup dấu chấm trong `MainLayout.razor` bằng SVG inline có `aria-hidden`; xóa CSS dấu chấm/animation không còn dùng và thêm kích thước brand mark phù hợp.
- Giữ màu brand độc lập với `BoxAnnotationStyle.TickColor` để không làm đổi màu overlay và các thành phần không liên quan.
- Bổ sung/cập nhật test pixel cho tray và chip: phải có mực Ivory/Amber ở vùng trung tâm, không còn phụ thuộc glyph `R`; giữ các test lifecycle, layout và hit-test hiện có.
- Kiểm chứng bằng test liên quan, build frontend và build solution; tạo preview thực tế ở app-icon lớn, tray 32 px và chip 44 px để rà nếp gấp.

## Quyết định để người triển khai tự chọn

Tên/file chứa renderer nội bộ, ngưỡng pixel khi test khử răng cưa và cách tổ chức helper SVG; các quyết định này không được làm thay đổi hình N đã duyệt hoặc hành vi tương tác hiện có.
