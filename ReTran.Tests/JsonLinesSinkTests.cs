using System.Text.Json.Nodes;
using ReTran.Core.Cli;
using Xunit;

namespace RetranCore.Tests;

public class JsonLinesSinkTests
{
    [Fact]
    public async Task OnCycle_WritesJsonLine_WithM3ContractKeys()
    {
        using var sw = new StringWriter();
        var result = new JsonObject
        {
            ["spots"] = new JsonArray(new JsonObject { ["text"] = "HELLO" }),
            ["elapsed_ms"] = 42,
            ["new_texts"] = new JsonArray("HELLO"),
        };
        await new JsonLinesSink(sw).OnCycleAsync(7, 100, 50, result, CancellationToken.None);

        var line = JsonNode.Parse(sw.ToString()!.Trim())!.AsObject();
        Assert.Equal(7, line["seq"]!.GetValue<long>());
        Assert.Equal(1, line["spots_count"]!.GetValue<int>());
        Assert.Equal(42, line["elapsed_ms"]!.GetValue<int>());
        Assert.Equal("HELLO", line["new_texts"]![0]!.GetValue<string>());
        Assert.NotNull(line["at"]); // ISO-8601 UTC
    }

    [Fact]
    public async Task OnCycle_EmitsEvenWhenNoNewTexts()
    {
        using var sw = new StringWriter();
        var result = new JsonObject { ["spots"] = new JsonArray(), ["new_texts"] = new JsonArray() };
        await new JsonLinesSink(sw).OnCycleAsync(1, 10, 10, result, CancellationToken.None);
        Assert.Contains("\"new_texts\":[]", sw.ToString());
    }
}
