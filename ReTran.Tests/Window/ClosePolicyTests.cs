using ReTran_App.Services.Window;

public class ClosePolicyTests
{
    [Theory]
    [InlineData(false, false, CloseDecision.HideToTray)]
    [InlineData(true, false, CloseDecision.AllowClose)]   // update đang chạy — không được ẩn
    [InlineData(false, true, CloseDecision.AllowClose)]   // thoát thật từ tray
    [InlineData(true, true, CloseDecision.AllowClose)]
    public void Decide_UpdateOrExiting_AllowsClose(bool updating, bool exiting, CloseDecision expected)
        => Assert.Equal(expected, ClosePolicy.Decide(updating, exiting));

    [Fact]
    public void ShouldHookWindow_OnlyPrimary() // Review Focus #5
    {
        Assert.True(WindowHooks.ShouldHookWindow(0));
        Assert.False(WindowHooks.ShouldHookWindow(1));
        Assert.False(WindowHooks.ShouldHookWindow(2));
    }
}
