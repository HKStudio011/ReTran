using System.Drawing;
using System.Drawing.Imaging;
using ReTran_App.Services.FloatingMenu;
using Color = System.Drawing.Color;

public class FloatingMenuRendererTests
{
    private static MenuState State(bool collapsed = false, bool running = true, bool statsOpen = false,
        string? errorLine = null) => new(
        Collapsed: collapsed, Running: running, SourceLabel: "Màn hình 1",
        OverlayOn: true, Dst: "vi", Src: "auto", OcrFps: 3, CaptureFps: 10,
        MergeLines: true, StatsOpen: statsOpen, SourceListOpen: false,
        Sources: Array.Empty<SourceItem>(), ErrorLine: errorLine);

    private static Bitmap Render(MenuState s)
    {
        var (w, h) = FloatingMenuLayout.WindowSize(s);
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Match the production surface: grayscale antialiasing, not ClearType subpixel
        // fringes (which would trip the teal detector).
        // Khớp bề mặt production: khử răng cưa xám, không phải ClearType (vệt màu sẽ
        // đánh lừa bộ dò teal).
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        FloatingMenuRenderer.DrawMenu(g, s, w, h);
        return bitmap;
    }

    // A > 0 matters: fully transparent pixels are (0,0,0), which would otherwise count as "dark" and let a
    // blank bitmap pass the coverage tests.
    // A > 0 quan trọng: pixel trong suốt hoàn toàn là (0,0,0), nếu không đếm sẽ để bitmap trống lọt qua test phủ.
    private static bool IsDark(Color c) => c.A > 0 && c.R < 100 && c.G < 100 && c.B < 100;

    private static bool IsTeal(Color c) => c.G > 140 && c.B > 110 && c.G > c.R + 12 && c.B > c.R + 4;

    /// <summary>
    /// Counts pixels matching <paramref name="isInk"/> inside a band and returns the count, the horizontal
    /// span (min..max x) and the longest contiguous run. Đếm pixel khớp isInk trong một dải, trả về số lượng,
    /// khoảng trải ngang (min..max x) và chuỗi liên tiếp dài nhất.
    /// </summary>
    private static (int Count, int Span, int MaxRun) Ink(Bitmap bitmap, int y0, int y1, int x0, int x1,
        Func<Color, bool> isInk)
    {
        int count = 0, maxRun = 0, min = int.MaxValue, max = -1;
        for (int y = y0; y < y1; y++)
        {
            int run = 0;
            for (int x = x0; x < x1; x++)
            {
                if (!isInk(bitmap.GetPixel(x, y))) { run = 0; continue; }
                count++;
                run++;
                maxRun = Math.Max(maxRun, run);
                min = Math.Min(min, x);
                max = Math.Max(max, x);
            }
        }
        return (count, max < 0 ? 0 : max - min + 1, maxRun);
    }

    [Fact]
    public void Expanded_DarkPanelCoversMostOfWindow()
    {
        using Bitmap bitmap = Render(State());
        var (px, py, pw, ph) = FloatingMenuLayout.PanelRect(State());

        int dark = 0, total = 0;
        for (int y = py; y < py + ph; y++)
            for (int x = px; x < px + pw; x++)
            {
                total++;
                if (IsDark(bitmap.GetPixel(x, y))) dark++;
            }

        Assert.True(dark > total * 0.6f,
            $"expected the dark panel to cover > 60% of its rect, got {dark}/{total}");
    }

    [Fact]
    public void Expanded_MarginIsTransparent()
    {
        using Bitmap bitmap = Render(State());

        // Top-left corner of the shadow margin: outside the panel and clear of the offset
        // shadow (which sits down-right of it), so every pixel there must be fully transparent.
        // Góc trái trên của viền shadow: ngoài panel và không chạm shadow lệch, mọi pixel phải trong suốt hẳn.
        for (int y = 0; y < FloatingMenuLayout.ShadowMargin - 1; y++)
            for (int x = 0; x < FloatingMenuLayout.ShadowMargin - 1; x++)
                Assert.True(bitmap.GetPixel(x, y).A == 0,
                    $"shadow-margin pixel ({x},{y}) must be transparent");
    }

    [Fact]
    public void Expanded_HintAndErrorRowsRenderRealText()
    {
        // Regression: TruncateToFit's height rule used to reject every candidate in the 18-px rows, so the
        // hint line and EVERY error message rendered as a lone "…". A blank or ellipsis-only row must fail here.
        // Hồi quy: luật chiều cao của TruncateToFit từng từ chối mọi ứng viên trong row 18 px, khiến dòng hint
        // và MỌI thông báo lỗi chỉ còn dấu "…" đơn lẻ. Row trống hoặc chỉ có ellipsis phải fail ở đây.
        using Bitmap bitmap = Render(State(errorLine:
            "Không kết nối được với core — kiểm tra lại model OCR và khởi động lại pipeline"));

        var (px, py, pw, _) = FloatingMenuLayout.PanelRect(State());
        int rowsBeforeClose = FloatingMenuLayout.Rows(State()).Count - 1; // the close row is last
        int hintTop = py + FloatingMenuLayout.HeaderHeight + rowsBeforeClose * FloatingMenuLayout.RowHeight;
        int errTop = hintTop + FloatingMenuLayout.HintRowHeight;

        // Hint line: dimmed text — count pixels that are painted and not panel-dark.
        // Dòng hint: chữ mờ — đếm pixel đã vẽ và không tối như nền panel.
        var (hintCount, hintSpan, _) = Ink(bitmap, hintTop, hintTop + FloatingMenuLayout.HintRowHeight,
            px, px + pw, c => c.A > 0 && !IsDark(c));
        Assert.True(hintCount > 60 && hintSpan >= 60,
            $"hint line should hold real text, got {hintCount} inked pixels spanning {hintSpan} px (a lone '…' spans ~15)");

        // Error line: the error colour (240,90,90) must cover a wide stretch of the row.
        // Dòng lỗi: màu lỗi (240,90,90) phải phủ một khoảng rộng của row.
        var (errCount, errSpan, _) = Ink(bitmap, errTop, errTop + FloatingMenuLayout.ErrorRowHeight,
            px, px + pw, c => c.A > 0 && c.R > 180 && c.G < 140 && c.B < 140);
        Assert.True(errCount > 150 && errSpan >= 100,
            $"error line should hold the (truncated) message, got {errCount} red pixels spanning {errSpan} px (a lone '…' spans ~15)");
    }

    [Fact]
    public void Collapsed_ChipDrawn_WithAccentGlyph()
    {
        using Bitmap bitmap = Render(State(collapsed: true));
        var (px, py, pw, ph) = FloatingMenuLayout.PanelRect(State(collapsed: true));
        int cx = px + pw / 2, cy = py + ph / 2;

        int teal = 0;
        for (int y = cy - 14; y <= cy + 14; y++)
            for (int x = cx - 14; x <= cx + 14; x++)
                if (x >= 0 && y >= 0 && x < bitmap.Width && y < bitmap.Height && IsTeal(bitmap.GetPixel(x, y)))
                    teal++;

        Assert.True(teal > 20, $"expected the teal 'R' glyph around the chip centre, got {teal} teal pixels");
    }

    [Fact]
    public void StatsOpen_DrawsStatsBlock()
    {
        var closed = FloatingMenuLayout.WindowSize(State());
        var open = FloatingMenuLayout.WindowSize(State(statsOpen: true));
        Assert.Equal(closed.Item2 + FloatingMenuLayout.StatsBlockHeight, open.Item2);

        MenuState s = State(statsOpen: true);
        using Bitmap bitmap = Render(s);
        Assert.Equal(open.Item2, bitmap.Height); // the taller state rendered without crashing

        // The stats block must actually contain drawn text, not just add height.
        // Vùng thống kê phải thật sự chứa chữ đã vẽ, không chỉ làm bitmap cao hơn.
        var (px, py, pw, _) = FloatingMenuLayout.PanelRect(s);
        var rows = FloatingMenuLayout.Rows(s);
        int statsIndex = -1;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Id == FloatingMenuLayout.RowStats) { statsIndex = i; break; }
        Assert.True(statsIndex >= 0, "stats row must be present in an expanded menu");
        int statsTop = py + FloatingMenuLayout.HeaderHeight + statsIndex * FloatingMenuLayout.RowHeight;
        var (inkCount, _, _) = Ink(bitmap, statsTop, statsTop + FloatingMenuLayout.StatsBlockHeight,
            px, px + pw, c => c.A > 0 && !IsDark(c));
        Assert.True(inkCount > 150, $"stats block should hold five lines of text, got {inkCount} inked pixels");
    }
}
