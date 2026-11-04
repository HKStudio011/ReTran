using System.Diagnostics;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
// The static framing class shares its name with the namespace it lives in; from any
// sibling namespace the bare name binds to the namespace, so alias it explicitly.
using Rpc = ReTran.Core.JsonRpc.JsonRpc;

namespace ReTran.Core.OcrClient;

/// <summary>
/// Owns the long-lived Python OCR sidecar process and talks JSON-RPC over its stdio.
/// Sở hữu tiến trình sidecar Python dài hạn và nói chuyện JSON-RPC qua stdio của nó.
/// </summary>
public sealed class SidecarProcess : IAsyncDisposable, ISidecarRpc
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private int _nextId = 0;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _exited;

    private SidecarProcess(Process proc, StreamWriter stdin) { _proc = proc; _stdin = stdin; }

    /// <summary>Spawns `python -m retran_core serve` in workingDir and starts the response read loop.</summary>
    public static Task<SidecarProcess> StartAsync(string pythonExe, string workingDir, CancellationToken ct)
    {
        var psi = BuildStartInfo(pythonExe, workingDir);
        var proc = new Process { StartInfo = psi };
        proc.Start();
        // Paddle floods stderr; an unread redirected pipe would fill up and deadlock the sidecar.
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[sidecar] {e.Data}"); };
        proc.BeginErrorReadLine();
        // .NET 10: StandardInput is a StreamWriter, StandardOutput a StreamReader — use them directly.
        proc.StandardInput.NewLine = "\n";
        var sp = new SidecarProcess(proc, proc.StandardInput);
        sp.StartReadLoop();
        return Task.FromResult(sp);
    }

    /// <summary>
    /// Builds the sidecar's <see cref="ProcessStartInfo"/>: `python -m retran_core serve` in
    /// <c>workingDir\ReTran.OCR</c> with the paddlex env vars pinned. No process is started.
    /// Dựng <see cref="ProcessStartInfo"/> cho sidecar: `python -m retran_core serve` chạy trong
    /// <c>workingDir\ReTran.OCR</c> với các biến env của paddlex được ghim sẵn. Không khởi chạy
    /// tiến trình nào.
    /// </summary>
    /// <param name="pythonExe">Interpreter to launch. Trình thông dịch cần khởi chạy.</param>
    /// <param name="workingDir">App/repo root; the sidecar runs in its <c>ReTran.OCR</c> subdirectory. Gốc app/repo; sidecar chạy trong thư mục con <c>ReTran.OCR</c> của nó.</param>
    /// <returns>The prepared start info. Start info đã chuẩn bị.</returns>
    internal static ProcessStartInfo BuildStartInfo(string pythonExe, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.Combine(workingDir, "ReTran.OCR"),
            CreateNoWindow = true,
        };
        // Skip PaddleX's model-host connectivity check (adds ~4s to first model load offline).
        psi.EnvironmentVariables["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        // Model cache lives next to the OCR project so venv and models survive app updates
        // together (spec 2026-11-03-first-run-setup-wizard §5.5).
        // Cache model nằm cạnh dự án OCR để venv và model sống qua update cùng nhau.
        psi.EnvironmentVariables["PADDLE_PDX_CACHE_HOME"] = Path.Combine(workingDir, "ReTran.OCR", "models");
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("retran_core");
        psi.ArgumentList.Add("serve");
        return psi;
    }

    /// <summary>Background loop: reads response lines and routes each to its waiter by id.</summary>
    private void StartReadLoop()
    {
        var reader = _proc.StandardOutput;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                    TryRouteResponseLine(line, Deliver, Fail);
            }
            catch { /* unexpected read failure; waiters are failed in finally */ }
            finally
            {
                FailAllPending(new SidecarRpcException(RpcErrorCodes.InternalError, "sidecar exited"));
            }
        });
    }

    /// <summary>
    /// Parses and routes one response line; the delivered result is a clone detached from the parsed envelope.
    /// Phân tích và định tuyến một dòng phản hồi; node kết quả giao ra là bản sao đã tách khỏi envelope đã phân tích.
    /// </summary>
    /// <param name="line">One raw response line. Một dòng phản hồi thô.</param>
    /// <param name="deliver">Receives the id and the detached result node. Nhận id và node kết quả đã tách.</param>
    /// <param name="fail">Receives the id and the sidecar error to surface. Nhận id và lỗi sidecar cần ném ra.</param>
    /// <returns>
    /// <see langword="true"/> when the line was a valid response and was routed; <see langword="false"/> for
    /// blank lines, non-responses and notifications.
    /// Trả <see langword="true"/> khi dòng là phản hồi hợp lệ và đã được định tuyến; <see langword="false"/> với
    /// dòng trống, dòng không phải phản hồi và thông báo.
    /// </returns>
    internal static bool TryRouteResponseLine(string line, Action<string, JsonNode?> deliver, Action<string, Exception> fail)
    {
        if (Rpc.ParseLine(line) is not { } n || n["id"]?.GetValue<string>() is not { } id)
            return false;
        if (n["result"] is { } r) { deliver(id, r.DeepClone()); return true; }
        if (n["error"] is { } err)
        {
            int code = err["code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : RpcErrorCodes.InternalError;
            string msg = err["message"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : "sidecar error";
            fail(id, new SidecarRpcException(code, msg));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sends a request and awaits its response (matched by id); throws when the sidecar has already exited.
    /// Gửi yêu cầu và đợi phản hồi (ghép theo id); ném ngoại lệ khi sidecar đã thoát.
    /// </summary>
    /// <returns>
    /// The response result as a detached clone (no parent), safe to re-parent or serialize.
    /// Kết quả phản hồi dưới dạng bản sao tách rời (không có parent), an toàn để gắn vào node khác hoặc serialize.
    /// </returns>
    /// <exception cref="SidecarRpcException">
    /// Thrown when the sidecar has already exited, or exits before replying.
    /// Khi sidecar đã thoát, hoặc thoát trước khi phản hồi.
    /// </exception>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string requestLine;
        lock (_gate)
        {
            if (_exited)
                throw new SidecarRpcException(RpcErrorCodes.InternalError, "sidecar exited");
            string id = (++_nextId).ToString();
            _pending[id] = tcs;
            requestLine = Rpc.SerializeRequest(new JsonRpcRequest(id, method, @params)) + "\n";
        }
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stdin.WriteAsync(requestLine.AsMemory(), ct);
            await _stdin.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>Called by the read loop to route a response to its waiter.</summary>
    private void Deliver(string id, JsonNode? result)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(id, out var tcs)) { _pending.Remove(id); tcs.TrySetResult(result); }
        }
    }

    /// <summary>Routes a sidecar error reply to its waiter as an exception.</summary>
    private void Fail(string id, Exception ex)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(id, out var tcs)) { _pending.Remove(id); tcs.TrySetException(ex); }
        }
    }

    /// <summary>Fails every waiter when the sidecar process exits.</summary>
    private void FailAllPending(Exception ex)
    {
        lock (_gate)
        {
            _exited = true;
            foreach (var tcs in _pending.Values) tcs.TrySetException(ex);
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        await _proc.WaitForExitAsync().ConfigureAwait(false);
        _proc.Dispose();
    }
}
