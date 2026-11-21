using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ReTran.Core.Llm;

/// <summary>
/// Owns the lifecycle of one <c>llama-server.exe</c> process: picks a free loopback port, spawns
/// the server with the catalog GGUF, waits for <c>/health</c> before any request, publishes
/// IO-based loading progress, detects a stalled load, drains output, and kills the process tree on
/// dispose. Starting is lazy and idempotent; runtime binaries are downloaded by Setup or Settings.
/// Sở hữu vòng đờ một tiến trình <c>llama-server.exe</c>: chọn cổng loopback trống, spawn server
/// với GGUF trong catalog, chờ <c>/health</c> trước mọi request, phát hành tiến trình nạp theo IO,
/// phát hiện nạp bị đứng, hút output và kill cây tiến trình khi dispose. Khởi động lazy và idempotent;
/// binary runtime do Setup hoặc Settings tải về.
/// </summary>
public sealed class LlamaServerHost : ILlamaServerHost
{
    /// <summary>Fixed health ceiling when process counters cannot be read. Trần health cố định khi không đọc được counter tiến trình.</summary>
    public static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(600);

    private static readonly TimeSpan HealthInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActivitySampleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(600);
    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private const int StderrTailLines = 50;
    private const long ActivityIoBytes = 1024 * 1024;
    private const long ActivityRamBytes = 1024 * 1024;
    private const long ActivityPageFaults = 64;

    private readonly string _serverExe;
    private readonly string _modelPath;
    private readonly int _contextSize;
    private readonly int _gpuLayers;
    private readonly ILlamaProcessMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly Func<ProcessStartInfo, Process?> _processFactory;
    private readonly Func<int, CancellationToken, Task<bool>>? _healthProbe;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private int _port;
    private int _disposed;
    private LlmLoadProgressSnapshot _loadProgress = new("stopped", null, null, null, false);

    /// <summary>
    /// Creates a host for one model with optional counter and clock implementations for controlled tests.
    /// Tạo host cho một model, có thể truyền bộ đếm và đồng hồ riêng cho test có kiểm soát.
    /// </summary>
    /// <param name="serverExe">Path to the extracted <c>llama-server.exe</c>. Đường dẫn <c>llama-server.exe</c> đã giải nén.</param>
    /// <param name="modelPath">Path to the GGUF model file. Đường dẫn file model GGUF.</param>
    /// <param name="contextSize">Context window passed as <c>-c</c>. Cửa sổ ngữ cảnh truyền qua <c>-c</c>.</param>
    /// <param name="gpuLayers">GPU layers passed as <c>-ngl</c>. Số layer GPU truyền qua <c>-ngl</c>.</param>
    /// <param name="metrics">Optional process-counter reader; null uses Win32. Bộ đọc counter tùy chọn; null dùng Win32.</param>
    /// <param name="timeProvider">Optional health-wait clock; null uses the system clock. Đồng hồ chờ health tùy chọn; null dùng đồng hồ hệ thống.</param>
    public LlamaServerHost(
        string serverExe, string modelPath,
        int contextSize = 4096, int gpuLayers = 99,
        ILlamaProcessMetrics? metrics = null, TimeProvider? timeProvider = null)
        : this(serverExe, modelPath, contextSize, gpuLayers,
            metrics ?? new LlamaProcessMetrics(), timeProvider ?? TimeProvider.System,
            Process.Start, null)
    {
    }

    internal LlamaServerHost(
        string serverExe, string modelPath, int contextSize, int gpuLayers,
        ILlamaProcessMetrics metrics, TimeProvider timeProvider,
        Func<ProcessStartInfo, Process?> processFactory,
        Func<int, CancellationToken, Task<bool>>? healthProbe)
    {
        _serverExe = serverExe ?? throw new ArgumentNullException(nameof(serverExe));
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        _contextSize = contextSize;
        _gpuLayers = gpuLayers;
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _healthProbe = healthProbe;
    }

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

    /// <inheritdoc />
    public LlmLoadProgressSnapshot LoadProgress
    {
        get { lock (_gate) return _loadProgress; }
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
            }
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Ensures the server is running and healthy; idempotent and safe to call before every translation.
    /// Đảm bảo server đang chạy và khỏe; idempotent, gọi được trước mọi lượt dịch.
    /// </summary>
    /// <param name="ct">Token used to cancel the start. Token dùng để hủy khởi động.</param>
    /// <exception cref="InvalidOperationException">Thrown when the binary/model is missing, the process exits, or loading stalls. Ném khi thiếu binary/model, tiến trình thoát hoặc nạp bị đứng.</exception>
    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        bool acquired = false;
        try
        {
            // Serialize the complete health wait so a second translator cannot use a loading server.
            await _startGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            Process process;
            lock (_gate)
            {
                if (IsAlive(_process))
                {
                    _loadProgress = _loadProgress with { Phase = "ready" };
                    return;
                }
                if (!File.Exists(_serverExe))
                    throw new InvalidOperationException($"llama-server not found at {_serverExe}.");
                if (!File.Exists(_modelPath))
                    throw new InvalidOperationException($"GGUF model not found at {_modelPath}.");

                _port = PickFreePort();
                long? total = new FileInfo(_modelPath).Length;
                _loadProgress = new LlmLoadProgressSnapshot("loading", null, total, null, false);
                var startInfo = new ProcessStartInfo(
                    _serverExe, BuildArguments(_modelPath, _port, _contextSize, _gpuLayers))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(_serverExe) ?? string.Empty,
                };
                _process = _processFactory(startInfo)
                    ?? throw new InvalidOperationException("Failed to start llama-server.");
                _process.OutputDataReceived += (_, _) => { };
                _process.ErrorDataReceived += (_, e) => AppendStderrTail(e.Data);
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                process = _process;
            }

            await MonitorHealthAsync(process, Port, linked.Token).ConfigureAwait(false);
            lock (_gate) _loadProgress = _loadProgress with { Phase = "ready" };
        }
        catch
        {
            if (acquired) _ = KillAndCollectStderrTail();
            throw;
        }
        finally
        {
            if (acquired) _startGate.Release();
        }
    }

    private async Task MonitorHealthAsync(Process process, int port, CancellationToken ct)
    {
        DateTimeOffset started = _timeProvider.GetUtcNow();
        DateTimeOffset deadline = started + StallWindow;
        DateTimeOffset nextSample = started;
        DateTimeOffset previousSampleAt = started;
        LlamaProcessSample previous = default!;
        bool havePrevious = false;
        bool countersUnavailable = false;
        long bytesHighWater = 0;
        long ramHighWater = 0;

        // A due sample always runs before the deadline decision: activity in the final sample
        // window is still activity, including while a health probe was in flight.
        void SampleDueActivity(DateTimeOffset now)
        {
            if (countersUnavailable || now < nextSample) return;
            if (!_metrics.TryRead(process, out LlamaProcessSample sample))
            {
                countersUnavailable = true;
                deadline = started + DefaultHealthTimeout;
                long? total = LoadProgress.BytesTotal;
                SetProgress(new LlmLoadProgressSnapshot("loading", null, total, null, false));
                return;
            }

            TimeSpan elapsed = havePrevious ? now - previousSampleAt : TimeSpan.Zero;
            double? speed = null;
            bool activity = false;
            if (havePrevious && elapsed > TimeSpan.Zero)
            {
                long readDelta = sample.ReadBytes - previous.ReadBytes;
                if (readDelta >= 0) speed = readDelta / elapsed.TotalSeconds;
                activity = readDelta >= ActivityIoBytes
                    || sample.PageFaults - previous.PageFaults >= ActivityPageFaults
                    || (sample.CpuTime - previous.CpuTime).TotalSeconds / elapsed.TotalSeconds >= 0.05;
            }

            activity |= sample.WorkingSetBytes - ramHighWater >= ActivityRamBytes;
            ramHighWater = Math.Max(ramHighWater, sample.WorkingSetBytes);
            bytesHighWater = Math.Max(bytesHighWater, sample.ReadBytes);
            if (activity) deadline = now + StallWindow;
            long? modelBytes = LoadProgress.BytesTotal;
            string phase = modelBytes > 0 && bytesHighWater >= modelBytes ? "finalizing" : "loading";
            SetProgress(new LlmLoadProgressSnapshot(
                phase, bytesHighWater, modelBytes, speed, modelBytes > 0));
            previous = sample;
            previousSampleAt = now;
            havePrevious = true;
            nextSample = now + ActivitySampleInterval;
        }

        void ThrowIfPastDeadline(DateTimeOffset now)
        {
            if (now < deadline) return;
            string tail = KillAndCollectStderrTail();
            throw new InvalidOperationException(
                $"llama-server stalled: no loading activity for 600 s (không có hoạt động trong 10 phút).{tail}");
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAlive(process))
            {
                int? exitCode = TryGetExitCode(process);
                string tail = KillAndCollectStderrTail();
                throw new InvalidOperationException(
                    $"llama-server exited before becoming healthy{(exitCode is null ? "" : $" (exit code {exitCode})")}.{tail}");
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            SampleDueActivity(now);
            ThrowIfPastDeadline(now);

            bool healthy = false;
            using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (ITimer probeDeadline = _timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(), probeCts,
                deadline - now, Timeout.InfiniteTimeSpan))
            {
                try
                {
                    healthy = _healthProbe is null
                        ? await ProbeHealthAsync(port, probeCts.Token).ConfigureAwait(false)
                        : await _healthProbe(port, probeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
                {
                    // The completion-time deadline check below turns this into a stall, not caller cancellation.
                }
                catch (Exception)
                {
                    // Connection refused while the model loads is not a startup failure yet.
                }
            }

            DateTimeOffset completedAt = _timeProvider.GetUtcNow();
            SampleDueActivity(completedAt);
            ThrowIfPastDeadline(completedAt);
            if (healthy) return;

            TimeSpan delay = HealthInterval;
            TimeSpan remaining = deadline - completedAt;
            if (remaining < delay) delay = remaining;
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ProbeHealthAsync(int port, CancellationToken ct)
    {
        using HttpResponseMessage response = await ProbeClient
            .GetAsync($"http://127.0.0.1:{port}/health", ct).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private void SetProgress(LlmLoadProgressSnapshot snapshot)
    {
        lock (_gate) _loadProgress = snapshot;
    }

    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            return null;
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
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000); // also flushes async stdout/stderr handlers after natural exit
        }
        catch (Exception)
        {
            // Best-effort kill; the health error still reports the root cause.
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
    /// Cancels an outstanding health wait, then kills the server process tree. Safe to repeat.
    /// Hủy health wait đang chờ rồi kill cây tiến trình server. Gọi lặp vẫn an toàn.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _disposeCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _ = KillAndCollectStderrTail();
        lock (_gate) _loadProgress = new LlmLoadProgressSnapshot("stopped", null, null, null, false);
    }
}
