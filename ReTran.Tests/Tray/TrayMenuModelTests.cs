using ReTran_App.Services.Tray;

public class TrayMenuModelTests
{
    [Fact]
    public void Build_FixedOrder_OnlyOverlayChecked()
    {
        var menu = TrayMenuModel.Build("Open", "Overlay", "Exit", overlayOn: true);
        Assert.Equal(new[] { TrayCommandId.OpenApp, TrayCommandId.ToggleOverlay, TrayCommandId.Exit },
            menu.Select(c => c.Id).ToArray());
        Assert.True(menu[TrayCommandId.ToggleOverlay].IsChecked);
        Assert.False(menu[TrayCommandId.OpenApp].IsChecked);
        Assert.Equal("Open", menu[0].Label);
    }

    [Fact]
    public void Build_OverlayOff_Unchecked()
    {
        var menu = TrayMenuModel.Build("a", "b", "c", overlayOn: false);
        Assert.False(menu[TrayCommandId.ToggleOverlay].IsChecked);
    }
}
