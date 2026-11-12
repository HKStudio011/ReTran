using System.Text.Json.Nodes;

namespace ReTran.Core.Ocr;

/// <summary>
/// Merges nearby OCR text lines into single paragraph blocks before translation and rendering.
/// Gộp các dòng OCR gần nhau thành một khối đoạn văn trước khi dịch và hiển thị.
/// </summary>
public static class LineGrouper
{
    /// <summary>
    /// Maximum vertical gap between two lines, as a fraction of the shorter line's height, for them to merge (design spec D4).
    /// Khoảng cách dọc tối đa giữa hai dòng để gộp, tính theo tỷ lệ chiều cao của dòng thấp hơn (spec thiết kế D4).
    /// </summary>
    public const double MaxGapRatio = 0.6;

    /// <summary>
    /// Maximum ratio of the taller line's height to the shorter line's height for them to merge (design spec D4).
    /// Tỷ lệ chiều cao tối đa của dòng cao hơn so với dòng thấp hơn để gộp (spec thiết kế D4).
    /// </summary>
    public const double MaxHeightRatio = 1.35;

    /// <summary>
    /// Minimum horizontal overlap between two lines, as a fraction of the narrower line's width, for them to merge (design spec D4).
    /// Độ chồng ngang tối thiểu giữa hai dòng để gộp, tính theo tỷ lệ chiều rộng của dòng hẹp hơn (spec thiết kế D4).
    /// </summary>
    public const double MinOverlapRatio = 0.5;

    private readonly record struct Box(double X, double Y, double W, double H);

    private readonly record struct Line(JsonObject Spot, Box Box, string Text, double Score, int Index);

    /// <summary>
    /// Merges nearby lines in the spot array into paragraph blocks, keeping reading order.
    /// Gộp các dòng gần nhau trong mảng spot thành khối đoạn văn, giữ nguyên thứ tự đọc.
    /// </summary>
    /// <param name="spots">OCR spots shaped as {box: [[x,y]×4] pixels, text, score}. Các spot OCR dạng {box: [[x,y]×4] pixel, text, score}.</param>
    /// <returns>
    /// A new array in the same shape; a merged block (≥ 2 lines) carries "paragraph": true and its union box.
    /// Mảng mới cùng shape; khối gộp (≥ 2 dòng) có thêm "paragraph": true và box hợp nhất của các dòng thành viên.
    /// </returns>
    public static JsonArray Group(JsonArray spots) => Group(spots, MergeThresholds.Default);

    /// <summary>
    /// Merges nearby lines in the spot array into paragraph blocks, keeping reading order, using caller-supplied merge thresholds.
    /// Gộp các dòng gần nhau trong mảng spot thành khối đoạn văn, giữ nguyên thứ tự đọc, theo ngưỡng gộp do nơi gọi cung cấp.
    /// </summary>
    /// <param name="spots">OCR spots shaped as {box: [[x,y]×4] pixels, text, score}. Các spot OCR dạng {box: [[x,y]×4] pixel, text, score}.</param>
    /// <param name="thresholds">Merge caps to apply instead of the constants. Ngưỡng gộp được dùng thay cho các hằng số.</param>
    /// <returns>
    /// A new array in the same shape; a merged block (≥ 2 lines) carries "paragraph": true and its union box.
    /// Mảng mới cùng shape; khối gộp (≥ 2 dòng) có thêm "paragraph": true và box hợp nhất của các dòng thành viên.
    /// </returns>
    public static JsonArray Group(JsonArray spots, MergeThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(spots);

        var lines = new List<Line>();
        var passthrough = new List<(JsonNode? Node, int Index)>();
        for (int i = 0; i < spots.Count; i++)
        {
            if (spots[i] is not JsonObject spot)
            {
                passthrough.Add((spots[i]?.DeepClone(), i));
                continue;
            }
            if (TryParseLine(spot, i, out Line line))
            {
                lines.Add(line);
            }
            else
            {
                // Deep-clone: a node keeps its parent until it is re-parented into the result array.
                passthrough.Add((spot.DeepClone(), i));
            }
        }

        // Reading order: top-to-bottom, then left-to-right.
        var sorted = lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ThenBy(l => l.Index).ToList();

        // Scan with multiple open groups: a line joins the first group whose last line it can merge
        // with; otherwise it opens its own group. Earlier groups stay open so a same-column chain is
        // not broken by an interleaved spot from another column; the gap check still caps how far
        // down any group can reach.
        var groups = new List<List<Line>>();
        foreach (var line in sorted)
        {
            List<Line>? target = null;
            foreach (var g in groups)
            {
                if (CanMerge(g[^1].Box, line.Box, thresholds)) { target = g; break; }
            }
            if (target is null)
            {
                target = new List<Line>();
                groups.Add(target);
            }
            target.Add(line);
        }

        var items = new List<(JsonNode? Node, double Y, double X, int Index)>();
        foreach (var g in groups)
        {
            if (g.Count == 1)
            {
                Line l = g[0];
                // Clone: the original spot stays parented to the input array.
                items.Add((l.Spot.DeepClone(), l.Box.Y, l.Box.X, l.Index));
            }
            else
            {
                items.Add((MakeBlock(g), g.Min(m => m.Box.Y), g.Min(m => m.Box.X), g[0].Index));
            }
        }
        foreach (var (node, index) in passthrough)
        {
            // Spots without usable geometry sort last, stable by input order.
            double py = double.MaxValue, px = double.MaxValue;
            if (node is JsonObject po && TryGetBox(po, out Box pb)) { py = pb.Y; px = pb.X; }
            items.Add((node, py, px, index));
        }

        var result = new JsonArray();
        foreach (var item in items.OrderBy(t => t.Y).ThenBy(t => t.X).ThenBy(t => t.Index))
            result.Add(item.Node);
        return result;
    }

    private static bool TryParseLine(JsonObject spot, int index, out Line line)
    {
        line = default!;
        if (!TryGetBox(spot, out Box box) || box.W < 1 || box.H < 1) return false;
        if (spot["text"] is not JsonValue tv || !tv.TryGetValue<string>(out string? text)) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        double score = 0;
        if (spot["score"] is JsonValue sv && TryToDouble(sv, out double s)) score = s;
        line = new Line(spot, box, text!, score, index);
        return true;
    }

    private static bool TryGetBox(JsonObject spot, out Box box)
    {
        box = default;
        if (spot["box"] is not JsonArray pts || pts.Count != 4) return false;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < 4; i++)
        {
            if (pts[i] is not JsonArray p || p.Count != 2) return false;
            if (!TryToDouble(p[0], out double x) || !TryToDouble(p[1], out double y)) return false;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        box = new Box(minX, minY, maxX - minX, maxY - minY);
        return true;
    }

    /// <summary>Applies the three merge conditions (design spec D4) with the given thresholds between a candidate line and the last line of a group.</summary>
    private static bool CanMerge(Box a, Box b, MergeThresholds t)
    {
        // (a) vertical gap — no lower bound: overlapping lines are fine.
        if (b.Y - (a.Y + a.H) > t.GapRatio * Math.Min(a.H, b.H)) return false;
        // (b) height ratio.
        double minH = Math.Min(a.H, b.H);
        if (minH <= 0 || Math.Max(a.H, b.H) / minH > t.HeightRatio) return false;
        // (c) horizontal overlap.
        double overlapX = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
        return overlapX >= t.MinOverlapRatio * Math.Min(a.W, b.W);
    }

    /// <summary>Builds the merged block spot: union box as a rectangle quad, trimmed texts joined by space, min score.</summary>
    private static JsonObject MakeBlock(IReadOnlyList<Line> members)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        double minScore = double.MaxValue;
        var texts = new List<string>(members.Count);
        foreach (Line m in members)
        {
            x0 = Math.Min(x0, m.Box.X); y0 = Math.Min(y0, m.Box.Y);
            x1 = Math.Max(x1, m.Box.X + m.Box.W); y1 = Math.Max(y1, m.Box.Y + m.Box.H);
            minScore = Math.Min(minScore, m.Score);
            texts.Add(m.Text.Trim());
        }
        return new JsonObject
        {
            ["box"] = new JsonArray(
                new JsonArray(x0, y0), new JsonArray(x1, y0),
                new JsonArray(x1, y1), new JsonArray(x0, y1)),
            ["text"] = string.Join(" ", texts),
            ["score"] = minScore,
            ["paragraph"] = true,
        };
    }

    /// <summary>Reads a JSON node as a double, accepting the integral numeric types as well.</summary>
    private static bool TryToDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out double d)) { value = d; return true; }
        if (v.TryGetValue<int>(out int i)) { value = i; return true; }
        if (v.TryGetValue<long>(out long l)) { value = l; return true; }
        if (v.TryGetValue<float>(out float f)) { value = f; return true; }
        return false;
    }
}
