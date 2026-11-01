using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Config;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;

namespace ReTran.Core.Cli;

/// <summary>
/// <c>ocr</c> CLI subcommands: one-shot PNG spotting and the continuous <c>ocr loop</c> (JSON lines).
/// Các subcommand CLI <c>ocr</c>: nhận diện PNG một lần và vòng lặp liên tục <c>ocr loop</c> (JSON lines).
/// </summary>
public static class OcrCommands
{
    /// <summary>
    /// Registers the <c>ocr</c> command on <paramref name="d"/>.
    /// Đăng ký lệnh <c>ocr</c> vào <paramref name="d"/>.
    /// </summary>
    /// <param name="d">Dispatcher to register on. Dispatcher cần đăng ký.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="d"/> is null.</exception>
    public static void Register(CommandDispatcher d)
    {
        ArgumentNullException.ThrowIfNull(d);
        d.Register("ocr", RunAsync);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length >= 1 && string.Equals(args[0], "loop", StringComparison.OrdinalIgnoreCase))
        {
            return await RunLoopAsync(args[1..]);
        }
        if (!TryParseOneShot(args, out string? input, out bool mergeLines, out string? parseError))
        {
            Console.Error.WriteLine("usage: ReTran.Core ocr <input.png> [--merge-lines]");
            if (parseError is not null)
                Console.Error.WriteLine(parseError);
            return 2;
        }
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"input not found: {input}");
            return 1;
        }
        string? repoRoot = OcrBootstrap.FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("repo root not found (ReTran.OCR/pyproject.toml above cwd).");
            return 1;
        }
        string? python = OcrBootstrap.ResolvePythonExe(CoreConfig.LoadFrom(CoreConfig.DefaultPath), repoRoot);
        if (python is null)
        {
            Console.Error.WriteLine("python not found (set PythonExe in config or run `uv sync` in ReTran.OCR/).");
            return 1;
        }
        try
        {
            await using var sp = await SidecarProcess.StartAsync(python, repoRoot, CancellationToken.None);
            var pipeline = new OcrPipeline(sp, new OcrDiff(), mergeLines);
            string b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(input));
            JsonObject result = await pipeline.SpotAsync(b64);
            Console.Out.WriteLine(result.ToJsonString());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ocr failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Parses one-shot <c>ocr</c> arguments: exactly one input path plus the optional
    /// <c>--merge-lines</c> flag (no value).
    /// Phân tích đối số <c>ocr</c> một lần: đúng một đường dẫn đầu vào kèm cờ tùy chọn
    /// <c>--merge-lines</c> (không có giá trị).
    /// </summary>
    /// <param name="args">Arguments after <c>ocr</c> (the <c>loop</c> form is handled by the caller). Đối số sau <c>ocr</c> (dạng <c>loop</c> do caller xử lý).</param>
    /// <param name="input">The input path on success. Đường dẫn đầu vào khi hợp lệ.</param>
    /// <param name="mergeLines">True when <c>--merge-lines</c> is present. True khi có <c>--merge-lines</c>.</param>
    /// <param name="error">Null on success; otherwise the reason. Không null khi thành công; ngược lại là lý do.</param>
    /// <returns>True when the arguments are valid. True khi đối số hợp lệ.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="args"/> is null. Ném khi <paramref name="args"/> là null.</exception>
    internal static bool TryParseOneShot(string[] args, out string? input, out bool mergeLines, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        input = null;
        mergeLines = false;
        error = null;

        foreach (string arg in args)
        {
            if (arg == "--merge-lines")
            {
                mergeLines = true;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unknown argument '{arg}'.";
                return false;
            }
            else if (input is null)
            {
                input = arg;
            }
            else
            {
                error = $"unexpected argument '{arg}' (one input only).";
                return false;
            }
        }

        if (input is null)
        {
            error = "missing <input.png>.";
            return false;
        }
        return true;
    }

    private static async Task<int> RunLoopAsync(string[] args)
    {
        CoreConfig config = CoreConfig.LoadFrom(CoreConfig.DefaultPath);
        OcrLoopOptions? options = OcrLoop.TryParse(args, config.OcrFps, out string? parseError);
        if (options is null)
        {
            Console.Error.WriteLine(OcrLoop.Usage);
            if (parseError is not null)
                Console.Error.WriteLine(parseError);
            return 2;
        }

        // Validate the source BEFORE spawning the sidecar (fast exit 2 for bad --monitor).
        ICaptureSource source;
        try
        {
            source = OcrLoop.CreateSource(options);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.Error.WriteLine($"invalid source: {ex.Message}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // we shut down in order; do not kill the process
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            string? repoRoot = OcrBootstrap.FindRepoRoot();
            if (repoRoot is null)
            {
                Console.Error.WriteLine("repo root not found (ReTran.OCR/pyproject.toml above cwd).");
                return 1;
            }
            string? python = OcrBootstrap.ResolvePythonExe(config, repoRoot);
            if (python is null)
            {
                Console.Error.WriteLine("python not found (set PythonExe in config or run `uv sync` in ReTran.OCR/).");
                return 1;
            }
            await using var sp = await SidecarProcess.StartAsync(python, repoRoot, cts.Token);
            var pipeline = new OcrPipeline(sp, new OcrDiff(), options.MergeLines);
            return await OcrLoop.RunAsync(options, source, pipeline, new JsonLinesSink(Console.Out), Console.Error, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 0; // Ctrl+C during sidecar spawn -> clean stop (spec §6)
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ocr loop failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            source.Dispose();
        }
    }
}
