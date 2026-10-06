namespace ReTran.Core.Serve;

using System.Text.Json.Nodes;

/// <summary>
/// Pure geometry helpers converting OCR quads (pixel coordinates) to overlay boxes.
/// Các hàm geometry thuần chuyển quad OCR (toạ độ pixel) sang box overlay.
/// </summary>
public static class SpotGeometry
{
    /// <summary>
    /// Normalizes a 4-point pixel quad to an axis-aligned box in the unit square [0,1].
    /// Chuẩn hoá quad 4 điểm (pixel) về box trục song của đơn vị [0,1].
    /// </summary>
    /// <param name="quad">
    /// Four points <c>[[x,y],…]</c> in frame pixels; <see langword="null"/> is treated as malformed.
    /// Bốn điểm <c>[[x,y],…]</c> theo pixel của khung; <see langword="null"/> coi như hỏng.
    /// </param>
    /// <param name="frameWidth">Frame width in pixels. Chiều rộng khung theo pixel.</param>
    /// <param name="frameHeight">Frame height in pixels. Chiều cao khung theo pixel.</param>
    /// <returns>
    /// Box <c>(X, Y, W, H)</c> with each component clamped so the box stays inside the unit
    /// square; <c>(0, 0, 0, 0)</c> when the quad or the frame size is malformed — this path
    /// must never throw because it runs inside the display loop. Box <c>(X, Y, W, H)</c> với
    /// từng thành phần được clamp để box nằm trọn trong đơn vị vuông; <c>(0, 0, 0, 0)</c> khi
    /// quad hoặc kích thước khung hỏng — path này tuyệt đối không được ném exception vì nó
    /// chạy giữa vòng hiển thị.
    /// </returns>
    public static (double X, double Y, double W, double H) NormalizeQuad(
        JsonArray? quad, int frameWidth, int frameHeight)
    {
        if (quad is null || quad.Count != 4)
        {
            return (0, 0, 0, 0);
        }

        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return (0, 0, 0, 0);
        }

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;

        foreach (JsonNode? point in quad)
        {
            if (point is not JsonArray p || p.Count < 2)
            {
                return (0, 0, 0, 0);
            }

            if (!TryNumber(p[0], out double px) || !TryNumber(p[1], out double py))
            {
                return (0, 0, 0, 0);
            }

            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);
        }

        // Clamp each bound first so the box cannot exceed the unit square even when the quad
        // extends past the frame edges. Clamp từng biên trước để box không vượt đơn vị vuông
        // kể cả khi quad tràn ra ngoài khung.
        double left = Clamp01(minX / frameWidth);
        double right = Clamp01(maxX / frameWidth);
        double top = Clamp01(minY / frameHeight);
        double bottom = Clamp01(maxY / frameHeight);

        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Reads the first two coordinates of a quad point as doubles.
    /// Đọc toạ độ hai phần tử đầu của một điểm quad thành double.
    /// </summary>
    /// <returns><see langword="true"/> when the node is a JSON number; <see langword="false"/> otherwise. <see langword="true"/> khi node là số JSON; ngược lại <see langword="false"/>.</returns>
    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue<double>(out value);
    }

    /// <summary>Clamps a value to the unit interval [0,1]. Clamp giá trị về khoảng đơn vị [0,1].</summary>
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
