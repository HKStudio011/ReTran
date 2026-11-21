using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;

namespace ReTran.Core.Llm;

/// <summary>
/// Resolved executable and GGUF paths for one installed local model.
/// Đường dẫn executable và GGUF đã resolve cho một model local đã cài.
/// </summary>
internal sealed record LocalLlamaPaths(string ModelName, string ServerExe, string ModelPath);

/// <summary>
/// Registers non-blocking local llama-server lifecycle RPCs and shares model/runtime validation
/// with the translator factory.
/// Đăng ký RPC vòng đờ llama-server local không chặn và dùng chung kiểm tra model/runtime với
/// translator factory.
/// </summary>
internal static class LlmServerRpc
{
    /// <summary>
    /// Registers <c>llm.server.start</c>, <c>llm.server.stop</c> and <c>llm.server.status</c>.
    /// Đăng ký <c>llm.server.start</c>, <c>llm.server.stop</c> và <c>llm.server.status</c>.
    /// </summary>
    internal static void Register(
        JsonRpcServer server, LlmModelManager models, LlamaRuntimeManager runtime, LlamaServerSlot slot)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(slot);

        server.Register("llm.server.start", p =>
        {
            if (p?["name"] is not JsonValue value
                || !value.TryGetValue(out string? name)
                || string.IsNullOrWhiteSpace(name))
            {
                throw new RpcMethodException(RpcErrorCodes.InvalidParams, "missing or empty 'name'.");
            }

            LocalLlamaPaths paths = ResolvePaths(name, models, runtime);
            long attempt = slot.Start(paths.ModelName, paths.ServerExe, paths.ModelPath);
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["accepted"] = true,
                ["attempt"] = attempt,
                ["model"] = paths.ModelName,
            });
        });
        server.Register("llm.server.stop", _ =>
        {
            slot.Stop();
            return Task.FromResult<JsonNode?>(StatusJson(slot.Status));
        });
        server.Register("llm.server.status", _ =>
            Task.FromResult<JsonNode?>(StatusJson(slot.Status)));
    }

    /// <summary>
    /// Resolves or creates the translator's host using the same validation as the prewarm RPC.
    /// Resolve hoặc tạo host cho translator bằng cùng đường kiểm tra với RPC prewarm.
    /// </summary>
    internal static ILlamaServerHost ResolveHost(
        string modelName, LlmModelManager models, LlamaRuntimeManager runtime, LlamaServerSlot slot)
    {
        LocalLlamaPaths paths = ResolvePaths(modelName, models, runtime);
        return slot.GetOrCreate(paths.ModelName, paths.ServerExe, paths.ModelPath);
    }

    private static LocalLlamaPaths ResolvePaths(
        string modelName, LlmModelManager models, LlamaRuntimeManager runtime)
    {
        string modelPath = models.ModelPath(modelName);
        if (!File.Exists(modelPath))
            throw new RpcMethodException(RpcErrorCodes.InternalError, $"llm-model-missing: {modelName}");
        LlmRuntimeStatus status = runtime.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!status.Installed || status.ServerPath is null)
        {
            throw new RpcMethodException(RpcErrorCodes.InternalError,
                "llm-runtime-missing: download llama.cpp from Setup or Settings.");
        }
        return new LocalLlamaPaths(modelName, status.ServerPath, modelPath);
    }

    private static JsonObject StatusJson(LlamaServerStatus status) => new()
    {
        ["state"] = status.State,
        ["model"] = status.Model,
        ["error"] = status.Error,
        ["attempt"] = status.Attempt,
        ["phase"] = status.Phase,
        ["bytesLoaded"] = status.BytesLoaded,
        ["bytesTotal"] = status.BytesTotal,
        ["bytesPerSec"] = status.BytesPerSec,
        ["progressAvailable"] = status.ProgressAvailable,
    };
}
