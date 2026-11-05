using System.Globalization;

namespace ReTran_App.Services;

/// <summary>
/// Pure parse/range helpers for the three merge-line thresholds edited in the OCR settings
/// section (the sheet validates before saving, spec K2). Parsing accepts both <c>"0.6"</c> and
/// <c>"0,6"</c> whatever the machine culture and rejects exponent text; the range checks mirror
/// Core's <c>MergeThresholds</c> bounds, which the App cannot reference. No UI, DI or state.
/// Trợ giúp kiểm tra/phân tích thuần cho ba ngưỡng gộp dòng chỉnh trong mục cài đặt OCR (sheet
/// kiểm tra trước khi lưu, spec K2). Phần phân tích chấp nhận cả <c>"0.6"</c> lẫn <c>"0,6"</c> bất
/// kể culture của máy và từ chối dạng số mũ; các dải giá trị mô phỏng đúng biên của
/// <c>MergeThresholds</c> phía Core mà App không tham chiếu được. Không phụ thuộc UI/DI, không state.
/// </summary>
public static class OcrMergeThresholds
{
    /// <summary>
    /// Shipped defaults shown when the thresholds are reset; kept in sync with Core's
    /// <c>MergeThresholds.Default</c> by <c>OcrMergeThresholdsTests.AppDefaults_MatchCoreConsts</c>.
    /// Mặc định gốc khi reset ngưỡng; được giữ khớp với <c>MergeThresholds.Default</c> của Core nhờ
    /// test <c>OcrMergeThresholdsTests.AppDefaults_MatchCoreConsts</c>.
    /// </summary>
    public const double DefaultGap = 0.6, DefaultHeight = 1.35, DefaultOverlap = 0.5;

    /// <summary>
    /// Float parse style without <see cref="NumberStyles.AllowExponent"/>, so text like
    /// <c>"1e5"</c> is rejected by the parser instead of quietly becoming 100000 for the range
    /// checks to judge.
    /// Kiểu phân tích số thực bỏ <see cref="NumberStyles.AllowExponent"/>, để chuỗi kiểu <c>"1e5"</c>
    /// bị chính bộ parse từ chối thay vì âm thầm thành 100000 rồi mới tới lượt dải giá trị.
    /// </summary>
    private const NumberStyles RatioStyle = NumberStyles.Float & ~NumberStyles.AllowExponent;

    /// <summary>
    /// Parses one threshold text control ("0.6", "0,6", " 1.35 "). Leading and trailing whitespace
    /// is trimmed. Null, blank and non-numeric text (including exponent forms such as <c>"1e5"</c>)
    /// are invalid. The invariant culture is tried first, then a comma-decimal format, so the
    /// result never depends on the machine's current culture.
    /// Phân tích một ô nhập ngưỡng ("0.6", "0,6", " 1.35 "). Khoảng trắng đầu/cuối được cắt bỏ.
    /// Null, chuỗi trống và chuỗi không phải số (kể cả dạng số mũ như <c>"1e5"</c>) đều không hợp
    /// lệ. Thử invariant culture trước rồi tới định dạng dùng dấu phẩy thập phân, nên kết quả không
    /// phụ thuộc culture hiện hành của máy.
    /// </summary>
    /// <param name="text">Raw control text, or null. Chuỗi thô của ô nhập, hoặc null.</param>
    /// <param name="value">Parsed value on success, otherwise 0. Giá trị đã phân tích khi thành công, ngược lại là 0.</param>
    /// <returns>True when the text is a plain decimal number. True khi chuỗi là số thập phân trần.</returns>
    public static bool TryParseRatio(string? text, out double value)
    {
        value = 0;
        if (text is null)
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (double.TryParse(trimmed, RatioStyle, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // Ô nhập hiển thị theo culture nên người dùng gõ dấu phẩy thập phân; chỉ đổi dấu phân
        // cách, không lấy NumberFormatInfo của CurrentCulture.
        var commaFormat = new NumberFormatInfo { NumberDecimalSeparator = "," };
        if (double.TryParse(trimmed, RatioStyle, commaFormat, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Whether a gap ratio sits in the allowed range 0.1..2.0 (bounds inclusive, spec K2).
    /// Cho biết ngưỡng khoảng cách có nằm trong dải cho phép 0.1..2.0 (bao gồm hai biên, spec K2)
    /// hay không.
    /// </summary>
    /// <param name="value">Value to check (NaN is out of range). Giá trị cần kiểm (NaN ngoài dải).</param>
    /// <returns>True when in range. True khi nằm trong dải.</returns>
    public static bool InGapRange(double value) => value is >= 0.1 and <= 2.0;

    /// <summary>
    /// Whether a height ratio sits in the allowed range 1.0..3.0 (bounds inclusive, spec K2).
    /// Cho biết ngưỡng chênh chiều cao có nằm trong dải cho phép 1.0..3.0 (bao gồm hai biên,
    /// spec K2) hay không.
    /// </summary>
    /// <param name="value">Value to check (NaN is out of range). Giá trị cần kiểm (NaN ngoài dải).</param>
    /// <returns>True when in range. True khi nằm trong dải.</returns>
    public static bool InHeightRange(double value) => value is >= 1.0 and <= 3.0;

    /// <summary>
    /// Whether an overlap ratio sits in the allowed range 0.0..1.0 (bounds inclusive, spec K2).
    /// Cho biết ngưỡng chồng ngang có nằm trong dải cho phép 0.0..1.0 (bao gồm hai biên, spec K2)
    /// hay không.
    /// </summary>
    /// <param name="value">Value to check (NaN is out of range). Giá trị cần kiểm (NaN ngoài dải).</param>
    /// <returns>True when in range. True khi nằm trong dải.</returns>
    public static bool InOverlapRange(double value) => value is >= 0.0 and <= 1.0;

    /// <summary>
    /// Whether all three thresholds are in their own ranges — the settings sheet saves only when
    /// this is true.
    /// Cho biết cả ba ngưỡng có nằm trong dải của từng cái hay không — sheet cài đặt chỉ lưu khi
    /// điều này đúng.
    /// </summary>
    /// <param name="gap">Gap ratio to check. Ngưỡng khoảng cách cần kiểm.</param>
    /// <param name="height">Height ratio to check. Ngưỡng chênh chiều cao cần kiểm.</param>
    /// <param name="overlap">Overlap ratio to check. Ngưỡng chồng ngang cần kiểm.</param>
    /// <returns>True when every value is in range. True khi mọi giá trị đều trong dải.</returns>
    public static bool AllValid(double gap, double height, double overlap) =>
        InGapRange(gap) && InHeightRange(height) && InOverlapRange(overlap);
}
