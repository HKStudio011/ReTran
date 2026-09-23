using System.Text.Json.Nodes;
using ReTran.Core.Config;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;

namespace ReTran.Core.Cli;

/// <summary>
/// One-shot <c>ocr</c> CLI subcommand: PNG file in, spotted text JSON out.
/// Subcommand CLI <c>ocr</c> một lần: nhận tệp PNG, in JSON text đã nhận diện.
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
}
