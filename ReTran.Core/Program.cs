using System.Text;
using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.JsonRpc;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;

namespace ReTran.Core;

/// <summary>
/// Entry point: `--serve` runs the long-running JSON-RPC peer; a subcommand runs one-shot.
/// Điểm vào: `--serve` chạy peer JSON-RPC dài hạn; subcommand chạy một lần rồi thoát.
/// </summary>
public static class Program
{
    /// <summary>App version reported by core.version and the CLI.</summary>
    public const string Version = "0.1.0";

    /// <summary>Main entry: dispatches to --serve mode or a one-shot subcommand.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "--serve" || args[0] == "serve")
            return await RunServeAsync();

        var dispatcher = new CommandDispatcher();
        dispatcher.Register("version", _ => Task.FromResult(WriteStdout(Version)));
        CaptureCommands.Register(dispatcher);
        OcrCommands.Register(dispatcher);
        // translate handler lands in M3.
        return await dispatcher.DispatchAsync(args);
    }

    private static int WriteStdout(string s) { Console.Out.Write(s + "\n"); return 0; }

    /// <summary>
    /// Registers core method ocr.spot: validates {image} (-32602) and delegates to the pipeline (-32603 when unavailable).
    /// Đăng ký phương thức core ocr.spot: kiểm tra {image} (-32602) và ủy quyền cho pipeline (-32603 khi không sẵn sàng).
    /// </summary>
    /// <param name="server">Server to register on. Server cần đăng ký.</param>
    /// <param name="pipeline">Pipeline to sidecar, or null when the sidecar could not start. Pipeline tới sidecar, hoặc null khi chưa khởi tạo được.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="server"/> is null.</exception>
    public static void RegisterOcrMethod(JsonRpcServer server, OcrPipeline? pipeline)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Register("ocr.spot", async p =>
        {
            if (p?["image"] is not JsonValue jv || !jv.TryGetValue<string>(out var image) || image.Length == 0)
                throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                    "missing or empty 'image' (non-empty base64 PNG string).");
            if (pipeline is null)
                throw new RpcMethodException(RpcErrorCodes.InternalError,
                    "sidecar not available (python/venv missing or spawn failed).");
            return await pipeline.SpotAsync(image);
        });
    }

    /// <summary>Runs the long-running JSON-RPC peer on stdio (spawns the OCR sidecar when possible).</summary>
    private static async Task<int> RunServeAsync()
    {
        var server = new JsonRpcServer();
        server.Register("core.ping", _ => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));
        server.Register("core.version", _ => Task.FromResult<JsonNode?>(JsonValue.Create(Version)));

        SidecarProcess? rpc = null;
        string? repoRoot = OcrBootstrap.FindRepoRoot();
        if (repoRoot is not null)
        {
            string? python = OcrBootstrap.ResolvePythonExe(Config.CoreConfig.LoadFrom(Config.CoreConfig.DefaultPath), repoRoot);
            if (python is not null)
            {
                try { rpc = await SidecarProcess.StartAsync(python, repoRoot, CancellationToken.None); }
                catch (Exception ex) { Console.Error.WriteLine($"sidecar spawn failed: {ex.Message}"); }
            }
        }
        RegisterOcrMethod(server, rpc is null ? null : new OcrPipeline(rpc, new OcrDiff()));

        var transport = new LineJsonRpcTransport(
            new StreamReader(Console.OpenStandardInput()),
            new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n" });
        await server.RunAsync(transport, CancellationToken.None);
        if (rpc is not null) await rpc.DisposeAsync();
        return 0;
    }
}
