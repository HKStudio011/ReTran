using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ReTran_App.Services.Camera;
using Color = System.Drawing.Color;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

/// <summary>
/// Round-trip tests for the camera shared-memory protocol: what the app-side writer publishes is
/// exactly what the native filter (and the test reader) sees.
/// Test vòng lặp cho giao thức shared memory của camera: thứ writer phía app ghi đúng bằng thứ
/// filter native (và reader trong test) đọc được.
/// </summary>
public class CameraProtocolTests
{
    /// <summary>Fresh shared-memory names per test so runs never collide. Tên shared memory mới cho mỗi test để không đụng nhau.</summary>
    private static string UniqueName(string stem) => $@"Local\ReTran.Camera.Test.{stem}.{Guid.NewGuid():N}";

    [Fact]
    public void Submit_PublishesHeaderAndLetterboxedPixels()
    {
        string frameName = UniqueName("frame");
        string heartbeatName = UniqueName("heartbeat");
        using var service = new CameraOutputService(frameName, heartbeatName);

        service.Submit(seq: 1, w: 2, h: 2, jpeg: MakeJpeg(Color.Red, 2, 2));

        using CameraFrameReader reader = CameraFrameReader.Open(frameName);
        Assert.True(reader.TryRead(out CameraFrameHeader header, out byte[] pixels));

        Assert.Equal(CameraProtocol.Magic, header.Magic);
        Assert.Equal(CameraProtocol.Version, header.Version);
        Assert.Equal(1280u, header.Width);
        Assert.Equal(720u, header.Height);
        Assert.Equal(5120u, header.Stride);
        Assert.Equal(0u, header.Format);
        // A stable frame always carries an even, non-zero seqlock counter (odd means "writing").
        // Khung ổn định luôn mang bộ đếm seqlock chẵn, khác 0 (lẻ nghĩa là "đang ghi").
        Assert.True(header.Seq >= 1 && (header.Seq & 1u) == 0, $"expected an even stable seq but was {header.Seq}");
        Assert.Equal(0u, header.Flags & CameraProtocol.FlagHold);
        Assert.Equal(CameraProtocol.FrameBytes, pixels.Length);

        // A 2x2 (1:1) frame letterboxed into 1280x720 becomes a centred 720x720 square: the
        // middle pixel carries the image, the far-left column is black padding.
        // Khung 2x2 (tỉ lệ 1:1) fit vào 1280x720 thành hình vuông 720x720 ở giữa: pixel giữa mang
        // nội dung ảnh, cột sát trái là phần đệm đen.
        AssertPixelIsBlack(pixels, x: 40, y: 360);
        AssertPixelIsRed(pixels, x: 640, y: 360);
        // Near the image edge GDI+ bicubic upscaling softens the colour, but it must still be
        // image content rather than letterbox padding.
        // Sát mép ảnh, bicubic của GDI+ làm mềm màu, nhưng vẫn phải là nội dung ảnh chứ không phải
        // phần đệm đen.
        AssertPixelIsReddish(pixels, x: 640, y: 20);
    }

    [Fact]
    public void Submit_WideImage_PadsTopAndBottomWithBlack()
    {
        string frameName = UniqueName("frame");
        using var service = new CameraOutputService(frameName, UniqueName("heartbeat"));

        // 8x2 (4:1) scaled to fit 1280 wide becomes 1280x320, centred with 200px black bars.
        // Khung 8x2 (tỉ lệ 4:1) fit theo chiều rộng thành 1280x320, ở giữa với 200px đệm đen.
        service.Submit(seq: 1, w: 8, h: 2, jpeg: MakeJpeg(Color.Yellow, 8, 2));

        using CameraFrameReader reader = CameraFrameReader.Open(frameName);
        Assert.True(reader.TryRead(out _, out byte[] pixels));

        AssertPixelIsBlack(pixels, x: 640, y: 20);
        AssertPixelIsYellow(pixels, x: 640, y: 360);
    }

    [Fact]
    public void Dispose_RemovesHeartbeatMapping()
    {
        string frameName = UniqueName("frame");
        string heartbeatName = UniqueName("heartbeat");
        var service = new CameraOutputService(frameName, heartbeatName);
        service.Submit(seq: 1, w: 2, h: 2, jpeg: MakeJpeg(Color.Lime, 2, 2));

        Assert.True(HeartbeatExists(heartbeatName), "heartbeat must exist while the writer lives");

        service.Dispose();

        Assert.False(HeartbeatExists(heartbeatName), "heartbeat must disappear when the writer exits");
    }

    [Fact]
    public void Hold_KeepsSequenceAndSetsFlag_NextSubmitClearsIt()
    {
        string frameName = UniqueName("frame");
        string heartbeatName = UniqueName("heartbeat");
        using var service = new CameraOutputService(frameName, heartbeatName);

        service.Submit(seq: 1, w: 2, h: 2, jpeg: MakeJpeg(Color.Blue, 2, 2));
        using CameraFrameReader reader = CameraFrameReader.Open(frameName);
        Assert.True(reader.TryRead(out CameraFrameHeader live, out byte[] livePixels));

        service.Hold();

        Assert.True(reader.TryRead(out CameraFrameHeader held, out byte[] heldPixels));
        Assert.Equal(live.Seq, held.Seq);
        Assert.Equal(CameraProtocol.FlagHold, held.Flags & CameraProtocol.FlagHold);
        Assert.Equal(livePixels, heldPixels);

        service.Submit(seq: 2, w: 2, h: 2, jpeg: MakeJpeg(Color.Blue, 2, 2));

        Assert.True(reader.TryRead(out CameraFrameHeader resumed, out _));
        Assert.Equal(0u, resumed.Flags & CameraProtocol.FlagHold);
        Assert.True(resumed.Seq > held.Seq);
    }

    [Fact]
    public void TryOpen_WithoutWriter_ReturnsFalse()
    {
        Assert.False(CameraFrameReader.TryOpen(UniqueName("absent"), out CameraFrameReader? reader));
        Assert.Null(reader);
    }

    [Fact]
    public void Writer_RejectsWrongSizedFrame()
    {
        using var writer = new CameraFrameWriter(UniqueName("frame"), UniqueName("heartbeat"));

        Assert.Throws<ArgumentException>(() => writer.WriteFrame(new byte[16], hold: false));
    }

    private static byte[] MakeJpeg(Color color, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
        }
        using var stream = new MemoryStream();
        ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 100L);
        bitmap.Save(stream, jpegCodec, parameters);
        return stream.ToArray();
    }

    /// <summary>Reads one BGRA pixel at (x, y) from a top-down BGRA buffer. Đọc một pixel BGRA tại (x, y) trong buffer BGRA top-down.</summary>
    private static (byte B, byte G, byte R) PixelAt(byte[] pixels, int x, int y)
    {
        int offset = (y * CameraProtocol.Stride) + (x * 4);
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private static void AssertPixelIsBlack(byte[] pixels, int x, int y)
    {
        (byte b, byte g, byte r) = PixelAt(pixels, x, y);
        Assert.True(b < 20 && g < 20 && r < 20, $"expected black at ({x},{y}) but was ({r},{g},{b})");
    }

    private static void AssertPixelIsRed(byte[] pixels, int x, int y)
    {
        (byte b, byte g, byte r) = PixelAt(pixels, x, y);
        Assert.True(r > 200 && g < 60 && b < 60, $"expected red at ({x},{y}) but was ({r},{g},{b})");
    }

    private static void AssertPixelIsYellow(byte[] pixels, int x, int y)
    {
        (byte b, byte g, byte r) = PixelAt(pixels, x, y);
        Assert.True(r > 200 && g > 200 && b < 60, $"expected yellow at ({x},{y}) but was ({r},{g},{b})");
    }

    private static void AssertPixelIsReddish(byte[] pixels, int x, int y)
    {
        (byte b, byte g, byte r) = PixelAt(pixels, x, y);
        Assert.True(r > 100 && g < 60 && b < 60, $"expected red image content at ({x},{y}) but was ({r},{g},{b})");
    }

    private const uint FileMapRead = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMapping(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Probes the heartbeat mapping exactly like the native filter does: a successful open means the
    /// writer process is still alive.
    /// Dò mapping heartbeat y như filter native: mở được nghĩa là tiến trình writer còn sống.
    /// </summary>
    private static bool HeartbeatExists(string name)
    {
        IntPtr handle = OpenFileMapping(FileMapRead, false, name);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        CloseHandle(handle);
        return true;
    }
}
