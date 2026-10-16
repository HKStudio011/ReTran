using System.Net;
using ReTran.Core.Translate;

namespace RetranCore.Tests;

public class GoogleWebTranslatorTests
{
    private sealed class StubHandler(HttpStatusCode code, string body, int delayMs = 0) : HttpMessageHandler
    {
        public string? LastUrl;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastUrl = req.RequestUri!.ToString();
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task Translate_ConcatenatesSegments()
    {
        var h = new StubHandler(HttpStatusCode.OK, "[[[\"Xin \",\"hello\",null,null,1],[\"chào\",\"hello\",null,null,1]]]");
        var t = new GoogleWebTranslator(h);
        Assert.Equal("Xin chào", await t.TranslateAsync("hello", "en", "vi", TestContext.Current.CancellationToken));
        Assert.Contains("client=gtx", h.LastUrl);
        Assert.Contains("sl=en", h.LastUrl); Assert.Contains("tl=vi", h.LastUrl);
        Assert.Contains("q=hello", h.LastUrl);
    }

    [Fact]
    public async Task Translate_SrcAuto_PassesAuto()
    {
        var h = new StubHandler(HttpStatusCode.OK, "[[[\"chào\",null,null,null,1]]]");
        await new GoogleWebTranslator(h).TranslateAsync("hi", "auto", "vi", TestContext.Current.CancellationToken);
        Assert.Contains("sl=auto", h.LastUrl);
    }

    [Fact]
    public async Task Translate_HttpError_Throws()
    {
        var t = new GoogleWebTranslator(new StubHandler(HttpStatusCode.InternalServerError, "boom"));
        await Assert.ThrowsAsync<HttpRequestException>(() => t.TranslateAsync("x", "en", "vi", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Translate_SlowerThanTimeout_ThrowsTimeout()
    {
        var t = new GoogleWebTranslator(new StubHandler(HttpStatusCode.OK, "[]", delayMs: 300), timeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => t.TranslateAsync("x", "en", "vi", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Registry_ReturnsNullForUnknown_AndIsCaseInsensitive()
    {
        var r = new TranslatorRegistry();
        r.Register(new GoogleWebTranslator(new StubHandler(HttpStatusCode.OK, "[]")));
        Assert.NotNull(r.Get("GOOGLE"));
        Assert.Null(r.Get("deepl"));
    }
}
