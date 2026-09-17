# App UI (Control + Overlay/OBS) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** App có 2 trang Blazor — `/` điều khiển (chọn nguồn, Start/Stop, settings, nút copy URL OBS) và `/overlay` hiển thị kết quả dịch nền trong suốt — kèm HTTP server local (`127.0.0.1:17863`) serve overlay cho OBS Browser Source qua SSE.

**Architecture:** Một nguồn dữ liệu duy nhất: `OverlayState` (Singleton, giữ frame mới nhất + drop cũ). Core đẩy notification `frame.ocr_result`/`frame.translated` qua `CoreProcessClient` vào `OverlayState`; trang `/overlay` (Blazor, xem nội bộ) và `OverlayFeedServer` (HttpListener + SSE, phục vụ OBS) cùng đọc từ đó. Trang overlay cho OBS là HTML tĩnh + vanilla JS (không qua build Vite) để OBS Browser Source tải độc lập với WebView của App.

**Tech Stack:** .NET 10 MAUI Blazor Hybrid (`net10.0-windows10.0.19041.0`), Razor, `HttpListener` (stdlib, không thêm package), xUnit (`ReTran.App.Core.Tests`, TFM `net10.0-windows`), Vite+Tailwind hiện có (chỉ cho trang điều khiển, không đụng config build).

## Global Constraints

- C# TFM App: `net10.0-windows10.0.19041.0`; test TFM: `net10.0-windows` (khớp `ReTran.App.Core.Tests.csproj` hiện tại).
- C# public API: doc comment song ngữ (Anh câu đầu, Việt ngay sau) theo AGENTS.md.
- Log qua DI `Microsoft.Extensions.Logging`, không `Console.WriteLine` (stdout/stderr của App không phải JSON-RPC nhưng giữ kỷ luật log chung).
- Không layer nào phụ thuộc concrete engine: UI chỉ thấy `OverlayState` + `CoreProcessClient`, không gọi thẳng Core CLI hay sidecar.
- HTTP server CHỈ bind `127.0.0.1` (không `*`, không `+`), port mặc định `17863` (đổi được qua settings, fallback port tự động khi bận).
- TDD: mọi logic unit-test được (state, SSE, parse) phải có xUnit test thật; trang Razor verify bằng build + smoke chạy thật.
- Build CHỈ Windows TFM. Một commit mỗi task, message `feat(app): ...`.
- KHÔNG đụng `ReTran Core/`, `Docs/` (ngoài plan này), `vite.config.js`; KHÔNG sửa `MainPage.xaml` (thanh Start/Ping/Stop native giữ nguyên làm đường điều khiển dự phòng).

## Scope honesty (đọc trước khi code)

- Core CHƯA có runtime methods `capture.*` (M1 chỉ có CLI one-shot) và CHƯA emit notification `frame.*`. Vì vậy plan này verify bằng **demo feed nội bộ** (`DemoFeed`: sinh box giả theo timer) + `core.ping` thật; đấu nối Core live là milestone riêng (gate ghi rõ ở Task 6). Không claim "live từ Core" khi chưa có.
- EF/SQLite, provider keys, translation cache: NGOÀI plan này (M3).

## File Structure (locked)

```
ReTran App/
├── Services/
│   ├── JsInteropService.cs       # FIX: namespace + DI (Task 1)
│   └── OverlayFeedServer.cs      # HttpListener 127.0.0.1:17863 + SSE /overlay + /events (Task 4)
├── Core/
│   ├── OverlayState.cs           # Singleton: Latest OverlayFrame + event (Task 2)
│   └── DemoFeed.cs               # sinh frame giả theo timer (Task 2, implements IOverlayFeed)
├── Components/
│   ├── Pages/Home.razor          # trang điều khiển / (Task 5)
│   ├── Pages/Overlay.razor       # trang overlay /overlay (Task 3)
│   └── Layout/OverlayLayout.razor# layout trống, nền trong suốt (Task 3)
├── wwwroot/
│   └── overlay/
│       ├── overlay.html          # trang tĩnh cho OBS Browser Source (Task 4)
│       └── overlay.js            # SSE client + vẽ boxes (vanilla JS, Task 4)
└── MauiProgram.cs                # đăng ký Singleton (Task 1)
ReTran App/vite-project/src/
├── css/theme.css                 # THÊM data-color pairs (Task 6)
└── ts/theme-manager.ts           # THÊM initTheme + persist localStorage (Task 6)
ReTran.App.Core.Tests/
└── OverlayStateTests.cs + OverlayFeedServerTests.cs
```

---

### Task 1: Fix `JsInteropService` (namespace + DI) — nền cho trang điều khiển

**Files:**
- Modify: `ReTran App/Services/JsInteropService.cs` (dòng 3: namespace)
- Modify: `ReTran App/MauiProgram.cs` (đăng ký Singleton)

**Interfaces:**
- Consumes: `IJSRuntime` (Blazor WebView), `./build/assets/main.js` (Vite lib output — đã khớp `vite.config.js`, không đổi).
- Produces: `ReTran_App.Services.JsInteropService` (Singleton): `bool IsWebViewReady`, `event Action? OnWebViewReady`, `void NotifyWebViewReady()`, `void Initialize(IJSRuntime)`, `InvokeVoidAsync/InvokeAsync<T>`, `GetViewportSizeAsync()`.

- [ ] **Step 1: Sửa namespace + doc song ngữ**

Đổi dòng 1–5 của `Services/JsInteropService.cs` từ:
```csharp
using Microsoft.JSInterop;

namespace T3AI.Services
{
```
thành:
```csharp
using Microsoft.JSInterop;

namespace ReTran_App.Services;

/// <summary>
/// Bridges Blazor pages and the Vite-built JS module (overlay preview, clipboard, viewport).
/// Cầu nối giữa trang Blazor và module JS do Vite build (preview overlay, clipboard, viewport).
/// </summary>
public class JsInteropService : IAsyncDisposable
```
(Giữ nguyên toàn bộ phần còn lại của file — API đã đúng, chỉ sai namespace + thiếu doc class.)

- [ ] **Step 2: Đăng ký DI trong `MauiProgram.cs`**

Thêm sau dòng `builder.Services.AddMauiBlazorWebView();`:
```csharp
builder.Services.AddSingleton<ReTran_App.Services.JsInteropService>();
builder.Services.AddSingleton<ReTran_App.Core.OverlayState>();
```
(`OverlayState` chưa tồn tại — build sẽ đỏ ở dòng này cho tới Task 2; đó là RED có chủ ý. Nếu muốn build xanh ngay, comment dòng `OverlayState` lại và mở ra ở Task 2.)

- [ ] **Step 3: Verify build**

Run: `dotnet build "ReTran App/ReTran App.csproj" -f net10.0-windows10.0.19041.0`
Expected: PASS (sau khi mở comment dòng OverlayState ở Task 2; ở Task 1 cho phép đỏ với đúng 1 lỗi `OverlayState not found` — ghi lại output).

- [ ] **Step 4: Commit**

```bash
git add "ReTran App/Services/JsInteropService.cs" "ReTran App/MauiProgram.cs"
git commit -m "feat(app): fix JsInteropService namespace + DI registration"
```

---

### Task 2: `OverlayState` + `DemoFeed` — một nguồn dữ liệu duy nhất

**Files:**
- Create: `ReTran App/Core/OverlayState.cs`
- Create: `ReTran App/Core/DemoFeed.cs`
- Test: `ReTran.App.Core.Tests/OverlayStateTests.cs`

**Interfaces:**
- Produces: `sealed record OverlayBox(double X, double Y, double W, double H, string Text, string Translated)` (tọa độ tương đối 0–1 theo frame); `sealed record OverlayFrame(int FrameWidth, int FrameHeight, DateTime TimestampUtc, IReadOnlyList<OverlayBox> Boxes)`; `interface IOverlayFeed { void Start(); void Stop(); }`; `sealed class OverlayState { OverlayFrame? Latest { get; } event Action? Changed; void Publish(OverlayFrame f); }` (Publish thay frame cũ — drop-if-busy, thread-safe qua lock); `sealed class DemoFeed(OverlayState state, int intervalMs = 500) : IOverlayFeed` (timer sinh 2 box giả di chuyển ngang, text `こんにちは` → `Xin chào`).

- [ ] **Step 1: Write the failing test**

Create `ReTran.App.Core.Tests/OverlayStateTests.cs`:
```csharp
using ReTran_App.Core;
using Xunit;

public class OverlayStateTests
{
    [Fact]
    public void Publish_Twice_WithoutRead_DropsFirst()
    {
        var state = new OverlayState();
        int events = 0;
        state.Changed += () => events++;
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.1, 0.1, 0.2, 0.05, "a", "A") }));
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.2, 0.2, 0.2, 0.05, "b", "B") }));
        Assert.Equal("b", state.Latest!.Boxes[0].Text);
        Assert.Equal(2, events);
    }

    [Fact]
    public void DemoFeed_Publishes_Within2Seconds()
    {
        var state = new OverlayState();
        using var feed = new DemoFeed(state, intervalMs: 200);
        feed.Start();
        SpinWait.SpinUntil(() => state.Latest is not null, 2000);
        feed.Stop();
        Assert.NotNull(state.Latest);
        Assert.NotEmpty(state.Latest!.Boxes);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "ReTran.App.Core.Tests/ReTran.App.Core.Tests.csproj" -f net10.0-windows --filter OverlayStateTests`
Expected: FAIL — `OverlayState`, `DemoFeed` do not exist (compile error).

- [ ] **Step 3: Implement `OverlayState.cs` + `DemoFeed.cs`**

Create `ReTran App/Core/OverlayState.cs`:
```csharp
namespace ReTran_App.Core;

/// <summary>A translated box in relative coordinates (0–1 of frame size). Hộp đã dịch theo tọa độ tương đối (0–1 kích thước frame).</summary>
public sealed record OverlayBox(double X, double Y, double W, double H, string Text, string Translated);

/// <summary>One overlay snapshot: frame size + translated boxes. Một snapshot overlay: kích thước frame + các hộp đã dịch.</summary>
public sealed record OverlayFrame(int FrameWidth, int FrameHeight, DateTime TimestampUtc, IReadOnlyList<OverlayBox> Boxes);

/// <summary>A frame producer pushing into an OverlayState. Nguồn frame đẩy vào OverlayState.</summary>
public interface IOverlayFeed : IDisposable
{
    /// <summary>Starts producing frames. Bắt đầu sinh frame.</summary>
    void Start();
    /// <summary>Stops producing frames. Dừng sinh frame.</summary>
    void Stop();
}

/// <summary>
/// Single source of truth for the overlay: keeps only the latest frame (older ones dropped).
/// Nguồn dữ liệu duy nhất cho overlay: chỉ giữ frame mới nhất (frame cũ bị drop).
/// </summary>
public sealed class OverlayState
{
    private readonly object _lock = new();
    private OverlayFrame? _latest;

    /// <summary>Raised on every Publish (on the publisher thread). Kích hoạt mỗi lần Publish (trên luồng của publisher).</summary>
    public event Action? Changed;

    /// <summary>The latest frame, or null when nothing published yet. Frame mới nhất, hoặc null khi chưa có gì.</summary>
    public OverlayFrame? Latest { get { lock (_lock) return _latest; } }

    /// <summary>Stores a frame, dropping the previous un-read one. Lưu frame mới, drop frame trước đó chưa ai đọc.</summary>
    public void Publish(OverlayFrame frame)
    {
        lock (_lock) _latest = frame;
        Changed?.Invoke();
    }
}
```

Create `ReTran App/Core/DemoFeed.cs`:
```csharp
namespace ReTran_App.Core;

/// <summary>
/// Demo feed: timer-generated moving boxes for UI verification before the Core emits live frames.
/// Feed demo: sinh box di chuyển theo timer để verify UI trước khi Core có frame live.
/// </summary>
public sealed class DemoFeed : IOverlayFeed
{
    private readonly OverlayState _state;
    private readonly int _intervalMs;
    private Timer? _timer;
    private int _tick;
    private bool _disposed;

    /// <summary>Creates a demo feed pushing into <paramref name="state"/> every <paramref name="intervalMs"/> ms.</summary>
    public DemoFeed(OverlayState state, int intervalMs = 500)
    {
        _state = state;
        if (intervalMs < 50) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _intervalMs = intervalMs;
    }

    /// <summary>Starts the timer (no-op when already started). Bắt đầu timer (không làm gì nếu đã chạy).</summary>
    public void Start() => _timer ??= new Timer(_ => Tick(), null, 0, _intervalMs);

    /// <summary>Stops the timer. Dừng timer.</summary>
    public void Stop() { _timer?.Dispose(); _timer = null; }

    private void Tick()
    {
        int t = Interlocked.Increment(ref _tick);
        double x = (t % 20) / 20.0 * 0.6;
        _state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow, new[]
        {
            new OverlayBox(x, 0.2, 0.3, 0.06, "こんにちは", "Xin chào"),
            new OverlayBox(0.1, 0.7, 0.25, 0.05, "スタート", "Bắt đầu"),
        }));
    }

    public void Dispose() { if (!_disposed) { _disposed = true; Stop(); } }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "ReTran.App.Core.Tests/ReTran.App.Core.Tests.csproj" -f net10.0-windows`
Expected: PASS (2 new + toàn bộ test M0 cũ xanh).

- [ ] **Step 5: Commit**

```bash
git add "ReTran App/Core/OverlayState.cs" "ReTran App/Core/DemoFeed.cs" "ReTran.App.Core.Tests/OverlayStateTests.cs"
git commit -m "feat(app): OverlayState single source + DemoFeed for UI verification"
```

---

### Task 3: Trang `/overlay` (Blazor, nền trong suốt) — render dùng chung cho preview nội bộ

**Files:**
- Create: `ReTran App/Components/Layout/OverlayLayout.razor`
- Create: `ReTran App/Components/Pages/Overlay.razor`
- Modify: `ReTran App/Components/Routes.razor` (đọc file trước — thêm route, không sửa route hiện có)

**Interfaces:**
- Consumes: `OverlayState.Latest/Changed` (Task 2).
- Produces: route `/overlay` hiển thị boxes theo % (absolute-positioned divs trên container full-viewport trong suốt).

- [ ] **Step 1: Tạo layout trống + trang overlay**

Create `Components/Layout/OverlayLayout.razor`:
```razor
@inherits LayoutComponentBase
<!—- Empty transparent layout for the overlay route (no NavMenu). Layout trống trong suốt cho route overlay (không NavMenu). -->
<div style="margin:0;background:transparent;width:100vw;height:100vh;overflow:hidden;position:relative;">
@Body
</div>
```

Create `Components/Pages/Overlay.razor`:
```razor
@page "/overlay"
@layout ReTran_App.Components.Layout.OverlayLayout
@inject ReTran_App.Core.OverlayState State
@implements IDisposable

@foreach (var b in _boxes)
{
    <div style="position:absolute;left:@(b.X*100)%;top:@(b.Y*100)%;width:@(b.W*100)%;border:2px solid #22c55e;background:rgba(0,0,0,.45);color:#fff;padding:2px 6px;font-size:clamp(12px,2.2vw,28px);">
        @b.Translated
    </div>
}

@code {
    private IReadOnlyList<ReTran_App.Core.OverlayBox> _boxes = Array.Empty<ReTran_App.Core.OverlayBox>();

    protected override void OnInitialized()
    {
        Refresh();
        State.Changed += OnChanged;
    }

    private void OnChanged() => InvokeAsync(() => { Refresh(); StateHasChanged(); });
    private void Refresh() => _boxes = State.Latest?.Boxes ?? _boxes;
    public void Dispose() => State.Changed -= OnChanged;
}
```

- [ ] **Step 2: Đăng ký route** — đọc `Components/Routes.razor` rồi thêm (Blazor `@page` tự регистрируется qua router — thường KHÔNG cần sửa Routes; nếu file có danh sách tĩnh thì thêm dòng tương ứng; ghi rõ đã làm gì vào report).

- [ ] **Step 3: Verify** — build App + chạy App, bật `DemoFeed`, mở `/overlay` trong WebView (tạm đổi `HostPage`? KHÔNG — chỉ cần navigate: thêm link tạm trong Home hoặc dùng `blazorWebView` Url). Ghi lại: boxes xanh + chữ "Xin chào"/"Bắt đầu" di chuyển. Xóa mọi thứ tạm sau khi verify.

Run: `dotnet build "ReTran App/ReTran App.csproj" -f net10.0-windows10.0.19041.0`
Expected: build succeeds, 0 error.

- [ ] **Step 4: Commit**

```bash
git add "ReTran App/Components/Layout/OverlayLayout.razor" "ReTran App/Components/Pages/Overlay.razor"
git commit -m "feat(app): transparent /overlay page rendering translated boxes"
```

---

### Task 4: `OverlayFeedServer` + trang tĩnh cho OBS (HttpListener + SSE, vanilla JS)

**Files:**
- Create: `ReTran App/Services/OverlayFeedServer.cs`
- Create: `ReTran App/wwwroot/overlay/overlay.html`
- Create: `ReTran App/wwwroot/overlay/overlay.js`
- Test: `ReTran.App.Core.Tests/OverlayFeedServerTests.cs`

**Interfaces:**
- Consumes: `OverlayState.Latest` (Task 2).
- Produces: `sealed class OverlayFeedServer(OverlayState state, int port = 17863) : IAsyncDisposable { int Port { get; } Task StartAsync(CancellationToken ct); }` — `GET /overlay` → `overlay.html`; `GET /overlay/overlay.js` → file; `GET /events` → SSE `data: {json}\n\n` mỗi lần `State.Changed` (giữ kết nối, heartbeat `:ping` mỗi 15s). Bind `127.0.0.1` only; port bận → +1 tới khi được (tối đa 10 lần) và cập nhật `Port`.

- [ ] **Step 1: Write the failing test**

Create `ReTran.App.Core.Tests/OverlayFeedServerTests.cs`:
```csharp
using System.Net.Http;
using System.Threading;
using ReTran_App.Core;
using Xunit;

public class OverlayFeedServerTests
{
    [Fact]
    public async Task Serves_OverlayHtml_And_SseEvent()
    {
        var state = new OverlayState();
        await using var server = new ReTran_App.Services.OverlayFeedServer(state, port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var http = new HttpClient();
        string html = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/overlay", cts.Token);
        Assert.Contains("overlay-root", html);

        using var sse = await http.GetStreamAsync($"http://127.0.0.1:{server.Port}/events", cts.Token);
        using var reader = new StreamReader(sse);
        var readTask = reader.ReadLineAsync();
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.1, 0.1, 0.2, 0.05, "a", "A") }));
        string? first = await readTask.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Equal("data: " + "{\"boxes\":[{\"x\":0.1}]}", first!.Substring(0, 24));
    }
}
```
(Lưu ý: assert chỉ kiểm tra prefix `data: {"boxes":[{"x":0.1` để tránh brittle với format số JSON đầy đủ.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "ReTran.App.Core.Tests/ReTran.App.Core.Tests.csproj" -f net10.0-windows --filter OverlayFeedServerTests`
Expected: FAIL — `OverlayFeedServer` does not exist.

- [ ] **Step 3: Implement server + static page**

`Services/OverlayFeedServer.cs` (khung bắt buộc — chi tiết khi code): `HttpListener` với prefix `http://127.0.0.1:{port}/`; loop `GetContextAsync` trên Task nền; route `/overlay` → đọc `wwwroot/overlay/overlay.html` (đường dẫn resolve từ `AppContext.BaseDirectory` đi lên tới khi thấy `wwwroot`, fallback cạnh exe); `/overlay/overlay.js` tương tự với content-type `text/javascript`; `/events` → `text/event-stream`, flush mỗi event + heartbeat 15s; port bận (`HttpListenerException` 183/access denied) → port+1 retry tối đa 10; `DisposeAsync` dừng listener. Mọi public member doc song ngữ.

`wwwroot/overlay/overlay.html`:
```html
<!doctype html>
<html><head><meta charset="utf-8"><style>
html,body{margin:0;background:transparent;width:100vw;height:100vh;overflow:hidden}
#overlay-root{position:relative;width:100%;height:100%}
.box{position:absolute;border:3px solid #22c55e;background:rgba(0,0,0,.45);color:#fff;padding:2px 8px;font:600 clamp(14px,2.4vw,32px)/1.3 system-ui}
</style></head>
<body><div id="overlay-root"></div><script src="./overlay.js"></script></body></html>
```

`wwwroot/overlay/overlay.js` (vanilla, không build):
```js
const root = document.getElementById("overlay-root");
const es = new EventSource("./events");
es.onmessage = (e) => {
  const frame = JSON.parse(e.data);
  root.replaceChildren();
  for (const b of frame.boxes) {
    const d = document.createElement("div");
    d.className = "box";
    d.style.left = (b.x * 100) + "%"; d.style.top = (b.y * 100) + "%";
    d.style.width = (b.w * 100) + "%";
    d.textContent = b.t;
    root.appendChild(d);
  }
};
```
(JSON contract: `{"boxes":[{"x":..,"y":..,"w":..,"h":..,"t":"translated"}]}` — server serialize từ `OverlayBox`, field `Translated` → `t`.)

- [ ] **Step 4: Run tests + real OBS smoke**

Run: `dotnet test "ReTran.App.Core.Tests/ReTran.App.Core.Tests.csproj" -f net10.0-windows`
Expected: PASS (toàn bộ xanh).
Smoke: chạy App + `DemoFeed` → mở OBS → Browser Source URL `http://127.0.0.1:17863/overlay` (1920×1080) → thấy boxes xanh + chữ demo cập nhật. Chụp screenshot OBS làm bằng chứng report.

- [ ] **Step 5: Commit**

```bash
git add "ReTran App/Services/OverlayFeedServer.cs" "ReTran App/wwwroot/overlay/overlay.html" "ReTran App/wwwroot/overlay/overlay.js" "ReTran.App.Core.Tests/OverlayFeedServerTests.cs"
git commit -m "feat(app): local overlay HTTP+SSE server for OBS Browser Source"
```

---

### Task 5: Trang điều khiển `/` (Blazor) — nguồn, Start/Stop, URL OBS

**Files:**
- Modify: `ReTran App/Components/Pages/Home.razor` (viết lại từ template)
- Test: manual smoke (không xUnit — Razor page; verify bằng chạy App thật)

**Interfaces:**
- Consumes: `OverlayState`, `OverlayFeedServer.Port` (Tasks 2, 4), `CoreProcessClient` (M0), `JsInteropService.InvokeVoidAsync("copyText", url)` (cần export `copyText` trong `vite-project/src/ts/main.ts` — THÊM function, không sửa function hiện có).
- Produces: route `/`: selects nguồn (Screen m0/m1 — lấy từ `capture list-monitors`? CHƯA có RPC → options tĩnh `Screen 0/1` + `Window (HWND)` textbox), select provider (Google/DeepL/Local/LLM — tĩnh, lưu vào `AppSettings`), nút Start/Stop DemoFeed (label rõ "Demo (Core live ở milestone sau)"), dòng URL OBS + nút Copy, iframe preview `http://127.0.0.1:{port}/overlay` 480×270.

- [ ] **Step 1: Thêm `copyText` vào `vite-project/src/ts/main.ts`** (append, không sửa code hiện có):

```ts
/** Copies text to the clipboard. Sao chép văn bản vào clipboard. */
export async function copyText(text: string): Promise<void> {
  await navigator.clipboard.writeText(text);
}
```
Run: `npm run build` từ `ReTran App/vite-project` → `../wwwroot/build` mới. Expected: build succeeds.

- [ ] **Step 2: Viết lại `Home.razor`** (khung bắt buộc): `@inject OverlayState/OverlayFeedServer/JsInteropService`; selects + textbox HWND; Start → `new DemoFeed(State).Start()` (giữ reference để Stop), Stop → dispose; URL `http://127.0.0.1:{Port}/overlay` + nút Copy gọi `copyText`; `<iframe src=url width=480 height=270>` preview; status line. Mọi label song ngữ Việt/Anh (UI hướng tới user Việt — tiếng Việt trước ở UI, ngược với doc comment code).

- [ ] **Step 3: Verify smoke** — chạy App: Start Demo → iframe preview có boxes; Copy → paste ra URL đúng; Stop → đứng hình. Ghi screenshot.

- [ ] **Step 4: Commit**

```bash
git add "ReTran App/Components/Pages/Home.razor" "ReTran App/vite-project/src/ts/main.ts" "ReTran App/wwwroot/build"
git commit -m "feat(app): control page with source select, demo pipeline, OBS URL"
```

---

### Task 6: Theme (data-color pairs + tích hợp theme-manager vào UI)

**Files:**
- Modify: `ReTran App/vite-project/src/css/theme.css` (thêm pairs)
- Modify: `ReTran App/vite-project/src/ts/theme-manager.ts` (thêm `initTheme`, persist localStorage)
- Modify: `ReTran App/Services/JsInteropService.cs` (thêm 2 wrappers gọi qua module đã import)
- Modify: `ReTran App/Components/Pages/Home.razor` (thêm cụm toggle theme + dots màu — append vào layout Task 5, không sửa logic Task 5)
- Test: manual smoke (không xUnit — theme là JS/DOM; verify bằng chạy App thật + screenshot)

**Interfaces:**
- Consumes: `applyTheme/applyColor` đã re-export trong `main.ts` (không sửa dòng export); `JsInteropService.InvokeVoidAsync` (Task 1).
- Produces: 4 data-color pairs (`default`, `emerald`, `violet`, `sunset`); `initTheme()` đọc localStorage và apply khi module load; `JsInteropService.SetThemeAsync(string theme)` (`light`/`dark`/`system`) và `SetColorAsync(string colorId)` (vừa gọi JS vừa persist — persist nằm trong `theme-manager`, C# chỉ forward).

- [ ] **Step 1: Thêm 3 data-color pairs vào `theme.css`** (append sau khối `[data-color="default"]` hiện có, không sửa khối đó):

```css
/* EMERALD */
[data-color="emerald"] {
    --primary: #16a34a;
    --secondary: #64748b;
}

/* VIOLET */
[data-color="violet"] {
    --primary: #7c3aed;
    --secondary: #64748b;
}

/* SUNSET */
[data-color="sunset"] {
    --primary: #ea580c;
    --secondary: #57534c;
}
```

- [ ] **Step 2: Thêm `initTheme` + persist vào `theme-manager.ts`** (append, không sửa `applyTheme`/`applyColor` hiện có):

```ts
const THEME_KEY = "retran-theme";
const COLOR_KEY = "retran-color";

/** Applies a theme and persists the choice. Áp dụng theme và lưu lựa chọn. */
export function setTheme(theme: string): void {
  localStorage.setItem(THEME_KEY, theme);
  applyTheme(theme);
}

/** Applies a color pair and persists the choice. Áp dụng cặp màu và lưu lựa chọn. */
export function setColor(colorId: string): void {
  localStorage.setItem(COLOR_KEY, colorId);
  applyColor(colorId);
}

/** Restores saved theme/color on startup. Khôi phục theme/màu đã lưu khi khởi động. */
export function initTheme(): void {
  applyTheme(localStorage.getItem(THEME_KEY) ?? "system");
  applyColor(localStorage.getItem(COLOR_KEY) ?? "default");
}

initTheme();
```
(`main.ts` KHÔNG cần sửa — `export { applyTheme, applyColor }` giữ nguyên; Blazor gọi `setTheme/setColor/initTheme` qua module dynamic import đã có trong `JsInteropService`. Lưu ý: `initTheme()` tự chạy khi module load nên trang overlay tĩnh không bị ảnh hưởng — file này chỉ bundle vào `main.js` của WebView, còn `/overlay` cho OBS dùng `overlay.js` riêng.)

- [ ] **Step 3: Thêm 2 wrappers vào `JsInteropService.cs`** (append methods, doc song ngữ):

```csharp
/// <summary>Sets the UI theme (light/dark/system) via the Vite module. Đặt theme giao diện (sáng/tối/theo hệ thống) qua module Vite.</summary>
public async ValueTask SetThemeAsync(string theme) => await InvokeVoidAsync("setTheme", theme);

/// <summary>Sets the accent color pair via the Vite module. Đặt cặp màu nhấn qua module Vite.</summary>
public async ValueTask SetColorAsync(string colorId) => await InvokeVoidAsync("setColor", colorId);
```

- [ ] **Step 4: Thêm cụm theme vào `Home.razor`** (append section, không sửa sections Task 5): 3 nút Light/Dark/System + 4 dots màu (default/emerald/violet/sunset) gọi 2 wrappers trên; dots dùng inline `background:var(--primary)` để tự phản ánh pair đang active.

- [ ] **Step 5: Verify** — `npm run build` từ `vite-project` (expected: succeeds) → build App → chạy App: bấm từng theme/màu, chụp 3 screenshot (light-default, dark-violet, light-sunset); reload App → theme/màu giữ nguyên (localStorage). Ghi vào report.

Run: `dotnet build "ReTran App/ReTran App.csproj" -f net10.0-windows10.0.19041.0`
Expected: 0 error.

- [ ] **Step 6: Commit**

```bash
git add "ReTran App/vite-project/src/css/theme.css" "ReTran App/vite-project/src/ts/theme-manager.ts" "ReTran App/Services/JsInteropService.cs" "ReTran App/Components/Pages/Home.razor" "ReTran App/wwwroot/build"
git commit -m "feat(app): theme pairs + theme-manager integration with persistence"
```

---

### Task 7: Gate milestone Core-live (KHÔNG implement trong plan này — ghi nhận)

Khi Core có runtime methods (`capture.start/stop`, notification `frame.*`), thêm 1 task: `CoreOverlayBridge : IOverlayFeed` subscribe notification qua `CoreProcessClient` → `OverlayState.Publish`; trang `/` chuyển nút Demo → Live. Plan này KHÔNG làm — để tránh claim live giả. Acceptance của plan này chỉ gồm demo feed + ping Core thật.

---

## UI Acceptance (end-to-end)

1. `dotnet build "ReTran App/ReTran App.csproj" -f net10.0-windows10.0.19041.0` — 0 error.
2. `dotnet test "ReTran.App.Core.Tests/ReTran.App.Core.Tests.csproj" -f net10.0-windows` — toàn bộ xanh (M0 + OverlayState 2 + FeedServer 1).
3. Chạy App → Start Demo → `/overlay` trong WebView hiện boxes di chuyển ("Xin chào").
4. OBS Browser Source `http://127.0.0.1:17863/overlay` hiện cùng nội dung (screenshot).
5. Nút Copy cho URL đúng port thực tế (kể cả khi port fallback).
6. Theme: 4 pairs đổi màu thật, light/dark/system chạy, reload giữ nguyên (screenshot).
7. M0 regression: thanh native Start Core → Ping `pong` → Stop vẫn chạy.

## Self-Review notes

- Spec coverage: design §Phần 1 (routes/hosting) → Tasks 1/3/4/5; single source `OverlayState` → Task 2; OBS đường 2 → Task 4 (+ smoke); điều khiển Blazor → Task 5; theme pairs + tích hợp → Task 6. Core-live gate → Task 7 (ghi nhận, không implement).
- Type consistency: `OverlayBox(X,Y,W,H,Text,Translated)`, `OverlayFrame(FrameWidth,FrameHeight,TimestampUtc,Boxes)`, `IOverlayFeed.Start/Stop`, `OverlayState.Publish/Latest/Changed`, `OverlayFeedServer.StartAsync/Port` dùng nhất quán xuyên tasks; JSON SSE `{"boxes":[{"x","y","w","h","t"}]}` khớp `overlay.js`.
- Rủi ro: (1) `HttpListener` cần URL ACL trên Windows khi bind `127.0.0.1` — thường OK không cần admin, nếu `Access denied` thì retry port+1 đã cover; (2) Blazor `@page "/overlay"` trong WebView nội bộ và `overlay.html` tĩnh cho OBS là 2 render path — chấp nhận có chủ ý (WebView cần Blazor binding, OBS cần standalone; cùng đọc `OverlayState`, style đồng bộ bằng tay, ghi chú trong code).
