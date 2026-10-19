using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;

namespace ReTran_App.Services.Camera;

/// <summary>
/// Default <see cref="ICameraOutput"/>: decodes each annotated JPEG coming from Core, letterboxes it
/// into the fixed 1280x720 BGRA32 camera frame (black padding, never cropped) and publishes it to
/// shared memory. The section is created lazily on the first frame, so a camera that was never fed
/// leaves no trace for the filter to read.
/// <see cref="ICameraOutput"/> mặc định: giải mã từng JPEG đã ghi chú từ Core, fit vào khung camera
/// BGRA32 1280x720 cố định (đệm đen, không cắt) rồi xuất bản vào shared memory. Vùng nhớ chỉ được
/// tạo ở khung đầu tiên, nên camera chưa từng được cấp khung thì không để lại dấu vết cho filter.
/// </summary>
public sealed class CameraOutputService : ICameraOutput
{
    private readonly string _frameMapName;
    private readonly string _heartbeatMapName;
    private readonly object _gate = new();

    private CameraFrameWriter? _writer;
    private byte[]? _lastFrame;
    private bool _disposed;

    /// <summary>
    /// Creates the service bound to the well-known camera section names.
    /// Tạo service gắn với tên vùng camera chuẩn.
    /// </summary>
    public CameraOutputService()
        : this(CameraProtocol.FrameMapName, CameraProtocol.HeartbeatMapName)
    {
    }

    /// <summary>
    /// Creates the service bound to explicit section names; used by tests to stay isolated.
    /// Tạo service gắn với tên vùng chỉ định; dùng cho test để không đụng nhau.
    /// </summary>
    /// <param name="frameMapName">Shared-memory name for the frame section. Tên shared memory cho vùng khung.</param>
    /// <param name="heartbeatMapName">Shared-memory name for the heartbeat mapping. Tên shared memory cho mapping heartbeat.</param>
    /// <exception cref="ArgumentException">Thrown when a name is null, empty or whitespace. Ném khi tên là null, rỗng hoặc chỉ có khoảng trắng.</exception>
    public CameraOutputService(string frameMapName, string heartbeatMapName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameMapName);
        ArgumentException.ThrowIfNullOrWhiteSpace(heartbeatMapName);
        _frameMapName = frameMapName;
        _heartbeatMapName = heartbeatMapName;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown when <paramref name="jpeg"/> is not a decodable image or the payload is empty. Ném khi <paramref name="jpeg"/> không giải mã được thành ảnh hoặc payload rỗng.</exception>
    public void Submit(long seq, int w, int h, byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        // Decode outside the lock: Hold() from the pipeline thread must never wait for a JPEG.
        // Giải mã ngoài lock: Hold() từ luồng pipeline không bao giờ phải chờ JPEG.
        byte[] frame = DecodeLetterboxed(jpeg);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer ??= new CameraFrameWriter(_frameMapName, _heartbeatMapName);
            _writer.WriteFrame(frame, hold: false);
            _lastFrame = frame;
        }
    }

    /// <inheritdoc/>
    public void Hold()
    {
        lock (_gate)
        {
            if (_disposed || _writer is null || _lastFrame is null)
            {
                return;
            }
            _writer.WriteFrame(_lastFrame, hold: true);
        }
    }

    /// <summary>
    /// Releases the shared-memory section and the heartbeat mapping, which is what makes the native
    /// filter fall back to a black frame. Giải phóng vùng shared memory và mapping heartbeat — đó là
    /// thứ khiến filter native quay về khung đen.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
            _lastFrame = null;
        }
    }

    /// <summary>
    /// Decodes a JPEG and draws it fit into a 1280x720 BGRA32 buffer, padding with black instead of
    /// cropping when the aspect ratio differs.
    /// Giải mã một JPEG và vẽ vừa vào buffer BGRA32 1280x720, đệm đen thay vì cắt khi tỉ lệ khác.
    /// </summary>
    private static byte[] DecodeLetterboxed(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        using var canvas = new Bitmap(CameraProtocol.Width, CameraProtocol.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float scale = Math.Min(
                (float)CameraProtocol.Width / source.Width,
                (float)CameraProtocol.Height / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            graphics.DrawImage(
                source,
                new Rectangle(
                    (CameraProtocol.Width - width) / 2,
                    (CameraProtocol.Height - height) / 2,
                    width,
                    height));
        }

        byte[] buffer = new byte[CameraProtocol.FrameBytes];
        BitmapData data = canvas.LockBits(
            new Rectangle(0, 0, CameraProtocol.Width, CameraProtocol.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < CameraProtocol.Height; y++)
            {
                Marshal.Copy(
                    data.Scan0 + (y * data.Stride),
                    buffer,
                    y * CameraProtocol.Stride,
                    CameraProtocol.Stride);
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }
        return buffer;
    }
}
