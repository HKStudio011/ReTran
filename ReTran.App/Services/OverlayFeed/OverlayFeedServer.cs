using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ReTran_App.Core;

namespace ReTran_App.Services.OverlayFeed;

/// <summary>
/// Loopback-only HTTP feed for the OBS overlay: serves the overlay page at <c>GET /overlay</c> and streams
/// translated-box updates at <c>GET /events</c> using Server-Sent Events, so an OBS browser source can
/// draw the boxes over the captured game.
/// Máy chủ HTTP chỉ loopback cho overlay OBS: phục vụ trang overlay tại <c>GET /overlay</c> và phát các cập nhật
/// hộp dịch tại <c>GET /events</c> bằng Server-Sent Events, để browser source của OBS vẽ các hộp lên game đang quay.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /overlay</c> serves the transparent overlay page (<c>wwwroot/overlay-feed/overlay.html</c>) and
/// <c>GET /overlay/overlay.js</c> / <c>GET /overlay/overlay.css</c> serve its script and stylesheet with
/// <c>application/javascript</c> / <c>text/css</c> content types and <c>Cache-Control: no-store</c>, so OBS/CEF
/// never serves a stale page after an asset update. The asset folder is resolved beside the
/// running binaries first (MAUI copies wwwroot content into the app output, and it flows into the test
/// output via the project reference), then by walking parent directories for
/// <c>ReTran.App/wwwroot/overlay-feed</c>; a missing folder or file answers 404.
/// </para>
/// <para>
/// <c>GET /overlay</c> phục vụ trang overlay trong suốt (<c>wwwroot/overlay-feed/overlay.html</c>) và
/// <c>GET /overlay/overlay.js</c> / <c>GET /overlay/overlay.css</c> phục vụ script/stylesheet của trang với
/// content type <c>application/javascript</c> / <c>text/css</c> và <c>Cache-Control: no-store</c>, để OBS/CEF
/// không bao giờ phục vụ trang cũ sau khi asset cập nhật. Thư mục asset được phân giải cạnh binary đang
/// chạy trước (MAUI copy nội dung wwwroot vào output app, và nó chảy sang output test qua project reference),
/// rồi đi lên các thư mục cha tìm <c>ReTran.App/wwwroot/overlay-feed</c>; thiếu thư mục hoặc file thì trả 404.
/// </para>
/// <para>
/// <c>GET /events</c> answers with content type <c>text/event-stream</c>: it first queues a snapshot of the
/// latest frame (empty <c>boxes</c> when nothing was published yet, when the session was cleared with
/// <see cref="OverlayState.Clear"/>, or while the visibility gate hides the overlay), then one <c>data:</c>
/// event per <see cref="OverlayState.Changed"/> with payload
/// <c>{"seq":n,"boxes":[{"x":..,"y":..,"w":..,"h":..,"text":"..","translated":"..","paragraph":false}]}</c>
/// (lowercase keys, 0–1 normalized coordinates), plus <c>:ping</c> comment heartbeats every
/// <c>heartbeat</c> (default 15 s) to keep the stream alive. There is no <c>translatedLang</c> field (spec C12).
/// The snapshot is composed and registered while holding the same lock broadcasts copy the client list
/// under, so a publish that lands while a client connects can no longer be missed.
/// </para>
/// <para>
/// <c>GET /events</c> trả content type <c>text/event-stream</c>: trước hết đưa vào hàng một snapshot của khung
/// mới nhất (<c>boxes</c> rỗng khi chưa có gì được xuất bản, khi phiên bị <see cref="OverlayState.Clear"/>
/// xóa, hoặc khi gate hiển thị đang ẩn overlay), rồi một sự kiện <c>data:</c> cho mỗi
/// <see cref="OverlayState.Changed"/> với payload như trên (key chữ thường, tọa độ chuẩn hóa 0–1), cộng chú thích
/// <c>:ping</c> mỗi <c>heartbeat</c> (mặc định 15 giây) để giữ luồng sống. Không có trường <c>translatedLang</c>
/// (spec C12). Snapshot được soạn và đăng ký khi giữ cùng khóa mà broadcast copy danh sách client, nên một
/// publish rơi vào lúc client đang connect không còn có thể bị lỡ.
/// </para>
/// <para>
/// When an <see cref="IOverlayVisibility"/> is supplied (spec K8/K9) the feed follows the single
/// <c>OverlayOnGame</c> switch: while it hides the overlay, frame events are dropped at the gate (the
/// sequence counter does not even move) and each hidden edge broadcasts exactly one
/// <c>{"boxes":[]}</c> payload, which is how OBS clears its stage — no traffic is wasted while the
/// overlay is off. Turning the overlay back on sends the current snapshot immediately, and a client
/// that connects while hidden is served an empty one, so a finished session's text never reappears in
/// a Browser Source. Without a gate every frame is broadcast as before.
/// </para>
/// <para>
/// Khi có <see cref="IOverlayVisibility"/> (spec K8/K9), feed theo đúng công tắc <c>OverlayOnGame</c>
/// duy nhất: lúc công tắc ẩn overlay, event khung hình bị bỏ ngay tại gate (bộ đếm seq còn không chạy)
/// và mỗi edge ẩn phát đúng một payload <c>{"boxes":[]}</c>, đó là cách OBS xóa sân khấu — không tốn
/// traffic khi overlay đang tắt. Bật overlay lại gửi ngay snapshot hiện tại, và client kết nối lúc đang
/// ẩn nhận snapshot rỗng, nên chữ của phiên đã tắt không bao giờ hiện lại trong Browser Source. Không
/// có gate thì mọi khung hình vẫn được phát như trước.
/// </para>
/// <para>
/// Each subscriber owns a bounded queue drained by its own writer task, so publishes only ever enqueue and
/// a stalled client (full receive window) can never block the publisher thread — a full queue or a write
/// that misses its deadline drops that subscriber only. The listener binds <c>127.0.0.1</c> only; when the
/// requested port is busy (<c>HttpListenerException</c> error 32) binding falls back to <c>port+1..port+10</c>
/// and <see cref="Port"/> reflects the port actually bound; any other bind error propagates out of
/// <see cref="StartAsync"/>. <see cref="Started"/> reports whether a bind actually succeeded.
/// </para>
/// <para>
/// Mỗi subscriber có hàng đợi hữu hạn riêng được task writer riêng của nó xả, nên publish chỉ đưa vào hàng
/// và một client chết cứng (cửa sổ nhận đầy) không bao giờ chặn thread xuất bản — hàng đầy hoặc một lệnh ghi
/// quá deadline chỉ làm subscriber đó bị bỏ. Listener chỉ bind <c>127.0.0.1</c>; khi cổng yêu cầu bận
/// (<c>HttpListenerException</c> lỗi 32) bind thử <c>port+1..port+10</c> và <see cref="Port"/> phản ánh cổng
/// thực sự bind; lỗi bind khác ném thẳng ra khỏi <see cref="StartAsync"/>. <see cref="Started"/> cho biết
/// bind có thực sự thành công không.
/// </para>
/// </remarks>
public sealed class OverlayFeedServer : IAsyncDisposable
{
    private const string Host = "127.0.0.1";
    private const int MaxPortOffset = 10;
    private const int MaxPort = 65535;
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(15);

    // How long DisposeAsync waits for a client writer to stop before abandoning it.
    // DisposeAsync chờ writer của client dừng bao lâu trước khi bỏ nó.
    private static readonly TimeSpan WriterShutdownTimeout = TimeSpan.FromSeconds(2);

    // Pause before retrying the accept loop after an unexpected failure.
    // Nghỉ trước khi thử lại vòng accept sau một lỗi bất thường.
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    // Static overlay page assets, served from wwwroot/overlay-feed.
    // Asset tĩnh của trang overlay, phục vụ từ wwwroot/overlay-feed.
    private const string OverlayFolderName = "overlay-feed";

    // Resolved once per process: beside the binaries first, then up the repo tree.
    // Chỉ phân giải một lần mỗi tiến trình: cạnh binary trước, rồi đi lên cây thư mục.
    private static readonly Lazy<string?> OverlayFolderLazy = new(ResolveOverlayFolder);

    private readonly OverlayState _state;
    private readonly IOverlayVisibility? _visibility;
    private readonly int _requestedPort;
    private readonly TimeSpan _heartbeat;
    private readonly ILogger<OverlayFeedServer>? _logger;
    private readonly object _gate = new();
    private readonly List<SseClient> _clients = [];

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _heartbeatLoop;
    private long _seq;
    private int _startAttempted;
    private int _started;
    private int _disposed;

    // Visibility gate (spec K8/K9): the visibility the server has already told its clients about
    // (1 = hidden, so the stage it holds is empty). Written on each edge; read by the snapshot handed
    // to a client that connects. It is deliberately NOT what OnStateChanged checks: that handler reads
    // the live value, because a stop flips IsRunning before Clear() runs (PipelineController:297-300),
    // and gating on the announced value would broadcast the Clear() frame and then the edge's clear
    // payload — two empty payloads for one stop.
    // Gate hiển thị (spec K8/K9): độ hiển thị mà server đã báo cho client của nó (1 = ẩn, tức sân khấu
    // nó đang giữ là rỗng). Ghi ở mỗi edge; được đọc bởi snapshot trao cho client vừa kết nối. Cố tình
    // KHÔNG dùng nó cho OnStateChanged: handler đó đọc giá trị trực tiếp, vì một lần dừng lật IsRunning
    // TRƯỚC khi Clear() chạy (PipelineController:297-300), và gate theo giá trị đã-báo sẽ phát khung
    // Clear() rồi tới payload xóa của edge — hai payload rỗng cho một lần dừng.
    private int _hidden;

    /// <summary>
    /// The port actually bound after <see cref="StartAsync"/>; the requested port before that.
    /// Cổng thực sự được bind sau <see cref="StartAsync"/>; trước đó là cổng được yêu cầu.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// True only after <see cref="StartAsync"/> bound a port successfully; false before that
    /// and after a failed start.
    /// Chỉ đúng sau khi <see cref="StartAsync"/> bind cổng thành công; sai trước đó và sau
    /// một lần start thất bại.
    /// </summary>
    public bool Started => Volatile.Read(ref _started) == 1;

    /// <summary>
    /// Creates a server that broadcasts <paramref name="state"/> on loopback. When the port is busy,
    /// <see cref="StartAsync"/> falls back to <paramref name="port"/>+1..+10.
    /// Tạo server phát <paramref name="state"/> trên loopback. Khi cổng bận, <see cref="StartAsync"/> thử
    /// <paramref name="port"/>+1..+10.
    /// </summary>
    /// <param name="state">Overlay state to broadcast. / Trạng thái overlay cần phát.</param>
    /// <param name="port">
    /// Preferred loopback port; must be 1..65525 so the <c>+1..+10</c> fallback range stays valid.
    /// Cổng loopback mong muốn; phải nằm trong 1..65525 để dải fallback <c>+1..+10</c> còn hợp lệ.
    /// </param>
    /// <param name="heartbeat">Interval between SSE <c>:ping</c> comments; 15 s when null. / Chu kỳ giữa các chú thích <c>:ping</c>; 15 giây nếu null.</param>
    /// <param name="logger">Optional logger for start/accept failures. / Logger tùy chọn cho lỗi start/accept.</param>
    /// <param name="visibility">
    /// Optional visibility gate (spec K8/K9). When given, frames stop reaching subscribers while
    /// <see cref="IOverlayVisibility.ShouldShowObs"/> is false, and each hidden edge sends exactly one
    /// <c>{"boxes":[]}</c> payload so OBS clears its stage; a client that connects while hidden gets an
    /// empty snapshot. <see langword="null"/> means "always visible" — every frame is broadcast, which
    /// is the pre-gate behaviour.
    /// Gate hiển thị tùy chọn (spec K8/K9). Khi được truyền, khung hình ngừng tới subscriber lúc
    /// <see cref="IOverlayVisibility.ShouldShowObs"/> sai, và mỗi edge ẩn gửi đúng một payload
    /// <c>{"boxes":[]}</c> để OBS xóa sân khấu; client kết nối lúc đang ẩn nhận snapshot rỗng.
    /// <see langword="null"/> nghĩa là "luôn hiện" — mọi khung hình được phát, đúng hành vi trước gate.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="port"/> leaves no room for the fallback range (1..65525) or
    /// <paramref name="heartbeat"/> is zero or negative.
    /// Ném ra khi <paramref name="port"/> không còn chỗ cho dải fallback (1..65525) hoặc
    /// <paramref name="heartbeat"/> bằng 0 hoặc âm.
    /// </exception>
    public OverlayFeedServer(OverlayState state, int port = 17863, TimeSpan? heartbeat = null,
        ILogger<OverlayFeedServer>? logger = null, IOverlayVisibility? visibility = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _visibility = visibility;
        if (port is < 1 or > MaxPort - MaxPortOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port,
                $"Port must be 1..{MaxPort - MaxPortOffset} so port+{MaxPortOffset} stays within 1..{MaxPort}. " +
                $"/ Cổng phải nằm trong 1..{MaxPort - MaxPortOffset} để port+{MaxPortOffset} vẫn còn trong 1..{MaxPort}.");
        }
        _requestedPort = port;
        _heartbeat = heartbeat ?? DefaultHeartbeat;
        if (_heartbeat <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeat), _heartbeat,
                "Heartbeat must be positive. / Heartbeat phải dương.");
        }
        _logger = logger;
        Port = port;
    }

    /// <summary>
    /// Binds loopback and starts serving; returns once the listener is listening.
    /// Bind loopback rồi bắt đầu phục vụ; trả về khi listener đang lắng nghe.
    /// </summary>
    /// <param name="ct">
    /// Lifetime token: cancelling it stops the listener and both loops; call <see cref="DisposeAsync"/> for full teardown.
    /// Token vòng đời: hủy nó sẽ dừng listener và hai vòng lặp; gọi <see cref="DisposeAsync"/> để dọn dẹp hoàn toàn.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when already started or when every candidate port is busy.
    /// Ném ra khi đã start hoặc khi mọi cổng ứng viên đều bận.
    /// </exception>
    /// <exception cref="HttpListenerException">
    /// Thrown when binding fails with an error other than port-in-use (error 32) — only that case falls back.
    /// Ném ra khi bind lỗi với mã khác "cổng bận" (lỗi 32) — chỉ trường hợp đó mới fallback.
    /// </exception>
    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (Interlocked.Exchange(ref _startAttempted, 1) == 1)
        {
            throw new InvalidOperationException("Server already started. / Server đã start rồi.");
        }

        HttpListenerException? failure = null;
        try
        {
            for (var offset = 0; offset <= MaxPortOffset; offset++)
            {
                try
                {
                    _listener = CreateLoopbackListener(_requestedPort + offset);
                    Port = _requestedPort + offset;
                    failure = null; // bound — a busy port at a lower offset is not a failure
                    break;
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 32)
                {
                    // Port in use (ERROR_SHARING_VIOLATION) — try the next candidate port.
                    // Cổng bận (ERROR_SHARING_VIOLATION) — thử cổng ứng viên kế tiếp.
                    failure = ex;
                }
            }
        }
        catch
        {
            // Non-port-busy bind error: let it propagate, but keep the server restartable.
            // Lỗi bind không phải "cổng bận": ném thẳng, nhưng server vẫn start lại được.
            _startAttempted = 0;
            throw;
        }
        if (failure is not null)
        {
            _startAttempted = 0; // every candidate port busy — a retry once ports free up must be possible
            _logger?.LogError(failure,
                "Overlay feed failed to start: no free loopback port in {PortRange}.",
                $"{_requestedPort}..{_requestedPort + MaxPortOffset}");
            throw new InvalidOperationException(
                $"No free loopback port in {_requestedPort}..{_requestedPort + MaxPortOffset}. " +
                "/ Không còn cổng loopback trống.",
                failure);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts.Token.Register(static l => { try { ((HttpListener?)l)?.Close(); } catch { } }, _listener);
        _state.Changed += OnStateChanged;
        if (_visibility is not null)
        {
            // Seed from the current value so the first edge is measured against it: a server that
            // starts while the overlay is hidden must not treat the next "shown" event as no change.
            // Gieo từ giá trị hiện tại để edge đầu tiên được đo theo nó: server start lúc overlay đang
            // ẩn không được coi event "hiện" kế tiếp là không đổi.
            Volatile.Write(ref _hidden, _visibility.ShouldShowObs ? 0 : 1);
            _visibility.Changed += OnVisibilityChanged;
        }
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        Volatile.Write(ref _started, 1);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the listener and both loops, closes every SSE connection, and unsubscribes from
    /// <see cref="OverlayState.Changed"/> and from the visibility gate. Safe to call multiple times;
    /// never blocks indefinitely on a client whose write is stuck in the OS (its writer task is
    /// cancelled, bounded-waited, then abandoned).
    /// Dừng listener và hai vòng lặp, đóng mọi kết nối SSE, và gỡ đăng ký khỏi
    /// <see cref="OverlayState.Changed"/> cùng gate hiển thị. Gọi nhiều lần vẫn an toàn; không bao giờ
    /// chặn vô hạn trên client có lệnh ghi kẹt trong OS (task writer bị hủy, chờ có giới hạn, rồi bỏ).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _state.Changed -= OnStateChanged;
        if (_visibility is not null)
        {
            _visibility.Changed -= OnVisibilityChanged;
        }
        var cts = _cts;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }
        try { _listener?.Close(); } catch { }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        if (_heartbeatLoop is not null)
        {
            try { await _heartbeatLoop.ConfigureAwait(false); } catch { }
        }

        SseClient[] remaining;
        lock (_gate)
        {
            remaining = [.. _clients];
            _clients.Clear();
        }

        // Cancel every client writer first (SseClient.Dispose never joins its writer task) so no
        // teardown step below can end up blocked on a write stuck in the OS.
        // Hủy mọi writer của client trước (SseClient.Dispose không bao giờ join task writer của nó)
        // để không bước dọn dẹp nào dưới đây có thể bị chặn trên một lệnh ghi kẹt trong OS.
        foreach (var client in remaining)
        {
            client.Dispose();
        }

        // Bounded wait per writer: abandon any that is still stuck — it owns no server state.
        // Chờ có giới hạn cho từng writer: bỏ cái nào vẫn kẹt — nó không giữ state nào của server.
        foreach (var client in remaining)
        {
            await client.WaitExitedAsync(WriterShutdownTimeout).ConfigureAwait(false);
        }
        cts?.Dispose();
    }

    private static HttpListener CreateLoopbackListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{Host}:{port}/");
        try
        {
            listener.Start();
        }
        catch
        {
            // A failed Start can leave the half-added prefix wedged in http.sys until the
            // handles are closed (dotnet/runtime#61303) — close now so the next candidate
            // starts from clean state.
            // Start lỗi có thể để prefix nửa vời kẹt trong http.sys đến khi handle được đóng
            // (dotnet/runtime#61303) — đóng ngay để ứng viên kế tiếp khởi đầu sạch.
            listener.Close();
            throw;
        }
        return listener;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || ex is ObjectDisposedException)
                {
                    // Listener stopped (dispose or lifetime token) — exit quietly.
                    // Listener đã dừng (dispose hoặc token vòng đời) — thoát lặng lẽ.
                    break;
                }
                // Unexpected accept failure — log it and keep serving after a short pause
                // instead of silently killing the loop.
                // Lỗi accept bất thường — ghi log rồi tiếp tục phục vụ sau một khoảng nghỉ
                // thay vì âm thầm giết vòng lặp.
                _logger?.LogError(ex,
                    "Overlay feed accept loop failed; retrying.");
                try { await Task.Delay(AcceptRetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                continue;
            }
            _ = Task.Run(() => HandleContext(context));
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_heartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Broadcast(":ping\n\n");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HandleContext(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                CloseWithStatus(context.Response, 405);
                return;
            }
            var path = context.Request.Url?.AbsolutePath;
            if (path == "/overlay")
            {
                ServeAsset(context, "overlay.html", "text/html; charset=utf-8");
            }
            else if (path == "/overlay/overlay.js")
            {
                ServeAsset(context, "overlay.js", "application/javascript; charset=utf-8");
            }
            else if (path == "/overlay/overlay.css")
            {
                ServeAsset(context, "overlay.css", "text/css; charset=utf-8");
            }
            else if (path == "/events")
            {
                ServeEvents(context);
            }
            else
            {
                CloseWithStatus(context.Response, 404);
            }
        }
        catch
        {
            // Client aborted mid-response; nothing more to do for it.
            // Client ngắt giữa response; không còn gì phải làm cho client đó.
            try { context.Response.Close(); } catch { }
        }
    }

    private static void ServeAsset(HttpListenerContext context, string fileName, string contentType)
    {
        string? path = OverlayFolderLazy.Value is { } folder ? Path.Combine(folder, fileName) : null;
        if (path is null || !File.Exists(path))
        {
            CloseWithStatus(context.Response, 404);
            return;
        }

        // The assets are a few KB and cold — a synchronous read off the request task is fine.
        // Asset chỉ vài KB và lạnh — đọc đồng bộ trên task của request là đủ.
        var bytes = File.ReadAllBytes(path);
        var response = context.Response;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = contentType;
        // OBS/CEF must not serve a stale page after an asset update.
        // OBS/CEF không được phục vụ trang cũ sau khi asset cập nhật.
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    /// <summary>
    /// Locates <c>wwwroot/overlay-feed</c>: beside the running binaries first (MAUI copies
    /// wwwroot content into the app output, and it flows into the test output via the project
    /// reference), then by walking parent directories for <c>ReTran.App/wwwroot/overlay-feed</c>.
    /// <see langword="null"/> when no folder is found.
    /// Tìm <c>wwwroot/overlay-feed</c>: cạnh binary đang chạy trước (MAUI copy nội dung wwwroot
    /// vào output app, và nó chảy sang output test qua project reference), rồi đi lên các thư mục
    /// cha tìm <c>ReTran.App/wwwroot/overlay-feed</c>. Trả <see langword="null"/> khi không thấy.
    /// </summary>
    private static string? ResolveOverlayFolder()
    {
        string baseDir = AppContext.BaseDirectory;
        string local = Path.Combine(baseDir, "wwwroot", OverlayFolderName);
        if (Directory.Exists(local))
        {
            return local;
        }

        for (string? dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDir));
             dir is not null;
             dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "ReTran.App", "wwwroot", OverlayFolderName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private void ServeEvents(HttpListenerContext context)
    {
        var response = context.Response;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;

        var client = new SseClient(response.OutputStream, DropClient, abortConnection: response.Abort);
        lock (_gate)
        {
            // Compose the snapshot and register while holding the same lock broadcasts copy the
            // client list under, so a publish that lands between composing and registering can
            // no longer be missed — it either precedes the snapshot or reaches this client
            // through its queue. The snapshot itself goes through the queue like every frame.
            // Soạn snapshot và đăng ký khi giữ cùng khóa mà broadcast copy danh sách client,
            // nên một publish rơi vào giữa lúc soạn và lúc đăng ký không còn bị lỡ — hoặc nó trước
            // snapshot, hoặc đến client này qua hàng đợi. Bản thân snapshot đi qua hàng đợi như mọi khung.
            if (!client.TryEnqueue(ComposePayload(Interlocked.Read(ref _seq), HiddenNow() ? null : _state.Latest)))
            {
                client.Dispose();
                return;
            }
            _clients.Add(client);
        }
        // The response stays open until the writer drops it (failed/deadline/queue full) or the
        // server is disposed.
        // Response giữ mở cho đến khi writer bỏ nó (ghi lỗi/quá deadline/hàng đầy) hoặc server bị dispose.
    }

    /// <summary>
    /// Live gate read: true when a visibility gate was injected and says the overlay is hidden. While
    /// hidden nothing of the current session reaches subscribers, so both the connecting client's
    /// snapshot and every frame event must answer empty even though <see cref="OverlayState.Latest"/>
    /// may still hold a frame (Task 8 showed a <c>frame.translated</c> still queued on stdio can
    /// repopulate it after <see cref="OverlayState.Clear"/>).
    /// Đọc gate trực tiếp: đúng khi có gate hiển thị được bơm và nó báo overlay đang ẩn. Khi đang ẩn,
    /// không gì của phiên hiện tại tới subscriber, nên cả snapshot của client đang kết nối lẫn mọi event
    /// khung hình phải trả rỗng dù <see cref="OverlayState.Latest"/> vẫn có thể còn khung hình (Task 8
    /// cho thấy một <c>frame.translated</c> còn xếp hàng trên stdio có thể đổ lại nó sau
    /// <see cref="OverlayState.Clear"/>).
    /// </summary>
    private bool HiddenNow() => _visibility is not null && !_visibility.ShouldShowObs;

    private static void CloseWithStatus(HttpListenerResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentLength64 = 0;
        response.Close();
    }

    /// <summary>
    /// Handles one <see cref="OverlayState.Changed"/>: while the gate is hidden it returns before the
    /// sequence counter moves (no work, no traffic), otherwise it forwards the current frame.
    /// Xử lý một <see cref="OverlayState.Changed"/>: khi gate ẩn thì trả về trước khi bộ đếm seq chạy
    /// (không tốn work, không có traffic), ngược lại chuyển tiếp khung hình hiện tại.
    /// </summary>
    private void OnStateChanged()
    {
        if (HiddenNow()) return;
        var seq = Interlocked.Increment(ref _seq);
        Broadcast(ComposePayload(seq, _state.Latest));
    }

    /// <summary>
    /// Handles one visibility edge: the first (and only) payload the transition owes every connected
    /// client — an empty <c>boxes</c> payload when the overlay goes hidden (the spec K9 clear OBS
    /// needs), the current snapshot when it becomes visible again. Duplicate notifications change
    /// nothing, so one pipeline stop that raises two pipeline events still clears exactly once.
    /// Xử lý một edge hiển thị: payload đầu tiên (và duy nhất) mà transition nợ mọi client đang kết nối
    /// — payload <c>boxes</c> rỗng khi overlay chuyển sang ẩn (lệnh xóa spec K9 mà OBS cần), snapshot hiện
    /// tại khi nó hiện lại. Thông báo trùng lặp không đổi gì, nên một lần dừng pipeline nổ hai event
    /// pipeline vẫn xóa đúng một lần.
    /// </summary>
    private void OnVisibilityChanged()
    {
        bool hide = HiddenNow();
        int hideFlag = hide ? 1 : 0;
        if (Interlocked.Exchange(ref _hidden, hideFlag) == hideFlag) return; // no edge / không có edge

        var seq = Interlocked.Increment(ref _seq);
        Broadcast(ComposePayload(seq, hide ? null : _state.Latest));
    }

    private void Broadcast(string payload)
    {
        SseClient[] clients;
        lock (_gate)
        {
            clients = [.. _clients];
        }
        foreach (var client in clients)
        {
            // Enqueue-only: a full queue means a stalled subscriber — drop it, never block.
            // Chỉ đưa vào hàng: hàng đầy nghĩa là subscriber chết cứng — bỏ nó, không bao giờ chặn.
            if (!client.TryEnqueue(payload))
            {
                DropClient(client);
            }
        }
    }

    // Removes a dead or stalled subscriber and closes its connection; idempotent.
    // Gỡ subscriber chết hoặc chết cứng và đóng kết nối của nó; gọi nhiều lần vẫn an toàn.
    private void DropClient(SseClient client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
        }
        client.Dispose();
    }

    private static string ComposePayload(long seq, OverlayFrame? frame)
    {
        var boxes = new JsonArray();
        if (frame is not null)
        {
            foreach (var box in frame.Boxes)
            {
                boxes.Add(new JsonObject
                {
                    ["x"] = box.X,
                    ["y"] = box.Y,
                    ["w"] = box.W,
                    ["h"] = box.H,
                    ["text"] = box.Text,
                    ["translated"] = box.Translated,
                    ["paragraph"] = box.IsParagraph,
                });
            }
        }
        var payload = new JsonObject
        {
            ["seq"] = seq,
            ["boxes"] = boxes,
        };
        return "data: " + payload.ToJsonString() + "\n\n";
    }

    /// <summary>
    /// One connected <c>/events</c> subscriber: a bounded outbound queue drained by its own writer task.
    /// Publishers only enqueue — they can never block on a stalled client.
    /// Một subscriber <c>/events</c> đang kết nối: hàng đợi đầu ra hữu hạn được task writer riêng xả.
    /// Publisher chỉ đưa vào hàng — không bao giờ bị chặn bởi client chết cứng.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer task encodes and writes one frame at a time. A write that fails, or that does not
    /// complete within <c>writeDeadline</c> (5 s by default), marks the client dead: the connection is
    /// aborted (<paramref name="abortConnection"/>, falling back to closing the stream) and
    /// <paramref name="onDead"/> unregisters it. Aborting matters: closing an http.sys stream with a
    /// write stuck in the OS never delivers a FIN, so a merely-closed connection would linger until TCP
    /// gives up. A full queue makes <see cref="TryEnqueue"/> return <see langword="false"/> so the
    /// publisher drops the client instead of blocking.
    /// <see cref="Dispose"/> cancels the writer and aborts the connection but never joins the writer
    /// task — a write stuck in the OS cannot hang the publisher or server teardown; use
    /// <see cref="WaitExitedAsync"/> for a bounded join.
    /// </para>
    /// <para>
    /// Task writer mã hóa và ghi từng khung một. Lệnh ghi lỗi, hoặc không hoàn tất trong
    /// <c>writeDeadline</c> (mặc định 5 giây), coi client chết: hủy kết nối
    /// (<paramref name="abortConnection"/>, dự phòng là đóng stream) và <paramref name="onDead"/> gỡ
    /// nó khỏi danh sách. Hủy là điều quan trọng: đóng stream http.sys khi còn lệnh ghi kẹt trong OS
    /// thì không bao giờ gửi FIN, nên kết nối chỉ-đóng sẽ nán lại đến khi TCP bỏ cuộc. Hàng đầy làm
    /// <see cref="TryEnqueue"/> trả <see langword="false"/> để publisher bỏ client thay vì chặn.
    /// <see cref="Dispose"/> hủy writer và hủy kết nối nhưng không bao giờ join task writer — lệnh ghi
    /// kẹt trong OS không thể treo publisher hay việc dọn server; dùng <see cref="WaitExitedAsync"/>
    /// để join có giới hạn.
    /// </para>
    /// </remarks>
    internal sealed class SseClient : IDisposable
    {
        internal const int DefaultCapacity = 64;
        private static readonly TimeSpan DefaultWriteDeadline = TimeSpan.FromSeconds(5);

        private readonly Stream _stream;
        private readonly Action<SseClient>? _onDead;
        private readonly Action? _abortConnection;
        private readonly TimeSpan _writeDeadline;
        private readonly Channel<string> _queue;
        private readonly Task _writeLoop;
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;

        /// <summary>
        /// Creates a subscriber writing SSE frames to <paramref name="stream"/> on a private writer task.
        /// Tạo subscriber ghi các khung SSE vào <paramref name="stream"/> bằng task writer riêng.
        /// </summary>
        /// <param name="stream">Sink for SSE frames (e.g. the response output stream). / Nơi ghi các khung SSE (vd. output stream của response).</param>
        /// <param name="onDead">Invoked once when the writer loop stops (failure, deadline, dispose). / Được gọi một lần khi vòng writer dừng (lỗi, quá deadline, dispose).</param>
        /// <param name="writeDeadline">Max time one write may take; 5 s when null. / Thời gian tối đa cho một lệnh ghi; 5 giây nếu null.</param>
        /// <param name="capacity">Bounded queue size in frames; 64 when null. / Cỡ hàng đợi hữu hạn tính theo khung; 64 nếu null.</param>
        /// <param name="abortConnection">
        /// Hard-aborts the underlying connection (e.g. <see cref="HttpListenerResponse.Abort"/>); when null the
        /// stream is closed instead. / Hủy cứng kết nối phía dưới (vd.
        /// <see cref="HttpListenerResponse.Abort"/>); nếu null thì đóng stream thay thế.
        /// </param>
        internal SseClient(Stream stream, Action<SseClient>? onDead = null, TimeSpan? writeDeadline = null,
            int capacity = DefaultCapacity, Action? abortConnection = null)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                    "Queue capacity must be positive. / Dung lượng hàng đợi phải dương.");
            }
            _writeDeadline = writeDeadline ?? DefaultWriteDeadline;
            if (_writeDeadline <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(writeDeadline), _writeDeadline,
                    "Write deadline must be positive. / Deadline ghi phải dương.");
            }
            _onDead = onDead;
            _abortConnection = abortConnection;
            _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,   // only the writer task reads
                SingleWriter = false,  // broadcasts, heartbeats and the snapshot can race
                // Wait (default) keeps TryWrite returning false when full; publishers never block.
                // Wait (mặc định) giữ TryWrite trả false khi đầy; publisher không bao giờ chặn.
                FullMode = BoundedChannelFullMode.Wait,
            });
            _writeLoop = Task.Run(WriteLoopAsync);
        }

        /// <summary>Completes when the writer loop has stopped. / Hoàn tất khi vòng writer đã dừng.</summary>
        internal Task WriteLoop => _writeLoop;

        /// <summary>
        /// Enqueues one SSE frame; <see langword="false"/> when the queue is full or the client is
        /// disposed — the caller must drop the client. Never blocks.
        /// Đưa một khung SSE vào hàng; <see langword="false"/> khi hàng đầy hoặc client đã bị
        /// dispose — caller phải bỏ client này. Không bao giờ chặn.
        /// </summary>
        internal bool TryEnqueue(string payload) =>
            _disposed == 0 && _queue.Writer.TryWrite(payload);

        /// <summary>
        /// Waits at most <paramref name="timeout"/> for the writer loop to stop; <see langword="false"/>
        /// means it was abandoned (still stuck) — safe to ignore, the loop owns no server state.
        /// Chờ tối đa <paramref name="timeout"/> để vòng writer dừng; <see langword="false"/> nghĩa là
        /// bị bỏ (vẫn kẹt) — có thể bỏ qua, vòng này không giữ state nào của server.
        /// </summary>
        internal async Task<bool> WaitExitedAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_writeLoop, Task.Delay(timeout)).ConfigureAwait(false);
            return completed == _writeLoop;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }
            try { _cts.Cancel(); } catch { }
            _queue.Writer.TryComplete();
            AbortConnection();
            // Deliberately does not join the writer task: a write stuck in the OS must not hang
            // Broadcast or DisposeAsync.
            // Cố tình không join task writer: lệnh ghi kẹt trong OS không được treo Broadcast
            // hay DisposeAsync.
        }

        // Hard-abort when available, else close the stream. Closing an http.sys stream with a
        // write stuck in the OS never delivers a FIN — the connection would linger until TCP
        // gives up, so a dropped subscriber must be aborted instead.
        // Hủy cứng khi có thể, không thì đóng stream. Đóng stream http.sys khi còn lệnh ghi kẹt
        // trong OS thì không bao giờ gửi FIN — kết nối sẽ nán lại đến khi TCP bỏ cuộc, nên
        // subscriber bị bỏ phải hủy thay thế.
        private void AbortConnection()
        {
            try { _abortConnection?.Invoke(); } catch { }
            try { _stream.Close(); } catch { }
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                var alive = true;
                while (alive && await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var payload))
                    {
                        if (!await TryWriteFrameAsync(payload).ConfigureAwait(false))
                        {
                            alive = false;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Disposed, or the connection failed: either way the subscriber is gone —
                // abort the connection and report the client dead below.
                // Bị dispose, hoặc kết nối lỗi: subscriber đã đi — hủy kết nối và báo
                // client chết ở dưới.
            }
            finally
            {
                AbortConnection();
                _onDead?.Invoke(this);
            }
        }

        // Writes one frame under a deadline; false = the client must be dropped (write failed
        // or exceeded the deadline). The 4-arg byte[] overload keeps the write on the overlapped
        // IO path of real listener streams.
        // Ghi một khung theo deadline; false = phải bỏ client (ghi lỗi hoặc quá deadline).
        // Overload byte[] 4 tham số giữ lệnh ghi trên đường IO overlapped của stream listener thật.
        private async Task<bool> TryWriteFrameAsync(string payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            Task write = _stream.WriteAsync(bytes, 0, bytes.Length, _cts.Token);
            var timeout = Task.Delay(_writeDeadline, _cts.Token);
            var winner = await Task.WhenAny(write, timeout).ConfigureAwait(false);
            if (winner != write)
            {
                // Deadline exceeded — observe the late write (it may still fault when the stream
                // closes) and report the client dead; never wait for it.
                // Quá deadline — quan sát lệnh ghi muộn (nó vẫn có thể lỗi khi stream đóng) và báo
                // client chết; không bao giờ đợi nó.
                _ = write.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
                return false;
            }
            try
            {
                await write.ConfigureAwait(false);
                return true;
            }
            catch
            {
                // The write failed (reset, closed stream, http.sys error) — drop the subscriber.
                // Lệnh ghi lỗi (reset, stream đóng, lỗi http.sys) — bỏ subscriber.
                return false;
            }
        }
    }
}
