using ReTran_App.Services.Camera;

public class CameraRegistrarTests
{
    [Fact]
    public void EnsureRegistered_InvokesRegistrarWithDllPath()
    {
        string? received = null;
        var registrar = new CameraRegistrar(path => received = path);

        registrar.EnsureRegistered();

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ReTran.Camera.dll"), received);
    }

    [Fact]
    public void EnsureRegistered_ExplicitPath_ForwardsPath()
    {
        string? received = null;
        var registrar = new CameraRegistrar(path => received = path);

        registrar.EnsureRegistered(@"C:\custom\ReTran.Camera.dll");

        Assert.Equal(@"C:\custom\ReTran.Camera.dll", received);
    }

    [Fact]
    public void EnsureRegistered_MissingDll_DoesNotThrow()
    {
        var registrar = new CameraRegistrar();

        var ex = Record.Exception(() => registrar.EnsureRegistered(
            Path.Combine(AppContext.BaseDirectory, "no-such-camera.dll")));

        Assert.Null(ex);
    }
}
