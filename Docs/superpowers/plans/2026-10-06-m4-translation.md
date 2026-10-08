# M4 Translation (cache + dịch + UI) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** End-to-end dịch chữ trên màn hình — serve mode chạy loop capture→OCR→diff→cache lookup→Google dịch, push `frame.translated` về App hiển thị lên overlay/OBS, kèm trang Cài đặt (provider/src/dst) và nút Start/Stop trên trang điều khiển.

**Architecture:** Đường A theo spec M0 — Core điều phối (diff, lookup, dịch), App giữ cache SQLite. `OcrLoop` được refactor thành `ICycleSink` để serve mode dùng lại y hệt vòng lặp M3; `JsonRpcServer` mở rộng thêm vai trò client (request App) + notifier; App mở rộng `JsonRpcClient` thành 3-nhánh (response/request/notification), EF Core lưu cache, `PipelineController` nối IPC với UI. Google web endpoint là provider đầu tiên, sau khóa sau `ITranslator`.

**Tech Stack:** .NET 10 (`ReTran.Core` net10.0-windows; `ReTran.App` MAUI net10; tests xUnit v2 TFM `net10.0-windows10.0.19041.0` + bUnit), EF Core Sqlite + migrations, `HttpClient` (Google `translate.googleapis.com`, không key).

**Spec:** `Docs/superpowers/specs/2026-10-06-m4-translation-design.md`

## Global Constraints

(Giá trị chép verbatim từ spec đã approve `Docs/superpowers/specs/2026-10-06-m4-translation-design.md`.)

- Spec governs: `Docs/superpowers/specs/2026-10-06-m4-translation-design.md`.
- IPC payload đúng như spec §3 — **không đổi tên/shape**: `core.start`, `core.stop`, `core.list_sources`, `translate.request_cache`, `translate.put`, `frame.translated`, `pipeline.state`.
- `core.start` lỗi: `-32602` params/provider lạ · `-32600` "pipeline already running" · `-32603` sidecar không sẵn sàng / mở capture thất bại. `core.stop` idempotent.
- Context key Core tự tính: window → process name thường + `.exe` (`notepad.exe`), screen → `screen:<monitor>`, không xác định → `unknown`.
- Box toạ độ **normalized 0–1** (khớp `OverlayBox`); sidecar trả quad pixel `[[x,y]×4]` → Core chia theo frame w/h, clamp [0,1].
- `frame.translated` emit **mỗi chu kỳ** kể cả `boxes:[]`; `seq` tăng không reset; `translated:null` khi chưa dịch/lỗi.
- Translate không bao giờ block tick: tối đa **4 request song song**, Google timeout **5 s**, `translate.request_cache` timeout **2 s** (fail = coi hết là miss), không retry trong phiên.
- Cadence/tick/exit-code/output contract của CLI `ocr loop` **giữ nguyên** (spec M3) — chỉ thay `TextWriter stdout` bằng `ICycleSink`.
- App cache key = unique index `(Context, Text, Src, Dst)`; DB `%LOCALAPPDATA%\ReTran\retran.db`; settings `%LOCALAPPDATA%\ReTran\settings.json` `{provider, src, dst, apiKey?}`; EF **migrations** (không raw schema, không `EnsureCreated` cho schema chính).
- Core không bao giờ mở file DB của App.
- Start/Stop do user bấm trên UI; không tự start khi mở app; Start dừng `DemoFeed` trước nếu đang chạy.
- i18n: mọi chuỗi UI mới vào **cả hai** bảng `I18nStrings.Vi`/`En` (test `EveryKey_Exists_In_Both_Languages` bắt).
- New publics: bilingual `///` docs (EN câu đầu, VI ngay sau); comment `//` trong code theo style file.
- Tests: skip-êm bằng early return (không `Assert.Skip`, không fail vì môi trường); không `Console.WriteLine` trong test body; test command: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`.
- Commit: đúng message từng task, 1 commit/task, stage đúng file của task.
- Test-count baseline: **C# 79, Python 15** (M4 không đụng Python). Count theo `dotnet test` **Total** (mỗi `[InlineData]` của Theory tính 1 case riêng). Targets: T1 81 · T2 85 · T3 90 · T4 98 · T5 106 · T6 118 · T7 122 · T8 131 · T9 137 · T10 144 · T11 150 · T12 159. (final-review fix wave +6 net.) (T5 +1 so với bản gốc: fix round 2 thêm test lookup treo.)
- Out of scope (spec §10): DeepL/LLM/local provider, game hook, obs-websocket, dịch lại giữa phiên đang chạy, retry/backoff Google.

## Review Focus

6 input/failure mode spec ngầm định mà test từng task phải ghim (mỗi dòng → test ở task sở hữu):

1. **Response của App (có `id`, không `method`) tới Core không được xử lý nhầm thành parse-error** → T2 `Response_WithoutMethod_RoutesToPending_NotParseError`.
2. **Hai writer trên cùng một stream (dispatch response vs `NotifyAsync`/`CallAsync`) không được torn-write** → T2 `ConcurrentNotify_DuringDispatch_WritesCleanLines`.
3. **Tick không bao giờ block vì App chậm / Google treo** → T5 `SlowTranslate_DoesNotBlockCycle` + `CacheFailure_TreatedAsAllMisses`.
4. **Box pixel→normalized clamp [0,1], quad hỏng trả về zeros chứ không ném异常 giữa vòng loop** → T4 `NormalizeQuad_ClampsToUnitSquare`, `NormalizeQuad_Malformed_ReturnsZeros`.
5. **App chết/ngoại lệ không được giết read loop của `JsonRpcClient`** (handler ném异常 vẫn phải trả error response, loop sống) → T8 `RequestHandler_Throws_ReturnsError_KeepsLoopAlive`.
6. *(code review, không unit-test được)* **`CoreProcessClient` phải drain stderr của Core** (`BeginErrorReadLine`) — redirect mà không đọc sẽ full pipe và deadlock Core khi paddle tràn log → kiểm tra ở review T10.

---

### Task 1: `ICycleSink` — tách emitter ra khỏi `OcrLoop`

**Files:**
- Create: `ReTran.Core/Cli/ICycleSink.cs`, `ReTran.Core/Cli/JsonLinesSink.cs`
- Modify: `ReTran.Core/Cli/OcrLoop.cs:143-254` (RunAsync), `ReTran.Core/Cli/OcrCommands.cs:118`
- Test: `ReTran.Tests/JsonLinesSinkTests.cs` (mới), sửa `ReTran.Tests/OcrLoopTests.cs`, `ReTran.Tests/OcrLoopCliTests.cs` theo signature mới

**Interfaces:**
- Consumes: `OcrPipeline.SpotAsync(string, CancellationToken) -> JsonObject` (`{spots:[{box,text,score}], elapsed_ms, new_texts:[...]}`), `Frame.Width/Height`, `BgraPngEncoder.ToPngBase64`.
- Produces (Task 5, 6 dùng):
  - `public interface ICycleSink { Task OnCycleAsync(long seq, int frameWidth, int frameHeight, JsonObject spotResult, CancellationToken ct); }`
  - `public sealed class JsonLinesSink(TextWriter stdout) : ICycleSink` — build đúng dòng JSON-M3 (`seq`, `at`, `new_texts`, `spots_count`, `elapsed_ms`) rồi `WriteLine + Flush`.
  - `OcrLoop.RunAsync(OcrLoopOptions options, ICaptureSource source, OcrPipeline pipeline, ICycleSink sink, TextWriter stderr, CancellationToken ct, Action<string>? onFatal = null)` — **tham số `TextWriter stdout` đổi thành `ICycleSink sink`**; `onFatal` nhận `"sidecar-exited"` (catch `SidecarRpcException`) và `"capture-error"` (first-frame timeout), gọi ngay trước `return 1`.

- [ ] **Step 1: Viết test failing**

```csharp
using System.Text.Json.Nodes;
using ReTran.Core.Cli;
using Xunit;

namespace RetranCore.Tests;

public class JsonLinesSinkTests
{
    [Fact]
    public async Task OnCycle_WritesJsonLine_WithM3ContractKeys()
    {
        using var sw = new StringWriter();
        var result = new JsonObject
        {
            ["spots"] = new JsonArray(new JsonObject { ["text"] = "HELLO" }),
            ["elapsed_ms"] = 42,
            ["new_texts"] = new JsonArray("HELLO"),
        };
        await new JsonLinesSink(sw).OnCycleAsync(7, 100, 50, result, CancellationToken.None);

        var line = JsonNode.Parse(sw.ToString()!.Trim())!.AsObject();
        Assert.Equal(7, line["seq"]!.GetValue<long>());
        Assert.Equal(1, line["spots_count"]!.GetValue<int>());
        Assert.Equal(42, line["elapsed_ms"]!.GetValue<int>());
        Assert.Equal("HELLO", line["new_texts"]![0]!.GetValue<string>());
        Assert.NotNull(line["at"]); // ISO-8601 UTC
    }

    [Fact]
    public async Task OnCycle_EmitsEvenWhenNoNewTexts()
    {
        using var sw = new StringWriter();
        var result = new JsonObject { ["spots"] = new JsonArray(), ["new_texts"] = new JsonArray() };
        await new JsonLinesSink(sw).OnCycleAsync(1, 10, 10, result, CancellationToken.None);
        Assert.Contains("\"new_texts\":[]", sw.ToString());
    }
}
```

- [ ] **Step 2: Chạy verify FAIL** — `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter JsonLinesSink` → FAIL (type không tồn tại).

- [ ] **Step 3: Implement**
  - `ICycleSink` như Interfaces; XML doc song ngữ cho public.
  - `JsonLinesSink`: chuyển nguyên khối build `line` hiện ở `OcrLoop.RunAsync:202-210` vào `OnCycleAsync` (giữ format `at` `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`).
  - `OcrLoop.RunAsync`: bỏ tham số `stdout`, thay emit bằng `await sink.OnCycleAsync(seq, frame.Width, frame.Height, result, ct);` (bên trong try-per-cycle sẵn có); thêm `onFatal` gọi ở đúng 2 chỗ `return 1`.
  - `OcrCommands.RunLoopAsync:118`: truyền `new JsonLinesSink(Console.Out)`.

- [ ] **Step 4: Chạy toàn bộ test, sửa test cũ dùng `TextWriter`**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`
Expected: PASS, **81** test methods. Output contract CLI không đổi (OcrLoopCliTests giữ nguyên assertion).

- [ ] **Step 5: Commit** — `git add ReTran.Core/Cli/ICycleSink.cs ReTran.Core/Cli/JsonLinesSink.cs ReTran.Core/Cli/OcrLoop.cs ReTran.Core/Cli/OcrCommands.cs ReTran.Tests/JsonLinesSinkTests.cs ReTran.Tests/OcrLoopTests.cs ReTran.Tests/OcrLoopCliTests.cs && git commit -m "refactor(core): tách OcrLoop emit qua ICycleSink, thêm onFatal reason"`

---

### Task 2: `JsonRpcServer` — write-lock, `NotifyAsync`, `CallAsync`, route response

**Files:**
- Modify: `ReTran.Core/JsonRpc/JsonRpcServer.cs`
- Test: `ReTran.Tests/JsonRpcServerTests.cs` (mở rộng — đã có `FakeTransport`)

**Interfaces:**
- Consumes: `IJsonRpcTransport.ReadLineAsync/WriteLineAsync`, `JsonRpc.ParseLine/SerializeResponse`, `RpcMethodException`, `RpcErrorCodes`.
- Produces (Task 5, 6 dùng):
  - `public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)` — id dạng **string** (`"1"`, `"2"`, …) khớp conventions App; response lỗi → `RpcMethodException(code, message)`; timeout/EOF → `TimeoutException`/`TaskCanceledException`.
  - `public Task NotifyAsync(string method, JsonNode? @params, CancellationToken ct = default)` — dòng không có `id`.
  - `JsonRpcServer` implements interface mới `public interface IJsonRpcNotifier { Task NotifyAsync(string method, JsonNode? @params, CancellationToken ct = default); }` (tạo file `ReTran.Core/JsonRpc/IJsonRpcNotifier.cs`).

- [ ] **Step 1: Viết test failing** (thêm vào `JsonRpcServerTests`; trước đó mở rộng `FakeTransport` — see note cuối block)

```csharp
[Fact]
public async Task CallAsync_Completes_WhenMatchingResponseArrives()
{
    var t = new FakeTransport();
    var server = new JsonRpcServer();
    _ = server.RunAsync(t, CancellationToken.None);
    Task<JsonNode?> call = server.CallAsync("translate.request_cache", new JsonObject(), CancellationToken.None);
    Assert.Equal("translate.request_cache", JsonNode.Parse(t.LastRequest!)!["method"]!.GetValue<string>());
    string id = JsonNode.Parse(t.LastRequest!)!["id"]!.GetValue<string>();
    await t.InjectLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"hits\":{{}}}}}}");
    Assert.NotNull((await call)["hits"]);
}

[Fact]
public async Task Response_WithoutMethod_RoutesToPending_NotParseError()
{
    var t = new FakeTransport();
    var server = new JsonRpcServer();
    _ = server.RunAsync(t, CancellationToken.None);
    Task<JsonNode?> call = server.CallAsync("m", null, CancellationToken.None);
    string id = JsonNode.Parse(t.LastRequest!)!["id"]!.GetValue<string>();
    await t.InjectLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":\"ok\"}}");
    Assert.Equal("ok", (await call)!.GetValue<string>());
    Assert.DoesNotContain(t.WrittenLines, line => line.Contains("Parse error")); // không được phát sinh parse-error
}

[Fact]
public async Task ConcurrentNotify_DuringDispatch_WritesCleanLines()
{
    var t = new FakeTransport();
    var server = new JsonRpcServer();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    server.Register("slow", async _ => { await gate.Task; return JsonValue.Create("done"); });
    _ = server.RunAsync(t, CancellationToken.None);
    await t.InjectLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"slow\"}");
    Task notify1 = server.NotifyAsync("frame.translated", new JsonObject());
    Task notify2 = server.NotifyAsync("pipeline.state", new JsonObject());
    gate.SetResult();
    await Task.WhenAll(notify1, notify2);
    await t.WaitForLinesAsync(3);
    foreach (string line in t.WrittenLines)
        Assert.NotNull(JsonNode.Parse(line)); // mọi dòng đều JSON hợp lệ, không torn
}

[Fact]
public async Task CallAsync_UnknownIdResponse_IsIgnored()
{
    var t = new FakeTransport();
    var server = new JsonRpcServer();
    server.Register("core.ping", _ => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));
    _ = server.RunAsync(t, CancellationToken.None);
    await t.InjectLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"999\",\"result\":\"stray\"}");
    await t.InjectLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"core.ping\"}");
    var resp = JsonNode.Parse((await t.WaitForResponseAsync("1"))!)!;
    Assert.Equal("pong", resp["result"]!.GetValue<string>()); // response lạ bị bỏ qua, dispatch chạy tiếp FIFO
    Assert.Single(t.WrittenLines);                             // không có dòng nào phát sinh từ id "999"
}
```

(Lưu ý Step 1 — mở rộng `FakeTransport` private trong `JsonRpcServerTests`, **giữ ctor cũ** `FakeTransport(IEnumerable<string>)` (hết seed → `ReadLineAsync` trả `null`, 3 test hiện có kết thúc bình thường); thêm:
- ctor rỗng `new FakeTransport()` → mode input mở: `InjectLineAsync(line)` đẩy dòng vào hàng đợi đang chờ, `CompleteInput()` đóng hàng đợi (sau đó `ReadLineAsync` trả `null`); `ReadLineAsync` chờ dòng kế tiếp khi hàng đợi rỗng (tôn trọng `ct`).
- `LastRequest` — dòng request vừa ghi (`WriteLineAsync` set khi line parse ra `method`).
- `WrittenLines` — mọi dòng đã ghi kể từ đầu (bất biến, không phụ thuộc dequeue); `Out` giữ như hiện tại (hàng đợi chờ).
- `WaitForLinesAsync(int n)` — chờ tới khi `Out.Count >= n`; `WaitForResponseAsync(string id)` — chờ dòng response có `id` khớp rồi trả nó.
- `WriteLineAsync` trả `Task.CompletedTask` (ghi đồng bộ) — để `CallAsync` gửi request xong trước khi trả task, test assert được `LastRequest` ngay dòng kế tiếp.)

- [ ] **Step 2: Chạy verify FAIL** — `--filter JsonRpcServerTests` → FAIL (thiếu member/type).

- [ ] **Step 3: Implement**
  - Thêm `_writeLock = new SemaphoreSlim(1,1)` — **mọi** ghi qua transport (response, parse-error, notify, request của CallAsync) đều giữ lock trong `using await _writeLock.WaitAsync(ct)`.
  - Thêm `_pending` (`Dictionary<string, TaskCompletionSource<JsonNode?>>`), `_nextId` (int, `Interlocked`), `CallAsync` như Interfaces (gửi request, chờ TCS).
  - Read loop phân nhánh: `method` là string → xử lý request như hiện tại; ngược lại **có `id`** → match `_pending` (result → `TrySetResult`, error → `TrySetException(new RpcMethodException(code,msg))`), id không match → bỏ qua; không có cả `method` lẫn `id` hợp lệ → parse-error response (giữ contract M0).

- [ ] **Step 4: Chạy verify PASS** — toàn bộ suite, **85** test methods.

- [ ] **Step 5: Commit** — `feat(core): JsonRpcServer thêm NotifyAsync/CallAsync, write-lock, route response`

---

### Task 3: `ITranslator` + `GoogleWebTranslator` + registry

**Files:**
- Create: `ReTran.Core/Translate/ITranslator.cs`, `ReTran.Core/Translate/GoogleWebTranslator.cs`, `ReTran.Core/Translate/TranslatorRegistry.cs`
- Test: `ReTran.Tests/GoogleWebTranslatorTests.cs`

**Interfaces:**
- Consumes: nothing (HttpClient standalone).
- Produces (Task 6, 7 dùng):
  - `public interface ITranslator { string Name { get; } Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct = default); }`
  - `public sealed class GoogleWebTranslator : ITranslator` — ctor `(HttpMessageHandler? handler = null, TimeSpan? timeout = null)` (default timeout **5 s**), `Name => "google"`.
  - `public sealed class TranslatorRegistry { public void Register(ITranslator t); public ITranslator? Get(string name); }` (so sánh ignore-case).

- [ ] **Step 1: Viết test failing**

```csharp
public class GoogleWebTranslatorTests
{
    private sealed class StubHandler(HttpStatusCode code, string body, int delayMs = 0) : HttpMessageHandler
    {
        public string? LastUrl;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastUrl = req.RequestUri!.ToString();
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task Translate_ConcatenatesSegments()
    {
        var h = new StubHandler(HttpStatusCode.OK, "[[[\"Xin \",\"hello\",null,null,1],[\"chào\",\"hello\",null,null,1]]]");
        var t = new GoogleWebTranslator(h);
        Assert.Equal("Xin chào", await t.TranslateAsync("hello", "en", "vi"));
        Assert.Contains("client=gtx", h.LastUrl);
        Assert.Contains("sl=en", h.LastUrl); Assert.Contains("tl=vi", h.LastUrl);
        Assert.Contains("q=hello", h.LastUrl);
    }

    [Fact]
    public async Task Translate_SrcAuto_PassesAuto()
    {
        var h = new StubHandler(HttpStatusCode.OK, "[[[\"chào\",null,null,null,1]]]");
        await new GoogleWebTranslator(h).TranslateAsync("hi", "auto", "vi");
        Assert.Contains("sl=auto", h.LastUrl);
    }

    [Fact]
    public async Task Translate_HttpError_Throws()
    {
        var t = new GoogleWebTranslator(new StubHandler(HttpStatusCode.InternalServerError, "boom"));
        await Assert.ThrowsAsync<HttpRequestException>(() => t.TranslateAsync("x", "en", "vi"));
    }

    [Fact]
    public async Task Translate_SlowerThanTimeout_ThrowsTimeout()
    {
        var t = new GoogleWebTranslator(new StubHandler(HttpStatusCode.OK, "[]", delayMs: 300), timeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => t.TranslateAsync("x", "en", "vi"));
    }

    [Fact]
    public void Registry_ReturnsNullForUnknown_AndIsCaseInsensitive()
    {
        var r = new TranslatorRegistry();
        r.Register(new GoogleWebTranslator(new StubHandler(HttpStatusCode.OK, "[]")));
        Assert.NotNull(r.Get("GOOGLE"));
        Assert.Null(r.Get("deepl"));
    }
}
```

- [ ] **Step 2: Chạy verify FAIL** — `--filter GoogleWebTranslatorTests` → FAIL (type không tồn tại).

- [ ] **Step 3: Implement**
  - URL: `https://translate.googleapis.com/translate_a/single?client=gtx&sl={EscapeDataString(src)}&tl={EscapeDataString(dst)}&dt=t&q={EscapeDataString(text)}` — `HttpClient` dùng chung static, `Timeout = timeout ?? 5s`.
  - Parse: ném `HttpRequestException` khi status != 200; body rỗng/sai cấu trúc → `FormatException`; `TaskCanceledException` khi timeout (chưa bị ct của caller hủy) → wrap thành `TimeoutException` (test trên yêu cầu `TimeoutException`).
  - Cancellation thật của `ct` → `OperationCanceledException` giữ nguyên.

- [ ] **Step 4: Chạy verify PASS** — **90** test cases.

- [ ] **Step 5: Commit** — `feat(core): ITranslator + GoogleWebTranslator (web endpoint, không key) + registry`

---

### Task 4: `ContextKey` + `SpotGeometry` (quad pixel → normalized)

**Files:**
- Create: `ReTran.Core/Serve/ContextKey.cs`, `ReTran.Core/Serve/SpotGeometry.cs`
- Test: `ReTran.Tests/ContextKeyTests.cs`, `ReTran.Tests/SpotGeometryTests.cs`

**Interfaces:**
- Consumes: Win32 (`GetWindowThreadProcessId`, `Process`).
- Produces (Task 5, 6 dùng):
  - `public static class ContextKey { public static string FromMonitor(int monitor); public static string FromHwnd(nint hwnd); }`
  - `public static class SpotGeometry { public static (double X, double Y, double W, double H) NormalizeQuad(JsonArray quad, int frameWidth, int frameHeight); }`

- [ ] **Step 1: Viết test failing**

```csharp
public class ContextKeyTests
{
    [Fact] public void FromMonitor_FormatsScreenKey() => Assert.Equal("screen:2", ContextKey.FromMonitor(2));

    [Fact]
    public void FromHwnd_DesktopWindow_ReturnsExplorerExe()
    {
        string key = ContextKey.FromHwnd(GetDesktopWindow()); // win32: desktop thuộc explorer.exe
        Assert.Equal("explorer.exe", key);
    }

    [Fact] public void FromHwnd_Zero_ReturnsUnknown() => Assert.Equal("unknown", ContextKey.FromHwnd(0));
}

public class SpotGeometryTests
{
    [Fact]
    public void NormalizeQuad_KnownPixels()
    {
        var quad = JsonNode.Parse("[[100,50],[300,50],[300,150],[100,150]]")!.AsArray();
        (double x, double y, double w, double h) = SpotGeometry.NormalizeQuad(quad, 1000, 500);
        Assert.Equal(0.1, x, 6); Assert.Equal(0.1, y, 6);
        Assert.Equal(0.2, w, 6); Assert.Equal(0.2, h, 6);
    }

    [Fact]
    public void NormalizeQuad_ClampsToUnitSquare()
    {
        var quad = JsonNode.Parse("[[-50,-50],[1200,-50],[1200,600],[-50,600]]")!.AsArray();
        (double x, double y, double w, double h) = SpotGeometry.NormalizeQuad(quad, 1000, 500);
        Assert.Equal(0, x); Assert.Equal(0, y);
        Assert.True(x + w <= 1.0000001 && y + h <= 1.0000001);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[[1,2],[3,4]]")]
    [InlineData("not-an-array")]
    public void NormalizeQuad_Malformed_ReturnsZeros(string json)
    {
        JsonArray? quad = JsonNode.Parse(json) as JsonArray;
        (double x, double y, double w, double h) = SpotGeometry.NormalizeQuad(quad!, 100, 100);
        Assert.Equal((0, 0, 0, 0), (x, y, w, h));
    }
}
```

(`FromHwnd_DesktopWindow`: dùng `[DllImport("user32.dll")] static extern nint GetDesktopWindow();` — test Windows-only, TFM đã windows sẵn.)

- [ ] **Step 2: Chạy verify FAIL** — `--filter "ContextKeyTests|SpotGeometryTests"` → FAIL.

- [ ] **Step 3: Implement**
  - `FromHwnd`: `GetWindowThreadProcessId(hwnd, out pid)` → `Process.GetProcessById(pid).ProcessName + ".exe"`.ToLowerInvariant(); mọi exception → `"unknown"`.
  - `NormalizeQuad`: cần đúng 4 điểm, mỗi điểm ≥2 số; minX/maxX/minY/maxY từ 8 số; x=minX/W … clamp [0,1]; `frameWidth/Height <= 0` hoặc shape sai → `(0,0,0,0)` (không ném — path hiển thị không được crash).

- [ ] **Step 4: Chạy verify PASS** — **98** test cases.

- [ ] **Step 5: Commit** — `feat(core): ContextKey (process name / screen:N) + SpotGeometry normalize quad pixel`

---

### Task 5: `TranslateCycleSink` — heart của M4

**Files:**
- Create: `ReTran.Core/Serve/ICacheChannel.cs`, `ReTran.Core/Serve/TranslateCycleSink.cs`
- Test: `ReTran.Tests/TranslateCycleSinkTests.cs`

**Interfaces:**
- Consumes: `ICycleSink` (T1), `SpotGeometry` (T4), `ITranslator` (T3), `IJsonRpcNotifier` (T2).
- Produces (Task 6 dùng):
  - `public sealed record CacheLookupResult(IReadOnlyDictionary<string, string> Hits, IReadOnlyList<string> Misses);`
  - `public interface ICacheChannel { Task<CacheLookupResult> LookupAsync(string context, string src, string dst, IReadOnlyList<string> texts, CancellationToken ct); Task PutAsync(string context, string src, string dst, IReadOnlyList<(string Text, string Translated)> entries, CancellationToken ct); }`
  - `public sealed class TranslateCycleSink : ICycleSink` — ctor:
    ```csharp
    public TranslateCycleSink(
        string context, string src, string dst,
        ITranslator translator,
        ICacheChannel cache,
        IJsonRpcNotifier notifier,
        TextWriter stderr)
    ```
  - Box payload trong `frame.translated`: `{x, y, w, h, text, translated}` — toạ độ normalized 0–1, ghi tường minh `"translated": null` khi chưa có (đúng spec §3.3).
  - `public Task WhenIdleAsync()` — chờ chuỗi task nền hiện tại drain xong (dùng cho test/diagnostic; production path không gọi).

- [ ] **Step 1: Viết test failing** (fakes `StubTranslator`/`StubCache`/`StubNotifier` nằm trong test file — define y hệt dưới đây)

```csharp
public class TranslateCycleSinkTests
{
    private static JsonObject Result(params string[] new_texts)
    {
        var spots = new JsonArray();
        foreach (string t in new_texts)
            spots.Add(new JsonObject
            {
                ["text"] = t,
                ["box"] = JsonNode.Parse("[[100,50],[300,50],[300,150],[100,150]]"),
                ["score"] = 0.9,
            });
        return new JsonObject
        {
            ["spots"] = spots,
            ["new_texts"] = new JsonArray(new_texts.Cast<object>().ToArray()),
            ["elapsed_ms"] = 1,
        };
    }

    private sealed class StubTranslator : ITranslator
    {
        public string Name => "stub";
        public string Result = "β";
        public bool ThrowOnTranslate;
        public bool BlockForever;
        public List<string> CallTexts { get; } = new();
        public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct)
        {
            CallTexts.Add(text);
            if (BlockForever)
                return new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously).Task; // treo có chủ đích
            if (ThrowOnTranslate) throw new HttpRequestException("stub translate failed");
            return Task.FromResult(Result);
        }
    }

    private sealed class StubCache : ICacheChannel
    {
        public Dictionary<string, string> Hits { get; set; } = new();
        public List<string>? Misses { get; set; }   // null -> tính từ input trừ Hits
        public bool ThrowOnLookup;
        public List<IReadOnlyList<(string Text, string Translated)>> Puts { get; } = new();

        public Task<CacheLookupResult> LookupAsync(string context, string src, string dst, IReadOnlyList<string> texts, CancellationToken ct)
        {
            if (ThrowOnLookup) throw new TimeoutException("stub cache down");
            var hits = new Dictionary<string, string>();
            foreach (string t in texts)
                if (Hits.TryGetValue(t, out string? v)) hits[t] = v;
            IReadOnlyList<string> misses = Misses ?? texts.Where(t => !hits.ContainsKey(t)).ToList();
            return Task.FromResult(new CacheLookupResult(hits, misses));
        }

        public Task PutAsync(string context, string src, string dst, IReadOnlyList<(string Text, string Translated)> entries, CancellationToken ct)
        {
            Puts.Add(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class StubNotifier : IJsonRpcNotifier
    {
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public IEnumerable<string> Methods => Calls.Select(c => c.Method);
        public JsonNode? Single(string method) => Calls.Where(c => c.Method == method).Select(c => c.Params).Single();
        public JsonNode? Last(string method) => Calls.Last(c => c.Method == method).Params;
        public Task NotifyAsync(string method, JsonNode? @params, CancellationToken ct = default)
        {
            Calls.Add((method, @params));
            return Task.CompletedTask;
        }
    }

    private static TranslateCycleSink Create(ITranslator tr, ICacheChannel cache, StubNotifier n, TextWriter? err = null)
        => new("screen:0", "auto", "vi", tr, cache, n, err ?? new StringWriter());

    [Fact]
    public async Task Cycle_EmitsFrameTranslated_EveryTime_EvenWithoutNewTexts()
    {
        var n = new StubNotifier();
        await Create(new StubTranslator(), new StubCache(), n).OnCycleAsync(1, 1000, 500, Result(), CancellationToken.None);
        var p = n.Single("frame.translated");
        Assert.Equal(1, p!["seq"]!.GetValue<long>());
        Assert.Equal(1000, p["w"]!.GetValue<int>());
        Assert.Empty(p["boxes"]!.AsArray());
    }

    [Fact]
    public async Task Cycle_BoxesNormalized_AnnotatedOnNextCycle()
    {
        var cache = new StubCache { Hits = new() { ["HELLO"] = "Xin chào" } };
        var n = new StubNotifier();
        var sink = Create(new StubTranslator { Result = "β" }, cache, n);
        await sink.OnCycleAsync(1, 1000, 500, Result("HELLO", "FRESH"), CancellationToken.None);
        // chu kỳ 1 emit trước khi background chạy -> cả 2 box chưa có bản dịch
        Assert.Null(n.Single("frame.translated")!["boxes"]!.AsArray()[1]!["translated"]);
        await sink.WhenIdleAsync(); // hit HELLO vào session; FRESH dịch "β" + put
        await sink.OnCycleAsync(2, 1000, 500, Result("HELLO", "FRESH"), CancellationToken.None);

        var boxes = n.Last("frame.translated")!["boxes"]!.AsArray();
        Assert.Equal(2, boxes.Count);
        var hello = boxes[0]!.AsObject();
        Assert.Equal("HELLO", hello["text"]!.GetValue<string>());
        Assert.Equal("Xin chào", hello["translated"]!.GetValue<string>());
        Assert.Equal(0.1, hello["x"]!.GetValue<double>(), 6); // 100/1000
        Assert.Equal(0.1, hello["y"]!.GetValue<double>(), 6); // 50/500
        Assert.Equal(0.2, hello["w"]!.GetValue<double>(), 6); // (300-100)/1000
        Assert.Equal(0.2, hello["h"]!.GetValue<double>(), 6); // (150-50)/500
        Assert.Equal("β", boxes[1]!.AsObject()["translated"]!.GetValue<string>());
    }

    [Fact]
    public async Task NewTexts_Misses_TranslatedThenPut()
    {
        var cache = new StubCache { Hits = new() { ["A"] = "α" }, Misses = new List<string> { "B" } };
        var tr = new StubTranslator { Result = "β" };
        var n = new StubNotifier();
        var sink = Create(tr, cache, n);
        await sink.OnCycleAsync(1, 100, 100, Result("A", "B"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.Equal(new[] { "B" }, tr.CallTexts); // A là hit -> không gọi dịch
        var entry = n.Single("translate.put")!["entries"]!.AsArray().Single()!.AsObject();
        Assert.Equal("B", entry["text"]!.GetValue<string>());
        Assert.Equal("β", entry["translated"]!.GetValue<string>());
    }

    [Fact]
    public async Task CacheFailure_TreatedAsAllMisses()
    {
        var cache = new StubCache { ThrowOnLookup = true };
        var tr = new StubTranslator { Result = "β" };
        var n = new StubNotifier();
        var sink = Create(tr, cache, n);
        await sink.OnCycleAsync(1, 100, 100, Result("X"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.Equal(new[] { "X" }, tr.CallTexts); // lookup ném -> coi hết là miss -> dịch hết
    }

    [Fact]
    public async Task TranslateFailure_NoPut_NoThrow()
    {
        var err = new StringWriter();
        var tr = new StubTranslator { ThrowOnTranslate = true };
        var n = new StubNotifier();
        var sink = Create(tr, new StubCache(), n, err);
        await sink.OnCycleAsync(1, 100, 100, Result("BAD"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.DoesNotContain("translate.put", n.Methods);  // không ghi DB dòng lỗi
        Assert.Contains("warn", err.ToString(), StringComparison.OrdinalIgnoreCase); // có log stderr
    }

    [Fact]
    public async Task SlowTranslate_DoesNotBlockCycle()
    {
        var tr = new StubTranslator { BlockForever = true };
        var sink = Create(tr, new StubCache { Misses = new List<string> { "SLOW" } }, new StubNotifier());
        Task cycle = sink.OnCycleAsync(1, 100, 100, Result("SLOW"), CancellationToken.None);
        Task done = await Task.WhenAny(cycle, Task.Delay(500));
        Assert.Same(cycle, done); // chu kỳ trả về dù dịch còn treo
                               // (không WhenIdleAsync ở test này — background treo có chủ đích)
    }

    [Fact]
    public async Task RepeatTexts_Deduped_InFlightAndSession()
    {
        var tr = new StubTranslator { Result = "β" };
        var n = new StubNotifier();
        var sink = Create(tr, new StubCache(), n);
        await sink.OnCycleAsync(1, 100, 100, Result("DUP"), CancellationToken.None);
        await sink.OnCycleAsync(2, 100, 100, Result("DUP"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.Single(tr.CallTexts); // chỉ dịch 1 lần
    }
}
```

- [ ] **Step 2: Chạy verify FAIL** — `--filter TranslateCycleSinkTests` → FAIL.

- [ ] **Step 3: Implement**
  - State phiên (field, lock `_sync`): `Dictionary<string, string> _session` (text→translated), `HashSet<string> _inFlight`.
  - `OnCycleAsync`: (1) build `boxes` = mỗi spot → `SpotGeometry.NormalizeQuad(spot["box"], frameWidth, frameHeight)` + `text` + `_session.TryGetValue(text)` → `translated` (không có → JSON `null`); (2) `await notifier.NotifyAsync("frame.translated", {seq,w,h,boxes})`; (3) `new_texts` rỗng → return; (4) với mỗi text chưa trong `_session`/`_inFlight` → đánh dấu `_inFlight`, `_ = Task.Run(BackgroundAsync(text, ct))` (chỉ **1** task nền tổng hợp — thêm list chờ nếu đang có task chạy).
  - `BackgroundAsync`: chia texts còn lại; `try cache.LookupAsync(...)` với `CancellationTokenSource(2s)` — catch → **toàn bộ là misses**; hits → vào `_session`; misses (loại text đã fail dịch trước đó — chỉ `_inFlight`/`_session` mới chặn) → dịch qua `SemaphoreSlim(4)` song song, thành công → `_session` + list put, thất bại → `stderr.WriteLine("warn: translate failed ...")` (không put, không retry); xong → `cache.PutAsync(...)` (entries rỗng thì bỏ qua); finally bỏ `_inFlight`.
  - Mọi异常 trong task nền bắt hết + log stderr — **không** để bay ra ngoài (trừ `OperationCanceledException` khi sink bị dispose/stop).
  - `WhenIdleAsync`: giữ field `Task _bg` (chuỗi task nền, update dưới `_sync`), method await snapshot của `_bg` cho tới khi drain (loop nếu có task mới nối vào trong lúc chờ).

- [ ] **Step 4: Chạy verify PASS** — **106** test cases.

- [ ] **Step 5: Commit** — `feat(core): TranslateCycleSink — emit frame.translated + cache lookup + dịch nền không block tick`

---

### Task 6: `PipelineHost` — `core.start/stop/list_sources` + `pipeline.state`

**Files:**
- Create: `ReTran.Core/Serve/PipelineHost.cs`, `ReTran.Core/Serve/JsonRpcCacheChannel.cs`, `ReTran.Core/Capture/CaptureCatalog.cs`
- Modify: `ReTran.Core/Program.cs` (RunServeAsync: đăng ký method + registry), `ReTran.Core/Cli/CaptureCommands.cs` (dùng `CaptureCatalog`, output CLI giữ nguyên)
- Test: `ReTran.Tests/PipelineHostTests.cs`

**Interfaces:**
- Consumes: T1 `OcrLoop.RunAsync(+onFatal)`, T2 `CallAsync/NotifyAsync`, T3 registry, T5 sink+`ICacheChannel`, `OcrLoop.CreateSource`, `LoopTestDoubles.FakeCaptureSource`, `FakeSidecarRpc` (pattern `OcrPipelineTests`).
- Produces:
  - `public sealed class PipelineHost : IAsyncDisposable` — ctor `(ISidecarRpc? rpc, TranslatorRegistry registry, JsonRpcServer server, TextWriter stderr, Func<OcrLoopOptions, ICaptureSource>? sourceFactory = null)`; methods:
    - `Task<JsonNode?> StartAsync(JsonNode? @params, CancellationToken ct)` → `{"running":true}`
    - `Task<JsonNode?> StopAsync(JsonNode? @params, CancellationToken ct)` → `{"running":false}`
    - `Task<JsonNode?> ListSourcesAsync(CancellationToken ct)` → `{"monitors":[{id,name}],"windows":[{hwnd,title,process}]}`
  - `public sealed class JsonRpcCacheChannel(JsonRpcServer server) : ICacheChannel` — `translate.request_cache` (timeout 2 s, lỗi → ném để sink coi là all-miss) + `translate.put` qua `NotifyAsync`.
  - `public static class CaptureCatalog { public static IReadOnlyList<MonitorInfo> ListMonitors(); public static IReadOnlyList<WindowInfo> ListWindows(); }` + records `MonitorInfo(int Id, string Name)`, `WindowInfo(string Hwnd, string Title, string Process)` — `CaptureCommands.ListMonitors/ListWindows` refactor chỉ còn in ra từ catalog (output CLI **không đổi**).

- [ ] **Step 1: Viết test failing**

```csharp
public class PipelineHostTests
{
    // Dùng FakeTransport + FakeCaptureSource + FakeSidecarRpc (pattern OcrPipelineTests/ServeOcrTests)

    [Fact] public async Task Start_InvalidSourceKind_InvalidParams()      // kind:"bogus" -> RpcMethodException -32602
    [Fact] public async Task Start_UnknownProvider_InvalidParams()        // provider:"deepl" -> -32602
    [Fact] public async Task Start_NoSidecar_InternalError()              // rpc:null -> -32603, message chứa "sidecar"
    [Fact] public async Task Start_WhileRunning_InvalidRequest()          // start lần 2 -> -32600 "already running"
    [Fact] public async Task Stop_BeforeStart_ReturnsFalse()              // {"running":false}, không lỗi
    [Fact] public async Task ListSources_ReturnsMonitorsAndWindowsShape() // keys đúng, hwnd dạng "0x..."
    [Fact] public async Task SidecarLost_NotifiesPipelineState()          // fake source đẩy frame, fake rpc SpotAsync ném SidecarRpcException
                                                                         // -> dòng notification pipeline.state {running:false, reason:"sidecar-exited"}
    [Fact] public async Task Restart_FreshSession_TranslatesAgain()       // start->stop->start với rpc trả cùng text:
                                                                         // lần 2 new_texts phải chứa lại text đó (OcrDiff mới)
}
```

(Assertion chi tiết: `RpcMethodException.Code` đúng mã; notification parse được, `reason` đúng chuỗi; `Restart_...` dùng translator stub đếm lời gọi > 0 ở phiên 2.)

- [ ] **Step 2: Chạy verify FAIL** — `--filter PipelineHostTests` → FAIL.

- [ ] **Step 3: Implement**
  - `StartAsync` validate theo đúng thứ tự spec §3.1 → `RpcMethodException` (`RpcErrorCodes.InvalidParams` / `InvalidRequest` / `InternalError`). Đọc defaults `CoreConfig.LoadFrom(CoreConfig.DefaultPath)` cho `ocrFps`/`captureFps` khi params thiếu. `sourceFactory ?? OcrLoop.CreateSource` (test inject fake). Bắt `ArgumentOutOfRangeException` khi mở source → `-32603`.
  - Tạo per-start: `new OcrPipeline(rpc, new OcrDiff())`, `new TranslateCycleSink(context, src, dst, registry.Get(provider)!, new JsonRpcCacheChannel(server), server, stderr)`, `CancellationTokenSource`, `_loopTask = Task.Run(() => OcrLoop.RunAsync(opts, source, pipeline, sink, stderr, token, onFatal: r => server.NotifyAsync("pipeline.state", {running:false, reason:r})))`.
  - Task loop kết thúc **không phải do stop** (return 1 / faulted / onFatal đã gọi) → ensure notify `pipeline.state {running:false, reason}` với reason: onFatal đã cấp thì dùng, faulted → `"internal"`, còn lại → `"capture-error"`. Stop bình thường → không notify (kết quả `core.stop` đã trả).
  - `StopAsync`: hủy CTS, await task (timeout 5 s best-effort), dispose source, trả `{"running":false}` — idempotent.
  - `Program.RunServeAsync`: tạo `TranslatorRegistry` (đăng ký `new GoogleWebTranslator()`), `PipelineHost`, `server.Register("core.start"|"core.stop"|"core.list_sources", ...)`; host disposal khi serve thoát.
  - `CaptureCatalog`: move logic liệt kê monitor/window hiện có trong `CaptureCommands` (dữ liệu thuần, không in); `CaptureCommands` giữ nguyên output string.

- [ ] **Step 4: Chạy verify PASS** — **118** test cases; chạy thêm `--filter "OcrCli|Capture"` để chắc CLI list không đổi output.

- [ ] **Step 5: Commit** — `feat(core): PipelineHost core.start/stop/list_sources + pipeline.state + CaptureCatalog`

---

### Task 7: CLI `translate` one-shot

**Files:**
- Create: `ReTran.Core/Cli/TranslateCommands.cs`
- Modify: `ReTran.Core/Program.cs:26-31` (bỏ comment "translate handler lands in M4", đăng ký)
- Test: `ReTran.Tests/TranslateCliTests.cs`

**Interfaces:**
- Consumes: T3 `TranslatorRegistry`/`GoogleWebTranslator`.
- Produces: `ReTran.Core translate "<text>" --dst <lang> [--src auto] [--provider google]` — in bản dịch + `\n` ra stdout.

- [ ] **Step 1: Viết test failing**

```csharp
public class TranslateCliTests
{
    [Fact]
    public async Task MissingDst_Exit2()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["translate", "hi"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingText_Exit2()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["translate", "--dst", "vi"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownArg_Exit2()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["translate", "hi", "--dst", "vi", "--bogus"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Google_Real_PrintsOneLine_OrSkipsWhenOffline()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (stdout, _, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["translate", "hello", "--dst", "vi", "--src", "en"]);
        if (exit == 1) return; // google fail/offline -> skip-êm, không fail vì môi trường
        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(stdout));
    }
}
```

(Dùng redirect harness `OcrTestSupport.RunDispatcherAsync` như `OcrCliTests` — không `Console.WriteLine` trong test.)

- [ ] **Step 2: Chạy verify FAIL** — `--filter TranslateCliTests` → FAIL.

- [ ] **Step 3: Implement** — parse args thủ công theo style `OcrLoop.TryParse` (unknown arg / thiếu `--dst` → usage, exit 2); chạy `new GoogleWebTranslator()` với timeout 10 s cho CLI; mạng lỗi → stderr + exit 1.

- [ ] **Step 4: Chạy verify PASS** — **122** test cases.

- [ ] **Step 5: Commit** — `feat(core): CLI translate one-shot (google)`

---

### Task 8: App `JsonRpcClient` — 3 nhánh + write-lock

**Files:**
- Modify: `ReTran.App/Core/JsonRpcClient.cs`
- Test: `ReTran.Tests/JsonRpcClientTests.cs`

**Interfaces:**
- Consumes: hiện có (`CallAsync`, `ReadLoopAsync`).
- Produces (Task 10 dùng):
  - `public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler)` — chưa đăng ký → trả error `-32601 Method not found: <m>`; handler ném异常 → error `-32603`.
  - `public event Action<string, JsonNode?>? NotificationReceived;` (dispatch tuần tự trong read loop, mỗi handler bọc try/catch → raise `public event Action<Exception>? HandlerError`).
  - Response cho request Core cũng đi qua write-lock dùng chung với `CallAsync`.

- [ ] **Step 1: Viết test failing** (StringReader/Writer, kiểu `JsonRpcServerTests`)

```csharp
[Fact] public async Task CallAsync_StillMatchesResponseById()                 // regression
[Fact] public async Task IncomingRequest_InvokesHandler_AndRepliesResult()    // dòng {"id":"5","method":"translate.request_cache",...} -> dòng reply cùng id, có result
[Fact] public async Task IncomingRequest_HandlerThrows_ReplyErrorCode32603()  // reply có error.code = -32603
[Fact] public async Task IncomingRequest_NoHandler_ReplyMethodNotFound()      // -32601
[Fact] public async Task Notification_WithoutId_RaisesEvent_AndSurvivesThrow() // handler throw -> event HandlerError nổ, read loop vẫn đọc tiếp (gửi notification kế tiếp vẫn nhận)
[Fact] public async Task ConcurrentCallAndReply_WritesCleanLines()            // 2 CallAsync song song + reply -> mọi dòng JSON hợp lệ
```

- [ ] **Step 2: Chạy verify FAIL** — `--filter JsonRpcClientTests` → FAIL.

- [ ] **Step 3: Implement** — thêm `_writeLock` (mọi outbound), `_pending`/`_nextId` (đã có `_pending`; id đã string), phân nhánh ReadLoop: (a) có `result`/`error` + id → pending như hiện tại; (b) có `method` + `id` → request → handler → ghi response; (c) có `method`, không id → notification → event. Log exception handler qua `HandlerError` (không nuốt im lặng, không Console.WriteLine).

- [ ] **Step 4: Chạy verify PASS** — **131** test cases (test `JsonRpcServerTests`/App khác không vỡ).

- [ ] **Step 5: Commit** — `feat(app): JsonRpcClient 3 nhánh (response/request/notification) + write-lock`

---

### Task 9: EF Core cache — entity, DbContext, migration, `TranslationCache`

**Files:**
- Create: `ReTran.App/Data/TranslationEntry.cs`, `ReTran.App/Data/RetranDbContext.cs`, `ReTran.App/Data/DesignTimeDbContextFactory.cs`, `ReTran.App/Services/TranslationCache.cs`
- Modify: `ReTran.App/ReTran.App.csproj` (thêm `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`)
- Test: `ReTran.Tests/TranslationCacheTests.cs`

**Interfaces:**
- Consumes: spec §5.1 (DB path `%LOCALAPPDATA%\ReTran\retran.db`, unique index).
- Produces (Task 10 dùng):
  - `public sealed class TranslationEntry { public int Id; public string Context; public string Text; public string Src; public string Dst; public string Translated; public DateTime CreatedAtUtc; }`
  - `public sealed class RetranDbContext(DbContextOptions<RetranDbContext> options) : DbContext` + `DbSet<TranslationEntry> Translations`; `OnModelCreating`: unique index `(Context, Text, Src, Dst)`.
  - `public sealed class TranslationCache(RtranDbContext db)` :
    - `Task<CacheLookupResult> LookupAsync(string context, string src, string dst, IReadOnlyList<string> texts, CancellationToken ct)` — record `CacheLookupResult(IReadOnlyDictionary<string,string> Hits, IReadOnlyList<string> Misses)` (bản App, cùng shape với Core).
    - `Task PutAsync(string context, string src, string dst, IReadOnlyList<(string Text,string Translated)> entries, CancellationToken ct)` — upsert: đã có → update `Translated`, chưa có → insert; transaction per batch.
  - Đường dẫn DB: `public static string DefaultDbPath` — resolve từ options đăng ký DI (`UseSqlite($"Data Source={path}")`).

- [ ] **Step 1: Viết test failing**

```csharp
public class TranslationCacheTests : IDisposable
{
    // ctor: DbContextOptions với Sqlite $"Data Source={tempFile}" + db.Database.Migrate() (dùng migration thật)
    // IDisposable: xóa tempFile

    [Fact] public async Task Lookup_ReturnsHitsAndMisses()
    [Fact] public async Task Lookup_DifferentContextOrLang_IsMiss()   // cùng text, context khác -> miss
    [Fact] public async Task Put_Upsert_UpdatesTranslated()          // put 2 lần -> 1 bản ghi, Translated = giá trị mới
    [Fact] public async Task Put_DifferentKeys_Coexist()             // unique index không nuốt key khác
    [Fact] public async Task Migrate_FreshFile_CreatesSchema()       // file mới -> Migrate() chạy sạch, Insert thành công
}
```

- [ ] **Step 2: Chạy verify FAIL** — `--filter TranslationCacheTests` → FAIL.

- [ ] **Step 3: Implement**
  - Entity + Fluent API unique index đúng 4 cột; `CreatedAtUtc = DateTime.UtcNow`.
  - Migration: chạy `dotnet ef migrations add InitialTranslationCache --project "ReTran.App/ReTran.App.csproj"` (cần `Design` package + `DesignTimeDbContextFactory` trỏ options mặc định — MAUI không host lúc design-time; nếu tool cần `--startup-object`, dùng factory). **Không** dùng `EnsureCreated` thay migration.
  - `LookupAsync`: `texts` rỗng → kết quả rỗng ngay; query `Contains` 1 lần; trả hits dict + misses (giữ thứ tự input).
  - `PutAsync`: upsert an toàn (EF: attach theo key + update, hoặc query trước trong transaction).

- [ ] **Step 4: Chạy verify PASS** — **137** test cases; confirm file migration xuất hiện trong `ReTran.App/Migrations/`.

- [ ] **Step 5: Commit** — `feat(app): EF Core translation cache (unique key context+text+src+dst) + migration`

---

### Task 10: `AppSettings` + `ICorePeer`/`PipelineController` + DI + stderr drain

**Files:**
- Create: `ReTran.App/Services/AppSettingsService.cs`, `ReTran.App/Core/ICorePeer.cs`, `ReTran.App/Core/PipelineController.cs`
- Modify: `ReTran.App/Core/CoreProcessClient.cs` (implement `ICorePeer` + **stderr drain**), `ReTran.App/MauiProgram.cs` (đăng ký DI), `ReTran.App/MainPage.xaml.cs` (dùng singleton từ DI thay `new CoreProcessClient()`)
- Test: `ReTran.Tests/AppSettingsServiceTests.cs`, `ReTran.Tests/PipelineControllerTests.cs`

**Interfaces:**
- Consumes: T8 `JsonRpcClient` hooks, T9 `TranslationCache`, `OverlayState.Publish`, `CoreLocator.FindCoreExe`.
- Produces (Task 11 dùng):
  - `public sealed record AppSettings(string Provider, string Src, string Dst, string? ApiKey)` — default `(google, auto, vi, null)`.
  - `public sealed class AppSettingsService(string? path = null)` — `AppSettings Load()`, `void Save(AppSettings s)`; path default `%LOCALAPPDATA%\ReTran\settings.json`; file thiếu/hỏng → default.
  - `public interface ICorePeer` — `bool IsStarted { get; }`, `Task StartCoreAsync(CancellationToken ct)`, `Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)`, `void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>>)`, `event Action<string, JsonNode?>? NotificationReceived`, `event Action<Exception>? HandlerError` (CoreProcessClient delegate từ JsonRpcClient).
  - `public sealed record SourceSelection(bool UseWindow, int Monitor, string? HwndHex);`
  - `public sealed class PipelineController` — ctor `(ICorePeer peer, AppSettingsService settings, TranslationCache cache, OverlayState overlay, ILogger<PipelineController> log)`:
    - `Task StartAsync(SourceSelection source, CancellationToken ct)` — ensure core chạy (chưa → `StartCoreAsync`), đăng ký handler 1 lần, gửi `core.start` (params đúng spec §3.1, `ocrFps`/`captureFps` omit → Core default); `RpcException` propagate nguyên cho UI.
    - `Task StopAsync(CancellationToken ct)` → `core.stop`.
    - `Task<(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows)> ListSourcesAsync(CancellationToken ct)` — parse `core.list_sources` (records UI: `MonitorInfo(int Id,string Name)`, `WindowInfo(string Hwnd,string Title,string Process)` — record App riêng, không tham chiếu Core).
    - `event Action<bool, string?>? PipelineStateChanged` — từ notification `pipeline.state`.
  - Handler trong controller: `translate.request_cache` → `cache.LookupAsync` (trả JSON `{hits,misses}`; exception → throw để client trả -32603); `translate.put` → `cache.PutAsync` fire-and-forget + log; `frame.translated` → parse boxes → `overlay.Publish(new OverlayFrame(w, h, DateTime.UtcNow, boxes))` (`translated:null` → để `OverlayBox.Translated = Text`? **Không** — giữ `null`? `OverlayBox.Translated` là `string` không nullable →传 **text gốc** khi null để UI/OBS luôn có chữ); `pipeline.state` → event.

- [ ] **Step 1: Viết test failing**

```csharp
public class AppSettingsServiceTests
{
    [Fact] public void MissingFile_ReturnsDefaults()        // (google, auto, vi, null)
    [Fact] public void SaveThenLoad_Roundtrips()            // path tempfile
}

public class PipelineControllerTests
{
    // FakeCorePeer: ghi lại CallAsync(method, params), script response; Fake cache/overlay qua DI thật với temp DB

    [Fact] public async Task Start_BuildsCoreStartParams_FromSettingsAndSelection()
    // settings (google,auto,vi); selection screen monitor 1 ->
    // assert method "core.start", params.source {kind:screen, monitor:1}, params.translate {provider:google, src:auto, dst:vi}

    [Fact] public async Task Start_CoreNotRunning_AutoStartsFirst()   // IsStarted=false -> StartCoreAsync được gọi trước
    [Fact] public async Task FrameTranslated_Notification_PublishesOverlay() // notification -> OverlayState.Latest có box, translated null -> dùng text gốc
    [Fact] public async Task PipelineState_Notification_RaisesEvent()  // {running:false,reason:"sidecar-exited"} -> event(false,"sidecar-exited")
    [Fact] public async Task Put_Notification_WritesCacheUpert()      // bản ghi có trong DB temp
}
```

- [ ] **Step 2: Chạy verify FAIL** — `--filter "AppSettingsServiceTests|PipelineControllerTests"` → FAIL.

- [ ] **Step 3: Implement**
  - `AppSettingsService`: System.Text.Json, ghi atom (tmp + move), thư mục tạo nếu thiếu.
  - `CoreProcessClient` → `ICorePeer`: thêm `_proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Trace.WriteLine($"[core] {e.Data}"); }; _proc.BeginErrorReadLine();` (**bắt buộc** — Review Focus #6, redirect mà không đọc sẽ deadlock Core); delegate events/`SetRequestHandler` xuống `_client` (tạo sau StartAsync — expose `IsStarted = _client is not null`).
  - `MauiProgram`: `AddSingleton<CoreProcessClient>` (as `ICorePeer`), `AddSingleton<AppSettingsService>`, `AddSingleton<TranslationCache>`, `AddDbContext<RetranDbContext>(UseSqlite path)`, `AddSingleton<PipelineController>`; app start → `db.Database.Migrate()` (best-effort + log).
  - `MainPage`: resolve `ICorePeer` từ `IPlatformApplication.Current.Services` thay `new CoreProcessClient()`; `OnStopClicked` gọi `DisposeAsync` của singleton như hiện tại (Start ở Home sẽ auto-restart).
  - Đăng ký handler: làm trong `PipelineController` **lần đầu** `StartAsync`/`ListSourcesAsync` gọi (peer đã started) — idempotent (`_handlersWired` flag).

- [ ] **Step 4: Chạy verify PASS** — **144** test cases; `dotnet build "ReTran Project.slnx"` clean.

- [ ] **Step 5: Commit** — `feat(app): AppSettings + PipelineController (core.start/stop, frame.translated→overlay) + DI + drain stderr`

---

### Task 11: UI — trang Cài đặt + Start/Stop trên trang Điều khiển

**Files:**
- Create: `ReTran.App/Components/Pages/Settings.razor`
- Modify: `ReTran.App/Components/Layout/NavMenu.razor` (link ⚙ `href="settings"`), `ReTran.App/Components/Pages/Home.razor` (khối Start/Stop + source dropdown + quick dst; card "Dịch" đổi thành dst nhanh), `ReTran.App/Services/I18nStrings.cs` (keys mới cả 2 bảng)
- Test: `ReTran.Tests/SettingsPageTests.cs` (mới), mở rộng `ReTran.Tests/HomeComponentTests.cs`, `ReTran.Tests/ChromeLocalizationTests.cs`

**Interfaces:**
- Consumes: T10 `PipelineController`, `AppSettingsService`, `SourceSelection`; patterns bUnit hiện có (`HomeComponentTests.AddHomeServices`, `I18nService`).
- Produces: UI — không API mới.

**i18n keys cần thêm (cả `Vi` lẫn `En`):**
`nav.settings`, `settings.title`, `settings.card.provider`, `settings.card.lang`, `settings.label.provider`, `settings.label.src`, `settings.label.dst`, `settings.label.apiKey`, `settings.opt.auto`, `settings.opt.comingSoon`, `settings.btn.save`, `settings.status.saved`, `settings.status.saveFailed`, `settings.note.srcNextStart`,
`home.card.pipeline`, `home.status.stopped`, `home.status.running`, `home.status.coreError`, `home.btn.start`, `home.btn.stop`, `home.btn.refreshSources`, `home.label.screen`, `home.label.window`, `home.opt.pickWindow`, `home.label.quickDst`, `home.status.startFailed`, `home.status.stopFailed`,
cùng **cập nhật chuỗi cũ** `home.note.capture` (bỏ câu "RPC capture chưa có" — giờ đã có) và `home.btn.startDemo` (bỏ "(Core live ở milestone sau)").

- [ ] **Step 1: Viết test failing**

```csharp
public class SettingsPageTests
{
    [Fact] public void Renders_GoogleSelected_OthersDisabled()  // select provider: google enabled, deepl/llm có attribute disabled
    [Fact] public async Task Save_WritesSettingsFile_AndShowsStatus() // temp path qua fake AppSettingsService/params
}

// Mở rộng HomeComponentTests (DI fake ICorePeer thật PipelineController + temp DB):
[Fact] public async Task StartButton_CallsCoreStart_WithSettingsAndSelection()
[Fact] public async Task StopButton_CallsCoreStop()
[Fact] public async Task PipelineStateNotification_UpdatesBadge()   // bắn NotificationReceived -> badge "Đang dừng"/text i18n
// Mở rộng ChromeLocalizationTests:
[Fact] public void NavMenu_ShowsSettingsLink_InCurrentLang()
```

- [ ] **Step 2: Chạy verify FAIL** — `--filter "SettingsPageTests"` → FAIL; `Home`/`NavMenu` test mới FAIL.

- [ ] **Step 3: Implement**
  - `Settings.razor` (`@page "/settings"`): inject `I18nService`, `AppSettingsService`; provider `<select>` option `google` + 2 option `disabled`; src list `auto,en,ja,ko,zh,ru,fr,de,es`; dst list `vi,en,ja,ko,zh,ru,fr,de,es`; api-key `<input disabled="@(_model.Provider == "google")>`; Lưu → `Save` + status; note "áp dụng cho lần start kế tiếp".
  - `Home.razor`: Card mới `home.card.pipeline` — badge trạng thái (state từ `PipelineController.PipelineStateChanged` + kết quả start/stop), 2 select nguồn (màn hình từ `ListSourcesAsync().Monitors`, cửa sổ từ `.Windows` + placeholder `home.opt.pickWindow`), nút refresh (gọi lại list), nút Bắt đầu/Dừng (Bắt đầu: nếu demo `_running` → `StopDemo()` trước; gửi `SourceSelection`: chọn cửa sổ khác rỗng → `UseWindow` true, ngược lại monitor). Card "Dịch" hiện tại → select **dst nhanh** (`AppSettingsService.Load().Dst`, đổi → `Save` ngay, note `settings.note.srcNextStart`). Gọi `ListSourcesAsync` + đăng ký `PipelineStateChanged` trong `OnInitialized` (dispose đúng pattern `IDisposable` như hiện có).
  - `NavMenu.razor`: NavLink thứ 2 `href="settings"` icon `&#9881;`.
  - I18nStrings: thêm đủ keys trên, song ngữ, không quên cập nhật 2 chuỗi cũ.

- [ ] **Step 4: Chạy verify PASS** — **150** test cases; `dotnet build "ReTran Project.slnx"` clean; `npm run build` trong `ReTran.App/vite-project` không liên quan (không đụng TS).

- [ ] **Step 5: Commit** — `feat(app): trang Cài đặt + Start/Stop pipeline trên trang điều khiển (i18n vi/en)`

---

### Task 12: Integration scripted-serve + đo perf + Docs/performance.md M4

**Files:**
- Create/Modify: `ReTran.Tests/ServeM4IntegrationTests.cs` (mới), `Docs/performance.md` (section M4)
- Test: 3 integration tests (skip-êm)

**Interfaces:**
- Consumes: binary Core đã build, harness `ServeOcrTests` (stdio scripted), T6 methods.
- Produces: số đo + bằng chứng E2E.

- [ ] **Step 1: Viết 3 test integration (skip-êm)**

```csharp
[Fact] public async Task Serve_PingStopListSources_Roundtrip_SkipEamWithoutExe()
// spawn ReTran.Core --serve (tìm exe như CoreLocator, thiếu -> early return);
// core.ping -> "pong"; core.stop trước start -> {"running":false}; core.list_sources -> monitors[] + windows[] parse được

[Fact] public async Task Serve_StartWithBogusProvider_Rejects_SkipEamWithoutExe()
// core.start provider "nope" -> error.code -32602

[Fact] public async Task Serve_Start_EmitsFrameTranslated_AndRequestsCache_SkipEam()
// core.start {source:{kind:"screen",monitor:0}, translate:{provider:"google",src:"auto",dst:"vi"}} -> {"running":true}
//   + đọc tiếp (scripted, FIFO):
//   - notification "frame.translated": seq/w/h/boxes parse được — PHẢI có (kể cả boxes:[])
//   - nếu box nào có text != rỗng -> trước đó Core phải gửi request id+method "translate.request_cache";
//     test reply {hits:{},misses:[...]} và không được deadlock/timeout
// core.start trả -32603 (thiếu sidecar/máy không có capture) -> early return (skip-êm)
```

- [ ] **Step 2: Chạy verify PASS** — **159** test cases (môi trường thiếu exe → skip-êm, không fail).

- [ ] **Step 3: E2E manual + đo perf (chạy thật, ghi số)**

Checklist (chạy trên máy user, có game/cửa sổ thật):
1. `dotnet build "ReTran Project.slnx"`; chạy App → Start Core (MainPage) → trang Điều khiển → chọn nguồn → **Bắt đầu**.
2. Quan sát: badge ● Đang chạy; overlay/OBS (`http://127.0.0.1:17863/overlay`) hiện box + bản dịch VI; text gốc hiện khi chưa dịch xong (`translated:null` → text gốc).
3. Dừng → badge ■; Start lại → text cũ hiện lại (phiên mới, cache hit không gọi Google).
4. Trang Cài đặt: đổi dst → Lưu → Start lại → bản dịch đổi ngôn ngữ.
5. Đo (ghi method + confounder như kỷ luật M2/M3): latency Google `translate` (10 lần qua CLI `translate`), latency `core.start` → frame.translated đầu tiên, CPU/RAM App+Core trong 60 s chạy. Nếu máy load > 20% → ghi nhận, không kết luận.

- [ ] **Step 4: Ghi `Docs/performance.md` section M4** — bảng số thật + method + confounder; **không** tuyên bố PASS/BREACH vượt budget khi chưa đủ số.

- [ ] **Step 5: Commit** — `test(app): integration scripted-serve M4 + docs: perf numbers M4`

---

## Self-review notes (plan đã tự kiểm)

- **Spec coverage:** §3 IPC → T2/T6/T8/T10 · §4 loop/translator/CLI → T1/T3/T5/T6/T7 · §5 EF/client/handlers → T8/T9/T10 · §6 UI → T11 · §7 error → T2 (routing), T5 (không block), T6 (pipeline.state), T10 (stderr drain) · §8 perf → T5 (sem 4, timeout), T12 (đo) · §9 testing → từng task + T12.
- **Box contract đã verify thay spec "bước 0":** `Spot.box` = quad **pixel** (`engine.py:14`) → `SpotGeometry.NormalizeQuad` (T4) — không còn mục chờ verify.
- **Type consistency:** `CacheLookupResult` định nghĩa 2 bên (Core T5 / App T9) cùng shape — deliberate (không tham chiếu chéo biên App/Core); `NotifyAsync` khớp `IJsonRpcNotifier` T2 ↔ sink T5 ↔ host T6; `SourceSelection` T10 ↔ Home T11.
- **Proportion:** plan không viết body code ngoài đoạn algorithm thật sự (normalize quad, background chain T5) — phần còn lại là signature + tên test + assertion.
- **Review pass 2 (2026-10-06):** đã fix inline — thêm `**Spec:**` ở header · ghim box payload `{x,y,w,h,text,translated}` (spec §3.3) · T2 design `FakeTransport` mở rộng + sửa test race (chạy read loop trước khi inject) · T5 viết lại test block thành code C# hợp lệ (+`WhenIdleAsync`, bỏ `sinkEmit_next`/`StderrRef`) · T7 test cụ thể qua `OcrTestSupport.RunDispatcherAsync` (4 test) · T12 thêm test E2E `core.start → frame.translated + translate.request_cache` (spec §9) → counts mới (pass 3, đếm lại theo brief test code + `dotnet test` Total): T3 90 · T4 98 · T5 105 · T6 113 · T7 117 · T8 123 · T9 128 · T10 135 · T11 141 · T12 144. Fix round 2 T5 (reviewer Important #1: hard-bound lookup 2s + test lookup treo) → counts T5 106 · T6 118 · T7 122 · T8 128 · T9 133 · T10 140 · T11 146 · T12 149. Fix round T6 (reviewer Important: +4 tests cover stop-silent/exactly-once/source-fail/fps-default + sửa doc JsonRpcCacheChannel) → T6 118 · T7 122 · T8 128 · T9 133 · T10 140 · T11 146 · T12 149. Fix round T8 (reviewer Important: FailAllPending EOF/dispose + dọn pending khi write fail; +3 tests) → T8 131 · T9 136 · T10 143 · T11 149 · T12 152. Fix round T9 (reviewer Important: pin unique index bằng test duplicate-key → DbUpdateException; +1 test) → T9 137 · T10 144 · T11 150 · T12 153. Final-review fix wave (+4 test wire/badge; +1 ChangeTracker; +2 CLI deterministic; −1 Google-real vô dụng) → T12 159.
