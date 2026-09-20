using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ReTran_App.Core;

/// <summary>
/// Launches the headless ReTran.Core.exe as a child process and talks JSON-RPC over its stdio.
/// Khởi chạy ReTran.Core.exe không-UI làm tiến trình con và nói chuyện JSON-RPC qua stdio của nó.
/// </summary>
public sealed class CoreProcessClient : IAsyncDisposable
{
    private Process? _proc;
    private JsonRpcClient? _client;

    /// <summary>
    /// Launches the core process with --serve and wires its stdio into a JSON-RPC client.
    /// Khởi chạy tiến trình core với --serve và nối stdio của nó vào client JSON-RPC.
    /// </summary>
    public Task StartAsync(string coreExePath, CancellationToken ct)
    {
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
        _client = new JsonRpcClient(_proc.StandardOutput, _proc.StandardInput);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pings the core; returns the result node (expected "pong").
    /// Ping tới core; trả về node kết quả (mong đợi "pong").
    /// </summary>
    public async Task<JsonNode?> PingAsync(CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Core not started.");
        return await _client.CallAsync("core.ping", null, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_proc is not null)
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            await _proc.WaitForExitAsync().ConfigureAwait(false);
            _proc.Dispose();
        }
    }
}
