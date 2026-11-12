using System.Diagnostics;
using System.Text.Json.Nodes;
using ReTran.Core.Ocr;

namespace RetranCore.Tests;

public class LineGrouperTests
{
    private static JsonObject Spot(double x, double y, double w, double h, string text, double score = 0.9) =>
        new()
        {
            ["box"] = new JsonArray(
                new JsonArray(x, y), new JsonArray(x + w, y),
                new JsonArray(x + w, y + h), new JsonArray(x, y + h)),
            ["text"] = text,
            ["score"] = score,
        };

    private static void AssertPoint(JsonNode? node, double x, double y)
    {
        var pt = (JsonArray?)node;
        Assert.NotNull(pt);
        Assert.Equal(x, (double)pt[0]!, 10);
        Assert.Equal(y, (double)pt[1]!, 10);
    }

    private static bool HasParagraph(JsonObject spot) =>
        spot.TryGetPropertyValue("paragraph", out var p) && (bool)p!;

    [Fact]
    public void TwoLeftAlignedLines_MergeIntoOneBlock()
    {
        // gap = 136 - (100 + 24) = 12 <= 0.6 * 24 = 14.4 -> merge
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "HELLO", score: 0.9),
            Spot(100, 136, 300, 24, "WORLD", score: 0.8));

        var result = LineGrouper.Group(spots);

        Assert.Single(result);
        var block = (JsonObject)result[0]!;
        Assert.Equal("HELLO WORLD", (string?)block["text"]);
        Assert.True(HasParagraph(block));
        Assert.Equal(0.8, (double)block["score"]!, 10); // min of member scores
        var box = (JsonArray)block["box"]!;
        // union AABB quad of (100,100,300,60)
        AssertPoint(box[0], 100, 100);
        AssertPoint(box[1], 400, 100);
        AssertPoint(box[2], 400, 160);
        AssertPoint(box[3], 100, 160);
    }

    [Fact]
    public void CenteredLines_MergeByOverlap()
    {
        // top (150,100), bottom (200,136): left offset 50px but overlap = 250 >= 0.5 * 300 -> merge
        var spots = new JsonArray(
            Spot(150, 100, 300, 24, "TOP"),
            Spot(200, 136, 300, 24, "BOTTOM"));

        var result = LineGrouper.Group(spots);

        Assert.Single(result);
        var block = (JsonObject)result[0]!;
        Assert.Equal("TOP BOTTOM", (string?)block["text"]);
        Assert.True(HasParagraph(block));
    }

    [Fact]
    public void SideBySideColumns_DoNotMerge()
    {
        // same row, far apart horizontally -> overlap negative -> no merge
        var spots = new JsonArray(
            Spot(100, 100, 200, 24, "LEFT"),
            Spot(600, 100, 200, 24, "RIGHT"));

        var result = LineGrouper.Group(spots);

        Assert.Equal(2, result.Count);
        foreach (var item in result)
            Assert.False(HasParagraph((JsonObject)item!));
        // reading order: LEFT (x=100) before RIGHT (x=600)
        Assert.Equal("LEFT", (string?)((JsonObject)result[0]!)["text"]);
        Assert.Equal("RIGHT", (string?)((JsonObject)result[1]!)["text"]);
    }

    [Fact]
    public void LargeVerticalGap_DoNotMerge()
    {
        // gap = 154 - (100 + 24) = 30 > 0.6 * 24 = 14.4 -> no merge
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "A"),
            Spot(100, 154, 300, 24, "B"));

        var result = LineGrouper.Group(spots);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void HeightRatioAbove1p35_DoNotMerge()
    {
        // heights 24 and 40 -> ratio 1.67 > 1.35 (other two conditions pass) -> no merge
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "A"),
            Spot(100, 130, 300, 40, "B"));

        var result = LineGrouper.Group(spots);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ThreeLineChain_MergesIntoOneBlock_ReadingOrder()
    {
        // 3 lines in one column; a side-column spot interleaved on the same row as line 2.
        // The chain must not be broken: all 3 lines merge, side spot stays separate.
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "L1"),
            Spot(100, 136, 300, 24, "L2"),
            Spot(500, 136, 200, 24, "SIDE"), // same row as L2, different column
            Spot(100, 172, 300, 24, "L3"));

        var result = LineGrouper.Group(spots);

        Assert.Equal(2, result.Count);
        var block = (JsonObject)result[0]!;
        Assert.Equal("L1 L2 L3", (string?)block["text"]);
        Assert.True(HasParagraph(block));
        var side = (JsonObject)result[1]!;
        Assert.Equal("SIDE", (string?)side["text"]);
        Assert.False(HasParagraph(side));
    }

    [Fact]
    public void BlankTextLines_NotMerged_NoStraySpaces()
    {
        // blank-text spot is unmergeable even when geometrically adjacent to a real line
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "   "),
            Spot(100, 136, 300, 24, "HELLO"));

        var result = LineGrouper.Group(spots);

        Assert.Equal(2, result.Count);
        var hello = result.Select(i => (JsonObject)i!).Single(o => (string?)o["text"] == "HELLO");
        Assert.Equal("HELLO", (string?)hello["text"]); // no stray spaces
        Assert.False(HasParagraph(hello));
    }

    [Fact]
    public void DegenerateQuad_PassesThrough_Unchanged()
    {
        // 4 identical points -> zero-area box -> unmergeable; a geometrically-eligible normal spot must not merge with it
        var degenerate = new JsonObject
        {
            ["box"] = new JsonArray(
                new JsonArray(50, 50), new JsonArray(50, 50),
                new JsonArray(50, 50), new JsonArray(50, 50)),
            ["text"] = "DEGEN",
            ["score"] = 0.9,
        };
        var spots = new JsonArray(degenerate, Spot(50, 84, 300, 24, "NORMAL"));

        var result = LineGrouper.Group(spots); // must not throw

        Assert.Equal(2, result.Count);
        foreach (var item in result)
            Assert.False(HasParagraph((JsonObject)item!));
    }

    [Fact]
    public void MissingBoxOrText_PassesThrough()
    {
        var missingBox = new JsonObject { ["text"] = "NOBOX", ["score"] = 0.9 }; // no box key
        var good = Spot(100, 100, 300, 24, "GOOD");
        var missingText = new JsonObject // valid box, no text key
        {
            ["box"] = new JsonArray(
                new JsonArray(100, 150), new JsonArray(400, 150),
                new JsonArray(400, 174), new JsonArray(100, 174)),
            ["score"] = 0.9,
        };
        var spots = new JsonArray(missingBox, good, missingText);

        var result = LineGrouper.Group(spots); // must not throw

        Assert.Equal(3, result.Count);
        Assert.Contains(result, i => (string?)((JsonObject)i!)["text"] == "GOOD");
        Assert.Contains(result, i => (string?)((JsonObject)i!)["text"] == "NOBOX");
        Assert.Contains(result, i => !((JsonObject)i!).TryGetPropertyValue("text", out _)); // the text-less spot passed through
    }

    [Fact]
    public void EmptyArray_ReturnsEmpty()
    {
        var result = LineGrouper.Group(new JsonArray());

        Assert.Empty(result);
    }

    [Fact]
    public void SingleSpot_Unchanged_NoParagraphFlag()
    {
        var spots = new JsonArray(Spot(100, 100, 300, 24, "ONLY"));

        var result = LineGrouper.Group(spots);

        Assert.Single(result);
        var o = (JsonObject)result[0]!;
        Assert.Equal("ONLY", (string?)o["text"]);
        Assert.False(o.TryGetPropertyValue("paragraph", out _)); // key must be absent, not false
    }

    [Fact]
    public void ManyLinesTwoColumns_GroupsPerColumn_Fast()
    {
        var spots = new JsonArray();
        const int rows = 150; // 2 columns x 150 lines
        for (int i = 0; i < rows; i++)
        {
            double y = 100 + i * 32; // height 24 + gap 8 -> gap <= 0.6 * 24
            spots.Add(Spot(100, y, 300, 24, $"A{i}"));
            spots.Add(Spot(600, y, 300, 24, $"B{i}"));
        }

        // warm-up call so the timed run excludes JIT cost
        LineGrouper.Group(spots);

        var sw = Stopwatch.StartNew();
        var result = LineGrouper.Group(spots);
        sw.Stop();

        Assert.Equal(2, result.Count); // one block per column
        foreach (var item in result)
            Assert.True(HasParagraph((JsonObject)item!));
        Assert.True(sw.ElapsedMilliseconds < 5, $"Group took {sw.ElapsedMilliseconds} ms (budget 5 ms)");
    }

    // ---- configurable thresholds: MergeThresholds + Group(spots, thresholds) ----

    [Fact]
    public void DefaultThresholds_MatchConsts()
    {
        Assert.Equal(new MergeThresholds(0.6, 1.35, 0.5), MergeThresholds.Default);
    }

    [Fact]
    public void Group_OneArgOverload_EqualsDefaultThresholds()
    {
        // gap 14 <= 0.6 * 24 = 14.4 -> merge; gap 20 > 14.4 -> stay apart:
        // pins that the 1-arg overload uses exactly the consts, not 0/1 defaults.
        var mergeable = new JsonArray(
            Spot(100, 100, 300, 24, "HELLO"),
            Spot(100, 138, 300, 24, "WORLD"));
        var apart = new JsonArray(
            Spot(100, 100, 300, 24, "HELLO"),
            Spot(100, 144, 300, 24, "WORLD"));

        var plainMerged = LineGrouper.Group(mergeable);
        var cfgMerged = LineGrouper.Group(mergeable, MergeThresholds.Default);
        var plainSplit = LineGrouper.Group(apart);
        var cfgSplit = LineGrouper.Group(apart, MergeThresholds.Default);

        Assert.Single(plainMerged);
        Assert.True(HasParagraph((JsonObject)plainMerged[0]!));
        Assert.Equal(plainMerged.ToJsonString(), cfgMerged.ToJsonString());

        Assert.Equal(2, plainSplit.Count);
        foreach (var item in plainSplit)
            Assert.False(HasParagraph((JsonObject)item!));
        Assert.Equal(plainSplit.ToJsonString(), cfgSplit.ToJsonString());
    }

    [Fact]
    public void Group_GapRatio2_MergesLinesConstsWouldSplit()
    {
        // gap 30 > default 0.6 * 24 = 14.4 (see LargeVerticalGap_DoNotMerge) but <= 2.0 * 24 = 48 -> merge
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "A"),
            Spot(100, 154, 300, 24, "B"));

        var result = LineGrouper.Group(spots, new MergeThresholds(2.0, 1.35, 0.5));

        Assert.Single(result);
        var block = (JsonObject)result[0]!;
        Assert.True(HasParagraph(block));
        Assert.Equal("A B", (string?)block["text"]);
    }

    [Fact]
    public void Group_HeightRatio3_MergesBigWithSmall()
    {
        // heights 24 and 40 -> ratio 1.67 > default 1.35 (see HeightRatioAbove1p35_DoNotMerge) but <= 3.0 -> merge
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "A"),
            Spot(100, 130, 300, 40, "B"));

        var result = LineGrouper.Group(spots, new MergeThresholds(0.6, 3.0, 0.5));

        Assert.Single(result);
        Assert.True(HasParagraph((JsonObject)result[0]!));
    }

    [Fact]
    public void Group_HeightRatio1_KeepsThemApart()
    {
        // same geometry, gap and overlap relaxed; only the height cap (1.0 < 1.67) keeps them apart
        var spots = new JsonArray(
            Spot(100, 100, 300, 24, "A"),
            Spot(100, 130, 300, 40, "B"));

        var result = LineGrouper.Group(spots, new MergeThresholds(2.0, 1.0, 0.0));

        Assert.Equal(2, result.Count);
        foreach (var item in result)
            Assert.False(HasParagraph((JsonObject)item!));
    }

    [Fact]
    public void Group_OverlapRatio0_MergesSideBySideColumns()
    {
        // same row, columns exactly adjacent (100..300 | 300..500): overlap 0 >= 0 * 200 -> merge
        // (default 0.5 keeps these apart, see SideBySideColumns_DoNotMerge)
        var spots = new JsonArray(
            Spot(100, 100, 200, 24, "LEFT"),
            Spot(300, 100, 200, 24, "RIGHT"));

        var result = LineGrouper.Group(spots, new MergeThresholds(0.6, 1.35, 0.0));

        Assert.Single(result);
        var block = (JsonObject)result[0]!;
        Assert.True(HasParagraph(block));
        Assert.Equal("LEFT RIGHT", (string?)block["text"]);
    }

    [Fact]
    public void Group_OverlapRatio0p9_KeepsColumnsApart()
    {
        // same geometry, gap and height relaxed; overlap 0 < 0.9 * 200 -> no merge
        var spots = new JsonArray(
            Spot(100, 100, 200, 24, "LEFT"),
            Spot(300, 100, 200, 24, "RIGHT"));

        var result = LineGrouper.Group(spots, new MergeThresholds(0.6, 1.35, 0.9));

        Assert.Equal(2, result.Count);
        foreach (var item in result)
            Assert.False(HasParagraph((JsonObject)item!));
    }

    [Theory]
    [InlineData(0.6, 1.35, 0.5, true)]   // defaults
    [InlineData(0.09, 1.35, 0.5, false)] // gap below 0.1
    [InlineData(2.01, 1.35, 0.5, false)] // gap above 2.0
    [InlineData(0.6, 0.99, 0.5, false)]  // height below 1.0
    [InlineData(0.6, 3.01, 0.5, false)]  // height above 3.0
    [InlineData(0.6, 1.35, 1.01, false)] // overlap above 1.0
    [InlineData(0.6, 1.35, 0.0, true)]   // overlap floor is allowed
    [InlineData(0.1, 1.0, 1.0, true)]    // lower bounds inclusive
    [InlineData(2.0, 3.0, 1.0, true)]    // upper bounds inclusive
    public void IsValid_Ranges(double gap, double height, double overlap, bool expected)
    {
        Assert.Equal(expected, new MergeThresholds(gap, height, overlap).IsValid());
    }
}
