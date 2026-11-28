using SD = System.Drawing;
using SDD = System.Drawing.Drawing2D;

namespace ReTran_App.Services.Branding;

/// <summary>
/// Draws the shared ReTran folded-A monogram without a background or font glyph.
/// Vẽ monogram A dải gấp dùng chung của ReTran, không nền và không dùng glyph font.
/// </summary>
internal static class BrandMarkRenderer
{
    private const float ViewBoxSize = 128f;

    /// <summary>
    /// Draws the monogram centered and uniformly scaled inside the supplied bounds, restoring all graphics state.
    /// Vẽ monogram căn giữa và scale đều trong bounds được cung cấp, đồng thời khôi phục toàn bộ trạng thái graphics.
    /// </summary>
    /// <param name="graphics">Target GDI+ graphics surface. Surface GDI+ đích.</param>
    /// <param name="bounds">Destination bounds in the caller's coordinate space. Bounds đích theo hệ tọa độ của caller.</param>
    internal static void Draw(SD.Graphics graphics, SD.RectangleF bounds)
    {
        SDD.GraphicsState state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SDD.SmoothingMode.AntiAlias;

            float scale = Math.Min(bounds.Width / ViewBoxSize, bounds.Height / ViewBoxSize);
            float offsetX = bounds.X + (bounds.Width - ViewBoxSize * scale) / 2f;
            float offsetY = bounds.Y + (bounds.Height - ViewBoxSize * scale) / 2f;
            graphics.TranslateTransform(offsetX, offsetY);
            graphics.ScaleTransform(scale, scale);

            using var amberPen = new SD.Pen(SD.Color.FromArgb(255, 255, 176, 32), 6f)
            {
                StartCap = SDD.LineCap.Round,
                EndCap = SDD.LineCap.Round,
            };
            using var ivoryBrush = new SD.SolidBrush(SD.Color.FromArgb(255, 239, 234, 217));
            using var foldBrush = new SD.SolidBrush(SD.Color.FromArgb(255, 199, 206, 196));
            using var amberBrush = new SD.SolidBrush(SD.Color.FromArgb(255, 255, 176, 32));

            using var arc = new SDD.GraphicsPath();
            arc.AddArc(20f, 20f, 88f, 88f, 45f, 270f);
            graphics.DrawPath(amberPen, arc);

            using var body = new SDD.GraphicsPath();
            body.StartFigure();
            body.AddLine(39f, 94f, 39f, 41f);
            AddQuadratic(body, 39f, 37f, 42f, 34f);
            body.AddLine(42f, 34f, 48f, 28f);
            AddQuadratic(body, 51f, 25f, 54f, 29f);
            body.AddLine(54f, 29f, 94f, 86f);
            AddQuadratic(body, 98f, 92f, 90f, 92f);
            body.AddLine(90f, 92f, 78f, 92f);
            body.AddLine(78f, 92f, 56f, 61f);
            body.AddLine(56f, 61f, 56f, 78f);
            body.AddLine(56f, 78f, 71f, 78f);
            AddQuadratic(body, 75f, 78f, 77f, 82f);
            body.AddLine(77f, 82f, 82f, 90f);
            AddQuadratic(body, 85f, 94f, 79f, 94f);
            body.CloseFigure();
            graphics.FillPath(ivoryBrush, body);

            using var fold = new SDD.GraphicsPath();
            fold.StartFigure();
            fold.AddLine(51f, 27f, 94f, 86f);
            AddQuadratic(fold, 98f, 92f, 90f, 92f);
            fold.AddLine(90f, 92f, 78f, 92f);
            fold.AddLine(78f, 92f, 56f, 61f);
            fold.AddLine(56f, 61f, 61f, 47f);
            fold.CloseFigure();
            graphics.FillPath(foldBrush, fold);

            using var accent = new SDD.GraphicsPath();
            accent.StartFigure();
            accent.AddLine(56f, 78f, 71f, 78f);
            AddQuadratic(accent, 75f, 78f, 77f, 82f);
            accent.AddLine(77f, 82f, 82f, 90f);
            AddQuadratic(accent, 85f, 94f, 79f, 94f);
            accent.AddLine(79f, 94f, 69f, 94f);
            accent.AddLine(69f, 94f, 64f, 86f);
            accent.AddLine(64f, 86f, 56f, 86f);
            accent.CloseFigure();
            graphics.FillPath(amberBrush, accent);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    /// <summary>
    /// Appends a quadratic curve as its exact cubic Bezier equivalent.
    /// Nối một đường cong quadratic dưới dạng cubic Bezier tương đương chính xác.
    /// </summary>
    private static void AddQuadratic(
        SDD.GraphicsPath path,
        float controlX,
        float controlY,
        float endX,
        float endY)
    {
        SD.PointF start = path.GetLastPoint();
        path.AddBezier(
            start.X,
            start.Y,
            start.X + 2f / 3f * (controlX - start.X),
            start.Y + 2f / 3f * (controlY - start.Y),
            endX + 2f / 3f * (controlX - endX),
            endY + 2f / 3f * (controlY - endY),
            endX,
            endY);
    }
}
