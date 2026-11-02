using ReTran_App.Services.Tray;

public class TrayContractTests
{
    [Fact]
    public void AddUFlags_MessageIconTip() => Assert.Equal(0x7u, TrayContract.AddUFlags());

    [Fact]
    public void ModifyUFlags_WithAndWithoutTip()
    {
        Assert.Equal(TrayContract.NifInfo | TrayContract.NifTip, TrayContract.ModifyUFlags(true));
        Assert.Equal(TrayContract.NifInfo, TrayContract.ModifyUFlags(false));
    }

    [Theory]
    [InlineData(BalloonIcon.None, 0)]
    [InlineData(BalloonIcon.Info, 1)]
    [InlineData(BalloonIcon.Warning, 2)]
    [InlineData(BalloonIcon.Error, 3)]
    // Cast: xUnit v3's Assert.Equal<T> không suy được T chung cho (int, uint).
    public void InfoFlags_MapsLevel(BalloonIcon icon, int expected) => Assert.Equal((uint)expected, TrayContract.InfoFlags(icon));

    [Fact]
    public void Dispatch_TaskbarCreated_ReAddsIcon() // Review Focus #1
        => Assert.Equal(TrayContract.TrayDispatch.ReAddIcon,
            TrayContract.Dispatch(wParamMsg: 0x8000, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: 0));

    [Theory]
    [InlineData(TrayContract.WmLbuttonUp, TrayContract.TrayDispatch.LeftClick)]
    [InlineData(TrayContract.WmLbuttonDblClk, TrayContract.TrayDispatch.LeftClick)]
    // internal: TrayDispatch là type internal (InternalsVisibleTo); method public không được expose nó trong signature.
    internal void Dispatch_LeftClicks(uint mouseMsg, TrayContract.TrayDispatch expected)
        => Assert.Equal(expected, TrayContract.Dispatch(wParamMsg: 0x0401, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: mouseMsg));

    [Theory]
    [InlineData(TrayContract.WmRbuttonUp, TrayContract.TrayDispatch.ShowMenu)]
    [InlineData(TrayContract.WmContextMenu, TrayContract.TrayDispatch.ShowMenu)]
    // internal: TrayDispatch là type internal (InternalsVisibleTo); method public không được expose nó trong signature.
    internal void Dispatch_RightClicks_ShowMenu(uint mouseMsg, TrayContract.TrayDispatch expected)
        => Assert.Equal(expected, TrayContract.Dispatch(wParamMsg: 0x0401, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: mouseMsg));

    [Fact]
    public void Dispatch_OtherMessages_Ignored()
        => Assert.Equal(TrayContract.TrayDispatch.Ignore, TrayContract.Dispatch(wParamMsg: 0x9999, taskbarCreatedId: 0x8000, callbackMessage: 0x0401, lParamMouseMsg: 0));
}
