using System.Drawing;
using System.Drawing.Imaging;
using ReTran_App.Services;
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

    private static Bitmap Render(MenuState s, I18nService? l = null)
    {
        var (w, h) = FloatingMenuLayout.WindowSize(s);
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Match the production surface: grayscale antialiasing, not ClearType subpixel
        // fringes (which would trip the color detectors).
        // Khớp bề mặt production: khử răng cưa xám, không phải ClearType (vệt màu sẽ
        // đánh lừa các bộ dò màu).
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        FloatingMenuRenderer.DrawMenu(g, s, w, h, l);
        return bitmap;
    }

    [Fact]
    public void Resolve_Null_ReturnsPreI18nValuesVerbatim()
    {
        // The null seam must reproduce exactly what the renderer hardcoded before the i18n wiring —
        // including the English "Start"/"Stop" button words — so legacy 4-arg call sites render unchanged.
        // Seam null phải tái hiện đúng những gì renderer hardcode trước khi nối i18n — kể cả chữ
        // "Start"/"Stop" tiếng Anh của nút — để caller 4 tham số cũ vẽ không đổi.
        FloatingMenuRenderer.MenuStrings t = FloatingMenuRenderer.Resolve(null);

        Assert.Equal("Start", t.Start);
        Assert.Equal("Stop", t.Stop);
        Assert.Equal("Nguồn", t.Source);
        Assert.Equal("Lớp phủ", t.Overlay);
        Assert.Equal("Dịch sang", t.Dst);
        Assert.Equal("Ngôn ngữ nguồn", t.Src);
        Assert.Equal("Nhịp OCR", t.OcrFps);
        Assert.Equal("Nhịp chụp", t.CaptureFps);
        Assert.Equal("Gộp nhiều dòng", t.MergeLines);
        Assert.Equal("Thống kê", t.Stats);
        Assert.Equal("Tắt menu nổi", t.CloseMenu);
        Assert.Equal("Áp dụng ở lần Start kế tiếp", t.NextStartHint);
        Assert.Equal("FPS", t.StatFps);
        Assert.Equal("Box", t.StatBoxes);
        Assert.Equal("Dòng dịch", t.StatLines);
    }

    [Fact]
    public void Resolve_En_ReturnsExactTableValues()
    {
        var en = new I18nService();
        en.Set("en");
        FloatingMenuRenderer.MenuStrings t = FloatingMenuRenderer.Resolve(en);

        Assert.Equal("Start", t.Start);
        Assert.Equal("Stop", t.Stop);
        Assert.Equal("Source", t.Source);
        Assert.Equal("Overlay", t.Overlay);
        Assert.Equal("Translate to", t.Dst);
        Assert.Equal("Source language", t.Src);
        Assert.Equal("OCR rhythm", t.OcrFps);
        Assert.Equal("Capture rhythm", t.CaptureFps);
        Assert.Equal("Merge lines", t.MergeLines);
        Assert.Equal("Stats", t.Stats);
        Assert.Equal("Turn off floating menu", t.CloseMenu);
        Assert.Equal("Language / rhythm / source apply on the next start.", t.NextStartHint);
        Assert.Equal("FPS", t.StatFps);
        Assert.Equal("Boxes", t.StatBoxes);
        Assert.Equal("Translated lines", t.StatLines);
    }

    [Fact]
    public void DrawMenu_WithEnService_RendersWithoutThrowing()
    {
        // Smoke: the full expanded menu (stats block open + error line) through the i18n path must
        // paint a real frame — same dark-panel coverage guarantee as the default path.
        // Smoke: menu mở rộng đầy đủ (vùng thống kê mở + dòng lỗi) qua đường i18n phải vẽ được khung
        // thật — cùng bảo đảm phủ panel tối như đường mặc định.
        var en = new I18nService();
        en.Set("en");
        MenuState s = State(statsOpen: true, errorLine: "Could not start: test");
        using Bitmap bitmap = Render(s, en);

        var (px, py, pw, ph) = FloatingMenuLayout.PanelRect(s);
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

    // A > 0 matters: fully transparent pixels are (0,0,0), which would otherwise count as "dark" and let a
    // blank bitmap pass the coverage tests.
    // A > 0 quan trọng: pixel trong suốt hoàn toàn là (0,0,0), nếu không đếm sẽ để bitmap trống lọt qua test phủ.
    private static bool IsDark(Color c) => c.A > 0 && c.R < 100 && c.G < 100 && c.B < 100;

    private static bool IsAmber(Color c) => c.R > 200 && c.G > 120 && c.G < 210 && c.B < 80;

    private static bool IsIvory(Color c) => c.R > 200 && c.G > 200 && c.B > 180;

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
    public void Collapsed_ChipDrawn_WithBrandMark()
    {
        using Bitmap bitmap = Render(State(collapsed: true));
        var (px, py, pw, ph) = FloatingMenuLayout.PanelRect(State(collapsed: true));
        int cx = px + pw / 2, cy = py + ph / 2;

        int amber = 0, ivory = 0;
        for (int y = cy - 14; y < cy + 14; y++)
            for (int x = cx - 14; x < cx + 14; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (IsAmber(pixel)) amber++;
                if (IsIvory(pixel)) ivory++;
            }

        Assert.True(amber > 20, $"expected the folded-A brand mark around the chip centre, got {amber} amber pixels");
        Assert.True(ivory > 20, $"expected the folded-A brand mark around the chip centre, got {ivory} ivory pixels");
    }

    [Fact]
    public void Collapsed_MarginIsTransparent()
    {
        using Bitmap bitmap = Render(State(collapsed: true));

        (int X, int Y)[] origins =
        [
            (0, 0),
            (bitmap.Width - FloatingMenuLayout.ShadowMargin, 0),
            (0, bitmap.Height - FloatingMenuLayout.ShadowMargin),
            (bitmap.Width - FloatingMenuLayout.ShadowMargin, bitmap.Height - FloatingMenuLayout.ShadowMargin),
        ];

        foreach ((int originX, int originY) in origins)
            for (int offsetY = 0; offsetY < FloatingMenuLayout.ShadowMargin; offsetY++)
                for (int offsetX = 0; offsetX < FloatingMenuLayout.ShadowMargin; offsetX++)
                    Assert.True(bitmap.GetPixel(originX + offsetX, originY + offsetY).A == 0,
                        $"shadow-margin origin ({originX},{originY}), offset ({offsetX},{offsetY}) must be transparent");
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
