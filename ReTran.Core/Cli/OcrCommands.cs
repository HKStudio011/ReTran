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
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: ReTran.Core ocr <input.png>");
            return 2;
        }
        if (!File.Exists(args[0]))
        {
            Console.Error.WriteLine($"input not found: {args[0]}");
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
            var pipeline = new OcrPipeline(sp, new OcrDiff());
            string b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(args[0]));
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
            var pipeline = new OcrPipeline(sp, new OcrDiff());
            return await OcrLoop.RunAsync(options, source, pipeline, Console.Out, Console.Error, cts.Token);
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
