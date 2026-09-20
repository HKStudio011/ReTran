using ReTran.Core.Capture;
using Xunit;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class LatestFrameBufferTests
{
    private sealed class FakeSource : ICaptureSource
    {
        public CaptureTarget Target { get; } = new ScreenTarget(0);
        public event EventHandler<FrameArrivedEventArgs>? FrameArrived;
        public bool IsRunning { get; private set; }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() { }
        public void Emit(Frame f) => FrameArrived?.Invoke(this, new FrameArrivedEventArgs(f));
    }

    private static Frame Tiny(byte v = 0) => Frame.FromBgra(2, 2, Enumerable.Repeat(v, 2 * 2 * 4).ToArray());

    [Fact]
    public void Push_Twice_WithoutTake_DropsFirst()
    {
        using var buf = new LatestFrameBuffer();
        buf.Push(Tiny(10)); buf.Push(Tiny(20));
        Assert.Equal(1, buf.DroppedCount);
        Assert.True(buf.TryTakeLatest(out Frame? f));
        Assert.Equal(20, f!.Pixels[0]);
        f.Dispose();
    }

    [Fact]
    public void FakeSource_Emits_ThroughInterface()
    {
        var src = new FakeSource();
        Frame? seen = null;
        src.FrameArrived += (_, e) => seen = e.Frame;
        src.Start();
        src.Emit(Tiny(7));
        Assert.True(src.IsRunning);
        Assert.NotNull(seen);
    }
}
