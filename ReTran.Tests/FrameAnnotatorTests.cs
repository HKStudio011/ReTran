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

    /// <summary>Vertical extent of the dark pill inside a y-band (x 30..226). Khoảng dọc của pill tối trong dải y (x 30..226).</summary>
    private static int PillHeight(Bitmap bitmap, int top, int bottom)
    {
        int min = int.MaxValue, max = -1;
        for (int y = Math.Max(0, top); y <= Math.Min(bitmap.Height - 1, bottom); y++)
        {
            for (int x = 30; x <= Math.Min(226, bitmap.Width - 1); x++)
            {
                if (!IsDark(bitmap.GetPixel(x, y))) continue;
                min = Math.Min(min, y);
                max = Math.Max(max, y);
            }
        }
        return max < 0 ? 0 : max - min + 1;
    }

    // JPEG 4:2:0 chroma subsampling washes the 1.6 px teal line toward grey, so match the
    // green-blue bias rather than the exact #4FD8C4 value.
    // JPEG 4:2:0 làm nét teal 1,6 px nhạt dần về xám, nên so độ lệch xanh lá/xanh dương thay vì
    // đúng giá trị #4FD8C4.
    private static bool IsTeal(Color c) => c.G > 140 && c.B > 110 && c.G > c.R + 12 && c.B > c.R + 4;

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
    public void Annotate_WithSnapshot_DrawsTealCornerTicks()
    {
        using Frame frame = SolidFrame(1280, 720, 0, 0, 0);
        var snapshot = new AnnotSnapshot(1280, 720,
            [new AnnotBox(0.1, 0.1, 0.3, 0.1, "Hello", "Xin chào")]);

        byte[] jpeg = new FrameAnnotator().Annotate(frame, snapshot).Jpeg;

        using Bitmap decoded = Decode(jpeg);
        // Source box is (128,72)-(512,144); ticks sit on its four corners (#4FD8C4, 1.6 px pen).
        // Box gốc là (128,72)-(512,144); tick nằm ở bốn góc (màu #4FD8C4, pen 1,6 px).
        (int X, int Y)[] corners = [(128, 72), (512, 72), (512, 144), (128, 144)];
        int teal = 0;
        foreach ((int cx, int cy) in corners)
        {
            for (int y = Math.Max(0, cy - 10); y <= Math.Min(719, cy + 10); y++)
                for (int x = Math.Max(0, cx - 10); x <= Math.Min(1279, cx + 10); x++)
                    if (IsTeal(decoded.GetPixel(x, y))) teal++;
        }

        Assert.True(teal > 10, $"expected teal (#4FD8C4) tick pixels near the box corners, got {teal}");
    }

    [Fact]
    public void Annotate_DrawsPillBehindTextInsideBox()
    {
        using Frame frame = SolidFrame(640, 360, 255, 255, 255); // white so only the pill is dark
        var snapshot = new AnnotSnapshot(640, 360,
            [new AnnotBox(0.1, 0.1, 0.3, 0.1, "Hello", "Xin chào")]);

        AnnotatedFrame annotated = new FrameAnnotator().Annotate(frame, snapshot);
        using Bitmap decoded = Decode(annotated.Jpeg);

        // The pill now sits INSIDE the source box (64,36)-(256,72), inset 4 px: roughly
        // x 68..160, y 40..68 for the 18 px text. A solid dark run ≥ 30 distinguishes the
        // pill fill from the removed rectangular outline.
        // Pill giờ nằm TRONG box gốc (64,36)-(256,72), inset 4 px: khoảng x 68..160, y 40..68
        // với chữ 18 px. Chuỗi tối liền ≥ 30 phân biệt nền pill với viền chữ nhật đã bỏ.
        int dark = 0, maxRun = 0;
        int pxMin = int.MaxValue, pxMax = 0, pyMin = int.MaxValue, pyMax = 0;
        for (int y = 36; y <= 72; y++)
        {
            int run = 0;
            for (int x = 60; x <= 260; x++)
            {
                if (IsDark(decoded.GetPixel(x, y)))
                {
                    dark++;
                    run++;
                    if (run > maxRun) maxRun = run;
                    pxMin = Math.Min(pxMin, x); pxMax = Math.Max(pxMax, x);
                    pyMin = Math.Min(pyMin, y); pyMax = Math.Max(pyMax, y);
                }
                else run = 0;
            }
        }

        Assert.True(dark > 300, $"pill: expected >300 dark pixels in the box interior, got {dark}");
        Assert.True(maxRun >= 30, $"pill: expected a solid dark rectangle (run >= 30), got run {maxRun}");
        Assert.InRange(pyMin, 36, 50);
        Assert.InRange(pyMax, 55, 72);

        // White pixels strictly inside the pill bounding box can only be the text itself
        // (the pill fill is dark). Vùng trắng nằm trong pill chỉ có thể là chữ (nền pill là tối).
        int textWhite = 0;
        for (int y = pyMin + 1; y < pyMax; y++)
            for (int x = pxMin + 1; x < pxMax; x++)
                if (IsWhite(decoded.GetPixel(x, y))) textWhite++;
        Assert.True(textWhite > 10, $"text: expected >10 white pixels inside the pill, got {textWhite}");
    }

    [Fact]
    public void Annotate_PillHeightGrowsWithBoxHeight()
    {
        using Frame frame = SolidFrame(640, 360, 255, 255, 255);
        var snapshot = new AnnotSnapshot(640, 360,
        [
            new AnnotBox(0.05, 0.10, 0.30, 0.083, "Chào", "Chào"),
            new AnnotBox(0.05, 0.55, 0.30, 0.250, "Chào", "Chào"),
        ]);

        using Bitmap decoded = Decode(new FrameAnnotator().Annotate(frame, snapshot).Jpeg);

        // Short box (30 px) vs tall box (90 px): the pill height must grow with the box instead
        // of staying at one fixed size for every box.
        // Box thấp (30 px) so với box cao (90 px): chiều cao pill phải tăng theo box thay vì
        // giữ một cỡ cố định cho mọi box.
        int shortPill = PillHeight(decoded, 30, 68);
        int tallPill = PillHeight(decoded, 196, 290);

        Assert.True(shortPill > 8, $"short box: expected a visible pill, got height {shortPill}");
        Assert.True(tallPill > shortPill + 20,
            $"tall box pill ({tallPill} px) should be at least 20 px taller than the short one ({shortPill} px)");
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

    /// <summary>
    /// Guards managed-allocation churn per annotate pass only. The removed `CopyPixels` cost
    /// lived in native GDI+ memory plus CPU time and is not observable through
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>; the copy-elimination property is instead
    /// evidenced by the core CPU probe recorded in Docs/performance.md §M5-fix.
    /// Chỉ chặn churn cấp phát managed mỗi lượt ghi chú. Chi phí của `CopyPixels` cũ nằm ở bộ
    /// nhớ native GDI+ kèm thời gian CPU, không đo được qua
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>; thuộc tính "không còn copy" được chứng minh
    /// bằng probe CPU core ghi trong Docs/performance.md §M5-fix.
    /// </summary>
    [Fact]
    public void Annotate_LargeFrame_ManagedAllocationStaysBounded()
    {
        using Frame frame = SolidFrame(1920, 1080, 10, 20, 30);
        var annotator = new FrameAnnotator();
        _ = annotator.Annotate(frame, null); // warm-up (JIT + GDI+). Khởi động (JIT + GDI+).

        long before = GC.GetAllocatedBytesForCurrentThread();
        AnnotatedFrame annotated = annotator.Annotate(frame, null);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // The frame is 1920×1080×4 ≈ 8.3 MB; measured ~33 KB managed per pass pre-fix, so a
        // 4 MB ceiling catches any future full-frame managed copy without naming the pin.
        // Khung là 1920×1080×4 ≈ 8,3 MB; đo được ~33 KB managed mỗi lượt trước fix, nên trần
        // 4 MB bắt được mọi copy managed toàn khung về sau mà không phụ thuộc cài đặt pin.
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
