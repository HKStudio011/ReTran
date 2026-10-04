using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ReTran_App.Core;
using ReTran_App.Services;
using Xunit;
using IElement = AngleSharp.Dom.IElement; // ambiguous with Microsoft.Maui.IElement via MAUI global usings

public class HomeComponentTests : BunitContext
{
    // FeedServer is registered unstarted (Port == 0), so Home renders this URL.
    private const string ObsUrl = "http://127.0.0.1:0/overlay";

    private static void AddHomeServices(BunitContext ctx)
    {
        var state = new OverlayState();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(new OverlayFeedServer(state, port: 0)); // unstarted → Port == 0
        ctx.Services.AddSingleton(new JsInteropService());
        var i18n = new I18nService();
        i18n.Set("vi"); // test tường minh theo vi — không phụ thuộc culture máy
        ctx.Services.AddSingleton(i18n);
    }

    private static IElement ButtonByText<T>(IRenderedComponent<T> cut, string text) where T : IComponent =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void StartDemo_RunningStatus_And_StopDemo_Stops()
    {
        AddHomeServices(this);
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
        AddHomeServices(this);
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
        AddHomeServices(this);
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
        AddHomeServices(this);
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
}
