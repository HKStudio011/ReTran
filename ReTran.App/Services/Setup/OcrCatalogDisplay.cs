namespace ReTran_App.Services.Setup;

/// <summary>
/// One selectable OCR model, as shown in the setup UI (display-only).
/// Một model OCR có thể chọn, dùng để hiển thị trong UI cài đặt (chỉ-để-hiển-thị).
/// </summary>
/// <param name="Name">Registry name understood by PaddleOCR (e.g. "PP-OCRv6_small_rec").
/// Tên registry mà PaddleOCR hiểu, ví dụ "PP-OCRv6_small_rec".</param>
/// <param name="Kind">Model slot: "det" (text detection) or "rec" (text recognition).
/// Vị trí model: "det" (phát hiện chữ) hoặc "rec" (nhận diện chữ).</param>
/// <param name="Langs">Lowercase language codes the model covers, in catalog order.
/// Các mã ngôn ngữ (viết thường) model hỗ trợ, đúng thứ tự catalog.</param>
/// <param name="SizeMb">Published inference-model storage size in megabytes.
/// Dung lượng lưu trữ model suy luận đã công bố (MB).</param>
public sealed record OcrModelInfo(string Name, string Kind, IReadOnlyList<string> Langs, float SizeMb);

/// <summary>
/// Display-only C# mirror of the Python OCR catalog (ReTran.OCR/src/retran_core/ocr/catalog.py —
/// the source of truth). The App reads this static copy for the setup UI instead of round-tripping
/// to the sidecar; it must never be edited by hand alone — any change to the Python CATALOG, its
/// order, or the default pair is guarded by the drift test OcrCatalogDisplayTests.DisplayCatalog_MatchesPythonCatalog,
/// which runs the real catalog and asserts field-by-field equality.
/// Bản sao C# chỉ-để-hiển-thị của catalog OCR Python (ReTran.OCR/src/retran_core/ocr/catalog.py —
/// nguồn sự thật). App đọc bản copy tĩnh này cho UI cài đặt thay vì gọi qua sidecar; KHÔNG được sửa
/// tay một mình — mọi thay đổi trong CATALOG Python, thứ tự mục, hay cặp mặc định đều bị chặn bởi test
/// drift OcrCatalogDisplayTests.DisplayCatalog_MatchesPythonCatalog: test chạy catalog thật và so khớp
/// từng trường.
/// </summary>
public static class OcrCatalogDisplay
{
    // Latin-script languages the v6 unified models cover (46) — mirrors _LATIN_LANGS in catalog.py.
    // 46 ngôn ngữ chữ Latin mà model v6 thống nhất hỗ trợ — bản sao của _LATIN_LANGS trong catalog.py.
    private static readonly string[] LatinLangs =
    [
        "af", "az", "bs", "ca", "cs", "cy", "da", "de", "es", "et", "eu", "fi", "fr", "ga",
        "gl", "hr", "hu", "id", "is", "it", "ku", "la", "lb", "lt", "lv", "mi", "ms", "mt",
        "nl", "no", "oc", "pl", "pt", "qu", "rm", "ro", "rs_latin", "sk", "sl", "sq", "sv",
        "sw", "tl", "tr", "uz", "vi",
    ];

    // 50 languages (zh/TC/en/ja + the 46 Latin) for the v6 small/medium tiers — mirrors _V6_LANGS.
    // 50 ngôn ngữ (zh/TC/en/ja + 46 Latin) cho tier small/medium của v6 — bản sao của _V6_LANGS.
    private static readonly string[] V6Langs = ["zh", "zh-hant", "en", "ja", ..LatinLangs];

    // Tiny supports 49: the same coverage without Japanese — mirrors _V6_TINY_LANGS.
    // Tiny hỗ trợ 49 ngôn ngữ: cùng phạm vi nhưng không có tiếng Nhật — bản sao của _V6_TINY_LANGS.
    private static readonly string[] V6TinyLangs = ["zh", "zh-hant", "en", ..LatinLangs];

    // Cyrillic-script languages of cyrillic_PP-OCRv5_mobile_rec (33 + English) — mirrors _CYRILLIC_LANGS.
    // 34 ngôn ngữ chữ Kirin của cyrillic_PP-OCRv5_mobile_rec (33 + tiếng Anh) — bản sao của _CYRILLIC_LANGS.
    private static readonly string[] CyrillicLangs =
    [
        "ru", "be", "uk", "bg", "rs_cyrillic", "mk", "mn", "ab", "ady", "kbd", "av",
        "dar", "inh", "ce", "lki", "lez", "tab", "kk", "ky", "tg", "tt", "cv", "ba", "mhr",
        "mo", "udm", "kv", "os", "bua", "xal", "tyv", "sah", "kaa", "en",
    ];

    // Arabic-script languages of arabic_PP-OCRv5_mobile_rec — mirrors _ARABIC_LANGS.
    // Các ngôn ngữ chữ Ả Rập của arabic_PP-OCRv5_mobile_rec — bản sao của _ARABIC_LANGS.
    private static readonly string[] ArabicLangs = ["ar", "fa", "ug", "ur", "ps", "ku", "sd", "bal", "en"];

    // Devanagari-script languages of devanagari_PP-OCRv5_mobile_rec — mirrors _DEVANAGARI_LANGS.
    // Các ngôn ngữ chữ Devanagari của devanagari_PP-OCRv5_mobile_rec — bản sao của _DEVANAGARI_LANGS.
    private static readonly string[] DevanagariLangs =
    [
        "hi", "mr", "ne", "bh", "mai", "anp", "bho", "mah", "sat", "new", "gom", "sa", "bgc",
        "en",
    ];

    /// <summary>
    /// The full model catalog — exactly the 16 entries of the Python CATALOG, in the same order.
    /// Toàn bộ catalog model — đúng 16 mục của CATALOG Python, cùng thứ tự.
    /// </summary>
    public static IReadOnlyList<OcrModelInfo> Models { get; } = new OcrModelInfo[]
    {
        // --- v6 group: unified PP-OCRv6 tiers (recommended default pair: small) ---
        new("PP-OCRv6_tiny_det", "det", V6TinyLangs, 1.9f),
        new("PP-OCRv6_small_det", "det", V6Langs, 9.6f),
        new("PP-OCRv6_medium_det", "det", V6Langs, 59.4f),
        new("PP-OCRv6_tiny_rec", "rec", V6TinyLangs, 4.4f),
        new("PP-OCRv6_small_rec", "rec", V6Langs, 20.4f),
        new("PP-OCRv6_medium_rec", "rec", V6Langs, 73.3f),
        // --- legacy group: v5/v3 per-language rec fallbacks for scripts v6 lacks.
        // Order matters for routing: cyrillic (general) before eslav (East Slavic subset). ---
        new("korean_PP-OCRv5_mobile_rec", "rec", ["ko", "en"], 14.0f),
        new("cyrillic_PP-OCRv5_mobile_rec", "rec", CyrillicLangs, 7.7f),
        new("arabic_PP-OCRv5_mobile_rec", "rec", ArabicLangs, 7.6f),
        new("th_PP-OCRv5_mobile_rec", "rec", ["th", "en"], 7.5f),
        new("ta_PP-OCRv5_mobile_rec", "rec", ["ta", "en"], 7.5f),
        new("te_PP-OCRv5_mobile_rec", "rec", ["te", "en"], 7.5f),
        new("devanagari_PP-OCRv5_mobile_rec", "rec", DevanagariLangs, 7.5f),
        new("el_PP-OCRv5_mobile_rec", "rec", ["el", "en"], 7.5f),
        new("eslav_PP-OCRv5_mobile_rec", "rec", ["ru", "be", "uk", "en"], 14.0f),
        new("chinese_cht_PP-OCRv3_mobile_rec", "rec", ["zh-hant"], 10.8f),
    };

    /// <summary>
    /// Default det model — mirrors _DEFAULT_DET in catalog.py (v6 small balances accuracy and size).
    /// Model det mặc định — bản sao của _DEFAULT_DET trong catalog.py (v6 small cân bằng độ chính xác
    /// và dung lượng).
    /// </summary>
    public static string DefaultDet { get; } = "PP-OCRv6_small_det";

    /// <summary>
    /// Default rec model — mirrors _DEFAULT_REC in catalog.py.
    /// Model rec mặc định — bản sao của _DEFAULT_REC trong catalog.py.
    /// </summary>
    public static string DefaultRec { get; } = "PP-OCRv6_small_rec";
}
