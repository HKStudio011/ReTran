using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using Xunit;

namespace RetranCore.Tests;

public class JsonRpcFramingTests
{
    [Fact]
    public void Serialize_ProducesSingleLine_WithoutTrailingNewline()
    {
        var req = new JsonRpcRequest("1", "core.ping", null);
        string line = JsonRpc.SerializeRequest(req);
        Assert.DoesNotContain('\n', line);
        Assert.StartsWith("{", line);
    }

    [Fact]
    public void ParseLine_RoundTrips_ARequest()
    {
        var req = new JsonRpcRequest("1", "core.ping", null);
        JsonNode? back = JsonRpc.ParseLine(JsonRpc.SerializeRequest(req));
        Assert.NotNull(back);
        Assert.Equal("2.0", back!["jsonrpc"]!.GetValue<string>());
        Assert.Equal("core.ping", back["method"]!.GetValue<string>());
    }

    [Fact]
    public void ParseLine_ReturnsNull_ForBlankLine()
    {
        Assert.Null(JsonRpc.ParseLine(""));
        Assert.Null(JsonRpc.ParseLine("   "));
    }
}
