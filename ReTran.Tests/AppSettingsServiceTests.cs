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

    [Fact]
    public void OcrMergeLines_RoundTrips()
    {
        var service = new AppSettingsService(_path);
        var off = AppSettings.Default with { OcrMergeLines = false };

        service.Save(off);
        Assert.False(service.Load().OcrMergeLines);

        service.Save(AppSettings.Default); // default is true
        Assert.True(service.Load().OcrMergeLines);
    }

    [Fact]
    public void OcrMergeLines_Absent_DefaultsTrue()
    {
        // File cũ không có ocrMergeLines -> mặc định true.
        Directory.CreateDirectory(_dir); // Save() would create it; a hand-written file needs the dir first.
        File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
        var service = new AppSettingsService(_path);

        AppSettings settings = service.Load();

        Assert.True(settings.OcrMergeLines);
    }

    [Fact]
    public void MenuAndSourceParams_RoundTrip()
    {
        var service = new AppSettingsService(_path);
        var custom = AppSettings.Default with
        {
            FloatingMenuOn = false,
            FloatingMenuCollapsed = true,
            FloatingMenuX = 123,
            FloatingMenuY = 456,
            SourceUseWindow = true,
            SourceMonitor = 1,
            SourceHwnd = "0x1A2B3C",
        };

        service.Save(custom);
        AppSettings loaded = service.Load();

        Assert.False(loaded.FloatingMenuOn);
        Assert.True(loaded.FloatingMenuCollapsed);
        Assert.Equal(123, loaded.FloatingMenuX);
        Assert.Equal(456, loaded.FloatingMenuY);
        Assert.True(loaded.SourceUseWindow);
        Assert.Equal(1, loaded.SourceMonitor);
        Assert.Equal("0x1A2B3C", loaded.SourceHwnd);
    }

    [Fact]
    public void MenuAndSourceParams_Absent_DefaultsSafe()
    {
        // File cũ không có 7 key mới → đúng default (menu bật, nguồn = monitor 0, hwnd null).
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
        var service = new AppSettingsService(_path);

        AppSettings settings = service.Load();

        Assert.True(settings.FloatingMenuOn);
        Assert.False(settings.FloatingMenuCollapsed);
        Assert.Null(settings.FloatingMenuX);
        Assert.Null(settings.FloatingMenuY);
        Assert.False(settings.SourceUseWindow);
        Assert.Equal(0, settings.SourceMonitor);
        Assert.Null(settings.SourceHwnd);
    }

    [Fact]
    public void MergeThresholdParams_RoundTrip()
    {
        var service = new AppSettingsService(_path);
        var custom = AppSettings.Default with
        {
            OcrMergeGap = 0.25,
            OcrMergeHeight = 2.0,
            OcrMergeOverlap = 0.0,
        };

        service.Save(custom);
        AppSettings loaded = service.Load();

        Assert.Equal(0.25, loaded.OcrMergeGap);
        Assert.Equal(2.0, loaded.OcrMergeHeight);
        // 0.0 là giá trị hợp lệ của overlap, không được bị thay bằng default 0.5.
        Assert.Equal(0.0, loaded.OcrMergeOverlap);
    }

    [Fact]
    public void MergeThresholdParams_Absent_DefaultsCurrent()
    {
        // File cũ không có ba key ngưỡng → đúng ngưỡng hiện tại của LineGrouper (0.6 / 1.35 / 0.5),
        // không phải 0 hay 1.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
        var service = new AppSettingsService(_path);

        AppSettings loaded = service.Load();

        Assert.Equal(0.6, loaded.OcrMergeGap);
        Assert.Equal(1.35, loaded.OcrMergeHeight);
        Assert.Equal(0.5, loaded.OcrMergeOverlap);
    }

    [Fact]
    public void LlmParams_RoundTrip()
    {
        var service = new AppSettingsService(_path);
        var custom = AppSettings.Default with
        {
            Provider = "llm",
            LlmMode = "api",
            LlmBaseUrl = "http://localhost:1234/v1",
            LlmApiKey = "sk-test",
            LlmApiModel = "llama-3.3-70b",
            LlmActiveModel = "Hy-MT2-7B-Q4_K_M",
            LlmSystemPrompt = "You are a translator.",
            LlmUserPrompt = "Translate: {source_text}",
        };

        service.Save(custom);
        AppSettings loaded = service.Load();

        Assert.Equal("llm", loaded.Provider);
        Assert.Equal("api", loaded.LlmMode);
        Assert.Equal("http://localhost:1234/v1", loaded.LlmBaseUrl);
        Assert.Equal("sk-test", loaded.LlmApiKey);
        Assert.Equal("llama-3.3-70b", loaded.LlmApiModel);
        Assert.Equal("Hy-MT2-7B-Q4_K_M", loaded.LlmActiveModel);
        Assert.Equal("You are a translator.", loaded.LlmSystemPrompt);
        Assert.Equal("Translate: {source_text}", loaded.LlmUserPrompt);
    }

    [Fact]
    public void LlmParams_Absent_DefaultsLocal()
    {
        // File cũ không có 7 key LLM → mặc định local + model catalog mặc định; prompt null
        // (= mặc định Hy-MT2 cả hai phía).
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, """{"provider":"google","src":"auto","dst":"vi"}""");
        var service = new AppSettingsService(_path);

        AppSettings settings = service.Load();

        Assert.Equal("local", settings.LlmMode);
        Assert.Equal("", settings.LlmBaseUrl);
        Assert.Null(settings.LlmApiKey);
        Assert.Equal("Hy-MT2-1.8B", settings.LlmApiModel);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", settings.LlmActiveModel);
        Assert.Null(settings.LlmSystemPrompt);
        Assert.Null(settings.LlmUserPrompt);
    }

    [Fact]
    public async Task Update_ConcurrentWriters_PreserveBothFields()
    {
        var ocrWriter = new AppSettingsService(_path);
        var languageWriter = new AppSettingsService(_path);
        ocrWriter.Save(AppSettings.Default);
        using var start = new ManualResetEventSlim(false);
        Task[] writers = Enumerable.Range(0, 24).SelectMany(_ => new[]
        {
            Task.Run(() =>
            {
                start.Wait();
                ocrWriter.Update(current => current with { OcrDet = "PP-OCRv6_medium_det" });
            }),
            Task.Run(() =>
            {
                start.Wait();
                languageWriter.Update(current => current with { Dst = "en" });
            }),
        }).ToArray();

        start.Set();
        await Task.WhenAll(writers);

        AppSettings settings = ocrWriter.Load();
        Assert.Equal("PP-OCRv6_medium_det", settings.OcrDet);
        Assert.Equal("en", settings.Dst);
    }

    [Fact]
    public void Update_NullUpdater_Throws()
    {
        var service = new AppSettingsService(_path);

        Assert.Throws<ArgumentNullException>(() => service.Update(null!));
    }
}
