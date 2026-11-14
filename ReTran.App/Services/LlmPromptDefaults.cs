namespace ReTran_App.Services;

/// <summary>
/// App-side copies of the LLM prompt defaults — a mirror of Core's
/// <c>ReTran.Core.Llm.PromptTemplate</c> (the App cannot reference Core; it spawns it as a
/// separate process). <c>PromptDefaultsSyncTests</c> pins the pair byte-equal.
/// Bản sao phía App của prompt mặc định LLM — gương của <c>ReTran.Core.Llm.PromptTemplate</c> phía
/// Core (App không tham chiếu được Core; nó spawn Core như process riêng).
/// <c>PromptDefaultsSyncTests</c> ghim hai bên giống nhau từng byte.
/// </summary>
public static class LlmPromptDefaults
{
    /// <summary>Empty default system prompt — Hy-MT2 model card: no default system prompt. System prompt mặc định rỗng — model card Hy-MT2: không có system prompt mặc định.</summary>
    public const string SystemPrompt = "";

    /// <summary>Default user prompt, verbatim from the Hy-MT2 model card (spec 2026-11-14 §0). Prompt người dùng mặc định, nguyên văn từ model card Hy-MT2 (spec 2026-11-14 §0).</summary>
    public const string UserPrompt =
        "Translate the following text into {target_lang}. Note that you should only output the translated result without any additional explanation:\n\n{source_text}";
}
