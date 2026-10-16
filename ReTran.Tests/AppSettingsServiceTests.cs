using ReTran_App.Services;

public class AppSettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AppSettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"retran-settings-{Guid.NewGuid():N}");
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var service = new AppSettingsService(_path);
        Assert.False(File.Exists(_path));

        AppSettings settings = service.Load();

        Assert.Equal(AppSettings.Default, settings);
        Assert.Equal("google", settings.Provider);
        Assert.Equal("auto", settings.Src);
        Assert.Equal("vi", settings.Dst);
        Assert.Null(settings.ApiKey);
    }

    [Fact]
    public void SaveThenLoad_Roundtrips()
    {
        var service = new AppSettingsService(_path);
        var saved = new AppSettings("deepl", "en", "ja", "secret-key");

        service.Save(saved);

        Assert.True(File.Exists(_path));
        Assert.Equal(saved, service.Load());

        service.Save(AppSettings.Default);
        Assert.Equal(AppSettings.Default, service.Load());
    }

    [Fact]
    public void LegacyFile_WithoutOverlayFields_LoadsOverlayDefaults()
    {
        Directory.CreateDirectory(_dir); // Save() would create it; a hand-written file needs the dir first.
        File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
        var service = new AppSettingsService(_path);

        AppSettings settings = service.Load();

        Assert.Equal("google", settings.Provider);
        Assert.False(settings.OverlayOnGame);
        Assert.Equal("subtitle", settings.OverlayMode);
        Assert.Equal("bl", settings.OverlayCorner);
        Assert.Equal(1.0, settings.OverlayFontScale);
    }

    [Fact]
    public void SaveThenLoad_Roundtrips_WithOverlayFields()
    {
        var service = new AppSettingsService(_path);
        var saved = new AppSettings("google", "auto", "vi", null,
            OverlayOnGame: true, OverlayMode: "panel", OverlayCorner: "tr", OverlayFontScale: 1.25);

        service.Save(saved);

        Assert.Equal(saved, service.Load());
    }
}
