using ReTran.Core.Serve;

namespace RetranCore.Tests;

public class AnnotationSnapshotTests
{
    [Fact]
    public void TryGet_BeforePublish_ReturnsNull()
    {
        var snapshot = new AnnotationSnapshot();
        Assert.Null(snapshot.TryGet());
    }

    [Fact]
    public void PublishThenTryGet_ReturnsLatestSnapshot()
    {
        var snapshot = new AnnotationSnapshot();
        snapshot.Publish(640, 360, [new AnnotBox(0.1, 0.2, 0.3, 0.05, "OLD", null)]);
        snapshot.Publish(1280, 720, [new AnnotBox(0.5, 0.5, 0.25, 0.1, "NEW", "Mới")]);

        AnnotSnapshot? latest = snapshot.TryGet();
        Assert.NotNull(latest);
        Assert.Equal(1280, latest!.FrameWidth);
        Assert.Equal(720, latest.FrameHeight);
        AnnotBox box = Assert.Single(latest.Boxes);
        Assert.Equal("NEW", box.Text);
        Assert.Equal("Mới", box.Translated);
    }
}
