using Bunit;
using Microsoft.AspNetCore.Components;
using ReTran_App.Services;
using Xunit;
using AngleSharp.Html.Dom;
using IElement = AngleSharp.Dom.IElement; // ambiguous with Microsoft.Maui.IElement via MAUI global usings

public class SettingsPageTests : BunitContext, IDisposable
{
    private readonly string _settingsPath =
        Path.Combine(Path.GetTempPath(), $"retran-settings-{Guid.NewGuid():N}.json");

    private AppSettingsService AddSettingsServices()
    {
        var i18n = new I18nService();
        i18n.Set("vi"); // test tường minh theo vi — không phụ thuộc culture máy
        Services.AddSingleton(i18n);
        var settings = new AppSettingsService(_settingsPath);
        Services.AddSingleton(settings);
        return settings;
    }

    // BunitContext.Dispose không virtual — tái hiện IDisposable để chạy cleanup rồi mới dispose bUnit.
    public new void Dispose()
    {
        try { File.Delete(_settingsPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        base.Dispose();
    }

    [Fact]
    public void Renders_GoogleSelected_OthersDisabled()
    {
        AddSettingsServices();
        var cut = Render<ReTran_App.Components.Pages.Settings>();

        var providerSelect = (IHtmlSelectElement)cut.FindAll("select")[0];
        Assert.Equal("google", providerSelect.Value); // google là lựa chọn hiện tại
        var options = providerSelect.QuerySelectorAll("option");
        Assert.Equal(3, options.Length);
        Assert.False(options[0].HasAttribute("disabled")); // google enabled
        Assert.True(options[1].HasAttribute("disabled")); // deepl — chưa có
        Assert.True(options[2].HasAttribute("disabled")); // llm — chưa có
        Assert.Contains("sắp có", providerSelect.TextContent);

        // Spec §6.2: label provider đầy đủ (không rút gọn) + 2 option disabled kèm nhãn "sắp có".
        Assert.Contains("Google (web, không cần key)", providerSelect.TextContent);
        Assert.Contains("DeepL", providerSelect.TextContent);
        Assert.Contains("LLM (OpenAI-compatible)", providerSelect.TextContent);

        // Spec §6.2: nhãn = tên ngôn ngữ (native), src có "Tự động (auto)"; value giữ code.
        IHtmlSelectElement srcSelect = (IHtmlSelectElement)cut.FindAll("select")[1];
        IHtmlSelectElement dstSelect = (IHtmlSelectElement)cut.FindAll("select")[2];
        Assert.Contains("Tự động (auto)", srcSelect.TextContent);
        Assert.Contains("日本語", srcSelect.TextContent);
        Assert.Contains("Tiếng Việt", dstSelect.TextContent);
        Assert.Contains("English", dstSelect.TextContent);
        Assert.Equal("vi", dstSelect.Value); // value vẫn là code, không phải tên hiển thị

        // Google web không cần key → input api-key bị disabled.
        Assert.True(cut.Find("input").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Save_WritesSettingsFile_AndShowsStatus()
    {
        AppSettingsService settings = AddSettingsServices();
        var cut = Render<ReTran_App.Components.Pages.Settings>();

        cut.FindAll("select")[1].Change("ja"); // ngôn ngữ gốc
        cut.FindAll("select")[2].Change("en"); // ngôn ngữ đích
        cut.Find("button").Click();            // Lưu cài đặt

        await cut.WaitForAssertionAsync(() => Assert.Contains("Đã lưu", cut.Markup));

        Assert.True(File.Exists(_settingsPath));
        AppSettings saved = settings.Load();
        Assert.Equal("google", saved.Provider);
        Assert.Equal("ja", saved.Src);
        Assert.Equal("en", saved.Dst);
    }
}
