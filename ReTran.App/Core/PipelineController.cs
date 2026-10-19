using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReTran_App.Services;
using ReTran_App.Services.Camera;

namespace ReTran_App.Core;

/// <summary>
/// Capture source chosen by the user for one pipeline session.
/// Nguồn capture do người dùng chọn cho một phiên pipeline.
/// </summary>
/// <param name="UseWindow">True to capture a window, false to capture a monitor. True để capture cửa sổ, false để capture màn hình.</param>
/// <param name="Monitor">Zero-based monitor index used when <paramref name="UseWindow"/> is false. Chỉ số màn hình (bắt đầu từ 0) dùng khi <paramref name="UseWindow"/> là false.</param>
/// <param name="HwndHex">Window handle as a hex string (<c>0x…</c>) used when <paramref name="UseWindow"/> is true. Handle cửa sổ dạng chuỗi hex (<c>0x…</c>) dùng khi <paramref name="UseWindow"/> là true.</param>
public sealed record SourceSelection(bool UseWindow, int Monitor, string? HwndHex);

/// <summary>
/// One display monitor offered as a capture source. Một màn hình được liệt kê làm nguồn capture.
/// </summary>
/// <param name="Id">Zero-based monitor index. Chỉ số màn hình (bắt đầu từ 0).</param>
/// <param name="Name">Human-readable monitor name. Tên hiển thị của màn hình.</param>
public sealed record MonitorInfo(int Id, string Name);

/// <summary>
/// One capturable top-level window. Một cửa sổ cấp cao có thể capture.
/// </summary>
/// <param name="Hwnd">Window handle as a hex string (<c>0x…</c>). Handle cửa sổ dạng chuỗi hex (<c>0x…</c>).</param>
/// <param name="Title">Window title. Tiêu đề cửa sổ.</param>
/// <param name="Process">Owning process file name. Tên tệp tiến trình sở hữu.</param>
public sealed record WindowInfo(string Hwnd, string Title, string Process);

/// <summary>
/// App-side orchestrator over <see cref="ICorePeer"/>: starts/stops the Core pipeline
/// (<c>core.start</c>/<c>core.stop</c>/<c>core.list_sources</c>), answers Core's cache
/// requests from the SQLite translation cache, and turns Core notifications into
/// <see cref="OverlayState"/> frames and <see cref="PipelineStateChanged"/>.
/// Bộ điều phối phía App trên <see cref="ICorePeer"/>: start/dừng pipeline của Core
/// (<c>core.start</c>/<c>core.stop</c>/<c>core.list_sources</c>), trả lời yêu cầu cache của Core
/// từ cache dịch thuật SQLite, và biến thông báo của Core thành khung <see cref="OverlayState"/>
/// cùng <see cref="PipelineStateChanged"/>.
/// </summary>
public sealed class PipelineController
{
    /// <summary>Stop reason relayed when the core process ends on its own. Lý do dừng khi tiến trình core tự kết thúc.</summary>
    public const string CoreExitedReason = "core-exited";

    private readonly ICorePeer _peer;
    private readonly AppSettingsService _settings;
    private readonly TranslationCache _cache;
    private readonly OverlayState _overlay;
    private readonly PreviewState? _preview;
    private readonly ICameraOutput? _camera;
    private readonly ILogger<PipelineController> _log;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    // Giữ handler trong bounds của read-loop: gate wait + truy cập DB tối đa 2 s (lookup và put).
    private static readonly TimeSpan CacheGateTimeout = TimeSpan.FromSeconds(2);
    private bool _handlersWired;

    /// <summary>
    /// Creates a controller bound to the given peer, settings store, cache and overlay.
    /// Tạo bộ điều phối gắn với peer, kho cài đặt, cache và overlay đã cho.
    /// </summary>
    /// <param name="peer">Core connection used for every RPC. Kết nối Core dùng cho mọi RPC.</param>
    /// <param name="settings">Settings store read on every start. Kho cài đặt đọc mỗi lần start.</param>
    /// <param name="cache">Translation cache backing <c>translate.request_cache</c>/<c>translate.put</c>. Cache dịch thuật chống lưng <c>translate.request_cache</c>/<c>translate.put</c>.</param>
    /// <param name="overlay">Overlay state published from <c>frame.translated</c>. Trạng thái overlay được xuất bản từ <c>frame.translated</c>.</param>
    /// <param name="log">Logger for best-effort failures. Logger cho các lỗi best-effort.</param>
    /// <param name="preview">
    /// Optional preview state published from <c>frame.annotated</c>; <see langword="null"/>
    /// disables preview publishing (old tests keep passing). Trạng thái preview tùy chọn được
    /// xuất bản từ <c>frame.annotated</c>; <see langword="null"/> tắt xuất bản preview (test cũ
    /// không vỡ).
    /// </param>
    /// <param name="camera">
    /// Optional virtual-camera output fed from <c>frame.annotated</c> and held when the pipeline
    /// stops; <see langword="null"/> disables the camera path. Đầu ra camera ảo tùy chọn được cấp
    /// khung từ <c>frame.annotated</c> và giữ khung khi pipeline dừng; <see langword="null"/> tắt
    /// đường camera.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    public PipelineController(
        ICorePeer peer,
        AppSettingsService settings,
        TranslationCache cache,
        OverlayState overlay,
        ILogger<PipelineController> log,
        PreviewState? preview = null,
        ICameraOutput? camera = null)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _preview = preview;
        _camera = camera;
    }

    /// <summary>
    /// Raised when Core pushes <c>pipeline.state</c> after the loop stops unexpectedly
    /// (sidecar died, capture error, internal error). Start/stop results do not raise it.
    /// Ném lên khi Core đẩy <c>pipeline.state</c> sau khi loop dừng đột xuất (sidecar chết, lỗi
    /// capture, lỗi nội bộ). Kết quả start/dừng bình thường không kích hoạt event này.
    /// </summary>
    /// <param name="running">Whether the pipeline is still running. Pipeline còn đang chạy hay không.</param>
    /// <param name="reason">Short stop reason code (e.g. <c>sidecar-exited</c>), or <see langword="null"/>. Mã lý do dừng ngắn (ví dụ <c>sidecar-exited</c>), hoặc <see langword="null"/>.</param>
    public event Action<bool, string?>? PipelineStateChanged;

    /// <summary>
    /// Whether the pipeline was last known to be running. Read by a page on its first render so
    /// navigating back to the control page shows the real badge instead of a stale "stopped".
    /// Pipeline có đang chạy theo lần biết cuối hay không. Trang đọc ở render đầu để quay lại trang
    /// điều khiển thấy badge đúng thay vì "stopped" cũ.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// The capture source of the current session, or <see langword="null"/> when no session is active.
    /// Nguồn capture của phiên hiện tại, hoặc <see langword="null"/> khi không có phiên đang chạy.
    /// </summary>
    public SourceSelection? ActiveSource { get; private set; }

    /// <summary>
    /// Ensures the core is running, wires the Core→App handlers once, then sends
    /// <c>core.start</c> built from the persisted settings (via <see cref="AppSettingsService.Load"/>)
    /// and the given source. <c>ocrFps</c>/<c>captureFps</c> are omitted so Core applies its defaults.
    /// Đảm bảo core đang chạy, gắn handler Core→App một lần, rồi gửi <c>core.start</c> dựng từ
    /// cài đặt đã lưu (qua <see cref="AppSettingsService.Load"/>) và nguồn đã cho. <c>ocrFps</c>/
    /// <c>captureFps</c> được bỏ qua để Core dùng mặc định của nó.
    /// </summary>
    /// <param name="source">Capture source for the session. Nguồn capture cho phiên.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once Core acknowledges the start. Task hoàn tất khi Core xác nhận start.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null. Ném khi <paramref name="source"/> là null.</exception>
    /// <exception cref="RpcException">
    /// Propagated unchanged from Core for invalid params, unknown provider or a session already
    /// running, so the UI can show the exact error. Được đẩy ra nguyên vẹn từ Core khi tham số
    /// sai, provider lạ hoặc phiên đang chạy, để UI hiển thị lỗi chính xác.
    /// </exception>
    public async Task StartAsync(SourceSelection source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();

        AppSettings settings = _settings.Load();
        var @params = new JsonObject
        {
            ["source"] = source.UseWindow
                ? new JsonObject { ["kind"] = "window", ["hwnd"] = source.HwndHex ?? string.Empty }
                : new JsonObject { ["kind"] = "screen", ["monitor"] = source.Monitor },
            ["translate"] = new JsonObject
            {
                ["provider"] = settings.Provider,
                ["src"] = settings.Src,
                ["dst"] = settings.Dst,
            },
        };
        await _peer.CallAsync("core.start", @params, ct).ConfigureAwait(false);
        IsRunning = true;
        ActiveSource = source;
    }

    /// <summary>
    /// Sends <c>core.stop</c> to end the current session; Core treats it as idempotent. The virtual
    /// camera is then held on the last frame (Core sends no <c>pipeline.state</c> for a normal stop,
    /// so this is where HOLD is set for a user-initiated stop).
    /// Gửi <c>core.stop</c> để kết thúc phiên hiện tại; Core coi lệnh này là idempotent. Camera ảo
    /// sau đó được giữ ở khung cuối (Core không gửi <c>pipeline.state</c> khi dừng bình thường, nên
    /// đây là chỗ bật HOLD cho lần dừng do người dùng).
    /// </summary>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once Core acknowledges the stop. Task hoàn tất khi Core xác nhận dừng.</returns>
    public async Task StopAsync(CancellationToken ct)
    {
        await _peer.CallAsync("core.stop", null, ct).ConfigureAwait(false);
        IsRunning = false;
        ActiveSource = null;
        _camera?.Hold();
    }

    /// <summary>
    /// Ensures the core <b>process</b> is running (spawning it when needed — this starts the
    /// process only, never the pipeline: <c>core.start</c> is sent exclusively by
    /// <see cref="StartAsync"/> on the user's command), wires the Core→App handlers once, then
    /// asks Core for the capturable monitors and windows (<c>core.list_sources</c>).
    /// Đảm bảo <b>tiến trình</b> core đang chạy (tự khởi tạo nếu cần — method này chỉ khởi động
    /// tiến trình, KHÔNG khởi động pipeline: <c>core.start</c> chỉ do <see cref="StartAsync"/> gửi
    /// khi người dùng bấm), gắn handler Core→App một lần, rồi hỏi Core về các màn hình và cửa sổ
    /// có thể capture (<c>core.list_sources</c>).
    /// </summary>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>The monitors and windows reported by Core. Các màn hình và cửa sổ do Core báo lại.</returns>
    public async Task<(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows)> ListSourcesAsync(
        CancellationToken ct)
    {
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();

        JsonNode? result = await _peer.CallAsync("core.list_sources", null, ct).ConfigureAwait(false);
        var monitors = new List<MonitorInfo>();
        var windows = new List<WindowInfo>();
        if (result?["monitors"] is JsonArray monitorArray)
        {
            foreach (JsonNode? node in monitorArray)
            {
                if (node is JsonObject monitor)
                {
                    monitors.Add(new MonitorInfo(
                        ReadInt(monitor, "id", -1),
                        ReadString(monitor, "name")));
                }
            }
        }
        if (result?["windows"] is JsonArray windowArray)
        {
            foreach (JsonNode? node in windowArray)
            {
                if (node is JsonObject window)
                {
                    windows.Add(new WindowInfo(
                        ReadString(window, "hwnd"),
                        ReadString(window, "title"),
                        ReadString(window, "process")));
                }
            }
        }
        return (monitors, windows);
    }

    private async Task EnsureCoreStartedAsync(CancellationToken ct)
    {
        if (!_peer.IsStarted) await _peer.StartCoreAsync(ct).ConfigureAwait(false);
    }

    private void EnsureHandlersWired()
    {
        if (_handlersWired) return;
        _peer.SetRequestHandler(HandleRequestAsync);
        _peer.NotificationReceived += HandleNotification;
        _peer.HandlerError += HandlePeerHandlerError;
        _peer.Exited += HandleCoreExited;
        _handlersWired = true;
    }

    private void HandlePeerHandlerError(Exception ex)
        => _log.LogWarning(ex, "Core->App message handler failed.");

    // Chỉ relay signal — không tự restart core; UI quyết định báo lỗi rồi người dùng bấm lại.
    private void HandleCoreExited(int exitCode)
    {
        _log.LogWarning("Core process exited with code {ExitCode}; the pipeline is gone.", exitCode);
        IsRunning = false;
        ActiveSource = null;
        PipelineStateChanged?.Invoke(false, CoreExitedReason);
    }

    private async Task<JsonNode?> HandleRequestAsync(string method, JsonNode? @params)
    {
        if (method != "translate.request_cache")
        {
            throw new InvalidOperationException($"Unsupported request from core: {method}");
        }
        if (@params is not JsonObject p)
        {
            throw new InvalidOperationException("translate.request_cache params must be an object.");
        }

        string context = ReadString(p, "context");
        string src = ReadString(p, "src");
        string dst = ReadString(p, "dst");
        var texts = new List<string>();
        if (p["texts"] is JsonArray textArray)
        {
            foreach (JsonNode? node in textArray)
            {
                if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                {
                    texts.Add(value.GetValue<string>());
                }
            }
        }

        using var timeoutCts = new CancellationTokenSource(CacheGateTimeout);
        try
        {
            await _cacheGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "translate.request_cache could not take the cache gate within {Timeout}; returning every text as a miss.",
                CacheGateTimeout);
            return AllMisses(texts);
        }

        try
        {
            CacheLookupResult result =
                await _cache.LookupAsync(context, src, dst, texts, timeoutCts.Token).ConfigureAwait(false);
            var hits = new JsonObject();
            foreach ((string text, string translated) in result.Hits) hits[text] = translated;
            var misses = new JsonArray();
            foreach (string text in result.Misses) misses.Add(text);
            return new JsonObject { ["hits"] = hits, ["misses"] = misses };
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "translate.request_cache gave up after {Timeout}; returning every text as a miss.",
                CacheGateTimeout);
            return AllMisses(texts);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static JsonNode AllMisses(IReadOnlyList<string> texts)
    {
        var misses = new JsonArray();
        foreach (string text in texts) misses.Add(text);
        return new JsonObject { ["hits"] = new JsonObject(), ["misses"] = misses };
    }

    private void HandleNotification(string method, JsonNode? @params)
    {
        switch (method)
        {
            case "frame.translated":
                HandleFrameTranslated(@params);
                break;
            case "frame.annotated":
                HandleFrameAnnotated(@params);
                break;
            case "pipeline.state":
                HandlePipelineState(@params);
                break;
            case "translate.put":
                HandleTranslatePut(@params);
                break;
        }
    }

    private void HandleFrameAnnotated(JsonNode? @params)
    {
        if (@params is not JsonObject p) return;
        if (p["jpeg"] is not JsonValue jpegValue || jpegValue.GetValueKind() != JsonValueKind.String)
        {
            return;
        }
        string? jpeg = jpegValue.GetValue<string>();
        if (string.IsNullOrEmpty(jpeg)) return;

        long seq = ReadLong(p, "seq");
        int w = ReadInt(p, "w");
        int h = ReadInt(p, "h");
        _preview?.Publish(new PreviewFrame(seq, w, h, jpeg));

        if (_camera is null) return;
        try
        {
            _camera.Submit(seq, w, h, Convert.FromBase64String(jpeg));
        }
        catch (Exception ex)
        {
            // The camera is a best-effort output: a bad payload must never kill the frame loop or
            // the preview that was just published. Camera là đầu ra best-effort: payload hỏng không
            // được giết vòng lặp khung hay preview vừa xuất bản.
            _log.LogWarning(ex, "Camera submit failed for frame {Seq}.", seq);
        }
    }

    private void HandleFrameTranslated(JsonNode? @params)
    {
        if (@params is not JsonObject p) return;
        var boxes = new List<OverlayBox>();
        if (p["boxes"] is JsonArray boxArray)
        {
            foreach (JsonNode? node in boxArray)
            {
                if (node is not JsonObject box) continue;
                string text = ReadString(box, "text");
                string translated = ReadString(box, "translated", text);
                boxes.Add(new OverlayBox(
                    ReadDouble(box, "x"),
                    ReadDouble(box, "y"),
                    ReadDouble(box, "w"),
                    ReadDouble(box, "h"),
                    text,
                    translated));
            }
        }
        _overlay.Publish(new OverlayFrame(
            ReadInt(p, "w"),
            ReadInt(p, "h"),
            DateTime.UtcNow,
            boxes));
    }

    private void HandlePipelineState(JsonNode? @params)
    {
        if (@params is not JsonObject p) return;
        bool running = p["running"] is JsonValue value && value.GetValueKind() == JsonValueKind.True;
        string? reason = p["reason"] is JsonValue reasonValue
            && reasonValue.GetValueKind() == JsonValueKind.String
            ? reasonValue.GetValue<string>()
            : null;
        IsRunning = running;
        PipelineStateChanged?.Invoke(running, reason);
        if (!running)
        {
            // The loop died on its own: keep the last frame on the camera instead of going black.
            // Loop tự chết: giữ khung cuối trên camera thay vì chuyển sang đen.
            _camera?.Hold();
        }
    }

    private void HandleTranslatePut(JsonNode? @params)
    {
        if (@params is not JsonObject p)
        {
            _log.LogWarning("translate.put ignored: params must be an object.");
            return;
        }

        string context = ReadString(p, "context");
        string src = ReadString(p, "src");
        string dst = ReadString(p, "dst");
        var entries = new List<(string Text, string Translated)>();
        if (p["entries"] is JsonArray entryArray)
        {
            foreach (JsonNode? node in entryArray)
            {
                if (node is not JsonObject entry) continue;
                string text = ReadString(entry, "text");
                if (text.Length == 0) continue;
                entries.Add((text, ReadString(entry, "translated")));
            }
        }

        _ = PutAsync(context, src, dst, entries);
    }

    // Best-effort: chờ gate cũng có timeout để SQLite kẹt không làm mọi lookup sau đó all-miss 30 s.
    private async Task PutAsync(
        string context, string src, string dst, IReadOnlyList<(string Text, string Translated)> entries)
    {
        using var timeoutCts = new CancellationTokenSource(CacheGateTimeout);
        try
        {
            await _cacheGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "translate.put skipped: cache gate still busy after {Timeout}.", CacheGateTimeout);
            return;
        }

        try
        {
            await _cache.PutAsync(context, src, dst, entries, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "translate.put gave up after {Timeout}; the cache write was skipped.", CacheGateTimeout);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "translate.put failed; the cache write was skipped.");
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static string ReadString(JsonObject o, string key, string fallback = "")
        => o[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>() ?? fallback
            : fallback;

    private static int ReadInt(JsonObject o, string key, int fallback = 0)
        => o[key] is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<int>(out int parsed)
            ? parsed
            : fallback;

    // Long (seq) đọc qua JSON literal: TryGetValue<long> chỉ sốc đúng kiểu backing — JsonNode
    // tự dựng (int) hay parse từ wire (JsonElement) đều phải đọc được.
    private static long ReadLong(JsonObject o, string key, long fallback = 0)
    {
        if (o[key] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
            return fallback;
        return long.TryParse(
            value.ToJsonString(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out long parsed)
            ? parsed
            : fallback;
    }

    private static double ReadDouble(JsonObject o, string key, double fallback = 0)
        => o[key] is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<double>(out double parsed)
            ? parsed
            : fallback;
}
