using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
/// <c>GET /events</c> answers with content type <c>text/event-stream</c>: it first sends a snapshot of the
/// latest frame (empty <c>boxes</c> when nothing was published yet), then one <c>data:</c> event per
/// <see cref="OverlayState.Changed"/> with payload
/// <c>{"seq":n,"boxes":[{"x":..,"y":..,"w":..,"h":..,"text":"..","translated":".."}]}</c>
/// (lowercase keys, 0–1 normalized coordinates), plus <c>:ping</c> comment heartbeats every
/// <c>heartbeat</c> (default 15 s) to keep the stream alive. There is no <c>translatedLang</c> field (spec C12).
/// </para>
/// <para>
/// <c>GET /events</c> trả content type <c>text/event-stream</c>: trước hết gửi snapshot của khung mới nhất
/// (<c>boxes</c> rỗng khi chưa có gì được xuất bản), rồi một sự kiện <c>data:</c> cho mỗi
/// <see cref="OverlayState.Changed"/> với payload như trên (key chữ thường, tọa độ chuẩn hóa 0–1), cộng chú thích
/// <c>:ping</c> mỗi <c>heartbeat</c> (mặc định 15 giây) để giữ luồng sống. Không có trường <c>translatedLang</c> (spec C12).
/// </para>
/// <para>
/// The listener binds <c>127.0.0.1</c> only; when the requested port is busy, binding falls back to
/// <c>port+1..port+10</c> and <see cref="Port"/> reflects the port actually bound. A client that disconnects
/// mid-stream only fails its own writes: the failed subscriber is removed and the server keeps serving.
/// </para>
/// <para>
/// Listener chỉ bind <c>127.0.0.1</c>; khi cổng yêu cầu bận, bind thử <c>port+1..port+10</c> và <see cref="Port"/>
/// phản ánh cổng thực sự bind. Client ngắt giữa luồng chỉ làm lệnh ghi của chính nó lỗi: subscriber lỗi bị gỡ khỏi
/// danh sách và server tiếp tục phục vụ.
/// </para>
/// </remarks>
public sealed class OverlayFeedServer : IAsyncDisposable
{
    private const string Host = "127.0.0.1";
    private const int MaxPortOffset = 10;
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(15);

    // Minimal placeholder page; task B2 replaces it with the real static overlay page.
    // Trang giữ chỗ tối thiểu; tác vụ B2 thay bằng trang overlay thật.
    private static readonly byte[] OverlayPageBytes = Encoding.UTF8.GetBytes(
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>ReTran Overlay</title>
        </head>
        <body>
          <!-- Task B2 serves the real overlay page here. / Tác vụ B2 phục vụ trang overlay thật tại đây. -->
        </body>
        </html>
        """);

    private readonly OverlayState _state;
    private readonly int _requestedPort;
    private readonly TimeSpan _heartbeat;
    private readonly object _gate = new();
    private readonly List<SseClient> _clients = [];

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _heartbeatLoop;
    private long _seq;
    private int _started;
    private int _disposed;

    /// <summary>
    /// The port actually bound after <see cref="StartAsync"/>; the requested port before that.
    /// Cổng thực sự được bind sau <see cref="StartAsync"/>; trước đó là cổng được yêu cầu.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Creates a server that broadcasts <paramref name="state"/> on loopback. When the port is busy,
    /// <see cref="StartAsync"/> falls back to <paramref name="port"/>+1..+10.
    /// Tạo server phát <paramref name="state"/> trên loopback. Khi cổng bận, <see cref="StartAsync"/> thử
    /// <paramref name="port"/>+1..+10.
    /// </summary>
    /// <param name="state">Overlay state to broadcast. / Trạng thái overlay cần phát.</param>
    /// <param name="port">Preferred loopback port. / Cổng loopback mong muốn.</param>
    /// <param name="heartbeat">Interval between SSE <c>:ping</c> comments; 15 s when null. / Chu kỳ giữa các chú thích <c>:ping</c>; 15 giây nếu null.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="heartbeat"/> is zero or negative.
    /// Ném ra khi <paramref name="heartbeat"/> bằng 0 hoặc âm.
    /// </exception>
    public OverlayFeedServer(OverlayState state, int port = 17863, TimeSpan? heartbeat = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _requestedPort = port;
        _heartbeat = heartbeat ?? DefaultHeartbeat;
        if (_heartbeat <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeat), _heartbeat,
                "Heartbeat must be positive. / Heartbeat phải dương.");
        }
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
    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("Server already started. / Server đã start rồi.");
        }

        HttpListenerException? failure = null;
        for (var offset = 0; offset <= MaxPortOffset; offset++)
        {
            try
            {
                _listener = CreateLoopbackListener(_requestedPort + offset);
                Port = _requestedPort + offset;
                failure = null;
                break;
            }
            catch (HttpListenerException ex)
            {
                // Port busy (typically error 32) — try the next candidate.
                // Cổng bận (thường là lỗi 32) — thử ứng viên kế tiếp.
                failure = ex;
            }
        }
        if (failure is not null)
        {
            _started = 0;
            throw new InvalidOperationException(
                $"No free loopback port in {_requestedPort}..{_requestedPort + MaxPortOffset}. " +
                "/ Không còn cổng loopback trống.",
                failure);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts.Token.Register(static l => { try { ((HttpListener?)l)?.Close(); } catch { } }, _listener);
        _state.Changed += OnStateChanged;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the listener and both loops, closes every SSE connection, and unsubscribes from
    /// <see cref="OverlayState.Changed"/>. Safe to call multiple times.
    /// Dừng listener và hai vòng lặp, đóng mọi kết nối SSE, và gỡ đăng ký khỏi
    /// <see cref="OverlayState.Changed"/>. Gọi nhiều lần vẫn an toàn.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _state.Changed -= OnStateChanged;
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
        foreach (var client in remaining)
        {
            client.Dispose();
        }
        cts?.Dispose();
    }

    private static HttpListener CreateLoopbackListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{Host}:{port}/");
        listener.Start();
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
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Listener stopped (dispose or lifetime token) — exit quietly.
                // Listener đã dừng (dispose hoặc token vòng đời) — thoát lặng lẽ.
                break;
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
                ServeOverlayPage(context);
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

    private static void ServeOverlayPage(HttpListenerContext context)
    {
        var response = context.Response;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = OverlayPageBytes.Length;
        response.OutputStream.Write(OverlayPageBytes, 0, OverlayPageBytes.Length);
        response.Close();
    }

    private void ServeEvents(HttpListenerContext context)
    {
        var response = context.Response;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;

        var client = new SseClient(response);
        var snapshot = ComposePayload(Interlocked.Read(ref _seq), _state.Latest);
        lock (_gate)
        {
            // Serialize the snapshot with broadcasts (they copy the client list under the same lock)
            // so the first event a client sees can never be older than its snapshot.
            // Ghi snapshot đồng bộ với broadcast (broadcast copy danh sách client dưới cùng một khóa)
            // để sự kiện đầu client nhận được không bao giờ cũ hơn snapshot của nó.
            if (!client.TryWrite(snapshot))
            {
                client.Dispose();
                return;
            }
            _clients.Add(client);
        }
        // The response stays open until a write fails (subscriber removed) or the server is disposed.
        // Response giữ mở cho đến khi một lệnh ghi lỗi (subscriber bị gỡ) hoặc server bị dispose.
    }

    private static void CloseWithStatus(HttpListenerResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentLength64 = 0;
        response.Close();
    }

    private void OnStateChanged()
    {
        var seq = Interlocked.Increment(ref _seq);
        Broadcast(ComposePayload(seq, _state.Latest));
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
            if (!client.TryWrite(payload))
            {
                lock (_gate)
                {
                    _clients.Remove(client);
                }
                client.Dispose();
            }
        }
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
    /// One connected <c>/events</c> subscriber: the response writer (guarded by <see cref="Gate"/>) and its cleanup.
    /// Một subscriber <c>/events</c> đang kết nối: writer của response (được <see cref="Gate"/> bảo vệ) và phần dọn dẹp.
    /// </summary>
    private sealed class SseClient : IDisposable
    {
        private readonly HttpListenerResponse _response;
        private readonly StreamWriter _writer;

        public SseClient(HttpListenerResponse response)
        {
            _response = response;
            _writer = new StreamWriter(response.OutputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>Guards the writer against concurrent event/heartbeat writes. / Bảo vệ writer khỏi các ghi event/heartbeat song song.</summary>
        public object Gate { get; } = new();

        /// <summary>
        /// Writes one SSE frame and flushes; <see langword="false"/> means the connection failed and the subscriber must be removed.
        /// Ghi một khung SSE rồi flush; <see langword="false"/> nghĩa là kết nối lỗi và phải gỡ subscriber này.
        /// </summary>
        public bool TryWrite(string payload)
        {
            lock (Gate)
            {
                try
                {
                    _writer.Write(payload);
                    _writer.Flush();
                    return true;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException)
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock (Gate)
            {
                try { _writer.Dispose(); } catch { }
                try { _response.Close(); } catch { }
            }
        }
    }
}
