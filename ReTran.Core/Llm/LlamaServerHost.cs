using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ReTran.Core.Llm;

/// <summary>
/// Owns the lifecycle of one <c>llama-server.exe</c> process: picks a free loopback port, spawns
/// the server with the catalog GGUF, waits for <c>/health</c> before any request (model load can
/// take tens of seconds), drains the server's output so its pipes never fill, and kills the whole
/// process tree on dispose. Starting is lazy and idempotent so it can be triggered on the first
/// translation without blocking <c>core.start</c>. The runtime binaries are never bundled with the
/// app — the setup wizard (or the settings UI) downloads them (spec 2026-11-14 D5/D13).
/// Sở hữu vòng đời một tiến trình <c>llama-server.exe</c>: chọn cổng loopback trống, spawn server
/// với GGUF trong catalog, chờ <c>/health</c> trước mọi request (nạp model có thể mất hàng chục
/// giây), hút output của server để pipe không đầy, và kill cả cây tiến trình khi dispose. Khởi
/// động là lazy và idempotent nên có thể kích hoạt ở lần dịch đầu mà không chặn <c>core.start</c>.
/// Binary runtime không bao giờ đi kèm app — setup wizard (hoặc UI Settings) tải về
/// (spec 2026-11-14 D5/D13).
/// </summary>
/// <param name="serverExe">Path to the extracted <c>llama-server.exe</c>. Đường dẫn <c>llama-server.exe</c> đã giải nén.</param>
/// <param name="modelPath">Path to the GGUF model file. Đường dẫn file model GGUF.</param>
/// <param name="contextSize">Context window passed as <c>-c</c>. Cửa sổ ngữ cảnh truyền qua <c>-c</c>.</param>
/// <param name="gpuLayers">GPU layers passed as <c>-ngl</c>. Số layer GPU truyền qua <c>-ngl</c>.</param>
public sealed class LlamaServerHost(
    string serverExe, string modelPath,
    int contextSize = 4096, int gpuLayers = 99) : ILlamaServerHost
{
    /// <summary>How long the server may take to report healthy (model load included). Thời gian tối đa để server báo khỏe (gồm nạp model).</summary>
    public static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan HealthInterval = TimeSpan.FromMilliseconds(250);
    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private const int StderrTailLines = 50;

    private readonly object _gate = new();
    private readonly Queue<string> _stderrTail = new();
    private Process? _process;
    private int _port;

    // Serializes EnsureStartedAsync ACROSS the health wait: without it a second concurrent caller
    // sees the process alive and returns before /health passes, so one translation can hit a
    // still-loading server (BA review 2026-11-17). Held from entry until healthy (or failed+cleaned).
    // Tuần tự hoá EnsureStartedAsync XUYÊN suốt pha chờ health: thiếu nó, caller thứ hai thấy process
    // còn sống và trả về trước khi /health xong — một lượt dịch có thể trúng server đang nạp model
    // (review BA 2026-11-17). Giữ từ lúc vào tới khi khỏe (hoặc fail + dọn sạch).
    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>The chosen port, or 0 before <see cref="EnsureStartedAsync"/>. Cổng đã chọn, hoặc 0 trước khi start.</summary>
    public int Port
    {
        get { lock (_gate) return _port; }
    }

    /// <summary>The base URL once the port is chosen. URL gốc sau khi đã chọn cổng.</summary>
    /// <exception cref="InvalidOperationException">Thrown before the server has started. Khi server chưa start.</exception>
    public string BaseUrl
    {
        get
        {
            int port = Port;
            if (port == 0) throw new InvalidOperationException("llama-server has not started.");
            return $"http://127.0.0.1:{port}";
        }
    }

    /// <summary>
    /// Command line for the server: model, loopback host, explicit port, context size, GPU layers
    /// and the embedded chat template. Dòng lệnh cho server: model, host loopback, cổng cố định,
    /// cỡ ngữ cảnh, số layer GPU và chat template nhúng.
    /// </summary>
    /// <param name="modelPath">GGUF path (quoted). Đường dẫn GGUF (đặt trong ngoặc kép).</param>
    /// <param name="port">Port to listen on. Cổng lắng nghe.</param>
    /// <param name="contextSize">Context window. Cửa sổ ngữ cảnh.</param>
    /// <param name="gpuLayers">GPU layers. Số layer GPU.</param>
    /// <returns>The full argument string. Chuỗi tham số đầy đủ.</returns>
    public static string BuildArguments(string modelPath, int port, int contextSize, int gpuLayers) =>
        $"-m \"{modelPath}\" --host 127.0.0.1 --port {port} -c {contextSize} -ngl {gpuLayers} --jinja";

    /// <summary>
    /// Binds a loopback listener on port 0 to let the OS pick a free port, then releases it.
    /// Bind listener loopback ở cổng 0 để OS chọn cổng trống, rồi nhả ra.
    /// </summary>
    /// <returns>A port that was free a moment ago. Cổng vừa còn trống.</returns>
    public static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Polls a health probe until it reports true or the timeout elapses; probe exceptions count
    /// as "not up yet". Poll health probe tới khi trả true hoặc hết timeout; exception của probe
    /// được coi là "chưa lên".
    /// </summary>
    /// <param name="probe">One health attempt. Một lần thử health.</param>
    /// <param name="timeout">Overall budget. Ngân sách tổng.</param>
    /// <param name="interval">Delay between attempts. Khoảng nghỉ giữa các lần thử.</param>
    /// <param name="ct">Token used to cancel the wait. Token dùng để hủy chờ.</param>
    /// <returns>True when the probe succeeded in time. True khi probe thành công kịp lúc.</returns>
    public static async Task<bool> WaitForHealthAsync(
        Func<CancellationToken, Task<bool>> probe, TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await probe(ct).ConfigureAwait(false)) return true;
            }
            catch (Exception)
            {
                // Connection refused while the model still loads — keep polling.
                // Connection refused trong lúc model còn nạp — tiếp tục poll.
            }
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Ensures the server is running and healthy; idempotent and safe to call before every
    /// translation. Đảm bảo server đang chạy và khỏe; idempotent, gọi được trước mọi lượt dịch.
    /// </summary>
    /// <param name="ct">Token used to cancel the start. Token dùng để hủy khởi động.</param>
    /// <exception cref="InvalidOperationException">Thrown when the binary/model is missing or the server never becomes healthy. Khi thiếu binary/model hoặc server không khỏe kịp.</exception>
    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        // Held across the health wait — see the field doc (BA review 2026-11-17).
        // Giữ xuyên suốt pha chờ health — xem doc của field (review BA 2026-11-17).
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        string effectiveServerExe = serverExe;
        string effectiveModelPath = modelPath;
        int port;
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            if (!File.Exists(effectiveServerExe))
            {
                throw new InvalidOperationException($"llama-server not found at {effectiveServerExe}.");
            }
            if (!File.Exists(effectiveModelPath))
            {
                throw new InvalidOperationException($"GGUF model not found at {effectiveModelPath}.");
            }

            _port = PickFreePort();
            port = _port;
            var startInfo = new ProcessStartInfo(effectiveServerExe, BuildArguments(effectiveModelPath, port, contextSize, gpuLayers))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(effectiveServerExe) ?? string.Empty,
            };
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start llama-server.");
            // Drain both pipes via events so a chatty server can never block on a full pipe.
            // Hút cả hai pipe bằng event để server nói nhiều không bao giờ kẹt vì pipe đầy.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, e) => AppendStderrTail(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        bool healthy = await WaitForHealthAsync(
            async token =>
            {
                using HttpResponseMessage response = await ProbeClient
                    .GetAsync($"http://127.0.0.1:{port}/health", token).ConfigureAwait(false);
                return response.StatusCode == HttpStatusCode.OK;
            },
            DefaultHealthTimeout, HealthInterval, ct).ConfigureAwait(false);

        if (!healthy)
        {
            string tail = KillAndCollectStderrTail();
            throw new InvalidOperationException(
                $"llama-server did not become healthy within {DefaultHealthTimeout.TotalSeconds:0} s.{tail}");
        }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void AppendStderrTail(string? line)
    {
        if (line is null) return;
        lock (_gate)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
        }
    }

    private string KillAndCollectStderrTail()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _port = 0;
        }
        if (process is null) return string.Empty;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception)
        {
            // Best-effort kill; the failure message below still explains the root cause.
            // Kill best-effort; thông điệp lỗi bên dưới vẫn nêu nguyên nhân gốc.
        }
        process.Dispose();

        lock (_gate)
        {
            if (_stderrTail.Count == 0) return string.Empty;
            string tail = string.Join(" | ", _stderrTail);
            _stderrTail.Clear();
            return $" (stderr tail) {(tail.Length > 300 ? tail[..300] : tail)}";
        }
    }

    /// <summary>
    /// Kills the server process tree. Safe to call multiple times. Kill cây tiến trình server.
    /// Gọi nhiều lần vẫn an toàn.
    /// </summary>
    public void Dispose()
    {
        _ = KillAndCollectStderrTail();
    }
}
