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
/// time in the background, pushes <c>pipeline.state {running, reason}</c> notifications when the
/// loop dies on its own (spec §3.3), and — when a frame annotator is configured — publishes each
/// annotated JPEG into the preview shared memory (<see cref="PreviewProtocol.MapName"/>) and emits
/// the slim <c>frame.annotated {seq,w,h}</c> notification that tells the App to read it.
/// Điều phối viên serve mode: cung cấp hợp đồng <c>core.start</c> / <c>core.stop</c> /
/// <c>core.list_sources</c> (spec §3.1), chạy nền mỗi phiên <see cref="OcrLoop"/> một lần tại một
/// thời điểm, đẩy thông báo <c>pipeline.state {running, reason}</c> khi loop tự chết (spec §3.3),
/// và — khi có bộ ghi chú khung — xuất bản từng JPEG đã ghi chú vào shared memory preview
/// (<see cref="PreviewProtocol.MapName"/>) rồi emit thông báo gọn <c>frame.annotated {seq,w,h}</c>
/// báo cho App đọc nó.
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
    private readonly IFrameAnnotator? _annotator;

    /// <summary>Annotation loop rate in fps (≤ capture fps). Tốc độ vòng ghi chú theo fps (≤ capture fps).</summary>
    private readonly int _annotatorFps;

    /// <summary>
    /// User font multiplier applied by the annotator (1.0 default); updated live by
    /// <see cref="SetFontScaleAsync"/> and read once per annotation tick.
    /// Hệ số cỡ chữ người dùng mà annotator áp dụng (mặc định 1.0); cập nhật trực tiếp bởi
    /// <see cref="SetFontScaleAsync"/> và đọc mỗi tick ghi chú.
    /// </summary>
    private double _annotationFontScale = 1.0;

    /// <summary>
    /// Shared-memory name of the preview channel; <see langword="null"/> means
    /// <see cref="PreviewProtocol.MapName"/>. Tên shared memory của channel preview;
    /// <see langword="null"/> nghĩa là <see cref="PreviewProtocol.MapName"/>.
    /// </summary>
    private readonly string? _previewMapName;

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
    /// <param name="annotator">
    /// Frame annotator emitting <c>frame.annotated</c> notifications; <see langword="null"/>
    /// disables the annotation loop (old tests keep passing). Bộ ghi chú khung emit thông báo
    /// <c>frame.annotated</c>; <see langword="null"/> tắt vòng ghi chú (test cũ không vỡ).
    /// </param>
    /// <param name="annotatorFps">Annotation rate in fps (1..60). Tốc độ ghi chú theo fps (1..60).</param>
    /// <param name="previewMapName">
    /// Shared-memory name of the preview channel; <see langword="null"/> uses
    /// <see cref="PreviewProtocol.MapName"/> (tests pass a unique name to stay isolated).
    /// Tên shared memory của channel preview; <see langword="null"/> dùng
    /// <see cref="PreviewProtocol.MapName"/> (test truyền tên riêng để không đụng nhau).
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when a non-nullable reference parameter is null. Ném khi tham số tham chiếu bắt buộc nào đó là null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="annotatorFps"/> is outside 1..60. Khi <paramref name="annotatorFps"/> ngoài khoảng 1..60.</exception>
    public PipelineHost(
        ISidecarRpc? rpc,
        TranslatorRegistry registry,
        JsonRpcServer server,
        TextWriter stderr,
        Func<OcrLoopOptions, ICaptureSource>? sourceFactory = null,
        IFrameAnnotator? annotator = null,
        int annotatorFps = 10,
        string? previewMapName = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(stderr);
        if (annotatorFps is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(annotatorFps), annotatorFps, "annotatorFps must be 1..60.");
        _rpc = rpc;
        _registry = registry;
        _server = server;
        _stderr = stderr;
        _sourceFactory = sourceFactory;
        _annotator = annotator;
        _annotatorFps = annotatorFps;
        _previewMapName = previewMapName;
    }

    /// <summary>
    /// Starts one pipeline session (<c>core.start</c>): validates params, applies the
    /// requested OCR engine params (schema-filtered) and switches the OCR model pair when
    /// <c>ocr:{det,rec}</c> is given, opens the capture source, launches the loop in the
    /// background, then fires one fire-and-forget warmup spot.
    /// Khởi động một phiên pipeline (<c>core.start</c>): kiểm tra tham số, áp tham số engine
    /// OCR yêu cầu (lọc theo schema) và chuyển cặp model OCR khi có <c>ocr:{det,rec}</c>, mở
    /// capture source, chạy loop ở chế độ nền, rồi bắn một lượt warmup spot kiểu fire-and-forget.
    /// </summary>
    /// <param name="@params">
    /// <c>{source:{kind:"screen",monitor}|"window",hwnd}, translate:{provider,src,dst},
    /// ocr?:{det,rec,params}, ocrFps?, captureFps?, fontScale?}</c> — missing fps values fall
    /// back to <see cref="CoreConfig"/> defaults; a missing <c>ocr</c> keeps the sidecar's
    /// current pair and engine config (no <c>ocr.models.*</c>/<c>ocr.config.set</c> call).
    /// Tham số theo hợp đồng spec §3.1 — fps thiếu thì lấy default từ
    /// <see cref="CoreConfig"/>; thiếu <c>ocr</c> thì giữ cặp model và cấu hình engine hiện
    /// tại của sidecar (không gọi <c>ocr.models.*</c>/<c>ocr.config.set</c>).
    /// </param>
    /// <param name="ct">Caller token; the session runs on its own token. Token của caller; phiên chạy bằng token riêng.</param>
    /// <returns><c>{"running": true}</c>. Kết quả <c>{"running": true}</c>.</returns>
    /// <exception cref="RpcMethodException">
    /// -32602 on malformed params or unknown provider; -32600 when a session is already running;
    /// -32603 when the sidecar is unavailable, the capture source cannot be opened, or a
    /// requested OCR model is not installed (<c>ocr-model-missing</c>).
    /// -32602 khi tham số sai hoặc provider lạ; -32600 khi phiên đang chạy;
    /// -32603 khi sidecar không sẵn sàng, không mở được capture source, hoặc model OCR
    /// yêu cầu chưa được cài (<c>ocr-model-missing</c>).
    /// </exception>
    public async Task<JsonNode?> StartAsync(JsonNode? @params, CancellationToken ct)
    {
        StartSpec spec = ParseStart(@params);

        // Cheap state gates first, in the same order as before (-32600 running, then
        // -32603 sidecar); the lock is then dropped because the ocr.models.* calls below
        // await sidecar I/O that must never hold the session lock.
        // Hai cửa trạng thái rẻ chạy trước, giữ đúng thứ tự cũ (-32600 đang chạy, rồi
        // -32603 sidecar); sau đó nhả lock vì các lời gọi ocr.models.* bên dưới phải
        // await I/O sidecar, không được giữ lock phiên.
        lock (_sync)
        {
            if (_session is not null)
                throw new RpcMethodException(RpcErrorCodes.InvalidRequest,
                    "pipeline already running (core.stop first).");
        }

        ISidecarRpc rpc = _rpc
            ?? throw new RpcMethodException(RpcErrorCodes.InternalError,
                "sidecar not available (python/venv missing or spawn failed).");

        // core.start ocr.params: filter the requested engine params against the sidecar
        // schema — unknown keys are dropped with a warning (a hand-edited settings.json must
        // not break Start) — and apply them before the model pair is switched, so the session
        // this core.start launches runs with exactly the requested configuration.
        // core.start ocr.params: lọc tham số engine yêu cầu theo schema sidecar — key lạ bị bỏ
        // kèm cảnh báo (settings.json sửa tay không được làm hỏng lần Start) — và áp chúng
        // trước khi chuyển cặp model, để phiên mà core.start này chạy đúng cấu hình yêu cầu.
        if (spec.OcrParams is JsonObject requested)
        {
            HashSet<string> allowed = await LoadOcrSchemaKeysAsync(rpc, ct);
            var filtered = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in requested)
            {
                if (allowed.Contains(kv.Key)) filtered[kv.Key] = kv.Value?.DeepClone();
                else _stderr.WriteLine($"warn: ocr.params: dropping unknown key '{kv.Key}'.");
            }
            await rpc.CallAsync("ocr.config.set",
                new JsonObject { ["params"] = filtered, ["deferReload"] = false }, ct);
        }

        // core.start ocr:{det,rec}: verify both models against the sidecar catalog and
        // switch the pair BEFORE any loop (or warmup) can touch the engine — the sidecar's
        // ocr.models.set deliberately skips the installed check.
        // core.start ocr:{det,rec}: kiểm tra cả hai model với catalog sidecar và chuyển
        // cặp TRƯỚC khi bất kỳ loop (hoặc warmup) nào chạm engine — ocr.models.set của
        // sidecar cố ý bỏ qua bước kiểm tra installed.
        if (spec.DetModel is string det && spec.RecModel is string rec)
            await EnsureOcrPairInstalledAsync(rpc, det, rec, ct);

        lock (_sync)
        {
            // Re-check: another core.start could have claimed the slot while the pair
            // RPC was in flight (the serve loop serializes requests; this guards direct
            // callers).
            // Kiểm tra lại: một core.start khác có thể đã chiếm slot khi RPC cặp model
            // đang bay (vòng serve tuần tự hoá request; phần này giữ cho caller trực
            // tiếp như test).
            if (_session is not null)
                throw new RpcMethodException(RpcErrorCodes.InvalidRequest,
                    "pipeline already running (core.stop first).");

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
            var snapshot = new AnnotationSnapshot();
            var sink = new TranslateCycleSink(context, spec.Src, spec.Dst, spec.Translator,
                new JsonRpcCacheChannel(_server), _server, _stderr, snapshot);

            var session = new Session(source, snapshot, trackAnnotatedFrames: _annotator is not null);
            _session = session;
            // Applied before either loop starts so the first annotated frame already uses the
            // App's font setting. Áp trước khi cả hai loop chạy để khung ghi chú đầu tiên đã
            // dùng đúng cỡ chữ của App.
            Volatile.Write(ref _annotationFontScale, spec.FontScale);

            // Subscribe the annotation handler BEFORE the OcrLoop starts (Task.Run below) so it
            // always runs first on FrameArrived and copies the frame into a pooled buffer before
            // OcrLoop can dispose the source frame. LatestFrameBuffer.Push (drop old) and
            // AnnotateLoopAsync (finally) both dispose the pooled frame, so buffers return to the
            // session pool exactly once.
            // Đăng handler ghi chú TRƯỚC khi OcrLoop chạy (Task.Run bên dưới) để nó luôn chạy đầu
            // trên FrameArrived và sao chép khung vào buffer từ pool trước khi OcrLoop có thể
            // dispose khung nguồn. LatestFrameBuffer.Push (drop cũ) và AnnotateLoopAsync (finally)
            // đều dispose frame từ pool nên buffer được trả về pool phiên đúng một lần.
            if (_annotator is not null)
            {
                IFrameAnnotator annotator = _annotator;
                // Created before the annotation loop starts; a creation failure only turns the
                // preview channel off (the loop then emits nothing), never the rest of the pipeline.
                // Tạo trước khi vòng ghi chú chạy; tạo lỗi chỉ tắt channel preview (loop không emit
                // gì), không ảnh hưởng phần còn lại của pipeline.
                session.PreviewWriter = TryCreatePreviewWriter();
                IPixelPool pixelPool = session.PixelPool!;
                EventHandler<FrameArrivedEventArgs> onAnnotFrame = (_, e) =>
                {
                    try
                    {
                        byte[] source = e.Frame.Pixels;
                        byte[] buffer = pixelPool.Rent(source.Length);
                        Array.Copy(source, buffer, source.Length);
                        session.AnnotBuffer!.Push(Frame.FromBgra(
                            e.Frame.Width, e.Frame.Height, buffer, pixelPool));
                    }
                    catch (Exception ex)
                    {
                        // Annotation is best-effort and must never break capture. Ghi chú là best-effort, không bao giờ được làm hỏng capture.
                        _stderr.WriteLine($"warn: annotation frame copy failed: {ex.Message}");
                    }
                };
                session.AnnotHandler = onAnnotFrame;
                source.FrameArrived += onAnnotFrame;
                CancellationToken annotToken = session.Cts.Token;
                session.AnnotLoop = Task.Run(() => AnnotateLoopAsync(session, annotator, annotToken));
            }

            session.Loop = Task.Run(() => RunSessionAsync(session, spec.ToOptions(), pipeline, sink));
        }

        // Fire-and-forget warmup AFTER the loops start (and after ocr.models.set when a
        // pair was provided): the first real frame then pays only inference time, not the
        // one-time model load. Never blocks core.start, never crashes the process.
        // Warmup fire-and-forget SAU khi các loop chạy (và sau ocr.models.set khi có
        // cặp model): khung thật đầu tiên khi đó chỉ trả thời gian suy luận, không phải
        // lần nạp model. Không bao giờ chặn core.start, không làm crash tiến trình.
        WarmUpSidecar(rpc);
        return new JsonObject { ["running"] = true };
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

            if (session.AnnotLoop is Task annotLoop)
            {
                // The annotation loop exits within one tick after cancellation; bound the wait so
                // a stuck notify cannot hang core.stop. Vòng ghi chú thoát trong một tick sau khi
                // hủy; giới chờ để notify kẹt không thể treo core.stop.
                try
                {
                    await annotLoop.WaitAsync(StopTimeout, ct);
                }
                catch (TimeoutException)
                {
                    _stderr.WriteLine($"warn: annotation loop did not stop within {StopTimeout.TotalSeconds:0}s.");
                }
                catch (OperationCanceledException)
                {
                    // caller cancelled the wait — the stop itself is best-effort. caller hủy lượt chờ — stop vẫn best-effort.
                }
                catch (Exception ex)
                {
                    _stderr.WriteLine($"warn: annotation loop stop failed: {ex.Message}");
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
    /// Updates the user font multiplier used for annotated frames (<c>core.set_font_scale</c>).
    /// It applies to the running session immediately (read on the next annotation tick) and to
    /// later sessions started without an explicit <c>fontScale</c>.
    /// Cập nhật hệ số cỡ chữ dùng cho khung ghi chú (<c>core.set_font_scale</c>). Áp dụng ngay
    /// cho phiên đang chạy (đọc ở tick ghi chú kế tiếp) và cho các phiên sau khởi động mà không
    /// truyền <c>fontScale</c> tường minh.
    /// </summary>
    /// <param name="params">Object <c>{"fontScale": number}</c> in (0, 4]. Object <c>{"fontScale": number}</c> trong (0, 4].</param>
    /// <param name="ct">Unused — the update is synchronous. Không dùng — cập nhật đồng bộ.</param>
    /// <returns><c>{"fontScale": number}</c> echoing the accepted value. Giá trị đã nhận, phản hồi lại.</returns>
    /// <exception cref="RpcMethodException">Thrown with -32602 when the params are invalid. Ném -32602 khi tham số sai.</exception>
    public Task<JsonNode?> SetFontScaleAsync(JsonNode? @params, CancellationToken ct)
    {
        if (@params is not JsonObject p
            || p["fontScale"] is not JsonValue value
            || !value.TryGetValue<double>(out double fontScale)
            || !double.IsFinite(fontScale)
            || fontScale is <= 0 or > 4)
        {
            throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                "'fontScale' must be a number in (0, 4].");
        }

        Volatile.Write(ref _annotationFontScale, fontScale);
        return Task.FromResult<JsonNode?>(new JsonObject { ["fontScale"] = fontScale });
    }

    /// <summary>
    /// Proxies <c>ocr.models.list</c> to the sidecar: the model catalog with per-model
    /// <c>installed</c>/<c>inUse</c> flags plus the active det/rec pair. The sidecar ignores
    /// the params; they are forwarded verbatim anyway.
    /// Proxy <c>ocr.models.list</c> sang sidecar: catalog model kèm cờ
    /// <c>installed</c>/<c>inUse</c> từng mục cùng cặp det/rec đang hoạt động. Sidecar
    /// không dùng params; chúng vẫn được chuyển tiếp nguyên văn.
    /// </summary>
    /// <param name="params">Ignored by the sidecar; forwarded verbatim. Sidecar không dùng; chuyển tiếp nguyên văn.</param>
    /// <param name="ct">Token for the sidecar call. Token cho lời gọi sidecar.</param>
    /// <returns>The sidecar's <c>{catalog, active}</c> reply, unchanged. Phản hồi <c>{catalog, active}</c> của sidecar, nguyên trạng.</returns>
    /// <exception cref="RpcMethodException">-32603 when the sidecar is unavailable. -32603 khi sidecar không sẵn sàng.</exception>
    public Task<JsonNode?> ListOcrModelsAsync(JsonNode? @params, CancellationToken ct) =>
        ForwardToSidecarAsync("ocr.models.list", @params, ct);

    /// <summary>
    /// Proxies <c>ocr.models.download {name}</c> to the sidecar: starts a background
    /// download of one catalog model (never blocks <c>ocr.spot</c>).
    /// Proxy <c>ocr.models.download {name}</c> sang sidecar: bắt đầu tải nền một model
    /// trong catalog (không bao giờ chặn <c>ocr.spot</c>).
    /// </summary>
    /// <param name="params">Object <c>{name}</c> with the catalog model name; validated by the sidecar. Object <c>{name}</c> chứa tên model trong catalog; sidecar kiểm tra.</param>
    /// <param name="ct">Token for the sidecar call. Token cho lời gọi sidecar.</param>
    /// <returns>The sidecar's reply, unchanged. Phản hồi của sidecar, nguyên trạng.</returns>
    /// <exception cref="RpcMethodException">-32603 when the sidecar is unavailable. -32603 khi sidecar không sẵn sàng.</exception>
    public Task<JsonNode?> DownloadOcrModelAsync(JsonNode? @params, CancellationToken ct) =>
        ForwardToSidecarAsync("ocr.models.download", @params, ct);

    /// <summary>
    /// Proxies <c>ocr.models.delete {name}</c> to the sidecar: removes one model's files
    /// from the cache (refused while the model is in use or downloading).
    /// Proxy <c>ocr.models.delete {name}</c> sang sidecar: xoá file của một model khỏi
    /// cache (từ chối khi model đang dùng hoặc đang tải).
    /// </summary>
    /// <param name="params">Object <c>{name}</c> with the catalog model name; validated by the sidecar. Object <c>{name}</c> chứa tên model trong catalog; sidecar kiểm tra.</param>
    /// <param name="ct">Token for the sidecar call. Token cho lời gọi sidecar.</param>
    /// <returns>The sidecar's reply, unchanged. Phản hồi của sidecar, nguyên trạng.</returns>
    /// <exception cref="RpcMethodException">-32603 when the sidecar is unavailable. -32603 khi sidecar không sẵn sàng.</exception>
    public Task<JsonNode?> DeleteOcrModelAsync(JsonNode? @params, CancellationToken ct) =>
        ForwardToSidecarAsync("ocr.models.delete", @params, ct);

    /// <summary>
    /// Proxies <c>ocr.models.set {det, rec}</c> to the sidecar: switches the active det/rec
    /// pair — the engine rebuilds lazily on the next <c>ocr.spot</c>. The sidecar does not
    /// check <c>installed</c> here; <see cref="StartAsync"/> performs that explicit check
    /// for <c>core.start</c>.
    /// Proxy <c>ocr.models.set {det, rec}</c> sang sidecar: chuyển cặp det/rec đang hoạt
    /// động — engine dựng lại lười ở lần <c>ocr.spot</c> kế tiếp. Sidecar không kiểm tra
    /// <c>installed</c> ở đây; <see cref="StartAsync"/> là nơi làm bước kiểm tra tường minh
    /// đó cho <c>core.start</c>.
    /// </summary>
    /// <param name="params">Object <c>{det, rec}</c> with the catalog names; validated by the sidecar. Object <c>{det, rec}</c> chứa hai tên trong catalog; sidecar kiểm tra.</param>
    /// <param name="ct">Token for the sidecar call. Token cho lời gọi sidecar.</param>
    /// <returns>The sidecar's reply, unchanged. Phản hồi của sidecar, nguyên trạng.</returns>
    /// <exception cref="RpcMethodException">-32603 when the sidecar is unavailable. -32603 khi sidecar không sẵn sàng.</exception>
    public Task<JsonNode?> SetOcrModelPairAsync(JsonNode? @params, CancellationToken ct) =>
        ForwardToSidecarAsync("ocr.models.set", @params, ct);

    /// <summary>
    /// Tells whether a pipeline session currently occupies the slot (evaluated under the
    /// session lock, so it cannot race the start/stop state toggles).
    /// Cho biết slot hiện có phiên pipeline đang chạy hay không (đánh giá dưới lock phiên,
    /// nên không thể đua với các lần bật/tắt trạng thái của start/stop).
    /// </summary>
    private bool IsRunning
    {
        get
        {
            lock (_sync) return _session is not null;
        }
    }

    /// <summary>
    /// Proxies <c>ocr.config.schema</c> to the sidecar: the full engine-parameter schema the
    /// Settings UI renders, unchanged.
    /// Proxy <c>ocr.config.schema</c> sang sidecar: toàn bộ schema tham số engine mà UI
    /// Settings dựng, nguyên trạng.
    /// </summary>
    /// <param name="ct">Token for the sidecar call. Token cho lời gọi sidecar.</param>
    /// <returns>The sidecar's <c>{params: [...]}</c> reply, unchanged. Phản hồi <c>{params: [...]}</c> của sidecar, nguyên trạng.</returns>
    /// <exception cref="RpcMethodException">-32603 when the sidecar is unavailable. -32603 khi sidecar không sẵn sàng.</exception>
    public Task<JsonNode?> OcrConfigSchemaAsync(CancellationToken ct) =>
        ForwardToSidecarAsync("ocr.config.schema", null, ct);

    /// <summary>
    /// Proxies <c>ocr.config.set</c> to the sidecar with <c>deferReload</c> forced from
    /// <see cref="IsRunning"/> — a caller-supplied value is ignored. Reload-required params
    /// must never reset the predictor mid-session, while an idle host applies them at once.
    /// The caller's params node is never mutated or re-parented.
    /// Proxy <c>ocr.config.set</c> sang sidecar với <c>deferReload</c> bị ép theo
    /// <see cref="IsRunning"/> — giá trị caller gửi bị bỏ qua. Tham số cần nạp lại không bao
    /// giờ reset predictor giữa phiên, còn host rảnh thì áp ngay. Node params của caller
    /// không bao giờ bị sửa hay đổi parent.
    /// </summary>
    /// <param name="params">Object <c>{params: {...}}</c> with the full override map; validated by the sidecar. Object <c>{params: {...}}</c> chứa toàn bộ map override; sidecar kiểm tra.</param>
    /// <param name="ct">Token for the sidecar call. Token cho lời gọi sidecar.</param>
    /// <returns>The sidecar's <c>{live, deferred, reloadRequired}</c> reply, unchanged. Phản hồi <c>{live, deferred, reloadRequired}</c> của sidecar, nguyên trạng.</returns>
    /// <exception cref="RpcMethodException">-32603 when the sidecar is unavailable. -32603 khi sidecar không sẵn sàng.</exception>
    public Task<JsonNode?> SetOcrConfigAsync(JsonNode? @params, CancellationToken ct)
    {
        // Clone before adding deferReload: the caller's (server's) node must never be mutated,
        // and each value is deep-cloned so nothing is re-parented into the forwarded object.
        // Clone trước khi thêm deferReload: node của caller (server) không bao giờ bị sửa, và
        // từng giá trị được deep-clone để không bị đổi parent sang object chuyển tiếp.
        JsonNode? forwarded = @params is JsonObject o
            ? new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())))
            {
                ["deferReload"] = IsRunning,
            }
            : @params;
        return ForwardToSidecarAsync("ocr.config.set", forwarded, ct);
    }

    /// <summary>
    /// Fetches <c>ocr.config.schema</c> once per call and returns the set of param keys it
    /// exposes; the reply must carry a <c>params</c> array.
    /// Lấy <c>ocr.config.schema</c> mỗi lần gọi một lần và trả tập key tham số mà nó phơi ra;
    /// phản hồi bắt buộc phải có mảng <c>params</c>.
    /// </summary>
    /// <param name="rpc">Sidecar peer. Peer sidecar.</param>
    /// <param name="ct">Caller token, honored on the sidecar call. Token của caller, được tôn trọng trên lời gọi sidecar.</param>
    /// <returns>Allowed keys, compared ordinal. Các key được phép, so sánh ordinal.</returns>
    /// <exception cref="RpcMethodException">-32603 when the schema reply has no <c>params</c> array. -32603 khi phản hồi schema không có mảng <c>params</c>.</exception>
    private static async Task<HashSet<string>> LoadOcrSchemaKeysAsync(ISidecarRpc rpc, CancellationToken ct)
    {
        JsonNode? schema = await rpc.CallAsync("ocr.config.schema", null, ct);
        if (schema?["params"] is not JsonArray paramsArray)
            throw new RpcMethodException(RpcErrorCodes.InternalError, "ocr.config.schema: missing 'params'.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? p in paramsArray)
            if (p?["key"]?.GetValue<string>() is { } key) keys.Add(key);
        return keys;
    }

    /// <summary>
    /// Shared body of the <c>ocr.models.*</c> and <c>ocr.config.*</c> proxies: forwards the
    /// method and params verbatim to the sidecar, or throws -32603 when it never spawned.
    /// Thân chung của các proxy <c>ocr.models.*</c> và <c>ocr.config.*</c>: chuyển tiếp method
    /// và params nguyên văn sang sidecar, hoặc ném -32603 khi sidecar không khởi tạo được.
    /// </summary>
    /// <exception cref="RpcMethodException">-32603 when <see cref="_rpc"/> is null. -32603 khi <see cref="_rpc"/> là null.</exception>
    private Task<JsonNode?> ForwardToSidecarAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        ISidecarRpc? rpc = _rpc;
        if (rpc is null)
            throw new RpcMethodException(RpcErrorCodes.InternalError,
                "sidecar not available (python/venv missing or spawn failed).");
        return rpc.CallAsync(method, @params, ct);
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
        // captureFps feeds the capture source via ToOptions (spec §3.1). It changes only the
        // capture cadence: the annotation loop keeps ticking at _annotatorFps, fixed from
        // CoreConfig.CaptureFps at serve start. Capped at 1..60 like ocrFps — a deliberate
        // tightening of §3.1's literal ">= 1" so a hand-crafted core.start cannot drive the
        // capture loop past the performance budget (the config path is capped indirectly by the
        // annotatorFps constructor check).
        // captureFps được cấp vào capture source qua ToOptions (spec §3.1). Nó chỉ đổi nhịp
        // capture: vòng ghi chú vẫn tick theo _annotatorFps, cố định từ CoreConfig.CaptureFps
        // lúc serve start. Giới hạn 1..60 giống ocrFps — siết cố ý so với chữ ">= 1" của §3.1
        // để một core.start tự tay gửi không thể đẩy loop capture vượt ngân sách hiệu năng
        // (đường config đã bị chặn gián tiếp bởi check 1..60 của annotatorFps).
        int captureFps = config.CaptureFps;
        if (p["captureFps"] is JsonValue captureValue
            && (!captureValue.TryGetValue<int>(out captureFps) || captureFps is < 1 or > 60))
        {
            throw new RpcMethodException(RpcErrorCodes.InvalidParams, "'captureFps' must be an integer 1..60.");
        }

        // User font multiplier for annotated frames (preview). The App
        // persists 0.85/1.0/1.25; (0, 4] guards against degenerate values.
        // Hệ số cỡ chữ cho khung ghi chú (preview). App lưu 0.85/1.0/1.25;
        // khoảng (0, 4] chặn giá trị vô lý.
        double fontScale = 1.0;
        if (p["fontScale"] is JsonValue fontValue
            && (!fontValue.TryGetValue<double>(out fontScale)
                || !double.IsFinite(fontScale)
                || fontScale is <= 0 or > 4))
        {
            throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                "'fontScale' must be a number in (0, 4].");
        }

        // Optional OCR model pair (ocr: {det, rec}) and engine params (ocr.params). Absent ->
        // the sidecar keeps its current pair and config, and core.start makes no
        // ocr.models.*/ocr.config.set call; present -> both names must be non-empty strings
        // (-32602 here) and StartAsync verifies them against the sidecar catalog before
        // starting any loop.
        // Cặp model OCR tùy chọn (ocr: {det, rec}) và tham số engine (ocr.params). Thiếu ->
        // sidecar giữ cặp và cấu hình hiện tại, core.start không gọi ocr.models.*/
        // ocr.config.set; có -> hai tên phải là chuỗi khác rỗng (-32602 ở đây), và StartAsync
        // kiểm tra với catalog sidecar trước khi chạy loop.
        string? detModel = null;
        string? recModel = null;
        JsonObject? ocrParams = null;
        if (p.TryGetPropertyValue("ocr", out JsonNode? ocrNode))
        {
            if (ocrNode is not JsonObject ocr)
                throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                    "'ocr' must be an object ({det, rec, params?}).");
            detModel = RequiredString(ocr, "ocr", "det");
            recModel = RequiredString(ocr, "ocr", "rec");
            // JSON null counts as absent: "keep the sidecar's current config" is the only
            // sane reading of an explicit null in a hand-edited settings file.
            // JSON null được tính là thiếu: "giữ cấu hình hiện tại của sidecar" là cách đọc
            // hợp lý duy nhất cho null tường minh trong file settings sửa tay.
            if (ocr.TryGetPropertyValue("params", out JsonNode? paramsNode) && paramsNode is not null)
            {
                ocrParams = paramsNode as JsonObject
                    ?? throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                        "'ocr.params' must be an object.");
            }
        }

        return new StartSpec(useWindow, monitor, hwnd, provider, src, dst, translator, ocrFps,
            captureFps, fontScale, detModel, recModel, ocrParams);
    }

    /// <summary>
    /// Verifies the requested det/rec pair against the sidecar catalog
    /// (<c>ocr.models.list</c>) and switches the sidecar to it (<c>ocr.models.set</c>)
    /// only when both models are installed — the explicit installed check the sidecar's
    /// own <c>ocr.models.set</c> deliberately skips.
    /// Kiểm tra cặp det/rec yêu cầu với catalog sidecar (<c>ocr.models.list</c>) và
    /// chuyển sidecar sang cặp đó (<c>ocr.models.set</c>) chỉ khi cả hai model đã cài —
    /// bước kiểm tra installed tường minh mà <c>ocr.models.set</c> của sidecar cố ý bỏ qua.
    /// </summary>
    /// <param name="rpc">Sidecar peer. Peer sidecar.</param>
    /// <param name="det">Requested det model name. Tên model det được yêu cầu.</param>
    /// <param name="rec">Requested rec model name. Tên model rec được yêu cầu.</param>
    /// <param name="ct">Caller token, honored on both sidecar calls. Token của caller, được tôn trọng trên cả hai lời gọi sidecar.</param>
    /// <exception cref="RpcMethodException">
    /// -32603 (<c>ocr-model-missing: {det}/{rec}</c>) when either name is absent from the
    /// catalog or reported as not installed — thrown before any loop starts.
    /// -32603 (<c>ocr-model-missing: {det}/{rec}</c>) khi một tên không có trong catalog
    /// hoặc được báo là chưa cài — ném trước khi loop nào chạy.
    /// </exception>
    private static async Task EnsureOcrPairInstalledAsync(
        ISidecarRpc rpc, string det, string rec, CancellationToken ct)
    {
        JsonNode? listed = await rpc.CallAsync("ocr.models.list", null, ct);
        JsonNode? catalog = listed?["catalog"];
        if (!IsModelInstalledInCatalog(catalog, det) || !IsModelInstalledInCatalog(catalog, rec))
            throw new RpcMethodException(RpcErrorCodes.InternalError, $"ocr-model-missing: {det}/{rec}");
        await rpc.CallAsync("ocr.models.set", new JsonObject { ["det"] = det, ["rec"] = rec }, ct);
    }

    /// <summary>
    /// Tells whether <paramref name="name"/> appears in the <c>ocr.models.list</c> catalog
    /// with <c>installed: true</c>. A missing entry counts as not installed.
    /// Cho biết <paramref name="name"/> có xuất hiện trong catalog của
    /// <c>ocr.models.list</c> với <c>installed: true</c> hay không. Thiếu mục được tính
    /// là chưa cài.
    /// </summary>
    /// <param name="catalog">The <c>catalog</c> array of the list reply (null-safe). Mảng <c>catalog</c> trong phản hồi list (an toàn với null).</param>
    /// <param name="name">Catalog model name to look up. Tên model trong catalog cần tra.</param>
    /// <returns>True when the model is listed and installed. True khi model có trong danh sách và đã cài.</returns>
    private static bool IsModelInstalledInCatalog(JsonNode? catalog, string name)
    {
        if (catalog is not JsonArray entries) return false;
        foreach (JsonNode? entry in entries)
        {
            if (entry is not JsonObject model) continue;
            if (model["name"] is not JsonValue nameValue
                || !nameValue.TryGetValue<string>(out string? entryName)
                || entryName != name) continue;
            return model["installed"] is JsonValue installed
                && installed.TryGetValue<bool>(out bool value) && value;
        }
        return false;
    }

    /// <summary>
    /// Tiny 32x8 white PNG (base64) used to warm the sidecar after <c>core.start</c>: the
    /// first real <c>ocr.spot</c> then pays only inference time, not the one-time paddle
    /// import + model load (~30s on cold start).
    /// PNG trắng 32x8 nhỏ (base64) dùng làm nóng sidecar sau <c>core.start</c>: khung
    /// <c>ocr.spot</c> thật đầu tiên khi đó chỉ tốn thời gian suy luận, không phải trả lần
    /// nạp paddle import + model (~30s khi lạnh).
    /// </summary>
    private const string WarmupImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAICAYAAACYhf2vAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAYSURBVDhPY/g/wIABXYDeYNQBow4YdQAAbID8LhqQghcAAAAASUVORK5CYII=";

    /// <summary>
    /// Fires one background <c>ocr.spot</c> with a tiny PNG right after the loops start
    /// (and after <c>ocr.models.set</c>, when a pair was provided) so the model load
    /// happens before the user's first frame. Fire-and-forget: never blocks
    /// <c>core.start</c>, and any failure is logged and swallowed — the first real
    /// request still works, it just takes longer.
    /// Gửi nền một <c>ocr.spot</c> với ảnh PNG nhỏ ngay sau khi các loop chạy (và sau
    /// <c>ocr.models.set</c>, khi có cặp model) để việc nạp model diễn ra trước khung
    /// đầu tiên của người dùng. Fire-and-forget: không bao giờ chặn <c>core.start</c>;
    /// mọi lỗi đều được ghi lại và nuốt — yêu cầu thật đầu tiên vẫn chạy được, chỉ là
    /// chậm hơn.
    /// </summary>
    /// <param name="rpc">Sidecar peer. Peer sidecar.</param>
    private void WarmUpSidecar(ISidecarRpc rpc) => _ = Task.Run(() => WarmUpSidecarAsync(rpc));

    /// <summary>
    /// Warmup body: one tiny spot with a generous timeout (the first call also carries
    /// the one-time model load); failures land in the warnings sink.
    /// Thân warmup: một spot nhỏ với timeout rộng (lời gọi đầu còn gánh lần nạp model);
    /// lỗi rơi vào nơi ghi cảnh báo.
    /// </summary>
    private async Task WarmUpSidecarAsync(ISidecarRpc rpc)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await rpc.CallAsync("ocr.spot", new JsonObject { ["image"] = WarmupImageBase64 }, cts.Token);
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: sidecar warmup failed: {ex.Message}");
        }
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
            // Cancel so the annotation loop also ends when the OcrLoop dies on its own (no stop).
            // Hủy để vòng ghi chú cũng kết thúc khi OcrLoop tự chết (không có stop).
            try
            {
                session.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // stop path already disposed the CTS — EndSession is a no-op slot clear. đường stop đã dispose CTS — EndSession chỉ dọn slot
            }
            EndSession(session);
        }
    }

    /// <summary>
    /// Annotation loop: at <see cref="_annotatorFps"/>, takes the latest cloned frame, renders it
    /// through <paramref name="annotator"/>, writes the JPEG into the preview shared memory and then
    /// pushes the slim <c>frame.annotated {seq,w,h}</c> (seq restarts at 1 each session). A frame
    /// whose shared-memory write failed is dropped — without it the App would read a stale frame.
    /// Ends with the session token; never throws.
    /// Vòng ghi chú: theo <see cref="_annotatorFps"/>, lấy khung clone mới nhất, vẽ qua
    /// <paramref name="annotator"/>, ghi JPEG vào shared memory preview rồi đẩy thông báo gọn
    /// <c>frame.annotated {seq,w,h}</c> (seq bắt đầu lại từ 1 mỗi phiên). Khung ghi shared memory
    /// lỗi sẽ bị bỏ — nếu không App sẽ đọc phải khung cũ. Kết thúc theo token phiên; không bao giờ ném.
    /// </summary>
    private async Task AnnotateLoopAsync(Session session, IFrameAnnotator annotator, CancellationToken token)
    {
        long seq = 1;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / _annotatorFps));
            while (await timer.WaitForNextTickAsync(token))
            {
                if (session.AnnotBuffer is null) continue;
                if (!session.AnnotBuffer.TryTakeLatest(out Frame? frame) || frame is null) continue;
                try
                {
                    AnnotatedFrame annotated = annotator.Annotate(
                        frame, session.Snapshot.TryGet(), Volatile.Read(ref _annotationFontScale));
                    if (!TryPublishPreview(session, annotated)) continue;
                    await _server.NotifyAsync("frame.annotated", new JsonObject
                    {
                        ["seq"] = seq,
                        ["w"] = annotated.Width,
                        ["h"] = annotated.Height,
                    });
                    seq++;
                }
                catch (Exception ex)
                {
                    // A failed notify/annotate drops this frame only (same policy as pipeline.state).
                    // Notify/annotate lỗi chỉ bỏ khung này (cùng policy pipeline.state).
                    _stderr.WriteLine($"warn: frame.annotated notify failed: {ex.Message}");
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop/session end. dừng bình thường / phiên kết thúc
        }
        finally
        {
            if (session.AnnotHandler is { } handler) session.Source.FrameArrived -= handler;
        }
    }

    /// <summary>
    /// Creates the session's preview writer, or returns <see langword="null"/> (with a warning) when
    /// the shared memory cannot be created. Preview is best-effort: the capture/OCR loop keeps
    /// running either way. Tạo writer preview của phiên, hoặc trả <see langword="null"/> (kèm cảnh
    /// báo) khi không tạo được shared memory. Preview là best-effort: loop capture/OCR vẫn chạy.
    /// </summary>
    private PreviewChannelWriter? TryCreatePreviewWriter()
    {
        try
        {
            return new PreviewChannelWriter(_previewMapName);
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: preview channel unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Publishes one annotated JPEG into the preview shared memory. Returns false when there is no
    /// channel or the write failed — in which case the caller drops the frame instead of notifying
    /// (a notification would make the App read a stale frame). Xuất bản một JPEG đã ghi chú vào
    /// shared memory preview. Trả false khi không có channel hoặc ghi lỗi — bên gọi bỏ khung thay
    /// vì notify (notify sẽ khiến App đọc phải khung cũ).
    /// </summary>
    /// <remarks>
    /// An oversize or otherwise failing write disables the channel for the rest of the session
    /// rather than retrying every tick; the loop itself never dies.
    /// Ghi quá cỡ hoặc lỗi sẽ tắt channel cho hết phiên thay vì thử lại mỗi tick; bản thân loop
    /// không bao giờ chết.
    /// </remarks>
    private bool TryPublishPreview(Session session, AnnotatedFrame annotated)
    {
        PreviewChannelWriter? writer = session.PreviewWriter;
        if (writer is null) return false;

        try
        {
            writer.Write(annotated.Width, annotated.Height, annotated.Jpeg);
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The session is winding down and EndSession already released the section: drop quietly.
            // Phiên đang kết thúc và EndSession đã giải phóng vùng nhớ: bỏ khung, không báo lỗi.
            return false;
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: preview channel write failed ({ex.Message}); disabling the preview channel.");
            session.PreviewWriter = null;
            try
            {
                writer.Dispose();
            }
            catch (Exception disposeEx)
            {
                _stderr.WriteLine($"warn: preview channel dispose failed: {disposeEx.Message}");
            }
            return false;
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
    /// Clears the session slot (only when it is still the current one), disposes the annotation
    /// frame buffer, the preview writer and the capture source once, and disposes the session token
    /// source. Xóa slot phiên (chỉ khi vẫn là phiên hiện tại), dispose buffer khung ghi chú, writer
    /// preview và capture source đúng một lần, và dispose token source của phiên.
    /// </summary>
    private void EndSession(Session session)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_session, session)) _session = null;
            session.Cts.Dispose(); // under the lock: serialized against StopAsync's Cancel
        }
        session.AnnotBuffer?.Dispose();

        // The writer's own lock serializes this against an in-flight annotate write, so tearing the
        // section down here cannot pull the mapping out from under it.
        // Lock của writer tuần tự hoá việc này với lần ghi đang chạy của vòng ghi chú, nên dỡ vùng
        // nhớ ở đây không thể rút mapping khỏi tay nó.
        PreviewChannelWriter? previewWriter = session.PreviewWriter;
        session.PreviewWriter = null;
        try
        {
            previewWriter?.Dispose();
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: preview channel dispose failed: {ex.Message}");
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
        int CaptureFps,
        double FontScale,
        string? DetModel,
        string? RecModel,
        JsonObject? OcrParams)
    {
        /// <summary>
        /// Builds the loop options; <see cref="StartSpec.CaptureFps"/> only cadences the capture
        /// source — the annotation loop keeps its serve-start <c>annotatorFps</c>.
        /// Tạo tùy chọn loop; <see cref="StartSpec.CaptureFps"/> chỉ nhịp capture source — vòng ghi
        /// chú giữ <c>annotatorFps</c> từ lúc serve start.
        /// </summary>
        public OcrLoopOptions ToOptions() => new(UseWindow, Monitor, Hwnd, OcrFps, 0, CaptureFps);
    }

    /// <summary>Per-start state: source, loop token, background task, and once-flags. Trạng thái mỗi lần start: source, token loop, task nền và cờ once.</summary>
    private sealed class Session
    {
        public Session(ICaptureSource source, AnnotationSnapshot snapshot, bool trackAnnotatedFrames)
        {
            Source = source;
            Cts = new CancellationTokenSource();
            Snapshot = snapshot;
            AnnotBuffer = trackAnnotatedFrames ? new LatestFrameBuffer() : null;
            PixelPool = trackAnnotatedFrames ? new BgraPool() : null;
        }

        public ICaptureSource Source { get; }
        public CancellationTokenSource Cts { get; }

        /// <summary>Latest box list published by the translation cycle. Dan sách box mới nhất do luồng dịch đăng.</summary>
        public AnnotationSnapshot Snapshot { get; }

        /// <summary>Cloned-frame buffer feeding the annotation loop; <see langword="null"/> when annotation is off. Buffer khung clone nuôi vòng ghi chú; <see langword="null"/> khi tắt ghi chú.</summary>
        public LatestFrameBuffer? AnnotBuffer { get; }

        /// <summary>Pool reusing the BGRA annotate buffers; <see langword="null"/> when annotation is off. Pool tái sử dụng buffer BGRA cho ghi chú; <see langword="null"/> khi tắt ghi chú.</summary>
        public IPixelPool? PixelPool { get; }

        /// <summary>
        /// Preview shared-memory writer feeding the App; <see langword="null"/> when annotation is
        /// off, creation failed, or the channel was disabled by a failed write.
        /// Writer shared memory preview cấp dữ liệu cho App; <see langword="null"/> khi tắt ghi chú,
        /// khi tạo lỗi, hoặc khi channel bị tắt do ghi lỗi.
        /// </summary>
        public PreviewChannelWriter? PreviewWriter { get; set; }

        /// <summary>Annotation loop task (null when annotation is off). Task vòng ghi chú (null khi tắt ghi chú).</summary>
        public Task? AnnotLoop { get; set; }

        /// <summary>FrameArrived handler to unsubscribe when the loop ends. Handler FrameArrived cần hủy đăng khi loop kết thúc.</summary>
        public EventHandler<FrameArrivedEventArgs>? AnnotHandler { get; set; }

        public Task? Loop { get; set; }
        public volatile bool StopRequested;
        public int StateNotified;
        public int SourceDisposed;
    }
}
