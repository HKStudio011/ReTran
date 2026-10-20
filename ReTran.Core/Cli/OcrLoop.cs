using System.Globalization;
using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;

namespace ReTran.Core.Cli;

/// <summary>
/// Options for a continuous <c>ocr loop</c> run.
/// Tùy chọn cho lần chạy <c>ocr loop</c> liên tục.
/// </summary>
/// <param name="UseWindow">True to capture a window (--hwnd), false a monitor. True để bắt cửa sổ, false để bắt màn hình.</param>
/// <param name="Monitor">Monitor index for screen capture. Chỉ số màn hình khi bắt màn hình.</param>
/// <param name="Hwnd">Window handle for window capture. Handle cửa sổ khi bắt cửa sổ.</param>
/// <param name="Fps">OCR tick rate, 1..60. Tần suất tick OCR, 1..60.</param>
/// <param name="MaxFrames">Stop after this many processed frames; 0 = unlimited. Dừng sau số khung đã xử lý; 0 = không giới hạn.</param>
/// <param name="CaptureFps">
/// Capture source frame rate in fps (>= 1). This cadences the capture only — the annotation loop
/// keeps its own serve-start rate (<c>PipelineHost</c> <c>annotatorFps</c>).
/// Tần suất khung của capture source (>= 1). Chỉ nhịp capture — vòng ghi chú giữ nhịp riêng từ lúc
/// serve start (<c>annotatorFps</c> của <c>PipelineHost</c>).
/// </param>
public sealed record OcrLoopOptions(bool UseWindow, int Monitor, nint Hwnd, int Fps, int MaxFrames, int CaptureFps = 10);

/// <summary>
/// Continuous OCR loop engine: capture buffer -> periodic tick -> pipeline -> cycle sink.
/// Engine vòng lặp OCR liên tục: buffer capture -> tick định kỳ -> pipeline -> cycle sink.
/// </summary>
public static class OcrLoop
{
    /// <summary>
    /// Usage line for the <c>loop</c> subcommand. Dòng hướng dẫn dùng cho subcommand <c>loop</c>.
    /// </summary>
    public static string Usage =>
        "usage: ReTran.Core ocr loop (--monitor N | --hwnd 0xHEX) [--fps 1..60] [--max-frames N]";

    /// <summary>
    /// Parses loop arguments; exactly one source flag is required.
    /// Phân tích đối số loop; phải có đúng một cờ nguồn.
    /// </summary>
    /// <param name="args">Arguments after <c>loop</c>. Đối số sau <c>loop</c>.</param>
    /// <param name="defaultFps">Tick rate used when --fps is absent (CoreConfig.OcrFps). Tần suất dùng khi thiếu --fps.</param>
    /// <param name="error">Null on success; otherwise the reason. Không null khi thành công; ngược lại là lý do.</param>
    /// <returns>Parsed options, or null when invalid. Tùy chọn đã parse, hoặc null nếu không hợp lệ.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="args"/> is null.</exception>
    public static OcrLoopOptions? TryParse(string[] args, int defaultFps, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        bool hasMonitor = false;
        bool hasWindow = false;
        int monitor = 0;
        nint hwnd = 0;
        int fps = defaultFps is < 1 or > 60 ? 3 : defaultFps;
        int maxFrames = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--monitor":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out monitor)
                        || monitor < 0)
                    {
                        error = "invalid --monitor (expected a decimal index >= 0).";
                        return null;
                    }
                    hasMonitor = true;
                    break;
                case "--hwnd":
                    if (i + 1 >= args.Length)
                    {
                        error = "missing value for --hwnd (expected 0xHEX or decimal).";
                        return null;
                    }
                    try
                    {
                        hwnd = CaptureCommands.ParseHwnd(args[++i]);
                    }
                    catch (FormatException ex)
                    {
                        error = ex.Message;
                        return null;
                    }
                    hasWindow = true;
                    break;
                case "--fps":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out fps)
                        || fps is < 1 or > 60)
                    {
                        error = "invalid --fps (expected 1..60).";
                        return null;
                    }
                    break;
                case "--max-frames":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxFrames)
                        || maxFrames < 0)
                    {
                        error = "invalid --max-frames (expected >= 0).";
                        return null;
                    }
                    break;
                default:
                    error = $"unknown argument '{args[i]}'.";
                    return null;
            }
        }

        if (hasMonitor == hasWindow) // both, or neither
        {
            error = "exactly one source required: --monitor N or --hwnd 0xHEX.";
            return null;
        }
        return new OcrLoopOptions(hasWindow, monitor, hwnd, fps, maxFrames);
    }

    /// <summary>
    /// Creates the capture source described by <paramref name="options"/>.
    /// Tạo capture source theo <paramref name="options"/>.
    /// </summary>
    /// <param name="options">
    /// Parsed loop options; <see cref="OcrLoopOptions.CaptureFps"/> becomes the source frame rate.
    /// Tùy chọn loop đã parse; <see cref="OcrLoopOptions.CaptureFps"/> thành tần suất của source.
    /// </param>
    /// <returns>The source (not started). Source (chưa Start).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Monitor index out of range. Chỉ số màn hình ngoài khoảng.</exception>
    public static ICaptureSource CreateSource(OcrLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.UseWindow
            ? new WindowCaptureSource(new WindowTarget(options.Hwnd), logger: null, fps: options.CaptureFps)
            : new ScreenCaptureSource(new ScreenTarget(options.Monitor), logger: null, fps: options.CaptureFps);
    }

    /// <summary>
    /// Runs the loop until max-frames is reached, <paramref name="ct"/> is cancelled, or a fatal error occurs.
    /// Chạy vòng lặp đến khi đủ max-frames, <paramref name="ct"/> bị hủy, hoặc lỗi nghiêm trọng.
    /// </summary>
    /// <param name="options">Loop options (fps, max-frames). Tùy chọn loop.</param>
    /// <param name="source">Capture source; started and stopped by this method. Capture source; method này Start/Stop.</param>
    /// <param name="pipeline">OCR pipeline shared for the whole run (session-seen diff). Pipeline OCR dùng chung cả lượt.</param>
    /// <param name="sink">Receives each completed cycle (JSON lines in the CLI). Nhận mỗi chu kỳ hoàn tất (JSON lines ở CLI).</param>
    /// <param name="stderr">Warnings/errors sink. Nơi ghi cảnh báo/lỗi.</param>
    /// <param name="ct">Cancellation (Ctrl+C). Token hủy (Ctrl+C).</param>
    /// <param name="onFatal">
    /// Optional callback with the fatal reason ("capture-error" or "sidecar-exited"),
    /// invoked right before returning 1. Callback tùy chọn nhận lý do lỗi nghiêm trọng
    /// ("capture-error" hoặc "sidecar-exited"), gọi ngay trước khi return 1.
    /// </param>
    /// <returns>0 = clean stop; 1 = first-frame timeout or sidecar lost. 0 = dừng sạch; 1 = hết chờ frame đầu hoặc mất sidecar.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null.</exception>
    public static async Task<int> RunAsync(
        OcrLoopOptions options,
        ICaptureSource source,
        OcrPipeline pipeline,
        ICycleSink sink,
        TextWriter stderr,
        CancellationToken ct,
        Action<string>? onFatal = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(stderr);

        using var buffer = new LatestFrameBuffer();
        EventHandler<FrameArrivedEventArgs> onFrame = (_, e) => buffer.Push(e.Frame);
        source.FrameArrived += onFrame;
        try
        {
            source.Start();

            // First frame: same 5 s contract as the capture CLI.
            bool gotFirst = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(CaptureCommands.FirstFrameTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                if (buffer.TryTakeLatest(out Frame? first))
                {
                    first?.Dispose();
                    gotFirst = true;
                    break;
                }
                Thread.Sleep(50);
            }
            if (!gotFirst)
            {
                stderr.WriteLine(
                    $"timed out after {CaptureCommands.FirstFrameTimeoutSeconds}s waiting for first frame.");
                onFatal?.Invoke("capture-error");
                return 1;
            }

            long seq = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / options.Fps));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (options.MaxFrames > 0 && seq >= options.MaxFrames)
                    break;
                if (!buffer.TryTakeLatest(out Frame? frame) || frame is null)
                    continue; // no new frame -> skip the tick: no OCR, no emit, never re-OCR

                try
                {
                    using (frame)
                    {
                        string b64 = BgraJpegEncoder.ToJpegBase64(frame);
                        JsonObject result = await pipeline.SpotAsync(b64, ct);
                        seq++;
                        await sink.OnCycleAsync(seq, frame.Width, frame.Height, result, ct);
                        if (options.MaxFrames > 0 && seq >= options.MaxFrames)
                            break;
                    }
                }
                catch (SidecarRpcException)
                {
                    throw; // fatal: sidecar lost
                }
                catch (OperationCanceledException)
                {
                    throw; // Ctrl+C mid-call
                }
                catch (Exception ex)
                {
                    // Per-cycle timeout or encode failure: warn and continue.
                    stderr.WriteLine($"warn: cycle skipped ({ex.Message})");
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0; // Ctrl+C -> clean stop
        }
        catch (SidecarRpcException ex)
        {
            stderr.WriteLine($"sidecar lost: {ex.Message}");
            onFatal?.Invoke("sidecar-exited");
            return 1;
        }
        finally
        {
            source.FrameArrived -= onFrame;
            try
            {
                source.Stop();
            }
            catch (InvalidOperationException)
            {
                // never started / already stopped
            }
        }
    }
}
