using Bunit;
using ReTran_App.Services;
using Xunit;

public class ChromeLocalizationTests : BunitContext
{
    private static I18nService NewLang(string lang)
    {
        var i18n = new I18nService();
        i18n.Set(lang);
        return i18n;
    }

    [Fact]
    public void NavMenu_ShowsControl_InCurrentLang_And_Updates_OnSwitch()
    {
        var i18n = NewLang("vi");
        Services.AddSingleton(i18n);
        var cut = Render<ReTran_App.Components.Layout.NavMenu>();
        Assert.Contains("Điều khiển", cut.Markup);
        Assert.DoesNotContain("Điều khiển / Control", cut.Markup);

        i18n.Set("en");
        cut.WaitForAssertion(() => Assert.Contains("Control", cut.Markup));
        Assert.DoesNotContain("Điều khiển / Control", cut.Markup); // chuỗi kép cũ không được quay lại
    }

    [Fact]
    public void NavMenu_ShowsSettingsLink_InCurrentLang()
    {
        var i18n = NewLang("vi");
        Services.AddSingleton(i18n);
        var cut = Render<ReTran_App.Components.Layout.NavMenu>();
        Assert.Contains("Cài đặt", cut.Markup);
        Assert.NotNull(cut.Find("a[href='settings']")); // link ⚙ trỏ đúng route

        i18n.Set("en");
        cut.WaitForAssertion(() => Assert.Contains("Settings", cut.Markup));
        Assert.DoesNotContain("Cài đặt", cut.Markup); // không còn chuỗi vi khi đã sang en
    }

    [Fact]
    public void NotFound_ShowsVietnamese_WhenVi()
    {
        Services.AddSingleton(NewLang("vi"));
        var cut = Render<ReTran_App.Components.Pages.NotFound>();
        Assert.Contains("Không tìm thấy", cut.Markup);
        Assert.Contains("Xin lỗi", cut.Markup);
    }

    [Fact]
    public void NotFound_ShowsEnglish_WhenEn()
    {
        Services.AddSingleton(NewLang("en"));
        var cut = Render<ReTran_App.Components.Pages.NotFound>();
        Assert.Contains("Not Found", cut.Markup);
        Assert.Contains("Sorry, the content you are looking for does not exist.", cut.Markup);
    }
}
