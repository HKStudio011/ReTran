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
public sealed class SidecarProcess : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private int _nextId = 0;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();

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
            WorkingDirectory = Path.Combine(workingDir, "ReTran Core"),
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("retran_core");
        psi.ArgumentList.Add("serve");
        var proc = new Process { StartInfo = psi };
        proc.Start();
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
                    if (Rpc.ParseLine(line) is { } n
                        && n["id"]?.GetValue<string>() is { } id
                        && n["result"] is { } r)
                    {
                        Deliver(id, r);
                    }
                }
            }
            catch { /* sidecar exited; pending waiters are cancelled by DisposeAsync */ }
        });
    }

    /// <summary>Sends a request and awaits its response (matched by id).</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        string id = (++_nextId).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await _stdin.WriteAsync((Rpc.SerializeRequest(new JsonRpcRequest(id, method, @params)) + "\n").AsMemory(), ct);
        await _stdin.FlushAsync(ct);
        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>Called by the read loop to route a response to its waiter.</summary>
    private void Deliver(string id, JsonNode? result)
    {
        if (_pending.TryGetValue(id, out var tcs)) { _pending.Remove(id); tcs.TrySetResult(result); }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        await _proc.WaitForExitAsync().ConfigureAwait(false);
        _proc.Dispose();
    }
}
