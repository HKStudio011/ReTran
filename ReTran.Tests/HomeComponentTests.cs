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
        Assert.Contains("Feed server not running", cut.Markup);

        ButtonByText(cut, "Sao chép / Copy").Click();
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

        ButtonByText(cut, "Tối / Dark").Click();
        cut.WaitForAssertion(() => Assert.Contains("Theme: dark.", cut.Markup));

        var invocation = module.VerifyInvoke("setTheme");
        Assert.Equal("dark", invocation.Arguments[0]);
    }
}
