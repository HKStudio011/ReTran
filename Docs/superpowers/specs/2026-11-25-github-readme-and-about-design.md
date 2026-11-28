# ReTran — thiết kế README song ngữ và GitHub About

Ngày: 2026-11-25
Trạng thái: Đã duyệt

## Mục đích

Trình bày `HKStudio011/ReTran` cho người mới mở trang GitHub: biết app làm gì, máy cần gì để chạy và các chức năng chính mà không phải đọc tài liệu kỹ thuật. Lấy cấu trúc ngắn gọn của `HKStudio011/router-balancing` làm tham chiếu, không sao chép nội dung của project đó.

## Phạm vi

1. `README.md`: tiếng Anh.
2. `README.vn.md`: tiếng Việt.
3. `Docs/design/logo/retran-icon.svg`: bản logo độc lập, dùng đúng hình N đã duyệt để hiển thị ở đầu README; không thiết kế lại icon.
4. GitHub repository metadata: thêm mô tả ngắn bằng tiếng Anh và homepage `https://hkstudio011.github.io/`.
5. Cuối mỗi README có mục About với cùng liên kết HKStudio011.

## Nội dung mỗi README

- Liên kết chuyển ngôn ngữ, icon, tên ReTran và một câu giới thiệu ngắn.
- **Requirements:** Windows 10/11 64-bit; 2 GB trống cho bản CPU hoặc 8 GB cho bản GPU; Internet cho lần thiết lập đầu tiên; không cần cài riêng .NET/Python; NVIDIA là tùy chọn, driver từ 576.02 khi dùng GPU, các máy khác dùng bản CPU.
- **Features:** chụp màn hình/cửa sổ; nhận diện và dịch chữ realtime; hiển thị bản dịch trên màn hình và qua OBS Browser Source; menu nổi; cache/chỉ dịch nội dung mới; dịch online hoặc model cục bộ; giao diện English/Tiếng Việt; tự cập nhật.
- **About:** HKStudio011 — `https://hkstudio011.github.io/`.

## Không làm

- Không có hướng dẫn build/test, kiến trúc, giải thích code, cấu hình chi tiết, chỉ số hiệu năng hoặc liên kết tài liệu kỹ thuật.
- Không slogan, emoji, khẳng định quảng cáo hoặc badge kỹ thuật không cần thiết.
- Không mô tả virtual camera hoặc exclusive-fullscreen game hook vì các tính năng này không có trong sản phẩm hiện tại.
- Không sửa timing test, không merge/push nhánh icon trong task này. README chỉ xuất hiện trên GitHub sau khi nhánh được merge/push lên `master`; metadata About có thể được cập nhật trước sau khi nội dung được duyệt.

## Cách thực hiện đã chọn

Làm trên cùng worktree/nhánh icon để dùng lại asset N đã duyệt. Việc tách nhánh README riêng đã được cân nhắc nhưng không chọn vì user muốn gom chung một nhánh trước khi xử lý merge sau.

## Kiểm chứng

- Hai README có cùng cấu trúc và không thiếu phần nào ở một ngôn ngữ.
- Mọi yêu cầu/tính năng khớp sản phẩm hiện tại; SVG và liên kết ngôn ngữ hoạt động trên GitHub.
- Kiểm tra Markdown/link và xem trước bố cục; không cần chạy lại test .NET vì chỉ đổi tài liệu/asset.
- `gh repo view HKStudio011/ReTran --json description,homepageUrl` xác nhận metadata sau khi cập nhật.
