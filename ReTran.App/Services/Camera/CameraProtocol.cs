using System.IO.MemoryMappedFiles;

namespace ReTran_App.Services.Camera;

/// <summary>
/// Wire contract of the virtual camera between this app (writer) and the native
/// <c>ReTran.Camera.dll</c> filter (reader): a fixed 1280x720 BGRA32 frame inside a named shared
/// memory section plus a heartbeat mapping whose lifetime signals "the app is still alive".
/// Giao kèo dây của camera ảo giữa app này (bên ghi) và filter native <c>ReTran.Camera.dll</c>
/// (bên đọc): một khung BGRA32 cố định 1280x720 trong vùng shared memory có tên, kèm mapping
/// heartbeat mà vòng đời báo hiệu "app còn sống".
/// </summary>
/// <remarks>
/// Header layout (fixed, mirrored byte-for-byte by the C++ side):
/// <code>
/// off  size  field
///  0    4    magic   = 0x4D414352
///  4    4    version = 1
///  8    4    width   = 1280
/// 12    4    height  = 720
/// 16    4    stride  = 5120
/// 20    4    format  = 0 (BGRA32)
/// 24    4    seq     (seqlock: odd = write in progress, even = stable)
/// 28    4    flags   (bit0 = HOLD)
/// 32    8    lastWriteTickMs
/// 40    4    reserved0
/// 44    4    reserved1
/// 48    ...  pixels (BGRA32, top-down, height * stride bytes)
/// </code>
/// The heartbeat mapping is created by the writer and held only by the writer, so a failed
/// <c>OpenFileMapping</c> on the filter side means the app exited.
/// Mapping heartbeat do writer tạo và chỉ writer giữ, nên <c>OpenFileMapping</c> thất bại ở phía
/// filter nghĩa là app đã thoát.
/// </remarks>
public static class CameraProtocol
{
    /// <summary>Name of the shared-memory section carrying the latest frame. Tên vùng shared memory mang khung mới nhất.</summary>
    public const string FrameMapName = @"Local\ReTran.Camera.V1";

    /// <summary>Name of the writer-liveness mapping. Tên mapping báo hiệu bên ghi còn sống.</summary>
    public const string HeartbeatMapName = @"Local\ReTran.Camera.Heartbeat";

    /// <summary>Frame magic ("RCAM" little-endian). Magic của khung ("RCAM" little-endian).</summary>
    public const uint Magic = 0x4D414352;

    /// <summary>Protocol version carried in the header. Phiên bản giao thức trong header.</summary>
    public const uint Version = 1;

    /// <summary>Fixed frame width in pixels. Chiều rộng khung cố định theo pixel.</summary>
    public const int Width = 1280;

    /// <summary>Fixed frame height in pixels. Chiều cao khung cố định theo pixel.</summary>
    public const int Height = 720;

    /// <summary>Bytes per row of the BGRA32 frame. Số byte mỗi hàng của khung BGRA32.</summary>
    public const int Stride = Width * 4;

    /// <summary>Header size in bytes, before the pixel data. Kích thước header theo byte, trước phần pixel.</summary>
    public const int HeaderSize = 48;

    /// <summary>Format tag for the single supported layout (0 = BGRA32). Nhãn định dạng cho bố cục duy nhất được hỗ trợ (0 = BGRA32).</summary>
    public const uint FormatBgra32 = 0;

    /// <summary>Flag bit set while the pipeline is stopped and the frame must be kept. Bit cờ được bật khi pipeline dừng và khung phải được giữ.</summary>
    public const uint FlagHold = 1;

    /// <summary>Pixel payload size in bytes (1280 * 720 * 4). Kích thước phần pixel theo byte.</summary>
    public const long FrameBytes = (long)Height * Stride;

    /// <summary>Total section size: header plus pixels. Tổng kích thước vùng: header cộng pixel.</summary>
    public const long TotalSize = HeaderSize + FrameBytes;

    /// <summary>Heartbeat mapping size in bytes. Kích thước mapping heartbeat theo byte.</summary>
    public const int HeartbeatBytes = 4;

    /// <summary>Offset of the magic field. Vị trí trường magic.</summary>
    public const int OffsetMagic = 0;
    /// <summary>Offset of the version field. Vị trí trường version.</summary>
    public const int OffsetVersion = 4;
    /// <summary>Offset of the width field. Vị trí trường width.</summary>
    public const int OffsetWidth = 8;
    /// <summary>Offset of the height field. Vị trí trường height.</summary>
    public const int OffsetHeight = 12;
    /// <summary>Offset of the stride field. Vị trí trường stride.</summary>
    public const int OffsetStride = 16;
    /// <summary>Offset of the format field. Vị trí trường format.</summary>
    public const int OffsetFormat = 20;
    /// <summary>Offset of the seqlock counter. Vị trí bộ đếm seqlock.</summary>
    public const int OffsetSeq = 24;
    /// <summary>Offset of the flags field. Vị trí trường flags.</summary>
    public const int OffsetFlags = 28;
    /// <summary>Offset of the last-write tick. Vị trí tick ghi gần nhất.</summary>
    public const int OffsetLastWriteTickMs = 32;
    /// <summary>Offset of the pixel payload. Vị trí phần pixel.</summary>
    public const int OffsetPixels = HeaderSize;

    /// <summary>
    /// Allocates a zeroed (black) frame of exactly <see cref="FrameBytes"/> bytes.
    /// Cấp phát một khung đen (toàn số 0) đúng <see cref="FrameBytes"/> byte.
    /// </summary>
    /// <returns>A black BGRA32 frame. Một khung BGRA32 màu đen.</returns>
    public static byte[] CreateBlackFrame() => new byte[FrameBytes];
}

/// <summary>
/// Header of one shared-memory camera frame. Header của một khung camera trong shared memory.
/// </summary>
/// <param name="Magic">Frame magic; must equal <see cref="CameraProtocol.Magic"/>. Magic của khung; phải bằng <see cref="CameraProtocol.Magic"/>.</param>
/// <param name="Version">Protocol version. Phiên bản giao thức.</param>
/// <param name="Width">Frame width in pixels. Chiều rộng khung theo pixel.</param>
/// <param name="Height">Frame height in pixels. Chiều cao khung theo pixel.</param>
/// <param name="Stride">Row stride in bytes. Sải hàng theo byte.</param>
/// <param name="Format">Format tag (0 = BGRA32). Nhãn định dạng (0 = BGRA32).</param>
/// <param name="Seq">Sequence counter; even when the frame is stable. Bộ đếm; chẵn khi khung ổn định.</param>
/// <param name="Flags">Flag bits; bit0 = HOLD. Các bit cờ; bit0 = HOLD.</param>
/// <param name="LastWriteTickMs">Tick (ms) of the last write. Tick (ms) của lần ghi cuối.</param>
public readonly record struct CameraFrameHeader(
    uint Magic,
    uint Version,
    uint Width,
    uint Height,
    uint Stride,
    uint Format,
    uint Seq,
    uint Flags,
    ulong LastWriteTickMs);

/// <summary>
/// Writes frames into the camera shared memory using a seqlock so the native filter never shows a
/// torn frame. Owns the section and the heartbeat mapping; disposing it makes the heartbeat
/// disappear, which is how the filter detects that the app exited.
/// Ghi khung vào shared memory của camera theo seqlock để filter native không bao giờ thấy khung
/// bị xé. Sở hữu vùng nhớ và mapping heartbeat; dispose sẽ làm heartbeat biến mất — đó là cách
/// filter phát hiện app đã thoát.
/// </summary>
public sealed class CameraFrameWriter : IDisposable
{
    private readonly MemoryMappedFile _frame;
    private readonly MemoryMappedViewAccessor _view;
    private readonly MemoryMappedFile _heartbeat;
    private uint _seq;
    private bool _disposed;

    /// <summary>
    /// Creates (or opens) the well-known camera section and heartbeat mapping.
    /// Tạo (hoặc mở) vùng camera và mapping heartbeat theo tên chuẩn.
    /// </summary>
    /// <exception cref="IOException">Thrown when a foreign section already owns the name with a different size. Ném khi một vùng lạ đã chiếm tên với kích thước khác.</exception>
    public CameraFrameWriter()
        : this(CameraProtocol.FrameMapName, CameraProtocol.HeartbeatMapName)
    {
    }

    /// <summary>
    /// Creates (or opens) a camera section under explicit names; used by tests to stay isolated.
    /// Tạo (hoặc mở) vùng camera theo tên chỉ định; dùng cho test để không đụng nhau.
    /// </summary>
    /// <param name="frameMapName">Shared-memory name for the frame section. Tên shared memory cho vùng khung.</param>
    /// <param name="heartbeatMapName">Shared-memory name for the heartbeat mapping. Tên shared memory cho mapping heartbeat.</param>
    /// <exception cref="ArgumentException">Thrown when a name is null, empty or whitespace. Ném khi tên là null, rỗng hoặc chỉ có khoảng trắng.</exception>
    /// <exception cref="IOException">Thrown when a foreign section already owns the name with a different size. Ném khi một vùng lạ đã chiếm tên với kích thước khác.</exception>
    public CameraFrameWriter(string frameMapName, string heartbeatMapName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameMapName);
        ArgumentException.ThrowIfNullOrWhiteSpace(heartbeatMapName);

        _frame = MemoryMappedFile.CreateOrOpen(
            frameMapName, CameraProtocol.TotalSize, MemoryMappedFileAccess.ReadWrite);
        _view = _frame.CreateViewAccessor(
            0, CameraProtocol.TotalSize, MemoryMappedFileAccess.ReadWrite);

        // Held for the writer's whole lifetime: the name vanishing is the "app exited" signal.
        // Giữ suốt vòng đời writer: tên biến mất chính là tín hiệu "app đã thoát".
        _heartbeat = MemoryMappedFile.CreateOrOpen(
            heartbeatMapName, CameraProtocol.HeartbeatBytes, MemoryMappedFileAccess.ReadWrite);
        using MemoryMappedViewAccessor heartbeatView = _heartbeat.CreateViewAccessor(
            0, CameraProtocol.HeartbeatBytes, MemoryMappedFileAccess.ReadWrite);
        heartbeatView.Write(0, (byte)1);
    }

    /// <summary>
    /// Publishes one full BGRA32 frame. When <paramref name="hold"/> is set the sequence counter is
    /// left untouched and only the HOLD flag changes, so the filter keeps the very same frame.
    /// Xuất bản một khung BGRA32 đầy đủ. Khi <paramref name="hold"/> được bật, bộ đếm giữ nguyên và
    /// chỉ cờ HOLD đổi, nên filter giữ đúng khung đó.
    /// </summary>
    /// <param name="bgra">Frame data, exactly <see cref="CameraProtocol.FrameBytes"/> bytes. Dữ liệu khung, đúng <see cref="CameraProtocol.FrameBytes"/> byte.</param>
    /// <param name="hold">True to flag the frame as held. True để đánh dấu khung đang được giữ.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bgra"/> is null. Ném khi <paramref name="bgra"/> là null.</exception>
    /// <exception cref="ArgumentException">Thrown when the frame is not exactly one 1280x720 BGRA32 frame. Ném khi khung không đúng một khung 1280x720 BGRA32.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the writer was disposed. Ném khi writer đã bị dispose.</exception>
    public void WriteFrame(byte[] bgra, bool hold)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != CameraProtocol.FrameBytes)
        {
            throw new ArgumentException(
                $"Frame must be exactly {CameraProtocol.FrameBytes} bytes ({CameraProtocol.Width}x{CameraProtocol.Height} BGRA32).",
                nameof(bgra));
        }
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Seqlock: an odd counter tells the reader a write is in flight.
        // Seqlock: bộ đếm lẻ báo cho bên đọc biết đang ghi.
        uint writing = _seq | 1u;
        _view.Write(CameraProtocol.OffsetSeq, writing);
        Thread.MemoryBarrier();

        _view.Write(CameraProtocol.OffsetMagic, CameraProtocol.Magic);
        _view.Write(CameraProtocol.OffsetVersion, CameraProtocol.Version);
        _view.Write(CameraProtocol.OffsetWidth, (uint)CameraProtocol.Width);
        _view.Write(CameraProtocol.OffsetHeight, (uint)CameraProtocol.Height);
        _view.Write(CameraProtocol.OffsetStride, (uint)CameraProtocol.Stride);
        _view.Write(CameraProtocol.OffsetFormat, CameraProtocol.FormatBgra32);
        _view.Write(CameraProtocol.OffsetFlags, hold ? CameraProtocol.FlagHold : 0u);
        _view.WriteArray(CameraProtocol.OffsetPixels, bgra, 0, bgra.Length);
        _view.Write(CameraProtocol.OffsetLastWriteTickMs, (ulong)Environment.TickCount64);
        Thread.MemoryBarrier();

        // Hold leaves the previous even counter in place; a submit moves it forward.
        // Hold để nguyên bộ đếm chẵn trước đó; submit thì tăng nó lên.
        _seq = hold ? writing - 1u : writing + 1u;
        _view.Write(CameraProtocol.OffsetSeq, _seq);
    }

    /// <summary>
    /// Releases the section and heartbeat mappings. Kết thúc vùng nhớ và các mapping heartbeat.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _view.Dispose();
        _frame.Dispose();
        _heartbeat.Dispose();
    }
}

/// <summary>
/// Reads camera frames out of the shared memory the same way the native filter does: seqlock
/// check, bounded retries, no torn frames. Used by tests and by diagnostics tooling.
/// Đọc khung camera từ shared memory giống hệt filter native: kiểm seqlock, thử lại có giới hạn,
/// không nhận khung bị xé. Dùng cho test và công cụ chẩn đoán.
/// </summary>
public sealed class CameraFrameReader : IDisposable
{
    private const int MaxAttempts = 4;

    private readonly MemoryMappedFile _frame;
    private readonly MemoryMappedViewAccessor _view;

    private CameraFrameReader(MemoryMappedFile frame, MemoryMappedViewAccessor view)
    {
        _frame = frame;
        _view = view;
    }

    /// <summary>
    /// Opens the camera section for reading. Mở vùng camera để đọc.
    /// </summary>
    /// <param name="frameMapName">Shared-memory name of the frame section. Tên shared memory của vùng khung.</param>
    /// <returns>An open reader; the caller owns it. Một reader đang mở; bên gọi chịu trách nhiệm dispose.</returns>
    /// <exception cref="IOException">Thrown when no writer has created the section yet. Ném khi chưa có writer tạo vùng nhớ.</exception>
    public static CameraFrameReader Open(string frameMapName)
    {
        if (!TryOpen(frameMapName, out CameraFrameReader? reader))
        {
            throw new IOException($"Shared memory '{frameMapName}' is not available.");
        }
        return reader!;
    }

    /// <summary>
    /// Tries to open the camera section for reading. Thử mở vùng camera để đọc.
    /// </summary>
    /// <param name="frameMapName">Shared-memory name of the frame section. Tên shared memory của vùng khung.</param>
    /// <param name="reader">The opened reader, or <see langword="null"/> when the section is missing. Reader đã mở, hoặc <see langword="null"/> khi thiếu vùng nhớ.</param>
    /// <returns>True when the section was opened. True khi mở được vùng nhớ.</returns>
    public static bool TryOpen(string frameMapName, out CameraFrameReader? reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameMapName);
        reader = null;
        try
        {
            MemoryMappedFile frame = MemoryMappedFile.OpenExisting(frameMapName);
            MemoryMappedViewAccessor view = frame.CreateViewAccessor(
                0, CameraProtocol.TotalSize, MemoryMappedFileAccess.Read);
            reader = new CameraFrameReader(frame, view);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the latest complete frame. Trả về khung hoàn chỉnh mới nhất.
    /// </summary>
    /// <param name="header">The frame header. Header của khung.</param>
    /// <param name="pixels">A fresh BGRA32 buffer of <see cref="CameraProtocol.FrameBytes"/> bytes. Buffer BGRA32 mới đúng <see cref="CameraProtocol.FrameBytes"/> byte.</param>
    /// <returns>True when a stable frame (magic matching) was read. True khi đọc được khung ổn định (magic khớp).</returns>
    public bool TryRead(out CameraFrameHeader header, out byte[] pixels)
    {
        pixels = new byte[CameraProtocol.FrameBytes];
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            uint before = _view.ReadUInt32(CameraProtocol.OffsetSeq);
            if ((before & 1u) != 0)
            {
                continue;
            }
            Thread.MemoryBarrier();

            uint magic = _view.ReadUInt32(CameraProtocol.OffsetMagic);
            uint version = _view.ReadUInt32(CameraProtocol.OffsetVersion);
            uint width = _view.ReadUInt32(CameraProtocol.OffsetWidth);
            uint height = _view.ReadUInt32(CameraProtocol.OffsetHeight);
            uint stride = _view.ReadUInt32(CameraProtocol.OffsetStride);
            uint format = _view.ReadUInt32(CameraProtocol.OffsetFormat);
            uint flags = _view.ReadUInt32(CameraProtocol.OffsetFlags);
            ulong lastWrite = _view.ReadUInt64(CameraProtocol.OffsetLastWriteTickMs);
            _view.ReadArray(CameraProtocol.OffsetPixels, pixels, 0, pixels.Length);
            Thread.MemoryBarrier();

            if (magic == CameraProtocol.Magic && before == _view.ReadUInt32(CameraProtocol.OffsetSeq))
            {
                header = new CameraFrameHeader(magic, version, width, height, stride, format, before, flags, lastWrite);
                return true;
            }
        }

        header = default;
        return false;
    }

    /// <summary>
    /// Releases the reader's mappings. Giải phóng các mapping của reader.
    /// </summary>
    public void Dispose()
    {
        _view.Dispose();
        _frame.Dispose();
    }
}

