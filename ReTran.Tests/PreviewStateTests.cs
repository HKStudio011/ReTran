using ReTran_App.Core;

public class PreviewStateTests
{
    [Fact]
    public void Publish_SetsLatestAndRaisesChanged()
    {
        var state = new PreviewState();
        Assert.Null(state.Latest);

        PreviewFrame? seen = null;
        state.Changed += frame => seen = frame;
        var frame = new PreviewFrame(7, 1280, 720, "AQID");
        state.Publish(frame);

        Assert.Equal(frame, state.Latest);
        Assert.Equal(frame, seen);
        Assert.Equal(7, seen!.Seq);
        Assert.Equal(1280, seen.W);
        Assert.Equal(720, seen.H);
        Assert.Equal("AQID", seen.JpegBase64);
    }

    [Fact]
    public void PublishTwice_LatestIsLast()
    {
        var state = new PreviewState();
        state.Publish(new PreviewFrame(1, 640, 360, "QQ=="));
        state.Publish(new PreviewFrame(2, 640, 360, "Qg=="));

        Assert.Equal(2, state.Latest!.Seq);
        Assert.Equal("Qg==", state.Latest.JpegBase64);
    }
}
