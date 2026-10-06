using System.Text.Json.Nodes;
using ReTran.Core.Serve;

namespace RetranCore.Tests;

public class SpotGeometryTests
{
    [Fact]
    public void NormalizeQuad_KnownPixels()
    {
        var quad = JsonNode.Parse("[[100,50],[300,50],[300,150],[100,150]]")!.AsArray();
        (double x, double y, double w, double h) = SpotGeometry.NormalizeQuad(quad, 1000, 500);
        Assert.Equal(0.1, x, 6); Assert.Equal(0.1, y, 6);
        Assert.Equal(0.2, w, 6); Assert.Equal(0.2, h, 6);
    }

    [Fact]
    public void NormalizeQuad_ClampsToUnitSquare()
    {
        var quad = JsonNode.Parse("[[-50,-50],[1200,-50],[1200,600],[-50,600]]")!.AsArray();
        (double x, double y, double w, double h) = SpotGeometry.NormalizeQuad(quad, 1000, 500);
        Assert.Equal(0, x); Assert.Equal(0, y);
        Assert.True(x + w <= 1.0000001 && y + h <= 1.0000001);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[[1,2],[3,4]]")]
    [InlineData("\"not-an-array\"")] // controller ruling: JSON string hợp lệ (brief ghi "not-an-array" — JsonNode.Parse ném JsonReaderException, không chạm NormalizeQuad)
    public void NormalizeQuad_Malformed_ReturnsZeros(string json)
    {
        JsonArray? quad = JsonNode.Parse(json) as JsonArray;
        (double x, double y, double w, double h) = SpotGeometry.NormalizeQuad(quad!, 100, 100);
        Assert.Equal((0, 0, 0, 0), (x, y, w, h));
    }
}
