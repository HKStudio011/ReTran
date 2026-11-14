# Thiết kế: Quản lý profile cache dịch theo cửa sổ (Profiles)

- Ngày: 2026-11-14
- Trạng thái: **đề xuất — user yêu cầu "làm luôn, review một lần"** (2026-11-14)
- Ngữ cảnh: DB cache dịch đã key theo `(Context, Text, Src, Dst)` với context = `screen:N` / `<process>.exe` / `unknown` (spec M4 + `ContextKey`). User cần xem/sửa/xoá cache theo từng "profile" cửa sổ, kèm import/export.

## 1. Mục tiêu / Non-goals

**Mục tiêu:** một sheet "Profiles" trong app để: liệt kê các context (cửa sổ/màn hình) kèm số entry; xem danh sách entry của một context; sửa bản dịch từng entry; xoá entry / xoá cả context; export JSON (một context hoặc tất cả); import JSON (upsert). Chạy được cả khi pipeline đang chạy.

**Non-goals:** đổi tên context; cấu hình per-window khác (allowlist…); đồng bộ cloud; phân trang phức tạp (dùng tìm kiếm LIKE + giới hạn hiển thị); dọn tự động entry mồ côi.

## 2. Quyết định chính

| # | Quyết định | Ghi chú |
|---|-----------|---------|
| D1 | Service mới `ProfileStore` (App/Services, dùng chung `RetranDbContext` như `TranslationCache`): `GetContextsAsync() -> [{Context, Count, Srcs, Dsts}]`; `GetEntriesAsync(context, search?, limit?)`; `UpdateEntryAsync(context, text, src, dst, translated)`; `DeleteEntryAsync(context, text, src, dst)`; `DeleteContextAsync(context)`; `ExportAsync(Stream, context?)`; `ImportAsync(Stream, ct) -> {Added, Updated, Skipped}`. | *(my call)* Tách khỏi `TranslationCache` (đường nóng của pipeline giữ nguyên). |
| D2 | Định dạng export/import JSON: `{ "version": 1, "exportedAtUtc": "...", "entries": [ { "context", "src", "dst", "text", "translated" } ] }`. Import: upsert theo unique key như `PutAsync`; entry thiếu/sai field → bỏ + đếm `Skipped`; JSON hỏng → `FormatException` cho UI báo đỏ. | *(my call)* |
| D3 | Export ghi vào `%LOCALAPPDATA%\ReTran\exports\profiles-<slug|all>-<yyyyMMdd-HHmmss>.json` + nút "Mở thư mục" (`explorer.exe /select,`). Import chọn file qua `Microsoft.Maui.Storage.FilePicker` (không thêm dependency). | *(my call — tránh tự dựng save-dialog native)* |
| D4 | UI: sheet `ProfilesPanel.razor` mở từ Home (cạnh nút Settings). Trái: danh sách context (số entry, huy hiệu `in-use` nếu trùng nguồn đang chạy — đọc từ `PipelineController.ActiveSource` nếu tiện, không thì bỏ). Phải: bảng entry (text, translated — sửa inline + Lưu; nút Xoá), ô tìm kiếm, nút Xoá context / Export (context) / Import / Export all. Xác nhận xoá bằng nút 2 bước (bấm → đổi chữ "Chắc chắn?"). | *(my call)* |
| D5 | Mọi thao tác qua EF tuần tự trên cùng DbContext scoped của app; không transaction dài; sửa khi pipeline chạy là hợp lệ (SQLite WAL mặc định của EF provider — serialized writes). | *(my call)* |
| D6 | i18n đầy đủ vi/en (block `Vi` + `En` — test `EveryKey_Exists_In_Both_Languages` giữ). | *(repo convention)* |

## 3. Thay đổi code theo file

### App (`ReTran.App`)
- **Mới** `Services/ProfileStore.cs` (+ record DTO `ProfileContextInfo`, `ProfileEntryInfo`, `ProfileImportResult`).
- **Mới** `Components/Shared/ProfilesPanel.razor` + entry point ở `Home.razor` (nút mở sheet).
- `Services/I18nStrings.cs`: keys `profiles.*` (vi + en).
- `MauiProgram.cs`: DI `ProfileStore`.
- Không đụng `TranslationCache`, `PipelineController` handlers, wire Core.

### Tests (`ReTran.Tests`)
- **Mới** `ProfileStoreTests.cs` (temp SQLite như `TranslationCacheTests`): list contexts (counts/nguồn), update/delete entry, delete context, export→import round-trip (Added/Updated đúng), import bỏ row hỏng (Skipped), import JSON hỏng → FormatException, import upsert không tạo trùng key.
- i18n: test sẵn có tự bắt key thiếu.

## 4. Kế hoạch test chính

Bảng trên + smoke tay (deferred cho user): mở sheet, sửa 1 dòng → text trên overlay đổi ở chu kỳ sau (nếu context đang chạy — lưu ý session cache RAM không tự nạp lại; chỉ DB đổi) — ghi rõ hành vi này trong hint UI: *"Sửa/xoá chỉ áp dụng cho cache đĩa; phiên đang chạy giữ bản dịch trong RAM tới khi restart pipeline"*.

## 5. Hiệu năng

Thao tác thủ công, không nằm đường nóng; query có index sẵn `(Context, Text, Src, Dst)`; limit mặc định 200 dòng/truy vấn + search LIKE prefix.

## 6. Rủi ro / giới hạn

- Sửa cache không đổi bản dịch đang hiển thị trong phiên (session RAM) — có hint UI.
- Export all với DB lớn: stream qua `Utf8JsonWriter` từng entry (không load hết vào RAM).
- Context `unknown` gom nhiều nguồn — chấp nhận.

## 7. Ngoài phạm vi

Đổi tên/notes cho profile; sắp xếp nâng cao; xoá entry mồ côi tự động; import merge chiến lược khác (chỉ upsert).
