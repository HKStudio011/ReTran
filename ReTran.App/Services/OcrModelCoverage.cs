using ReTran_App.Core;

namespace ReTran_App.Services;

/// <summary>
/// Language-coverage checks over <see cref="OcrModelInfo"/> catalog entries, so the settings UI
/// can warn when the active recognition model does not cover the chosen source language
/// (spec §6.1 — e.g. src <c>ko</c> with the v6 pair, which only ships Latin/CJK scripts).
/// Kiểm tra phạm vi ngôn ngữ trên các mục catalog <see cref="OcrModelInfo"/>, để UI cài đặt
/// cảnh báo khi model nhận dạng đang dùng không phủ ngôn ngữ nguồn đã chọn (spec §6.1 — ví dụ
/// src <c>ko</c> với cặp v6, vốn chỉ có chữ Latin/CJK).
/// </summary>
public static class OcrModelCoverage
{
    /// <summary>
    /// Whether <paramref name="model"/> can read the given source language. <c>auto</c> always
    /// returns <see langword="true"/>: no single language is pinned, so it never triggers a
    /// coverage warning.
    /// Model <paramref name="model"/> có đọc được ngôn ngữ nguồn đã cho hay không. <c>auto</c>
    /// luôn trả <see langword="true"/>: không ghim một ngôn ngữ cụ thể nên không bao giờ phát
    /// cảnh báo phủ.
    /// </summary>
    /// <param name="model">Catalog model to check (typically the active rec model). Model trong catalog cần kiểm tra (thường là model rec đang dùng).</param>
    /// <param name="lang">Source language code from settings (e.g. <c>ko</c>), or <c>auto</c>. Mã ngôn ngữ nguồn từ cài đặt (ví dụ <c>ko</c>), hoặc <c>auto</c>.</param>
    /// <returns>
    /// True when the model covers the language (or the code is <c>auto</c>); false when the
    /// model lacks it and the UI should warn.
    /// True khi model phủ ngôn ngữ đó (hoặc mã là <c>auto</c>); false khi model không có và UI
    /// nên cảnh báo.
    /// </returns>
    public static bool Covers(OcrModelInfo model, string lang)
    {
        string code = lang.Trim().ToLowerInvariant();
        return code == "auto" || model.Langs.Contains(code);
    }
}
