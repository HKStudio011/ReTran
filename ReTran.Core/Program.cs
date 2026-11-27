using System.Text;
using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.JsonRpc;
using ReTran.Core.Llm;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using ReTran.Core.Serve;
using ReTran.Core.Translate;

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
        TranslateCommands.Register(dispatcher);
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
                    "missing or empty 'image' (non-empty base64 PNG/JPEG string).");
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
        // No eager warmup here: warming with a dummy spot before any pair is chosen would
        // make the engine auto-download models (spec C10). PipelineHost.StartAsync fires
        // the warmup after a successful core.start (and its ocr.models.set) instead.
        // Không warmup sớm ở đây: warm bằng spot dummy trước khi chọn cặp sẽ khiến engine
        // tự tải model (spec C10). PipelineHost.StartAsync bắn warmup sau một core.start
        // thành công (và sau ocr.models.set của nó).
        RegisterOcrMethod(server, rpc is null ? null : new OcrPipeline(rpc, new OcrDiff()));

        var registry = new TranslatorRegistry();
        registry.Register(new GoogleWebTranslator());
        Config.CoreConfig config = Config.CoreConfig.LoadFrom(Config.CoreConfig.DefaultPath);

        // LLM provider (spec 2026-11-14): the managers live in Core — the setup wizard and the
        // Settings UI drive them through the llm.* RPCs; one lazy llama-server serves the active
        // model for this process and is killed on exit. Runtime binaries are downloaded by
        // setup / Settings, never shipped in the release zip.
        // Provider LLM (spec 2026-11-14): manager sống ở Core — setup wizard và UI Settings điều
        // khiển qua RPC llm.*; một llama-server lazy phục vụ model đang dùng cho process này và bị
        // kill khi thoát. Binary runtime do setup/Settings tải, không bao giờ nằm trong zip release.
        string llmRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTran", "llm");
        var llmModels = new LlmModelManager(
            Path.Combine(llmRoot, "models"), Path.Combine(llmRoot, "models.json"));
        var llmRuntime = new LlamaRuntimeManager(
            Path.Combine(llmRoot, "runtime"), HttpUa.CreateClient(), errorWriter: Console.Error);
        var llmServers = new LlamaServerSlot();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => llmServers.Stop();

        ITranslator LlmFactory(LlmSettings llmSettings)
        {
            // The .NET default HttpClient timeout (100 s) would fire before OpenAiChatClient's own
            // CancelAfter (300 s); pin the client to infinite so the constant is the only cap.
            // Timeout mặc định 100 s của HttpClient sẽ chặn trước CancelAfter (300 s) của
            // OpenAiChatClient; đặt client về vô hạn để hằng số là trần duy nhất.
            var chat = new OpenAiChatClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            if (!llmSettings.IsLocal)
            {
                llmServers.Stop(); // local → API must release llama.cpp/VRAM immediately
                return new LlmTranslator(chat, llmSettings);
            }

            string modelName = llmSettings.Model!;
            ILlamaServerHost serverRef = LlmServerRpc.ResolveHost(modelName, llmModels, llmRuntime, llmServers);
            return new LlmTranslator(chat, llmSettings, async ct =>
            {
                await llmServers.EnsureStartedAsync(serverRef, ct).ConfigureAwait(false);
                return serverRef.BaseUrl;
            });
        }

        await using var host = new PipelineHost(rpc, registry, server, Console.Error,
            sourceFactory: null, annotator: new FrameAnnotator(), annotatorFps: config.CaptureFps,
            llmFactory: LlmFactory);
        server.Register("core.start", p => host.StartAsync(p, CancellationToken.None));
        server.Register("core.stop", async p =>
        {
            try
            {
                return await host.StopAsync(p, CancellationToken.None);
            }
            finally
            {
                llmServers.Stop();
            }
        });
        server.Register("core.list_sources", _ => host.ListSourcesAsync(CancellationToken.None));
        // ocr.models.* proxies: the App talks model management to the sidecar through the
        // core, keeping a single JSON-RPC channel.
        // Proxy ocr.models.*: App nói chuyện quản lý model với sidecar qua core, giữ
        // đúng một kênh JSON-RPC.
        server.Register("ocr.models.list", p => host.ListOcrModelsAsync(p, CancellationToken.None));
        server.Register("ocr.models.download", p => host.DownloadOcrModelAsync(p, CancellationToken.None));
        server.Register("ocr.models.delete", p => host.DeleteOcrModelAsync(p, CancellationToken.None));
        server.Register("ocr.models.set", p => host.SetOcrModelPairAsync(p, CancellationToken.None));
        server.Register("ocr.models.recommend", p => host.RecommendOcrModelsAsync(p, CancellationToken.None));
        // sidecar.* proxies: the setup wizard pings the sidecar and probes its device
        // capability through the core; without these registrations both verbs answer -32601
        // and the VerifyingSidecar step can never pass.
        // Proxy sidecar.*: wizard thiết lập ping sidecar và dò capability thiết bị qua core;
        // thiếu hai registration này cả hai verb trả -32601 và bước VerifyingSidecar không
        // bao giờ qua được.
        server.Register("sidecar.ping", p => host.SidecarPingAsync(p, CancellationToken.None));
        server.Register("sidecar.device", p => host.SidecarDeviceAsync(p, CancellationToken.None));
        // llm.*: runtime + model của provider LLM, quản lý phía Core — setup wizard và Settings
        // dùng chung một đường RPC với các verb khác.
        LlmRpcHandlers.Register(server, llmModels, llmRuntime);
        LlmServerRpc.Register(server, llmModels, llmRuntime, llmServers);
        // ocr.config.* proxies: the Settings UI reads the engine-parameter schema and saves
        // values through the core; the core forces deferReload from the session state.
        // Proxy ocr.config.*: UI Settings đọc schema tham số engine và lưu giá trị qua core;
        // core ép deferReload theo trạng thái phiên.
        server.Register("ocr.config.schema", _ => host.OcrConfigSchemaAsync(CancellationToken.None));
        server.Register("ocr.config.set", p => host.SetOcrConfigAsync(p, CancellationToken.None));

        var transport = new LineJsonRpcTransport(
            new StreamReader(Console.OpenStandardInput()),
            new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n" });
        await server.RunAsync(transport, CancellationToken.None);
        await host.DisposeAsync(); // stop the pipeline while the sidecar is still alive
        llmServers.Stop();
        if (rpc is not null) await rpc.DisposeAsync();
        return 0;
    }
}
