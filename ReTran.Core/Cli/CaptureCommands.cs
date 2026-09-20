using System.Globalization;
using ReTran.Core.Cli;

namespace ReTran.Core.Capture;

/// <summary>
/// One-shot <c>capture</c> CLI subcommands (screen / window / list-monitors / list-windows).
/// Các subcommand CLI <c>capture</c> chạy một lần (screen / window / list-monitors / list-windows).
/// </summary>
public static class CaptureCommands
{
    /// <summary>
    /// Timeout waiting for the first frame before giving up (HDR displays skip frames by design, so this can expire there).
    /// Thời gian tối đa chờ frame đầu tiên trước khi bỏ cuộc (màn hình HDR bỏ qua frame theo thiết kế nên có thể hết timeout ở đó).
    /// </summary>
    public const int FirstFrameTimeoutSeconds = 5;

    /// <summary>
    /// Registers the <c>capture</c> command on <paramref name="d"/> (subcommands: screen, window, list-monitors, list-windows).
    /// Đăng ký lệnh <c>capture</c> vào <paramref name="d"/> (subcommand: screen, window, list-monitors, list-windows).
    /// </summary>
    /// <param name="d">Dispatcher to register on. Dispatcher cần đăng ký.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="d"/> is null.</exception>
    public static void Register(CommandDispatcher d)
    {
        ArgumentNullException.ThrowIfNull(d);
        d.Register("capture", args => Task.FromResult(Dispatch(args)));
    }

    /// <summary>
    /// Parses an HWND from <c>0x...</c> hex or decimal text (e.g. "0x1A2B" or "6699").
    /// Phân tích HWND từ chuỗi hex <c>0x...</c> hoặc thập phân (ví dụ "0x1A2B" hay "6699").
    /// </summary>
    /// <param name="s">Text to parse. Chuỗi cần phân tích.</param>
    /// <returns>The parsed window handle. Handle cửa sổ đã phân tích.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="s"/> is null.</exception>
    /// <exception cref="FormatException">Thrown when <paramref name="s"/> is neither 0x-hex nor decimal.</exception>
    public static nint ParseHwnd(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string text = s.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = text[2..];
            if (hex.Length == 0 || !long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv))
                throw new FormatException($"Invalid HWND value '{s}'. Expected 0xHEX or decimal.");
            return (nint)hv;
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dv))
            return (nint)dv;
        throw new FormatException($"Invalid HWND value '{s}'. Expected 0xHEX or decimal.");
    }

    private static int Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }
        return args[0].ToLowerInvariant() switch
        {
            "screen" => CaptureScreen(args[1..]),
            "window" => CaptureWindow(args[1..]),
            "list-monitors" => ListMonitors(),
            "list-windows" => ListWindows(),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string name)
    {
        Console.Error.WriteLine($"unknown capture subcommand '{name}'.");
        Usage();
        return 2;
    }

    private static void Usage() => Console.Error.WriteLine(
        "usage: ReTran.Core capture (screen [--monitor N] [--out PATH] | window --hwnd 0xHEX|--dec [--out PATH] | list-monitors | list-windows)");

    private static int CaptureScreen(string[] args)
    {
        string monitorText = TryGetOption(args, "--monitor", out string? m) && !string.IsNullOrWhiteSpace(m) ? m : "0";
        if (!int.TryParse(monitorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int monitor))
        {
            Console.Error.WriteLine($"invalid --monitor '{monitorText}'. Expected a decimal index.");
            return 2;
        }
        if (!TryGetOption(args, "--out", out string? outPath) || string.IsNullOrWhiteSpace(outPath))
            outPath = Path.Combine(Directory.GetCurrentDirectory(), "capture-screen.png");

        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = ScreenCaptureSource.EnumerateMonitors();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"list monitors failed: {ex.Message}");
            return 1;
        }
        if (monitor < 0 || monitor >= monitors.Count)
        {
            Console.Error.WriteLine($"monitor index {monitor} out of range (0..{monitors.Count - 1}).");
            return 2;
        }

        using var source = new ScreenCaptureSource(new ScreenTarget(monitor), logger: null);
        Frame? frame = WaitForFirstFrame(source);
        if (frame is null)
        {
            Console.Error.WriteLine(
                $"timed out after {FirstFrameTimeoutSeconds}s waiting for the first frame on monitor {monitor} " +
                "(on HDR displays the DXGI source skips non-BGRA frames by design — try an SDR monitor or 'capture window').");
            return 1;
        }
        using (frame)
            PngWriter.WriteBgra(outPath, frame);
        Console.Out.WriteLine(outPath);
        return 0;
    }

    private static int CaptureWindow(string[] args)
    {
        if (!TryGetOption(args, "--hwnd", out string? hwndText) || string.IsNullOrWhiteSpace(hwndText))
        {
            Console.Error.WriteLine("missing required --hwnd 0xHEX|--dec.");
            Usage();
            return 2;
        }
        nint hwnd;
        try
        {
            hwnd = ParseHwnd(hwndText);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (!TryGetOption(args, "--out", out string? outPath) || string.IsNullOrWhiteSpace(outPath))
            outPath = Path.Combine(Directory.GetCurrentDirectory(), "capture-window.png");

        using var source = new WindowCaptureSource(new WindowTarget(hwnd), logger: null);
        Frame? frame = WaitForFirstFrame(source);
        if (frame is null)
        {
            Console.Error.WriteLine($"timed out after {FirstFrameTimeoutSeconds}s waiting for the first frame on HWND {hwnd}.");
            return 1;
        }
        using (frame)
            PngWriter.WriteBgra(outPath, frame);
        Console.Out.WriteLine(outPath);
        return 0;
    }

    private static int ListMonitors()
    {
        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = ScreenCaptureSource.EnumerateMonitors();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"list monitors failed: {ex.Message}");
            return 1;
        }
        foreach (var m in monitors)
            Console.Out.WriteLine($"{m.Index} {m.DeviceName} {m.Width}x{m.Height} primary={m.IsPrimary}");
        return 0;
    }

    private static int ListWindows()
    {
        IReadOnlyList<WindowInfo> windows;
        try
        {
            windows = WindowCaptureSource.EnumerateWindows();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"list windows failed: {ex.Message}");
            return 1;
        }
        foreach (var w in windows)
            Console.Out.WriteLine($"0x{w.Hwnd:X} \"{w.Title}\" {w.Width}x{w.Height}");
        return 0;
    }

    private static Frame? WaitForFirstFrame(ICaptureSource source)
    {
        using var buffer = new LatestFrameBuffer();
        source.FrameArrived += (_, e) => buffer.Push(e.Frame);
        source.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(FirstFrameTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (buffer.TryTakeLatest(out Frame? frame))
                    return frame;
                Thread.Sleep(50);
            }
            return null;
        }
        finally
        {
            source.Stop();
        }
    }

    private static bool TryGetOption(string[] args, string name, out string? value)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
                return true;
            }
        }
        value = null;
        return false;
    }
}
