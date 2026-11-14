using System.Net;
using System.Text.Json;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class OpenAiChatClientTests
{
    private sealed class CapturingHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public string? LastUrl;
        public string? LastBody;
        public string? LastAuth;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastUrl = req.RequestUri!.ToString();
            LastAuth = req.Headers.Authorization?.ToString();
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    private static string ChatBody(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    [Fact]
    public async Task Complete_FillsMessagesAndParams()
    {
        var h = new CapturingHandler(HttpStatusCode.OK, ChatBody("  xin chào  "));
        var client = new OpenAiChatClient(new HttpClient(h));

        string result = await client.CompleteAsync(
            "http://127.0.0.1:9999", "k", "m", "SYS", "USER", local: false, TestContext.Current.CancellationToken);

        Assert.Equal("xin chào", result); // content is trimmed — nội dung được trim
        Assert.Equal("http://127.0.0.1:9999/v1/chat/completions", h.LastUrl);
        Assert.Equal("Bearer k", h.LastAuth);

        using var doc = JsonDocument.Parse(h.LastBody!);
        JsonElement root = doc.RootElement;
        Assert.Equal("m", root.GetProperty("model").GetString());
        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYS", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("USER", messages[1].GetProperty("content").GetString());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.6, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Complete_Remote_OmitsLlamaOnlyParams()
    {
        var h = new CapturingHandler(HttpStatusCode.OK, ChatBody("ok"));
        var client = new OpenAiChatClient(new HttpClient(h));

        await client.CompleteAsync("http://x", null, "m", "", "u", local: false, TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(h.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("top_k", out _));
        Assert.False(doc.RootElement.TryGetProperty("repeat_penalty", out _));
        Assert.Null(h.LastAuth);
        // Empty system prompt → a single user message. System rỗng → chỉ còn một message user.
        Assert.Equal(1, doc.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Complete_Local_AddsLlamaOnlyParams()
    {
        var h = new CapturingHandler(HttpStatusCode.OK, ChatBody("ok"));
        var client = new OpenAiChatClient(new HttpClient(h));

        await client.CompleteAsync("http://x", null, "m", "", "u", local: true, TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(h.LastBody!);
        Assert.Equal(20, doc.RootElement.GetProperty("top_k").GetInt32());
        Assert.Equal(1.05, doc.RootElement.GetProperty("repeat_penalty").GetDouble(), 3);
    }

    [Fact]
    public async Task Complete_Non200_Throws()
    {
        var client = new OpenAiChatClient(new HttpClient(new CapturingHandler(HttpStatusCode.BadRequest, "boom")));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync(
            "http://x", null, "m", "", "u", local: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Complete_BadShape_Throws()
    {
        var client = new OpenAiChatClient(new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{\"nope\":true}")));

        await Assert.ThrowsAsync<FormatException>(() => client.CompleteAsync(
            "http://x", null, "m", "", "u", local: false, TestContext.Current.CancellationToken));
    }
}

public class LlmSettingsTests
{
    [Fact]
    public void EffectivePrompts_FallBackToDefaults()
    {
        var settings = new LlmSettings("api", "http://x", null, "m", null, null);

        Assert.Equal(PromptTemplate.DefaultSystemPrompt, settings.EffectiveSystem);
        Assert.Equal(PromptTemplate.DefaultUserPrompt, settings.EffectiveUser);
        Assert.False(settings.IsLocal);
        Assert.True(new LlmSettings("LOCAL", null, null, "m", null, null).IsLocal);
    }
}
