# App Test (xUnit + bUnit + Appium) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Phủ 3 tầng test cho App đang thiếu: xUnit cho logic còn hở (`DemoFeed` edges), bUnit cho 2 Razor pages (`Overlay`, `Home`), Appium smoke 1 luồng thật (Start Core → Ping → pong) — tất cả graceful-skip khi thiếu hạ tầng.

**Architecture:** Tầng 1 (xUnit, có sẵn): thêm edge tests vào `ReTran.Tests`. Tầng 2 (bUnit 2.11.3 + xUnit v2, đã có package, chưa có test nào): render component trong `TestContext`, services thật (`OverlayState`, `OverlayFeedServer` chưa start, `JsInteropService`) + `JSInterop` giả lập của bUnit. Tầng 3 (Appium.WebDriver 9, đã có package): 1 smoke test mở App thật qua Appium Windows driver; skip êm khi không có server.

**Tech Stack:** xUnit v2 (2.9.3 + runner 2.8.2 — KHÔNG v3, bUnit 2.x chỉ chạy v2), bUnit 2.11.3, Appium.WebDriver 9, `ReTran.Tests` TFM `net10.0-windows10.0.19041.0`.

## Global Constraints

- Test TFM: `net10.0-windows10.0.19041.0` (full — TFM ngắn lỗi MSB4086, NU1201 với App ref).
- C# `Nullable` + implicit usings enabled; không `Console.WriteLine` trong test (dùng `ITestOutputHelper` khi cần log).
- Mọi test phụ thuộc hạ tầng ngoài (Appium server, App exe, Core exe) PHẢI skip êm (return sớm, không `Assert.Skip` — giữ style `SidecarProcessTests`), không bao giờ fail vì môi trường.
- Không đụng code sản phẩm để "dễ test" (không `internal` hóa, không thêm hook) — bUnit/Appium test hành vi công khai.
- Một commit mỗi task, message `test(app): ...`.

## File Structure (locked)

```
ReTran.Tests/
├── DemoFeedEdgeTests.cs      # Task 1 (xUnit)
├── OverlayComponentTests.cs  # Task 2 (bUnit, Overlay.razor)
├── HomeComponentTests.cs     # Task 3 (bUnit, Home.razor)
└── AppiumSmokeTests.cs       # Task 4 (Appium, 1 smoke + infra note)
```

---

### Task 1: xUnit `DemoFeed` edge tests (lấp chỗ test cũ không phủ)

**Files:**
- Create: `ReTran.Tests/DemoFeedEdgeTests.cs`

**Interfaces:**
- Consumes: `ReTran_App.Core.DemoFeed(OverlayState, intervalMs=500)`, `OverlayState.Latest` (đã có, không đổi).

- [ ] **Step 1: Write the failing test** — hmm, code đã tồn tại nên RED ở đây = test MỚI phải fail trước khi... không, code đã đúng: viết test và chạy, nếu PASS ngay thì ghi rõ "behavior-first test, green on first run" (không bịa RED giả). Tạo file:

```csharp
using ReTran_App.Core;
using Xunit;

public class DemoFeedEdgeTests
{
    [Fact]
    public void Ctor_Rejects_IntervalBelow50()
    {
        var state = new OverlayState();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DemoFeed(state, intervalMs: 10));
    }

    [Fact]
    public void DoubleStart_Publishes_And_StopSticks()
    {
        var state = new OverlayState();
        using var feed = new DemoFeed(state, intervalMs: 100);
        feed.Start();
        feed.Start(); // no-op, không ném, không nhân timer
        SpinWait.SpinUntil(() => state.Latest is not null, 2000);
        Assert.NotNull(state.Latest);
        feed.Stop();
        feed.Stop(); // idempotent, không ném
        var last = state.Latest;
        Thread.Sleep(300);
        Assert.Same(last, state.Latest); // dừng thật — không còn publish mới
    }

    [Fact]
    public void Boxes_Stay_Inside_Frame()
    {
        var state = new OverlayState();
        using var feed = new DemoFeed(state, intervalMs: 50);
        feed.Start();
        SpinWait.SpinUntil(() => state.Latest is not null, 2000);
        feed.Stop();
        foreach (var b in state.Latest!.Boxes)
        {
            Assert.InRange(b.X, 0, 1);
            Assert.InRange(b.Y, 0, 1);
            Assert.True(b.X + b.W <= 1.0);
            Assert.False(string.IsNullOrWhiteSpace(b.Translated));
        }
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter DemoFeedEdgeTests`
Expected: PASS (behavior-first — code đã có; nếu FAIL thì đó là bug thật, dừng lại báo).

- [ ] **Step 3: Run full suite**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`
Expected: toàn bộ xanh (17 cũ + 3 mới = 20).

- [ ] **Step 4: Commit**

```bash
git add "ReTran.Tests/DemoFeedEdgeTests.cs"
git commit -m "test(app): DemoFeed edge cases (validation, idempotent stop, box bounds)"
```

---

### Task 2: bUnit `Overlay.razor` — render boxes từ state

**Files:**
- Create: `ReTran.Tests/OverlayComponentTests.cs`

**Interfaces:**
- Consumes: `ReTran_App.Components.Pages.Overlay` (`@page "/overlay"`, `@inject OverlayState`), `OverlayState.Publish/Latest`, `OverlayBox/OverlayFrame` (không đổi gì).

- [ ] **Step 1: Write the test** (bUnit `TestContext`, services thật, không JS cần thiết — Overlay không gọi JS):

```csharp
using bunit;
using Microsoft.Extensions.DependencyInjection;
using ReTran_App.Core;
using Xunit;

public class OverlayComponentTests : TestContext
{
    [Fact]
    public void EmptyState_Renders_NoBoxes()
    {
        Services.AddSingleton(new OverlayState());
        var cut = RenderComponent<ReTran_App.Components.Pages.Overlay>();
        Assert.Empty(cut.FindAll("div[style*='position:absolute']"));
    }

    [Fact]
    public void PublishedFrame_Renders_TranslatedText_InGreenBox()
    {
        var state = new OverlayState();
        Services.AddSingleton(state);
        var cut = RenderComponent<ReTran_App.Components.Pages.Overlay>();
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow, new[]
        {
            new OverlayBox(0.1, 0.2, 0.3, 0.06, "こんにちは", "Xin chào"),
        }));
        cut.WaitForAssertion(() => Assert.Contains("Xin chào", cut.Markup));
        var box = cut.Find("div[style*='position:absolute']");
        Assert.Contains("left:10%", box.GetAttribute("style"));
        Assert.Contains("#22c55e", box.GetAttribute("style"));
        Assert.DoesNotContain("こんにちは", cut.Markup); // chỉ hiện bản dịch, không hiện gốc
    }
}
```

Lưu ý implementer (đọc kỹ, không skip):
- `State.Changed` fire trên luồng timer/test — component dùng `InvokeAsync(StateHasChanged)` nên render async: DÙNG `cut.WaitForAssertion`, không assert markup ngay sau Publish.
- `b.X*100%` render dạng `10%` hay `10.0%`? Razor in `double` theo invariant? Kiểm tra markup thật khi chạy — nếu là `10%` thì giữ, nếu khác thì sửa assert cho khớp (ghi rõ đã làm gì).
- Nếu bUnit 2.11.3 không chạy được trên net10 (lỗi load), dừng lại báo BLOCKED kèm message — không downgrade TFM, không đổi package.

- [ ] **Step 2: Run test**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter OverlayComponentTests`
Expected: PASS (2/2). Nếu FAIL vì format số (10% vs 10.0%) → sửa assert, chạy lại, ghi vào report.

- [ ] **Step 3: Run full suite** — Expected: xanh (20 + 2 = 22).

- [ ] **Step 4: Commit**

```bash
git add "ReTran.Tests/OverlayComponentTests.cs"
git commit -m "test(app): bUnit Overlay page renders translated boxes"
```

---

### Task 3: bUnit `Home.razor` — Demo buttons, Copy, theme (JS giả lập)

**Files:**
- Create: `ReTran.Tests/HomeComponentTests.cs`

**Interfaces:**
- Consumes: `Home` (`@page "/"`, injects `OverlayState`, `OverlayFeedServer`, `JsInteropService`, `IJSRuntime`); `OverlayFeedServer(OverlayState, port: 0)` CHƯA start (Port == 0 → hiện "feed not running", đúng để test); bUnit `JSInterop.SetupModule`.

- [ ] **Step 1: Write the test**

```csharp
using bunit;
using Microsoft.Extensions.DependencyInjection;
using ReTran_App.Core;
using ReTran_App.Services;
using Xunit;

public class HomeComponentTests : TestContext
{
    private static void AddHomeServices(TestContext ctx)
    {
        var state = new OverlayState();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(new OverlayFeedServer(state, port: 0)); // unstarted
        ctx.Services.AddSingleton(new JsInteropService());
    }

    [Fact]
    public void StartDemo_RunningStatus_And_StopDemo_Stops()
    {
        AddHomeServices(this);
        var cut = RenderComponent<ReTran_App.Components.Pages.Home>();
        cut.Find("section:nth-of-type(3) button:first-child").Click();
        cut.WaitForAssertion(() => Assert.Contains("đang chạy", cut.Markup));
        cut.Find("section:nth-of-type(3) button:last-child").Click();
        cut.WaitForAssertion(() => Assert.Contains("đã dừng", cut.Markup));
    }

    [Fact]
    public void CopyUrl_Calls_CopyText_WithObsUrl()
    {
        AddHomeServices(this);
        var module = JSInterop.SetupModule("./build/assets/main.js");
        module.SetupVoid("copyText", _ => true).SetResult();
        var cut = RenderComponent<ReTran_App.Components.Pages.Home>();
        cut.Find("section:nth-of-type(4) button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Đã sao chép", cut.Markup));
        module.VerifyInvoke("copyText");
    }

    [Fact]
    public void ThemeButton_Sets_StatusText()
    {
        AddHomeServices(this);
        var module = JSInterop.SetupModule("./build/assets/main.js");
        module.SetupVoid("setTheme", _ => true).SetResult();
        var cut = RenderComponent<ReTran_App.Components.Pages.Home>();
        var buttons = cut.FindAll("section:nth-of-type(5) button");
        buttons[1].Click(); // Tối / Dark
        cut.WaitForAssertion(() => Assert.Contains("Theme: dark.", cut.Markup));
        module.VerifyInvoke("setTheme");
    }
}
```

Lưu ý implementer:
- `RenderComponent` trigger `OnAfterRender` → `Js.Initialize(JS)` với JS runtime của bUnit — loose mode mặc định phải đủ; nếu NRE vì module import (`InvokeAsync<IJSObjectReference>("import")` trả null ở loose mode) thì DÙNG `JSInterop.SetupModule("./build/assets/main.js")` ngay từ đầu test 1 nữa (ghi rõ).
- Selector `nth-of-type` giòn nếu Home.razor đổi thứ tự section — kiểm tra `Home.razor` hiện tại trước khi chạy; nếu lệch, chọn selector theo text (vd tìm button chứa "Bắt đầu Demo") và ghi rõ.
- `VerifyInvoke` API đúng của bUnit 2.x là `module.VerifyInvoke("copyText")` — nếu signature khác (cần `Times`), đọc lỗi compiler và sửa đúng, ghi vào report.

- [ ] **Step 2: Run test** — `--filter HomeComponentTests`, Expected: PASS (3/3).
- [ ] **Step 3: Run full suite** — Expected: xanh (22 + 3 = 25).
- [ ] **Step 4: Commit** — `git add "ReTran.Tests/HomeComponentTests.cs"` + `git commit -m "test(app): bUnit Home page (demo, copy URL, theme)"`.

---

### Task 4: Appium smoke — App thật, Start Core → Ping → pong (skip êm khi thiếu hạ tầng)

**Files:**
- Create: `ReTran.Tests/AppiumSmokeTests.cs`

**Interfaces:**
- Consumes: KHÔNG gì mới — dùng App exe đã build + Core exe đã build + Appium server ngoài (không đụng code sản phẩm).

- [ ] **Step 1: Ghi infra vào report trước khi code** — kiểm tra trên máy: `node --version`, `appium --version` (npm i -g appium), driver windows (`appium driver list --installed` cần `windows`), build App (`dotnet build "ReTran.App/ReTran.App.csproj" -f net10.0-windows10.0.19041.0`) + Core (`dotnet build "ReTran.Core/ReTran.Core.csproj" -f net10.0-windows`), set `RETRAN_CORE_EXE` trỏ Core exe. Ghi từng kết quả vào report — thiếu món nào thì test PHẢI skip vì món đó.

- [ ] **Step 2: Write the test** (1 smoke duy nhất, timeout İşçin tổng 120s):

```csharp
using System.Net.Sockets;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Windows;
using Xunit;

public class AppiumSmokeTests
{
    private static bool AppiumUp()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 4723).Wait(2000) && c.Connected;
        }
        catch { return false; }
    }

    private static string? FindAppExe()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null)
        {
            string p = Path.Combine(d.FullName, "ReTran.App", "bin", "Debug",
                "net10.0-windows10.0.19041.0", "win-x64", "ReTran.App.exe");
            if (File.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }

    [Fact]
    public void StartCore_Ping_ShowsPong()
    {
        if (!AppiumUp()) return; // không có Appium server -> skip
        if (FindAppExe() is not string appExe) return; // chưa build App -> skip
        var opts = new AppiumOptions();
        opts.AddAdditionalAppiumOption("app", appExe);
        opts.AddAdditionalAppiumOption("automationName", "Windows");
        using var driver = new WindowsDriver(AppiumUris.Localhost, opts, TimeSpan.FromSeconds(60));
        try
        {
            var start = driver.FindElement(OpenQA.Selenium.By.Name("Start Core"));
            start.Click();
            var status = driver.FindElement(OpenQA.Selenium.By.Name("Core: started"));
            Assert.NotNull(status); // implicit wait từ driver; nếu flaky, thêm WebDriverWait polling 10s
            driver.FindElement(OpenQA.Selenium.By.Name("Ping")).Click();
            var pong = driver.FindElement(OpenQA.Selenium.By.Name("Ping: pong"));
            Assert.NotNull(pong);
        }
        finally
        {
            driver.Quit();
        }
    }
}
```

Lưu ý implementer:
- `AppiumUris.Localhost` tồn tại ở Appium.WebDriver 9 không? Nếu không, dùng `new Uri("http://127.0.0.1:4723/")` — đọc lỗi compiler, sửa đúng, ghi report.
- Tên accessible của MAUI `Label`/`Button` = `Text` — nếu driver không tìm thấy "Core: started" (Label động), fallback: đọc mọi element có Name chứa "Core:"/`"Ping:"` và assert Contains — ghi rõ đã làm gì.
- `driver.Quit()` trong `finally` để không kẹt App. Test này KHÔNG chạy trong CI (không có server) — skip là hành vi đúng, không phải trốn test.

- [ ] **Step 3: Run test** — CÓ server: PASS thật; KHÔNG server: PASS-skip (ghi rõ trường hợp nào vào report).
- [ ] **Step 4: Commit** — `git add "ReTran.Tests/AppiumSmokeTests.cs"` + `git commit -m "test(app): Appium smoke Start Core-Ping (skips without server)"`.

---

## Test Acceptance (end-to-end)

1. `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0` — toàn bộ xanh (17 cũ + 3 + 2 + 3 + 1 = 26; Appium PASS-skip nếu không có server).
2. Không test nào fail vì môi trường (mọi phụ thuộc ngoài đều có guard skip + lý do trong report).
3. Không sửa code sản phẩm trong cả 4 tasks (`git status` chỉ có 4 file test mới).

## Self-Review notes

- Spec coverage: user yêu cầu xUnit → Task 1; bUnit → Tasks 2–3; Appium → Task 4. Mỗi framework ít nhất 1 file test chạy được.
- Placeholder scan: không có TBD/TODO; mọi "nếu X thì Y" đều có lệnh cụ thể + expected output + fallback được ghi sẵn.
- Type consistency: `OverlayBox(X,Y,W,H,Text,Translated)`, `OverlayFrame(FrameWidth,FrameHeight,TimestampUtc,Boxes)`, `OverlayState.Publish/Latest/Changed`, `OverlayFeedServer(state, port)`, `JsInteropService.InvokeVoidAsync/SetThemeAsync/SetColorAsync`, SSE contract — khớp code hiện tại đã đọc (`Home.razor` 179 dòng, `Overlay.razor` 25 dòng).
- Rủi ro chính: (1) bUnit 2.11.3 trên net10 — Task 2 là canary, fail thì BLOCKED cả track bUnit; (2) Appium server/driver chưa cài — Task 4 vẫn PASS-skip đúng thiết kế, cài sau để lấy bằng chứng thật.
