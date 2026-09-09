using System.Text;
using System.Text.Json.Nodes;
using ReTran.Core.Cli;
using ReTran.Core.JsonRpc;

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
        // ocr / translate / capture handlers land in M1–M3.
        return await dispatcher.DispatchAsync(args);
    }

    private static int WriteStdout(string s) { Console.Out.Write(s + "\n"); return 0; }

    /// <summary>Runs the long-running JSON-RPC peer on stdio.</summary>
    private static async Task<int> RunServeAsync()
    {
        var server = new JsonRpcServer();
        server.Register("core.ping", _ => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));
        server.Register("core.version", _ => Task.FromResult<JsonNode?>(JsonValue.Create(Version)));
        var transport = new LineJsonRpcTransport(
            new StreamReader(Console.OpenStandardInput()),
            new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n" });
        await server.RunAsync(transport, CancellationToken.None);
        return 0;
    }
}
