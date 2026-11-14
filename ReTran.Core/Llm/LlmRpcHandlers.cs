using System.Text.Json;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;

namespace ReTran.Core.Llm;

/// <summary>
/// Registers the <c>llm.*</c> JSON-RPC verbs on a serve-mode server: model catalog/download/
/// delete/set backed by <see cref="LlmModelManager"/>, and runtime status/check/download backed by
/// <see cref="LlamaRuntimeManager"/>. Downloads are fire-and-forget — the reply only acknowledges
/// acceptance, and callers poll the list/status verbs for progress.
/// Đăng ký các verb JSON-RPC <c>llm.*</c> lên server serve mode: catalog/tải/xoá/chọn model dựa
/// trên <see cref="LlmModelManager"/>, và trạng thái/kiểm tra/tải runtime dựa trên
/// <see cref="LlamaRuntimeManager"/>. Lượt tải là fire-and-forget — phản hồi chỉ xác nhận đã nhận,
/// nơi gọi poll verb list/status để theo dõi tiến độ.
/// </summary>
public static class LlmRpcHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Registers every <c>llm.*</c> handler over the shared managers.
    /// Đăng ký toàn bộ handler <c>llm.*</c> trên hai manager dùng chung.
    /// </summary>
    /// <param name="server">Serve-mode JSON-RPC server. Server JSON-RPC của serve mode.</param>
    /// <param name="models">GGUF model manager (state lives in Core). Manager model GGUF (trạng thái nằm ở Core).</param>
    /// <param name="runtime">llama.cpp runtime manager. Manager runtime llama.cpp.</param>
    public static void Register(JsonRpcServer server, LlmModelManager models, LlamaRuntimeManager runtime)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(runtime);

        server.Register("llm.models.list", _ =>
            Task.FromResult<JsonNode?>(JsonSerializer.SerializeToNode(models.List(), JsonOptions)));

        server.Register("llm.models.download", p =>
        {
            string name = RequiredName(p);
            return Task.FromResult<JsonNode?>(new JsonObject { ["accepted"] = models.StartDownload(name) });
        });

        server.Register("llm.models.delete", p =>
        {
            models.Delete(RequiredName(p));
            return Task.FromResult<JsonNode?>(new JsonObject { ["deleted"] = true });
        });

        server.Register("llm.models.set", p =>
        {
            models.SetActive(RequiredName(p));
            return Task.FromResult<JsonNode?>(new JsonObject { ["active"] = models.ActiveModel });
        });

        server.Register("llm.runtime.status", async _ =>
        {
            LlmRuntimeStatus status = await runtime.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            return JsonSerializer.SerializeToNode(status, JsonOptions);
        });

        server.Register("llm.runtime.check", async _ =>
        {
            (string? current, string? latest) = await runtime.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            return new JsonObject { ["current"] = current, ["latest"] = latest };
        });

        server.Register("llm.runtime.download", p =>
        {
            string flavor = OptionalFlavor(p);
            // Fire-and-forget: the download can take minutes; callers poll llm.runtime.status.
            // Chạy nền: lượt tải có thể mất vài phút; nơi gọi poll llm.runtime.status.
            _ = Task.Run(() => runtime.DownloadAsync(flavor, CancellationToken.None));
            return Task.FromResult<JsonNode?>(new JsonObject { ["accepted"] = true, ["flavor"] = flavor });
        });
    }

    private static string RequiredName(JsonNode? p)
    {
        if (p is not JsonObject obj
            || obj["name"] is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out string? name)
            || string.IsNullOrWhiteSpace(name))
        {
            throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                "missing or invalid 'name' (non-empty catalog model string).");
        }
        return name;
    }

    private static string OptionalFlavor(JsonNode? p)
    {
        if (p is JsonObject obj && obj["flavor"] is JsonValue flavorValue)
        {
            if (!flavorValue.TryGetValue<string>(out string? flavor))
            {
                throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                    "'flavor' must be a string (\"cuda\" or \"cpu\").");
            }
            if (flavor is not ("cuda" or "cpu"))
            {
                throw new RpcMethodException(RpcErrorCodes.InvalidParams,
                    "'flavor' must be \"cuda\" or \"cpu\".");
            }
            return flavor;
        }
        return "cuda";
    }
}
