namespace ReTran.Core.Ocr;

/// <summary>
/// The three line-merge thresholds used by <see cref="LineGrouper"/>, validated against the ranges the UI accepts (design spec K2).
/// Bộ ba ngưỡng gộp dòng mà <see cref="LineGrouper"/> dùng, kiểm theo dải giá trị mà UI chấp nhận (spec thiết kế K2).
/// </summary>
/// <param name="GapRatio">Vertical gap cap, as a fraction of the shorter line's height. Ngưỡng khoảng cách dọc, tính theo tỷ lệ chiều cao của dòng thấp hơn.</param>
/// <param name="HeightRatio">Height cap, taller line over shorter line. Tỷ lệ chiều cao tối đa của dòng cao hơn so với dòng thấp hơn.</param>
/// <param name="MinOverlapRatio">Horizontal overlap floor, as a fraction of the narrower line's width. Ngưỡng độ chồng ngang tối thiểu, tính theo tỷ lệ chiều rộng của dòng hẹp hơn.</param>
public readonly record struct MergeThresholds(double GapRatio, double HeightRatio, double MinOverlapRatio)
{
    /// <summary>
    /// Allowed range for <see cref="GapRatio"/>, bounds inclusive (design spec K2). Used by both
    /// <see cref="IsValid"/> and <c>core.start</c> validation, so the bounds are never restated as
    /// literals by a caller.
    /// Dải cho phép của <see cref="GapRatio"/>, bao gồm biên (spec thiết kế K2). Cả
    /// <see cref="IsValid"/> và phần kiểm tra của <c>core.start</c> đều dùng dải này, nên nơi gọi
    /// không phải nhắc lại các hằng số.
    /// </summary>
    public const double GapRatioLow = 0.1, GapRatioHigh = 2.0;

    /// <summary>
    /// Allowed range for <see cref="HeightRatio"/>, bounds inclusive (design spec K2); same role as
    /// <see cref="GapRatioLow"/>.
    /// Dải cho phép của <see cref="HeightRatio"/>, bao gồm biên (spec thiết kế K2); vai trò như
    /// <see cref="GapRatioLow"/>.
    /// </summary>
    public const double HeightRatioLow = 1.0, HeightRatioHigh = 3.0;

    /// <summary>
    /// Allowed range for <see cref="MinOverlapRatio"/>, bounds inclusive (design spec K2); same role
    /// as <see cref="GapRatioLow"/>.
    /// Dải cho phép của <see cref="MinOverlapRatio"/>, bao gồm biên (spec thiết kế K2); vai trò như
    /// <see cref="GapRatioLow"/>.
    /// </summary>
    public const double OverlapRatioLow = 0.0, OverlapRatioHigh = 1.0;

    /// <summary>
    /// The shipped defaults, taken from the original <see cref="LineGrouper"/> constants — identical to pre-configuration behaviour.
    /// Giá trị mặc định gốc, lấy từ ba hằng số ban đầu của <see cref="LineGrouper"/> — giống hệt hành vi trước khi cấu hình.
    /// </summary>
    public static MergeThresholds Default { get; } =
        new(LineGrouper.MaxGapRatio, LineGrouper.MaxHeightRatio, LineGrouper.MinOverlapRatio);

    /// <summary>
    /// Whether every threshold sits inside its allowed range (gap 0.1..2.0, height 1.0..3.0, overlap 0..1, bounds inclusive).
    /// Cho biết cả ba ngưỡng có nằm trong dải cho phép hay không (gap 0.1..2.0, height 1.0..3.0, overlap 0..1, bao gồm biên).
    /// </summary>
    /// <returns>true when all three values are in range; false otherwise (NaN is out of range). true khi cả ba giá trị đều trong dải; ngược lại false (NaN ngoài dải).</returns>
    public bool IsValid() =>
        GapRatio is >= GapRatioLow and <= GapRatioHigh
        && HeightRatio is >= HeightRatioLow and <= HeightRatioHigh
        && MinOverlapRatio is >= OverlapRatioLow and <= OverlapRatioHigh;
}
