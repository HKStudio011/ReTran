using System.Drawing;
using System.Drawing.Imaging;
using ReTran_App.Services.FloatingMenu;
using Color = System.Drawing.Color;

public class FloatingMenuRendererTests
{
    private static MenuState State(bool collapsed = false, bool running = true, bool statsOpen = false) => new(
        Collapsed: collapsed, Running: running, SourceLabel: "Màn hình 1",
        OverlayOn: true, Dst: "vi", Src: "auto", OcrFps: 3, CaptureFps: 10,
        MergeLines: true, StatsOpen: statsOpen, SourceListOpen: false,
        Sources: Array.Empty<SourceItem>(), ErrorLine: null);

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

    private static bool IsDark(Color c) => c.R < 100 && c.G < 100 && c.B < 100;

    private static bool IsTeal(Color c) => c.G > 140 && c.B > 110 && c.G > c.R + 12 && c.B > c.R + 4;

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

        using Bitmap bitmap = Render(State(statsOpen: true));
        Assert.Equal(open.Item2, bitmap.Height); // the taller state rendered without crashing
    }
}
