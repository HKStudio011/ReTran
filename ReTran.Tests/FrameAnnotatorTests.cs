using System.Drawing;
using ReTran.Core.Capture;
using ReTran.Core.Serve;
using Color = System.Drawing.Color;
using Frame = ReTran.Core.Capture.Frame;
using Image = System.Drawing.Image;

namespace RetranCore.Tests;

public class FrameAnnotatorTests
{
    private static Frame SolidFrame(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return Frame.FromBgra(width, height, pixels);
    }

    private static Bitmap Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        using Image image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static bool IsGreen(Color c) => c.G > 150 && c.R < 120 && c.B < 120;

    private static bool IsDark(Color c) => c.R < DarkCap && c.G < DarkCap && c.B < DarkCap;

    private static bool IsWhite(Color c) => c.R > 230 && c.G > 230 && c.B > 230;

    /// <summary>Dark-pixel cap for the pill fill: alpha 180 blends to ≈75 over a white frame (a 60 cap would miss the pill and its JPEG noise). M cap pixel tối của nền pill: alpha 180 hoà thành ≈75 trên nền trắng (cap 60 sẽ bỏ sót pill và nhiễu JPEG).</summary>
    private const int DarkCap = 100;

    [Fact]
    public void Annotate_WithoutSnapshot_ReturnsJpegCappedAt1280()
    {
        using Frame frame = SolidFrame(2560, 1440, 10, 20, 30);
        byte[] jpeg = new FrameAnnotator().Annotate(frame, null).Jpeg;

        using Bitmap decoded = Decode(jpeg);
        Assert.Equal(1280, decoded.Width);
        Assert.Equal(720, decoded.Height);
    }

    [Fact]
    public void Annotate_ReturnsAnnotatedSize_NotSourceSize()
    {
        using Frame frame = SolidFrame(2560, 1440, 10, 20, 30);

        AnnotatedFrame annotated = new FrameAnnotator().Annotate(frame, null);

        // The record must report the annotated output size (bug fix: notify used to send the
        // source size 2560×1440 while the JPEG was only 1280×720).
        // Record phải trả kích thước annotated (fix bug: notify dùng gửi kích thước nguồn
        // 2560×1440 trong khi JPEG chỉ 1280×720).
        Assert.Equal(1280, annotated.Width);
        Assert.Equal(720, annotated.Height);
        using Bitmap decoded = Decode(annotated.Jpeg);
        Assert.Equal(1280, decoded.Width);
        Assert.Equal(720, decoded.Height);
    }

    [Fact]
    public void Annotate_WithSnapshot_DrawsGreenBox()
    {
        using Frame frame = SolidFrame(640, 360, 0, 0, 0);
        var snapshot = new AnnotSnapshot(640, 360,
            [new AnnotBox(0.1, 0.1, 0.3, 0.1, "Hello", "Xin chào")]);

        byte[] jpeg = new FrameAnnotator().Annotate(frame, snapshot).Jpeg;

        using Bitmap decoded = Decode(jpeg);
        int green = 0;
        for (int y = 30; y < 80; y++)
            for (int x = 58; x < 262; x++)
                if (IsGreen(decoded.GetPixel(x, y))) green++;
        Assert.True(green > 0, "expected green (#22C55E) pixels inside/around the box region");
    }

    [Fact]
    public void Annotate_DrawsPillBehindText()
    {
        using Frame frame = SolidFrame(640, 360, 255, 255, 255); // white so only the pill is dark
        var snapshot = new AnnotSnapshot(640, 360,
            [new AnnotBox(0.1, 0.1, 0.3, 0.1, "Hello", "Xin chào")]);

        AnnotatedFrame annotated = new FrameAnnotator().Annotate(frame, snapshot);
        using Bitmap decoded = Decode(annotated.Jpeg);

        // Box (64, 36, 192×36) → text drawn at ly = 74 with a 12.8 px font, so the pill sits
        // roughly at x 62..124, y 73..92. A pill is a solid dark rectangle; the previous
        // outline style drew thin ~1 px strokes, so a horizontal run of dark pixels ≥ 40
        // distinguishes the fill from outlines.
        // Box (64, 36, 192×36) → text vẽ tại ly = 74 với font 12,8 px, pill ≈ x 62..124,
        // y 73..92. Pill là hình chữ nhật đặc; style viền cũ chỉ vẽ nét mảnh ~1 px, nên một
        // chuỗi pixel tối liền ≥ 40 phân biệt được hình nền đặc với viền.
        int dark = 0, maxRun = 0;
        int pxMin = 0, pxMax = 0, pyMin = 0, pyMax = 0;
        bool hasPill = false;
        for (int y = 73; y < 92; y++)
        {
            int run = 0;
            for (int x = 60; x < 135; x++)
            {
                Color c = decoded.GetPixel(x, y);
                if (IsDark(c))
                {
                    dark++;
                    run++;
                    if (run > maxRun) maxRun = run;
                    if (!hasPill) { pxMin = pxMax = x; pyMin = pyMax = y; hasPill = true; }
                    else
                    {
                        pxMin = Math.Min(pxMin, x); pxMax = Math.Max(pxMax, x);
                        pyMin = Math.Min(pyMin, y); pyMax = Math.Max(pyMax, y);
                    }
                }
                else run = 0;
            }
        }

        Assert.True(dark > 200, $"pill: expected >200 dark pixels in x 60..135/y 73..92, got {dark}");
        Assert.True(maxRun >= 40, $"pill: expected a solid dark rectangle (run >= 40), got run {maxRun}");

        // White pixels strictly inside the pill bounding box can only be the text itself
        // (the pill fill is dark). Vùng trắng nằm trong pill chỉ có thể là chữ (nền pill là tối).
        int textWhite = 0;
        for (int y = pyMin + 1; y < pyMax; y++)
            for (int x = pxMin + 1; x < pxMax; x++)
                if (IsWhite(decoded.GetPixel(x, y))) textWhite++;
        Assert.True(textWhite > 20, $"text: expected >20 white pixels inside the pill, got {textWhite}");
    }

    [Fact]
    public void Annotate_RendersVietnameseAndSymbol_WithoutThrowing()
    {
        using Frame frame = SolidFrame(640, 360, 0, 0, 0);
        var snapshot = new AnnotSnapshot(640, 360,
            [new AnnotBox(0.1, 0.4, 0.5, 0.1, "Xin chào ∑ ẻ", "∑ ẻ chào")]);

        byte[] jpeg = new FrameAnnotator().Annotate(frame, snapshot).Jpeg;

        Assert.NotNull(jpeg);
        Assert.NotEmpty(jpeg);
        using Bitmap decoded = Decode(jpeg);
        Assert.Equal(640, decoded.Width);
    }

    [Fact]
    public void Annotate_LargeFrame_DoesNotCopyFullPixels()
    {
        using Frame frame = SolidFrame(1920, 1080, 10, 20, 30);
        var annotator = new FrameAnnotator();
        _ = annotator.Annotate(frame, null); // warm-up (JIT + GDI+). Khởi động (JIT + GDI+).

        long before = GC.GetAllocatedBytesForCurrentThread();
        AnnotatedFrame annotated = annotator.Annotate(frame, null);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // The frame is 1920×1080×4 ≈ 8.3 MB. Measured: pre-fix CopyPixels kept ~33 KB managed
        // per pass (its cost is native GDI+ memory + time churn), so keep per-pass managed
        // allocation bounded well below the frame size.
        // Khung là 1920×1080×4 ≈ 8,3 MB. Số đo: CopyPixels cũ giữ ~33 KB managed mỗi lượt
        // (chi phí của nó nằm ở bộ nhớ native GDI+ và thời gian), nên giữ phần cấp phát
        // managed mỗi lượt bị chặn dưới kích thước khung.
        Assert.True(delta < 4_000_000,
            $"allocated {delta} bytes per annotate pass of 1920×1080 (limit 4 MB)");
        Assert.Equal(1280, annotated.Width);
        Assert.Equal(720, annotated.Height);
    }

    [Fact]
    public void AnnotatorFrameCopy_SurvivesSourceDispose()
    {
        using Frame source = SolidFrame(64, 36, 0, 0, 255);

        // The annotation handler clones the shared frame before OcrLoop disposes it
        // (see PipelineHost.StartAsync) — this lock-step contract must survive disposal.
        // Handler clone khung dùng chung trước khi OcrLoop dispose nó — hợp đồng này phải sống qua dispose.
        var copy = Frame.FromBgra(source.Width, source.Height, (byte[])source.Pixels.Clone());
        source.Dispose();

        byte[] jpeg = new FrameAnnotator().Annotate(copy, null).Jpeg;
        using Bitmap decoded = Decode(jpeg);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(36, decoded.Height);
    }
}
