namespace ReTran.Core.Llm;

/// <summary>
/// English display names for language codes, straight from the Hy-MT2 model card's supported list
/// (the model expects full language names in prompts, not codes). Unknown codes pass through
/// unchanged. Tên hiển thị tiếng Anh cho mã ngôn ngữ, lấy từ danh sách ngôn ngữ trong model card
/// Hy-MT2 (model cần tên đầy đủ trong prompt, không phải mã). Mã lạ trả về nguyên trạng.
/// </summary>
public static class LanguageNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "Chinese",
        ["en"] = "English",
        ["fr"] = "French",
        ["pt"] = "Portuguese",
        ["es"] = "Spanish",
        ["ja"] = "Japanese",
        ["tr"] = "Turkish",
        ["ru"] = "Russian",
        ["ar"] = "Arabic",
        ["ko"] = "Korean",
        ["th"] = "Thai",
        ["it"] = "Italian",
        ["de"] = "German",
        ["vi"] = "Vietnamese",
        ["ms"] = "Malay",
        ["id"] = "Indonesian",
        ["tl"] = "Filipino",
        ["hi"] = "Hindi",
        ["zh-Hant"] = "Traditional Chinese",
        ["pl"] = "Polish",
        ["cs"] = "Czech",
        ["nl"] = "Dutch",
        ["km"] = "Khmer",
        ["my"] = "Burmese",
        ["fa"] = "Persian",
        ["gu"] = "Gujarati",
        ["ur"] = "Urdu",
        ["te"] = "Telugu",
        ["mr"] = "Marathi",
        ["he"] = "Hebrew",
        ["bn"] = "Bengali",
        ["ta"] = "Tamil",
        ["uk"] = "Ukrainian",
        ["bo"] = "Tibetan",
        ["kk"] = "Kazakh",
        ["mn"] = "Mongolian",
        ["ug"] = "Uyghur",
        ["yue"] = "Cantonese",
    };

    /// <summary>
    /// Returns the full English name for a language code (e.g. <c>vi</c> → <c>Vietnamese</c>),
    /// falling back to the code itself when it is not in the Hy-MT2 list (e.g. <c>auto</c>).
    /// Trả tên tiếng Anh đầy đủ cho một mã ngôn ngữ (vd <c>vi</c> → <c>Vietnamese</c>), hoặc chính
    /// mã đó khi không có trong danh sách Hy-MT2 (vd <c>auto</c>).
    /// </summary>
    /// <param name="code">Language code, e.g. <c>vi</c> or <c>auto</c>. Mã ngôn ngữ, vd <c>vi</c> hoặc <c>auto</c>.</param>
    /// <returns>The display name, or the code when unknown. Tên hiển thị, hoặc mã khi không rõ.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="code"/> is null or empty. Khi <paramref name="code"/> null hoặc rỗng.</exception>
    public static string For(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return Map.TryGetValue(code, out string? name) ? name : code;
    }
}
