using System.Drawing;
using ReTran_App.Services.Tray;
using Color = System.Drawing.Color;

public class TrayIconSourceTests
{
    [Fact]
    public void RenderBitmap_ContainsSimplifiedBrandMark()
    {
        using var bitmap = TrayIconSource.RenderBitmap();

        Assert.Equal(32, bitmap.Width);
        Assert.Equal(32, bitmap.Height);

        int amberPixels = 0;
        int ivoryPixels = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R > 200 && pixel.G > 120 && pixel.G < 210 && pixel.B < 80)
                    amberPixels++;
                if (pixel.R > 200 && pixel.G > 200 && pixel.B > 180)
                    ivoryPixels++;
            }
        }

        Assert.True(amberPixels > 8, $"Expected more than 8 amber pixels, got {amberPixels}.");
        Assert.True(ivoryPixels > 8, $"Expected more than 8 ivory pixels, got {ivoryPixels}.");
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, 0).A);
        Assert.Equal(0, bitmap.GetPixel(0, bitmap.Height - 1).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A);
    }

    [Fact]
    public void RenderBitmap_UsesOpaqueInkBackground()
    {
        using var bitmap = TrayIconSource.RenderBitmap();

        Assert.Equal(Color.FromArgb(255, 7, 32, 30), bitmap.GetPixel(2, 16));
    }

    [Fact]
    public void Handle_NonZero_AndStable()
    {
        using var icon = new TrayIconSource();
        Assert.True(icon.Handle != 0);
        Assert.Equal(icon.Handle, icon.Handle);
    }

    [Fact]
    public void Dispose_Idempotent_AndMarksDisposed()
    {
        var icon = new TrayIconSource();
        icon.Dispose();
        Assert.True(icon.IsDisposed);
        icon.Dispose(); // không throw
    }
}
