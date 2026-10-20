using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace ReTran.Core.Serve;

/// <summary>
/// Wire contract of the annotated-frame preview between Core (writer) and the App (reader): the
/// annotated JPEG of the latest frame lives in a named shared-memory section, so it no longer
/// travels base64-encoded — 200–400 KB per frame — through the <c>frame.annotated</c> stdio
/// notification.
/// Giao kèo dây của preview khung đã ghi chú giữa Core (bên ghi) và App (bên đọc): JPEG đã ghi chú
/// của khung mới nhất nằm trong vùng shared memory có tên, nên nó không còn đi base64 — 200–400 KB
/// mỗi khung — qua thông báo stdio <c>frame.annotated</c>.
/// </summary>
/// <remarks>
/// Header layout (fixed, mirrored byte-for-byte by
/// <c>ReTran.App/Services/Preview/PreviewProtocol.cs</c>):
/// <code>
/// off  size  field
///  0    4    magic   = 0x45525052 ('RPRE')
///  4    4    version = 1
///  8    4    width   (annotated frame)
/// 12    4    height
/// 16    4    jpegLength
/// 20    4    seq     (seqlock: odd = write in progress, even = stable)
/// 24    8    lastWriteTickMs
/// 32    ...  jpeg bytes
/// </code>
/// The App does not reference <c>ReTran.Core</c> (the same reason the native camera filter mirrors
/// <c>CameraProtocol</c>), so both sides duplicate this layout and
/// <c>ReTran.Tests/PreviewProtocolTests.cs</c> asserts the two constant sets stay equal.
/// App không tham chiếu <c>ReTran.Core</c> (cùng lý do filter camera native nhân bản
/// <c>CameraProtocol</c>), nên hai phía copy bố cục này và
/// <c>ReTran.Tests/PreviewProtocolTests.cs</c> assert hai bộ hằng số luôn khớp nhau.
/// </remarks>
public static class PreviewProtocol
{
    /// <summary>Name of the shared-memory section carrying the latest annotated JPEG. Tên vùng shared memory mang JPEG đã ghi chú mới nhất.</summary>
    public const string MapName = @"Local\ReTran.Preview.V1";

    /// <summary>Frame magic ("RPRE" little-endian). Magic của khung ("RPRE" little-endian).</summary>
    public const uint Magic = 0x45525052;

    /// <summary>Protocol version carried in the header. Phiên bản giao thức trong header.</summary>
    public const uint Version = 1;

    /// <summary>Header size in bytes, before the JPEG payload. Kích thước header theo byte, trước phần JPEG.</summary>
    public const int HeaderSize = 32;

    /// <summary>Maximum JPEG payload size this channel carries (2 MiB). Kích thước JPEG tối đa channel này mang (2 MiB).</summary>
    public const int Capacity = 2 * 1024 * 1024;

    /// <summary>Total section size: header plus capacity. Tổng kích thước vùng: header cộng capacity.</summary>
    public const long TotalSize = HeaderSize + (long)Capacity;

    /// <summary>Offset of the magic field. Vị trí trường magic.</summary>
    public const int OffsetMagic = 0;
    /// <summary>Offset of the version field. Vị trí trường version.</summary>
    public const int OffsetVersion = 4;
    /// <summary>Offset of the width field. Vị trí trường width.</summary>
    public const int OffsetWidth = 8;
    /// <summary>Offset of the height field. Vị trí trường height.</summary>
    public const int OffsetHeight = 12;
    /// <summary>Offset of the JPEG length field. Vị trí trường độ dài JPEG.</summary>
    public const int OffsetJpegLength = 16;
    /// <summary>Offset of the seqlock counter. Vị trí bộ đếm seqlock.</summary>
    public const int OffsetSeq = 20;
    /// <summary>Offset of the last-write tick. Vị trí tick ghi gần nhất.</summary>
    public const int OffsetLastWriteTickMs = 24;
    /// <summary>Offset of the JPEG payload. Vị trí phần JPEG.</summary>
    public const int OffsetJpeg = HeaderSize;
}

/// <summary>
/// Publishes annotated frames into the preview shared memory using a seqlock, so the App never
/// reads a torn JPEG. Owns the section; disposing it releases its mapping, while an App-side reader
/// that is still open keeps the section (and the last frame) alive for the next session.
/// Xuất bản khung đã ghi chú vào shared memory preview theo seqlock, để App không bao giờ đọc phải
/// JPEG bị xé. Sở hữu vùng nhớ; dispose giải phóng mapping của nó, còn reader phía App đang mở vẫn
/// giữ vùng nhớ (và khung cuối) sống cho phiên sau.
/// </summary>
public sealed class PreviewChannelWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryMappedFile _section;
    private readonly MemoryMappedViewAccessor _view;
    private uint _seq;
    private bool _disposed;

    /// <summary>
    /// Creates (or opens) the preview section; tests pass a unique name to stay isolated.
    /// Tạo (hoặc mở) vùng preview; test truyền tên riêng để không đụng nhau.
    /// </summary>
    /// <param name="mapName">Shared-memory name, or <see langword="null"/>/blank for <see cref="PreviewProtocol.MapName"/>. Tên shared memory, hoặc <see langword="null"/> rỗng để dùng <see cref="PreviewProtocol.MapName"/>.</param>
    /// <remarks>
    /// When the name already exists, Windows adopts that mapping's existing size rather than
    /// resizing it — <see cref="MemoryMappedFile.CreateOrOpen(string, long, MemoryMappedFileAccess)"/>
    /// does not fail on a size mismatch. A section too small for <see cref="PreviewProtocol.TotalSize"/>
    /// therefore surfaces later as the view/allocation failure the host already reports as a warning.
    /// Khi tên đã tồn tại, Windows dùng luôn kích thước của mapping sẵn có chứ không resize —
    /// <see cref="MemoryMappedFile.CreateOrOpen(string, long, MemoryMappedFileAccess)"/> không lỗi
    /// khi kích thước khác. Vùng quá nhỏ cho <see cref="PreviewProtocol.TotalSize"/> vì thế chỉ lộ
    /// ra sau đó dưới dạng lỗi tạo view/cấp phát mà host đã báo cảnh báo.
    /// </remarks>
    public PreviewChannelWriter(string? mapName = null)
    {
        string name = string.IsNullOrWhiteSpace(mapName) ? PreviewProtocol.MapName : mapName;
        _section = MemoryMappedFile.CreateOrOpen(
            name, PreviewProtocol.TotalSize, MemoryMappedFileAccess.ReadWrite);
        _view = _section.CreateViewAccessor(
            0, PreviewProtocol.TotalSize, MemoryMappedFileAccess.ReadWrite);

        // The seqlock counter belongs to the SECTION, not to the writer instance: a session may end
        // while an App-side reader keeps the section (and its last frame) alive, so a new writer must
        // continue from the counter already in the header. Rounding down to even discards a half
        // written (odd) value from a crashed writer, and the first write still lands strictly above
        // the last stable frame — so a stale previous-session frame can never out-rank a new one.
        // Bộ đếm seqlock thuộc về VÙNG NHỚ, không phải từng instance writer: phiên có thể kết thúc
        // trong khi reader phía App vẫn giữ vùng nhớ (và khung cuối), nên writer mới phải tiếp tục
        // từ bộ đếm đang có trong header. Làm tròn xuống số chẵn để bỏ giá trị dở dang (lẻ) của
        // writer bị chết, và lần ghi đầu vẫn cao hơn hẳn khung ổn định cuối — nên khung cũ của
        // phiên trước không bao giờ xếp trên khung của phiên mới.
        _seq = _view.ReadUInt32(PreviewProtocol.OffsetSeq) & ~1u;
    }

    /// <summary>
    /// Publishes one annotated JPEG, bumping the seqlock counter by two (odd while writing, even
    /// once the frame is stable). Ghi một JPEG đã ghi chú, tăng bộ đếm seqlock thêm hai (lẻ khi đang
    /// ghi, chẵn khi khung đã ổn định).
    /// </summary>
    /// <param name="width">Annotated frame width in pixels. Chiều rộng khung đã ghi chú (pixel).</param>
    /// <param name="height">Annotated frame height in pixels. Chiều cao khung đã ghi chú (pixel).</param>
    /// <param name="jpeg">Encoded JPEG bytes, at most <see cref="PreviewProtocol.Capacity"/> bytes. Byte JPEG đã mã hoá, tối đa <see cref="PreviewProtocol.Capacity"/> byte.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="jpeg"/> is larger than <see cref="PreviewProtocol.Capacity"/>. Ném khi <paramref name="jpeg"/> lớn hơn <see cref="PreviewProtocol.Capacity"/>.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the writer was disposed. Ném khi writer đã bị dispose.</exception>
    /// <remarks>
    /// Thread-safe: serializing writes against disposal is what keeps a session teardown from
    /// pulling the mapping out from under an in-flight write.
    /// An toàn luồng: tuần tự hoá ghi với dispose để lúc dỡ phiên không rút mapping khỏi tay một
    /// lần ghi đang chạy.
    /// </remarks>
    public void Write(int width, int height, ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length > PreviewProtocol.Capacity)
        {
            throw new ArgumentException(
                $"JPEG payload is {jpeg.Length} bytes, exceeding the {PreviewProtocol.Capacity}-byte preview capacity.",
                nameof(jpeg));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Seqlock: an odd counter tells the reader a write is in flight.
            // Seqlock: bộ đếm lẻ báo cho bên đọc biết đang ghi.
            uint writing = _seq | 1u;
            _view.Write(PreviewProtocol.OffsetSeq, writing);
            Thread.MemoryBarrier();

            _view.Write(PreviewProtocol.OffsetMagic, PreviewProtocol.Magic);
            _view.Write(PreviewProtocol.OffsetVersion, PreviewProtocol.Version);
            _view.Write(PreviewProtocol.OffsetWidth, (uint)width);
            _view.Write(PreviewProtocol.OffsetHeight, (uint)height);
            _view.Write(PreviewProtocol.OffsetJpegLength, (uint)jpeg.Length);
            WriteJpeg(jpeg);
            _view.Write(PreviewProtocol.OffsetLastWriteTickMs, (ulong)Environment.TickCount64);
            Thread.MemoryBarrier();

            _seq = writing + 1u;
            _view.Write(PreviewProtocol.OffsetSeq, _seq);
        }
    }

    /// <summary>
    /// Copies the JPEG into the section. <see cref="MemoryMappedViewAccessor"/> has no span
    /// overload, so the copy bounces through a pooled array (no per-frame allocation).
    /// Sao chép JPEG vào vùng nhớ. <see cref="MemoryMappedViewAccessor"/> không có overload span,
    /// nên bản copy đi qua mảng mượn từ pool (không cấp phát mỗi khung).
    /// </summary>
    private void WriteJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.IsEmpty) return;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(jpeg.Length);
        try
        {
            jpeg.CopyTo(buffer);
            _view.WriteArray(PreviewProtocol.OffsetJpeg, buffer, 0, jpeg.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Releases the section mapping. Giải phóng mapping của vùng nhớ.
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
