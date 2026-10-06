namespace ReTran.Core.Translate;

/// <summary>
/// Registry of translation providers, keyed by <see cref="ITranslator.Name"/> (case-insensitive).
/// Registry các nhà cung cấp dịch thuật, khóa theo <see cref="ITranslator.Name"/> (không phân biệt hoa thường).
/// </summary>
public sealed class TranslatorRegistry
{
    private readonly Dictionary<string, ITranslator> _translators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a translator under its name, replacing any previous registration with the same name.
    /// Đăng ký trình dịch theo tên của nó, ghi đè đăng ký trùng tên trước đó.
    /// </summary>
    /// <param name="t">Translator to register. Trình dịch cần đăng ký.</param>
    /// <exception cref="ArgumentNullException"><paramref name="t"/> is <see langword="null"/>. <paramref name="t"/> là <see langword="null"/>.</exception>
    public void Register(ITranslator t)
    {
        ArgumentNullException.ThrowIfNull(t);
        _translators[t.Name] = t;
    }

    /// <summary>
    /// Looks up a translator by name (case-insensitive).
    /// Tra cứu trình dịch theo tên (không phân biệt hoa thường).
    /// </summary>
    /// <param name="name">Provider name, e.g. <c>google</c>. Tên nhà cung cấp, ví dụ <c>google</c>.</param>
    /// <returns>The registered translator, or <see langword="null"/> when not found. Trình dịch đã đăng ký, hoặc <see langword="null"/> nếu không thấy.</returns>
    public ITranslator? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _translators.GetValueOrDefault(name);
    }
}
