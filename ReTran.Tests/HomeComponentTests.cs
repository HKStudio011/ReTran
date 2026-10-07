using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using Xunit;
using IElement = AngleSharp.Dom.IElement; // ambiguous with Microsoft.Maui.IElement via MAUI global usings

public class HomeComponentTests : BunitContext, IDisposable
{
    // FeedServer is registered unstarted (Port == 0), so Home renders this URL.
    private const string ObsUrl = "http://127.0.0.1:0/overlay";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"retran-home-{Guid.NewGuid():N}.db");
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"retran-home-{Guid.NewGuid():N}.json");
    private readonly FakeCorePeer _peer = new() { IsStarted = true };
    private RetranDbContext? _db;

    // DI thật (PipelineController) trên fake ICorePeer + DB/settings file temp —
    // Home khởi động PipelineController từ OnInitialized nên phải đăng ký đủ.
    private void AddHomeServices()
    {
        var state = new OverlayState();
        Services.AddSingleton(state);
        Services.AddSingleton(new OverlayFeedServer(state, port: 0)); // unstarted → Port == 0
        Services.AddSingleton(new JsInteropService());
        var i18n = new I18nService();
        i18n.Set("vi"); // test tường minh theo vi — không phụ thuộc culture máy
        Services.AddSingleton(i18n);

        var options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new RetranDbContext(options);
        _db.Database.Migrate();
        Services.AddSingleton(_db);
        var cache = new TranslationCache(_db);
        Services.AddSingleton(cache);

        var settings = new AppSettingsService(_settingsPath);
        Services.AddSingleton(settings);
        Services.AddSingleton(new PipelineController(
            _peer, settings, cache, state, NullLogger<PipelineController>.Instance));
    }

    // BunitContext.Dispose không virtual — tái hiện IDisposable để chạy cleanup rồi mới dispose bUnit.
    public new void Dispose()
    {
        _db?.Dispose();
        foreach (string path in new[] { _dbPath, _settingsPath })
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        base.Dispose();
    }

    private static IElement ButtonByText<T>(IRenderedComponent<T> cut, string text) where T : IComponent =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    // So khớp chính xác cả label — "Bắt đầu" không được lẫn "Bắt đầu Demo".
    private static IElement ButtonExact<T>(IRenderedComponent<T> cut, string text) where T : IComponent =>
        cut.FindAll("button").First(b => string.Equals(b.TextContent.Trim(), text, StringComparison.Ordinal));

    [Fact]
    public void StartDemo_RunningStatus_And_StopDemo_Stops()
    {
        AddHomeServices();
        var cut = Render<ReTran_App.Components.Pages.Home>();
        Assert.Contains("đã dừng", cut.Markup); // initial status

        ButtonByText(cut, "Bắt đầu Demo").Click();
        cut.WaitForAssertion(() => Assert.Contains("đang chạy", cut.Markup));

        ButtonByText(cut, "Dừng Demo").Click();
        cut.WaitForAssertion(() => Assert.Contains("đã dừng", cut.Markup));
    }

    [Fact]
    public void CopyUrl_Calls_CopyText_WithObsUrl()
    {
        AddHomeServices();
        var module = JSInterop.SetupModule("./build/assets/main.js");
        module.SetupVoid("copyText", _ => true).SetVoidResult();
        var cut = Render<ReTran_App.Components.Pages.Home>();
        Assert.Contains(ObsUrl, cut.Markup); // URL rendered in the OBS section
        Assert.Contains("Máy chủ feed chưa chạy", cut.Markup); // vi feed warning

        ButtonByText(cut, "Sao chép").Click();
        cut.WaitForAssertion(() => Assert.Contains("Đã sao chép", cut.Markup));

        var invocation = module.VerifyInvoke("copyText");
        Assert.Equal(ObsUrl, invocation.Arguments[0]);
    }

    [Fact]
    public void ThemeButton_Sets_StatusText()
    {
        AddHomeServices();
        var module = JSInterop.SetupModule("./build/assets/main.js");
        module.SetupVoid("setTheme", _ => true).SetVoidResult();
        var cut = Render<ReTran_App.Components.Pages.Home>();

        ButtonByText(cut, "Tối").Click();
        cut.WaitForAssertion(() => Assert.Contains("Theme: dark.", cut.Markup));

        var invocation = module.VerifyInvoke("setTheme");
        Assert.Equal("dark", invocation.Arguments[0]);
    }

    [Fact]
    public void LanguageSwitcher_SwapsMarkup_And_Persists()
    {
        AddHomeServices();
        var module = JSInterop.SetupModule("./build/assets/main.js");
        module.SetupVoid("setLang", _ => true).SetVoidResult();
        var cut = Render<ReTran_App.Components.Pages.Home>();
        Assert.Contains("Bắt đầu Demo", cut.Markup); // vi là mặc định trong test

        ButtonByText(cut, "EN").Click();
        cut.WaitForAssertion(() => Assert.Contains("Start Demo", cut.Markup));
        Assert.Contains("Feed server not running", cut.Markup); // cảnh báo feed bản en
        var langInvocation = module.VerifyInvoke("setLang"); // persist "en"
        Assert.Equal("en", langInvocation.Arguments[0]);

        ButtonByText(cut, "VI").Click();
        cut.WaitForAssertion(() => Assert.Contains("Bắt đầu Demo", cut.Markup));
    }

    [Fact]
    public async Task StartButton_CallsCoreStart_WithSettingsAndSelection()
    {
        AddHomeServices();
        _peer.Monitors = new[] { new MonitorInfo(0, "Màn hình chính"), new MonitorInfo(1, "Màn hình phụ") };
        _peer.Windows = new[] { new WindowInfo("0x1A2B3C", "Notepad", "notepad.exe") };
        new AppSettingsService(_settingsPath).Save(new AppSettings("google", "auto", "vi", null));
        var cut = Render<ReTran_App.Components.Pages.Home>();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Notepad", cut.Markup)); // nguồn đã nạp

        // Start phải dừng DemoFeed trước khi gửi core.start (plan: tránh 2 nguồn publish cùng overlay).
        ButtonByText(cut, "Bắt đầu Demo").Click();
        cut.WaitForAssertion(() => Assert.Contains("đang chạy", cut.Markup));

        cut.FindAll("select")[0].Change("1"); // chọn màn hình 1
        ButtonExact(cut, "Bắt đầu").Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains(_peer.Calls, c => c.Method == "core.start"));

        (string Method, JsonNode? Params) call = _peer.Calls.Single(c => c.Method == "core.start");
        JsonObject source = call.Params!["source"]!.AsObject();
        Assert.Equal("screen", source["kind"]!.GetValue<string>());
        Assert.Equal(1, source["monitor"]!.GetValue<int>());

        JsonObject translate = call.Params!["translate"]!.AsObject();
        Assert.Equal("google", translate["provider"]!.GetValue<string>());
        Assert.Equal("auto", translate["src"]!.GetValue<string>());
        Assert.Equal("vi", translate["dst"]!.GetValue<string>());
        Assert.Null(call.Params["ocrFps"]);
        Assert.Null(call.Params["captureFps"]);

        Assert.Contains("Demo: đã dừng", cut.Markup);   // demo dừng trước start
        Assert.Contains("Đang chạy", cut.Markup);       // badge theo kết quả core.start

        // Chọn cửa sổ khác rỗng → UseWindow true với đúng hwnd.
        cut.FindAll("select")[1].Change("0x1A2B3C");
        ButtonExact(cut, "Bắt đầu").Click();
        await cut.WaitForAssertionAsync(() => Assert.Equal(2, _peer.Calls.Count(c => c.Method == "core.start")));

        var windowCall = _peer.Calls.Last(c => c.Method == "core.start");
        Assert.Equal("window", windowCall.Params!["source"]!["kind"]!.GetValue<string>());
        Assert.Equal("0x1A2B3C", windowCall.Params!["source"]!["hwnd"]!.GetValue<string>());
    }

    [Fact]
    public async Task StopButton_CallsCoreStop()
    {
        AddHomeServices();
        var cut = Render<ReTran_App.Components.Pages.Home>();
        await cut.WaitForAssertionAsync(() => Assert.Contains(_peer.Calls, c => c.Method == "core.list_sources"));

        ButtonExact(cut, "Bắt đầu").Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Đang chạy", cut.Markup));
        Assert.Contains(_peer.Calls, c => c.Method == "core.start");

        ButtonExact(cut, "Dừng").Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains(_peer.Calls, c => c.Method == "core.stop"));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Đang dừng", cut.Markup));
    }

    [Fact]
    public async Task PipelineStateNotification_UpdatesBadge()
    {
        AddHomeServices();
        var cut = Render<ReTran_App.Components.Pages.Home>();
        // Handler Core→App chỉ gắn khi ListSourcesAsync chạy — chờ nó trước khi bắn notification.
        await cut.WaitForAssertionAsync(() => Assert.Contains(_peer.Calls, c => c.Method == "core.list_sources"));
        Assert.Contains("Đang dừng", cut.Markup); // badge mặc định = trạng thái đã dừng

        _peer.RaiseNotification("pipeline.state", new JsonObject { ["running"] = true });
        await cut.WaitForAssertionAsync(() => Assert.Contains("Đang chạy", cut.Markup));

        _peer.RaiseNotification("pipeline.state", new JsonObject { ["running"] = false });
        await cut.WaitForAssertionAsync(() => Assert.Contains("Đang dừng", cut.Markup));

        _peer.RaiseNotification(
            "pipeline.state",
            new JsonObject { ["running"] = false, ["reason"] = "sidecar-exited" });
        await cut.WaitForAssertionAsync(() => Assert.Contains("Lỗi core: sidecar-exited", cut.Markup));
    }

    private sealed class FakeCorePeer : ICorePeer
    {
        public bool IsStarted { get; set; }
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public IReadOnlyList<MonitorInfo> Monitors { get; set; } = Array.Empty<MonitorInfo>();
        public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();

        public event Action<string, JsonNode?>? NotificationReceived;
        public event Action<Exception>? HandlerError;

        public Task StartCoreAsync(CancellationToken ct)
        {
            IsStarted = true;
            return Task.CompletedTask;
        }

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            Calls.Add((method, @params));
            JsonNode? result = method == "core.list_sources"
                ? new JsonObject
                {
                    ["monitors"] = new JsonArray(
                        Monitors.Select(m => (JsonNode)new JsonObject
                        {
                            ["id"] = m.Id,
                            ["name"] = m.Name,
                        }).ToArray()),
                    ["windows"] = new JsonArray(
                        Windows.Select(w => (JsonNode)new JsonObject
                        {
                            ["hwnd"] = w.Hwnd,
                            ["title"] = w.Title,
                            ["process"] = w.Process,
                        }).ToArray()),
                }
                : new JsonObject { ["running"] = true };
            return Task.FromResult<JsonNode?>(result);
        }

        public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler) { }

        public void RaiseNotification(string method, JsonNode? @params)
            => NotificationReceived?.Invoke(method, @params);

        public void RaiseHandlerError(Exception ex) => HandlerError?.Invoke(ex);
    }
}
