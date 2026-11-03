# Plan 2: Menu nổi GDI+ trên desktop + RunStats + chọn nguồn capture trong menu + tray "Hiện/ẩn menu nổi"

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Một window desktop topmost vẽ bằng GDI+ (không WebView) cho phép Start/Stop pipeline, chọn nguồn capture, bật/tắt lớp phủ, đổi dst/src, chỉnh nhịp OCR/capture, gộp dòng, xem thống kê — kéo được quanh màn hình, thu gọn thành chip, **mặc định bật**, không bao giờ cướp focus/bàn phím của game. Kèm theo: `RunStats` singleton dùng chung cho Home và menu; persist bộ ba nguồn vào `settings.json`; tray thêm mục `Hiện/ẩn menu nổi`.

**Architecture:** Window menu là window thứ hai trong chính `Win32OverlayHost` (cùng class STA thread + pump + `Enqueue`, **không thêm thread**) — nhưng đăng ký một window class riêng `"ReTranMenu"` với WNDPROC riêng vì Win32 gắn wndproc theo class. Toàn bộ logic quyết định (layout, hit-test, clamp, cycle, source list) nằm trong `FloatingMenuLayout` thuần C# test được không cần pixel; vẽ nằm trong `FloatingMenuRenderer` (static internal, smoke-test kiểu `Win32OverlayHostRenderTests`); nối intent → service nằm trong `FloatingMenuService`. Z-order: window box `Show()` dùng `hWndInsertAfter = hwnd menu` thay vì `HWND_TOPMOST` tự do (spec D3).

**Tech Stack:** C# .NET 10 (MAUI Blazor Hybrid, `WindowsPackageType=None`), GDI+ (`System.Drawing.Common` đã có sẵn cho overlay host), xUnit v3. Không dependency mới, không process/thread mới.

**Spec:** `Docs/superpowers/specs/2026-11-01-floating-menu-tray-design.md` — mục G1, G2 + §6.2 D2–D11 + §7.1 + §7.3 + §10 (phần menu) + §11. **Tiền điều kiện:** Plan 1 `Docs/superpowers/plans/2026-11-02-settings-checkbox-tray.md` đã chạy xong (tray 3 mục, `AppNotifier`, `TrayIconService` tồn tại). Plan song song `2026-11-01-ocr-line-grouping` đã commit hết (kiểm tra lại bằng `git log --oneline -10` trước khi bắt đầu).

## Global Constraints

- **Không `git add -A`.** Repo có ~97 file WIP của user. Mỗi commit chỉ `git add` đúng các file của task. Không push. Branch `master`.
- **Menu không được phép kích hoạt app hay game mất foreground:** window menu mang `WS_EX_NOACTIVATE`, mọi `SetWindowPos` cho menu dùng `SWP_NOACTIVATE`, không gọi `SetForegroundWindow`/`Activate` ở bất kỳ đâu trong đường menu. Lý do: overlay tự ẩn khi app thành foreground (`Win32AnchorTracker.IsSourceForeground`) và game mất bàn phím là anti-goal (spec §4).
- **Không process mới, không thread mới.** Window menu chạy trên STA thread + pump của `Win32OverlayHost` hiện có. Redraw ≤ ~15 fps (throttle 66 ms, cùng `OverlayWindowService.DefaultRedrawInterval`) và **chỉ khi state đổi thật** — không timer redraw định kỳ.
- **Vùng ngoài panel phải click-through:** window menu lớn hơn panel một viền shadow; `WM_NCHITTEST` trả `HTTRANSPARENT` cho mọi điểm ngoài rect panel (kể cả 4 góc bo tròn), `HTCLIENT` bên trong.
- P/Invoke theo quy ước `Win32OverlayHost.NativeMethods`: `DllImport` cổ điển (không `LibraryImport`), không bật `AllowUnsafeBlocks`, struct `CharSet.Unicode`. Mọi API Win32 dùng **bản W**.
- Doc comment C# public API song ngữ Anh → Việt theo `AGENTS.md`; `internal` chỉ doc khi hành vi không hiển nhiên.
- i18n: key mới phải vào **cả** block `Vi` lẫn `En` trong `I18nStrings.cs`; test giữ chân là `I18nServiceTests.EveryKey_Exists_In_Both_Languages`. Trước khi sửa `I18nStrings.cs` đọc lại file mới nhất (plan song song có thể đã commit thêm).
- Console output mangle dấu tiếng Việt (codepage) — file trên disk đúng UTF-8; không "sửa" chữ trông như `?` trong terminal, xác minh bằng tool đọc file.
- Build chuẩn: `dotnet build "ReTran Project.slnx"` → 0 error. Test chuẩn: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<ClassName>"`. Test App nằm ở **global namespace**; test Core mang prefix `RetranCore.Tests.`.
- Không sửa `ReTran.Core`, không thêm wire RPC mới, không đụng sidecar Python (spec §3).

## Review Focus

Sáu failure mode spec ngụ ý mà không task nào tự phủ — mỗi dòng đã ghim test vào task sở hữu:

1. **Menu cướp foreground của game** → overlay tự ẩn + game mất bàn phím (anti-goal). → Task 5: `Win32MenuHostTests.MenuWindowStyles_NoActivateAndTopmost` (assert cờ window) + code review WndProc không có `SetForegroundWindow`.
2. **Box đè lên menu** (regression z-order khi thêm window thứ hai) → Task 5: `Win32MenuHostTests.BoxInsertAfter_MenusHwndWhenPresent` (thuần) — box phải insert sau hwnd menu, fallback `HWND_TOPMOST` khi menu chưa tạo.
3. **Vùng shadow/góc bo tròn ăn click của game** → Task 3: hit-test trả transparent cho margin + góc; Task 5: WndProc `WM_NCHITTEST` theo đúng layout (code review).
4. **Start với hwnd nguồn đã đóng âm thầm rơi về monitor 0** (spec D10 — hành vi tệ nhất có thể) → Task 6: `FloatingMenuServiceTests.Start_WindowSourceWithoutHwnd_ErrorsAndDoesNotStartOrMutateSettings`.
5. **Hai chỗ tính fps khác nhau** sau khi tách `RunStats` (Home giữ logic cũ + menu đọc RunStats → số khác nhau) → Task 8: xoá hẳn `_fps/_fpsFrames/_fpsTimer/TickFps` khỏi `Home.razor`, grep xác nhận không còn; Home chỉ đọc `RunStats`.
6. **Redraw storm** khi stats/overlay sự kiện dồn dập → Task 2: `RunStatsTests.Changed_Throttled`; Task 6: `FloatingMenuServiceTests.RapidStateChanges_DrawnAtMostEveryInterval` (throttle 66 ms, leading-edge).

---

### Task 1: `AppSettings` + 7 tham số mới (menu + bộ ba nguồn)

**Files:**
- Modify: `ReTran.App/Services/AppSettingsService.cs` (record `AppSettings` — thêm 7 tham số cuối sau `OcrMergeLines`)
- Test: `ReTran.Tests/AppSettingsServiceTests.cs` (thêm 2 test vào class hiện có)

**Interfaces:**
- Consumes: không.
- Produces: `AppSettings` record 18 tham số; 7 giá trị mới với default an toàn cho file cũ:

```csharp
bool FloatingMenuOn = true,
bool FloatingMenuCollapsed = false,
int? FloatingMenuX = null,   // null = tự đặt vị trí mặc định lần đầu (góc trên phải work area)
int? FloatingMenuY = null,
bool SourceUseWindow = false,
int SourceMonitor = 0,
string? SourceHwnd = null    // hex "0x…"; KHÔNG có giá trị qua lần chạy app (spec D10)
```

- [ ] **Step 1: Viết test failing** (theo đúng pattern `OcrMergeLines_Absent_DefaultsTrue` đang có trong file — STJ tôn trọng default parameter của record cho key vắng, đã kiểm chứng bằng test đó):

```csharp
[Fact]
public void MenuAndSourceParams_RoundTrip()
{
    var service = new AppSettingsService(_path);
    var custom = AppSettings.Default with
    {
        FloatingMenuOn = false,
        FloatingMenuCollapsed = true,
        FloatingMenuX = 123,
        FloatingMenuY = 456,
        SourceUseWindow = true,
        SourceMonitor = 1,
        SourceHwnd = "0x1A2B3C",
    };

    service.Save(custom);
    AppSettings loaded = service.Load();

    Assert.False(loaded.FloatingMenuOn);
    Assert.True(loaded.FloatingMenuCollapsed);
    Assert.Equal(123, loaded.FloatingMenuX);
    Assert.Equal(456, loaded.FloatingMenuY);
    Assert.True(loaded.SourceUseWindow);
    Assert.Equal(1, loaded.SourceMonitor);
    Assert.Equal("0x1A2B3C", loaded.SourceHwnd);
}

[Fact]
public void MenuAndSourceParams_Absent_DefaultsSafe()
{
    // File cũ không có 7 key mới → đúng default (menu bật, nguồn = monitor 0, hwnd null).
    Directory.CreateDirectory(_dir);
    File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
    var service = new AppSettingsService(_path);

    AppSettings settings = service.Load();

    Assert.True(settings.FloatingMenuOn);
    Assert.False(settings.FloatingMenuCollapsed);
    Assert.Null(settings.FloatingMenuX);
    Assert.Null(settings.FloatingMenuY);
    Assert.False(settings.SourceUseWindow);
    Assert.Equal(0, settings.SourceMonitor);
    Assert.Null(settings.SourceHwnd);
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error: property chưa tồn tại)

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "AppSettingsServiceTests"`

- [ ] **Step 3: Implement** — thêm 7 tham số cuối vào record + doc comment song ngữ cho từng `<param>` (giữ nguyên thứ tự, không xáo tham số cũ). Cập nhật `<summary>` class nếu nó liệt kê nhóm tham số.
- [ ] **Step 4: Chạy test — pass** + `dotnet build "ReTran Project.slnx"` 0 error.
- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/AppSettingsService.cs" "ReTran.Tests/AppSettingsServiceTests.cs"
git commit -m "feat(settings): persist floating menu state and last capture source"
```

---

### Task 2: `RunStats` — thống kê dùng chung (fps, box, dòng dịch)

**Files:**
- Create: `ReTran.App/Core/RunStats.cs`
- Test: `ReTran.Tests/RunStatsTests.cs` (global namespace)

**Interfaces:**
- Consumes: `PreviewState`, `OverlayState` (cả hai trong `ReTran_App.Core`, có event `Changed`), `IPipelineControl` (Task 6 tạo interface — **nhưng Task 2 cần nó trước**: xem ruling dưới).
- Produces:

```csharp
namespace ReTran_App.Core;

/// fps tính trên cửa sổ thời gian cố định đếm PREVIEW frame (frame.annotated) —
/// đúng nguồn Home.razor đang dùng, không phải frame translated.
public sealed class RunStats : IDisposable
{
    public RunStats(PreviewState preview, OverlayState overlay, IPipelineControl pipeline,
        Func<long>? now = null,      // mặc định Environment.TickCount64 — inject cho test
        TimeSpan? window = null);    // mặc định 1 s

    public double Fps { get; }                 // 0 khi pipeline không chạy
    public int BoxCount { get; }               // từ OverlayState.Latest
    public int TranslatedLineCount { get; }    // box có Translated không rỗng
    public event Action? Changed;              // throttle ≤ 4 lần/giây
}
```

**Ruling (ghi trong ledger khi chạy):** `IPipelineControl` được định nghĩa trong task này (file `ReTran.App/Core/IPipelineControl.cs`, cùng commit) thay vì Task 6 — vì RunStats cần subscribe pipeline state và interface phải tồn tại trước. Task 6 chỉ thêm member nếu thiếu (không thiếu: đủ với 5 thành viên dưới).

```csharp
namespace ReTran_App.Core;

/// <summary>
/// The pipeline surface the floating menu and stats consume: start/stop, source listing,
/// run state. Implemented by <see cref="PipelineController"/> so both can be faked in tests.
/// Bề mặt pipeline mà menu nổi và thống kê dùng: start/dừng, liệt kê nguồn, trạng thái chạy.
/// Do <see cref="PipelineController"/> implement để test fake được.
/// </summary>
public interface IPipelineControl
{
    bool IsRunning { get; }
    SourceSelection? ActiveSource { get; }
    Task StartAsync(SourceSelection source, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows)> ListSourcesAsync(CancellationToken ct);
    event Action? StateChanged;
    event Action<bool, string?>? PipelineStateChanged;
}
```

`PipelineController` implement `IPipelineControl` (thêm vào declaration class — mọi member đã khớp signature).

- [ ] **Step 1: Viết test failing**

```csharp
public class RunStatsTests
{
    // Fake pipeline tối thiểu: IsRunning/ActiveSource giả, 2 event raise thủ công.
    private sealed class FakePipeline : IPipelineControl { /* ... */ }

    [Fact]
    public void Fps_CountsPreviewFramesInWindow()
    {
        var preview = new PreviewState();
        long tick = 0;
        using var stats = new RunStats(preview, new OverlayState(), new FakePipeline(running: true),
            now: () => tick, window: TimeSpan.FromSeconds(1));

        for (int i = 0; i < 5; i++) preview.Publish(new PreviewFrame(i + 1, 100, 100, ""));
        tick += 1000; // cửa sổ 1 s trôi qua → tick nội bộ của RunStats tính fps
        stats.TickForTest(); // expose internal cho test (InternalsVisibleTo đã có)

        Assert.Equal(5.0, stats.Fps, 2);
    }

    [Fact]
    public void BoxAndLineCounts_FollowLatestOverlayFrame()
    {
        var overlay = new OverlayState();
        using var stats = new RunStats(new PreviewState(), overlay, new FakePipeline(running: true));
        overlay.Publish(new OverlayFrame(100, 100, DateTime.UtcNow,
            [new OverlayBox(0, 0, 1, 1, "a", "b"), new OverlayBox(0, 0, 1, 1, "c", "")]));

        Assert.Equal(2, stats.BoxCount);
        Assert.Equal(1, stats.TranslatedLineCount);
    }

    [Fact]
    public void PipelineStop_ResetsFpsAndCounts()
    {
        // publish frame + preview frames → fake pipeline IsRunning=false rồi raise StateChanged
        // (đường Stop do người dùng) → Fps/BoxCount = 0. Một case thứ hai raise
        // PipelineStateChanged(false, null) (đường dừng đột xuất) → cũng reset.
    }

    [Fact]
    public void Changed_Throttled_NotEveryFrame()
    {
        // 10 preview frame liên tiếp (clock không trôi) → số lần Changed ≤ 4
    }
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error).
- [ ] **Step 3: Implement**
  - Đếm preview frame bằng `Interlocked.Increment` khi `preview.Changed` nổ (đúng cách `Home.razor.OnPreviewChanged` đang đếm — fps của PREVIEW, không phải translated).
  - Tick nội bộ: `System.Threading.Timer` 1 s (threadpool — cùng pattern `OverlayWindowService._timer`, **không phải thread mới**), tính `fps = frames / elapsed` rồi reset counter, đúng công thức `Home.razor.TickFps`.
  - `overlay.Changed` → đọc `Latest` cập nhật BoxCount/TranslatedLineCount.
  - Reset khi pipeline dừng — **cả hai đường**: `pipeline.PipelineStateChanged(false, _)` (dừng đột xuất) VÀ `pipeline.StateChanged` với `!pipeline.IsRunning` (Stop do người dùng bấm chỉ raise `StateChanged`, không raise `PipelineStateChanged` — xem `PipelineController.StopAsync`). Reset = Fps=0, BoxCount=0, TranslatedLineCount=0.
  - Khi `now` được inject (test) thì KHÔNG tạo timer thật — test gọi `TickForTest()` trực tiếp.
  - `Changed` throttle: tối đa 1 lần mỗi 250 ms (leading-edge, không trailing timer).
  - `Dispose`: unsubscribe + dispose timer.
- [ ] **Step 4: Chạy test — pass** + build solution.
- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Core/RunStats.cs" "ReTran.App/Core/IPipelineControl.cs" "ReTran.App/Core/PipelineController.cs" "ReTran.Tests/RunStatsTests.cs"
git commit -m "feat(core): shared RunStats singleton and IPipelineControl interface"
```

---

### Task 3: `FloatingMenuLayout` — layout thuần (rows, hit-test, clamp, cycle, source list)

**Files:**
- Create: `ReTran.App/Services/FloatingMenu/FloatingMenuLayout.cs`
- Test: `ReTran.Tests/FloatingMenu/FloatingMenuLayoutTests.cs` (global namespace)

**Interfaces:**
- Consumes: `MonitorInfo`, `WindowInfo` (`ReTran_App.Core`).
- Produces (toàn bộ thuần, không Win32, không GDI+):

```csharp
namespace ReTran_App.Services.FloatingMenu;

public sealed record SourceItem(bool IsMonitor, int Id, string Label, string? HwndHex);

// Zone cho hit-test: Whole = click thường; Left/Right = hai nửa stepper (− / +).
public enum MenuZone { Whole, Left, Right }

public readonly record struct MenuHit(int RowId, MenuZone Zone);

// Snapshot trạng thái bất biến mà renderer + layout cùng đọc — service dựng mỗi lần redraw.
public sealed record MenuState(
    bool Collapsed,
    bool Running,
    string SourceLabel,          // "Màn hình 1" / "game.exe — Tít…" / "(chưa chọn cửa sổ)"
    bool OverlayOn,
    string Dst, string Src,
    int OcrFps, int CaptureFps,
    bool MergeLines,
    bool StatsOpen,
    bool SourceListOpen,
    IReadOnlyList<SourceItem> Sources,
    string? ErrorLine);

public static class FloatingMenuLayout
{
    // Hằng số hình học (pixel vật lý) — ghim ở đây để test + renderer dùng chung.
    public const int PanelWidth = 320;
    public const int ShadowMargin = 8;          // viền shadow quanh panel (vùng HTTRANSPARENT)
    public const int HeaderHeight = 36;         // dải grip kéo + nút thu gọn
    public const int RowHeight = 44;
    public const int ChipSize = 44;             // chip collapsed
    public const int StatsBlockHeight = 92;     // vùng số khi mở rộng
    public const int HintRowHeight = 18;        // dòng "áp dụng ở lần Start kế tiếp"
    public const int ErrorRowHeight = 18;
    public const int MaxSourceWindows = 8;      // giới hạn cửa sổ trong danh sách nguồn

    // Row id — ghim hằng số để host/service/test dùng chung.
    public const int RowStartStop = 0, RowSource = 1, RowOverlay = 2, RowDst = 3, RowSrc = 4,
        RowOcrFps = 5, RowCaptureFps = 6, RowMergeLines = 7, RowStats = 8, RowCloseMenu = 9,
        Chip = 10, CollapseButton = 11;
    public const int SourceItemBase = 100;      // id mục danh sách nguồn = SourceItemBase + index

    // (w,h) của WINDOW (panel + 2×ShadowMargin) theo trạng thái.
    public static (int Width, int Height) WindowSize(MenuState s);
    // Rect panel trong hệ toạ độ window (offset ShadowMargin).
    public static (int X, int Y, int W, int H) PanelRect(MenuState s);

    // Hit-test điểm TOÀN CẢNH window (kể cả margin): null = HTTRANSPARENT.
    // Collapsed: mọi điểm trong chip → (Chip, Whole).
    // Expanded: header trái = grip (không click), header phải 24×24 → CollapseButton;
    //           các row theo thứ tự Rows(s); stepper có Zone Left/Right (± 40 px mép);
    //           SourceListOpen → chèn các row nguồn sau RowSource.
    public static MenuHit? HitTest(int x, int y, MenuState s);

    // Thứ tự + sự hiện diện row theo trạng thái pipeline (spec §7.1, §10).
    public static IReadOnlyList<(int Id, string Kind)> Rows(MenuState s);
    // Kind: "startstop" | "source" | "overlay" | "cycle" | "stepper" | "switch" | "stats" | "close" | "sourceitem"

    public static int ClampFps(int value) => Math.Clamp(value, 1, 60);
    // Cycle wrap: current không có trong danh sách → trả index 0.
    public static int CycleIndex(IReadOnlyList<string> codes, string current, bool forward);

    // Clamp vị trí window (kể cả margin) vào work area monitor CHỨA điểm neo;
    // monitors rỗng → clamp vào origin (0,0)..(1920,1080) fallback.
    public static (int X, int Y) ClampPosition(int x, int y, int windowW, int windowH,
        IReadOnlyList<(int Left, int Top, int Right, int Bottom)> workAreas);

    // Dựng danh sách nguồn: monitor trước (label "Màn hình {Id+1}"), cửa sổ sau
    // (tối đa MaxSourceWindows, label "{Process} — {Title}" cắt 40 ký tự + "…"), thứ tự ổn định.
    public static IReadOnlyList<SourceItem> BuildSources(IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<WindowInfo> windows);

    // Vị trí mặc định lần đầu: góc trên phải work area chính, cách mép 16 px (spec §7.1).
    public static (int X, int Y) DefaultPosition(int windowW, int windowH,
        (int Left, int Top, int Right, int Bottom) primaryWorkArea);
}
```

- [ ] **Step 1: Viết test failing** — phủ đủ mục §10 spec:

```csharp
public class FloatingMenuLayoutTests
{
    private static MenuState State(bool collapsed = false, bool running = true,
        bool statsOpen = false, bool sourceListOpen = false,
        IReadOnlyList<SourceItem>? sources = null) => new(
        Collapsed: collapsed, Running: running, SourceLabel: "Màn hình 1",
        OverlayOn: true, Dst: "vi", Src: "auto", OcrFps: 3, CaptureFps: 10,
        MergeLines: true, StatsOpen: statsOpen, SourceListOpen: sourceListOpen,
        Sources: sources ?? (IReadOnlyList<SourceItem>)Array.Empty<SourceItem>(), ErrorLine: null);

    [Fact] public void WindowSize_Collapsed_IsChipPlusMargins();      // (ChipSize+16, ChipSize+16)
    [Fact] public void WindowSize_Expanded_HeaderRowsHintClose();     // chiều cao = header + 9 row + hint + close… theo Rows()
    [Fact] public void WindowSize_StatsOpen_AddsStatsBlock();
    [Fact] public void HitTest_OutsidePanel_ReturnsNull();            // margin + góc bo tròn → HTTRANSPARENT (Review Focus #3)
    [Fact] public void HitTest_Collapsed_ChipZone();
    [Fact] public void HitTest_Expanded_RowOrderAndIds();             // điểm giữa từng row → đúng RowId
    [Fact] public void HitTest_Stepper_LeftRightZones();              // 20 px mép trái/phải → Left/Right
    [Fact] public void HitTest_SourceList_InsertedAfterSourceRow();   // SourceItemBase+i theo vị trí
    [Theory]
    [InlineData(0, 1)] [InlineData(61, 60)] [InlineData(30, 30)]
    public void ClampFps_Bounds(int input, int expected);
    [Fact] public void CycleIndex_WrapsBothWays_AndUnknownCurrent();
    [Fact] public void Rows_OverlayAlwaysPresent_StartStopLabelSwitchesWithRunning(); // "sự hiện diện theo trạng thái pipeline"
    [Fact] public void BuildSources_MonitorsFirst_TruncatesLongTitles_StableOrder_LimitsWindows();
    [Fact] public void ClampPosition_ClampsIntoContainingMonitor_EdgeAndOffscreen();
    [Fact] public void DefaultPosition_TopRightMinus16();
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error).
- [ ] **Step 3: Implement** theo interface. Cắt tên cửa sổ: `label.Length > 40 ? label[..39] + "…" : label` (tránh cắt giữa surrogate pair — dùng helper kiểu `BoxAnnotationStyle.Prefix`).
- [ ] **Step 4: Chạy test — pass** + build solution.
- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/FloatingMenu/FloatingMenuLayout.cs" "ReTran.Tests/FloatingMenu/FloatingMenuLayoutTests.cs"
git commit -m "feat(menu): pure floating menu layout — rows, hit-test, clamps and source list"
```

---

### Task 4: `FloatingMenuRenderer` — vẽ panel bằng GDI+

**Files:**
- Create: `ReTran.App/Services/FloatingMenu/FloatingMenuRenderer.cs`
- Test: `ReTran.Tests/FloatingMenu/FloatingMenuRendererTests.cs` (global namespace, theo khuôn `ReTran.Tests/Overlay/Win32OverlayHostRenderTests.cs`)

**Interfaces:**
- Consumes: `FloatingMenuLayout`, `MenuState` (Task 3); token màu/font từ `BoxAnnotationStyle` (`PillFill`, `PillBorder`, `TextColor`, `TickColor`, `GetFont`, `BuildRoundedRect`, `CreateTextFormat`).
- Produces:

```csharp
namespace ReTran_App.Services.FloatingMenu;

public static class FloatingMenuRenderer
{
    // Vẽ panel (hoặc chip) lên surface có sẵn, toạ độ WINDOW (kể cả ShadowMargin).
    // Panel: nền bo góc rgba(5,12,14,.86) + viền PillBorder + shadow offset 3 px
    //         Color.FromArgb(90,0,0,0) phía sau; header có 3 chấm grip + nút thu gọn (icon "—");
    //         row: nhãn trái 14 px TextColor, giá trị phải — switch pill (bật: nền TickColor),
    //         stepper "− {v} +" với hai vùng nhấn, cycle "{code}" in hoa, source list check mục đang chọn;
    //         stats block 5 số 13 px; hint row 11 px mờ; error row 11 px Color.FromArgb(255,240,90,90);
    //         control disabled (overlay khi !Running) vẽ alpha 40%.
    // Chip: nền bo góc + chữ "R" 18 px TickColor giữa chip.
    internal static void DrawMenu(SD.Graphics g, MenuState s, int windowW, int windowH);
}
```

- [ ] **Step 1: Viết test failing** (smoke, KHÔNG pixel-perfect — cùng tinh thần `Win32OverlayHostRenderTests`):

```csharp
public class FloatingMenuRendererTests
{
    private static Bitmap Render(MenuState s)
    {
        var (w, h) = FloatingMenuLayout.WindowSize(s);
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        FloatingMenuRenderer.DrawMenu(g, s, w, h);
        return bitmap;
    }

    [Fact] public void Expanded_DarkPanelCoversMostOfWindow();   // > 60% pixel tối trong rect panel (giống IsDark của render test overlay)
    [Fact] public void Expanded_MarginIsTransparent();           // pixel ở ShadowMargin alpha = 0 (trừ vùng shadow offset — đo tại góc trái trên ngoài margin)
    [Fact] public void Collapsed_ChipDrawn_WithAccentGlyph();    // có pixel teal-ish quanh tâm chip (giống IsTeal)
    [Fact] public void StatsOpen_DrawsStatsBlock();              // bitmap cao hơn WindowSize khi statsOpen=false, không crash
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error).
- [ ] **Step 3: Implement** — mọi hình học lấy từ `FloatingMenuLayout` (không hardcode lại số); font qua `BoxAnnotationStyle.GetFont`; không tạo Font mới ngoài cache.
- [ ] **Step 4: Chạy test — pass** + build solution.
- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/FloatingMenu/FloatingMenuRenderer.cs" "ReTran.Tests/FloatingMenu/FloatingMenuRendererTests.cs"
git commit -m "feat(menu): GDI+ renderer for the floating menu panel and chip"
```

---

### Task 5: Window menu trong `Win32OverlayHost` + z-order box → menu

**Files:**
- Create: `ReTran.App/Services/FloatingMenu/IMenuWindowHost.cs`
- Modify: `ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs` (window thứ hai, WNDPROC riêng, drag/click, z-order)
- Test: `ReTran.Tests/FloatingMenu/Win32MenuHostTests.cs` (global namespace; phần Win32 real guarded `OperatingSystem.IsWindows()`)

**Interfaces:**
- Consumes: `FloatingMenuLayout` (hằng số margin/panel rect cho hit-test `WM_NCHITTEST`).
- Produces:

```csharp
namespace ReTran_App.Services.FloatingMenu;

/// <summary>
/// Window hosting the floating menu: layered, topmost, NOT click-through and non-activating.
/// Same worker thread/pump as the overlay box window. Lifetime is tied to the host's
/// IOverlayHost disposal (do not dispose twice).
/// Cửa sổ chứa menu nổi: layered, topmost, KHÔNG click-through và không kích hoạt.
/// Cùng worker thread/pump với cửa sổ box overlay. Tuổi thọ gắn với Dispose của IOverlayHost.
/// </summary>
public interface IMenuWindowHost
{
    bool MenuCreated { get; }               // window menu đã tạo chưa (KHÔNG phải IsCreated của IOverlayHost — host implement cả hai interface, ngữ nghĩa khác nhau)
    nint MenuHwnd { get; }                  // 0 khi chưa tạo — dùng cho z-order box
    void CreateMenu();                      // idempotent; tự khởi worker thread nếu chưa chạy
    void ShowMenu(int x, int y, int width, int height); // pixel vật lý, SWP_NOACTIVATE
    void HideMenu();
    void DrawMenu(System.Drawing.Bitmap bitmap); // 32bpp premultiplied, đúng cỡ window
    event Action<int, int>? PanelClicked;   // (x,y) TOÁN HỆ PANEL (gốc = góc trái trên panel), chỉ khi click không kéo
    event Action<int, int>? DragFinished;   // (X,Y) desktop vật lý mới sau khi kéo xong
}
```

**Thay đổi trong `Win32OverlayHost`:**
- Trích `EnsureWorkerStarted(ManualResetEventSlim done)` dùng chung cho `Create()` và `CreateMenu()` (thread chỉ start 1 lần).
- Class `"ReTranMenu"`: styles `WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE` (**KHÔNG** `WS_EX_TRANSPARENT` — menu phải nhận click), `WS_POPUP`. WNDPROC là **instance method** (cần state drag) giữ trong field rooted `MenuWndProcDelegate`.
- WndProc menu: `WM_ERASEBKGND → 0`; `WM_NCHITTEST`: điểm ngoài `PanelRect` (offset `ShadowMargin`, theo `FloatingMenuLayout`) → `HTTRANSPARENT` (1), trong → `HTCLIENT` (1); `WM_LBUTTONDOWN`: ghi điểm xuống + offset cửa sổ; `WM_MOUSEMOVE` khi đang nhấn: lệch > 4 px → kéo bằng `SetWindowPos(_menuHwnd, HWND_TOPMOST, newX, newY, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE)`; `WM_LBUTTONUP`: đã kéo → raise `DragFinished(X, Y)`; chưa kéo và điểm trong panel → raise `PanelClicked(x - margin, y - margin)`.
- **Z-order box (D3):** `Show()` đổi `HWND_TOPMOST` thành:

```csharp
internal static nint BoxInsertAfter(nint menuHwnd) =>
    menuHwnd != 0 ? menuHwnd : NativeMethods.HWND_TOPMOST;
```

  (`SetWindowPos(_hwnd, BoxInsertAfter(_menuHwnd), …)` — cả hai window đều topmost nên box luôn dưới menu của chính ReTran.)
- `PushBitmap` refactor nhận tham số `nint hwnd` (đang dùng field `_hwnd` cứng); `DrawMenu(bitmap)` gọi cùng đường push với `_menuHwnd`.
- `Dispose`: destroy cả hai hwnd.
- NativeMethods thêm: `HTCLIENT = 1`, `HTTRANSPARENT = -1`, `WM_NCHITTEST = 0x0084`, `WM_LBUTTONDOWN = 0x0201`, `WM_MOUSEMOVE = 0x0200`, `WM_LBUTTONUP = 0x0202`, `SWP_NOSIZE = 0x0001`.

- [ ] **Step 1: Viết test failing**

```csharp
public class Win32MenuHostTests
{
    [Fact] public void BoxInsertAfter_MenusHwndWhenPresent();        // menuHwnd=42 → 42; 0 → HWND_TOPMOST (Review Focus #2)
    [Fact] public void MenuWindowStyles_NoActivateAndTopmost();      // helper thuần MenuWindowExStyles() trả uint → assert có WS_EX_TOPMOST|WS_EX_NOACTIVATE, KHÔNG có WS_EX_TRANSPARENT (Review Focus #1)

    [Fact]
    public void CreateShowDrawHideDispose_DoesNotThrow()             // smoke Win32 real, guarded
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new Win32OverlayHost();
        host.CreateMenu();
        Assert.True(host.MenuCreated); // IMenuWindowHost.MenuCreated
        host.ShowMenu(100, 100, 344, 500);
        using var bmp = new System.Drawing.Bitmap(344, 500, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        host.DrawMenu(bmp);
        host.HideMenu();
        host.Dispose();
    }
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error).
- [ ] **Step 3: Implement** theo mô tả trên. Giữ `IOverlayHost` KHÔNG đổi (fake trong `OverlayWindowServiceTests` không phải cập nhật — khác với hướng "mở rộng IOverlayHost" ở spec D2: interface menu tách riêng, cùng instance implement cả hai).
- [ ] **Step 4: Chạy test — pass** + build solution + chạy lại `--filter-class "OverlayWindowServiceTests"` (khuyến nghị bắt buộc vì đụng host chung).
- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/FloatingMenu/IMenuWindowHost.cs" "ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs" "ReTran.Tests/FloatingMenu/Win32MenuHostTests.cs"
git commit -m "feat(menu): second Win32 window for the floating menu with drag, hit-test and box z-order"
```

---

### Task 6: `FloatingMenuService` — nối intent → settings/overlay/pipeline

**Files:**
- Create: `ReTran.App/Services/FloatingMenu/FloatingMenuService.cs`
- Modify: `ReTran.App/Services/OverlayWindow/OverlayWindowService.cs` (implement interface mới, 1 dòng declaration)
- Create: `ReTran.App/Services/OverlayWindow/IOverlayToggle.cs`
- Test: `ReTran.Tests/FloatingMenu/FloatingMenuServiceTests.cs` (global namespace)

**Interfaces:**
- Consumes: `AppSettingsService` (concrete, temp path trong test), `IPipelineControl` + `RunStats` (Task 2 — dùng **thật** trong test với `PreviewState`/`OverlayState` thật), `IMenuWindowHost` (fake), `AppNotifier` (Plan 1 Task 6), `I18nService`, `FloatingMenuLayout`/`FloatingMenuRenderer` (Task 3–4).
- Produces:

```csharp
namespace ReTran_App.Services.OverlayWindow;
public interface IOverlayToggle
{
    bool IsEnabled { get; }
    void SetEnabled(bool on);
}
// OverlayWindowService thêm ": IOverlayToggle" vào declaration — không code mới.

namespace ReTran_App.Services.FloatingMenu;

public sealed class FloatingMenuService : IDisposable
{
    public FloatingMenuService(AppSettingsService settings, IOverlayToggle overlay, IPipelineControl pipeline,
        IMenuWindowHost host, AppNotifier notifier, RunStats stats, I18nService L,
        ILogger<FloatingMenuService> log, TimeSpan? redrawInterval = null); // default 66 ms

    public bool IsVisible { get; }
    public void Start();                    // start-up: CreateMenu + Show nếu FloatingMenuOn (vị trí persisted hoặc DefaultPosition)
    public void SetEnabled(bool on);        // persist FloatingMenuOn + show/hide — dùng bởi switch app VÀ mục tray
    public bool ToggleVisible();            // cho tray "Hiện/ẩn menu nổi": trả trạng thái mới

    // Intent (gọi từ OnRowClicked / Start button):
    public Task StartPipelineAsync();       // D10: SourceUseWindow && !SourceHwnd → error line + notifier, KHÔNG gọi StartAsync, KHÔNG đổi settings
    public Task StopPipelineAsync();
    public void ToggleOverlay();            // no-op (không báo lỗi) khi pipeline chưa chạy — control disabled theo spec §7.1
    public void CycleDst(bool forward);     // ghi settings ngay; áp dụng ở Start kế tiếp
    public void CycleSrc(bool forward);
    public void StepOcrFps(int delta);      // clamp 1..60 (FloatingMenuLayout.ClampFps)
    public void StepCaptureFps(int delta);
    public void ToggleMergeLines();
    public void ToggleStats();
    public Task OpenSourceListAsync();      // ListSourcesAsync CHỈ ở đây (spec D11), BuildSources, mở list
    public void SelectSource(SourceItem item); // persist bộ ba ngay; không Stop hộ pipeline đang chạy
    public void CloseMenu();                // "Tắt menu nổi": SetEnabled(false)

    // Từ host events:
    // NOTE toạ độ: host raise PanelClicked theo hệ PANEL (gốc = góc trái trên panel), còn
    // FloatingMenuLayout.HitTest nhận toạ độ WINDOW (kể cả ShadowMargin) → service cộng
    // ShadowMargin vào cả x, y trước khi gọi HitTest.
    public void OnPanelClicked(int x, int y);  // HitTest → dispatch intent; Chip → expand (persist Collapsed=false)
    public void OnDragFinished(int x, int y);  // ClampPosition + persist X/Y
}
```

**Danh sách cycle:** src = `["auto","en","ja","ko","zh","ru","fr","de","es"]`, dst = `["vi","en","ja","ko","zh","ru","fr","de","es"]` — **đúng** `SrcCodes`/`DstCodes` trong `SettingsPanel.razor:302-303`. Ghim làm hằng số static trong service (tránh parse Razor).

**Redraw:** mọi intent thành công → dựng `MenuState` mới (labels qua `L[...]`, stats từ `RunStats`) → render bitmap → `host.DrawMenu`, throttle leading-edge 66 ms. Subscribe: `pipeline.StateChanged`, `pipeline.PipelineStateChanged`, `settings.Changed` (overlay toggle từ tray/home persist `OverlayOnGame` → event này), `stats.Changed`. Không timer.

**Error line:** fail của intent → `MenuState.ErrorLine = L["menu.error.*"]` + `notifier.Notify(same, ToastSeverity.Error)`; xoá khi intent kế tiếp thành công.

- [ ] **Step 1: Viết test failing** (fake host ghi log DrawMenu/ShowMenu; settings thật temp path; pipeline fake; notifier thật + `FakeTrayHost` kiểu Plan 1 Task 6 để bắt balloon):

```csharp
public class FloatingMenuServiceTests
{
    [Fact] public void Start_MonitorSource_StartsPipelineWithPersistedTriple();        // SourceSelection(false, monitor, null) đúng settings
    [Fact] public void Start_WindowSourceWithoutHwnd_ErrorsAndDoesNotStartOrMutateSettings(); // Review Focus #4: fake pipeline không nhận StartAsync; settings giữ nguyên; ErrorLine set + balloon nổ
    [Fact] public void Start_RpcError_SetsErrorLineAndNotifies();                      // fake ném RpcException("…ocr-model-missing…") → error line, không crash
    [Fact] public void ToggleOverlay_WhileRunning_TogglesService();
    [Fact] public void ToggleOverlay_WhileStopped_IsNoOp();                             // spec §7.1: control disabled
    [Fact] public void CycleDst_WrapsAndPersistsImmediately();                          // save settings ngay; value wrap cuối → đầu
    [Fact] public void StepFps_ClampsAtBounds();                                        // 60 + 1 → 60; 1 − 1 → 1
    [Fact] public void OpenSourceList_CallsListSourcesOnce_AndBuildsItems();            // fake ListSourcesAsync đếm call = 1; Sources theo BuildSources
    [Fact] public void SelectSource_Window_PersistsTriple();                            // UseWindow=true, Hwnd=item.HwndHex
    [Fact] public void CloseMenu_HidesAndPersistsOff();                                 // FloatingMenuOn=false + host.HideMenu
    [Fact] public void RapidStateChanges_DrawnAtMostEveryInterval();                    // Review Focus #6: 5 intent liên tiếp (redrawInterval lớn) → host.DrawMenu ≤ 2 lần
    [Fact] public void OnPanelClicked_Chip_ExpandsAndPersists();
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error).
- [ ] **Step 3: Implement** theo interface. `StartPipelineAsync` catch `RpcException` (namespace `ReTran_App.Core`) + `OperationCanceledException`; dùng `PipelineController.IsOcrModelMissing(ex)` để chọn message thân thiện.
- [ ] **Step 4: Chạy test — pass** + build solution.
- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/FloatingMenu/FloatingMenuService.cs" "ReTran.App/Services/OverlayWindow/IOverlayToggle.cs" "ReTran.App/Services/OverlayWindow/OverlayWindowService.cs" "ReTran.Tests/FloatingMenu/FloatingMenuServiceTests.cs"
git commit -m "feat(menu): floating menu service wiring intents to settings, overlay and pipeline"
```

---

### Task 7: Tray "Hiện/ẩn menu nổi" + i18n keys (menu + tray)

**Files:**
- Modify: `ReTran.App/Services/Tray/TrayMenuModel.cs`, `ReTran.App/Services/Tray/TrayIconService.cs`, `ReTran.App/Services/Tray/AppTrayActions.cs` (tất cả do Plan 1 tạo — **đọc lại trước khi sửa**)
- Modify: `ReTran.App/Services/I18nStrings.cs` (**task nhạy cảm xung đột** — đọc lại file mới nhất trước)
- Test: modify `ReTran.Tests/Tray/TrayMenuModelTests.cs`, `ReTran.Tests/Tray/TrayIconServiceTests.cs`; i18n giữ chân bằng `I18nServiceTests`

**Interfaces:**
- Consumes: `FloatingMenuService` (Task 6), `TrayCommandId`/`TrayMenuModel`/`ITrayActions` (Plan 1).
- Produces:

```csharp
// TrayCommandId thêm member (giữa OpenApp và ToggleOverlay — đúng thứ tự spec G3):
public enum TrayCommandId { OpenApp, ShowHideMenu, ToggleOverlay, Exit }

// TrayMenuModel.Build đổi signature (4 mục):
public static IReadOnlyList<TrayCommand> Build(string openLabel, string showHideMenuLabel,
    string overlayLabel, string exitLabel, bool menuVisible, bool overlayOn);
// Chỉ ShowHideMenu và ToggleOverlay mang IsChecked.

// ITrayActions thêm:
bool MenuVisible { get; }
void SetMenuVisible(bool on);   // AppTrayActions → FloatingMenuService.SetEnabled(on)
```

- [ ] **Step 1: Kiểm tra xung đột trước khi sửa `I18nStrings.cs`** — `git status --short "ReTran.App/Services/I18nStrings.cs"` + đọc lại file. Nếu phiên khác đang edit → chờ.
- [ ] **Step 2: Viết test failing** — cập nhật `TrayMenuModelTests.Build_FixedOrder_OnlyOverlayChecked` thành thứ tự 4 mục + check state của `ShowHideMenu`; thêm case vào `TrayIconServiceTests`: `Selected_ShowHideMenu_TogglesAction` (raise → `actions.SetMenuVisible(!visible)`), `BuildMenu_ReflectsMenuVisibility`.
- [ ] **Step 3: Chạy test — xác nhận fail**.
- [ ] **Step 4: Implement** — `TrayIconService` xử lý `ShowHideMenu` → `actions.SetMenuVisible(!actions.MenuVisible)`; `AppTrayActions` nối vào `FloatingMenuService`.
- [ ] **Step 5: Thêm i18n keys — block `Vi`:**

```csharp
["tray.menu.showHideMenu"] = "Hiện/ẩn menu nổi",
["menu.row.start"] = "Bắt đầu",
["menu.row.stop"] = "Dừng",
["menu.row.source"] = "Nguồn",
["menu.row.overlay"] = "Lớp phủ",
["menu.row.dst"] = "Dịch sang",
["menu.row.src"] = "Ngôn ngữ nguồn",
["menu.row.ocrFps"] = "Nhịp OCR",
["menu.row.captureFps"] = "Nhịp chụp",
["menu.row.mergeLines"] = "Gộp nhiều dòng",
["menu.row.stats"] = "Thống kê",
["menu.row.closeMenu"] = "Tắt menu nổi",
["menu.note.nextStart"] = "Ngôn ngữ / nhịp / nguồn áp dụng ở lần Start kế tiếp.",
["menu.error.noWindow"] = "Chưa chọn cửa sổ nguồn — mở hàng Nguồn để chọn.",
["menu.error.startFailed"] = "Không thể bắt đầu: {0}",
["menu.source.monitor"] = "Màn hình {0}",
["menu.stats.fps"] = "FPS",
["menu.stats.boxes"] = "Box",
["menu.stats.lines"] = "Dòng dịch",
```

**block `En`:**

```csharp
["tray.menu.showHideMenu"] = "Show/hide floating menu",
["menu.row.start"] = "Start",
["menu.row.stop"] = "Stop",
["menu.row.source"] = "Source",
["menu.row.overlay"] = "Overlay",
["menu.row.dst"] = "Translate to",
["menu.row.src"] = "Source language",
["menu.row.ocrFps"] = "OCR rhythm",
["menu.row.captureFps"] = "Capture rhythm",
["menu.row.mergeLines"] = "Merge lines",
["menu.row.stats"] = "Stats",
["menu.row.closeMenu"] = "Turn off floating menu",
["menu.note.nextStart"] = "Language / rhythm / source apply on the next start.",
["menu.error.noWindow"] = "No source window selected — open the Source row to pick one.",
["menu.error.startFailed"] = "Could not start: {0}",
["menu.source.monitor"] = "Monitor {0}",
["menu.stats.fps"] = "FPS",
["menu.stats.boxes"] = "Boxes",
["menu.stats.lines"] = "Translated lines",
```

- [ ] **Step 6: Chạy test** — `--filter-class "TrayMenuModelTests"`, `"TrayIconServiceTests"`, `"I18nServiceTests"` → PASS toàn bộ.
- [ ] **Step 7: Commit**

```bash
git add "ReTran.App/Services/Tray/TrayMenuModel.cs" "ReTran.App/Services/Tray/TrayIconService.cs" "ReTran.App/Services/Tray/AppTrayActions.cs" "ReTran.App/Services/I18nStrings.cs" "ReTran.Tests/Tray/TrayMenuModelTests.cs" "ReTran.Tests/Tray/TrayIconServiceTests.cs"
git commit -m "feat(tray): show/hide floating menu item + menu i18n strings (vi + en)"
```

---

### Task 8: `Home.razor` — switch menu nổi + đọc `RunStats` (bỏ logic fps riêng)

**Files:**
- Modify: `ReTran.App/Components/Pages/Home.razor`

**Interfaces:**
- Consumes: `RunStats` (Task 2), `FloatingMenuService` (Task 6), i18n keys (Task 7 — nếu chạy task này sau Task 7; nếu key chưa có, `L[...]` trả key, vẫn render).
- Produces: không interface mới.

- [ ] **Step 1: Xoá logic stats riêng** (Review Focus #5) — xoá các field `_fps`, `_fpsFrames`, `_fpsWindowStart`, `_fpsTimer`, `_boxCount`, `_translatedCount`; xoá `TickFps()`; trong `OnPreviewChanged` bỏ `Interlocked.Increment(ref _fpsFrames)` (giữ phần render preview); xoá `OnOverlayChanged` nếu chỉ còn đếm box/dòng; xoá lifecycle của `_fpsTimer` (nơi tạo/dispose — đọc kỹ `OnInitialized`/`Dispose`). Sau đó chạy: `Select-String -Path "ReTran.App/Components/Pages/Home.razor" -Pattern "_fpsFrames|TickFps"` → **0 match**.
- [ ] **Step 2: Đọc từ `RunStats`** — inject `RunStats` (theo style inject hiện có của trang); subscribe `Stats.Changed += () => _ = InvokeAsync(StateHasChanged)` trong `OnInitialized`, unsubscribe trong `Dispose`; `FpsText`/`BoxText`/`LinesText` đọc `Stats.Fps`/`Stats.BoxCount`/`Stats.TranslatedLineCount` (giữ nguyên format `Num(...)` + i18n unit đang có).
- [ ] **Step 3: Switch menu nổi** — cạnh switch overlay hiện có (~dòng 195, đọc markup thật): thêm `_menuOn` init từ `SettingsStore.Load().FloatingMenuOn`, handler gọi `FloatingMenuService.SetEnabled(_menuOn)` (inject service), label dùng key i18n mới — **thêm vào task này** trong `I18nStrings.cs`: block `Vi` `["home.switch.floatingMenu"] = "Menu nổi"`, block `En` `["home.switch.floatingMenu"] = "Floating menu"` (chạy lại `I18nServiceTests` sau khi thêm).
- [ ] **Step 4: Build + test toàn bộ** — `dotnet build "ReTran Project.slnx"` 0 error; `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` PASS.
- [ ] **Step 5: Smoke thủ công — DEFERRED TO CONTROLLER** (subagent không nhìn được UI desktop): chạy app, xác nhận rail thống kê vẫn hiện fps/box/dòng khi pipeline chạy; switch menu nổi bật/tắt làm window menu hiện/ẩn. Ghi "deferred" trong report.
- [ ] **Step 6: Commit**

```bash
git add "ReTran.App/Components/Pages/Home.razor" "ReTran.App/Services/I18nStrings.cs"
git commit -m "feat(home): floating menu switch and shared RunStats readout (drop local fps logic)"
```

---

### Task 9: Wiring DI + start-up + ghi nhận perf + smoke cuối

**Files:**
- Modify: `ReTran.App/MauiProgram.cs`
- Modify: `Docs/performance.md`

**Interfaces:**
- Consumes: toàn bộ Task 1–8.
- Produces: app chạy thật có menu nổi; không interface mới.

- [ ] **Step 1: Đăng ký DI** trong `CreateMauiApp`, cạnh khối overlay hiện có (~dòng 36–41):

```csharp
builder.Services.AddSingleton<RunStats>();
// Win32OverlayHost đã đăng ký là IOverlayHost — trỏ cùng instance cho menu:
builder.Services.AddSingleton<IMenuWindowHost>(sp => sp.GetRequiredService<Win32OverlayHost>());
builder.Services.AddSingleton<IOverlayToggle>(sp => sp.GetRequiredService<OverlayWindowService>());
builder.Services.AddSingleton<FloatingMenuService>();
```

`AppTrayActions` (Plan 1) thêm tham số `FloatingMenuService` vào constructor — cập nhật nơi đăng ký nếu DI cần.

- [ ] **Step 2: Start-up** — trong hook `OnWindowCreated` mà Plan 1 Task 9 đã thêm (chỉ window chính), sau `TrayIconService.Start()`:

```csharp
App.Current.Services.GetRequiredService<FloatingMenuService>().Start();
```

Đọc lại `MauiProgram.cs` mới nhất trước khi sửa (Plan 1 có thể đã commit thay đổi).

- [ ] **Step 3: Build + test toàn bộ** — 0 error; full test PASS.
- [ ] **Step 4: Smoke thủ công — DEFERRED TO CONTROLLER, checklist bắt buộc:**

1. Chạy app → menu nổi hiện ở góc trên phải (mặc định bật), kéo bằng grip quanh màn hình, thả → vị trí giữ sau khi restart app (kiểm tra `settings.json`).
2. Game borderless đang chạy: click vào menu **không** làm game mất focus; box overlay vẫn đè đúng nguồn; menu luôn trên box của ReTran.
3. Thu gọn thành chip → click chip mở lại; "Tắt menu nổi" trong menu → biến mất; bật lại bằng switch trong app; bật lại bằng tray `Hiện/ẩn menu nổi`.
4. Chọn nguồn là cửa sổ game trong menu → Start chạy đúng nguồn đó; Stop rồi bấm Start khi chưa chọn cửa sổ (gỡ hwnd) → lỗi hiện ở hàng cuối panel + bubble (app đang ẩn).
5. Đổi dst/src/nhịp từ menu khi pipeline chạy → hint "áp dụng ở lần Start kế tiếp" hiện; Start lại → giá trị mới có hiệu lực.
6. Thống kê trong menu khớp số rail trong app (fps/box/dòng) — không còn hai số khác nhau.
7. Kéo menu ra mép màn hình / monitor phụ → clamp đúng work area, không rơi ra ngoài.

- [ ] **Step 5: Ghi nhận perf vào `Docs/performance.md`** — mục ngắn: menu = 1 layered window thứ hai trên STA thread overlay hiện có (không thread/process mới); bitmap ~344×~520 px ≈ 0,7 MB; redraw chỉ khi state đổi, throttle 66 ms; `ListSourcesAsync` chỉ khi mở hàng nguồn; không timer re-assert z-order. Ngân sách AGENTS.md (UI/overlay ≤ 10–15 fps) không được xấu đi.
- [ ] **Step 6: Commit**

```bash
git add "ReTran.App/MauiProgram.cs" "Docs/performance.md"
git commit -m "feat(menu): wire floating menu into DI, startup and performance notes"
```
