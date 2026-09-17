using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReTran_App.Core;

namespace ReTran_App.Services;

/// <summary>
/// Local HTTP + Server-Sent Events server exposing overlay frames to an OBS Browser Source.
/// Máy chủ HTTP cục bộ + Server-Sent Events phục vụ các khung overlay cho OBS Browser Source.
/// </summary>
/// <param name="state">The overlay state watched for new frames. / Trạng thái overlay được theo dõi để lấy khung hình mới.</param>
/// <param name="port">Preferred localhost port (default 17863); 0 picks a free port. / Cổng localhost mong muốn (mặc định 17863); 0 nghĩa là tự chọn cổng trống.</param>
public sealed class OverlayFeedServer(OverlayState state, int port = 17863) : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly OverlayState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly int _requestedPort = port < 0
        ? throw new ArgumentOutOfRangeException(nameof(port), "Port must be >= 0. / Cổng phải >= 0.")
        : port;
    private readonly List<Task> _connections = new();
    private readonly object _gate = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoop;
    private string _wwwroot = string.Empty;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// The actual localhost port the server listens on (differs from requested when busy ports were skipped, or 0 was passed).
    /// Cổng localhost thực tế máy chủ đang nghe (khác cổng yêu cầu khi phải bỏ qua cổng bận, hoặc khi truyền 0).
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Starts the listener (localhost only) and runs the accept loop on a background task tied to <paramref name="cancellationToken"/>.
    /// Port busy/access-denied retries with port+1, up to 10 tries, then throws.
    /// Khởi động listener (chỉ localhost) và chạy vòng lặp chấp nhận trên tác vụ nền gắn với <paramref name="cancellationToken"/>.
    /// Cổng bận/không có quyền thì thử cổng+1, tối đa 10 lần, sau đó ném lỗi.
    /// </summary>
    /// <param name="cancellationToken">Token stopping the accept loop. / Token dùng để dừng vòng lặp chấp nhận.</param>
    /// <returns>A task completing once the listener is accepting. / Tác vụ hoàn thành khi listener đã sẵn sàng nhận kết nối.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the server was already started or disposed, or no free port was found in 10 tries.
    /// Ném ra khi máy chủ đã khởi động hoặc đã hủy, hoặc không tìm được cổng trống sau 10 lần thử.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("Server already started. / Máy chủ đã khởi động rồi.");
            _started = true;
        }

        _wwwroot = ResolveWwwRoot();

        int candidate = _requestedPort == 0 ? GetFreePort() : _requestedPort;
        Exception? lastError = null;
        HttpListener? listener = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{candidate + attempt}/");
            try
            {
                l.Start();
                listener = l;
                Port = candidate + attempt;
                lastError = null;
                break;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
                l.Close();
            }
            catch (SocketException ex)
            {
                lastError = ex;
                l.Close();
            }
        }

        if (listener is null)
            throw new InvalidOperationException(
                $"No free localhost port in [{candidate}, {candidate + 9}]. / Không tìm được cổng localhost trống trong [{candidate}, {candidate + 9}].",
                lastError);

        _listener = listener;
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the listener and completes open streams.
    /// Dừng listener và kết thúc các luồng đang mở.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        HttpListener? listener;
        Task? acceptLoop;
        List<Task> connections;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            listener = _listener;
            acceptLoop = _acceptLoop;
            connections = new List<Task>(_connections);
        }

        try
        {
            _serverCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            listener?.Stop();
        }
        catch (ObjectDisposedException) { }
        listener?.Close();
        _serverCts?.Dispose();

        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
        if (connections.Count > 0)
        {
            try
            {
                await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break; // Listener stopped. / Listener đã dừng.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            Task connection = Task.Run(() => HandleRequestAsync(ctx, ct));
            lock (_gate) _connections.Add(connection);
            _ = connection.ContinueWith(
                _ => { lock (_gate) _connections.Remove(connection); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/events")
            {
                await HandleSseAsync(ctx, ct).ConfigureAwait(false);
                return;
            }
            if (path == "/overlay")
            {
                await ServeFileAsync(ctx, "overlay/overlay.html", "text/html; charset=utf-8", ct).ConfigureAwait(false);
                return;
            }
            if (path == "/overlay/overlay.js" || path == "/overlay.js")
            {
                // NOTE: the verbatim overlay.html sits at /overlay (no trailing slash),
                // so its relative "./overlay.js" resolves to /overlay.js per RFC 3986.
                // Serve the same file on both paths so the page works in a real browser/OBS.
                // LƯU Ý: overlay.html nguyên văn nằm ở /overlay (không có gạch chéo cuối),
                // nên "./overlay.js" tương đối sẽ trỏ tới /overlay.js theo RFC 3986.
                // Phục vụ cùng một file trên cả hai đường dẫn để trang chạy được trên trình duyệt/OBS thật.
                await ServeFileAsync(ctx, "overlay/overlay.js", "text/javascript; charset=utf-8", ct).ConfigureAwait(false);
                return;
            }
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception)
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private async Task ServeFileAsync(HttpListenerContext ctx, string relativePath, string contentType, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(_wwwroot, relativePath), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Missing/unreadable static file reads as 404. / File tĩnh thiếu/không đọc được thì trả 404.
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private async Task HandleSseAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var response = ctx.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.SendChunked = true;

        // All writes for this stream happen below on this task only; the Changed
        // handler merely signals. writeGate serializes event writes vs heartbeats.
        // Mọi lần ghi của luồng này chỉ diễn ra trên tác vụ này; handler Changed
        // chỉ phát tín hiệu. writeGate xâu chuỗi ghi sự kiện và heartbeat.
        var writeGate = new SemaphoreSlim(1, 1);
        var signal = new SemaphoreSlim(0, int.MaxValue);
        void OnChanged()
        {
            try { signal.Release(); } catch { }
        }
        // Subscribe BEFORE writing anything: a Publish landing between connect
        // and the subscribe would otherwise be missed forever. The snapshot below
        // closes that race (Latest is null when nothing was published yet, so the
        // first body line a fresh client reads is still the first "data:" event).
        // Đăng ký TRƯỚC khi ghi bất cứ gì: Publish rơi vào giữa kết nối và đăng ký
        // nếu không sẽ bị mất vĩnh viễn. Snapshot bên dưới khép kín race đó
        // (Latest null khi chưa có gì xuất bản, nên dòng body đầu client mới đọc
        // vẫn là sự kiện "data:" đầu tiên).
        _state.Changed += OnChanged;
        try
        {
            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            // HttpListener (http.sys) sends response headers only together with the
            // first body bytes — a bare flush never reaches the client. So emit an
            // UNTERMINATED "data: " prefix: headers go out, GetStreamAsync-style
            // clients return, yet their first ReadLine still completes only at the
            // first frame's newline. The concatenated line is exactly
            // "data: {json}", valid SSE for real EventSource clients too.
            // HttpListener (http.sys) chỉ gửi header kèm byte body đầu tiên —
            // flush trơn không bao giờ tới client. Vì vậy ghi tiền tố "data: "
            // CHƯA kết thúc dòng: header được đẩy, client kiểu GetStreamAsync trả
            // về, nhưng ReadLine đầu của chúng vẫn chỉ hoàn thành ở ký tự xuống
            // dòng của khung đầu tiên. Dòng ghép lại chính xác là "data: {json}",
            // SSE hợp lệ cho EventSource thật.
            try
            {
                await writer.WriteAsync("data: ".AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException)
            {
                return;
            }

            bool firstFrameSent = false;

            // Catch up with anything published before (or during) the subscribe by
            // completing the pending "data: " line (prefix already sent above).
            // Bắt kịp mọi khung đã xuất bản trước (hoặc trong lúc) đăng ký bằng
            // cách hoàn thành dòng "data: " đang treo (tiền tố đã gửi ở trên).
            var current = _state.Latest;
            if (current is not null)
            {
                await writeGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await writer.WriteAsync((SerializeFrame(current) + "\n\n").AsMemory(), ct).ConfigureAwait(false);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                    firstFrameSent = true;
                }
                finally
                {
                    writeGate.Release();
                }
                // Drop signals for frames already covered by the snapshot; a
                // concurrent Publish may still cause one benign duplicate below.
                // Xả tín hiệu của các khung snapshot đã bao phủ; Publish đồng thời
                // vẫn có thể gây một bản trùng lặp vô hại bên dưới.
                while (signal.Wait(0)) { }
            }

            while (!ct.IsCancellationRequested)
            {
                bool signaled;
                try
                {
                    signaled = await signal.WaitAsync(HeartbeatInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                await writeGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!signaled)
                    {
                        // No heartbeat before the first frame: any bytes here would
                        // either complete a corrupt line or break a client waiting
                        // for its first "data:" line.
                        // Không heartbeat trước khung đầu tiên: mọi byte lúc này đều
                        // hoặc tạo dòng hỏng, hoặc phá client đang chờ dòng "data:".
                        if (!firstFrameSent) continue;
                        await writer.WriteAsync(":ping\n\n".AsMemory(), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Coalesce bursts: drain extra signals, send latest once.
                        // Gộp loạt tín hiệu: xả tín hiệu dư, chỉ gửi khung mới nhất một lần.
                        while (signal.Wait(0)) { }
                        var frame = _state.Latest;
                        if (frame is null) continue;
                        if (!firstFrameSent)
                        {
                            await writer.WriteAsync((SerializeFrame(frame) + "\n\n").AsMemory(), ct).ConfigureAwait(false);
                            firstFrameSent = true;
                        }
                        else
                        {
                            await writer.WriteAsync(("data: " + SerializeFrame(frame) + "\n\n").AsMemory(), ct).ConfigureAwait(false);
                        }
                    }
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { } // Client disconnected. / Client đã ngắt kết nối.
        catch (IOException) { } // Client disconnected. / Client đã ngắt kết nối.
        catch (ObjectDisposedException) { }
        finally
        {
            _state.Changed -= OnChanged;
            signal.Dispose();
            writeGate.Dispose();
            try { response.Close(); } catch { }
        }
    }

    private static string SerializeFrame(OverlayFrame frame)
    {
        var boxes = frame.Boxes
            .Select(b => new { x = b.X, y = b.Y, w = b.W, h = b.H, t = b.Translated })
            .ToArray();
        return JsonSerializer.Serialize(new { boxes }, JsonOptions);
    }

    private static string ResolveWwwRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
            string? parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
            dir = parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }

    private static int GetFreePort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int free = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return free;
    }
}
