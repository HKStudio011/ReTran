# Thiết kế: Menu nổi trên desktop + thu app về system tray + sửa hàng checkbox trong Settings

Ngày: 2026-11-01, cập nhật 2026-11-02 sau builder-check · Trạng thái: **user đã duyệt 2026-11-04** (chọn chạy toàn bộ phần menu nổi cùng đợt với `2026-11-03-merge-thresholds-and-obs-visibility-design.md`) · Liên quan: `Docs/superpowers/plans/2026-11-01-ocr-line-grouping.md` (đang chạy song song, Task 3–7 chưa làm)

## 1. Ý định và vì sao

User chơi game và stream cùng lúc. Muốn chỉnh nhanh bật/tắt lớp phủ, đổi ngôn ngữ dịch, chỉnh nhịp pipeline và xem số thống kê **mà không alt-tab vào cửa sổ app**.

System tray không phải là đường duy nhất được, vì khi game fullscreen/borderless thì taskbar ẩn nên không với tới icon. Nên:

- **Menu nổi** là surface chính khi đang game: luôn trên cùng, kéo quanh màn hình, thu gọn được.
- **System tray** là đường còn lại để gọi lại cửa sổ app hoặc thoát, sau khi app đã thu về tray.
- **Thu app về tray**: bấm X không thoát, app ẩn xuống tray và **pipeline vẫn chạy**.

## 2. Goals

- **G1 — Menu nổi**: một window desktop topmost, **luôn đè lên chính cửa sổ overlay box của ReTran**; kéo được bằng chuột; thu gọn còn một chip; **mặc định BẬT**; tắt được bằng nút trong menu và bằng switch trong app. Không cam kết đè lên mọi app topmost khác (user đã chốt mức "như overlay hiện tại", xem D3).
- **G2 — Nội dung menu**: Start/Stop pipeline · **Chọn nguồn capture** (màn hình hoặc cửa sổ, chọn ngay trong menu) · Lớp phủ (switch) · Dịch sang (dst) · Nguồn ngôn ngữ (src) · Nhịp OCR · Nhịp chụp · Gộp nhiều dòng OCR · Thống kê. Mục Setting và Thống kê **giãn nở ngay trong menu**, không mở cửa sổ app.
- **G5 — Feedback lỗi khi app không nhìn thấy được**: action fail từ menu/tray phải hiện bằng **thông báo của Windows** (bubble tray) khi cửa sổ app đang ẩn xuống tray hoặc không được focus.
- **G3 — Tray**: icon + menu ngữ cảnh bốn mục: `Mở ReTran` · `Hiện/ẩn menu nổi` · `Bật/tắt lớp phủ` · `Thoát`. Bấm X trên cửa sổ app → ẩn window xuống tray, pipeline giữ nguyên trạng thái chạy.
- **G4 — Sửa lỗi Settings**: hàng checkbox trong sheet Settings đang bị tách thành 2–3 hàng (`ReTran.App/vite-project/src/css/deck.css:754` `.rt-srow { display: grid; gap: 5px }` một cột, còn markup `SettingsPanel.razor:161-195` có 3 con: nhãn / checkbox / hint).

## 3. Non-goals

- Không mở/hạ tầng cửa sổ app từ menu (Setting và Thống kê là panel bên trong menu).
- Không có nhập liệu bằng bàn phím trong menu: API key, tên model det/rec, tham số OCR kiểu `string`/`json`/`shape`, theme — tất cả ở lại sheet Settings trong app. Lý do: menu là window `WS_EX_NOACTIVATE`; kích hoạt để gõ chữ sẽ cướp bàn phím của game.
- Không làm menu cho game **exclusive fullscreen** (giới hạn ở §5.4; đường hook `Present` trong `ReTran.OCR/hook/` vẫn là việc riêng, planned, chưa start).
- Không chuyển menu sang HTML/`BlazorWebView`/WebView2. User đã chọn đường GDI+ native (phương án A).
- Không đổi `ReTran.Core`, không thêm wire RPC mới, không đổi sidecar Python. Menu chỉ là client của `AppSettingsService` / `OverlayWindowService` / `PipelineController` / `OverlayState` đang có.

## 4. Anti-goals (coi là thất bại nếu xảy ra)

- Thao tác với menu làm game mất foreground hoặc mất bàn phím.
- Menu kéo theo footprint vượt budget trong `AGENTS.md`: **không process mới**, **không thread mới**, redraw không vượt ~15 fps.
- Vùng trống của window menu ăn click của game (phải `HTTRANSPARENT`).
- Sửa sheet Settings làm vỡ layout các hàng `select`/`number` đang có, hoặc ghi đè file mà plan song song đang sửa.

## 5. Sự thật kỹ thuật đã kiểm chứng (không phải phỏng đoán)

1. **Window overlay hiện tại** (`ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs:185-192`): layered + topmost + `WS_EX_TRANSPARENT` (click-through) + `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`, `WS_POPUP`, nằm trên một STA thread có message pump riêng (`Pump` :134), mọi thao tác đi qua `Enqueue`. Nó phủ **toàn bộ rect nguồn** (`OverlayWindowService.cs:161`) và vẽ box bằng `UpdateLayeredWindow`.
2. **Z-order sẽ hỏng nếu menu là window khác mà không xử lý**: `OverlayWindowService.TickAnchor` chạy mỗi 250 ms (`:16`) và mỗi lần rect đổi gọi `Show()` → `SetWindowPos(hwnd, HWND_TOPMOST, ...)` (`Win32OverlayHost.cs:83`), đẩy window overlay lên trên window menu mới tạo.
3. **Overlay tự ẩn khi app thành foreground**: `Win32AnchorTracker.IsSourceForeground` (`:36-63`) trả `false` khi cửa sổ foreground không phải nguồn (với nguồn window). Đây là hành vi đang có; menu không được phép gây ra nó.
4. **Exclusive fullscreen**: Windows không cho window nào khác đè lên màn hình khi game ở exclusive fullscreen — cùng lý do `Home.razor:203` đang show note `overlay.note.fullscreen`. Borderless/windowed thì đè được bình thường.
5. **Stack** (`ReTran.App/obj/project.assets.json`): MAUI 10.0.20, `WindowsPackageType=None` (unpackaged), WindowsAppSDK **1.7.250909003**, `Microsoft.Web.WebView2/1.0.3179.45`. Trong metadata WindowsAppSDK 1.7 có `TaskbarIcon` (trong `Microsoft.UI.winmd` + `Microsoft.InteractiveExperiences.Projection.dll`), **không có** `NotifyIcon`; chưa xác nhận projection đó nằm trong assembly MAUI thực tham chiếu → **chọn đường `Shell_NotifyIcon` P/Invoke** (`DllImport` thường, cùng quy ước với `Win32OverlayHost.NativeMethods` / `Win32AnchorTracker.NativeMethods`, không bật `AllowUnsafeBlocks`).
6. **Thu app về tray**: đường đã kiểm chứng cho MAUI Blazor Hybrid unpackaged là `OnWindowCreated` (MauiProgram `ConfigureLifecycleEvents`) → `WinRT.Interop.WindowNative.GetWindowHandle` → `Microsoft.UI.Win32Interop.GetWindowIdFromWindow` → `AppWindow`, rồi `AppWindow.Hide()` / `Show()` và `AppWindow.Closing` với `e.Cancel = true`.
7. **Thống kê không có sẵn dưới dạng service**: fps, số box, số dòng đã dịch đang tính trong `ReTran.App/Components/Pages/Home.razor:251-256` và `:538-569`. Phải tách thành một nguồn dùng chung, nếu không sẽ có hai chỗ tính fps khác nhau.
8. **Settings**: `AppSettings` là record 10 tham số (`AppSettingsService.cs:31-38`), JSON camelCase, file thiếu/hỏng → `AppSettings.Default`, `Save` nguyên tử và bắn event `Changed` (`:127`).
9. **Đường tắt sạch đã có**: `UpdateStopper.StopServicesAsync` (dừng phiên pipeline trước, rồi dispose `CoreProcessClient` — kill tree, best-effort, không ném) + `IAppShutdown.Quit` (`MauiAppShutdown`). Tray "Thoát" phải đi qua đúng đường này để sidecar Paddle không mồ côi.
10. **`:has()`** trong CSS: cần kiểm chứng WebView2 của máy dev chấp nhận; nếu không, fallback là class `.rt-srow--check` trong Razor (quyết định của builder, có ghi ở §9).

11. **Nguồn capture không được lưu**: `SourceSelection(bool UseWindow, int Monitor, string? HwndHex)` chỉ là record trong `ReTran.App/Core/PipelineController.cs:16`; nó sống trong state của `Home.razor` (`_sourceKind`/`_monitorId`/`_windowHwnd`) và `PipelineController.ActiveSource` về `null` sau Stop. Danh sách nguồn lấy từ `PipelineController.ListSourcesAsync(...)` (`:304`, rpc `capture.list-sources`) — `Home.razor` đang gọi nó để đổ dropdown.
12. **`Shell_NotifyIcon` hỗ trợ bubble** qua cờ `NIF_INFO` trên chính `Modify` — đường báo lỗi Windows đơn giản nhất cho app unpackaged không cần toast AUMID/registration.

## 6. Hình thái tổng thể

Bốn khối mới, một khối sửa nhỏ, và các mối nối vào code đã có.

```
Win32OverlayHost (đã có, 1 STA thread + pump)
├── window #1 "ReTranOverlay"   : box + pill dịch, click-through
└── window #2 "ReTranMenu"      : menu nổi, KHÔNG click-through, NOACTIVATE
        │
        ├── FloatingMenuLayout    (thuần C#: rect, rows, hit-test, input intent)
        ├── FloatingMenuRenderer  (GDI+: panel bo góc + đổ bóng, switch, stepper, cycle, dòng số)
        └── FloatingMenuService   (nối layout ↔ AppSettings / OverlayWindowService / PipelineController)

RunStats (singleton mới)  ← PipelineController / OverlayState / preview frame
   ├── đọc bởi Home.razor (bỏ đoạn tự tính fps)
   └── đọc bởi FloatingMenuService (bảng Thống kê)

TrayIcon (mới, Shell_NotifyIcon + hwnd nhận callback message)
   └── Mở ReTran · Hiện/ẩn menu nổi · Bật/tắt lớp phủ · Thoát

AppWindow bridge (mới, Platforms/Windows + MauiProgram lifecycle)
   └── X → e.Cancel = true + AppWindow.Hide();  Hiện lại → AppWindow.Show() + Activate
```

### 6.1 Các quyết định đã chốt (user)

- Menu là **GDI+ native** (phương án A), không WebView.
- Menu chứa Start/Stop, Lớp phủ, dst, src, Nhịp OCR, Nhịp chụp, Gộp nhiều dòng OCR, Thống kê, và **nút Tắt menu nổi**.
- Bấm X = **ẩn xuống tray, pipeline vẫn chạy**.
- Menu tray đúng bốn mục: `Mở ReTran · Hiện/ẩn menu nổi · Bật/tắt lớp phủ · Thoát`.
- Switch bật/tắt menu nổi trong app, **mặc định bật**.
- **Chọn nguồn capture ngay trong menu** (selectbox/danh sách nguồn, không phải chỉ "nguồn gần nhất") + lưu lựa chọn cuối vào `settings.json`.
- Lỗi của hành động từ menu/tray: dùng **thông báo của Windows (bubble tray)** khi app đang ở tray hoặc không được focus; khi app đang focus thì giữ feedback trong app như hiện tại (`ToastService`).
- Z-order: **không** cơ chế re-assert định kỳ — chấp nhận mức như overlay hiện tại (menu có thể bị đè bởi app khác tự đẩy topmost).

### 6.2 Các quyết định của tôi (không phải user quyết) — user gạch nếu không đồng ý

- **D2.** Window menu dùng lại chính `Win32OverlayHost` (thêm window thứ hai trong cùng class, cùng STA thread, cùng `Enqueue`) thay vì một host riêng — để không thêm thread, và để nó biết hwnd của window box mà giữ z-order. `IOverlayHost` sẽ mở rộng thêm khả năng tạo/đặt/nhận hwnd cho menu; test fake hiện có (`OverlayWindowServiceTests`) phải cập nhật theo.
- **D3.** Z-order: khi window box `Show()`, `hWndInsertAfter` là hwnd window menu (thay vì `HWND_TOPMOST` tự do) → **menu luôn ở trên box của chính ReTran**. Không có timer re-assert: menu không tự leo lên trên các window topmost của app khác. Hệ quả user đã chấp nhận: app khác tự đẩy topmost (Discord/Steam overlay, app quay màn hình) vẫn có thể đè lên menu.
- **D4.** Chuột: `WM_NCHITTEST` trả `HTTRANSPARENT` cho điểm ngoài panel, `HTCLIENT` trong panel. Kéo menu bằng một dải grip ở header của panel; tọa độ mới là tọa độ desktop vật lý, clamp trong monitor chứa menu. Lưu vào `settings.json` (không lưu theo tỉ lệ %, lưu pixel vật lý).
- **D5.** Hai đường bật lại menu sau khi bấm "Tắt menu nổi": switch trong app **và** mục `Hiện/ẩn menu nổi` ở tray. Không có hotkey (để ngỏ, §9).
- **D6.** Stat nguồn dùng chung tên `RunStats`, đặt trong `ReTran.App/Core/`, và `Home.razor` chuyển sang đọc nó (không giữ lại logic fps riêng).
- **D7.** Tray qua `Shell_NotifyIcon` với một hwnd ẩn (message window) do chính app sở hữu, `TrackPopupMenu` cho menu ngữ cảnh; icon là **HICON 32×32 vẽ runtime bằng GDI+** (`TrayIconSource`: nền bo góc màu `BoxAnnotationStyle.PillFill`, chữ "R" màu `TickColor`) thay vì file `.ico` — tránh thêm asset + tooling chuyển đổi, và giữ handle sống tới khi xoá icon (chọn lại 2026-11-02 khi viết plan).
- **D8.** `AppSettings` thêm 7 tham số cuối (default an toàn, file cũ vẫn đọc được): `FloatingMenuOn = true`, `FloatingMenuCollapsed = false`, `FloatingMenuX` (`int?`, `null` = tự đặt vị trí mặc định lần đầu), `FloatingMenuY` (như trên), và bộ ba nguồn `SourceUseWindow = false` + `SourceMonitor = 0` + `SourceHwnd` (`string?`, `null`). **Không** có `CloseToTray`: X luôn ẩn xuống tray (bạn đã chốt không có điều kiện), thoát thật chỉ qua `Thoát` ở tray — một tham số không có chỗ bật/tắt trong UI chỉ là tham số chết.
- **D9.** Nguồn capture **trong menu** là một hàng cycle/danh sách (không phải `select` HTML): chạm vào hàng để mở danh sách nguồn ngay trong panel, chạm một mục để chọn. Kèm theo đó là persist bộ ba nguồn ở D8.
- **D10.** Persist nguồn: `SourceUseWindow`/`SourceMonitor` là thứ tái sử dụng được an toàn qua nhiều lần chạy app. `SourceHwnd` **không** có giá trị qua lần chạy app (hwnd đổi), nên nếu `SourceUseWindow = true` mà chưa chọn lại cửa sổ trong phiên hiện tại, nút Start trên menu phải báo rõ trên menu ("chưa chọn cửa sổ nguồn") + bubble, **không** âm thầm chuyển về màn hình 0.
- **D11.** Danh sách nguồn trong menu lấy từ `PipelineController.ListSourcesAsync(...)` (`capture.list-sources`), chỉ gọi khi hàng nguồn được mở (không gọi định kỳ); cắt tên cửa sổ dài, và thứ tự cycle là monitor trước rồi tới cửa sổ.
- **D12.** Một đường báo lỗi dùng chung (`AppNotifier`): nếu cửa sổ app đang ẩn hoặc không phải foreground → `Shell_NotifyIcon` bubble (`NIF_INFO`, xem §5.12); nếu app đang focus → `ToastService` như hiện tại. Menu không có dialog; với menu thì ngoài bubble còn thêm một dòng trạng thái lỗi ngắn trong panel (hàng cuối) để lỗi không biến mất khi bạn đang nhìn vào menu.
- **D13.** Fix checkbox làm bằng **CSS thuần** trong `deck.css` (rule nhắm vào `.rt-srow` có `input[type=checkbox]` làm con trực tiếp: nhãn + control cùng hàng, hint xuống hàng dưới; thêm `accent-color` theo token theme). Lý do chọn CSS thuần: không đụng `SettingsPanel.razor` — file mà Task 6 của plan song song đang sửa — và checkbox "Gộp nhiều dòng" họ sắp thêm sẽ tự đúng theo.
- **D14.** Sequencing với plan song song: xem §8.

## 7. Hành vi chi tiết đã chốt

### 7.1 Menu nổi

- Mặc định bật, mở rộng, vị trí lần đầu do app chọn (góc trên phải của monitor làm việc, trừ vùng taskbar, cách mép 16 px).
- Collapsed: chỉ còn một chip nhỏ (glyph logo/kích thước ~`BoxAnnotationStyle.DecorationScale`) vẫn kéo được, vẫn topmost, click vào chip = mở rộng.
- Expanded: panel gồm các hàng — nút Start/Stop (trạng thái theo `PipelineController`), **Chọn nguồn** (màn hình/cửa sổ), Lớp phủ (switch; **tắt control khi pipeline chưa chạy**, cùng quy tắc `Home.razor:197 disabled="@(!IsRunning)"`), dst và src (cycle qua danh sách mã đã dùng trong `SettingsPanel.razor:297-298`), Nhịp OCR và Nhịp chụp (stepper 1..60, cùng ngưỡng `TryParseOcrFps`), Gộp nhiều dòng OCR (switch), Thống kê (khi mở rộng thêm một vùng số), dòng trạng thái lỗi, Tắt menu nổi.
- **Start từ menu**: dùng `AppSettings.SourceUseWindow`/`SourceMonitor`/`SourceHwnd` (D8–D10). Hàng "Chọn nguồn" mở ngay trong panel một danh sách dựng từ `ListSourcesAsync` (monitor trước, cửa sổ sau, tên cắt ngắn); chọn một mục là ghi `AppSettings` ngay; `StartAsync` dựng `SourceSelection` từ bộ ba đó (không tự gán `PipelineController.ActiveSource` — property đó do `StartAsync` sở hữu, `PipelineController.cs:191`). Start khi `SourceUseWindow = true` nhưng hwnd không còn tồn tại → không tự đổi nguồn, hiện lỗi (D10, D12).
- đổi dst/src: ghi `AppSettings` ngay và **áp dụng ở lần Start kế tiếp** (giống note `settings.note.srcNextStart` hiện có) — menu phải nói rõ điều đó bằng chữ, không làm người dùng tưởng nó đổi ngay.
- đổi nhịp: cũng chỉ áp dụng ở `core.start` kế tiếp (`OcrFps`/`CaptureFps` gửi tại `PipelineController.cs:254-255`) — cùng quy tắc hiển thị "sẽ áp dụng khi Start lại".
- đổi **nguồn**: có hiệu lực ngay ở lần Start kế tiếp; nếu pipeline đang chạy thì đổi nguồn phải Stop trước (menu hiện nút Stop, không tự Stop hộ).
- Redraw: cùng nhịp throttle ~15 fps như `OverlayWindowService.DefaultRedrawInterval`, và chỉ khi có thay đổi trạng thái thật (không redraw định kỳ vô ích).

### 7.2 Tray

- Icon hiện từ lúc app chạy (kể cả khi menu nổi đang tắt).
- X trên cửa sổ app: `AppWindow.Closing` → `e.Cancel = true` + `AppWindow.Hide()`; không đụng `PipelineController`, không đụng `CoreProcessClient`, overlay/menu vẫn chạy.
- `Mở ReTran`: `AppWindow.Show()` + activate để window hiện lên đúng chỗ cũ.
- `Hiện/ẩn menu nổi`: toggle `FloatingMenuOn` (lưu file) rồi hiện/ẩn window menu.
- `Bật/tắt lớp phủ`: gọi `OverlayWindowService.SetEnabled`; khi pipeline chưa chạy thì overlay không có tác dụng — menu tray vẫn cho toggle nhưng trạng thái overlay chỉ có nghĩa khi running (giữ nguyên ngữ nghĩa `OverlayOnGame`).
- `Thoát`: `UpdateStopper.StopServicesAsync` (best-effort) rồi `IAppShutdown.Quit()`; khi update đang chạy thì chặn thoát (theo `Coordinator.UpdateInProgress` đang có).
- Bấm đơn/chuột trái icon: mở lại cửa sổ app (một hành vi quen thuộc; không cần dialog).
- **Báo lỗi (D12)**: mọi hành động khởi phát từ menu/tray khi thất bại (Start fail vì thiếu model OCR / core crash / hwnd nguồn đã đóng / `Save` lỗi) → nếu cửa sổ app đang ẩn hoặc không phải foreground thì dùng bubble `Shell_NotifyIcon` (`NIF_INFO`) với title "ReTran" và nội dung lỗi đã bản địa hoá; nếu app đang focus thì dùng `ToastService` như cũ. Khi lỗi đến từ menu nổi, ngoài bubble còn ghi một dòng trạng thái ngắn ở hàng cuối panel menu.
- Bubble là one-shot, không queue nhiều lỗi liên tiếp trong vài giây (gộp/throttle) để không spam khi core crash-loop.

### 7.3 Thống kê trong menu

Ít nhất: fps (khung/giây pipeline thấy được), số box của khung hiện tại, số dòng đã dịch, nhãn nguồn đang capture, cặp ngôn ngữ đang dùng. Lấy từ `RunStats` + `AppSettings` + `PipelineController.ActiveSource`.

### 7.4 Sửa checkbox Settings

- Kết quả mong muốn: một hàng `[control] [nhãn]` (hoặc `[nhãn] [control]` cùng hàng) và hint ở hàng dưới, căn phải theo control khác; ô tick dùng màu accent của theme, không còn ô tick mặc định trần trụi.
- Các hàng `select`/`number`/`text` **giữ nguyên** layout đang có (nhãn trên, control chiếm hết chiều ngang sheet 430 px) — không phải là bug.

## 8. Xung đột với plan đang chạy (`2026-11-01-ocr-line-grouping`)

Plan đó: Task 1–2 đã commit (`3ae1ba2`, `20aca50`); còn Task 3–7. Task 5 sửa `AppSettingsService.cs` (thêm `OcrMergeLines`), `PipelineController.cs`, `OverlayState.cs`; Task 6 sửa `SettingsPanel.razor`, `I18nStrings.cs`, `Services/OverlayWindow/OverlayLayout.cs`, `Win32OverlayHost.cs`.

Hai phiên đang ngồi trên **cùng một working tree** (`master`, ~97 file chưa commit của user). Nên thứ tự bắt buộc:

- **Làm được ngay, không đụng file nào của plan song song**: `deck.css` (fix checkbox), toàn bộ file mới `FloatingMenu*`, `RunStats`, `TrayIcon`, `AppWindow` bridge trong `Platforms/Windows` + `MauiProgram`, và test của chúng.
- **Xếp sau khi plan song song commit Task 5–6**: `AppSettingsService.cs` (record — cả hai bên đều thêm tham số cuối), `Win32OverlayHost.cs` (window thứ hai + z-order), `Home.razor` (switch menu nổi + bỏ đoạn fps), `I18nStrings.cs` (key mới), `MauiProgram.cs` (DI).
- **Worktree riêng**: phương án dự phòng, nhưng **mặc định không dùng** — worktree sẽ không có các thay đổi chưa commit của user (`deck.css`, `fonts.css`, `wwwroot/build`, toàn bộ UI v2), nên mọi thứ xây trên đó sẽ lệch với bản đang chạy.

Nếu trong lúc làm mà plan song song commit vào một file ta sắp sửa, dừng phần đó và rebase lại trên commit mới, không ghi đè.

## 9. Để ngỏ cho builder (quyết ở bước plan, không cần user)

- Kích thước panel, cỡ chữ, spacing, thứ tự chính xác của các hàng (menu dựng theo layout thuần → đo được bằng test).
- Glyph cho các nút (không có icon font trong project → vẽ bằng GDI+ path, giống cách `Card.razor`/`overlay.js` đang vẽ SVG).
- Cách menu báo "tham số này chỉ áp dụng ở lần Start kế tiếp" (một dòng chữ nhỏ, hay badge trên control).
- Vị trí mặc định cụ thể, hành vi multi-monitor khi rút monitor, xử lý DPI (toạ độ vật lý vs DIP) cho cả menu và tray.
- Hotkey toàn cục để hiện/ẩn menu (không nằm trong yêu cầu).
- Hình thái cụ thể của hàng "Chọn nguồn" trong panel GDI+: danh sách mở rộng cuộn được, hay hai hàng cycle (monitor / cửa sổ) — cả hai đều đọc `ListSourcesAsync`, chỉ khác số dòng renderer và cách test layout.
- Danh sách nguồn: `capture.list-sources` đã lọc phía Core (`ListSourcesAsync` trả thẳng danh sách `WindowInfo` như `Home.razor:393-395`), nên menu chỉ cần quyết định giới hạn số mục hiển thị và cách cắt tên.
- Có dùng `:has()` hay class `.rt-srow--check` (xem §5.10).
- Tên chính xác của các key i18n mới (phải theo thông lệ `deck.*` / `ocr.*` hiện có, và phải vào **cả hai** block `vi`/`en` trong `I18nStrings.cs`).

## 10. Test strategy

- `FloatingMenuLayout` (unit test thuần, không pixel): hit-test trong/ngoài panel khi collapsed và expanded; `HTTRANSPARENT` cho vùng trống; bounds của stepper (clamp 1..60); cycle wrap đúng danh sách; clamp vị trí menu trong monitor; thứ tự và sự hiện diện của row theo trạng thái pipeline; dựng danh sách nguồn từ `(Monitors, Windows)` — monitor trước, cắt tên cửa sổ dài, thứ tự ổn định.
- `FloatingMenuService`: mỗi intent dẫn tới đúng một tác động (`OverlayWindowService.SetEnabled`, `AppSettings.Save` với `Dst`/`Src`/`OcrFps`/`CaptureFps`/bộ ba nguồn, `PipelineController` start/stop); không ném khi pipeline chưa chạy; Start với `SourceUseWindow = true` + hwnd đã đóng → báo lỗi, không đổi nguồn; throttle redraw.
- `AppNotifier`: rule chọn kênh (app ẩn/không foreground → bubble, app đang focus → toast) và throttle bubble (nhiều lỗi trong một cửa sổ thời gian ngắn → chỉ 1 bubble).
- `RunStats`: cửa sổ tính fps, reset khi pipeline stop, số box/dòng từ `OverlayState.Latest`.
- `AppSettingsService`: round-trip 6 tham số mới; file cũ không có các key đó → đúng default (`FloatingMenuOn = true`, `SourceUseWindow = false`).
- `TrayIcon`: dựng `NOTIFYICONDATA`/menu items đúng flags và đúng thứ tự (test không cần tray thật).
- Render smoke: một test theo khuôn `ReTran.Tests/Overlay/Win32OverlayHostRenderTests.cs` dựng panel expanded và xác nhận JPEG/bitmap hợp lệ, không pixel-perfect assert.
- Thủ công (ghi kết quả, không tự động hoá): kéo menu quanh màn hình khi một game borderless đang chạy; chọn nguồn là cửa sổ game ngay trong menu rồi Start; bật/tắt lớp phủ và Start/Stop từ menu; bấm Start khi chưa chọn cửa sổ nguồn → lỗi hiện trên menu + bubble; X → xuống tray → `Mở ReTran`; `Thoát` không để lại process `ReTran.Core`/python mồ côi; bật/tắt menu nổi bằng switch trong app.

## 11. Performance

- Không process mới, không thread mới (D2). Window menu chỉ là một layered window + bitmap nhỏ.
- Bitmap 32bppPArgb cho panel ~320×420 px ≈ 0,5 MB; redraw ≤ ~15 fps và chỉ khi state đổi.
- Không có timer re-assert z-order (D3) — `TickAnchor` 250 ms hiện có là timer duy nhất liên quan tới window, và nó chỉ `SetWindowPos` khi rect đổi.
- `ListSourcesAsync` chỉ được gọi khi hàng nguồn được mở (D11), không gọi định kỳ.
- Tray: một hwnd ẩn + callback message, không poll, không timer.
- Ngân sách `AGENTS.md` (UI/overlay ≤ 10–15 fps, tổng footprint < 8% CPU / < 500 MB) không được xấu đi; khi xong phải đo và ghi vào `Docs/performance.md` (ghi chú ý: RAM đã vượt do sidecar Paddle ~2 GB, open item đã ghi ngày 2026-10-21).

## 12. Doc comment

Mọi public API mới (C#) theo quy ước song ngữ Anh → Việt của `AGENTS.md`; `internal` API chỉ doc khi hành vi không hiển nhiên. Test không cần doc.
