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

    [Fact]
    public async Task SpotAsync_PassesImageB64_AndAppendsNewTexts()
    {
        var fake = new FakeSidecarRpc { NextResult = SpotResult("HELLO", "WORLD") };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        var result = await pipeline.SpotAsync("BASE64PNG");
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
        await pipeline.SpotAsync("x");
        var second = await pipeline.SpotAsync("x");
        Assert.Empty(second["new_texts"]!.AsArray());
    }

    [Fact]
    public async Task SpotAsync_SidecarError_PropagatesAsSidecarRpcException()
    {
        var fake = new FakeSidecarRpc { Throw = new SidecarRpcException(-32602, "invalid PNG image: bad") };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        var ex = await Assert.ThrowsAsync<SidecarRpcException>(() => pipeline.SpotAsync("x"));
        Assert.Equal(-32602, ex.Code);
        Assert.Contains("invalid PNG", ex.Message);
    }

    [Fact]
    public async Task SpotAsync_Timeout_ThrowsTimeoutException()
    {
        var fake = new FakeSidecarRpc { Delay = TimeSpan.FromSeconds(5) };
        var pipeline = new OcrPipeline(fake, new OcrDiff()) { Timeout = TimeSpan.FromMilliseconds(200) };
        await Assert.ThrowsAsync<TimeoutException>(() => pipeline.SpotAsync("x"));
    }

    [Fact]
    public async Task SpotAsync_NoSpotsArray_ThrowsInvalidOperation()
    {
        var fake = new FakeSidecarRpc { NextResult = new JsonObject() };
        var pipeline = new OcrPipeline(fake, new OcrDiff());
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.SpotAsync("x"));
    }
}
