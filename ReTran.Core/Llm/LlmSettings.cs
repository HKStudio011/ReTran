namespace ReTran.Core.Llm;

/// <summary>
/// Translation settings of one LLM session, resolved from <c>core.start</c>'s
/// <c>translate.llm</c> object. Absolute values only — language pair and cache stay with the sink.
/// Cài đặt dịch của một phiên LLM, được chốt từ object <c>translate.llm</c> của <c>core.start</c>.
/// Chỉ chứa giá trị tuyệt đối — cặp ngôn ngữ và cache vẫn thuộc sink.
/// </summary>
/// <param name="Mode"><c>local</c> (llama.cpp GGUF) or <c>api</c> (OpenAI-compatible endpoint). <c>local</c> (llama.cpp GGUF) hoặc <c>api</c> (endpoint OpenAI-compatible).</param>
/// <param name="BaseUrl">Endpoint base URL; for local it is supplied lazily by the server host. URL gốc endpoint; với local do server host cấp muộn.</param>
/// <param name="ApiKey">Bearer key, or <see langword="null"/> when the endpoint needs none. Bearer key, hoặc <see langword="null"/> khi endpoint không cần.</param>
/// <param name="Model">Model name: API model id or installed catalog name for local. Tên model: id của API hoặc tên catalog đã cài với local.</param>
/// <param name="SystemPrompt">User system prompt override; <see langword="null"/> keeps the Hy-MT2 default (empty). System prompt người dùng ghi đè; <see langword="null"/> giữ mặc định Hy-MT2 (rỗng).</param>
/// <param name="UserPrompt">User prompt template override; <see langword="null"/> keeps the Hy-MT2 default. Template user prompt ghi đè; <see langword="null"/> giữ mặc định Hy-MT2.</param>
public sealed record LlmSettings(
    string Mode, string? BaseUrl, string? ApiKey, string? Model, string? SystemPrompt, string? UserPrompt)
{
    /// <summary>Whether this session talks to the locally managed llama.cpp server. Phiên này có nói chuyện với server llama.cpp quản lý cục bộ không.</summary>
    public bool IsLocal => string.Equals(Mode, "local", StringComparison.OrdinalIgnoreCase);

    /// <summary>The system prompt actually sent: the override, or the model-card default. System prompt thực gửi: bản ghi đè, hoặc mặc định theo model card.</summary>
    public string EffectiveSystem => SystemPrompt ?? PromptTemplate.DefaultSystemPrompt;

    /// <summary>The user prompt template actually used: the override, or the model-card default. Template user prompt thực dùng: bản ghi đè, hoặc mặc định theo model card.</summary>
    public string EffectiveUser => UserPrompt ?? PromptTemplate.DefaultUserPrompt;
}
