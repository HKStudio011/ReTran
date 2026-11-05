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

    [Fact]
    public void Clear_ResetsLatest_AndRaisesOnce()
    {
        var state = new OverlayState();
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.1, 0.1, 0.2, 0.05, "a", "A") }));

        // Chỉ đếm event do Clear gây ra — publish ở trên đã nổ Changed của riêng nó.
        int events = 0;
        state.Changed += () => events++;

        state.Clear();

        // Latest == null là hợp lệ "snapshot rỗng" mà Task 9 dựa vào cho OBS Browser Source.
        Assert.Null(state.Latest);
        Assert.Equal(1, events);

        // Đường dừng có thể nổ hai event (PipelineStateChanged + StateChanged) ⇒ Clear lần hai
        // phải im lặng, nếu không feed sẽ gửi payload rỗng trùng lặp.
        state.Clear();
        Assert.Null(state.Latest);
        Assert.Equal(1, events);
    }

    [Fact]
    public void Clear_OnEmptyState_RaisesNothing()
    {
        var state = new OverlayState();
        int events = 0;
        state.Changed += () => events++;

        state.Clear();

        Assert.Null(state.Latest);
        Assert.Equal(0, events);
    }

    [Fact]
    public void Clear_ThenPublish_PublishesAgain()
    {
        var state = new OverlayState();
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.1, 0.1, 0.2, 0.05, "a", "A") }));
        state.Clear();

        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.2, 0.2, 0.2, 0.05, "b", "B") }));

        // Clear chỉ xóa khung, không khóa session: phiên mới phải publish lại bình thường.
        Assert.Equal("b", state.Latest!.Boxes[0].Text);
    }
}
