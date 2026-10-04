using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ReTran_App.Services;

/// <summary>
/// Provides localized UI strings (vi/en) for the control app and raises <see cref="Changed"/> on language switches.
/// Cung cấp chuỗi UI đã dịch (vi/en) cho app điều khiển và phát <see cref="Changed"/> khi đổi ngôn ngữ.
/// </summary>
public sealed class I18nService
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new()
    {
        ["vi"] = I18nStrings.Vi,
        ["en"] = I18nStrings.En,
    };

    private readonly ILogger<I18nService>? _logger;
    private readonly HashSet<string> _warnedKeys = new();

    /// <summary>
    /// Initializes the service, detecting the default language from the OS UI culture.
    /// Khởi tạo service, phát hiện ngôn ngữ mặc định từ UI culture của OS.
    /// </summary>
    /// <param name="logger">Optional logger for missing-key warnings. Logger tùy chọn cho cảnh báo key thiếu.</param>
    public I18nService(ILogger<I18nService>? logger = null) => _logger = logger;

    /// <summary>Current language code, always "vi" or "en". Mã ngôn ngữ hiện tại, luôn "vi" hoặc "en".</summary>
    public string Lang { get; private set; } = DetectDefault();

    /// <summary>Raised after <see cref="Set"/> changes the language. Được phát sau khi <see cref="Set"/> đổi ngôn ngữ.</summary>
    public event Action? Changed;

    /// <summary>All keys defined in either table (union of vi/en). Toàn bộ key có trong bảng (vi ∪ en).</summary>
    public IEnumerable<string> Keys => I18nStrings.Vi.Keys.Union(I18nStrings.En.Keys);

    /// <summary>
    /// Looks up the string for <paramref name="key"/> in the current language.
    /// Tra chuỗi của <paramref name="key"/> theo ngôn ngữ hiện tại.
    /// </summary>
    /// <param name="key">Dotted string id. Id chuỗi dạng chấm.</param>
    /// <returns>
    /// The localized string, or <paramref name="key"/> itself when missing (logged once per key).
    /// Chuỗi đã dịch, hoặc chính <paramref name="key"/> khi thiếu (log mỗi key một lần).
    /// </returns>
    public string this[string key] => Lookup(key);

    /// <summary>
    /// Formats the current-language string for <paramref name="key"/> with the given arguments.
    /// Định dạng chuỗi ngôn ngữ hiện tại của <paramref name="key"/> với các tham số cho trước.
    /// </summary>
    /// <param name="key">Dotted string id. Id chuỗi dạng chấm.</param>
    /// <param name="args">Values substituted into the {0} placeholders. Giá trị thay vào chỗ {0}.</param>
    /// <returns>The formatted string. Chuỗi sau định dạng.</returns>
    public string Fmt(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Lookup(key), args);

    /// <summary>
    /// Switches the language and raises <see cref="Changed"/>; values other than "vi"/"en" are ignored.
    /// Đổi ngôn ngữ và phát <see cref="Changed"/>; giá trị khác "vi"/"en" bị bỏ qua.
    /// </summary>
    /// <param name="lang">Target language code. Mã ngôn ngữ đích.</param>
    public void Set(string lang)
    {
        if (lang is not ("vi" or "en") || lang == Lang) return;
        Lang = lang;
        Changed?.Invoke();
    }

    private static string DetectDefault() =>
        CultureInfo.CurrentUICulture.Name.StartsWith("vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";

    private string Lookup(string key)
    {
        if (Tables[Lang].TryGetValue(key, out var value)) return value;
        if (_warnedKeys.Add(key)) _logger?.LogWarning("Missing i18n key {Key} for language {Lang}", key, Lang);
        return key;
    }
}
