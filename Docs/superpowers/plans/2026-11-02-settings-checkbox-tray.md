# Plan 1: Sửa hàng checkbox Settings + System Tray (icon, X → ẩn xuống tray, thoát sạch) + bubble lỗi

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bấm X trên cửa sổ app ẩn xuống system tray (pipeline vẫn chạy), tray icon có menu ngữ cảnh 3 mục (Mở ReTran · Lớp phủ · Thoát), lỗi từ tray hiện bằng bubble của Windows, và sửa hàng checkbox trong sheet Settings đang bị tách 2–3 hàng.

**Architecture:** Tray đi đường `Shell_NotifyIcon` P/Invoke với một message-only window trên UI thread (không thêm process/thread, không thêm dependency). Toàn bộ logic có thể test được (menu model, flags/dispatch Win32, policy đóng cửa sổ, routing lỗi) tách thành hàm thuần; phần glue Win32 (`Win32TrayHost`) mỏng và chỉ smoke-test. `AppWindow` của MAUI Blazor Hybrid hook qua `ConfigureLifecycleEvents` → `OnWindowCreated`.

**Tech Stack:** C# .NET 10 (MAUI Blazor Hybrid, `WindowsPackageType=None`), WindowsAppSDK 1.7.250909003, GDI+ (`System.Drawing.Common` đã có sẵn cho overlay host), xUnit v3, Tailwind v4 (chỉ sửa CSS).

**Spec:** `Docs/superpowers/specs/2026-11-01-floating-menu-tray-design.md` — mục G3, G4, G5 + §6.2 D7/D8/D12/D13 + §7.2 + §10. Plan này **không** bao gồm menu nổi (Plan 2) và không thêm field mới vào `AppSettings`.

## Global Constraints

- **Không `git add -A`.** Repo có ~97 file WIP của user. Mỗi commit chỉ `git add` đúng các file của task. Không push. Branch `master`.
- **Không đụng `wwwroot/build` bằng tay** (output WIP). Task 1 rebuild qua `npm run build` nhưng **không commit** file output — chỉ commit `deck.css`.
- **Plan song song** `Docs/superpowers/plans/2026-11-01-ocr-line-grouping.md` (Task 3–7 chưa làm) sẽ sửa `AppSettingsService.cs`, `PipelineController.cs`, `OverlayState.cs`, `SettingsPanel.razor`, `I18nStrings.cs`, `Services/OverlayWindow/OverlayLayout.cs`, `Win32OverlayHost.cs`. Plan này chỉ chạm `I18nStrings.cs` (Task 8) — trước khi sửa phải đọc lại file mới nhất và kiểm tra phiên kia không đang edit; nếu đang edit thì chờ.
- Build chuẩn: `dotnet build "ReTran Project.slnx"` → expected 0 error (~34 warning `xUnit1051` có sẵn).
- Test chuẩn: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<ClassName>"`. Test App nằm ở **global namespace** (không prefix); test Core mang prefix `RetranCore.Tests.`.
- Không process mới, không thread mới cho tray: message-only window tạo trên UI thread, dùng pump của WinUI.
- P/Invoke theo quy ước `Win32OverlayHost.NativeMethods`: `DllImport` cổ điển (không `LibraryImport`), không bật `AllowUnsafeBlocks`, struct `CharSet.Unicode`. Mọi API Win32 dùng **bản W** (Unicode) — nhãn tiếng Việt phải sống qua mọi system locale.
- Doc comment C# public API song ngữ Anh → Việt theo `AGENTS.md`; `internal` chỉ doc khi hành vi không hiển nhiên.
- i18n: key mới phải vào **cả** `I18nStrings.Vi` lẫn `.En`; test giữ chân là `I18nServiceTests.EveryKey_Exists_In_Both_Languages`.

## Review Focus

Năm failure mode spec ngụ ý mà không task nào tự phủ — mỗi dòng đã ghim test vào task sở hữu:

1. **explorer.exe restart** (log off/đổi theme) → Windows xoá tray icon; nếu không bắt `TaskbarCreated` thì mất luôn đường duy nhất gọi lại app khi game fullscreen. → test tại Task 3 (`TrayContractTests.Dispatch_TaskbarCreated_ReAddsIcon`).
2. **HICON bị dispose/GC trước `NIM_DELETE`** → icon thành ô vuông trắng, người dùng tưởng app chết. → test tại Task 3 (`TrayIconSourceTests`).
3. **Bấm X khi update đang chạy** (`UpdateCoordinator.UpdateInProgress`) → không được ẩn xuống tray (updater cần process thật sự thoát để thay file). → test tại Task 4 (`ClosePolicyTests`).
4. **`Thoát` từ tray khi pipeline + sidecar python đang chạy** → phải kill tree `ReTran.Core` trước rồi mới quit, không để mồ côi process Paddle ~2 GB RAM. → test tại Task 5 (`TrayExitServiceTests`, thứ tự call).
5. **Multi-window**: `OnWindowCreated` sẽ bắn cho mọi window (Plan 2 thêm window menu nổi) — chỉ window chính được hook `Closing` và start tray, không thì có 2 icon + 2 lần hide. → test tại Task 4 (`ClosePolicyTests.ShouldHookWindow_OnlyPrimary`).

---

### Task 1: Sửa hàng checkbox trong Settings sheet (CSS thuần)

**Files:**
- Modify: `ReTran.App/vite-project/src/css/deck.css` (sau dòng `.rt-srow .rt-select select { height: 32px; }`, ~dòng 757)

**Interfaces:**
- Consumes: không.
- Produces: rule CSS mới — mọi `.rt-srow` có `input[type=checkbox]` làm con trực tiếp sẽ xếp nhãn + control cùng hàng, hint xuống hàng dưới. Checkbox "Gộp nhiều dòng" mà plan song song sắp thêm vào `SettingsPanel.razor` tự đúng theo, không cần sửa Razor.

- [ ] **Step 1: Thêm rule CSS** (chính xác, giữ nguyên token theme đang có):

```css
/* Checkbox rows: label + control on the same line, hint below. */
.rt-srow:has(> input[type="checkbox"]) { grid-template-columns: 1fr auto; column-gap: 10px; align-items: center; }
.rt-srow:has(> input[type="checkbox"]) > span { grid-row: 1; grid-column: 1; }
.rt-srow:has(> input[type="checkbox"]) > input[type="checkbox"] { grid-row: 1; grid-column: 2; justify-self: end; width: 15px; height: 15px; accent-color: var(--rt-tally); cursor: pointer; }
.rt-srow:has(> input[type="checkbox"]) > .rt-hint { grid-row: 2; grid-column: 1 / -1; }
```

- [ ] **Step 2: Rebuild frontend (không commit output)**

Run (trong `ReTran.App/vite-project/`): `npm run build`
Expected: tsc + vite build sạch, ghi ra `../wwwroot/build/assets/styles.css`.

- [ ] **Step 3: Xác nhận rule có mặt trong build**

Run: `Select-String -Path "wwwroot/build/assets/styles.css" -Pattern "rt-srow:has"` (từ repo root)
Expected: ≥ 1 match. Nếu WebView2 của máy không hỗ trợ `:has()` (kiểm tra ở Step 4), fallback: thêm class `.rt-srow--check` trong rule trên và ghi chú vào Plan 2 để gắn class khi sửa Razor; **không** tự sửa `SettingsPanel.razor` ở đây.

- [ ] **Step 4: Xác nhận thủ công**

Chạy app, mở Settings sheet → mục OCR params: hàng bool (vd `use_doc_orientation_classify`) phải là 1 hàng `[nhãn] [ô tick]`, hint xuống hàng dưới; các hàng `select`/`number` giữ nguyên layout cũ. Ghi lại kết quả.

- [ ] **Step 5: Commit (chỉ CSS source)**

```bash
git add "ReTran.App/vite-project/src/css/deck.css"
git commit -m "fix(settings): put checkbox and label on one row in settings sheet"
```

---

### Task 2: `TrayMenuModel` — mô hình menu ngữ cảnh tray (thuần)

**Files:**
- Create: `ReTran.App/Services/Tray/TrayMenuModel.cs`
- Test: `ReTran.Tests/Tray/TrayMenuModelTests.cs` (global namespace, như các test App khác)

**Interfaces:**
- Consumes: không.
- Produces:

```csharp
namespace ReTran_App.Services.Tray;

public enum TrayCommandId { OpenApp, ToggleOverlay, Exit }

public sealed record TrayCommand(TrayCommandId Id, string Label, bool IsChecked);

public static class TrayMenuModel
{
    // Thứ tự cố định: OpenApp, ToggleOverlay, Exit. Chỉ Overlay mang IsChecked.
    public static IReadOnlyList<TrayCommand> Build(string openLabel, string overlayLabel, string exitLabel, bool overlayOn);
}
```

- [ ] **Step 1: Viết test failing**

```csharp
public class TrayMenuModelTests
{
    // Build trả IReadOnlyList<TrayCommand> — không có indexer enum; tra theo Id bằng helper này.
    private static TrayCommand ById(IReadOnlyList<TrayCommand> menu, TrayCommandId id) =>
        menu.Single(c => c.Id == id);

    [Fact]
    public void Build_FixedOrder_OnlyOverlayChecked()
    {
        var menu = TrayMenuModel.Build("Open", "Overlay", "Exit", overlayOn: true);
        Assert.Equal(new[] { TrayCommandId.OpenApp, TrayCommandId.ToggleOverlay, TrayCommandId.Exit },
            menu.Select(c => c.Id).ToArray());
        Assert.True(ById(menu, TrayCommandId.ToggleOverlay).IsChecked);
        Assert.False(ById(menu, TrayCommandId.OpenApp).IsChecked);
        Assert.Equal("Open", menu[0].Label);
    }

    [Fact]
    public void Build_OverlayOff_Unchecked()
    {
        var menu = TrayMenuModel.Build("a", "b", "c", overlayOn: false);
        Assert.False(ById(menu, TrayCommandId.ToggleOverlay).IsChecked);
    }
}
```

- [ ] **Step 2: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "TrayMenuModelTests"`
Expected: compile error (`TrayMenuModel` chưa tồn tại).

- [ ] **Step 3: Implement `TrayMenuModel.cs`** theo interface trên; doc comment class + method song ngữ.

- [ ] **Step 4: Chạy test — xác nhận pass**

Run: cùng lệnh Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/Tray/TrayMenuModel.cs" "ReTran.Tests/Tray/TrayMenuModelTests.cs"
git commit -m "feat(tray): tray menu model — fixed command order and check state"
```

---

### Task 3: `TrayContract` + `TrayIconSource` + `ITrayHost`/`Win32TrayHost` (glue Win32)

**Files:**
- Create: `ReTran.App/Services/Tray/TrayContract.cs`, `ReTran.App/Services/Tray/TrayIconSource.cs`, `ReTran.App/Services/Tray/ITrayHost.cs`, `ReTran.App/Services/Tray/Win32TrayHost.cs`
- Test: `ReTran.Tests/Tray/TrayContractTests.cs`, `ReTran.Tests/Tray/TrayIconSourceTests.cs`, `ReTran.Tests/Tray/Win32TrayHostSmokeTests.cs`

**Interfaces:**
- Consumes: `TrayCommand`, `TrayCommandId` (Task 2).
- Produces:

```csharp
namespace ReTran_App.Services.Tray;

public enum BalloonIcon { None, Info, Warning, Error }

public interface ITrayHost : IDisposable
{
    bool IsCreated { get; }
    void Create(nint iconHandle, string tooltip, Func<IReadOnlyList<TrayCommand>> menuProvider);
    void Remove();
    void ShowBalloon(string title, string text, BalloonIcon icon);
    event Action<TrayCommandId>? CommandSelected;
}

// Thuần — mọi quyết định Win32 test được không cần tray thật.
internal static class TrayContract
{
    internal const uint NifMessage = 0x0001, NifIcon = 0x0002, NifTip = 0x0004, NifInfo = 0x0010;
    internal const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    internal const int NiifNone = 0, NiifInfo = 1, NiifWarning = 2, NiifError = 3;
    internal const uint WmLbuttonUp = 0x0202, WmLbuttonDblClk = 0x0203, WmRbuttonUp = 0x0205, WmContextMenu = 0x007B;

    internal static uint AddUFlags() => NifMessage | NifIcon | NifTip;
    internal static uint ModifyUFlags(bool withTip) => NifInfo | (withTip ? NifTip : 0u);
    internal static uint InfoFlags(BalloonIcon icon);

    internal enum TrayDispatch { Ignore, ReAddIcon, ShowMenu, LeftClick }
    // wParamMsg: WM nhận được; taskbarCreatedId: RegisterWindowMessage("TaskbarCreated");
    // callbackMessage: WM_USER+1 đã đăng ký cho icon; lParamMouseMsg: mã chuột trong lParam.
    internal static TrayDispatch Dispatch(uint wParamMsg, uint taskbarCreatedId, uint callbackMessage, uint lParamMouseMsg);
}

// Vẽ HICON 32×32 bằng GDI+ (nền bo góc `BoxAnnotationStyle.PillFill`, chữ "R" màu `TickColor`), giữ handle sống tới Dispose.
public sealed class TrayIconSource : IDisposable
{
    public nint Handle { get; }   // khác 0, ổn định qua mọi lần đọc, hợp lệ tới khi Dispose
    public bool IsDisposed { get; }
    public void Dispose();        // DestroyIcon đúng 1 lần, idempotent
}
```

- [ ] **Step 1: Viết test failing cho `TrayContract`**

```csharp
public class TrayContractTests
{
    [Fact]
    public void AddUFlags_MessageIconTip() => Assert.Equal(0x7u, TrayContract.AddUFlags());

    [Fact]
    public void ModifyUFlags_WithAndWithoutTip()
    {
        Assert.Equal(TrayContract.NifInfo | TrayContract.NifTip, TrayContract.ModifyUFlags(true));
        Assert.Equal(TrayContract.NifInfo, TrayContract.ModifyUFlags(false));
    }

    [Theory]
    [InlineData(BalloonIcon.None, 0)]
    [InlineData(BalloonIcon.Info, 1)]
    [InlineData(BalloonIcon.Warning, 2)]
    [InlineData(BalloonIcon.Error, 3)]
    // xUnit v3 không suy được T chung cho (int, uint) → cast; 2 Theory expose enum internal TrayDispatch phải là internal.
    public void InfoFlags_MapsLevel(BalloonIcon icon, int expected) => Assert.Equal((uint)expected, TrayContract.InfoFlags(icon));

    [Fact]
    public void Dispatch_TaskbarCreated_ReAddsIcon() // Review Focus #1
        => Assert.Equal(TrayContract.TrayDispatch.ReAddIcon,
            TrayContract.Dispatch(wParamMsg: 0x8000, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: 0));

    [Theory]
    [InlineData(TrayContract.WmLbuttonUp, TrayContract.TrayDispatch.LeftClick)]
    [InlineData(TrayContract.WmLbuttonDblClk, TrayContract.TrayDispatch.LeftClick)]
    public void Dispatch_LeftClicks(uint mouseMsg, TrayContract.TrayDispatch expected)
        => Assert.Equal(expected, TrayContract.Dispatch(wParamMsg: 0x0401, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: mouseMsg));

    [Theory]
    [InlineData(TrayContract.WmRbuttonUp, TrayContract.TrayDispatch.ShowMenu)]
    [InlineData(TrayContract.WmContextMenu, TrayContract.TrayDispatch.ShowMenu)]
    public void Dispatch_RightClicks_ShowMenu(uint mouseMsg, TrayContract.TrayDispatch expected)
        => Assert.Equal(expected, TrayContract.Dispatch(wParamMsg: 0x0401, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: mouseMsg));

    [Fact]
    public void Dispatch_OtherMessages_Ignored()
        => Assert.Equal(TrayContract.TrayDispatch.Ignore, TrayContract.Dispatch(wParamMsg: 0x9999, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: 0));
}
```

- [ ] **Step 2: Viết test failing cho `TrayIconSource`** (Review Focus #2)

```csharp
public class TrayIconSourceTests
{
    [Fact]
    public void Handle_NonZero_AndStable()
    {
        using var icon = new TrayIconSource();
        Assert.True(icon.Handle != 0);
        Assert.Equal(icon.Handle, icon.Handle);
    }

    [Fact]
    public void Dispose_Idempotent_AndMarksDisposed()
    {
        var icon = new TrayIconSource();
        icon.Dispose();
        Assert.True(icon.IsDisposed);
        icon.Dispose(); // không throw
    }
}
```

- [ ] **Step 3: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "TrayContractTests"` và `--filter-class "TrayIconSourceTests"`
Expected: FAIL (các type chưa tồn tại).

- [ ] **Step 4: Implement**

- `TrayContract`: theo interface trên; `Dispatch` — `wParamMsg == taskbarCreatedId → ReAddIcon`; `wParamMsg != callbackMessage → Ignore`; còn lại theo `lParamMouseMsg` (bảng ở test).
- `TrayIconSource`: bitmap 32×32 `Format32bppArgb`, nền trong suốt, vẽ bo góc bằng `BoxAnnotationStyle.BuildRoundedRect` + fill `PillFill`, chữ "R" bằng `DrawString` với `BoxAnnotationStyle.GetFont(18)` (tham số `int`) màu `TickColor`; `Handle = bitmap.GetHicon()` (giữ handle trần, **không** bọc `Icon.FromHandle` — wrapper sẽ destroy handle khi dispose); `Dispose` → `DestroyIcon(Handle)` đúng 1 lần.
- `ITrayHost`: theo interface trên.
- `Win32TrayHost` (implement `ITrayHost`): message-only window (`CreateWindowExW(parent: HWND_MESSAGE)`) tạo **trên thread gọi `Create`** (UI thread — pump của WinUI sẽ dispatch); WNDPROC delegate giữ trong field static; mọi thao tác public enqueue-free (gọi thẳng, vì chỉ UI thread dùng). `Create`: lưu icon/tooltip/menuProvider, `RegisterWindowMessage("TaskbarCreated")`, `Shell_NotifyIcon(NIM_ADD)` với `uFlags = TrayContract.AddUFlags()`, `uVersion = 4`. `Remove` → `NIM_DELETE` + `DestroyWindow`. `ShowBalloon` → `NIM_MODIFY` với `uFlags = ModifyUFlags(withTip: true)`, `dwInfoFlags = InfoFlags(icon)`; cắt `text` ≤ 255 ký tự, `title` ≤ 63. `WndProc`: `TrayContract.Dispatch`; `ShowMenu` → dựng HMENU từ `menuProvider()` bằng `AppendMenuW` (`MF_STRING | MF_CHECKED` khi `IsChecked`), `SetForegroundWindow` rồi `TrackPopupMenu(hmenu, TPM_RIGHTBUTTON, x, y, 0, IntPtr.Zero, null)`, `DestroyMenu`, raise `CommandSelected(id)`; `LeftClick` → `CommandSelected(TrayCommandId.OpenApp)`; `ReAddIcon` → chạy lại `NIM_ADD`. Struct `NOTIFYICONDATA` (cbSize theo bản có `szInfo[256]`/`szInfoTitle[64]`/`dwInfoFlags`, `CharSet.Unicode`) nằm trong `private static class NativeMethods` — cùng quy ước `Win32OverlayHost.NativeMethods`.

- [ ] **Step 5: Smoke test cho `Win32TrayHost`** (Windows-guarded, không cần tray thật)

```csharp
public class Win32TrayHostSmokeTests
{
    [Fact]
    public void CreateRemoveDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var icon = new TrayIconSource();
        using var host = new Win32TrayHost();
        Assert.False(host.IsCreated);
        host.Create(icon.Handle, "ReTran", () => TrayMenuModel.Build("a", "b", "c", false));
        host.Create(icon.Handle, "ReTran", () => TrayMenuModel.Build("a", "b", "c", false)); // idempotent
        Assert.True(host.IsCreated);
        host.Remove();
        host.ShowBalloon("t", "m", BalloonIcon.Info); // no-op sau Remove, không throw
    }
}
```

- [ ] **Step 6: Chạy test — xác nhận pass**

Run: 3 filter class trên. Expected: PASS toàn bộ.

- [ ] **Step 7: Commit**

```bash
git add "ReTran.App/Services/Tray/TrayContract.cs" "ReTran.App/Services/Tray/TrayIconSource.cs" "ReTran.App/Services/Tray/ITrayHost.cs" "ReTran.App/Services/Tray/Win32TrayHost.cs" "ReTran.Tests/Tray/TrayContractTests.cs" "ReTran.Tests/Tray/TrayIconSourceTests.cs" "ReTran.Tests/Tray/Win32TrayHostSmokeTests.cs"
git commit -m "feat(tray): Shell_NotifyIcon host with message-only window, HICON source and pure dispatch contract"
```

---

### Task 4: `ClosePolicy` + `WindowHooks` + `AppWindowBridge` (X → ẩn xuống tray)

**Files:**
- Create: `ReTran.App/Services/Window/ClosePolicy.cs`, `ReTran.App/Services/Window/AppWindowBridge.cs`
- Test: `ReTran.Tests/Window/ClosePolicyTests.cs`

**Interfaces:**
- Consumes: không (glue WinUI tách khỏi test).
- Produces:

```csharp
namespace ReTran_App.Services.Window;

public enum CloseDecision { HideToTray, AllowClose }

public static class ClosePolicy
{
    // Update đang chạy hoặc app đã chọn thoát thật → cho đóng; còn lại ẩn xuống tray.
    public static CloseDecision Decide(bool updateInProgress, bool appIsExiting);
}

public static class WindowHooks
{
    // Chỉ window chính (ordinal 0) được hook — Plan 2 sẽ mở thêm window menu nổi.
    public static bool ShouldHookWindow(int windowOrdinal);
}

public sealed class AppWindowBridge : IDisposable
{
    public AppWindowBridge(ToastService toast, I18nService L, UpdateCoordinator coordinator);
    public void Attach(Microsoft.UI.Xaml.Window winuiWindow); // gọi 1 lần; hook AppWindow.Closing
    public bool IsAttached { get; }
    public bool IsVisible { get; }        // window chưa Hide()
    public bool IsForeground { get; }     // GetForegroundWindow() == hwnd của app
    public void HideToTray();             // AppWindow.Hide() — không đụng pipeline/core
    public void Restore();                // AppWindow.Show() + Activate()
    public void BeginExit();              // idempotent: đánh dấu "app đang thoát thật" — Closing cho AllowClose từ lúc này
}
```

- [ ] **Step 1: Viết test failing** (Review Focus #3 và #5)

```csharp
public class ClosePolicyTests
{
    [Theory]
    [InlineData(false, false, CloseDecision.HideToTray)]
    [InlineData(true, false, CloseDecision.AllowClose)]   // update đang chạy — không được ẩn
    [InlineData(false, true, CloseDecision.AllowClose)]   // thoát thật từ tray
    [InlineData(true, true, CloseDecision.AllowClose)]
    public void Decide_UpdateOrExiting_AllowsClose(bool updating, bool exiting, CloseDecision expected)
        => Assert.Equal(expected, ClosePolicy.Decide(updating, exiting));

    [Fact]
    public void ShouldHookWindow_OnlyPrimary() // Review Focus #5
    {
        Assert.True(WindowHooks.ShouldHookWindow(0));
        Assert.False(WindowHooks.ShouldHookWindow(1));
        Assert.False(WindowHooks.ShouldHookWindow(2));
    }
}
```

- [ ] **Step 2: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "ClosePolicyTests"`
Expected: compile error.

- [ ] **Step 3: Implement**

- `ClosePolicy`, `WindowHooks`: thuần, theo interface.
- `AppWindowBridge`: `Attach` — `WinRT.Interop.WindowNative.GetWindowHandle(winuiWindow)` → `Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd))`; hook `Closing += (s, e) => { e.Cancel = ClosePolicy.Decide(_updateInProgress, _exiting) == CloseDecision.HideToTray; }` — `_updateInProgress` đọc qua `UpdateCoordinator` (inject thêm vào constructor: `AppWindowBridge(ToastService toast, I18nService L, UpdateCoordinator coordinator)`), `_exiting` được set qua `BeginExit()` (idempotent) — do `AppTrayActions.ExitApp` (Task 9) gọi TRƯỚC `IAppShutdown.Quit()`, để Closing cho app đóng thật. `HideToTray` → `appWindow.Hide()`; `Restore` → `appWindow.Show(); appWindow.Activate();`; `IsForeground` → `GetForegroundWindow() == hwnd` (DllImport trong `NativeMethods` nội bộ).

- [ ] **Step 4: Chạy test — xác nhận pass** + build toàn solution.

Run: filter class trên rồi `dotnet build "ReTran Project.slnx"`. Expected: PASS, 0 error.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/Window/ClosePolicy.cs" "ReTran.App/Services/Window/AppWindowBridge.cs" "ReTran.Tests/Window/ClosePolicyTests.cs"
git commit -m "feat(tray): close-to-tray policy, primary-window hook guard and AppWindow bridge"
```

---

### Task 5: `ITrayExit` + `TrayExitService` (thoát sạch, không mồ côi Core/python)

**Files:**
- Create: `ReTran.App/Services/Tray/TrayExitService.cs` (kèm interface `ITrayExit` trong cùng file, theo pattern `IOverlayHost`)
- Test: `ReTran.Tests/Tray/TrayExitServiceTests.cs`

**Interfaces:**
- Consumes: `IUpdateStopper`, `IAppShutdown` (cả hai trong `ReTran_App.Services.Update`).
- Produces:

```csharp
namespace ReTran_App.Services.Tray;

public interface ITrayExit
{
    Task ExitAsync(CancellationToken cancellationToken);
}

public sealed class TrayExitService : ITrayExit
{
    public TrayExitService(IUpdateStopper stopper, IAppShutdown shutdown, ILogger<TrayExitService> log);
    // Dừng pipeline + core (kill tree) TRƯỚC, rồi Quit. StopServicesAsync ném → log + vẫn Quit.
    public async Task ExitAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Viết test failing** (Review Focus #4 — thứ tự call là điều kiện sống còn)

```csharp
public class TrayExitServiceTests
{
    // Hai fake dùng chung MỘT call list — thứ tự stop→quit (Review Focus #4) phải được khóa
    // bằng chứng ghi đè thật, không phải bằng Concat hai list riêng (luôn ra đúng thứ tự mong đợi).
    private sealed class FakeStopper : IUpdateStopper
    {
        private readonly List<string> _calls;
        public bool ThrowOnStop { get; set; }
        public FakeStopper(List<string> calls) => _calls = calls;
        public Task StopServicesAsync(CancellationToken ct)
        {
            _calls.Add("stop");
            if (ThrowOnStop) throw new IOException("simulated");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShutdown : IAppShutdown
    {
        private readonly List<string> _calls;
        public FakeShutdown(List<string> calls) => _calls = calls;
        public void Quit() => _calls.Add("quit");
    }

    [Fact]
    public async Task Exit_StopsServices_BeforeQuit()
    {
        var calls = new List<string>();
        await new TrayExitService(new FakeStopper(calls), new FakeShutdown(calls),
            NullLogger<TrayExitService>.Instance).ExitAsync(CancellationToken.None);

        Assert.Equal(new[] { "stop", "quit" }, calls.ToArray());
    }

    [Fact]
    public async Task Exit_StopFails_StillQuits()
    {
        var calls = new List<string>();
        await new TrayExitService(new FakeStopper(calls) { ThrowOnStop = true }, new FakeShutdown(calls),
            NullLogger<TrayExitService>.Instance).ExitAsync(CancellationToken.None);

        Assert.Equal(new[] { "stop", "quit" }, calls.ToArray());
    }
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error)

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "TrayExitServiceTests"`

- [ ] **Step 3: Implement** theo interface; doc comment song ngữ nêu rõ thứ tự và lý do (không mồ côi sidecar).

- [ ] **Step 4: Chạy test — xác nhận pass**

Run: cùng lệnh. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/Tray/TrayExitService.cs" "ReTran.Tests/Tray/TrayExitServiceTests.cs"
git commit -m "feat(tray): clean exit path — stop pipeline and core tree before quitting"
```

---

### Task 6: `NotifyRoute` + `AppNotifier` (bubble khi app ẩn/không focus, toast khi đang focus)

**Files:**
- Create: `ReTran.App/Services/Tray/AppNotifier.cs`
- Test: `ReTran.Tests/Tray/AppNotifierTests.cs`

**Interfaces:**
- Consumes: `ToastService`, `ITrayHost` (Task 3), `ToastSeverity`.
- Produces:

```csharp
namespace ReTran_App.Services.Tray;

public static class NotifyRoute
{
    // App đang nhìn thấy và được focus → toast trong app; ngược lại → bubble tray.
    public static bool UseTrayBubble(bool windowVisible, bool windowIsForeground);
}

public sealed class AppNotifier
{
    // bubbleCooldown mặc định 3 s: nhiều lỗi dồn trong cửa sổ đó chỉ phát 1 bubble.
    public AppNotifier(ToastService toast, ITrayHost tray, Func<bool> windowVisible,
        Func<bool> windowIsForeground, ILogger<AppNotifier> log, TimeSpan? bubbleCooldown = null);

    public void Notify(string message, ToastSeverity severity = ToastSeverity.Info);

    internal static BalloonIcon LevelFor(ToastSeverity severity); // Info→Info, Error→Error
}
```

- [ ] **Step 1: Viết test failing**

```csharp
public class AppNotifierTests
{
    private sealed class FakeTray : ITrayHost
    {
        public List<(string Title, string Text, BalloonIcon Icon)> Balloons { get; } = new();
        public bool IsCreated => true;
        public void Create(nint iconHandle, string tooltip, Func<IReadOnlyList<TrayCommand>> menuProvider) { }
        public void Remove() { }
        public void ShowBalloon(string title, string text, BalloonIcon icon) => Balloons.Add((title, text, icon));
        // Fake không bao giờ raise event → accessors rỗng để tránh CS0067.
        public event Action<TrayCommandId>? CommandSelected { add { } remove { } }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false, false, true)]  // app ẩn → bubble dù có focus hay không
    [InlineData(true, false, true)]   // app hiện nhưng đang chơi game (không focus) → bubble
    [InlineData(true, true, false)]   // app đang focus → toast trong app
    public void UseTrayBubble_Matrix(bool visible, bool focused, bool expected)
        => Assert.Equal(expected, NotifyRoute.UseTrayBubble(visible, focused));

    [Fact]
    public void Notify_FocusedWindow_UsesToast_NotBalloon()
    {
        var tray = new FakeTray();
        var toast = new ToastService();
        string? toasted = null;
        toast.ToastAdded += t => toasted = t.Message;
        var notifier = new AppNotifier(toast, tray, () => true, () => true, NullLogger<AppNotifier>.Instance);

        notifier.Notify("Lỗi gì đó", ToastSeverity.Error);

        Assert.Equal("Lỗi gì đó", toasted);
        Assert.Empty(tray.Balloons);
    }

    [Fact]
    public void Notify_HiddenWindow_UsesBalloon_NotToast()
    {
        var tray = new FakeTray();
        var toast = new ToastService();
        bool toasted = false;
        toast.ToastAdded += _ => toasted = true;
        var notifier = new AppNotifier(toast, tray, () => false, () => false, NullLogger<AppNotifier>.Instance);

        notifier.Notify("Lỗi nặng", ToastSeverity.Error);

        Assert.False(toasted);
        var (title, text, icon) = tray.Balloons.Single();
        Assert.Equal("ReTran", title); // brand — không i18n (ruling ledger 2026-11-02)
        Assert.Equal("Lỗi nặng", text);
        Assert.Equal(BalloonIcon.Error, icon);
    }

    [Fact]
    public void Notify_ManyErrorsWithinCooldown_OneBalloon()
    {
        var tray = new FakeTray();
        var notifier = new AppNotifier(new ToastService(), tray, () => false, () => false,
            NullLogger<AppNotifier>.Instance, bubbleCooldown: TimeSpan.FromSeconds(30));

        notifier.Notify("e1");
        notifier.Notify("e2");
        notifier.Notify("e3");

        // Assert.Equal(1, count) sẽ bắn xUnit2013 (output test mới phải sạch) → Assert.Single.
        Assert.Single(tray.Balloons);
    }

    [Fact]
    public void LevelFor_MapsSeverity()
    {
        Assert.Equal(BalloonIcon.Info, AppNotifier.LevelFor(ToastSeverity.Info));
        Assert.Equal(BalloonIcon.Error, AppNotifier.LevelFor(ToastSeverity.Error));
    }
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error)

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "AppNotifierTests"`

- [ ] **Step 3: Implement** theo interface; cooldown bằng `DateTime.UtcNow` + field `_lastBubbleUtc`, không thêm clock abstraction. Balloon title là chuỗi cố định `"ReTran"` (tên thương hiệu, không dịch — ruling ledger 2026-11-02; key i18n `tray.balloon.title` đã bỏ khỏi Task 8).

- [ ] **Step 4: Chạy test — xác nhận pass**

Run: cùng lệnh. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/Tray/AppNotifier.cs" "ReTran.Tests/Tray/AppNotifierTests.cs"
git commit -m "feat(tray): error notifier — Windows balloon when app hidden/unfocused, toast otherwise"
```

---

### Task 7: `ITrayActions` + `TrayIconService` (nối tray ↔ overlay/pipeline/window)

**Files:**
- Create: `ReTran.App/Services/Tray/TrayIconService.cs` (kèm interface `ITrayActions`)
- Test: `ReTran.Tests/Tray/TrayIconServiceTests.cs`

**Interfaces:**
- Consumes: `ITrayHost` (Task 3), `TrayCommand`/`TrayMenuModel` (Task 2), `AppNotifier` (Task 6), `I18nService`.
- Produces:

```csharp
namespace ReTran_App.Services.Tray;

public interface ITrayActions
{
    bool OverlayIsOn { get; }
    bool PipelineRunning { get; }
    void SetOverlay(bool on);
    void OpenAppWindow();
    Task ExitApp();
}

public sealed class TrayIconService : IDisposable
{
    public TrayIconService(ITrayHost host, ITrayActions actions, I18nService L, AppNotifier notifier, ILogger<TrayIconService> log);
    // Idempotent: tạo TrayIconSource + host.Create(icon.Handle, L["tray.tip"], BuildMenu).
    public void Start();
    // Remove icon, dispose icon source.
    public void Stop();
    public IReadOnlyList<TrayCommand> BuildMenu();
}
```

- [ ] **Step 1: Viết test failing** (dùng `FakeTrayHost`/`FakeTrayActions` trong file test — pattern như `ReTran.Tests/Overlay/OverlayWindowServiceTests.cs`)

```csharp
public class TrayIconServiceTests
{
    private sealed class FakeTrayHost : ITrayHost
    {
        public int CreateCount;
        public int RemoveCount;
        public List<string> Balloons { get; } = new();
        public bool IsCreated => true;
        public void Create(nint iconHandle, string tooltip, Func<IReadOnlyList<TrayCommand>> menuProvider) => CreateCount++;
        public void Remove() => RemoveCount++;
        public void ShowBalloon(string title, string text, BalloonIcon icon) => Balloons.Add(text);
        public event Action<TrayCommandId>? CommandSelected;
        public void Raise(TrayCommandId id) => CommandSelected?.Invoke(id);
        public void Dispose() { }
    }
    private sealed class FakeActions : ITrayActions
    {
        public bool OverlayOn { get; set; }
        public bool Running { get; set; } = true;
        public bool ThrowOnOverlay { get; set; }
        public List<string> Calls { get; } = new();
        public bool OverlayIsOn => OverlayOn;
        public bool PipelineRunning => Running;
        public void SetOverlay(bool on)
        {
            if (ThrowOnOverlay) throw new InvalidOperationException("simulated");
            OverlayOn = on;
            Calls.Add("overlay:" + on);
        }
        public void OpenAppWindow() => Calls.Add("open");
        public Task ExitApp() { Calls.Add("exit"); return Task.CompletedTask; }
    }

    private static (TrayIconService Svc, FakeTrayHost Host, FakeActions Actions, I18nService L) Create(bool overlayOn, bool running)
    {
        var host = new FakeTrayHost();
        var actions = new FakeActions { OverlayOn = overlayOn, Running = running };
        var l = new I18nService();
        l.Set("vi");
        return (new TrayIconService(host, actions, l, new AppNotifier(new ToastService(), host, () => false, () => false, NullLogger<AppNotifier>.Instance), NullLogger<TrayIconService>.Instance), host, actions, l);
    }

    [Fact]
    public void BuildMenu_ReflectsOverlayState()
    {
        var (svc, _, _, _) = Create(overlayOn: true, running: true);
        var menu = svc.BuildMenu();
        // IReadOnlyList<TrayCommand> không index được bằng enum (ruling Task 2) → Single theo Id.
        Assert.True(menu.Single(c => c.Id == TrayCommandId.ToggleOverlay).IsChecked);
    }

    [Fact]
    public void Selected_ToggleOverlay_FlipsAction()
    {
        var (svc, host, actions, _) = Create(overlayOn: false, running: true);
        svc.Start();
        host.Raise(TrayCommandId.ToggleOverlay);
        Assert.Equal("overlay:True", actions.Calls.Single());
    }

    [Fact]
    public void Selected_ToggleOverlay_OnWhilePipelineStopped_StillSets_AndNotifies()
    {
        // Spec §7.2: vẫn cho toggle (áp dụng ở lần Start kế tiếp) + nói rõ, không ném.
        var (svc, host, actions, l) = Create(overlayOn: false, running: false);
        svc.Start();
        host.Raise(TrayCommandId.ToggleOverlay);
        Assert.Equal("overlay:True", actions.Calls.Single());
        Assert.Equal(l["tray.overlay.appliesAtStart"], host.Balloons.Last());
    }

    [Fact]
    public void Selected_OpenApp_And_Exit()
    {
        var (svc, host, actions, _) = Create(overlayOn: false, running: true);
        svc.Start();
        host.Raise(TrayCommandId.OpenApp);
        host.Raise(TrayCommandId.Exit);
        Assert.Equal(new[] { "open", "exit" }, actions.Calls.ToArray());
    }

    [Fact]
    public void Selected_ActionThrows_BalloonReportsError()
    {
        // Spec §Báo lỗi (D12): MỌI lỗi khởi phát từ tray phải tới mắt người dùng —
        // bubble khi app ẩn/không focus (FakeTray + windowVisible=false ở helper Create).
        var (svc, host, actions, l) = Create(overlayOn: false, running: true);
        actions.ThrowOnOverlay = true;
        svc.Start();
        host.Raise(TrayCommandId.ToggleOverlay); // không được ném ra ngoài handler
        Assert.Equal(l["tray.error.command"], host.Balloons.Last());
    }

    [Fact]
    public void Start_Idempotent_StopRemoves_RestartRecreates()
    {
        var (svc, host, _, _) = Create(false, true);
        svc.Start();
        svc.Start();
        Assert.Equal(1, host.CreateCount);
        svc.Stop();
        Assert.Equal(1, host.RemoveCount);
        svc.Start();
        Assert.Equal(2, host.CreateCount);
    }
}
```

- [ ] **Step 2: Chạy test — xác nhận fail** (compile error)

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "TrayIconServiceTests"`

- [ ] **Step 3: Implement** theo interface. Logic chọn: `ToggleOverlay` → `actions.SetOverlay(!actions.OverlayIsOn)`; nếu `!actions.PipelineRunning` và vừa bật → `notifier.Notify(L["tray.overlay.appliesAtStart"])`. `Exit` → `await actions.ExitApp()`. **Handler catch (mọi command, spec §Báo lỗi D12):** log chi tiết + `notifier.Notify(L["tray.error.command"], ToastSeverity.Error)` — lỗi khởi phát từ tray phải tới mắt người dùng (bubble khi app ẩn, toast khi focus), không chỉ log.

- [ ] **Step 4: Chạy test — xác nhận pass**

Run: cùng lệnh. Expected: PASS (key `tray.overlay.appliesAtStart` chưa tồn tại ở Task 8 — assertion so với `l["..."]` nên vẫn ổn).

- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/Tray/TrayIconService.cs" "ReTran.Tests/Tray/TrayIconServiceTests.cs"
git commit -m "feat(tray): tray icon service wiring overlay/pipeline/window actions to the menu"
```

---

### Task 8: i18n keys cho tray (vi + en) — **task nhạy cảm xung đột**

**Files:**
- Modify: `ReTran.App/Services/I18nStrings.cs` (block `Vi` cạnh cụm `deck.overlay.*` ~dòng 127; block `En` ~dòng 422)

**Interfaces:**
- Consumes: không.
- Produces: keys dưới đây, dùng bởi Task 3/6/7 (`tray.tip`, `tray.menu.*`, `tray.overlay.appliesAtStart`).

- [ ] **Step 1: Kiểm tra xung đột trước khi sửa**

Run: `git status --short "ReTran.App/Services/I18nStrings.cs"` và đọc lại file mới nhất. Nếu phiên song song đang thêm key vào cùng file (Task 6 của plan kia), **chờ** đến khi họ commit rồi làm tiếp.

- [ ] **Step 2: Thêm keys — block `Vi`:**

```csharp
["tray.tip"] = "ReTran",
["tray.menu.open"] = "Mở ReTran",
["tray.menu.overlay"] = "Lớp phủ",
["tray.menu.exit"] = "Thoát",
["tray.overlay.appliesAtStart"] = "Lớp phủ sẽ hiện ở lần Start kế tiếp.",
["tray.error.command"] = "Hành động từ khay thất bại.",
```

**block `En`:**

```csharp
["tray.tip"] = "ReTran",
["tray.menu.open"] = "Open ReTran",
["tray.menu.overlay"] = "Overlay",
["tray.menu.exit"] = "Exit",
["tray.overlay.appliesAtStart"] = "The overlay will appear on the next start.",
["tray.error.command"] = "A tray action failed.",
```

- [ ] **Step 3: Chạy test i18n**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "I18nServiceTests"`
Expected: PASS (mọi key phải có giá trị khác key ở cả 2 ngôn ngữ).

- [ ] **Step 4: Commit**

```bash
git add "ReTran.App/Services/I18nStrings.cs"
git commit -m "feat(i18n): tray labels and balloon strings (vi + en)"
```

---

### Task 9: Wiring DI + hook window chính + xác nhận cuối

**Files:**
- Modify: `ReTran.App/MauiProgram.cs`
- Create: `ReTran.App/Services/Tray/AppTrayActions.cs`
- Modify: `ReTran.App/Services/Window/AppWindowBridge.cs` (hardening từ review Task 4: null-guard `AppWindow.GetFromWindowId` trong `Attach`, try/catch quanh `HideToTray()` trong `OnClosing` — WinRT call có thể ném vào Closing event loop, clear `_hwnd` khi `Dispose`)
- Modify: `Docs/performance.md` (mục ngắn ghi nhận footprint tray)

**Interfaces:**
- Consumes: toàn bộ Task 2–8.
- Produces: app chạy thật có tray icon; không interface mới cho task khác.

- [ ] **Step 1: `AppTrayActions` — lớp nối `ITrayActions` vào service thật**

```csharp
namespace ReTran_App.Services.Tray;

public sealed class AppTrayActions : ITrayActions
{
    public AppTrayActions(OverlayWindowService overlay, PipelineController pipeline, AppWindowBridge window, ITrayExit exit);
    // OverlayIsOn => _overlay.IsEnabled; PipelineRunning => _pipeline.IsRunning;
    // SetOverlay => _overlay.SetEnabled(on); OpenAppWindow => _window.Restore();
    // ExitApp => _window.BeginExit() RỒI await _exit.ExitAsync(CancellationToken.None) — BeginExit trước, để Closing (bắn khi Quit) cho AllowClose thay vì ẩn xuống tray.
}
```

- [ ] **Step 2: Đăng ký DI trong `MauiProgram.CreateMauiApp`** (cạnh khối overlay hiện có ~dòng 38–41):

```csharp
builder.Services.AddSingleton<AppWindowBridge>();
builder.Services.AddSingleton<ITrayHost, Win32TrayHost>();
builder.Services.AddSingleton<ITrayExit, TrayExitService>();
builder.Services.AddSingleton(sp => new AppNotifier(
    sp.GetRequiredService<ToastService>(),
    sp.GetRequiredService<ITrayHost>(),
    () => sp.GetRequiredService<AppWindowBridge>().IsVisible,
    () => sp.GetRequiredService<AppWindowBridge>().IsForeground,
    sp.GetRequiredService<ILogger<AppNotifier>>()));
builder.Services.AddSingleton<ITrayActions, AppTrayActions>();
builder.Services.AddSingleton<TrayIconService>();
```

- [ ] **Step 3: Hook window chính** trong `MauiProgram.CreateMauiApp`, trước `builder.Build()`:

```csharp
int windowOrdinal = -1; // Interlocked.Increment trả 0 cho window đầu → khớp ShouldHookWindow(0)
builder.ConfigureLifecycleEvents(events => events.AddWindows(w => w.OnWindowCreated(window =>
{
    var ordinal = Interlocked.Increment(ref windowOrdinal);
    if (!WindowHooks.ShouldHookWindow(ordinal)) return; // chỉ window chính (Review Focus #5)
    IServiceProvider? services = IPlatformApplication.Current?.Services; // pattern repo, xem App.xaml.cs:10
    if (services is null) return;
    services.GetRequiredService<AppWindowBridge>().Attach(window);
    services.GetRequiredService<TrayIconService>().Start();
})));
```

- [ ] **Step 4: Build + test toàn bộ**

Run: `dotnet build "ReTran Project.slnx"` → 0 error.
Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` → PASS (chạy lại lần hai nếu chỉ có fail do timing).

- [ ] **Step 5: Smoke thủ công — checklist bắt buộc, ghi kết quả**

1. Chạy app, Start nguồn screen → tray icon hiện, tooltip "ReTran".
2. Bấm X → window ẩn, pipeline vẫn chạy (box overlay vẫn dịch), core không chết.
3. Chuột trái icon → app hiện lại đúng chỗ cũ. Chuột phải → menu 3 mục tiếng Việt: `Mở ReTran · Lớp phủ · Thoát`; checkmark theo trạng thái lớp phủ.
4. Tắt pipeline rồi toggle `Lớp phủ` từ tray → bubble "Lớp phủ sẽ hiện ở lần Start kế tiếp." (app đang ẩn).
5. Bật lại pipeline → overlay tự hiện (ngữ nghĩa `OverlayOnGame`).
6. `Thoát` từ tray → kiểm tra task list: không còn process `ReTran.Core` lẫn python sidecar mồ côi.
7. Đổi theme Windows/kill explorer.exe → icon tự hiện lại (`TaskbarCreated`).

- [ ] **Step 6: Ghi nhận perf vào `Docs/performance.md`**

Mục ngắn: tray = 1 message-only window trên UI thread + 1 HICON 32×32; không process/thread mới, không timer, không redraw định kỳ; đo CPU/RAM trước-sau nếu có máy đo (không bắt buộc số).

- [ ] **Step 7: Commit**

```bash
git add "ReTran.App/MauiProgram.cs" "ReTran.App/Services/Tray/AppTrayActions.cs" "ReTran.App/Services/Window/AppWindowBridge.cs" "Docs/performance.md"
git commit -m "feat(tray): wire tray icon, close-to-tray and error balloons into the app"
```
