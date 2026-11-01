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
        Assert.Equal("PP-OCRv6_small_det", settings.OcrDet);
        Assert.Equal("PP-OCRv6_small_rec", settings.OcrRec);
        Assert.Null(settings.OcrEngine);
        Assert.Equal(3, settings.OcrFps);
        Assert.Equal(10, settings.CaptureFps);
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
        // File cũ không có cặp model OCR → nhận cặp mặc định small.
        Assert.Equal("PP-OCRv6_small_det", settings.OcrDet);
        Assert.Equal("PP-OCRv6_small_rec", settings.OcrRec);
    }

    [Fact]
    public void LegacyFile_WithRemovedPanelAndFontKeys_StillLoads()
    {
        // File do bản cũ ghi có overlayMode/overlayCorner/overlayFontScale (đã bỏ) — khóa lạ
        // bị bỏ qua, file cũ vẫn đọc được.
        Directory.CreateDirectory(_dir); // Save() would create it; a hand-written file needs the dir first.
        File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi","overlayMode":"panel","overlayCorner":"tr","overlayFontScale":1.25}""");
        var service = new AppSettingsService(_path);

        AppSettings settings = service.Load();

        Assert.Equal("google", settings.Provider);
        Assert.Equal("vi", settings.Dst);
    }

    [Fact]
    public void SaveThenLoad_Roundtrips_WithOcrPair()
    {
        var service = new AppSettingsService(_path);
        var saved = new AppSettings("google", "auto", "vi", null,
            OcrDet: "PP-OCRv6_tiny_det", OcrRec: "PP-OCRv6_tiny_rec");

        service.Save(saved);

        Assert.True(File.Exists(_path));
        Assert.Equal(saved, service.Load());
    }

    [Fact]
    public void SaveThenLoad_Roundtrips_WithOverlayFields()
    {
        var service = new AppSettingsService(_path);
        var saved = new AppSettings("google", "auto", "vi", null,
            OverlayOnGame: true);

        service.Save(saved);

        Assert.Equal(saved, service.Load());
    }

    [Fact]
    public void OcrEngine_RoundTrips_AndUnknownKeysSurviveLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"retran-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {"provider":"google","src":"auto","dst":"vi",
                 "ocrEngine":{"text_det_thresh":0.35,"bogus":{"x":1}},"ocrFps":5,"captureFps":12}
                """);
            var svc = new AppSettingsService(path);
            AppSettings loaded = svc.Load();
            Assert.Equal(0.35, loaded.OcrEngine!["text_det_thresh"]!.GetValue<double>());
            Assert.NotNull(loaded.OcrEngine!["bogus"]);   // App giữ nguyên; Core lọc khi start
            Assert.Equal(5, loaded.OcrFps);
            Assert.Equal(12, loaded.CaptureFps);
            svc.Save(loaded);
            AppSettings again = svc.Load();
            Assert.Equal(0.35, again.OcrEngine!["text_det_thresh"]!.GetValue<double>());
            Assert.NotNull(again.OcrEngine!["bogus"]);    // key lạ cũng sống qua được save/load
            Assert.Equal(5, again.OcrFps);
            Assert.Equal(12, again.CaptureFps);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OcrEngine_Absent_MeansDefaults()
    {
        // File cũ không có ocrEngine/ocrFps/captureFps -> null / 3 / 10.
        Directory.CreateDirectory(_dir); // Save() would create it; a hand-written file needs the dir first.
        File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
        var service = new AppSettingsService(_path);

        AppSettings settings = service.Load();

        Assert.Null(settings.OcrEngine);
        Assert.Equal(3, settings.OcrFps);
        Assert.Equal(10, settings.CaptureFps);
    }
}
