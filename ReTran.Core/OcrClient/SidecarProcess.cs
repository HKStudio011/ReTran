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
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("retran_core");
        psi.ArgumentList.Add("serve");
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
                {
                    if (Rpc.ParseLine(line) is not { } n
                        || n["id"]?.GetValue<string>() is not { } id)
                        continue;
                    if (n["result"] is { } r)
                    {
                        Deliver(id, r);
                    }
                    else if (n["error"] is { } err)
                    {
                        int code = err["code"] is JsonValue cv && cv.TryGetValue<int>(out var c)
                            ? c : RpcErrorCodes.InternalError;
                        string msg = err["message"] is JsonValue mv && mv.TryGetValue<string>(out var m)
                            ? m : "sidecar error";
                        Fail(id, new SidecarRpcException(code, msg));
                    }
                }
            }
            catch { /* unexpected read failure; waiters are failed in finally */ }
            finally
            {
                FailAllPending(new SidecarRpcException(RpcErrorCodes.InternalError, "sidecar exited"));
            }
        });
    }

    /// <summary>
    /// Sends a request and awaits its response (matched by id); throws when the sidecar has already exited.
    /// Gửi yêu cầu và đợi phản hồi (ghép theo id); ném ngoại lệ khi sidecar đã thoát.
    /// </summary>
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
