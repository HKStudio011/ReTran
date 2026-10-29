using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReTran_App.Services;
using ReTran_App.Services.Preview;

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
/// One OCR model from the sidecar catalog (<c>ocr.models.list</c>): its name, slot, language
/// coverage, download size and current state.
/// Một model OCR trong catalog sidecar (<c>ocr.models.list</c>): tên, vị trí det/rec, phạm vi
/// ngôn ngữ, dung lượng tải và trạng thái hiện tại.
/// </summary>
/// <param name="Name">Catalog model name, e.g. <c>PP-OCRv6_small_det</c>. Tên model trong catalog, ví dụ <c>PP-OCRv6_small_det</c>.</param>
/// <param name="Kind">Model slot: <c>"det"</c> or <c>"rec"</c>. Vị trí model: <c>"det"</c> hoặc <c>"rec"</c>.</param>
/// <param name="Langs">Language codes the model covers (display hint). Các mã ngôn ngữ model hỗ trợ (gợi ý hiển thị).</param>
/// <param name="SizeMb">Published model size in megabytes. Dung lượng model công bố (MB).</param>
/// <param name="Installed">Whether the model files are present in the cache. File model có sẵn trong cache hay không.</param>
/// <param name="InUse">Whether the model is part of the sidecar's active pair. Model có nằm trong cặp đang dùng của sidecar hay không.</param>
public sealed record OcrModelInfo(
    string Name, string Kind, IReadOnlyList<string> Langs, double SizeMb, bool Installed, bool InUse);

/// <summary>
/// One OCR engine parameter from the sidecar schema (<c>ocr.config.schema</c>): its key, UI
/// group, wire type, schema default and the constraints the settings UI renders.
/// Một tham số engine OCR từ schema sidecar (<c>ocr.config.schema</c>): khóa, nhóm UI, kiểu
/// trên wire, giá trị mặc định theo schema và các ràng buộc mà UI cài đặt dựng.
/// </summary>
/// <param name="Key">PaddleOCR keyword, also the <c>ocr.config.set</c> key. Từ khóa PaddleOCR, cũng là khóa <c>ocr.config.set</c>.</param>
/// <param name="Group">UI group: <c>device</c>, <c>models</c>, <c>preprocess</c>, <c>det</c> or <c>rec</c>. Nhóm UI: <c>device</c>, <c>models</c>, <c>preprocess</c>, <c>det</c> hoặc <c>rec</c>.</param>
/// <param name="Type">Validation/rendering type: <c>bool</c>, <c>int</c>, <c>float</c>, <c>string</c>, <c>enum</c>, <c>json</c> or <c>shape</c>. Kiểu kiểm tra/hiển thị: <c>bool</c>, <c>int</c>, <c>float</c>, <c>string</c>, <c>enum</c>, <c>json</c> hoặc <c>shape</c>.</param>
/// <param name="Default">Schema default; <see langword="null"/> means the engine decides. Mặc định theo schema; <see langword="null"/> nghĩa là engine tự quyết.</param>
/// <param name="ReloadRequired">True for constructor options — changing them needs a model reload. True với tùy chọn constructor — đổi chúng cần nạp lại model.</param>
/// <param name="Values">Allowed strings for <c>enum</c>; empty for every other type. Chuỗi hợp lệ cho <c>enum</c>; rỗng với mọi kiểu khác.</param>
/// <param name="Min">Inclusive lower bound for numeric params, or <see langword="null"/>. Biên dưới (bao gồm) cho tham số số, hoặc <see langword="null"/>.</param>
/// <param name="Max">Inclusive upper bound for numeric params, or <see langword="null"/>. Biên trên (bao gồm) cho tham số số, hoặc <see langword="null"/>.</param>
/// <param name="Step">UI input step for numeric params, or <see langword="null"/>. Bước nhập liệu UI cho tham số số, hoặc <see langword="null"/>.</param>
/// <param name="Placeholder">UI hint shown while the field is empty, or <see langword="null"/>. Gợi ý hiển thị khi ô nhập trống, hoặc <see langword="null"/>.</param>
public sealed record OcrParamSpec(
    string Key, string Group, string Type, JsonNode? Default, bool ReloadRequired,
    IReadOnlyList<string> Values, double? Min, double? Max, double? Step, string? Placeholder);

/// <summary>
/// Outcome of one <c>ocr.config.set</c> (<see cref="PipelineController.SetOcrConfigAsync"/>):
/// which changed keys took effect immediately, which wait for the next model reload, and
/// whether at least one changed key is a constructor option.
/// Kết quả một lần <c>ocr.config.set</c> (<see cref="PipelineController.SetOcrConfigAsync"/>):
/// các khóa đổi được áp dụng ngay, các khóa chờ lần nạp model kế tiếp, và liệu có ít nhất một
/// khóa đổi là tùy chọn constructor hay không.
/// </summary>
/// <param name="Live">Changed keys applied immediately, in schema order. Các khóa đổi được áp ngay, theo thứ tự schema.</param>
/// <param name="Deferred">Changed constructor keys that wait for the next reload. Các khóa constructor đổi, chờ lần nạp kế tiếp.</param>
/// <param name="ReloadRequired">True when at least one changed key is a constructor option. True khi có ít nhất một khóa đổi là tùy chọn constructor.</param>
public sealed record OcrConfigSetResult(
    IReadOnlyList<string> Live, IReadOnlyList<string> Deferred, bool ReloadRequired);

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
    private readonly ILogger<PipelineController> _log;
    private readonly string _previewMapName;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    // Giữ handler trong bounds của read-loop: gate wait + truy cập DB tối đa 2 s (lookup và put).
    private static readonly TimeSpan CacheGateTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before retrying a preview section that Core has not created yet. Thời gian chờ trước khi thử lại vùng preview mà Core chưa tạo.</summary>
    private static readonly TimeSpan PreviewOpenRetry = TimeSpan.FromSeconds(1);

    private PreviewChannelReader? _previewReader;
    private DateTime _nextPreviewOpenUtc;
    // Guard chống phát lại: seq thông báo đã xử lý (reset mỗi phiên — Core restart từ 1), và seq
    // seqlock của vùng nhớ đã đọc (không reset — writer gieo bộ đếm từ vùng nhớ nên seq liên tục
    // giữa các thế hệ writer).
    private long _lastPreviewSeq;
    private long _lastMmfSeq;
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
    /// <param name="previewMapName">
    /// Shared-memory name of the preview channel Core writes; tests pass a unique name to stay
    /// isolated. Tên shared memory của channel preview mà Core ghi; test truyền tên riêng để không
    /// đụng nhau.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    public PipelineController(
        ICorePeer peer,
        AppSettingsService settings,
        TranslationCache cache,
        OverlayState overlay,
        ILogger<PipelineController> log,
        PreviewState? preview = null,
        string previewMapName = PreviewProtocol.MapName)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _preview = preview;
        _previewMapName = string.IsNullOrWhiteSpace(previewMapName) ? PreviewProtocol.MapName : previewMapName;
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
    /// Raised whenever <see cref="IsRunning"/> or <see cref="ActiveSource"/> changes, including a
    /// user-initiated start/stop (which does not raise <see cref="PipelineStateChanged"/>) and an
    /// unexpected pipeline stop. UI shells subscribe here to keep status badges in sync.
    /// Được phát mỗi khi <see cref="IsRunning"/> hoặc <see cref="ActiveSource"/> thay đổi, kể cả
    /// start/stop do người dùng bấm (không phát <see cref="PipelineStateChanged"/>) và khi pipeline
    /// dừng đột xuất. Vỏ UI đăng ký event này để giữ badge trạng thái đồng bộ.
    /// </summary>
    public event Action? StateChanged;

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
    /// and the given source: OCR/capture rhythm (<c>ocrFps</c>/<c>captureFps</c>) and the
    /// configured det/rec pair — which Core verifies against the sidecar catalog before any
    /// loop starts — plus the persisted engine overrides as <c>ocr.params</c> whenever any are
    /// set (Core drops unknown keys).
    /// Đảm bảo core đang chạy, gắn handler Core→App một lần, rồi gửi <c>core.start</c> dựng từ
    /// cài đặt đã lưu (qua <see cref="AppSettingsService.Load"/>) và nguồn đã cho: nhịp
    /// OCR/capture (<c>ocrFps</c>/<c>captureFps</c>) và cặp det/rec đã cấu hình — Core kiểm tra
    /// cặp này với catalog sidecar trước khi loop nào chạy — kèm các tham số engine đã lưu dưới
    /// dạng <c>ocr.params</c> khi có ghi đè (khóa lạ bị Core bỏ).
    /// </summary>
    /// <remarks>
    /// Starting a session clears the wire-seq guard before <c>core.start</c> is sent, because Core
    /// restarts its <c>frame.annotated</c> seq at 1 for every session. The shared-memory seqlock
    /// guard is not reset: the writer seeds its counter from the section, so the MMF seq stays
    /// continuous across writer generations.
    /// Mỗi lần start phiên sẽ xoá guard seq trên wire trước khi gửi <c>core.start</c>, vì Core
    /// khởi động lại seq <c>frame.annotated</c> từ 1 cho mỗi phiên. Guard seqlock của vùng nhớ
    /// không bị reset: writer gieo bộ đếm từ vùng nhớ nên seq MMF chạy liên tục giữa các thế hệ
    /// writer.
    /// </remarks>
    /// <param name="source">Capture source for the session. Nguồn capture cho phiên.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once Core acknowledges the start. Task hoàn tất khi Core xác nhận start.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null. Ném khi <paramref name="source"/> là null.</exception>
    /// <exception cref="RpcException">
    /// Propagated unchanged from Core for invalid params, unknown provider, a session already
    /// running, or a requested OCR model that is not installed (<c>ocr-model-missing</c> — see
    /// <see cref="IsOcrModelMissing"/>), so the UI can show the exact error.
    /// Được đẩy ra nguyên vẹn từ Core khi tham số sai, provider lạ, phiên đang chạy, hoặc model
    /// OCR yêu cầu chưa được cài (<c>ocr-model-missing</c> — xem <see cref="IsOcrModelMissing"/>),
    /// để UI hiển thị lỗi chính xác.
    /// </exception>
    public async Task StartAsync(SourceSelection source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();

        AppSettings settings = _settings.Load();
        // Chỉ gửi ocr.params khi có ghi đè: vắng mặt = sidecar giữ cấu hình hiện tại. Mỗi giá
        // trị được DeepClone để tách khỏi node settings trước khi gắn vào params (tránh lỗi
        // "node already has a parent" khi node bị dùng lại ở lần start sau).
        var ocr = new JsonObject { ["det"] = settings.OcrDet, ["rec"] = settings.OcrRec };
        JsonObject? ocrParams = BuildOcrParams(settings.OcrEngine ?? new Dictionary<string, JsonNode?>());
        if (ocrParams is not null) ocr["params"] = ocrParams;

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
            ["ocr"] = ocr,
            ["ocrFps"] = settings.OcrFps,
            ["captureFps"] = settings.CaptureFps,
        };

        // Cleared before core.start is sent (never after it returns) because Core restarts its
        // frame.annotated seq at 1 for every session: without this, the new session's first
        // notifications would look like replays. The shared-memory seqlock guard is NOT reset —
        // the writer seeds its counter from the section, so the MMF seq is continuous across
        // writer generations and an old-session frame stays rejected until a fresh write lands.
        // Xoá trước khi gửi core.start (không phải sau khi nó trả về) vì Core khởi động lại seq
        // frame.annotated từ 1 cho mỗi phiên: nếu không, những thông báo đầu của phiên mới sẽ bị
        // coi là phát lại. Guard seqlock của vùng nhớ KHÔNG reset — writer gieo bộ đếm từ vùng
        // nhớ nên seq MMF chạy liên tục giữa các thế hệ writer, khung của phiên cũ vẫn bị loại
        // cho tới khi có lần ghi mới.
        _lastPreviewSeq = 0;

        await _peer.CallAsync("core.start", @params, ct).ConfigureAwait(false);
        IsRunning = true;
        ActiveSource = source;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Sends <c>core.stop</c> to end the current session; Core treats it as idempotent (Core sends
    /// no <c>pipeline.state</c> for a normal stop).
    /// Gửi <c>core.stop</c> để kết thúc phiên hiện tại; Core coi lệnh này là idempotent (Core
    /// không gửi <c>pipeline.state</c> khi dừng bình thường).
    /// </summary>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once Core acknowledges the stop. Task hoàn tất khi Core xác nhận dừng.</returns>
    public async Task StopAsync(CancellationToken ct)
    {
        await _peer.CallAsync("core.stop", null, ct).ConfigureAwait(false);
        IsRunning = false;
        ActiveSource = null;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Sends <c>core.set_font_scale</c> so the preview annotation uses the new size
    /// immediately, without restarting the session. The setting is also persisted by the overlay
    /// service and sent again in the next <c>core.start</c>.
    /// Gửi <c>core.set_font_scale</c> để phần ghi chú preview dùng cỡ mới ngay, không cần
    /// khởi động lại phiên. Cài đặt cũng được overlay service lưu lại và gửi kèm <c>core.start</c>
    /// lần sau.
    /// </summary>
    /// <param name="fontScale">Font multiplier in (0, 4]. Hệ số cỡ chữ trong (0, 4].</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once Core accepts the value. Task hoàn tất khi Core nhận giá trị.</returns>
    /// <exception cref="RpcException">Propagated from Core when the value is rejected. Đẩy ra từ Core khi giá trị bị từ chối.</exception>
    public async Task SetAnnotationFontScaleAsync(double fontScale, CancellationToken ct)
    {
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        await _peer.CallAsync("core.set_font_scale",
            new JsonObject { ["fontScale"] = fontScale }, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Tells whether an exception thrown by <see cref="StartAsync"/> is Core's
    /// <c>ocr-model-missing</c> rejection — the requested det/rec pair is not installed. The UI
    /// uses this to guide the user to the settings sheet's OCR section instead of showing a raw
    /// error.
    /// Cho biết exception do <see cref="StartAsync"/> ném có phải lỗi <c>ocr-model-missing</c>
    /// của Core — cặp det/rec yêu cầu chưa được cài — hay không. UI dùng nó để dẫn người dùng
    /// tới mục OCR trong bảng cài đặt thay vì hiện lỗi thô.
    /// </summary>
    /// <param name="ex">The caught exception, or <see langword="null"/>. Exception vừa bắt, hoặc <see langword="null"/>.</param>
    /// <returns>True when Core rejected the start because the model pair is missing. True khi Core từ chối start vì thiếu cặp model.</returns>
    public static bool IsOcrModelMissing(Exception? ex) =>
        ex is RpcException && ex.Message.Contains("ocr-model-missing", StringComparison.Ordinal);

    /// <summary>
    /// Ensures the core process is running, then asks the sidecar (via Core's
    /// <c>ocr.models.list</c> proxy) for the model catalog with per-model installed/in-use
    /// flags. The active pair is the two entries with <see cref="OcrModelInfo.InUse"/> set.
    /// Đảm bảo tiến trình core đang chạy, rồi hỏi sidecar (qua proxy <c>ocr.models.list</c>
    /// của Core) lấy catalog model kèm cờ installed/in-use. Cặp đang dùng là hai mục có
    /// <see cref="OcrModelInfo.InUse"/> bật.
    /// </summary>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>The catalog entries in sidecar order. Các mục catalog theo thứ tự sidecar.</returns>
    public async Task<IReadOnlyList<OcrModelInfo>> ListOcrModelsAsync(CancellationToken ct)
    {
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();

        JsonNode? result = await _peer.CallAsync("ocr.models.list", null, ct).ConfigureAwait(false);
        var models = new List<OcrModelInfo>();
        if (result?["catalog"] is JsonArray catalog)
        {
            foreach (JsonNode? node in catalog)
            {
                if (node is not JsonObject model) continue;
                var langs = new List<string>();
                if (model["langs"] is JsonArray langArray)
                {
                    foreach (JsonNode? lang in langArray)
                    {
                        if (lang is JsonValue langValue
                            && langValue.GetValueKind() == JsonValueKind.String)
                        {
                            langs.Add(langValue.GetValue<string>());
                        }
                    }
                }
                models.Add(new OcrModelInfo(
                    ReadString(model, "name"),
                    ReadString(model, "kind"),
                    langs,
                    ReadDouble(model, "sizeMb"),
                    ReadBool(model, "installed"),
                    ReadBool(model, "inUse")));
            }
        }
        return models;
    }

    /// <summary>
    /// Starts downloading one model on the sidecar via <c>ocr.models.download</c>. The call
    /// returns as soon as the download is accepted — it continues on a background thread, so
    /// <see cref="ListOcrModelsAsync"/> keeps reporting the model as not installed until it
    /// finishes.
    /// Bắt đầu tải một model trên sidecar qua <c>ocr.models.download</c>. Lời gọi trả về ngay
    /// khi được chấp nhận — việc tải chạy nền, nên <see cref="ListOcrModelsAsync"/> vẫn báo
    /// model chưa cài cho tới khi tải xong.
    /// </summary>
    /// <param name="name">Catalog model name. Tên model trong catalog.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once the sidecar accepts the download. Task hoàn tất khi sidecar chấp nhận tải.</returns>
    /// <exception cref="RpcException">Propagated from Core when the model is unknown. Đẩy ra từ Core khi model không có trong catalog.</exception>
    public async Task DownloadOcrModelAsync(string name, CancellationToken ct)
    {
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();
        await _peer.CallAsync("ocr.models.download",
            new JsonObject { ["name"] = name }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes one model's files from the cache via <c>ocr.models.delete</c>. The sidecar
    /// refuses to delete a model of the active pair or one that is still downloading.
    /// Xoá file của một model khỏi cache qua <c>ocr.models.delete</c>. Sidecar từ chối xoá
    /// model thuộc cặp đang dùng hoặc đang tải.
    /// </summary>
    /// <param name="name">Catalog model name. Tên model trong catalog.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once the sidecar acknowledges the delete. Task hoàn tất khi sidecar xác nhận xoá.</returns>
    /// <exception cref="RpcException">Propagated from Core when the model is unknown, in use, or downloading. Đẩy ra từ Core khi model lạ, đang dùng, hoặc đang tải.</exception>
    public async Task DeleteOcrModelAsync(string name, CancellationToken ct)
    {
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();
        await _peer.CallAsync("ocr.models.delete",
            new JsonObject { ["name"] = name }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches the sidecar's active det/rec pair via <c>ocr.models.set</c>. The models do not
    /// need to be installed yet — a missing model surfaces as an <c>ocr-model-missing</c> start
    /// failure instead (see <see cref="IsOcrModelMissing"/>). Persist the chosen pair in
    /// <see cref="AppSettings"/> too, or the next <c>core.start</c> switches back.
    /// Chuyển cặp det/rec đang dùng của sidecar qua <c>ocr.models.set</c>. Model chưa cần cài —
    /// thiếu model sẽ lộ ra thành lỗi start <c>ocr-model-missing</c> (xem
    /// <see cref="IsOcrModelMissing"/>). Cần lưu cặp đã chọn vào <see cref="AppSettings"/> nữa,
    /// không thì lần <c>core.start</c> kế tiếp sẽ chuyển lại cặp cũ.
    /// </summary>
    /// <param name="det">Catalog det model name. Tên model det trong catalog.</param>
    /// <param name="rec">Catalog rec model name. Tên model rec trong catalog.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>A task that completes once the sidecar accepts the new pair. Task hoàn tất khi sidecar chấp nhận cặp mới.</returns>
    /// <exception cref="RpcException">Propagated from Core when a name is unknown or has the wrong kind. Đẩy ra từ Core khi tên lạ hoặc sai loại model.</exception>
    public async Task SetOcrModelPairAsync(string det, string rec, CancellationToken ct)
    {
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();
        await _peer.CallAsync("ocr.models.set",
            new JsonObject { ["det"] = det, ["rec"] = rec }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a new det/rec pair only while no pipeline session is running (spec C8): switching
    /// the sidecar's models mid-session would drop its cached predictor and cost a full model
    /// reload (worst measured 27.1 s), so the change is deferred to the next
    /// <see cref="StartAsync"/> instead. When <see cref="IsRunning"/> is true the call returns
    /// <see langword="false"/> without sending any RPC — callers persist the pair in settings
    /// (which the next <c>core.start</c> sends) and hint that it applies after Stop.
    /// Chỉ áp dụng cặp det/rec mới khi không có phiên pipeline đang chạy (spec C8): đổi model
    /// giữa phiên làm mất predictor đã nạp của sidecar và tốn một lần nạp lại cả model (đo
    /// được tối đa 27,1 s), nên thay đổi được hoãn tới <see cref="StartAsync"/> kế tiếp. Khi
    /// <see cref="IsRunning"/> là true, lời gọi trả <see langword="false"/> và không gửi RPC
    /// nào — bên gọi lưu cặp vào settings (lần <c>core.start</c> kế sẽ gửi nó) và gợi ý rằng
    /// cặp mới áp dụng sau khi Dừng.
    /// </summary>
    /// <param name="det">Catalog det model name. Tên model det trong catalog.</param>
    /// <param name="rec">Catalog rec model name. Tên model rec trong catalog.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>
    /// True when the pipeline was idle and <c>ocr.models.set</c> was sent; false when a session
    /// is running and the switch was skipped (no RPC went out).
    /// True khi pipeline đang idle và đã gửi <c>ocr.models.set</c>; false khi có phiên đang
    /// chạy và lượt chuyển bị bỏ qua (không RPC nào đi).
    /// </returns>
    /// <exception cref="RpcException">Propagated from Core when a name is unknown or has the wrong kind. Đẩy ra từ Core khi tên lạ hoặc sai loại model.</exception>
    public async Task<bool> ApplyOcrPairIfIdleAsync(string det, string rec, CancellationToken ct)
    {
        if (IsRunning)
        {
            return false;
        }
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();
        await _peer.CallAsync("ocr.models.set",
            new JsonObject { ["det"] = det, ["rec"] = rec }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Ensures the core process is running, then asks the sidecar (through Core's
    /// <c>ocr.config.schema</c> proxy) for the full engine-parameter schema the settings UI
    /// renders. Missing optional fields come back as empty/null instead of throwing.
    /// Đảm bảo tiến trình core đang chạy, rồi hỏi sidecar (qua proxy <c>ocr.config.schema</c>
    /// của Core) toàn bộ schema tham số engine mà UI cài đặt dựng. Trường tùy chọn thiếu trả về
    /// rỗng/null thay vì ném.
    /// </summary>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>The schema entries in sidecar order. Các mục schema theo thứ tự sidecar.</returns>
    /// <exception cref="RpcException">Propagated unchanged from Core when the sidecar is unavailable (-32603). Đẩy ra nguyên vẹn từ Core khi sidecar không sẵn sàng (-32603).</exception>
    public async Task<IReadOnlyList<OcrParamSpec>> GetOcrConfigSchemaAsync(CancellationToken ct)
    {
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();

        JsonNode? result = await _peer.CallAsync("ocr.config.schema", null, ct).ConfigureAwait(false);
        var specs = new List<OcrParamSpec>();
        if (result?["params"] is JsonArray paramArray)
        {
            foreach (JsonNode? node in paramArray)
            {
                if (node is not JsonObject param) continue;
                specs.Add(new OcrParamSpec(
                    ReadString(param, "key"),
                    ReadString(param, "group"),
                    ReadString(param, "type"),
                    param["default"],
                    ReadBool(param, "reloadRequired"),
                    ReadStringArray(param["values"]),
                    ReadNullableDouble(param, "min"),
                    ReadNullableDouble(param, "max"),
                    ReadNullableDouble(param, "step"),
                    ReadNullableString(param, "placeholder")));
            }
        }
        return specs;
    }

    /// <summary>
    /// Ensures the core process is running, then replaces the sidecar's engine override map
    /// through Core's <c>ocr.config.set</c> proxy. The map is the whole desired override map
    /// (replace semantics): keys absent from it fall back to their schema default, and an
    /// empty map restores every default. Values are deep-cloned onto the wire, so the caller's
    /// nodes can be reused across calls. <c>deferReload</c> is not sent — Core forces it from
    /// its run state.
    /// Đảm bảo tiến trình core đang chạy, rồi thay thế map ghi đè tham số engine của sidecar
    /// qua proxy <c>ocr.config.set</c> của Core. Map là toàn bộ map ghi đè mong muốn (ngữ nghĩa
    /// thay thế): khóa vắng mặt quay về mặc định schema, và map rỗng khôi phục mọi mặc định.
    /// Giá trị được deep-clone lên wire, nên node của bên gọi dùng lại được qua nhiều lần gọi.
    /// Không gửi <c>deferReload</c> — Core tự ép theo trạng thái chạy của nó.
    /// </summary>
    /// <param name="values">Override map: param key → JSON value; <see langword="null"/> means "use the default". Map ghi đè: khóa tham số → giá trị JSON; <see langword="null"/> nghĩa là dùng mặc định.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>Which keys applied live, which were deferred, and whether a reload is required. Các khóa áp ngay, các khóa bị hoãn, và liệu có cần nạp lại model hay không.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> is null. Ném khi <paramref name="values"/> là null.</exception>
    /// <exception cref="RpcException">Propagated unchanged from Core when the sidecar is unavailable (-32603) or rejects a value (-32602). Đẩy ra nguyên vẹn từ Core khi sidecar không sẵn sàng (-32603) hoặc từ chối giá trị (-32602).</exception>
    public async Task<OcrConfigSetResult> SetOcrConfigAsync(
        IReadOnlyDictionary<string, JsonNode?> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        await EnsureCoreStartedAsync(ct).ConfigureAwait(false);
        EnsureHandlersWired();

        // DeepClone từng giá trị: node của caller (settings/UI) không bao giờ bị đổi parent,
        // nên cùng map dùng lại được cho lần gọi sau.
        var overrides = new JsonObject();
        foreach ((string key, JsonNode? value) in values) overrides[key] = value?.DeepClone();

        JsonNode? result = await _peer.CallAsync("ocr.config.set",
            new JsonObject { ["params"] = overrides }, ct).ConfigureAwait(false);

        return new OcrConfigSetResult(
            ReadStringArray(result?["live"]),
            ReadStringArray(result?["deferred"]),
            result is JsonObject resultObj && ReadBool(resultObj, "reloadRequired"));
    }

    /// <summary>
    /// Builds the <c>ocr.params</c> object for <c>core.start</c> from persisted overrides.
    /// Returns <see langword="null"/> when there is nothing to send (no overrides, or every
    /// value null), so callers omit the property entirely — Core then keeps the sidecar's
    /// current configuration. Each value is deep-cloned before being attached.
    /// Dựng object <c>ocr.params</c> cho <c>core.start</c> từ các ghi đè đã lưu. Trả
    /// <see langword="null"/> khi không có gì để gửi (không ghi đè, hoặc mọi giá trị đều null),
    /// để bên gọi bỏ hẳn thuộc tính — Core khi đó giữ cấu hình hiện tại của sidecar. Mỗi giá trị
    /// được deep-clone trước khi gắn vào object.
    /// </summary>
    /// <param name="overrides">Persisted param key → JSON value map. Map khóa tham số → giá trị JSON đã lưu.</param>
    /// <returns>The params object, or <see langword="null"/> when nothing should be sent. Object params, hoặc <see langword="null"/> khi không nên gửi gì.</returns>
    private static JsonObject? BuildOcrParams(Dictionary<string, JsonNode?> overrides)
    {
        if (overrides.Count == 0) return null;
        var o = new JsonObject();
        foreach ((string key, JsonNode? value) in overrides)
        {
            if (value is not null) o[key] = value.DeepClone(); // tách parent trước khi attach
        }
        return o.Count == 0 ? null : o;
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
        StateChanged?.Invoke();
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

    /// <summary>
    /// Turns one slim <c>frame.annotated {seq,w,h}</c> notification into a preview frame by
    /// reading the JPEG out of the preview shared memory. Notifications never throw: an absent
    /// section, a torn read or an out-of-order seq simply drops the frame.
    /// Biến một thông báo gọn <c>frame.annotated {seq,w,h}</c> thành khung preview bằng cách đọc
    /// JPEG từ shared memory preview. Thông báo không bao giờ ném: thiếu vùng nhớ, đọc bị xé, hay
    /// seq sai thứ tự đều chỉ bỏ khung đó.
    /// </summary>
    private void HandleFrameAnnotated(JsonNode? @params)
    {
        if (@params is not JsonObject p) return;

        long seq = ReadLong(p, "seq");
        // Core may replay a notification for a frame the App already handled; a repeated or older
        // seq must not re-publish. Core có thể phát lại thông báo của khung App đã xử lý; seq lặp
        // hoặc cũ hơn không được xuất bản lại.
        if (seq <= _lastPreviewSeq) return;

        // No preview consumer (test-only setups): skip the shared-memory read entirely — nothing
        // consumes it.
        // Không có preview (chỉ gặp trong cấu hình test): bỏ hẳn bước đọc shared memory — không
        // có ai tiêu thụ.
        if (_preview is null) return;

        PreviewChannelReader? reader = EnsurePreviewChannel();
        if (reader is null || !reader.TryRead(out PreviewChannelFrame frame)) return;
        // Two notifications can describe the same write (Core notifies once per write, but the App
        // may drain them out of step); the seqlock counter tells them apart.
        // Hai thông báo có thể nói về cùng một lần ghi; bộ đếm seqlock giúp phân biệt.
        if (frame.Seq <= _lastMmfSeq) return;

        _lastMmfSeq = frame.Seq;
        _lastPreviewSeq = seq;

        // The bytes and the dimensions must come from the same snapshot, so both are taken from
        // the shared memory frame; only the seq (which restarts per session) comes from the wire.
        // Byte và kích thước phải đến từ cùng một ảnh chụp, nên cả hai lấy từ khung trong shared
        // memory; chỉ seq (khởi động lại mỗi phiên) lấy từ wire.
        _preview?.Publish(new PreviewFrame(
            seq, frame.Width, frame.Height, Convert.ToBase64String(frame.Jpeg)));
    }

    /// <summary>
    /// Returns the open preview reader, opening it at most once per second while Core has not
    /// created the section yet (Core may still be starting, or may have restarted).
    /// Trả reader preview đang mở, hoặc thử mở tối đa một lần mỗi giây khi Core chưa tạo vùng nhớ
    /// (Core có thể đang khởi động, hoặc đã khởi động lại).
    /// </summary>
    /// <returns>The reader, or <see langword="null"/> when the section is not available yet. Reader, hoặc <see langword="null"/> khi vùng nhớ chưa sẵn sàng.</returns>
    private PreviewChannelReader? EnsurePreviewChannel()
    {
        PreviewChannelReader? reader = _previewReader;
        if (reader is not null) return reader;

        if (DateTime.UtcNow < _nextPreviewOpenUtc) return null;

        if (!PreviewChannelReader.TryOpen(_previewMapName, out reader) || reader is null)
        {
            _nextPreviewOpenUtc = DateTime.UtcNow.Add(PreviewOpenRetry);
            return null;
        }

        _previewReader = reader;
        return reader;
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
        StateChanged?.Invoke();
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

    // Các trường tùy chọn của schema (placeholder, min/max/step): thiếu hoặc JSON null → null.
    private static string? ReadNullableString(JsonObject o, string key)
        => o[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static double? ReadNullableDouble(JsonObject o, string key)
        => o[key] is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<double>(out double parsed)
            ? parsed
            : null;

    // Mảng chuỗi (values của schema, live/deferred của set); thiếu hoặc không phải mảng → rỗng.
    private static List<string> ReadStringArray(JsonNode? node)
    {
        var items = new List<string>();
        if (node is not JsonArray array) return items;
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                items.Add(value.GetValue<string>());
            }
        }
        return items;
    }

    // installed/inUse đến từ wire dưới dạng JsonElement (parse từ chuỗi JSON) hoặc bool dựng sẵn
    // trong test — TryGetValue<bool> đọc được cả hai backing, không cần vòng giải mã như ReadLong.
    private static bool ReadBool(JsonObject o, string key, bool fallback = false)
        => o[key] is JsonValue value
            && value.TryGetValue<bool>(out bool parsed)
            ? parsed
            : fallback;
}
