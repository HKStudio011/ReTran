using ReTran_App.Services.Tray;

public class TrayIconSourceTests
{
    [Fact]
    public void Handle_NonZero_AndStable()
    {
        using var icon = new TrayIconSource();
        Assert.True(icon.Handle != 0);
        Assert.Equal(icon.Handle, icon.Handle);
    }

    [Fact]
    public void Dispose_Idempotent_AndMarksDisposed()
    {
        var icon = new TrayIconSource();
        icon.Dispose();
        Assert.True(icon.IsDisposed);
        icon.Dispose(); // không throw
    }
}
