using ReTran_App.Services.Tray;

public class TrayMenuModelTests
{
    [Fact]
    public void Build_FixedOrder_OnlyOverlayChecked()
    {
        var menu = TrayMenuModel.Build("Open", "Overlay", "Exit", overlayOn: true);
        Assert.Equal(new[] { TrayCommandId.OpenApp, TrayCommandId.ToggleOverlay, TrayCommandId.Exit },
            menu.Select(c => c.Id).ToArray());
        Assert.True(ById(menu, TrayCommandId.ToggleOverlay).IsChecked);
        Assert.False(ById(menu, TrayCommandId.OpenApp).IsChecked);
        Assert.Equal("Open", menu[0].Label);
    }

    [Fact]
    public void Build_OverlayOff_Unchecked()
    {
        var menu = TrayMenuModel.Build("a", "b", "c", overlayOn: false);
        Assert.False(ById(menu, TrayCommandId.ToggleOverlay).IsChecked);
    }

    // Build trả IReadOnlyList<TrayCommand> — không có indexer enum; tra theo Id bằng helper này.
    private static TrayCommand ById(IReadOnlyList<TrayCommand> menu, TrayCommandId id) =>
        menu.Single(c => c.Id == id);
}
