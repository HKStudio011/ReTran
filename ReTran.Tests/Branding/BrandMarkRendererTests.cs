using System.Drawing;
using System.Drawing.Imaging;
using ReTran_App.Services.Branding;
using Color = System.Drawing.Color;

public class BrandMarkRendererTests
{
    [Fact]
    public void Draw_PaintsBrandColors_WithoutCoveringTransparentCorners()
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);

        BrandMarkRenderer.Draw(graphics, new RectangleF(0, 0, 64, 64));

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

        Assert.True(amberPixels > 20, $"Expected more than 20 amber pixels, got {amberPixels}.");
        Assert.True(ivoryPixels > 20, $"Expected more than 20 ivory pixels, got {ivoryPixels}.");
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, 0).A);
        Assert.Equal(0, bitmap.GetPixel(0, bitmap.Height - 1).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A);
    }

    [Fact]
    public void Draw_PreservesGraphicsState()
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.TranslateTransform(11f, 7f);
        graphics.ScaleTransform(1.5f, 0.75f);
        graphics.SetClip(new RectangleF(2, 3, 40, 35));
        float[] transformBefore = graphics.Transform.Elements;
        RectangleF clipBefore = graphics.ClipBounds;

        BrandMarkRenderer.Draw(graphics, new RectangleF(0, 0, 32, 32));

        Assert.Equal(transformBefore, graphics.Transform.Elements);
        Assert.Equal(clipBefore, graphics.ClipBounds);
    }
}
