namespace ReTran.Core.Serve;

using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Config;
using ReTran.Core.JsonRpc;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using ReTran.Core.Translate;

/// <summary>
/// Serve-mode orchestrator: implements the <c>core.start</c> / <c>core.stop</c> /
/// <c>core.list_sources</c> contract (spec §3.1), runs one <see cref="OcrLoop"/> session at a
/// time in the background, and pushes <c>pipeline.state {running, reason}</c> notifications when
/// the loop dies on its own (spec §3.3).
/// Điều phối viên serve mode: cung cấp hợp đồng <c>core.start</c> / <c>core.stop</c> /
/// <c>core.list_sources</c> (spec §3.1), chạy nền mỗi phiên <see cref="OcrLoop"/> một lần tại một
/// thời điểm, và đẩy thông báo <c>pipeline.state {running, reason}</c> khi loop tự chết
/// (spec §3.3).
/// </summary>
/// <remarks>
/// Normal start/stop answer within their own request — no <c>pipeline.state</c> is sent for them.
/// Start/stop bình thường đã có kết quả request của chính nó — không gửi <c>pipeline.state</c>.
/// </remarks>
public sealed class PipelineHost : IAsyncDisposable
{
    /// <summary>Best-effort bound on waiting for the loop to wind down in <see cref="StopAsync"/>.
    /// Giới best-effort chờ loop dừng trong <see cref="StopAsync"/>.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly ISidecarRpc? _rpc;
    private readonly TranslatorRegistry _registry;
    private readonly JsonRpcServer _server;
    private readonly TextWriter _stderr;
    private readonly Func<OcrLoopOptions, ICaptureSource>? _sourceFactory;
    private Session? _session;

    /// <summary>
    /// Creates an orchestrator over the shared sidecar/registry/peer.
    /// Tạo điều phối viên trên sidecar/registry/peer dùng chung.
    /// </summary>
    /// <param name="rpc">OCR sidecar peer, or <see langword="null"/> when it failed to start. Peer sidecar OCR, hoặc <see langword="null"/> khi chưa khởi tạo được.</param>
    /// <param name="registry">Translation providers resolved by <c>translate.provider</c>. Các nhà cung cấp dịch tra theo <c>translate.provider</c>.</param>
    /// <param name="server">Peer server used for <c>pipeline.state</c> and cache IPC. Peer server dùng cho <c>pipeline.state</c> và IPC cache.</param>
    /// <param name="stderr">Warnings/errors sink. Nơi ghi cảnh báo/lỗi.</param>
    /// <param name="sourceFactory">
    /// Optional capture-source factory (tests inject fakes); <see langword="null"/> uses
    /// <see cref="OcrLoop.CreateSource"/>. Factory capture-source tùy chọn (test inject bản giả);
    /// <see langword="null"/> dùng <see cref="OcrLoop.CreateSource"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when a non-nullable reference parameter is null. Ném khi tham số tham chiếu bắt buộc nào đó là null.</exception>
    public PipelineHost(
        ISidecarRpc? rpc,
        TranslatorRegistry registry,
        JsonRpcServer server,
        TextWriter stderr,
        Func<OcrLoopOptions, ICaptureSource>? sourceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(stderr);
        _rpc = rpc;
        _registry = registry;
        _server = server;
        _stderr = stderr;
        _sourceFactory = sourceFactory;
    }

    /// <summary>
    /// Starts one pipeline session (<c>core.start</c>): validates params, opens the capture source,
    /// and launches the loop in the background. Khởi động một phiên pipeline (<c>core.start</c>):
    /// kiểm tra tham số, mở capture source, rồi chạy loop ở chế độ nền.
    /// </summary>
    /// <param name="@params">
    /// <c>{source:{kind:"screen",monitor}|"window",hwnd}, translate:{provider,src,dst},
    /// ocrFps?, captureFps?}</c> — missing fps values fall back to <see cref="CoreConfig"/> defaults.
    /// Tham số theo hợp đồng spec §3.1 — fps thiếu thì lấy default từ <see cref="CoreConfig"/>.
    /// </param>
    /// <param name="ct">Caller token; the session runs on its own token. Token của caller; phiên chạy bằng token riêng.</param>
    /// <returns><c>{"running": true}</c>. Kết quả <c>{"running": true}</c>.</returns>
    /// <exception cref="RpcMethodException">
    /// -32602 on malformed params or unknown provider; -32600 when a session is already running;
    /// -32603 when the sidecar is unavailable or the capture source cannot be opened.
    /// -32602 khi tham số sai hoặc provider lạ; -32600 khi phiên đang chạy;
    /// -32603 khi sidecar không sẵn sàng hoặc không mở được capture source.
    /// </exception>
    public Task<JsonNode?> StartAsync(JsonNode? @params, CancellationToken ct)
    {
        StartSpec spec = ParseStart(@params);
        lock (_sync)
        {
            if (_session is not null)
                throw new RpcMethodException(RpcErrorCodes.InvalidRequest,
                    "pipeline already running (core.stop first).");

            ISidecarRpc? rpc = _rpc;
            if (rpc is null)
                throw new RpcMethodException(RpcErrorCodes.InternalError,
                    "sidecar not available (python/venv missing or spawn failed).");

            ICaptureSource source;
            try
            {
                Func<OcrLoopOptions, ICaptureSource> factory = _sourceFactory ?? OcrLoop.CreateSource;
                source = factory(spec.ToOptions());
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new RpcMethodException(RpcErrorCodes.InternalError,
                    $"capture source unavailable: {ex.Message}");
            }

            string context = spec.UseWindow
                ? ContextKey.FromHwnd(spec.Hwnd)
                : ContextKey.FromMonitor(spec.Monitor);
            var pipeline = new OcrPipeline(rpc, new OcrDiff());
            var sink = new TranslateCycleSink(context, spec.Src, spec.Dst, spec.Translator,
                new JsonRpcCacheChannel(_server), _server, _stderr);

            var session = new Session(source);
            _session = session;
            session.Loop = Task.Run(() => RunSessionAsync(session, spec.ToOptions(), pipeline, sink));
        }
        return Task.FromResult<JsonNode?>(new JsonObject { ["running"] = true });
    }

    /// <summary>
    /// Stops the running session, if any (<c>core.stop</c>) — idempotent: stopping before any
    /// start simply reports <c>{"running": false}</c>. Dừng phiên đang chạy, nếu có
    /// (<c>core.stop</c>) — idempotent: dừng khi chưa start chỉ trả <c>{"running": false}</c>.
    /// </summary>
    /// <param name="@params">Unused; kept for the handler signature. Không dùng; giữ cho khớp signature handler.</param>
    /// <param name="ct">Token used to cancel the (bounded) wait for the loop. Token dùng hủy lượt chờ (có giới) loop.</param>
    /// <returns><c>{"running": false}</c>. Kết quả <c>{"running": false}</c>.</returns>
    public async Task<JsonNode?> StopAsync(JsonNode? @params, CancellationToken ct)
    {
        Session? session;
        lock (_sync)
        {
            session = _session;
            if (session is not null)
            {
                // Set before cancel (so the completion path stays silent) and cancel under the
                // lock so it cannot race EndSession's Cts.Dispose.
                // Đặt trước khi hủy (đường kết thúc giữ im) và hủy dưới lock để không đua với
                // Cts.Dispose của EndSession.
                session.StopRequested = true;
                try
                {
                    session.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // loop already ended and disposed its own CTS. loop đã kết thúc và tự dispose CTS.
                }
            }
        }

        if (session is not null)
        {
            if (session.Loop is Task loop)
            {
                try
                {
                    await loop.WaitAsync(StopTimeout, ct);
                }
                catch (TimeoutException)
                {
                    _stderr.WriteLine($"warn: pipeline loop did not stop within {StopTimeout.TotalSeconds:0}s.");
                }
                catch (OperationCanceledException)
                {
                    // caller cancelled the wait — the stop itself is best-effort. caller hủy lượt chờ — stop vẫn best-effort.
                }
                catch (Exception ex)
                {
                    _stderr.WriteLine($"warn: pipeline stop failed: {ex.Message}");
                }
            }

            DisposeSourceOnce(session);
            lock (_sync)
            {
                if (ReferenceEquals(_session, session)) _session = null;
            }
        }
        return new JsonObject { ["running"] = false };
    }

    /// <summary>
    /// Lists capture sources for the UI dropdowns (<c>core.list_sources</c>).
    /// Liệt kê nguồn chụp cho dropdown của UI (<c>core.list_sources</c>).
    /// </summary>
    /// <param name="ct">Unused — enumeration is synchronous and fast. Không dùng — liệt kê đồng bộ và nhanh.</param>
    /// <returns>
    /// <c>{"monitors":[{id,name}], "windows":[{hwnd,title,process}]}</c> — <c>hwnd</c> is
    /// <c>0x</c>-hex, <c>process</c> is the lowercase owner process (<c>unknown</c> when unresolvable).
    /// <c>{"monitors":[{id,name}], "windows":[{hwnd,title,process}]}</c> — <c>hwnd</c> dạng
    /// hex <c>0x</c>, <c>process</c> là tên tiến trình chủ viết thường (<c>unknown</c> khi không tra được).
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when monitor/window enumeration fails. Khi liệt kê monitor/cửa sổ thất bại.</exception>
    public Task<JsonNode?> ListSourcesAsync(CancellationToken ct)
    {
        var monitors = new JsonArray();
        foreach (MonitorInfo monitor in CaptureCatalog.ListMonitors())
            monitors.Add(new JsonObject { ["id"] = monitor.Index, ["name"] = monitor.DeviceName });

        var windows = new JsonArray();
        foreach (WindowInfo window in CaptureCatalog.ListWindows())
        {
            windows.Add(new JsonObject
            {
                ["hwnd"] = $"0x{window.Hwnd:X}",
                ["title"] = window.Title,
                ["process"] = ContextKey.FromHwnd(window.Hwnd),
            });
        }
        return Task.FromResult<JsonNode?>(new JsonObject
        {
            ["monitors"] = monitors,
            ["windows"] = windows,
        });
    }

    /// <summary>
    /// Stops any running session and releases it — called when serve mode exits.
    /// Dừng (nếu có) và giải phóng phiên đang chạy — gọi khi serve mode thoát.
    /// </summary>
    public async ValueTask DisposeAsync() => await StopAsync(null, CancellationToken.None);

    /// <summary>
    /// Validates one <c>core.start</c> payload in spec §3.1 order: params (-32602), then the
    /// caller checks state/sidecar/source (-32600/-32603).
    /// Kiểm tra một payload <c>core.start</c> theo đúng thứ tự spec §3.1: params (-32602), rồi
    /// phần state/sidecar/source (-32600/-32603).
    /// </summary>
    /// <param name="@params">Raw request params. Params thô của request.</param>
    /// <returns>The parsed, provider-resolved start spec. Start spec đã parse và đã resolve provider.</returns>
    /// <exception cref="RpcMethodException">Thrown with -32602 on any contract violation. Ném với -32602 khi vi phạm hợp đồng.</exception>
    private StartSpec ParseStart(JsonNode? @params)
    {
        if (@params is not JsonObject p)
            throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                "params must be an object ({source, translate, ocrFps?, captureFps?}).");

        if (p["source"] is not JsonObject source)
            throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                "missing 'source' object ({kind: \"screen\"|\"window\", ...}).");

        string kind = RequiredString(source, "source", "kind");
        bool useWindow;
        int monitor = 0;
        nint hwnd = 0;
        switch (kind)
        {
            case "screen":
                useWindow = false;
                if (source["monitor"] is JsonValue monitorValue
                    && (!monitorValue.TryGetValue<int>(out monitor) || monitor < 0))
                {
                    throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                        "'source.monitor' must be an integer >= 0.");
                }
                break;
            case "window":
                useWindow = true;
                string? hwndText = ReadString(source, "hwnd");
                if (string.IsNullOrWhiteSpace(hwndText))
                    throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                        "'source.hwnd' must be a \"0x...\" or decimal string.");
                try
                {
                    hwnd = CaptureCommands.ParseHwnd(hwndText);
                }
                catch (FormatException ex)
                {
                    throw new RpcMethodException(RpcErrorCodes.InvalidParams, ex.Message);
                }
                break;
            default:
                throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                    $"unknown 'source.kind' \"{kind}\" (expected \"screen\" or \"window\").");
        }

        if (p["translate"] is not JsonObject translate)
            throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                "missing 'translate' object ({provider, src, dst}).");
        string provider = RequiredString(translate, "translate", "provider");
        string src = RequiredString(translate, "translate", "src");
        string dst = RequiredString(translate, "translate", "dst");
        ITranslator translator = _registry.Get(provider)
            ?? throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                $"unknown translate.provider \"{provider}\".");

        CoreConfig config = CoreConfig.LoadFrom(CoreConfig.DefaultPath);
        int ocrFps = config.OcrFps;
        if (p["ocrFps"] is JsonValue fpsValue
            && (!fpsValue.TryGetValue<int>(out ocrFps) || ocrFps is < 1 or > 60))
        {
            throw new RpcMethodException(RpcErrorCodes.InvalidParams, "'ocrFps' must be an integer 1..60.");
        }
        // captureFps is accepted+validated per spec §3.1 but not yet plumbed into the capture
        // source (OcrLoopOptions has no field; OcrLoop.CreateSource uses the source default 10).
        // captureFps được nhận+kiểm tra theo spec §3.1 nhưng chưa truyền vào capture source
        // (OcrLoopOptions không có field; OcrLoop.CreateSource dùng default 10 của source).
        int captureFps = config.CaptureFps;
        if (p["captureFps"] is JsonValue captureValue
            && (!captureValue.TryGetValue<int>(out captureFps) || captureFps < 1))
        {
            throw new RpcMethodException(RpcErrorCodes.InvalidParams, "'captureFps' must be an integer >= 1.");
        }

        return new StartSpec(useWindow, monitor, hwnd, provider, src, dst, translator, ocrFps, captureFps);
    }

    /// <summary>
    /// Background body of one session: runs the loop, guarantees at most one
    /// <c>pipeline.state</c> notification when the loop ends without a stop, then ends the session.
    /// Thân nền của một phiên: chạy loop, đảm bảo tối đa một thông báo <c>pipeline.state</c>
    /// khi loop kết thúc mà không do stop, rồi kết thúc phiên.
    /// </summary>
    private async Task RunSessionAsync(
        Session session, OcrLoopOptions options, OcrPipeline pipeline, ICycleSink sink)
    {
        try
        {
            await OcrLoop.RunAsync(options, session.Source, pipeline, sink, _stderr, session.Cts.Token,
                reason => NotifyStateOnce(session, reason));
            if (!session.StopRequested)
                NotifyStateOnce(session, "capture-error"); // no onFatal: first call wins (a fatal reason was already sent, if any)
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: pipeline loop ended unexpectedly: {ex.Message}");
            if (!session.StopRequested)
                NotifyStateOnce(session, "internal");
        }
        finally
        {
            EndSession(session);
        }
    }

    /// <summary>
    /// Sends <c>pipeline.state {running:false, reason}</c> exactly once per session; suppressed
    /// when the session is stopping normally. Gửi <c>pipeline.state {running:false, reason}</c>
    /// đúng một lần mỗi phiên; bị chặn khi phiên đang dừng bình thường.
    /// </summary>
    private void NotifyStateOnce(Session session, string reason)
    {
        if (session.StopRequested) return;
        if (Interlocked.Exchange(ref session.StateNotified, 1) != 0) return;
        _ = NotifyStateSafeAsync(reason);
    }

    /// <summary>
    /// Fire-and-forget sender: a notify failure must never crash the loop's completion path.
    /// Người gửi fire-and-forget: lỗi notify không bao giờ được làm crash đường kết thúc của loop.
    /// </summary>
    private async Task NotifyStateSafeAsync(string reason)
    {
        try
        {
            await _server.NotifyAsync("pipeline.state", new JsonObject
            {
                ["running"] = false,
                ["reason"] = reason,
            });
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: pipeline.state notify failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the session slot (only when it is still the current one), disposes the capture
    /// source once, and disposes the session token source. Xóa slot phiên (chỉ khi vẫn là phiên
    /// hiện tại), dispose capture source đúng một lần, và dispose token source của phiên.
    /// </summary>
    private void EndSession(Session session)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_session, session)) _session = null;
            session.Cts.Dispose(); // under the lock: serialized against StopAsync's Cancel
        }
        DisposeSourceOnce(session);
    }

    /// <summary>
    /// Disposes the session's capture source exactly once (stop, loop end, and timeout may race).
    /// Dispose capture source của phiên đúng một lần (stop, loop kết thúc và timeout có thể đua nhau).
    /// </summary>
    private void DisposeSourceOnce(Session session)
    {
        if (Interlocked.Exchange(ref session.SourceDisposed, 1) != 0) return;
        try
        {
            session.Source.Dispose();
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: capture source dispose failed: {ex.Message}");
        }
    }

    /// <summary>Reads a required non-empty string field, or throws -32602. Đọc field bắt buộc là chuỗi khác rỗng, hoặc ném -32602.</summary>
    private static string RequiredString(JsonObject obj, string owner, string key) =>
        ReadString(obj, key) is string value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                $"'{owner}.{key}' must be a non-empty string.");

    /// <summary>Reads a string field, yielding <see langword="null"/> for missing/non-string. Đọc field chuỗi, trả <see langword="null"/> khi thiếu/không phải chuỗi.</summary>
    private static string? ReadString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    /// <summary>Parsed and validated <c>core.start</c> params (spec §3.1). Tham số <c>core.start</c> đã parse và kiểm tra (spec §3.1).</summary>
    private sealed record StartSpec(
        bool UseWindow,
        int Monitor,
        nint Hwnd,
        string Provider,
        string Src,
        string Dst,
        ITranslator Translator,
        int OcrFps,
        int CaptureFps)
    {
        public OcrLoopOptions ToOptions() => new(UseWindow, Monitor, Hwnd, OcrFps, 0);
    }

    /// <summary>Per-start state: source, loop token, background task, and once-flags. Trạng thái mỗi lần start: source, token loop, task nền và cờ once.</summary>
    private sealed class Session
    {
        public Session(ICaptureSource source)
        {
            Source = source;
            Cts = new CancellationTokenSource();
        }

        public ICaptureSource Source { get; }
        public CancellationTokenSource Cts { get; }
        public Task? Loop { get; set; }
        public volatile bool StopRequested;
        public int StateNotified;
        public int SourceDisposed;
    }
}
