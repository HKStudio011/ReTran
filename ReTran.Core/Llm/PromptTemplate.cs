namespace ReTran.Core.Llm;

/// <summary>
/// Builds the chat prompt an LLM translator sends: fills the <c>{source_text}</c>,
/// <c>{source_lang}</c> and <c>{target_lang}</c> placeholders of a user-editable template. The
/// defaults are verbatim from the Hy-MT2 model card (verified 2026-11-12): the model has no
/// default system prompt, and the user prompt must ask for the translation only.
/// Dựng prompt chat mà trình dịch LLM gửi đi: điền các placeholder <c>{source_text}</c>,
/// <c>{source_lang}</c>, <c>{target_lang}</c> của template người dùng sửa được. Mặc định lấy
/// nguyên văn từ model card Hy-MT2 (kiểm chứng 2026-11-12): model không có system prompt mặc
/// định, và user prompt phải yêu cầu chỉ trả bản dịch.
/// </summary>
public static class PromptTemplate
{
    /// <summary>
    /// Default system prompt: empty — the Hy-MT2 model card states the models have no default
    /// system prompt. System prompt mặc định: rỗng — model card Hy-MT2 nói model không có
    /// system prompt mặc định.
    /// </summary>
    public const string DefaultSystemPrompt = "";

    /// <summary>
    /// Default user prompt, verbatim from the Hy-MT2 model card (do not reword; the language
    /// placeholder is filled with a full English language name). User prompt mặc định, nguyên
    /// văn từ model card Hy-MT2 (không đổi chữ; placeholder ngôn ngữ được điền bằng tên tiếng
    /// Anh đầy đủ).
    /// </summary>
    public const string DefaultUserPrompt =
        "Translate the following text into {target_lang}. Note that you should only output the translated result without any additional explanation:\n\n{source_text}";

    /// <summary>
    /// Fills the template placeholders; the source text is substituted last so placeholder-like
    /// sequences inside the translation input are never re-expanded.
    /// Điền các placeholder của template; text nguồn được thay cuối cùng để chuỗi dạng placeholder
    /// nằm trong nội dung cần dịch không bị thay tiếp.
    /// </summary>
    /// <param name="template">User-editable template. Template người dùng sửa được.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="text">Source text to translate. Văn bản nguồn cần dịch.</param>
    /// <returns>The filled prompt. Prompt đã điền.</returns>
    /// <exception cref="FormatException">Thrown when the template has no <c>{source_text}</c> placeholder. Khi template thiếu placeholder <c>{source_text}</c>.</exception>
    public static string Apply(string template, string src, string dst, string text)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!template.Contains("{source_text}", StringComparison.Ordinal))
        {
            throw new FormatException("Prompt template is missing the {source_text} placeholder.");
        }

        return template
            .Replace("{target_lang}", LanguageNames.For(dst), StringComparison.Ordinal)
            .Replace("{source_lang}", LanguageNames.For(src), StringComparison.Ordinal)
            .Replace("{source_text}", text, StringComparison.Ordinal);
    }
}
