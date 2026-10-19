using ReTran_App.Core;
using Xunit;

public class OverlayStateTests
{
    [Fact]
    public void Publish_Twice_WithoutRead_DropsFirst()
    {
        var state = new OverlayState();
        int events = 0;
        state.Changed += () => events++;
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.1, 0.1, 0.2, 0.05, "a", "A") }));
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.2, 0.2, 0.2, 0.05, "b", "B") }));
        Assert.Equal("b", state.Latest!.Boxes[0].Text);
        Assert.Equal(2, events);
    }
}
