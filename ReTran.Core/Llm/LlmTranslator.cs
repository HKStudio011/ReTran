using ReTran.Core.Translate;

namespace ReTran.Core.Llm;

/// <summary>
/// Translation provider backed by an OpenAI-compatible chat endpoint (remote API or the locally
/// managed llama.cpp server). One request per text — batching stays per-text in v1 to protect
/// segment alignment (spec 2026-11-14 D4). When <paramref name="endpointProvider"/> is given
/// (local mode), the base URL is resolved lazily on each call so the llama-server can start on
/// first use without blocking <c>core.start</c>.
/// Nhà cung cấp dịch dùng endpoint chat chuẩn OpenAI (API remote hoặc server llama.cpp quản lý cục
/// bộ). Mỗi text một request — batching giữ per-text ở v1 để bảo toàn khớp segment (spec
/// 2026-11-14 D4). Khi có <paramref name="endpointProvider"/> (chế độ local), base URL được giải
/// quyết muộn ở mỗi lần gọi để llama-server khởi động ở lần dùng đầu mà không chặn
/// <c>core.start</c>.
/// </summary>
/// <param name="client">Chat client used for every request. Chat client dùng cho mọi yêu cầu.</param>
/// <param name="settings">Resolved session settings. Cài đặt phiên đã chốt.</param>
/// <param name="endpointProvider">Optional lazy base-URL resolver (local server host). Hàm giải base URL muộn tuỳ chọn (server host cục bộ).</param>
public sealed class LlmTranslator(
    OpenAiChatClient client,
    LlmSettings settings,
    Func<CancellationToken, Task<string>>? endpointProvider = null) : ITranslator
{
    private readonly OpenAiChatClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <inheritdoc/>
    public string Name => "llm";

    /// <summary>
    /// Translates one text through the chat endpoint. Dịch một text qua endpoint chat.
    /// </summary>
    /// <param name="text">Source text to translate. Văn bản nguồn cần dịch.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="ct">Token used to cancel the request. Token dùng để hủy yêu cầu.</param>
    /// <returns>The translated text. Văn bản đã dịch.</returns>
    /// <exception cref="InvalidOperationException">Thrown when neither a base URL nor an endpoint provider is configured, or the model is missing. Khi không có base URL lẫn endpoint provider, hoặc thiếu model.</exception>
    public async Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct = default)
    {
        string baseUrl = _settings.BaseUrl
            ?? (endpointProvider is not null
                ? await endpointProvider(ct).ConfigureAwait(false)
                : throw new InvalidOperationException("LLM base URL is not configured."));
        string model = _settings.Model
            ?? throw new InvalidOperationException("LLM model is not configured.");
        string prompt = PromptTemplate.Apply(_settings.EffectiveUser, src, dst, text);
        return await _client.CompleteAsync(
            baseUrl, _settings.ApiKey, model, _settings.EffectiveSystem, prompt, _settings.IsLocal, ct)
            .ConfigureAwait(false);
    }
}
