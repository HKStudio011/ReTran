using ReTran_App.Services;
using Xunit;

public class I18nServiceTests
{
    private static I18nService Create() => new();

    [Fact]
    public void EveryKey_Exists_In_Both_Languages()
    {
        var svc = Create();
        var keys = svc.Keys.ToList();
        Assert.NotEmpty(keys);
        foreach (var lang in new[] { "vi", "en" })
        {
            svc.Set(lang);
            foreach (var key in keys)
            {
                Assert.NotEqual(key, svc[key]); // giá trị phải khác key ở MỖI lang
            }
        }
    }

    [Fact]
    public void MissingKey_Returns_Key()
    {
        var svc = Create();
        Assert.Equal("nope.missing", svc["nope.missing"]);
    }

    [Fact]
    public void Fmt_Formats_Arguments()
    {
        var svc = Create();
        svc.Set("vi");
        var s = svc.Fmt("home.status.coreError", "boom");
        Assert.Contains("boom", s);
        Assert.Contains("Lỗi core", s);
    }

    [Fact]
    public void Set_Raises_Changed_And_Ignores_Invalid()
    {
        var svc = Create();
        svc.Set("vi"); // pin known start lang before subscribing — may fire on en-US machines, no listener yet
        var fired = 0;
        svc.Changed += () => fired++;

        svc.Set("en");
        Assert.Equal("en", svc.Lang);
        Assert.Equal(1, fired);

        svc.Set("en"); // cùng lang → không phát lại
        Assert.Equal(1, fired);

        svc.Set("fr"); // không hợp lệ → bỏ qua
        Assert.Equal("en", svc.Lang);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Default_Lang_Is_Vi_Or_En()
    {
        Assert.Contains(Create().Lang, new[] { "vi", "en" });
    }

    [Fact]
    public void LanguageNames_HaveNativeTamilTelugu()
    {
        Assert.Equal("தமிழ்", I18nStrings.LanguageName("ta"));
        Assert.Equal("తెలుగు", I18nStrings.LanguageName("te"));
    }
}
