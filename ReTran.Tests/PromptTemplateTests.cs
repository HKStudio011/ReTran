using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class PromptTemplateTests
{
    [Fact]
    public void Apply_FillsAllPlaceholders()
    {
        string filled = PromptTemplate.Apply(
            "src={source_lang} dst={target_lang} text={source_text}", "en", "vi", "hello");

        Assert.Equal("src=English dst=Vietnamese text=hello", filled);
    }

    [Fact]
    public void Apply_AutoSource_KeepsAuto()
    {
        string filled = PromptTemplate.Apply("{source_lang}:{target_lang}:{source_text}", "auto", "ja", "x");

        Assert.Equal("auto:Japanese:x", filled);
    }

    [Fact]
    public void Apply_MissingSourceText_Throws()
    {
        Assert.Throws<FormatException>(() =>
            PromptTemplate.Apply("Translate into {target_lang}.", "en", "vi", "hello"));
    }

    [Fact]
    public void Defaults_MatchHyMt2Card()
    {
        // Verbatim from the Hy-MT2 model card (spec 2026-11-14 §0) — do not reword.
        // Nguyên văn từ model card Hy-MT2 (spec 2026-11-14 §0) — không đổi chữ.
        Assert.Equal(
            "Translate the following text into {target_lang}. Note that you should only output the translated result without any additional explanation:\n\n{source_text}",
            PromptTemplate.DefaultUserPrompt);
        Assert.Equal(PromptTemplate.DefaultSystemPrompt, string.Empty);
    }
}

public class LanguageNamesTests
{
    [Theory]
    [InlineData("vi", "Vietnamese")]
    [InlineData("ja", "Japanese")]
    [InlineData("zh", "Chinese")]
    [InlineData("en", "English")]
    [InlineData("ko", "Korean")]
    [InlineData("ru", "Russian")]
    [InlineData("auto", "auto")]
    [InlineData("xx-unknown", "xx-unknown")]
    public void For_MapsKnownAndFallsBack(string code, string expected)
    {
        Assert.Equal(expected, LanguageNames.For(code));
    }
}
