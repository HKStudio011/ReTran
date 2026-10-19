# Spec: Game Overlay — cửa sổ overlay local đè lên game (always-on-top, click-through)

- **Ngày:** 2026-10-15
- **Trạng thái:** Draft — chờ user review
- **Phạm vi:** App only — subsystem mới `Services/OverlayWindow/`, UI Home.razor, i18n, settings. Không đụng Core, Python sidecar, OBS feed.
- **Nguồn:** Brainstorming session 2026-10-15 (pain: user chơi game trên một màn hình duy nhất — muốn vừa chơi, vừa thấy bản dịch, vừa stream; hiện tại chỉ người xem stream thấy bản dịch qua OBS Browser Source).

## 1. Bối cảnh

Pipeline capture → OCR → translate đã publish `OverlayState` (singleton, `Core/OverlayState.cs`). Consumer duy nhất hiện nay là `OverlayFeedServer` (HTTP + SSE, `http://127.0.0.1:17863/overlay`) cho OBS Browser Source — tức **người chơi không nhìn thấy bản dịch trong lúc chơi**, chỉ người xem stream thấy.

Ý tưởng ban đầu của user (virtual camera + virtual screen) đã được loại bỏ sau khi phân tích:

- **Virtual display** đòi hỏi Indirect Display Driver (kernel driver, phải ký/test-sign) — nặng, rủi ro, và không cần thiết vì window capture hiện có đã bắt được frame game.
- **Virtual camera** chỉ giải quyết "OBS nhận feed", không giải quyết vấn đề thật là người chơi không thấy bản dịch; để dành cho spec sau nếu cần.

Phương án chốt: **cửa sổ overlay trong suốt, always-on-top, click-through, neo đúng lên cửa sổ game** (chạy borderless/windowed). Về anti-cheat: đây chính là cơ chế mà Discord/Steam/NVIDIA overlay dùng — vẽ cửa sổ topmost không bị flag; OCR đọc bằng screen capture (không đọc bộ nhớ game, không inject, không hook). Không đụng Rust hook.

## 2. Quyết định đã chốt (brainstorming 2026-10-15)

| # | Câu hỏi | Lựa chọn |
|---|---|---|
| 1 | Chế độ hiển thị | **Cả hai**: phủ lên chữ gốc (subtitle) + bảng góc màn hình (panel); chuyển được trong UI app. |
| 2 | Công nghệ cửa sổ overlay | **Win32 layered window thuần** (`WS_EX_LAYERED` + `UpdateLayeredWindow`, per-pixel alpha) + GDI+ render text. Không dùng WebView2/WPF — tránh hẳn bài toán "webview trong suốt" (phần rủi ro nhất của ý tưởng). |
| 3 | Nguồn dữ liệu | **In-process** qua `OverlayState.Changed` — không cần HTTP cho overlay local; OBS feed giữ nguyên. |
| 4 | Neo vị trí | Theo nguồn pipeline: window → poll rect HWND; screen → rect monitor. Không drag thủ công. |
| 5 | Exclusive fullscreen | **Không hỗ trợ** (cửa sổ ngoài không vẽ đè được) — UI ghi chú hướng dẫn chạy borderless. Không hook/injection. |
| 6 | Virtual camera / virtual display | Non-goal, spec riêng sau này nếu cần. |

## 3. Kiến trúc & luồng dữ liệu

```
Core "frame.translated" → PipelineController.HandleFrameTranslated → OverlayState.Publish(frame)
   ├─→ OverlayFeedServer (SSE) → OBS Browser Source        [KHÔNG ĐỘNG]
   └─→ OverlayWindowService (event Changed)
           │ throttle ≤ 15 fps + marshal UI thread
           ▼
         Win32OverlayHost.Draw(rects, text) → UpdateLayeredWindow

Neo vị trí: PipelineController.ActiveSource (mới, §7)
   window  → GetWindowRect(hwnd) @ ~250 ms; IsWindow()/IsIconic() → ẩn/hiện
   screen  → GetMonitorInfo(index monitor) — tĩnh, tra lại khi đổi nguồn
```

### File mới/sửa

```
ReTran.App/Services/OverlayWindow/          (mới)
├── IOverlayHost.cs                          # interface thao tác Win32 window (testable seam)
├── Win32OverlayHost.cs                      # P/Invoke: CreateWindowEx, UpdateLayeredWindow,
│                                              #   SetWindowPos, GetWindowRect, IsWindow, IsIconic,
│                                              #   GetMonitorInfo, DestroyWindow + GDI+ draw
├── OverlayLayout.cs                         # PURE: (frame, options) → danh sách vùng vẽ. Không Win32/IO.
└── OverlayWindowService.cs                  # orchestration: subscribe state, theo dõi anchor,
                                              #   throttle redraw, toggle on/off, lifecycle

ReTran.App/Core/PipelineController.cs        # + ActiveSource (§7)
ReTran.App/Services/AppSettingsService.cs    # AppSettings + 4 field overlay (§8)
ReTran.App/Components/Pages/Home.razor       # Card "Overlay trên game" (§9)
ReTran.App/Services/I18nStrings.cs           # keys overlay.* VI+EN
ReTran.App/MauiProgram.cs                    # DI: IOverlayHost → Win32OverlayHost, OverlayWindowService
```

## 4. OverlayWindowService

- Singleton DI (cạnh `OverlayFeedServer` trong `MauiProgram.cs`).
- Đọc cài đặt overlay từ `AppSettingsService.Load()` mỗi khi toggle/đổi option (§8).
- Subscribe:
  - `OverlayState.Changed` → đánh dấu "có frame mới" → redraw nếu qua throttle (≤ 15 fps — budget UI/overlay).
  - `PipelineController.PipelineStateChanged` → pipeline dừng/core chết → ẩn cửa sổ (không destroy, để bật lại nhanh).
- **Anchor theo dõi:** timer ~250 ms đọc rect của nguồn đang active:
  - window: `GetWindowRect(hwnd)`; `!IsWindow` hoặc `IsIconic` → ẩn overlay.
  - screen: rect monitor tương ứng (từ `ActiveSource.Monitor`). Map index → `HMONITOR` qua `EnumDisplayMonitors` theo thứ tự; nếu thứ tự lệch với Core thì match theo tên trong `MonitorInfo.Name`.
  - Rect thay đổi → `SetWindowPos` overlay khớp đúng (position + size, pixel vật lý).
- Toggle: `SetEnabled(bool)` tạo/destroy HWND; gọi từ UI qua dispatcher UI thread. Mọi thao tác Win32 chạy trên **UI thread** (MAUI dispatcher) — event `Changed` nổ trên read-loop thread nên phải marshal trước khi `UpdateLayeredWindow`.
- Lifecycle: implement `IDisposable` — destroy HWND khi dispose (app quit / stop-services), không để cửa sổ mồ côi.

## 5. Win32OverlayHost

- `CreateWindowEx(WS_POPUP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)` — click-through (không bắt chuột, không tranh focus), luôn trên cùng, không hiện taskbar/Alt-Tab.
- Render: bitmap 32-bit BGRA (per-pixel alpha) đúng kích thước client → `UpdateLayeredWindow`. Kỹ thuật chuẩn cho overlay trong suốt; window tự nó không draw gì khác.
- GDI+ vẽ theo danh sách vùng của `OverlayLayout`:
  - **subtitle:** mỗi box — rounded rect nền `rgba(0,0,0,0.45)`, viền 3 px `#22c55e` (đồng nhất với overlay OBS hiện có), chữ trắng word-wrap trong width box; cỡ font tỷ lệ theo chiều cao khung hình × FontScale.
  - **panel:** cột các dòng dịch docked vào góc đã chọn; mỗi dòng một rounded rect như trên; tối đa ~10 dòng; bỏ dòng rỗng và trùng liên tiếp.
- `IOverlayHost` là seam cho test (fake host ghi lại lệnh); `Win32OverlayHost` là implementation thật — P/Invoke không unit-test, chỉ acceptance tay.

## 6. OverlayLayout (pure function)

```csharp
record DrawBox(double X, double Y, double W, double H, string Text, bool IsPanelLine);
IReadOnlyList<DrawBox> Render(OverlayFrame frame, OverlayViewOptions opts);
// OverlayViewOptions: Mode(subtitle|panel), Corner(tl|tr|bl|br), FontScale, PanelMaxLines
```

- **subtitle:** toạ độ box = (x,y,w,h) × kích thước cửa sổ overlay; clamp trong viewport; text = `Translated`.
- **panel:** xếp các dòng theo góc; width panel ≈ 40% width cửa sổ (hoặc width nội dung lớn nhất, lấy min); thứ tự giữ theo frame.
- Không phụ thuộc Win32/IO → unit-test đầy đủ bằng xUnit.

## 7. PipelineController — `ActiveSource`

```csharp
public SourceSelection? ActiveSource { get; private set; }   // set trong StartAsync, null khi StopAsync / core-exited
```

- Gán sau `core.start` thành công; dọn về `null` ở `StopAsync` và `HandleCoreExited`.
- `OverlayWindowService` đọc `ActiveSource` để biết neo vào HWND hay monitor.

## 8. Settings

Mở rộng record (đặt default cho mọi field mới — file `settings.json` cũ vẫn deserialize được):

```csharp
public sealed record AppSettings(
    string Provider, string Src, string Dst, string? ApiKey,
    bool OverlayOnGame = false,
    string OverlayMode = "subtitle",      // "subtitle" | "panel"
    string OverlayCorner = "bl",          // "tl" | "tr" | "bl" | "br" (chỉ panel)
    double OverlayFontScale = 1.0);       // 0.85 | 1.0 | 1.25
```

Lưu ý: `AppSettingsService.Load()` trả về `Default` khi field bắt buộc rỗng — các field mới có default nên không ảnh hưởng; test xác nhận (§10). Ghi settings dùng pattern hiện có (`Settings.Save(Settings.Load() with { ... })`, ghi ngay như `SaveQuickDst`).

## 9. UI (Home.razor) + i18n

Card mới **"Overlay trên game"**, đặt sau card Pipeline:

| Điều khiển | Hành vi |
|---|---|
| Toggle "Hiện overlay lên nguồn đang capture" | `OverlayWindowService.SetEnabled`; **disabled khi không có `ActiveSource`** (pipeline chưa start) |
| Chọn chế độ | "Phủ lên chữ gốc" / "Bảng góc màn hình" → reload layout, ghi settings |
| Chọn góc (TL/TR/BL/BR) | Chỉ hiện ở chế độ panel; mặc định BL |
| Cỡ chữ | Nhỏ / Trung bình / Lớn (0.85 / 1.0 / 1.25) |
| Ghi chú | "Game exclusive fullscreen không thể hiện overlay — chạy borderless windowed để dùng tính năng này." |

i18n: keys `overlay.*` (title, toggle, mode.subtitle, mode.panel, corner.*, font.*, note.fullscreen) vào `I18nStrings.cs`, VI + EN theo pattern key dạng chấm hiện có; component subscribe `L.Changed`.

## 10. Testing

| File test | Nội dung |
|---|---|
| `Overlay/OverlayLayoutTests` (mới) | subtitle: toạ độ, clamp viewport; panel: xếp góc, dedup dòng trùng liên tiếp, max lines, width; FontScale tác động cỡ font |
| `AppSettingsServiceTests` (mới) | file cũ không có field overlay → default đúng; round-trip đủ field; file hỏng → Default (pattern test settings hiện có nếu có, ngược lại tạo mới với temp path) |
| PipelineController (bổ sung) | `ActiveSource` set khi start, null khi stop và khi core-exited (dùng fake peer theo pattern test hiện có) |

Verify: `dotnet build "ReTran Project.slnx"` + `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` clean.

**Acceptance tay (bắt buộc trước merge):**
1. Game borderless windowed 1080p + pipeline chạy → overlay phủ đúng frame, chữ bám vị trí OCR, chuột bấm xuyên qua được vào game.
2. Đổi sang panel mode + đổi góc → bảng dịch hiện đúng góc.
3. Minimize/đóng game → overlay ẩn; mở lại → hiện theo.
4. Scaling 150% (Settings → Display) → overlay vẫn thẳng hàng với frame (không lệch).
5. OBS Browser Source vẫn nhận feed bình thường, không đổi.

## 11. Hiệu năng (budget)

- Redraw ≤ 15 fps; `UpdateLayeredWindow` trên DIB cùng kích thước cửa sổ game — CPU < 0.5%, RAM thêm ~1 DIB tạm. Không thêm capture/OCR nào. Tổng footprint vẫn trong budget (< 8% CPU khi gaming + streaming).

## 12. DPI & toạ độ

- App đã khai `PerMonitorV2` (`Platforms/Windows/app.manifest`) → mọi thao tác window ở pixel vật lý; rect từ `GetWindowRect`/`GetMonitorInfo` dùng nguyên trong không gian đó.
- Rủi ro lệch toạ độ khi game chạy trên monitor khác scale — bắt buộc acceptance item 4 (§10).

## 13. Non-goals

- Không virtual camera, không virtual display (spec riêng sau này).
- Không overlay đè lên exclusive fullscreen; không hook/injection vào game.
- Không drag/resize thủ công cửa sổ overlay (chỉ auto-anchor theo nguồn).
- Không hiển thị text gốc trên overlay local (chỉ bản dịch — đồng nhất với OBS overlay hiện có).
- Không đổi `OverlayFeedServer`, Core, Python sidecar.

## 14. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| Lệch toạ độ do PM-DPI ở scaling ≠ 100% | Acceptance 150% bắt buộc; toàn bộ tính toán trong pixel vật lý |
| GDI+ word-wrap dòng tiếng Việt dài bị tràn box | Clamp + ellipsis; font scale có 3 mức để user chỉnh |
| Game borderless hiếm hoi "không chịu" cửa sổ topmost đè lên | Fallback: user chuyển panel mode hoặc tắt overlay — không ảnh hưởng pipeline/OBS |
| `UpdateLayeredWindow` nặng ở cửa sổ rất lớn (4K) | Throttle 15 fps + chỉ redraw khi có frame mới; đo lại nếu cần (budget §11) |
