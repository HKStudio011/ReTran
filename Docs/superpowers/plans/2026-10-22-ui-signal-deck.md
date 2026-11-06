# Plan — Port mockup "signal deck" thành UI thật

- **Ngày:** 2026-10-22
- **Mockup nguồn:** `Docs/mockups/retran-ui-v2.html` (đã duyệt, chưa commit)
- **Phạm vi:** thay UI Blazor hiện tại (sidebar + card stack) bằng topbar + preview hero + transport + rail "Chuỗi tín hiệu", settings thành slide-over.
- **Không làm:** không đổi IPC/Core, không thêm số liệu giả, không đổi hành vi pipeline/overlay/camera.

## Nguyên tắc

1. **Giữ nguyên chức năng:** Nguồn (màn hình/cửa sổ) + refresh, Dịch sang, Bắt đầu/Dừng, overlay (bật/tắt, mode, cỡ chữ), camera ảo (tên + copy), theme/accent/ngôn ngữ, update, toast.
2. **Chỉ hiển thị dữ liệu thật:** fps đo từ `PreviewState.Changed`, box/dòng dịch từ `OverlayState.Latest.Boxes`, độ phân giải từ `PreviewFrame.W/H`, trạng thái từ `PipelineController`. Không port RAM/GPU/"hàng đợi OCR" giả của mockup.
3. **Không phụ thuộc mạng:** bundle Be Vietnam Pro (woff2, OFL) vào `wwwroot/fonts`; mono dùng `Cascadia Mono`/`Consolas`. Nếu tải font thất bại → fallback `Segoe UI Variable`.
4. **Không đụng class Tailwind cũ:** CSS mới đặt tiền tố `rt-` trong `deck.css`; token theme cũ (`--background/--surface/--primary/--secondary`) được cập nhật để các trang còn lại (UpdateModal, NotFound, Settings page) đồng bộ.
5. **Chưa commit** cho tới khi user xem bản chạy.

## Quyết định thiết kế

- **Settings:** một component dùng chung `SettingsPanel.razor`; MainLayout render nó trong slide-over (mở từ topbar). Route `/settings` vẫn giữ, render cùng panel (deep-link không gãy).
- **Theme/accent:** dùng `data-theme` sẵn có; thêm `data-accent` (`amber` mặc định, `jade`, `coral`). `theme-manager.ts` thêm `setAccent/toggleTheme`, giữ `data-color` cũ cho localStorage cũ (inert).
- **Nguồn:** segmented Màn hình/Cửa sổ + một select + nút refresh (thay hai select).
- **Settings drawer** chứa: provider/API key, ngôn ngữ nguồn/đích, giao diện (theme/accent/ngôn ngữ UI), update, nút Lưu.

## File map

| File | Việc |
| --- | --- |
| `ReTran.App/vite-project/src/css/deck.css` | **mới** — token `--rt-*`, topbar, stage, monitor, transport, rail, sheet, toast, responsive |
| `ReTran.App/vite-project/src/css/style.css` | import `deck.css` |
| `ReTran.App/vite-project/src/css/theme.css` | token nền/chữ/primary theo palette mới + `data-accent` |
| `ReTran.App/vite-project/src/ts/theme-manager.ts` | accent, toggleTheme, getThemeSetting, getAccent |
| `ReTran.App/vite-project/src/ts/main.ts` | export các hàm mới |
| `ReTran.App/Services/JsInteropService.cs` | `ToggleThemeAsync`, `GetThemeSettingAsync`, `SetAccentAsync`, `GetAccentAsync`, `CopyTextAsync` |
| `ReTran.App/wwwroot/fonts/` | **mới** — Be Vietnam Pro woff2 + notice license |
| `ReTran.App/Services/I18nStrings.cs` | key `deck.*` (VI/EN) |
| `ReTran.App/Components/Layout/MainLayout.razor` | topbar, statusline, settings sheet, toast, khởi tạo JS, bỏ sidebar |
| `ReTran.App/Components/Layout/NavMenu.razor` | **xóa** (không còn dùng) |
| `ReTran.App/Components/Shared/SettingsPanel.razor` | **mới** — form settings tách từ `Settings.razor` |
| `ReTran.App/Components/Pages/Settings.razor` | wrapper mỏng quanh `SettingsPanel` |
| `ReTran.App/Components/Pages/Home.razor` | viết lại markup control deck + telemetry thật |
| `Docs/mockups/retran-ui-v2.html` | sửa lỗi literal `` `n `` trong CSS (dòng 124, 285) và icon CTA |

## Task 1 — Design tokens + CSS

1. Viết `deck.css`:
   - Token `:root` (dark mặc định) + `[data-theme="light"]`: `--rt-page/--rt-panel/--rt-panel2/--rt-well/--rt-raised/--rt-line/--rt-line2/--rt-text/--rt-dim/--rt-faint/--rt-tally/--rt-tally-ink/--rt-tally-soft/--rt-data/--rt-danger`.
   - `[data-accent="jade"]`, `[data-accent="coral"]` đổi tally.
   - Layout: `.rt-app` (grid 3 hàng), `.rt-topbar` 56px, `.rt-stage` (1fr + 336px), `.rt-frame` 16:9, `.rt-transport`, `.rt-rail`, `.rt-monitor-bar`, `.rt-statusline`.
   - Thành phần: `.rt-btn` (+primary/ghost/mini), `.rt-icon-btn`, `.rt-chip`, `.rt-seg`, `.rt-select`, `.rt-switch`, `.rt-step`, `.rt-device`, `.rt-backdrop`, `.rt-sheet`, `.rt-toast`, `.rt-swatch`.
   - Responsive 1240/1040/760/520 như mockup; `prefers-reduced-motion`.
2. `theme.css`: light `--background:#E2E8E3; --surface:#07201E; --primary:#A85F00; --secondary:#3D5956`; dark `#0A191C / #EFEAD9 / #FFB020 / #94A9A5`; `data-accent` thay `data-color`.
3. Import `deck.css` sau `utility.css` trong `style.css`.
4. Font: tải Be Vietnam Pro variable (woff2) + `@font-face` `unicode-range` latin/vietnamese; nếu mạng lỗi ghi chú fallback.
5. **Verify:** `npm run build` trong `ReTran.App/vite-project` thành công; `wwwroot/build/assets/*.css` chứa `.rt-topbar`.

## Task 2 — JS bridge

1. `theme-manager.ts`:
   - `applyAccent(id)` set/remove `data-accent`; `setAccent` lưu `retran-accent`.
   - `getThemeSetting()`, `getAccent()` đọc localStorage (default `system`/`amber`).
   - `toggleTheme(): string` đọc `data-theme` đã resolve, gọi `setTheme(ngược lại)`, trả về theme mới.
   - `initTheme` gọi `applyAccent`.
2. `main.ts` export thêm `setAccent, getAccent, getThemeSetting, toggleTheme`.
3. `JsInteropService`: thêm các method wrapper + `CopyTextAsync(string)`.
4. **Verify:** build TS không lỗi; kiểm tra export trong `wwwroot/build/assets/main.js`.

## Task 3 — i18n

Thêm key `deck.*` VI/EN (giữ key cũ):
- khung: `deck.brand.sub`, `deck.chip.live/stopped/error`, `deck.theme.toggle`, `deck.settings.close`
- nguồn: `deck.source.screen/window`, `deck.source.refresh`, `deck.run.hint.idle/live`, `deck.preview.idleHint`
- monitor/rail: `deck.unit.fps/boxes/lines`, `deck.chain.title/capture/ocr/translate/camera`, `deck.cam.device/res/format`
- overlay: `deck.overlay.title/sub`, `deck.overlay.on/off`
- camera: `deck.camera.copy/copied/hold`
- status: `deck.status.ready/live/error`, `deck.status.os`
- settings: `deck.settings.title/section.provider/section.lang/section.look/section.update`, `deck.settings.theme/accent/uiLang`, `deck.settings.accent.amber/jade/coral`, `deck.settings.apiKeyHint`
- lỗi: `deck.error.start/stop`

**Verify:** mọi key `deck.*` có đủ ở cả `Vi` và `En` (script đối chiếu nhanh).

## Task 4 — Shell (MainLayout)

1. Bỏ sidebar; root `.rt-app` + `@Body`.
2. Topbar: brand mark/name/sub, chip live/idle/error, nút VI/EN, nút theme (2 SVG, CSS ẩn/hiện theo `data-theme`), nút Cài đặt, version.
3. Statusline: chấm trạng thái + text (`Sẵn sàng`/`Đang chạy`/`Lỗi core: …`), version, `Windows`.
4. Settings sheet: backdrop + aside, mở/đóng bằng state `_settingsOpen`; focus vào sheet khi mở; Escape đóng; click backdrop đóng; render `<SettingsPanel />`.
5. Toasts: dùng `.rt-toasts/.rt-toast`; giữ logic 4 s + severity.
6. Chuyển `Js.Initialize/NotifyWebViewReady/InitLangFromStorage` từ `Home.razor` sang `MainLayout.OnAfterRender` (topbar dùng JS được ở mọi trang).
7. Xóa `NavMenu.razor`.
8. **Verify:** build C#; mở app không lỗi; topbar đổi VI/EN, theme, mở/đóng sheet.

## Task 5 — SettingsPanel

1. Tách toàn bộ logic `Settings.razor` sang `Shared/SettingsPanel.razor` (provider, API key, src/dst, update, save/status).
2. Thêm section Giao diện: theme seg (light/dark/system), accent swatches (amber/jade/coral), UI language seg; đọc trạng thái hiện tại từ JS (`GetThemeSettingAsync`, `GetAccentAsync`) ở `OnAfterRender` đầu.
3. Theme/accent áp dụng ngay + persist; nút Lưu chỉ lưu provider/src/dst/apiKey.
4. `Settings.razor` route `/settings` render `<SettingsPanel />` trong khung trang.
5. **Verify:** lưu settings ghi file; reload app giữ provider/ngôn ngữ; theme/accent giữ sau reload.

## Task 6 — Home control deck

1. Markup: `.rt-stage` → monitor bar (Xem trước · W×H · fps · box · `SRC → DST`), frame (ảnh thật, corner, grain, vignette, scan khi live), idle slate có CTA; transport (seg nguồn + select + refresh, Dịch sang, nút Bắt đầu/Dừng + hint); rail (4 bước + overlay + camera).
2. Telemetry:
   - fps: đếm `Preview.Changed` trong cửa sổ 1 s, cập nhật mỗi giây khi chạy.
   - box/dòng dịch: subscribe `OverlayState.Changed`, đọc `Latest.Boxes` (`Count`, số box có `Translated` khác rỗng).
   - độ phân giải: `PreviewFrame.W/H` của khung mới nhất.
   - nguồn: tên monitor/cửa sổ đang chọn; kỹ thuật "DXGI" (screen) / "PrintWindow" (window).
   - camera: `CameraRegistrar.DeviceName`, `YUY2 1280×720`, trạng thái live/held.
3. Giữ nguyên logic start/stop, load sources, overlay service, quick dst, lỗi.
4. Nút copy camera gọi `Js.CopyTextAsync` + toast.
5. Idle: không có khung → slate; có khung cũ nhưng dừng → vẫn hiện khung cuối + chip "Đang dừng".
6. **Verify:** `dotnet build` sạch; chạy app, bấm Bắt đầu, thấy fps/box/dòng cập nhật; Dừng giữ khung; đổi nguồn/dst/overlay/camera copy hoạt động.

## Task 7 — Mockup typo

Sửa literal `` `n `` thành newline ở `.slate-start` và media query 520 px; thêm style icon play cho `.slate-start`. **Verify:** mở mockup, nút "Bắt đầu chụp" có icon và layout mobile không lỗi.

## Task 8 — Kiểm chứng tổng

1. `dotnet build "ReTran Project.slnx"` — 0 warning/error mới.
2. `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` — 311/311.
3. `uv run pytest tests -q` trong `ReTran.OCR/` — 19/19 (không đổi Python, chạy xác nhận).
4. Chạy app thật (không headless) và chụp ảnh các trạng thái: dark live, light, idle, settings sheet, cửa sổ hẹp; đối chiếu `Docs/mockups/shot-*.png`.
5. Ghi lại số đo fps/box thật vào báo cáo cuối (không ghi vào `performance.md` vì không phải benchmark).
6. Chưa commit; chờ user duyệt.

## Rủi ro / cần theo dõi

- **Be Vietnam Pro tải font:** nếu request Google Fonts bị chặn, dùng fallback Segoe UI (UI vẫn đúng layout, khác typography).
- **`data-accent` + localStorage cũ:** người dùng đã lưu `retran-color` sẽ thấy accent mặc định amber; không lỗi.
- **fps thật thấp hơn mockup:** do `captureFps` mặc định của Core; hiển thị đúng số đo, không làm tròn lên.
- **Không có bUnit:** UI verify bằng build + chạy app + ảnh chụp; không thêm test giả.
