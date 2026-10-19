using ReTran.Core.Ocr;
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

    [Fact]
    public void DeviceName_MatchesTheNameRegisteredByTheNativeFilter()
    {
        // The UI card tells users which device to pick, so the C# name and the friendly name the
        // native filter registers must stay identical - this guards the drift.
        // Card UI chỉ cho user chọn thiết bị nào, nên tên trong C# và friendly name mà filter
        // native đăng ký phải trùng nhau - test này chốt chống lệch.
        Assert.False(string.IsNullOrWhiteSpace(CameraRegistrar.DeviceName));

        if (OcrBootstrap.FindRepoRoot() is not string repoRoot)
        {
            return; // not running inside the repo layout
        }
        string guidsSource = Path.Combine(repoRoot, "ReTran.Camera", "src", "guids.cpp");
        if (!File.Exists(guidsSource))
        {
            return; // native project not present
        }

        Assert.Contains($"L\"{CameraRegistrar.DeviceName}\"", File.ReadAllText(guidsSource));
    }
}
