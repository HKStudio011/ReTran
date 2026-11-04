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
    // Ranges fixed by design spec K2; shared by settings validation and core.start (Task 3).
    private const double GapLow = 0.1, GapHigh = 2.0;
    private const double HeightLow = 1.0, HeightHigh = 3.0;
    private const double OverlapLow = 0.0, OverlapHigh = 1.0;

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
        GapRatio is >= GapLow and <= GapHigh
        && HeightRatio is >= HeightLow and <= HeightHigh
        && MinOverlapRatio is >= OverlapLow and <= OverlapHigh;
}
