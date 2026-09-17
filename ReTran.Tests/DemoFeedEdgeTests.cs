using ReTran_App.Core;
using Xunit;

public class DemoFeedEdgeTests
{
    [Fact]
    public void Ctor_Rejects_IntervalBelow50()
    {
        var state = new OverlayState();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DemoFeed(state, intervalMs: 10));
    }

    [Fact]
    public void DoubleStart_Publishes_And_StopSticks()
    {
        var state = new OverlayState();
        using var feed = new DemoFeed(state, intervalMs: 100);
        feed.Start();
        feed.Start(); // no-op, không ném, không nhân timer
        SpinWait.SpinUntil(() => state.Latest is not null, 2000);
        Assert.NotNull(state.Latest);
        feed.Stop();
        feed.Stop(); // idempotent, không ném
        var last = state.Latest;
        Thread.Sleep(300);
        Assert.Same(last, state.Latest); // dừng thật — không còn publish mới
    }

    [Fact]
    public void Boxes_Stay_Inside_Frame()
    {
        var state = new OverlayState();
        using var feed = new DemoFeed(state, intervalMs: 50);
        feed.Start();
        SpinWait.SpinUntil(() => state.Latest is not null, 2000);
        feed.Stop();
        foreach (var b in state.Latest!.Boxes)
        {
            Assert.InRange(b.X, 0, 1);
            Assert.InRange(b.Y, 0, 1);
            Assert.True(b.X + b.W <= 1.0);
            Assert.False(string.IsNullOrWhiteSpace(b.Translated));
        }
    }
}
