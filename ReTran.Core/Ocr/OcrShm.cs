using System.IO.MemoryMappedFiles;
using ReTran.Core.Capture;

namespace ReTran.Core.Ocr;

/// <summary>
/// Wire contract of the raw OCR frame transport between Core (writer) and the Python sidecar
/// (reader): the captured BGRA frame travels through a named shared-memory section instead of
/// a base64 JPEG inside every <c>ocr.spot</c> call, so the JSON-RPC stdio channel only carries
/// <c>{seq, width, height}</c>.
/// Giao ước dây của kênh truyền khung OCR raw giữa Core (bên ghi) và sidecar Python (bên đọc):
/// khung BGRA đã chụp đi qua vùng shared memory có tên thay vì JPEG base64 trong mỗi lần gọi
/// <c>ocr.spot</c>, nên kênh JSON-RPC stdio chỉ còn mang <c>{seq, width, height}</c>.
/// </summary>
/// <remarks>
/// Header layout (fixed, little-endian, mirrored byte-for-byte by
/// <c>ReTran.OCR/src/retran_core/ocr/shm.py</c>):
/// <code>
/// off  size  field
///  0    4    magic   = "RTOC" (0x52 0x54 0x4F 0x43)
///  4    4    version = 1
///  8    8    seq     (seqlock: odd = write in progress, even = frame stable)
/// 16    4    width
/// 20    4    height
/// 24    4    stride  (= width * 4)
/// 28    4    format  = 1 (BGRA32)
/// 32   32    reserved (zero; room for future fields)
/// 64   ...   BGRA32 payload (height * stride bytes)
/// </code>
/// The capacity is FIXED at 4K BGRA so the section is created once and never recreated: a
/// recreated (resized) section under the same name would invalidate the reader's cached
/// mmap view.
/// Capacity cố định bằng 4K BGRA để section được tạo một lần và không bao giờ tạo lại: tạo
/// lại (đổi kích thước) section cùng tên sẽ làm mất hiệu lực view mmap reader đang giữ.
/// </remarks>
public static class OcrShm
{
    /// <summary>Shared-memory section carrying the latest captured frame. Vùng shared memory mang khung đã chụp mới nhất.</summary>
    public const string Name = @"Local\ReTran.Ocr.V1";

    /// <summary>Frame magic ("RTOC", bytes 52 54 4F 43 when written little-endian). Magic của khung ("RTOC", byte 52 54 4F 43 khi ghi little-endian).</summary>
    public const uint Magic = 0x434F5452;

    /// <summary>Protocol version carried in the header. Phiên bản giao thức ghi trong header.</summary>
    public const int Version = 1;

    /// <summary>Header size in bytes; the payload starts right after it. Kích thước header theo byte; payload nằm ngay sau.</summary>
    public const int HeaderSize = 64;

    /// <summary>Offset of the version field. Vị trí trường version.</summary>
    public const int OffsetVersion = 4;
    /// <summary>Offset of the seqlock counter (int64). Vị trí bộ đếm seqlock (int64).</summary>
    public const int OffsetSeq = 8;
    /// <summary>Offset of the width field. Vị trí trường width.</summary>
    public const int OffsetWidth = 16;
    /// <summary>Offset of the height field. Vị trí trường height.</summary>
    public const int OffsetHeight = 20;
    /// <summary>Offset of the stride field. Vị trí trường stride.</summary>
    public const int OffsetStride = 24;
    /// <summary>Offset of the format field. Vị trí trường format.</summary>
    public const int OffsetFormat = 28;

    /// <summary>Payload format id: 32-bit BGRA pixel order. Id định dạng payload: thứ tự pixel BGRA 32-bit.</summary>
    public const int FormatBgra32 = 1;

    /// <summary>Largest frame width the fixed capacity accepts. Chiều rộng khung lớn nhất mà capacity cố định chấp nhận.</summary>
    public const int MaxWidth = 3840;
    /// <summary>Largest frame height the fixed capacity accepts. Chiều cao khung lớn nhất mà capacity cố định chấp nhận.</summary>
    public const int MaxHeight = 2160;

    /// <summary>Payload capacity in bytes (4K BGRA). Dung lượng payload theo byte (4K BGRA).</summary>
    public const long PayloadCapacity = (long)MaxWidth * MaxHeight * 4;

    /// <summary>Total section size: header plus payload capacity. Tổng kích thước section: header cộng dung lượng payload.</summary>
    public const long TotalSize = HeaderSize + PayloadCapacity;
}

/// <summary>
/// Publishes captured frames into the OCR shared-memory section using a seqlock, so the sidecar
/// never reads a torn frame. Owns the section for the writer's lifetime; a reader that is still
/// open keeps the section (and its last frame) alive after disposal.
/// Ghi khung đã chụp vào vùng shared memory OCR theo seqlock, để sidecar không bao giờ đọc phải
/// khung bị xé. Sở hữu section trong vòng đời writer; reader còn mở sẽ giữ section (và khung
/// cuối) sống sau khi dispose.
/// </summary>
public sealed class OcrShmWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryMappedFile _section;
    private readonly MemoryMappedViewAccessor _view;
    private long _seq;
    private bool _disposed;

    /// <summary>
    /// Creates (or opens) the OCR section; tests pass a unique name to stay isolated.
    /// Tạo (hoặc mở) vùng OCR; test truyền tên riêng để không đụng nhau.
    /// </summary>
    /// <param name="name">Shared-memory section name. Tên vùng shared memory.</param>
    public OcrShmWriter(string name = OcrShm.Name)
    {
        _section = MemoryMappedFile.CreateOrOpen(name, OcrShm.TotalSize, MemoryMappedFileAccess.ReadWrite);
        _view = _section.CreateViewAccessor(0, OcrShm.TotalSize, MemoryMappedFileAccess.ReadWrite);

        // The seqlock counter belongs to the SECTION, not to this instance: the previous run's
        // reader may still hold its last frame, so a new run must continue from the counter
        // already in the header. Rounding down to even discards a half-written (odd) value from
        // a crashed writer.
        // Bộ đếm seqlock thuộc về VÙNG NHỚ, không phải từng instance: reader của lần chạy trước
        // có thể còn giữ khung cuối, nên lần chạy mới phải tiếp tục từ bộ đếm đang có trong
        // header. Làm tròn xuống chẵn để bỏ giá trị dở dang (lẻ) của writer bị chết.
        _seq = _view.ReadInt64(OcrShm.OffsetSeq) & ~1L;
    }

    /// <summary>
    /// Publishes one frame: seq is odd while the header and payload are being written, even
    /// once the frame is stable. Returns the new stable (even) seq Core sends to the sidecar.
    /// Ghi một khung: seq lẻ trong lúc ghi header và payload, chẵn khi khung đã ổn định. Trả về
    /// seq ổn định (chẵn) mới để Core gửi cho sidecar.
    /// </summary>
    /// <param name="frame">Captured BGRA frame; its pixels are copied into the section. Khung BGRA đã chụp; điểm ảnh được copy vào vùng nhớ.</param>
    /// <returns>The stable seq of the published frame. Seq ổn định của khung đã ghi.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null. Ném khi <paramref name="frame"/> là null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the frame exceeds the fixed 4K capacity. Ném khi khung vượt capacity 4K cố định.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the frame is disposed or not tightly packed (stride != width*4). Ném khi khung
    /// đã dispose hoặc không đóng khít (stride != width*4).
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the writer was disposed. Ném khi writer đã bị dispose.</exception>
    /// <remarks>
    /// Thread-safe like <c>PreviewChannelWriter</c>: publishing serializes against disposal so a
    /// teardown cannot pull the section out from under an in-flight publish.
    /// An toàn luồng như <c>PreviewChannelWriter</c>: publish tuần tự hoá với dispose để lúc dỡ
    /// phiên không rút section khỏi tay một lần ghi đang chạy.
    /// </remarks>
    public long Publish(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width > OcrShm.MaxWidth || frame.Height > OcrShm.MaxHeight
            || (long)frame.Height * frame.Stride > OcrShm.PayloadCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(frame),
                $"frame is {frame.Width}x{frame.Height}, exceeding the {OcrShm.MaxWidth}x{OcrShm.MaxHeight} BGRA shared-memory capacity.");
        }
        if (frame.Stride != frame.Width * 4 || frame.Pixels.Length != (long)frame.Height * frame.Stride)
        {
            throw new ArgumentException(
                "frame must be a tightly packed BGRA buffer (width*4 stride, not disposed).", nameof(frame));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Seqlock: an odd counter tells the reader a write is in flight.
            // Seqlock: bộ đếm lẻ báo cho bên đọc biết đang ghi.
            long writing = _seq | 1;
            _view.Write(OcrShm.OffsetSeq, writing);
            Thread.MemoryBarrier();

            _view.Write(0u, OcrShm.Magic);
            _view.Write(OcrShm.OffsetVersion, OcrShm.Version);
            _view.Write(OcrShm.OffsetWidth, frame.Width);
            _view.Write(OcrShm.OffsetHeight, frame.Height);
            _view.Write(OcrShm.OffsetStride, frame.Stride);
            _view.Write(OcrShm.OffsetFormat, OcrShm.FormatBgra32);
            _view.WriteArray(OcrShm.HeaderSize, frame.Pixels, 0, frame.Pixels.Length);
            Thread.MemoryBarrier();

            _seq = writing + 1;
            _view.Write(OcrShm.OffsetSeq, _seq);
            return _seq;
        }
    }

    /// <summary>
    /// Releases the section mapping; a reader that is still open keeps the section alive.
    /// Giải phóng mapping của section; reader còn mở vẫn giữ section sống.
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
            _view.Dispose();
            _section.Dispose();
        }
    }
}
