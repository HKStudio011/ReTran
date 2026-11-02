namespace ReTran_App.Services.OverlayWindow;

using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

/// <summary>
/// Geometry and typography shared by every overlay box: teal corner ticks, the dark pill behind
/// the translation, and a font size derived from the box height (never from the viewport width,
/// so short boxes get small text and tall boxes get large text).
/// Hình học và kiểu chữ dùng chung cho mọi box overlay: tick teal 4 góc, nền pill tối sau bản
/// dịch, và cỡ chữ suy ra từ chiều cao box (không bao giờ từ chiều rộng viewport, nhờ vậy box
/// thấp có chữ nhỏ và box cao có chữ lớn).
/// </summary>
/// <remarks>
/// Kept in sync with <c>ReTran.Core/Serve/BoxAnnotationStyle.cs</c>. The App cannot reference
/// Core in-process (it spawns Core as a separate process), so the file is duplicated; change
/// both together.
/// Đồng bộ với <c>ReTran.Core/Serve/BoxAnnotationStyle.cs</c>. App không tham chiếu Core trong
/// cùng process (nó spawn Core như process riêng), nên file được sao chép; sửa thì sửa cả hai.
/// </remarks>
internal static class BoxAnnotationStyle
{
    /// <summary>Padding between the source box edge and the pill (pixels). Đệm giữa mép box gốc và pill (pixel).</summary>
    public const float Inset = 4f;

    /// <summary>Lower bound of the shrink-to-fit loop (pixels). Sàn của vòng thu nhỏ để vừa box (pixel).</summary>
    public const float MinFontSize = 9f;

    /// <summary>Upper bound of the base font size (pixels). Trần của cỡ chữ cơ sở (pixel).</summary>
    public const float MaxFontSize = 40f;

    /// <summary>Base font size as a fraction of the box height. Cỡ chữ cơ sở theo tỉ lệ chiều cao box.</summary>
    public const float FontHeightRatio = 0.5f;

    /// <summary>Tick color #4FD8C4 from the UI mockup. Màu tick #4FD8C4 theo mockup UI.</summary>
    public static Color TickColor { get; } = Color.FromArgb(255, 79, 216, 196);

    /// <summary>Pill fill rgba(5,12,14,.86). Màu nền pill rgba(5,12,14,.86).</summary>
    public static Color PillFill { get; } = Color.FromArgb(222, 5, 12, 14);

    /// <summary>Pill border rgba(239,234,217,.16). Màu viền pill rgba(239,234,217,.16).</summary>
    public static Color PillBorder { get; } = Color.FromArgb(41, 239, 234, 217);

    /// <summary>Text color #F3EEDD. Màu chữ #F3EEDD.</summary>
    public static Color TextColor { get; } = Color.FromArgb(255, 243, 238, 221);

    /// <summary>One-pixel fonts per integer size; bounded by <see cref="MinFontSize"/>..<see cref="MaxFontSize"/>.</summary>
    private static readonly ConcurrentDictionary<int, Font> FontCache = new();

    /// <summary>
    /// Horizontal padding inside the pill for a font size. Đệm ngang trong pill theo cỡ chữ.
    /// </summary>
    /// <param name="size">Font size in pixels. Cỡ chữ theo pixel.</param>
    public static float PadX(int size) => MathF.Max(4f, size * 0.35f);

    /// <summary>
    /// Vertical padding inside the pill for a font size. Đệm dọc trong pill theo cỡ chữ.
    /// </summary>
    /// <param name="size">Font size in pixels. Cỡ chữ theo pixel.</param>
    public static float PadY(int size) => MathF.Max(2f, size * 0.12f);

    /// <summary>
    /// Returns the shared font for an integer pixel size (created on first use and kept for the
    /// process lifetime — the cache is bounded to the supported size range).
    /// Trả font dùng chung cho cỡ nguyên theo pixel (tạo lần đầu, giữ tới hết process — cache bị
    /// chặn trong khoảng cỡ hỗ trợ).
    /// </summary>
    /// <param name="size">Font size in pixels. Cỡ chữ theo pixel.</param>
    public static Font GetFont(int size) =>
        FontCache.GetOrAdd(size, static s => new Font("Segoe UI", s, FontStyle.Regular, GraphicsUnit.Pixel));

    /// <summary>
    /// Creates the text format used for both measuring and drawing (near/near, no clipping, no
    /// mnemonic handling). Tạo format chữ dùng cho cả đo và vẽ (gần/gần, không cắt, không xử lý
    /// ký tự mnemonics).
    /// </summary>
    public static StringFormat CreateTextFormat() => new(StringFormatFlags.NoClip)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.None,
        HotkeyPrefix = HotkeyPrefix.None,
    };

    /// <summary>
    /// Decoration scale for ticks/borders relative to the 1280-wide mockup (0.5 lower bound so
    /// small viewports still show visible corners). Hệ số phóng trang trí (tick/viền) so với
    /// mockup rộng 1280 (sàn 0,5 để viewport nhỏ vẫn thấy góc).
    /// </summary>
    /// <param name="viewportWidth">Viewport width in pixels. Chiều rộng viewport (pixel).</param>
    public static float DecorationScale(float viewportWidth) => MathF.Max(0.5f, viewportWidth / 1280f);

    /// <summary>
    /// Inner pill rectangle matching the mockup: 4 px inset on the left/top/bottom and flush
    /// with the right edge of the source box. Hình chữ nhật pill bên trong khớp mockup: inset
    /// 4 px trái/trên/dưới và sát mép phải của box gốc.
    /// </summary>
    /// <param name="box">Source box in pixels. Box gốc theo pixel.</param>
    public static RectangleF InnerRect(RectangleF box) => new(
        box.X + Inset,
        box.Y + Inset,
        MathF.Max(1f, box.Width - Inset),
        MathF.Max(1f, box.Height - 2f * Inset));

    /// <summary>
    /// Base font size for a box: half the box height, clamped to 12..40 px.
    /// Cỡ chữ cơ sở cho một box: nửa chiều cao box, kẹp 12..40 px.
    /// </summary>
    /// <param name="boxHeight">Box height in pixels. Chiều cao box (pixel).</param>
    /// <param name="maxFontSize">Upper clamp in pixels; callers scale it with the viewport. Trần theo pixel; nơi gọi tự scale theo viewport.</param>
    public static int BaseFontSize(float boxHeight, float maxFontSize = MaxFontSize)
    {
        float size = FontHeightRatio * boxHeight;
        return (int)MathF.Round(Math.Clamp(size, 12f, maxFontSize));
    }

    /// <summary>
    /// Finds the largest font size (from the box-height base down to <see cref="MinFontSize"/>)
    /// whose wrapped block fits the inner rectangle in at most <paramref name="maxLines"/>
    /// lines. Tìm cỡ chữ lớn nhất (từ cỡ cơ sở theo chiều cao box xuống
    /// <see cref="MinFontSize"/>) mà khối chữ sau khi wrap vừa hình chữ nhật trong, tối đa
    /// <paramref name="maxLines"/> dòng.
    /// </summary>
    /// <param name="g">Target graphics (measurement only). Graphics đích (chỉ để đo).</param>
    /// <param name="text">Text to fit. Chữ cần fit.</param>
    /// <param name="innerWidth">Usable width inside the box. Chiều rộng dùng được trong box.</param>
    /// <param name="innerHeight">Usable height inside the box. Chiều cao dùng được trong box.</param>
    /// <param name="boxHeight">Full box height (drives the base size). Chiều cao đầy đủ của box (quyết định cỡ cơ sở).</param>
    /// <param name="format">Format shared with drawing. Format dùng chung với lúc vẽ.</param>
    /// <param name="maxLines">Maximum wrapped lines (callers pass 2 for a single line, 4 for a merged paragraph block). Số dòng wrap tối đa (nơi gọi truyền 2 cho một dòng đơn, 4 cho khối đoạn văn gộp).</param>
    /// <param name="shrink">Whether to shrink below the base size; <see langword="false"/> keeps the base and lets the caller ellipsize. Có thu nhỏ dưới cỡ cơ sở không; <see langword="false"/> giữ cỡ cơ sở để người gọi cắt ellipsis.</param>
    /// <param name="maxFontSize">Upper clamp in pixels; callers scale it with the viewport. Trần theo pixel; nơi gọi tự scale theo viewport.</param>
    /// <returns>The best size, its measured block and whether it fits. Cỡ tốt nhất, khối đã đo và có vừa không.</returns>
    public static TextFit Fit(Graphics g, string text, float innerWidth, float innerHeight,
        float boxHeight, StringFormat format, int maxLines, bool shrink = true,
        float maxFontSize = MaxFontSize)
    {
        int high = BaseFontSize(boxHeight, maxFontSize);
        TextFit atHigh = Measure(g, text, high, innerWidth, innerHeight, format, maxLines);
        if (atHigh.Fits || !shrink) return atHigh;

        int low = (int)MinFontSize;
        TextFit best = Measure(g, text, low, innerWidth, innerHeight, format, maxLines);
        int from = low + 1, to = high - 1;
        while (from <= to)
        {
            int mid = (from + to) / 2;
            TextFit candidate = Measure(g, text, mid, innerWidth, innerHeight, format, maxLines);
            if (candidate.Fits)
            {
                best = candidate;
                from = mid + 1;
            }
            else
            {
                to = mid - 1;
            }
        }
        return best;
    }

    /// <summary>
    /// Measures a wrapped text block at an integer size (same padding/format as drawing). Đo khối
    /// chữ đã wrap ở cỡ nguyên (cùng đệm/format với lúc vẽ).
    /// </summary>
    /// <param name="g">Target graphics. Graphics đích.</param>
    /// <param name="text">Text to measure. Chữ cần đo.</param>
    /// <param name="size">Font size in pixels. Cỡ chữ theo pixel.</param>
    /// <param name="innerWidth">Usable width inside the box. Chiều rộng dùng được trong box.</param>
    /// <param name="format">Format shared with drawing. Format dùng chung với lúc vẽ.</param>
    public static SizeF MeasureBlock(Graphics g, string text, int size, float innerWidth, StringFormat format) =>
        g.MeasureString(text, GetFont(size), new SizeF(MathF.Max(1f, innerWidth - 2f * PadX(size)), 1_000_000f), format);

    /// <summary>
    /// Truncates the text with an ellipsis so it fits the inner rectangle in at most
    /// <paramref name="maxLines"/> lines at the given size. Cắt chữ kèm ellipsis để vừa hình chữ
    /// nhật trong, tối đa <paramref name="maxLines"/> dòng ở cỡ cho trước.
    /// </summary>
    /// <param name="g">Target graphics. Graphics đích.</param>
    /// <param name="text">Text to truncate. Chữ cần cắt.</param>
    /// <param name="size">Font size in pixels. Cỡ chữ theo pixel.</param>
    /// <param name="innerWidth">Usable width inside the box. Chiều rộng dùng được trong box.</param>
    /// <param name="innerHeight">Usable height inside the box. Chiều cao dùng được trong box.</param>
    /// <param name="format">Format shared with drawing. Format dùng chung với lúc vẽ.</param>
    /// <param name="maxLines">Maximum wrapped lines. Số dòng wrap tối đa.</param>
    public static string TruncateToFit(Graphics g, string text, int size, float innerWidth,
        float innerHeight, StringFormat format, int maxLines)
    {
        int low = 0, high = text.Length, best = 0;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            string candidate = Prefix(text, mid) + "…";
            if (Measure(g, candidate, size, innerWidth, innerHeight, format, maxLines).Fits)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return best >= text.Length ? text : Prefix(text, best) + "…";
    }

    /// <summary>
    /// Draws the four corner ticks (two 7-px-scale segments per corner) around a source box. Vẽ
    /// tick 4 góc (mỗi góc hai đoạn, tỉ lệ 7 px) quanh box gốc.
    /// </summary>
    /// <param name="g">Target graphics. Graphics đích.</param>
    /// <param name="pen">Tick pen (color/width/caps). Pen tick (màu/độ dày/đầu nét).</param>
    /// <param name="box">Source box in pixels. Box gốc theo pixel.</param>
    /// <param name="arm">Tick arm length in pixels. Độ dài nhánh tick (pixel).</param>
    public static void DrawTicks(Graphics g, Pen pen, RectangleF box, float arm)
    {
        float x = box.Left, y = box.Top, right = box.Right, bottom = box.Bottom;
        g.DrawLine(pen, x, y + arm, x, y);
        g.DrawLine(pen, x, y, x + arm, y);
        g.DrawLine(pen, right - arm, y, right, y);
        g.DrawLine(pen, right, y, right, y + arm);
        g.DrawLine(pen, right, bottom - arm, right, bottom);
        g.DrawLine(pen, right, bottom, right - arm, bottom);
        g.DrawLine(pen, x + arm, bottom, x, bottom);
        g.DrawLine(pen, x, bottom, x, bottom - arm);
    }

    /// <summary>
    /// Rebuilds a path as a rounded rectangle (radius clamped to half the shorter side). Dựng lại
    /// path thành hình chữ nhật bo góc (bán kính kẹp theo nửa cạnh ngắn hơn).
    /// </summary>
    /// <param name="path">Path to reset and fill. Path cần reset rồi dựng.</param>
    /// <param name="rect">Rectangle to round. Hình chữ nhật cần bo góc.</param>
    /// <param name="radius">Corner radius in pixels. Bán kính góc (pixel).</param>
    public static void BuildRoundedRect(GraphicsPath path, RectangleF rect, float radius)
    {
        path.Reset();
        float d = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) / 2f) * 2f;
        if (d <= 0f)
        {
            path.AddRectangle(rect);
            return;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }

    /// <summary>Measured text block at one size, plus whether it fits the requested line/height budget.</summary>
    private static TextFit Measure(Graphics g, string text, int size, float innerWidth,
        float innerHeight, StringFormat format, int maxLines)
    {
        Font font = GetFont(size);
        SizeF block = MeasureBlock(g, text, size, innerWidth, format);
        int lines = Math.Max(1, (int)MathF.Round(block.Height / font.GetHeight(g)));
        bool fits = lines <= maxLines && block.Height + 2f * PadY(size) <= innerHeight + 0.5f;
        return new TextFit(size, block, lines, fits);
    }

    private static string Prefix(string text, int length)
    {
        int cut = Math.Min(length, text.Length);
        if (cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd();
    }

    /// <summary>Result of <see cref="Fit"/>: chosen size, measured block, line count and fit flag.</summary>
    /// <param name="Size">Chosen font size in pixels. Cỡ chữ đã chọn (pixel).</param>
    /// <param name="Block">Measured wrapped block. Khối chữ đã đo sau wrap.</param>
    /// <param name="Lines">Wrapped line count. Số dòng sau wrap.</param>
    /// <param name="Fits">Whether the block fits the line/height budget. Khối có vừa ngân sách dòng/chiều cao không.</param>
    public readonly record struct TextFit(int Size, SizeF Block, int Lines, bool Fits);
}
