using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="DownloadProgressText"/>: percent/ETA math that tolerates missing data
/// (wizard vòng 2 §9 — no NaN, no negative values; unusable inputs stay <see langword="null"/> so
/// the UI keeps its indeterminate state).
/// Test cho <see cref="DownloadProgressText"/>: toán %/ETA chịu được dữ liệu thiếu (wizard vòng 2
/// §9 — không NaN, không số âm; đầu vào không dùng được giữ <see langword="null"/> để UI giữ trạng
/// thái vô định).
/// </summary>
public class DownloadProgressTextTests
{
    [Fact]
    public void Format_Normal_PercentAndEta()
    {
        var (percent, eta) = DownloadProgressText.Format(60_000_000, 100_000_000, 1_000_000);

        Assert.Equal(60, percent);
        Assert.NotNull(eta);
        Assert.Equal(40, eta!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void Format_UnknownTotal_NullBoth()
    {
        var (percent, eta) = DownloadProgressText.Format(123, 0, 50.0);

        Assert.Null(percent);
        Assert.Null(eta);
    }

    [Fact]
    public void Format_MissingSpeed_PercentOnly()
    {
        var (percent, eta) = DownloadProgressText.Format(50, 100, null);

        Assert.Equal(50, percent);
        Assert.Null(eta);
    }

    [Fact]
    public void Format_MissingData_NoNaN()
    {
        // received âm ⇒ không dùng được.
        var (negativePercent, negativeEta) = DownloadProgressText.Format(-5, 100, 10.0);
        Assert.Null(negativePercent);
        Assert.Null(negativeEta);

        // received vượt tổng ⇒ kẹp về 100, hết ETA.
        var (overPercent, overEta) = DownloadProgressText.Format(150, 100, 10.0);
        Assert.Equal(100, overPercent);
        Assert.Null(overEta);

        // tốc độ 0 ⇒ còn % nhưng không ETA.
        var (zeroSpeedPercent, zeroSpeedEta) = DownloadProgressText.Format(50, 100, 0.0);
        Assert.Equal(50, zeroSpeedPercent);
        Assert.Null(zeroSpeedEta);
    }

    [Fact]
    public void Format_Complete_Percent100_NoEta()
    {
        var (percent, eta) = DownloadProgressText.Format(100, 100, 999.0);

        Assert.Equal(100, percent);
        Assert.Null(eta);
    }
}
