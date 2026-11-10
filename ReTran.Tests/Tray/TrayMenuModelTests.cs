using ReTran_App.Services.Tray;

public class TrayMenuModelTests
{
    [Fact]
    public void Build_FixedOrder_OnlyOverlayChecked()
    {
        // Spec G3 order: OpenApp, ShowHideMenu, ToggleOverlay, Exit — chỉ ShowHideMenu và
        // ToggleOverlay mang trạng thái tick.
        var menu = TrayMenuModel.Build("Open", "Show/hide", "Overlay", "Exit",
            menuVisible: true, overlayOn: true);
        Assert.Equal(new[] { TrayCommandId.OpenApp, TrayCommandId.ShowHideMenu,
                TrayCommandId.ToggleOverlay, TrayCommandId.Exit },
            menu.Select(c => c.Id).ToArray());
        Assert.True(ById(menu, TrayCommandId.ShowHideMenu).IsChecked);
        Assert.True(ById(menu, TrayCommandId.ToggleOverlay).IsChecked);
        Assert.False(ById(menu, TrayCommandId.OpenApp).IsChecked);
        Assert.False(ById(menu, TrayCommandId.Exit).IsChecked);
        Assert.Equal("Open", menu[0].Label);
    }

    [Fact]
    public void Build_OverlayOff_Unchecked()
    {
        var menu = TrayMenuModel.Build("a", "b", "c", "d", menuVisible: false, overlayOn: false);
        Assert.False(ById(menu, TrayCommandId.ToggleOverlay).IsChecked);
        Assert.False(ById(menu, TrayCommandId.ShowHideMenu).IsChecked);
    }

    // Build trả IReadOnlyList<TrayCommand> — không có indexer enum; tra theo Id bằng helper này.
    private static TrayCommand ById(IReadOnlyList<TrayCommand> menu, TrayCommandId id) =>
        menu.Single(c => c.Id == id);
}
