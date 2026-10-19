# Game Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cửa sổ overlay local trong suốt, always-on-top, click-through — vẽ bản dịch đè lên nguồn capture (cửa sổ game borderless/windowed hoặc monitor), 2 chế độ hiển thị (phủ lên chữ / bảng góc) chỉnh được trong UI.

**Architecture:** Subsystem mới `ReTran.App/Services/OverlayWindow/`: layout engine thuần (`OverlayLayout`) + seam testable (`IOverlayHost`, `IAnchorTracker`) + host Win32 layered window trên worker thread STA riêng (`Win32OverlayHost`, render GDI+ qua `UpdateLayeredWindow`) + `OverlayWindowService` điều phối (throttle ≤15 fps, theo dõi anchor 250 ms). Dữ liệu in-process từ `OverlayState`; OBS feed không đổi. UI: card mới trong Home.razor; settings mở rộng 4 field.

**Tech Stack:** .NET 10 MAUI Blazor Hybrid, Win32 P/Invoke (User32/GDI32), System.Drawing.Common (GDI+), xUnit v3.

**Spec:** `Docs/superpowers/specs/2026-10-15-game-overlay-design.md` (đã user-approved).

## Global Constraints

- Namespace App-side: `ReTran_App.Services.OverlayWindow`; test files: `ReTran.Tests/Overlay/` (không khai namespace — theo style test hiện có như `PipelineControllerTests.cs`).
- Doc comments song ngữ Anh/Việt cho mọi public API (AGENTS.md).
- Log qua `ILogger<T>`; không `Console.WriteLine`.
- Redraw throttle ≤ 15 fps (mặc định 66 ms); anchor poll 250 ms; CPU < 0.5% (budget spec §11).
- App đã PerMonitorV2 (`Platforms/Windows/app.manifest`) — mọi toạ độ là pixel vật lý, không tự scale thêm.
- KHÔNG đổi `OverlayFeedServer`, `ReTran.Core`, Python sidecar, trang `/overlay`.
- TDD: Task 1–4 viết test fail trước khi implement; Task 5 (P/Invoke) verify bằng build clean + manual smoke; commit mỗi task (`feat:`/`test:`).
- Build: `dotnet build "ReTran Project.slnx"` — Test: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`.

## Review Focus

1. **Game đóng/minimize khi overlay đang bật** — HWND chết không được ném exception; overlay ẩn, hiện lại khi game mở lại. → Task 4 test `TickAnchor_TrackerNull_Hides`; Task 5 tracker trả `null` khi `!IsWindow`/`IsIconic`.
2. **Click vào vùng có box overlay phải xuyên qua vào game** (WS_EX_TRANSPARENT), không bao giờ tranh focus. → Task 5 manual smoke: click xuyên qua box overlay ra desktop/app phía sau.
3. **Game exclusive fullscreen** — overlay không vẽ đè được; hành vi mong đợi: không crash, UI có ghi chú hướng dẫn borderless, pipeline/OBS nguyên vẹn. → Task 6: i18n key `overlay.note.fullscreen` render trong card + manual acceptance.
4. **Scaling 150% / monitor khác DPI** — overlay thẳng hàng với frame, không lệch toạ độ. → Task 6 manual acceptance item 4 (spec §10).
5. **Game di chuyển/resize nhanh (đổi resolution)** — overlay theo kịp trong ≤ 1 poll interval (~250 ms), không để lại cửa sổ "ma" ở vị trí cũ; rect không đổi thì không gọi `SetWindowPos` lặp. → Task 4 test `TickAnchor_SameRect_NoRepeatedShow`; Task 6 manual: đổi resolution game khi đang chạy.

---

### Task 1: AppSettings + 4 field overlay

**Files:**
- Modify: `ReTran.App/Services/AppSettingsService.cs:15` (record `AppSettings`)
- Test: `ReTran.Tests/AppSettingsServiceTests.cs` (bổ sung)

**Interfaces:**
- Consumes: không có.
- Produces:
  ```csharp
  public sealed record AppSettings(
      string Provider, string Src, string Dst, string? ApiKey,
      bool OverlayOnGame = false,
      string OverlayMode = "subtitle",   // "subtitle" | "panel"
      string OverlayCorner = "bl",       // "tl" | "tr" | "bl" | "br"
      double OverlayFontScale = 1.0);    // 0.85 | 1.0 | 1.25
  ```

- [ ] **Step 1: Viết test fail — legacy file + round-trip field mới**

Thêm vào `ReTran.Tests/AppSettingsServiceTests.cs`:

```csharp
[Fact]
public void LegacyFile_WithoutOverlayFields_LoadsOverlayDefaults()
{
    File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
    var service = new AppSettingsService(_path);

    AppSettings settings = service.Load();

    Assert.Equal("google", settings.Provider);
    Assert.False(settings.OverlayOnGame);
    Assert.Equal("subtitle", settings.OverlayMode);
    Assert.Equal("bl", settings.OverlayCorner);
    Assert.Equal(1.0, settings.OverlayFontScale);
}

[Fact]
public void SaveThenLoad_Roundtrips_WithOverlayFields()
{
    var service = new AppSettingsService(_path);
    var saved = new AppSettings("google", "auto", "vi", null,
        OverlayOnGame: true, OverlayMode: "panel", OverlayCorner: "tr", OverlayFontScale: 1.25);

    service.Save(saved);

    Assert.Equal(saved, service.Load());
}
```

- [ ] **Step 2: Chạy test — mong đợi FAIL (compile error: record chưa có field)**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class AppSettingsServiceTests`
Expected: build fail — `AppSettings` không có constructor parameter `OverlayOnGame`.

- [ ] **Step 3: Implement — mở rộng record**

Đổi record ở `ReTran.App/Services/AppSettingsService.cs:15` thành signature trong Interfaces (giữ doc comment song ngữ, thêm `<param>` cho 4 field mới). Constructor 4-arg cũ vẫn compile vì field mới có default.

- [ ] **Step 4: Chạy test — mong đợi PASS**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class AppSettingsServiceTests`
Expected: PASS (2 test mới + 2 test cũ).

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/AppSettingsService.cs ReTran.Tests/AppSettingsServiceTests.cs
git commit -m "feat: extend AppSettings with overlay display options"
```

---

### Task 2: PipelineController.ActiveSource

**Files:**
- Modify: `ReTran.App/Core/PipelineController.cs` (class `PipelineController`, ~line 97)
- Test: `ReTran.Tests/PipelineControllerTests.cs` (bổ sung)

**Interfaces:**
- Consumes: `SourceSelection` (`ReTran.App/Core/PipelineController.cs:15`).
- Produces:
  ```csharp
  // trong PipelineController
  public SourceSelection? ActiveSource { get; private set; }   // set sau core.start thành công; null khi StopAsync / core-exited
  ```

- [ ] **Step 1: Viết test fail — 3 test mới**

Thêm vào `ReTran.Tests/PipelineControllerTests.cs` (dùng helper `CreateController(peer, overlay)` có sẵn):

```csharp
[Fact]
public async Task Start_SetsActiveSource_ToSelection()
{
    var peer = new FakeCorePeer { IsStarted = true };
    PipelineController controller = CreateController(peer, new OverlayState());
    SourceSelection selection = new(UseWindow: true, Monitor: 0, HwndHex: "0x1234");

    await controller.StartAsync(selection, CancellationToken.None);

    Assert.Equal(selection, controller.ActiveSource);
}

[Fact]
public async Task Stop_ClearsActiveSource()
{
    var peer = new FakeCorePeer { IsStarted = true };
    PipelineController controller = CreateController(peer, new OverlayState());
    await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
    Assert.NotNull(controller.ActiveSource);

    await controller.StopAsync(CancellationToken.None);

    Assert.Null(controller.ActiveSource);
}

[Fact]
public async Task CoreExited_ClearsActiveSource()
{
    var peer = new FakeCorePeer { IsStarted = true };
    PipelineController controller = CreateController(peer, new OverlayState());
    await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

    peer.RaiseExited(0);

    Assert.Null(controller.ActiveSource);
}
```

- [ ] **Step 2: Chạy test — mong đợi FAIL (compile error: `ActiveSource` chưa tồn tại)**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class PipelineControllerTests`
Expected: build fail.

- [ ] **Step 3: Implement**

Trong `PipelineController`: thêm property `ActiveSource` (doc comment song ngữ); gán `ActiveSource = source;` cuối `StartAsync` (sau `IsRunning = true;`); gán `ActiveSource = null;` trong `StopAsync` và `HandleCoreExited`.

- [ ] **Step 4: Chạy test — mong đợi PASS**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class PipelineControllerTests`
Expected: PASS (3 mới + toàn bộ test cũ).

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Core/PipelineController.cs ReTran.Tests/PipelineControllerTests.cs
git commit -m "feat: expose ActiveSource on PipelineController"
```

---

### Task 3: OverlayLayout — layout engine thuần

**Files:**
- Create: `ReTran.App/Services/OverlayWindow/OverlayLayout.cs`
- Test: `ReTran.Tests/Overlay/OverlayLayoutTests.cs` (mới)

**Interfaces:**
- Consumes: `OverlayFrame`, `OverlayBox` (`ReTran.App/Core/OverlayState.cs`).
- Produces (namespace `ReTran_App.Services.OverlayWindow`):
  ```csharp
  public enum OverlayMode { Subtitle, Panel }
  public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

  public sealed record OverlayViewOptions(OverlayMode Mode, OverlayCorner Corner, double FontScale);
  public sealed record DrawBox(double X, double Y, double W, double H, string Text, bool IsPanelLine);

  public static class OverlayLayout
  {
      public const int PanelMaxLines = 10;
      public const double PanelMargin = 12.0;
      public const double PanelLineGap = 8.0;
      public const double PanelWidthRatio = 0.4;
      public const double PanelLineHeight = 24.0;   // × FontScale

      public static IReadOnlyList<DrawBox> Render(OverlayFrame frame, OverlayViewOptions options, double viewportWidth, double viewportHeight);
  }
  ```

Quy tắc render (pin — test dựa vào đây):
- **Subtitle:** mỗi box có `Translated` khác rỗng (`!string.IsNullOrWhiteSpace`) → `x = b.X*vw, y = b.Y*vh, w = b.W*vw, h = b.H*vh`; clamp: `x,y ≥ 0`, `w = min(w, vw-x)`, `h = min(h, vh-y)`; bỏ box nếu `w <= 0 || h <= 0`. `IsPanelLine = false`, `Text = Translated`.
- **Panel:** lấy các box có translation khác rỗng (giữ thứ tự frame), bỏ trùng **liên tiếp** (`OrdinalIgnoreCase`), cắt tối đa `PanelMaxLines`. `panelW = vw * PanelWidthRatio`; `x = corner phải ? vw - PanelMargin - panelW : PanelMargin`. `lineH = PanelLineHeight * FontScale`. Corner trên: `y_i = PanelMargin + i*(lineH + PanelLineGap)`. Corner dưới: dòng đầu áp sát đáy — `y_i = (vh - PanelMargin) - (i+1)*lineH - i*PanelLineGap`. `IsPanelLine = true`, `W = panelW`, `H = lineH`.

- [ ] **Step 1: Viết test fail**

Tạo `ReTran.Tests/Overlay/OverlayLayoutTests.cs` (`using ReTran_App.Core; using ReTran_App.Services.OverlayWindow;`). Helper dựng frame:

```csharp
static OverlayFrame Frame(params (double X, double Y, double W, double H, string Text, string Translated)[] boxes) =>
    new(1920, 1080, DateTime.UtcNow, boxes.Select(b => new OverlayBox(b.X, b.Y, b.W, b.H, b.Text, b.Translated)).ToList());

static OverlayViewOptions Subtitle(double scale = 1.0) => new(OverlayMode.Subtitle, OverlayCorner.BottomLeft, scale);
```

Test methods:
- `Subtitle_MapsRelativeCoordsToViewport` — frame box `(0.1, 0.2, 0.3, 0.05, "HELLO", "Xin chào")`, viewport `1920×1080` → 1 DrawBox `(192, 216, 576, 54, "Xin chào", false)`.
- `Subtitle_ClampsToViewportEdges` — viewport `1000×500`, 2 box: `(0.95, -0.1, 0.2, 0.3, "A", "B")` → clamp thành `(x=950, y=0, w=min(200, 50)=50, h=150)`; và `(1.0, 0.5, 0.1, 0.1, "C", "D")` → sau clamp `w = min(100, 0) = 0` → **bỏ**. Assert kết quả đúng 1 DrawBox `(950, 0, 50, 150, "B", false)`.
- `Subtitle_SkipsEmptyTranslation` — box `Translated = ""` → kết quả rỗng.
- `Panel_StacksTopLeft_FromTopDown` — options `new(OverlayMode.Panel, OverlayCorner.TopLeft, 1.0)`, 3 dòng "A","B","C", viewport `1000×500` → 3 DrawBox: `x=12`, `W=400`, `H=24`; `y = 12, 44, 76`.
- `Panel_BottomRight_DocksToBottomEdge` — corner `BottomRight`, viewport `1000×500` → dòng đầu: `x = 1000-12-400 = 588`, đáy dòng = `y+H = 500-12 = 488`; dòng thứ hai nằm **trên** dòng đầu (`y2 < y1`).
- `Panel_DedupsConsecutiveDuplicates` — "A","a","B" (OrdinalIgnoreCase) → 2 dòng.
- `Panel_CapsAtMaxLines` — 15 box khác nhau → 10 dòng.
- `Panel_SkipsEmptyTranslations` — xen kẽ "" → chỉ tính dòng có chữ.

- [ ] **Step 2: Chạy test — mong đợi FAIL (compile error)**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class OverlayLayoutTests`
Expected: build fail — thiếu `OverlayLayout`.

- [ ] **Step 3: Implement `OverlayLayout.Render` theo quy tắc đã pin**

Một file duy nhất, static class + 2 enum + 2 record. Không IO, không Win32.

- [ ] **Step 4: Chạy test — mong đợi PASS**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class OverlayLayoutTests`
Expected: PASS (8 test).

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/OverlayWindow/OverlayLayout.cs ReTran.Tests/Overlay/OverlayLayoutTests.cs
git commit -m "feat: add pure overlay layout engine (subtitle + panel modes)"
```

---

### Task 4: IOverlayHost + IAnchorTracker + OverlayWindowService

**Files:**
- Create: `ReTran.App/Services/OverlayWindow/IOverlayHost.cs`
- Create: `ReTran.App/Services/OverlayWindow/IAnchorTracker.cs`
- Create: `ReTran.App/Services/OverlayWindow/OverlayWindowService.cs`
- Test: `ReTran.Tests/Overlay/OverlayWindowServiceTests.cs` (mới)

**Interfaces:**
- Consumes: Task 1 (`AppSettings` + field overlay), Task 2 (`PipelineController.ActiveSource`), Task 3 (`OverlayLayout`, `OverlayViewOptions`, `DrawBox`, `OverlayMode`, `OverlayCorner`), `OverlayState` (`Core/OverlayState.cs`).
- Produces (namespace `ReTran_App.Services.OverlayWindow`):
  ```csharp
  public interface IOverlayHost : IDisposable
  {
      bool IsCreated { get; }
      void Create();                                            // tạo HWND (idempotent)
      void Show(int x, int y, int width, int height);           // move/resize + show (pixel vật lý)
      void Hide();                                              // ẩn, không destroy
      void Draw(IReadOnlyList<DrawBox> boxes, double fontScale);// vẽ frame hiện tại (empty list = màn trống)
  }

  public interface IAnchorTracker
  {
      (int X, int Y, int Width, int Height)? GetAnchorRect(SourceSelection source); // null = nguồn biến mất/minimized
  }

  public sealed class OverlayWindowService : IDisposable
  {
      public OverlayWindowService(
          OverlayState overlay, PipelineController pipeline, IOverlayHost host, IAnchorTracker anchor,
          AppSettingsService settings, ILogger<OverlayWindowService> log, TimeSpan? redrawInterval = null); // default 66 ms (≈15 fps)
      public bool IsEnabled { get; }
      public void SetEnabled(bool enabled);   // true: Create + timer anchor 250 ms + persist OverlayOnGame; false: stop timer + host.Dispose() + persist
      public void ApplyOptions(string? mode = null, string? corner = null, double? fontScale = null); // cập nhật options + persist; giá trị không hợp lệ → giữ option cũ + log warning
      public void TickAnchor();               // gọi bởi timer nội bộ; expose cho test
  }
  ```

Hành vi (pin):
- `OverlayState.Changed` handler: bỏ qua nếu `!IsEnabled`, chưa có anchor rect, hoặc còn trong `redrawInterval` kể từ lần Draw cuối. Ngược lại: đọc `overlay.Latest` (null → bỏ), layout bằng `OverlayLayout.Render(frame, options, anchorRect.Width, anchorRect.Height)`, gọi `host.Draw(boxes, fontScale)`.
- Mapping settings→options: `"subtitle"|"panel"` → `OverlayMode`; `"tl"|"tr"|"bl"|"br"` → `OverlayCorner` (invalid → giữ cũ + log).
- `TickAnchor`: `!IsEnabled` → return. `pipeline.ActiveSource` null → `host.Hide()`. `anchor.GetAnchorRect(source)` null → `host.Hide()`. Rect khác rect lần trước → `host.Show(...)` + lưu; giống → không gọi gì.
- `PipelineStateChanged(running:false, ...)` → `host.Hide()` ngay (không đổi `IsEnabled`).
- `Dispose`: unsubscribe events, stop timer, `host.Dispose()`.

- [ ] **Step 1: Viết test fail**

Tạo `ReTran.Tests/Overlay/OverlayWindowServiceTests.cs`. Doubles trong cùng file:

```csharp
sealed class FakeHost : IOverlayHost
{
    public bool IsCreated { get; private set; }
    public int CreateCalls, HideCalls, DisposeCalls;
    public List<(int X, int Y, int W, int H)> Shows = new();
    public List<(IReadOnlyList<DrawBox> Boxes, double Scale)> Draws = new();
    // implement các method theo nghĩa rõ ràng: Create → IsCreated=true, CreateCalls++ ...
}

sealed class FakeTracker : IAnchorTracker
{
    public (int X, int Y, int Width, int Height)? Rect;          // set được từ test
    public (int X, int Y, int Width, int Height)? GetAnchorRect(SourceSelection source) => Rect;
}
```

Dựng service như `PipelineControllerTests` (temp settings path + `FakeCorePeer` copy pattern — import/copy class `FakeCorePeer` từ `PipelineControllerTests.cs` nếu nó private: khai lại bản giống hệt trong file test này).

Test methods (mỗi test dựng `OverlayState`, `PipelineController`(FakeCorePeer), `FakeHost`, `FakeTracker`, `AppSettingsService(temp)`, service với `redrawInterval: null` trừ khi nói khác):
- `SetEnabled_True_CreatesHostAndPersists` — `service.SetEnabled(true)` → `host.CreateCalls == 1`, `IsEnabled == true`, file settings chứa `"overlayOnGame":true`.
- `SetEnabled_False_DisposesHostAndPersists` — enable rồi disable → `host.DisposeCalls == 1`, `IsEnabled == false`, settings `"overlayOnGame":false`.
- `PublishFrame_BeforeAnchor_NoDraw` — enable, publish frame (không TickAnchor) → `host.Draws.Count == 0`.
- `TickAnchor_ThenPublish_DrawsLaidOutBoxes` — tracker.Rect = `(0,0,1280,720)`; `TickAnchor()` → `host.Shows == [(0,0,1280,720)]`; publish frame box `(0.5, 0.5, 0.2, 0.1, "A", "B")` → 1 Draw: box đầu `(640, 360, 256, 72, "B", false)`.
- `TickAnchor_SameRect_NoRepeatedShow` — TickAnchor 2 lần → `host.Shows.Count == 1`.
- `TickAnchor_TrackerNull_Hides` — tracker.Rect = null → `host.HideCalls >= 1`.
- `TickAnchor_NoActiveSource_Hides` — không start pipeline (ActiveSource null) → Hide.
- `PublishTwiceWithinInterval_DrawsOnce` — service với `redrawInterval: TimeSpan.FromHours(1)`; anchor xong, publish 2 frame liên tiếp → `host.Draws.Count == 1`.
- `PublishFrame_WithNoBoxes_DrawsEmptyList` — frame boxes rỗng → Draw với list rỗng (màn phải được làm sạch).
- `CoreExited_HidesOverlay` — start pipeline, enable + anchor, `peer.RaiseExited(0)` → Hide được gọi.
- `ApplyOptions_PersistsAndAppliesMode` — enable + anchor (tracker.Rect = `(0,0,1280,720)`, `TickAnchor()`); `service.ApplyOptions(mode: "panel")` → settings `"overlayMode":"panel"`; publish frame sau đó → mọi DrawBox trong lần Draw kế tiếp có `IsPanelLine == true`.

- [ ] **Step 2: Chạy test — mong đợi FAIL (compile error)**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class OverlayWindowServiceTests`
Expected: build fail — thiếu interfaces/service.

- [ ] **Step 3: Implement 3 file**

Theo Interfaces + Hành vi đã pin. Timer: `System.Threading.Timer(250 ms)` tạo trong `SetEnabled(true)`, hủy trong `SetEnabled(false)`/`Dispose`. Mọi handler event phải an toàn cross-thread (chỉ đọc state + gọi host — host thật tự serialize, Task 5).

- [ ] **Step 4: Chạy test — mong đợi PASS**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class OverlayWindowServiceTests`
Expected: PASS (11 test). Rồi chạy toàn bộ: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` — PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/OverlayWindow/ ReTran.Tests/Overlay/OverlayWindowServiceTests.cs
git commit -m "feat: add OverlayWindowService with host/anchor seams and throttling"
```

---

### Task 5: Win32 implementation — Win32AnchorTracker + Win32OverlayHost

**Files:**
- Create: `ReTran.App/Services/OverlayWindow/Win32AnchorTracker.cs`
- Create: `ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs`
- Modify: `ReTran.App/ReTran.App.csproj` (thêm `<PackageReference Include="System.Drawing.Common" Version="10.*" />`)
- Modify: `ReTran.App/MauiProgram.cs` (DI wiring — Step 5)
- Modify: `ReTran.App/MainPage.xaml.cs` (một dòng bật overlay TẠM cho smoke test — xóa ở Task 6)

**Interfaces:**
- Consumes: Task 4 (`IOverlayHost`, `IAnchorTracker`), `SourceSelection`.
- Produces: `public sealed class Win32AnchorTracker : IAnchorTracker` và `public sealed class Win32OverlayHost : IOverlayHost` — cùng namespace, signatures khớp interface.

Chi tiết pin (không unit-test được — P/Invoke):
- **Win32AnchorTracker:** window source — parse `HwndHex` ("0x…") → `IntPtr`; `!IsWindow(hwnd)` hoặc `IsIconic(hwnd)` → `null`; ngược lại `GetWindowRect` → tuple. Screen source — `EnumDisplayMonitors` theo thứ tự, lấy index `source.Monitor`, `GetMonitorInfo` → `rcMonitor`. Guard `OperatingSystem.IsWindows()` → trả `null` off-Windows.
- **Win32OverlayHost:** worker thread background STA riêng + message pump (`GetMessage` loop) + `ConcurrentQueue<Action>`; mọi method public enqueue action (thread-safe). HWND sống trên thread đó.
  - `Create`: đăng ký window class `"ReTranOverlay"`, `CreateWindowEx(WS_POPUP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)`; WNDPROC = DefWindowProc, trả 0 cho `WM_ERASEBKGND`.
  - `Show`: `SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW | SWP_NOACTIVATE)`.
  - `Hide`: `ShowWindow(SW_HIDE)` (giữ HWND).
  - `Draw(boxes, fontScale)`: dựng bitmap 32-bit BGRA đúng kích thước hiện tại → GDI+ `Graphics`:
    - Subtitle box: rounded rect (radius 6) fill `Color.FromArgb(115, 0, 0, 0)` (≈45% đen — đồng nhất overlay OBS), border 3 px `Color.FromArgb(255, 34, 197, 94)` (#22c55e); chữ trắng "Segoe UI" Bold, cỡ px = `clamp(14, viewportW * 0.024, 32) * fontScale`, word-wrap (`StringFormat.GenericTypographic`) + căn giữa dọc trong box.
    - Panel line: rounded rect giống trên; chữ single-line ellipsis (`StringTrimming.EllipsisCharacter`), căn giữa dọc, cùng cỡ font công thức trên.
    - `UpdateLayeredWindow(hwnd, null, null, &size, hdcMem, &ptSrc(0,0), 0, &blendFunc(AC_SRC_ALPHA), ULW_OPAQUE)`.
  - `Dispose`: enqueue `DestroyWindow` + quit message loop, join thread (timeout 5 s).

- [ ] **Step 1: Thêm package reference**

Thêm vào `ReTran.App/ReTran.App.csproj`: `<PackageReference Include="System.Drawing.Common" Version="10.*" />`. Run: `dotnet build "ReTran Project.slnx"` — PASS.

- [ ] **Step 2: Implement `Win32AnchorTracker`**

P/Invoke User32: `IsWindow`, `IsIconic`, `GetWindowRect`, `EnumDisplayMonitors`, `GetMonitorInfo`. Guard off-Windows trả `null`.

- [ ] **Step 3: Implement `Win32OverlayHost`**

Theo chi tiết pin ở trên. P/Invoke: `RegisterClassEx`, `CreateWindowEx`, `DefWindowProc`, `SetWindowPos`, `ShowWindow`, `DestroyWindow`, `GetMessage`, `PostThreadMessage`, `CreateDIBSection`, `DeleteObject`, `GetDC`/`ReleaseDC`, `UpdateLayeredWindow`. GDI+ qua `System.Drawing.Graphics` trên `Bitmap` (32bpp ARGB).

- [ ] **Step 4: Build clean**

Run: `dotnet build "ReTran Project.slnx"`
Expected: PASS, không warning mới.

- [ ] **Step 5: Manual smoke test (bắt buộc trước khi sang Task 6)**

Trước tiên thêm DI wiring vào `MauiProgram.cs` (cạnh các registration `OverlayFeedServer` hiện có — Task 6 chỉ còn UI, không đụng lại DI):

```csharp
builder.Services.AddSingleton<IOverlayHost, Win32OverlayHost>();
builder.Services.AddSingleton<IAnchorTracker, Win32AnchorTracker>();
builder.Services.AddSingleton<OverlayWindowService>();
```

Run app (`dotnet run --project ReTran.App/ReTran.App.csproj` — hoặc launch MAUI như dev flow hiện có). Chưa có UI ở task này → thêm **tạm** vào `MainPage.xaml.cs`, ngay sau `_feedServer.StartAsync(...)` (line ~44):

```csharp
// TEMP smoke-test overlay — remove in Task 6
services.GetService<OverlayWindowService>()?.SetEnabled(true);
```

Start pipeline bằng UI control hiện có (chọn screen source hoặc một cửa sổ). Kiểm tra:
- Overlay window hiện, phủ đúng rect monitor (chọn screen source) hoặc cửa sổ đã chọn.
- Click chuột **xuyên qua** box overlay ra desktop/app phía sau; overlay không hiện trong taskbar/Alt-Tab.
- Demo pipeline publish frame → box dịch hiện/tắt theo frame.
Expected: đủ 3 điều trên. Nếu lệch toạ độ → debug DPI trước khi tiếp tục.

- [ ] **Step 6: Commit**

```bash
git add ReTran.App/Services/OverlayWindow/Win32AnchorTracker.cs ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs ReTran.App/ReTran.App.csproj ReTran.App/MauiProgram.cs
git commit -m "feat: add Win32 layered-window overlay host and anchor tracker"
```

---

### Task 6: UI card + i18n + DI + acceptance cuối

**Files:**
- Modify: `ReTran.App/Components/Pages/Home.razor` (card mới sau card Pipeline, ~line 65)
- Modify: `ReTran.App/Services/I18nStrings.cs` (keys `overlay.*`, VI + EN)
- Modify: `ReTran.App/MainPage.xaml.cs` (xóa dòng `SetEnabled(true)` TEMP của Task 5)
- DI đã wire ở Task 5 Step 5 — task này chỉ đụng lại `MauiProgram.cs` nếu wiring thiếu.

**Interfaces:**
- Consumes: Task 4 (`OverlayWindowService.SetEnabled/ApplyOptions/IsEnabled`), Task 2 (`Pipeline.ActiveSource` để disable toggle).
- Produces: không có (task cuối).

i18n keys (VI / EN):
| Key | VI | EN |
|---|---|---|
| `overlay.title` | Overlay trên game | Game overlay |
| `overlay.toggle` | Hiện overlay lên nguồn đang capture | Show overlay over active source |
| `overlay.mode.label` | Chế độ hiển thị | Display mode |
| `overlay.mode.subtitle` | Phủ lên chữ gốc | Over original text |
| `overlay.mode.panel` | Bảng góc màn hình | Corner panel |
| `overlay.corner.label` | Vị trí bảng | Panel corner |
| `overlay.corner.tl` / `tr` / `bl` / `br` | Trên-trái / Trên-phải / Dưới-trái / Dưới-phải | Top-left / Top-right / Bottom-left / Bottom-right |
| `overlay.font.label` | Cỡ chữ | Font size |
| `overlay.font.small` / `medium` / `large` | Nhỏ / Trung bình / Lớn | Small / Medium / Large |
| `overlay.note.fullscreen` | Game exclusive fullscreen không thể hiện overlay — chạy borderless windowed để dùng tính năng này. | Exclusive-fullscreen games cannot show the overlay — run borderless windowed to use this feature. |

- [ ] **Step 1: Dọn code tạm + xác nhận DI wiring**

Xóa dòng `SetEnabled(true)` TEMP trong `MainPage.xaml.cs` (thêm ở Task 5 Step 5). Kiểm tra `MauiProgram.cs` đã có 3 registration (`IOverlayHost → Win32OverlayHost`, `IAnchorTracker → Win32AnchorTracker`, `OverlayWindowService`). Thiếu thì bổ sung đúng khối đó.

- [ ] **Step 2: Thêm i18n keys vào I18nStrings.cs** (bảng trên, cả hai ngôn ngữ, theo pattern dictionary hiện có).

- [ ] **Step 3: Card "Overlay trên game" trong Home.razor**

`@inject ReTran_App.Services.OverlayWindow.OverlayWindowService OverlaySvc`. Card nội dung:
- Toggle `overlay.toggle` — bind `OverlaySvc.IsEnabled`; **disabled khi `Pipeline.ActiveSource is null`**; onchange → `OverlaySvc.SetEnabled(value)`.
- Select chế độ (`overlay.mode.*`) — value từ settings hiện tại; onchange → `OverlaySvc.ApplyOptions(mode: ...)`.
- Select góc (`overlay.corner.*`) — chỉ render khi mode = panel; onchange → `ApplyOptions(corner: ...)`.
- 3 nút cỡ chữ (0.85 / 1.0 / 1.25) — onchange → `ApplyOptions(fontScale: ...)`; nút active theo settings hiện tại.
- Ghi chú `overlay.note.fullscreen` (text nhỏ, style như các note hiện có).
- Đọc giá trị hiển thị từ `Settings.Load()` tại render + refresh sau mỗi lần đổi (pattern `SaveQuickDst`/`StateHasChanged` hiện có); subscribe `L.Changed` như các card khác.

- [ ] **Step 4: Build + chạy toàn bộ test**

Run: `dotnet build "ReTran Project.slnx"` && `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: PASS clean.

- [ ] **Step 5: Manual acceptance (spec §10 — bắt buộc, chạy với game thật borderless)**

1. Game borderless windowed + pipeline capture đúng cửa sổ đó → overlay phủ đúng frame, chữ bám vị trí OCR, chuột bấm xuyên qua được vào game.
2. Đổi sang panel mode + đổi góc → bảng dịch hiện đúng góc.
3. Minimize/đóng game → overlay ẩn; mở lại → hiện theo.
4. Display scaling 150% (Settings → Display) → overlay thẳng hàng với frame, không lệch.
5. OBS Browser Source vẫn nhận feed bình thường (không đổi).

- [ ] **Step 6: Commit**

```bash
git add ReTran.App/Components/Pages/Home.razor ReTran.App/Services/I18nStrings.cs ReTran.App/MainPage.xaml.cs
git commit -m "feat: add game overlay card to control page with i18n"
```
(Chỉ thêm `ReTran.App/MauiProgram.cs` vào commit nếu Step 1 phải bổ sung wiring.)
