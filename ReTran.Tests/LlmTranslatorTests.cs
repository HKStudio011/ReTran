using System.Net;
using System.Text.Json;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlmTranslatorTests
{
    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? LastUrl;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastUrl = req.RequestUri!.ToString();
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private static string ChatBody(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    private static string UserContent(CapturingHandler h)
    {
        using var doc = JsonDocument.Parse(h.LastBody!);
        JsonElement messages = doc.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
    }

    [Fact]
    public async Task Translator_AppliesDefaultPrompt()
    {
        var h = new CapturingHandler(ChatBody("  dịch  "));
        var settings = new LlmSettings("api", "http://x", null, "m", null, null);
        var translator = new LlmTranslator(new OpenAiChatClient(new HttpClient(h)), settings);

        Assert.Equal("llm", translator.Name);
        string result = await translator.TranslateAsync("hello", "en", "vi", TestContext.Current.CancellationToken);

        Assert.Equal("dịch", result);
        Assert.Equal("http://x/v1/chat/completions", h.LastUrl);
        Assert.Equal(
            PromptTemplate.Apply(PromptTemplate.DefaultUserPrompt, "en", "vi", "hello"),
            UserContent(h));
    }

    [Fact]
    public async Task Translator_CustomPrompts_Win()
    {
        var h = new CapturingHandler(ChatBody("ok"));
        var settings = new LlmSettings("api", "http://x", null, "m", "S", "T:{source_text}:{target_lang}");
        var translator = new LlmTranslator(new OpenAiChatClient(new HttpClient(h)), settings);

        await translator.TranslateAsync("hello", "en", "ja", TestContext.Current.CancellationToken);

        Assert.Equal("T:hello:Japanese", UserContent(h));
        using var doc = JsonDocument.Parse(h.LastBody!);
        Assert.Equal("S", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Translator_EndpointProvider_ResolvesBaseUrlLazily()
    {
        var h = new CapturingHandler(ChatBody("ok"));
        var settings = new LlmSettings("local", null, null, "m", null, null);
        int ensured = 0;
        var translator = new LlmTranslator(
            new OpenAiChatClient(new HttpClient(h)),
            settings,
            _ => { ensured++; return Task.FromResult("http://lazy:1234"); });

        await translator.TranslateAsync("a", "en", "vi", TestContext.Current.CancellationToken);

        Assert.Equal(1, ensured);
        Assert.Equal("http://lazy:1234/v1/chat/completions", h.LastUrl);
    }

    [Fact]
    public async Task Translator_NoBaseUrlNoProvider_Throws()
    {
        var settings = new LlmSettings("local", null, null, "m", null, null);
        var translator = new LlmTranslator(new OpenAiChatClient(new HttpClient(new CapturingHandler(ChatBody("x")))), settings);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync("a", "en", "vi", TestContext.Current.CancellationToken));
    }
}
