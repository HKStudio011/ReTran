using System.Text.Json.Nodes;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class OcrPipelineTests
{
    private sealed class FakeSidecarRpc : ISidecarRpc
    {
        public JsonNode? NextResult { get; set; }
        public Exception? Throw { get; set; }
        public TimeSpan Delay { get; set; }
        public string? LastMethod { get; private set; }
        public JsonNode? LastParams { get; private set; }

        public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            LastMethod = method;
            LastParams = @params;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Throw is not null) throw Throw;
            return NextResult;
        }
    }

    private static JsonObject SpotResult(params string[] texts)
    {
        var spots = new JsonArray();
        foreach (var t in texts)
            spots.Add(new JsonObject
            {
                ["box"] = new JsonArray(
                    new JsonArray(0, 0), new JsonArray(1, 0),
                    new JsonArray(1, 1), new JsonArray(0, 1)),
                ["text"] = t,
                ["score"] = 0.9,
            });
        return new JsonObject { ["spots"] = spots, ["elapsed_ms"] = 12 };
    }

    private static JsonObject SpotAt(double x, double y, double w, double h, string text) =>
        new()
        {
            ["box"] = new JsonArray(
                new JsonArray(x, y), new JsonArray(x + w, y),
                new JsonArray(x + w, y + h), new JsonArray(x, y + h)),
            ["text"] = text,
            ["score"] = 0.9,
        };

    private static JsonObject TwoLineResult() => new()
    {
        ["spots"] = new JsonArray(
            SpotAt(100, 100, 300, 24, "HELLO"),
            SpotAt(100, 136, 300, 24, "WORLD")),
        ["elapsed_ms"] = 5,
    };

    /// <summary>
    /// Two 24 px-tall lines with a 30 px vertical gap (154 − (100 + 24)): above the default gap
    /// cap of 0.6 × 24 = 14.4 px, so the default thresholds keep them apart.
    /// Hai dòng cao 24 px cách nhau 30 px dọc (154 − (100 + 24)): lớn hơn ngưỡng gap mặc định
    /// 0.6 × 24 = 14.4 px, nên ngưỡng mặc định giữ chúng tách riêng.
    /// </summary>
    private static JsonObject TwoFarLinesResult() => new()
    {
        ["spots"] = new JsonArray(
            SpotAt(100, 100, 300, 24, "HELLO"),
            SpotAt(100, 154, 300, 24, "WORLD")),
        ["elapsed_ms"] = 5,
    };

    [Fact]
    public async Task SpotAsync_PassesImageB64_AndAppendsNewTexts()
    {
        var fake = new FakeSidecarRpc { NextResult = SpotResult("HELLO", "WORLD") };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        var result = await pipeline.SpotAsync("BASE64PNG", TestContext.Current.CancellationToken);
        Assert.Equal("ocr.spot", fake.LastMethod);
        Assert.Equal("BASE64PNG", fake.LastParams!["image"]!.GetValue<string>());
        Assert.Equal(12, result["elapsed_ms"]!.GetValue<int>());
        Assert.Equal(["HELLO", "WORLD"], result["new_texts"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task SpotAsync_SecondCall_SameTexts_ReturnsEmptyNewTexts()
    {
        var fake = new FakeSidecarRpc { NextResult = SpotResult("HELLO") };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        await pipeline.SpotAsync("x", TestContext.Current.CancellationToken);
        var second = await pipeline.SpotAsync("x", TestContext.Current.CancellationToken);
        Assert.Empty(second["new_texts"]!.AsArray());
    }

    [Fact]
    public async Task SpotAsync_SidecarError_PropagatesAsSidecarRpcException()
    {
        var fake = new FakeSidecarRpc { Throw = new SidecarRpcException(-32602, "invalid PNG image: bad") };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        var ex = await Assert.ThrowsAsync<SidecarRpcException>(() => pipeline.SpotAsync("x", TestContext.Current.CancellationToken));
        Assert.Equal(-32602, ex.Code);
        Assert.Contains("invalid PNG", ex.Message);
    }

    [Fact]
    public async Task SpotAsync_Timeout_ThrowsTimeoutException()
    {
        var fake = new FakeSidecarRpc { Delay = TimeSpan.FromSeconds(5) };
        var pipeline = new OcrPipeline(fake, new OcrDiff()) { Timeout = TimeSpan.FromMilliseconds(200) };
        await Assert.ThrowsAsync<TimeoutException>(() => pipeline.SpotAsync("x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SpotAsync_NoSpotsArray_ThrowsInvalidOperation()
    {
        var fake = new FakeSidecarRpc { NextResult = new JsonObject() };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.SpotAsync("x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SpotAsync_MergeOn_SpotsMerged_AndNewTextsAreBlocks()
    {
        var fake = new FakeSidecarRpc { NextResult = TwoLineResult() };
        var pipeline = new OcrPipeline(fake, new OcrDiff(), mergeLines: true);
        var result = await pipeline.SpotAsync("BASE64PNG", TestContext.Current.CancellationToken);

        var spots = result["spots"]!.AsArray();
        Assert.Single(spots);
        var block = (JsonObject)spots[0]!;
        Assert.Equal("HELLO WORLD", (string?)block["text"]);
        Assert.True(block.TryGetPropertyValue("paragraph", out var para) && (bool)para!);
        Assert.Equal(["HELLO WORLD"], result["new_texts"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task SpotAsync_MergeOn_SecondFrameChangedLine_RetranslatesWholeBlock()
    {
        var fake = new FakeSidecarRpc { NextResult = TwoLineResult() };
        var pipeline = new OcrPipeline(fake, new OcrDiff(), mergeLines: true);
        await pipeline.SpotAsync("BASE64PNG", TestContext.Current.CancellationToken);

        // Frame 2: line 2 changed to "EARTH" -> the block text changes as a whole.
        fake.NextResult = new JsonObject
        {
            ["spots"] = new JsonArray(
                SpotAt(100, 100, 300, 24, "HELLO"),
                SpotAt(100, 136, 300, 24, "EARTH")),
            ["elapsed_ms"] = 5,
        };
        var second = await pipeline.SpotAsync("BASE64PNG", TestContext.Current.CancellationToken);

        // Exactly the new block text — never the individual lines (Review Focus #4).
        Assert.Equal(["HELLO EARTH"], second["new_texts"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task SpotAsync_MergeOff_Default_OutputUnchanged()
    {
        var fake = new FakeSidecarRpc { NextResult = TwoLineResult() };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        var result = await pipeline.SpotAsync("BASE64PNG", TestContext.Current.CancellationToken);

        // Regression M3: without mergeLines the reply keeps the original spots untouched.
        var spots = result["spots"]!.AsArray();
        Assert.Equal(2, spots.Count);
        foreach (var s in spots)
            Assert.False(((JsonObject)s!).TryGetPropertyValue("paragraph", out _));
        Assert.Equal(["HELLO", "WORLD"], result["new_texts"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task SpotAsync_MergeOn_CustomGap_MergesWhatDefaultSplits()
    {
        var fake = new FakeSidecarRpc { NextResult = TwoFarLinesResult() };

        // Same frame, default thresholds: the 30 px gap exceeds 0.6 × 24 = 14.4 px, so the
        // lines stay separate — the custom gap below must change something real.
        // Cùng khung, ngưỡng mặc định: gap 30 px vượt 0.6 × 24 = 14.4 px nên hai dòng
        // vẫn tách — ngưỡng gap tùy chọn bên dưới phải thay đổi điều gì đó thực sự.
        var baseline = new OcrPipeline(fake, new OcrDiff(), mergeLines: true);
        var split = await baseline.SpotAsync("BASE64PNG", TestContext.Current.CancellationToken);
        Assert.Equal(2, split["spots"]!.AsArray().Count);

        // Gap cap 2.0 × 24 = 48 px covers the 30 px gap -> one merged block: the cap is
        // configurable per session, not a compile-time constant.
        // Ngưỡng gap 2.0 × 24 = 48 px bao được gap 30 px => một khối gộp: ngưỡng này cấu hình
        // được theo phiên, không phải hằng số biên dịch.
        var merged = new OcrPipeline(fake, new OcrDiff(), mergeLines: true,
            new MergeThresholds(2.0, LineGrouper.MaxHeightRatio, LineGrouper.MinOverlapRatio));
        var result = await merged.SpotAsync("BASE64PNG", TestContext.Current.CancellationToken);

        var spots = result["spots"]!.AsArray();
        Assert.Single(spots);
        var block = (JsonObject)spots[0]!;
        Assert.Equal("HELLO WORLD", (string?)block["text"]);
        Assert.True(block.TryGetPropertyValue("paragraph", out var para) && (bool)para!);
        Assert.Equal(["HELLO WORLD"], result["new_texts"]!.AsArray().Select(n => n!.GetValue<string>()));
    }
}
