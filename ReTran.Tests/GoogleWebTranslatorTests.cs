using System.Net;
using System.Text.Json;
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

    /// <summary>
    /// Records every request URL and echoes the requested <c>q</c> back as a Google-shaped body,
    /// one segment per line (originals echo verbatim, translations wrap each line in guillemets).
    /// Ghi lại mọi URL yêu cầu và echo tham số <c>q</c> thành body đúng dạng Google, mỗi dòng một segment.
    /// </summary>
    private sealed class EchoHandler : HttpMessageHandler
    {
        /// <summary>Appends <c>X</c> to every echoed original so the response no longer matches. Thêm <c>X</c> vào bản gốc echo để phản hồi không còn khớp.</summary>
        public bool CorruptOriginals { get; init; }

        /// <summary>Splits every line into two segments to exercise accumulation. Cắt mỗi dòng thành hai segment để kiểm tra cộng dồn.</summary>
        public bool SplitSegmentsPerLine { get; init; }

        /// <summary>Request URLs seen, in order. Các URL yêu cầu đã nhận, theo thứ tự.</summary>
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var uri = req.RequestUri!;
            // AbsoluteUri keeps the escaping (ToString would decode %20 to a space).
            Urls.Add(uri.AbsoluteUri);

            var qParam = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .First(p => p.StartsWith("q=", StringComparison.Ordinal));
            var q = Uri.UnescapeDataString(qParam["q=".Length..]);

            var lines = q.Split('\n');
            var segments = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                // Google appends the newline separator to every line but the last. Google gắn dấu ngăn
                // cách xuống dòng vào mọi dòng trừ dòng cuối.
                var suffix = i < lines.Length - 1 ? "\n" : string.Empty;
                if (SplitSegmentsPerLine)
                {
                    var cut = lines[i].Length / 2;
                    segments.Add(Segment(lines[i][..cut], string.Empty));
                    segments.Add(Segment(lines[i][cut..], suffix));
                }
                else
                {
                    segments.Add(Segment(lines[i], suffix));
                }
            }

            var body = "[[" + string.Join(",", segments) + "],null,\"en\"]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });

            string Segment(string source, string tail)
            {
                var original = (CorruptOriginals ? source + "X" : source) + tail;
                var translated = "«" + source + "»" + tail;
                return $"[{JsonSerializer.Serialize(translated)},{JsonSerializer.Serialize(original)},null,null,1]";
            }
        }
    }

    [Fact]
    public async Task Batch_SingleRequest_TranslatesAllTexts()
    {
        var h = new EchoHandler();
        var t = new GoogleWebTranslator(h);

        var result = await t.TranslateBatchAsync(["A", "B"], "en", "vi", TestContext.Current.CancellationToken);

        Assert.Single(h.Urls);
        Assert.Contains("q=A%0AB", h.Urls[0]);
        Assert.Equal(new[] { "«A»", "«B»" }, result);
    }

    [Fact]
    public async Task Batch_TextWithNewline_IsNormalizedBeforeJoin()
    {
        var h = new EchoHandler();
        var t = new GoogleWebTranslator(h);

        var result = await t.TranslateBatchAsync(["A\nB"], "en", "vi", TestContext.Current.CancellationToken);

        Assert.Single(h.Urls);
        Assert.Contains("q=A%20B", h.Urls[0]);
        Assert.DoesNotContain("%0A", h.Urls[0]);
        Assert.Equal(new[] { "«A B»" }, result);
    }

    [Fact]
    public async Task Batch_OriginalMismatch_ThrowsFormatException()
    {
        var h = new EchoHandler { CorruptOriginals = true };
        var t = new GoogleWebTranslator(h);

        await Assert.ThrowsAsync<FormatException>(() =>
            t.TranslateBatchAsync(["AB"], "en", "vi", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Batch_MultiSegmentPerText_MapsBack()
    {
        var h = new EchoHandler { SplitSegmentsPerLine = true };
        var t = new GoogleWebTranslator(h);

        var result = await t.TranslateBatchAsync(["AB"], "en", "vi", TestContext.Current.CancellationToken);

        Assert.Single(h.Urls);
        Assert.Equal(new[] { "«A»«B»" }, result);
    }

    [Fact]
    public async Task Batch_Chunks_ByUrlLength_OrderPreserved()
    {
        var text = new string('x', 900);
        Assert.True((text.Length * 2) + 1 > GoogleWebTranslator.MaxBatchUrlLength);

        var h = new EchoHandler();
        var t = new GoogleWebTranslator(h);

        var result = await t.TranslateBatchAsync([text, text, text], "en", "vi", TestContext.Current.CancellationToken);

        Assert.Equal(3, h.Urls.Count);
        Assert.Equal(new[] { "«" + text + "»", "«" + text + "»", "«" + text + "»" }, result);
    }
}
