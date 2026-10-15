using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ReTran_App.Core;

/// <summary>
/// Launches the headless ReTran.Core.exe as a child process and talks JSON-RPC over its stdio.
/// Khởi chạy ReTran.Core.exe không-UI làm tiến trình con và nói chuyện JSON-RPC qua stdio của nó.
/// </summary>
public class CoreProcessClient : ICorePeer, IAsyncDisposable
{
    private Process? _proc;
    private JsonRpcClient? _client;
    private Func<string, JsonNode?, Task<JsonNode?>>? _requestHandler;

    /// <summary>
    /// Whether the core process connection is currently up. True after a successful start and
    /// false again after disposal.
    /// Kết nối tiến trình core hiện có đang mở hay không. True sau khi start thành công và trở lại
    /// false sau khi dispose.
    /// </summary>
    public bool IsStarted => _client is not null;

    /// <summary>
    /// Raised for each notification received from the core; forwarded from the current
    /// <see cref="JsonRpcClient"/> and kept across core restarts.
    /// Ném lên mỗi thông báo nhận từ core; được chuyển tiếp từ <see cref="JsonRpcClient"/> hiện
    /// tại và giữ qua các lần khởi động lại core.
    /// </summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>
    /// Raised when a request or notification handler throws; forwarded from the current
    /// <see cref="JsonRpcClient"/> and kept across core restarts.
    /// Ném lên khi handler của yêu cầu hoặc thông báo ném exception; được chuyển tiếp từ
    /// <see cref="JsonRpcClient"/> hiện tại và giữ qua các lần khởi động lại core.
    /// </summary>
    public event Action<Exception>? HandlerError;

    /// <summary>
    /// Raised when the core process ends on its own, carrying its exit code; not raised for the
    /// shutdown this client performs on disposal.
    /// Ném lên khi tiến trình core tự kết thúc, kèm mã thoát; không nổ cho lần tắt do client này
    /// thực hiện lúc dispose.
    /// </summary>
    public event Action<int>? Exited;

    /// <summary>
    /// Locates the core executable and launches it with <see cref="StartAsync"/>.
    /// Tìm tệp thực thi của core và khởi chạy nó qua <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="ct">Token used to cancel the start. Token dùng để hủy việc start.</param>
    /// <returns>A task that completes once the process is up. Task hoàn tất khi tiến trình đã lên.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the core executable cannot be found. Ném khi không tìm thấy tệp core.</exception>
    public Task StartCoreAsync(CancellationToken ct)
    {
        string exe = CoreLocator.FindCoreExe()
            ?? throw new FileNotFoundException("ReTran.Core.exe not found. Build it first or set RETRAN_CORE_EXE.");
        return StartAsync(exe, ct);
    }

    /// <summary>
    /// Launches the core process with --serve and wires its stdio into a JSON-RPC client.
    /// Khởi chạy tiến trình core với --serve và nối stdio của nó vào client JSON-RPC.
    /// </summary>
    public Task StartAsync(string coreExePath, CancellationToken ct)
    {
        if (_client is not null) return Task.CompletedTask;
        if (!File.Exists(coreExePath)) throw new FileNotFoundException("Core executable not found.", coreExePath);
        var psi = new ProcessStartInfo
        {
            FileName = coreExePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--serve");
        _proc = new Process { StartInfo = psi };
        _proc.Start();
        // .NET 10: StandardInput is a StreamWriter, StandardOutput a StreamReader — bind directly.
        _proc.StandardInput.NewLine = "\n";
        // Redirected stderr must be drained or the child blocks once the pipe buffer fills.
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Trace.WriteLine($"[core] {e.Data}"); };
        _proc.BeginErrorReadLine();
        // Crash/kill phải báo lên App; đặt handler trước khi bật raising để không bỏ sót exit tức thì.
        _proc.Exited += OnProcessExited;
        _proc.EnableRaisingEvents = true;
        _client = new JsonRpcClient(_proc.StandardOutput, _proc.StandardInput);
        if (_requestHandler is not null) _client.SetRequestHandler(_requestHandler);
        _client.NotificationReceived += ForwardNotification;
        _client.HandlerError += ForwardHandlerError;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a JSON-RPC request to the core and awaits its result.
    /// Gửi yêu cầu JSON-RPC tới core và chờ kết quả.
    /// </summary>
    /// <param name="method">Method name, e.g. <c>core.start</c>. Tên phương thức, ví dụ <c>core.start</c>.</param>
    /// <param name="@params">Params object, or <see langword="null"/> for none. Object tham số, hoặc <see langword="null"/> nếu không có.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>The result node of the response. Node kết quả của phản hồi.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the core is not started. Ném khi core chưa được start.</exception>
    public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        JsonRpcClient client = _client ?? throw new InvalidOperationException("Core not started.");
        return client.CallAsync(method, @params, ct);
    }

    /// <summary>
    /// Pings the core; returns the result node (expected "pong").
    /// Ping tới core; trả về node kết quả (mong đợi "pong").
    /// </summary>
    public Task<JsonNode?> PingAsync(CancellationToken ct) => CallAsync("core.ping", null, ct);

    /// <summary>
    /// Registers the single handler that answers requests sent by the core (Core → App). Kept
    /// across core restarts: it is re-applied to every new <see cref="JsonRpcClient"/>.
    /// Đăng ký handler duy nhất trả lời yêu cầu do core gửi tới (Core → App). Được giữ qua các
    /// lần khởi động lại core: handler được gắn lại cho mỗi <see cref="JsonRpcClient"/> mới.
    /// </summary>
    /// <param name="handler">
    /// Receives the method name and params and returns the result node; a thrown exception is
    /// answered as a -32603 error.
    /// Nhận tên phương thức và params rồi trả về node kết quả; exception được ném ra sẽ được trả
    /// lời bằng lỗi -32603.
    /// </param>
    public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler)
    {
        _requestHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        _client?.SetRequestHandler(handler);
    }

    private void ForwardNotification(string method, JsonNode? @params)
        => NotificationReceived?.Invoke(method, @params);

    private void ForwardHandlerError(Exception ex) => HandlerError?.Invoke(ex);

    // Event của Process đến trên luồng riêng: lỗi của subscriber không được kéo app xuống.
    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode = sender is Process proc ? SafeExitCode(proc) : -1;
        try
        {
            Exited?.Invoke(exitCode);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[core] Exited handler failed: {ex.Message}");
        }
    }

    private static int SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>
    /// Kills the core process tree and releases the JSON-RPC connection; idempotent.
    /// Virtual so test doubles can simulate a disposal failure (UpdateStopper's per-step isolation).
    /// Kill cây tiến trình core và giải phóng kết nối JSON-RPC; idempotent.
    /// Virtual để test double giả lập lỗi disposal (cách ly từng bước của UpdateStopper).
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        JsonRpcClient? client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync();
        Process? proc = _proc;
        _proc = null;
        if (proc is not null)
        {
            // Tắt raising + bỏ handler trước khi kill: tắt có chủ đích không được báo như crash.
            proc.Exited -= OnProcessExited;
            proc.EnableRaisingEvents = false;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            await proc.WaitForExitAsync().ConfigureAwait(false);
            proc.Dispose();
        }
    }
}
