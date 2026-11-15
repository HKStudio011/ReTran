namespace ReTran_App.Services.Setup;

/// <summary>
/// A localizable wizard message: an i18n key plus format args, resolved by the UI at render time so
/// the same failure or notice renders in the current UI language (VI/EN) instead of a hardcoded
/// string. The coordinator and <see cref="SystemCheck"/> are pure state machines and only say WHICH
/// message happened and with what data; <c>SetupModal</c> formats it through <c>I18nService</c>.
/// Thông điệp wizard bản địa hoá được: key i18n + tham số format, do UI resolve lúc render để cùng
/// một lỗi/notice hiện đúng ngôn ngữ UI hiện hành (VI/EN) thay vì chuỗi cứng. Coordinator và
/// <see cref="SystemCheck"/> là máy trạng thái thuần, chỉ nói THÔNG ĐIỆP NÀO xảy ra với dữ liệu gì;
/// <c>SetupModal</c> format qua <c>I18nService</c>.
/// </summary>
/// <param name="Key">i18n key such as <c>setup.fail.disk</c>. Key i18n như <c>setup.fail.disk</c>.</param>
/// <param name="Args">Format args in <c>string.Format</c> order. Tham số format theo thứ tự <c>string.Format</c>.</param>
public sealed record SetupMessage(string Key, IReadOnlyList<string> Args)
{
    /// <summary>
    /// Creates a message from a key and its format args.
    /// Tạo thông điệp từ key và các tham số format.
    /// </summary>
    /// <param name="key">i18n key. Key i18n.</param>
    /// <param name="args">Format args, already formatted as display strings. Tham số format, đã được format sẵn thành chuỗi hiển thị.</param>
    /// <returns>The message. Thông điệp.</returns>
    public static SetupMessage Of(string key, params string[] args) => new(key, args);
}
