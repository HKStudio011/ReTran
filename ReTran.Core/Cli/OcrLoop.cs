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
/// <param name="MergeLines">Group multi-line OCR spots before translation (--merge-lines). Gom spot OCR đa dòng trước khi dịch (--merge-lines).</param>
/// <param name="Merge">
/// Line-merge caps from <c>--merge-gap</c>/<c>--merge-height</c>/<c>--merge-overlap</c>; <see langword="null"/>
/// means <see cref="MergeThresholds.Default"/>. Nullable on purpose: a <see cref="MergeThresholds"/>
/// value is not a compile-time constant, so it cannot be an optional parameter default (CS1736), and
/// <c>default(MergeThresholds)</c> (0/0/0) sits outside the allowed range.
/// Ngưỡng gộp dòng lấy từ <c>--merge-gap</c>/<c>--merge-height</c>/<c>--merge-overlap</c>; <see langword="null"/>
/// nghĩa là <see cref="MergeThresholds.Default"/>. Để dạng null có chủ ý: giá trị <see cref="MergeThresholds"/>
/// không phải hằng số lúc biên dịch nên không làm giá trị mặc định của tham số được (CS1736), và
/// <c>default(MergeThresholds)</c> (0/0/0) thì ngoài dải cho phép.
/// </param>
public sealed record OcrLoopOptions(bool UseWindow, int Monitor, nint Hwnd, int Fps, int MaxFrames, int CaptureFps = 10, bool MergeLines = false, MergeThresholds? Merge = null);

/// <summary>
/// Continuous OCR loop engine: capture buffer -> periodic tick -> pipeline -> cycle sink.
/// Engine vòng lặp OCR liên tục: buffer capture -> tick định kỳ -> pipeline -> cycle sink.
/// </summary>
public static class OcrLoop
{
    /// <summary>
    /// Names of the three line-merge threshold flags accepted by <c>ocr</c> and <c>ocr loop</c>.
    /// Holding them in one place keeps the parser, the validation messages and both usage lines in
    /// agreement about what the user may type.
    /// Tên ba cờ ngưỡng gộp dòng mà <c>ocr</c> và <c>ocr loop</c> chấp nhận. Giữ chung một chỗ để
    /// trình parse, message kiểm tra và hai dòng usage luôn khớp những gì người dùng được gõ.
    /// </summary>
    internal static class MergeFlags
    {
        /// <summary>Gap-ratio flag. Cờ ngưỡng khoảng cách.</summary>
        public const string Gap = "--merge-gap";

        /// <summary>Height-ratio flag. Cờ ngưỡng tỷ lệ chiều cao.</summary>
        public const string Height = "--merge-height";

        /// <summary>Overlap-ratio flag. Cờ ngưỡng độ chồng ngang.</summary>
        public const string Overlap = "--merge-overlap";

        /// <summary>
        /// Bracketed block appended to both usage lines. Khối đặt trong ngoặc vuông nối vào cả hai dòng usage.
        /// </summary>
        public static string UsageSuffix => $"[{Gap} N] [{Height} N] [{Overlap} N]";
    }

    /// <summary>
    /// Usage line for the <c>loop</c> subcommand. Dòng hướng dẫn dùng cho subcommand <c>loop</c>.
    /// </summary>
    public static string Usage =>
        "usage: ReTran.Core ocr loop (--monitor N | --hwnd 0xHEX) [--fps 1..60] [--max-frames N] [--merge-lines] " + MergeFlags.UsageSuffix;

    /// <summary>
    /// Parses loop arguments; exactly one source flag is required.
    /// Phân tích đối số loop; phải có đúng một cờ nguồn.
    /// </summary>
    /// <remarks>
    /// The merge flags (<see cref="MergeFlags.Gap"/>, <see cref="MergeFlags.Height"/>,
    /// <see cref="MergeFlags.Overlap"/>) are optional in any order; absent, the parsed
    /// <see cref="OcrLoopOptions.Merge"/> is exactly <see cref="MergeThresholds.Default"/>.
    /// Các cờ gộp (<see cref="MergeFlags.Gap"/>, <see cref="MergeFlags.Height"/>,
    /// <see cref="MergeFlags.Overlap"/>) tùy chọn và không bắt buộc thứ tự; nếu thiếu,
    /// <see cref="OcrLoopOptions.Merge"/> parse ra đúng <see cref="MergeThresholds.Default"/>.
    /// </remarks>
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
        bool mergeLines = false;
        // Every merge flag is optional; the starting triple is the shipped default, so flags that
        // are never typed can never leave a partially-set (or all-zero) threshold behind.
        // Mọi cờ gộp đều tùy chọn; bộ ba khởi điểm là mặc định gốc, nên cờ không gõ sẽ không để lại
        // ngưỡng đặt dở (hoặc toàn 0).
        MergeThresholds merge = MergeThresholds.Default;

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
                case "--merge-lines":
                    mergeLines = true;
                    break;
                case MergeFlags.Gap:
                case MergeFlags.Height:
                case MergeFlags.Overlap:
                    if (!TryReadMergeThreshold(args[i], args, ref i, merge, out merge, out error))
                        return null;
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
        return new OcrLoopOptions(hasWindow, monitor, hwnd, fps, maxFrames,
            MergeLines: mergeLines, Merge: merge);
    }

    /// <summary>
    /// Reads the value of one merge-threshold flag and folds it into <paramref name="current"/>. The
    /// number is parsed with <see cref="CultureInfo.InvariantCulture"/> (the CLI always takes "0.6",
    /// never the vi-VN "0,6"), and the assembled triple must satisfy
    /// <see cref="MergeThresholds.IsValid"/>; a missing value, a non-number, or an out-of-range value
    /// yields a message naming that flag and its range, built from the <see cref="MergeThresholds"/>
    /// bounds so no range literal is restated here.
    /// Đọc giá trị của một cờ ngưỡng gộp và gộp vào <paramref name="current"/>. Số được parse theo
    /// <see cref="CultureInfo.InvariantCulture"/> (CLI luôn nhận "0.6", không nhận "0,6" kiểu vi-VN), và
    /// bộ ba sau khi gộp phải thoả <see cref="MergeThresholds.IsValid"/>; thiếu giá trị, giá trị không
    /// phải số hoặc ngoài dải đều cho message nêu tên cờ đó cùng dải của nó, lấy từ hằng số của
    /// <see cref="MergeThresholds"/> nên không phải nhắc lại dải ở đây.
    /// </summary>
    /// <param name="flag">The flag being read — one of the <see cref="MergeFlags"/> names. Cờ đang đọc, một trong các tên <see cref="MergeFlags"/>.</param>
    /// <param name="args">The full argument array. Toàn bộ mảng đối số.</param>
    /// <param name="index">
    /// Position of <paramref name="flag"/> in <paramref name="args"/>; moved past the value.
    /// Vị trí của <paramref name="flag"/> trong <paramref name="args"/>; được dời qua giá trị.
    /// </param>
    /// <param name="current">Thresholds collected so far. Ngưỡng đã thu thập tới lúc này.</param>
    /// <param name="updated">
    /// The triple to keep on success; <paramref name="current"/> unchanged when the call fails.
    /// Bộ ba giữ lại khi thành công; vẫn là <paramref name="current"/> khi call thất bại.
    /// </param>
    /// <param name="error">Null on success; otherwise the reason. Không null khi thành công; ngược lại là lý do.</param>
    /// <returns>True when the flag carries an in-range value. True khi cờ mang giá trị trong dải.</returns>
    internal static bool TryReadMergeThreshold(string flag, string[] args, ref int index,
        MergeThresholds current, out MergeThresholds updated, out string? error)
    {
        updated = current;
        if (index + 1 >= args.Length)
        {
            error = $"missing value for {flag}.";
            return false;
        }
        if (!double.TryParse(args[++index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            error = MergeRangeError(flag);
            return false;
        }

        MergeThresholds candidate = flag switch
        {
            MergeFlags.Gap => current with { GapRatio = value },
            MergeFlags.Height => current with { HeightRatio = value },
            _ => current with { MinOverlapRatio = value },
        };
        if (!candidate.IsValid())
        {
            error = MergeRangeError(flag);
            return false;
        }
        updated = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// "invalid --merge-gap (expected a number 0.1..2.0)." — the range always comes from
    /// <see cref="MergeThresholds"/>, and "0.0##" keeps a whole bound printed as "2.0" instead of "2".
    /// Message luôn lấy dải từ <see cref="MergeThresholds"/>, và "0.0##" giữ nguyên số biên chẵn in ra
    /// "2.0" thay vì "2".
    /// </summary>
    private static string MergeRangeError(string flag)
    {
        (double Low, double High) range = flag switch
        {
            MergeFlags.Gap => (MergeThresholds.GapRatioLow, MergeThresholds.GapRatioHigh),
            MergeFlags.Height => (MergeThresholds.HeightRatioLow, MergeThresholds.HeightRatioHigh),
            _ => (MergeThresholds.OverlapRatioLow, MergeThresholds.OverlapRatioHigh),
        };
        string Invariant(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);
        return $"invalid {flag} (expected a number {Invariant(range.Low)}..{Invariant(range.High)}).";
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
    /// <param name="shmName">
    /// Name of the OCR shared-memory section the frames are published into; the default is
    /// <see cref="OcrShm.Name"/> and tests pass a unique name to stay isolated.
    /// Tên vùng shared memory OCR để ghi khung; mặc định là <see cref="OcrShm.Name"/>,
    /// test truyền tên riêng để không đụng nhau.
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
        Action<string>? onFatal = null,
        string shmName = OcrShm.Name)
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
            // Raw-frame transport: each captured frame is published into the OCR shared-memory
            // section once, and the ocr.spot request only carries {seq, width, height} — the
            // base64 JPEG this loop used to encode per frame (100s of KB over stdio) is gone.
            // The writer is disposed at the end of the run; a reader still open keeps the
            // section (and its last frame) alive.
            // Truyền khung raw: mỗi khung đã chụp được ghi một lần vào vùng shared memory OCR,
            // request ocr.spot chỉ còn mang {seq, width, height} — JPEG base64 mà vòng này từng
            // mã hoá mỗi khung (hàng trăm KB qua stdio) không còn nữa. Writer được dispose khi
            // kết thúc lượt chạy; reader còn mở vẫn giữ section (và khung cuối) sống.
            using var writer = new OcrShmWriter(shmName);
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
                        long frameSeq = writer.Publish(frame);
                        JsonObject result = await pipeline.SpotAsync(frameSeq, frame.Width, frame.Height, ct);
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
