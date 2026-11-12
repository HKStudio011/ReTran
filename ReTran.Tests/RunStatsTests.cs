using ReTran_App.Core;

public class RunStatsTests
{
    /// <summary>
    /// Minimal pipeline fake: IsRunning/ActiveSource are settable, both events raise manually.
    /// Fake pipeline tối thiểu: IsRunning/ActiveSource đặt tay, cả hai event raise thủ công.
    /// </summary>
    private sealed class FakePipeline : IPipelineControl
    {
        public FakePipeline(bool running) => IsRunning = running;

        public bool IsRunning { get; set; }
        public SourceSelection? ActiveSource { get; set; }

        public Task StartAsync(SourceSelection source, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows)> ListSourcesAsync(
            CancellationToken ct)
        {
            (IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows) result =
                (Array.Empty<MonitorInfo>(), Array.Empty<WindowInfo>());
            return Task.FromResult(result);
        }

        public event Action? StateChanged;
        public event Action<bool, string?>? PipelineStateChanged;

        public void RaiseStateChanged() => StateChanged?.Invoke();
        public void RaisePipelineStateChanged(bool running, string? reason) => PipelineStateChanged?.Invoke(running, reason);
    }

    /// <summary>
    /// Seeds running stats: 5 preview frames + one overlay frame (2 boxes, 1 translated), then one
    /// fps tick — pre-condition Fps/BoxCount/TranslatedLineCount all non-zero.
    /// Gieo trạng thái đang chạy: 5 khung preview + một khung overlay (2 box, 1 dòng dịch) rồi một
    /// tick fps — tiền điều kiện Fps/BoxCount/TranslatedLineCount đều khác 0.
    /// </summary>
    private static void SeedRunningStats(RunStats stats, PreviewState preview, OverlayState overlay, ref long tick)
    {
        for (int i = 0; i < 5; i++) preview.Publish(new PreviewFrame(i + 1, 100, 100, ""));
        overlay.Publish(new OverlayFrame(100, 100, DateTime.UtcNow,
            [new OverlayBox(0, 0, 1, 1, "a", "b"), new OverlayBox(0, 0, 1, 1, "c", "")]));
        tick += 1000; // cửa sổ 1 s trôi qua
        stats.TickForTest();
    }

    [Fact]
    public void Fps_CountsPreviewFramesInWindow()
    {
        var preview = new PreviewState();
        long tick = 0;
        using var stats = new RunStats(preview, new OverlayState(), new FakePipeline(running: true),
            now: () => tick, window: TimeSpan.FromSeconds(1));

        for (int i = 0; i < 5; i++) preview.Publish(new PreviewFrame(i + 1, 100, 100, ""));
        tick += 1000; // cửa sổ 1 s trôi qua → tick nội bộ của RunStats tính fps
        stats.TickForTest();

        Assert.Equal(5.0, stats.Fps, 2);
    }

    [Fact]
    public void BoxAndLineCounts_FollowLatestOverlayFrame()
    {
        var overlay = new OverlayState();
        using var stats = new RunStats(new PreviewState(), overlay, new FakePipeline(running: true));
        overlay.Publish(new OverlayFrame(100, 100, DateTime.UtcNow,
            [new OverlayBox(0, 0, 1, 1, "a", "b"), new OverlayBox(0, 0, 1, 1, "c", "")]));

        Assert.Equal(2, stats.BoxCount);
        Assert.Equal(1, stats.TranslatedLineCount);
    }

    [Fact]
    public void PipelineStop_ResetsFpsAndCounts()
    {
        // Đường 1 — Stop do người dùng: PipelineController.StopAsync chỉ raise StateChanged
        // (không raise PipelineStateChanged).
        long tick = 0;
        var preview = new PreviewState();
        var overlay = new OverlayState();
        var pipeline = new FakePipeline(running: true);
        using (var stats = new RunStats(preview, overlay, pipeline, now: () => tick))
        {
            SeedRunningStats(stats, preview, overlay, ref tick);
            Assert.True(stats.Fps > 0.0);
            Assert.Equal(2, stats.BoxCount);
            Assert.Equal(1, stats.TranslatedLineCount);

            pipeline.IsRunning = false; // StopAsync gán IsRunning=false trước khi raise StateChanged
            pipeline.RaiseStateChanged();

            Assert.Equal(0.0, stats.Fps, 2);
            Assert.Equal(0, stats.BoxCount);
            Assert.Equal(0, stats.TranslatedLineCount);
        }

        // Đường 2 — dừng đột xuất: Core raise PipelineStateChanged(false, reason).
        var preview2 = new PreviewState();
        var overlay2 = new OverlayState();
        var pipeline2 = new FakePipeline(running: true);
        using (var stats2 = new RunStats(preview2, overlay2, pipeline2, now: () => tick))
        {
            SeedRunningStats(stats2, preview2, overlay2, ref tick);
            Assert.True(stats2.Fps > 0.0);

            pipeline2.IsRunning = false;
            pipeline2.RaisePipelineStateChanged(false, "sidecar-exited");

            Assert.Equal(0.0, stats2.Fps, 2);
            Assert.Equal(0, stats2.BoxCount);
            Assert.Equal(0, stats2.TranslatedLineCount);
        }
    }

    [Fact]
    public void Changed_Throttled_NotEveryFrame()
    {
        long tick = 0; // clock đóng băng — không bao giờ đủ 250 ms giữa hai frame
        int raised = 0;
        var preview = new PreviewState();
        using var stats = new RunStats(preview, new OverlayState(), new FakePipeline(running: true), now: () => tick);
        stats.Changed += () => Interlocked.Increment(ref raised);

        for (int i = 0; i < 10; i++) preview.Publish(new PreviewFrame(i + 1, 100, 100, ""));

        // Leading-edge: frame đầu phát, các frame còn lại bị throttle → tổng cộng ≤ 4 lần.
        Assert.InRange(raised, 1, 4);
    }
}
