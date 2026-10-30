using System.Drawing;
using System.Drawing.Imaging;
using ReTran_App.Services.OverlayWindow;
using Color = System.Drawing.Color;

namespace ReTran.Tests;

public class Win32OverlayHostRenderTests
{
    private static Bitmap Render(params DrawBox[] boxes)
    {
        var bitmap = new Bitmap(1280, 720, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Match the production surface: grayscale antialiasing, not ClearType subpixel
        // fringes (which would trip the teal-tick detector).
        // Khớp bề mặt production: khử răng cưa xám, không phải ClearType (vệt màu sẽ
        // đánh lừa bộ dò tick teal).
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.White);
        Win32OverlayHost.DrawBoxes(g, boxes, 1.0, 1280, 720);
        return bitmap;
    }

    private static bool IsTeal(Color c) => c.G > 140 && c.B > 110 && c.G > c.R + 12 && c.B > c.R + 4;

    private static bool IsDark(Color c) => c.R < 100 && c.G < 100 && c.B < 100;

    /// <summary>Vertical extent of dark pill pixels inside a y-band (x 100..340). Khoảng dọc của pixel pill tối trong dải y (x 100..340).</summary>
    private static int PillHeight(Bitmap bitmap, int top, int bottom)
    {
        int min = int.MaxValue, max = -1;
        for (int y = Math.Max(0, top); y <= Math.Min(bitmap.Height - 1, bottom); y++)
        {
            for (int x = 100; x <= Math.Min(340, bitmap.Width - 1); x++)
            {
                if (!IsDark(bitmap.GetPixel(x, y))) continue;
                min = Math.Min(min, y);
                max = Math.Max(max, y);
            }
        }
        return max < 0 ? 0 : max - min + 1;
    }

    [Fact]
    public void DrawBoxes_DrawsTealTicksAndPillInsideBox()
    {
        using Bitmap bitmap = Render(new DrawBox(100, 100, 240, 48, "Xin chào"));

        int teal = 0;
        (int X, int Y)[] corners = [(100, 100), (340, 100), (340, 148), (100, 148)];
        foreach ((int cx, int cy) in corners)
        {
            for (int y = cy - 9; y <= cy + 9; y++)
                for (int x = cx - 9; x <= cx + 9; x++)
                    if (x >= 0 && y >= 0 && x < 1280 && y < 720 && IsTeal(bitmap.GetPixel(x, y))) teal++;
        }
        Assert.True(teal > 20, $"expected teal (#4FD8C4) ticks near the box corners, got {teal}");

        int dark = 0, maxRun = 0;
        for (int y = 102; y <= 146; y++)
        {
            int run = 0;
            for (int x = 98; x <= 342; x++)
            {
                if (IsDark(bitmap.GetPixel(x, y)))
                {
                    dark++;
                    run++;
                    maxRun = Math.Max(maxRun, run);
                }
                else run = 0;
            }
        }
        Assert.True(dark > 400, $"expected a dark pill inside the box, got {dark} dark pixels");
        Assert.True(maxRun >= 30, $"expected a solid dark pill (run >= 30), got run {maxRun}");
    }

    [Fact]
    public void DrawBoxes_FontScalesWithBoxHeight()
    {
        using Bitmap bitmap = Render(
            new DrawBox(100, 40, 240, 30, "Chào"),
            new DrawBox(100, 300, 240, 96, "Chào"));

        int shortPill = PillHeight(bitmap, 36, 74);
        int tallPill = PillHeight(bitmap, 296, 400);

        Assert.True(shortPill > 8, $"short box: expected a visible pill, got height {shortPill}");
        Assert.True(tallPill > shortPill + 20,
            $"tall box pill ({tallPill} px) should be at least 20 px taller than the short one ({shortPill} px)");
    }
}
