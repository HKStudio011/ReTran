using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ReTran_App.Core;
using Xunit;

public class OverlayComponentTests : BunitContext
{
    [Fact]
    public void EmptyState_Renders_NoBoxes()
    {
        Services.AddSingleton(new OverlayState());
        var cut = Render<ReTran_App.Components.Pages.Overlay>();
        Assert.Empty(cut.FindAll("div[style*='position:absolute']"));
    }

    [Fact]
    public void PublishedFrame_Renders_TranslatedText_InGreenBox()
    {
        var state = new OverlayState();
        Services.AddSingleton(state);
        var cut = Render<ReTran_App.Components.Pages.Overlay>();
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow, new[]
        {
            new OverlayBox(0.1, 0.2, 0.3, 0.06, "こんにちは", "Xin chào"),
        }));
        cut.WaitForAssertion(() => Assert.Contains("Xin chào", cut.Markup));
        var box = cut.Find("div[style*='position:absolute']");
        Assert.Contains("left:10%", box.GetAttribute("style"));
        Assert.Contains("#22c55e", box.GetAttribute("style"));
        Assert.DoesNotContain("こんにちは", cut.Markup); // chỉ hiện bản dịch, không hiện gốc
    }
}
