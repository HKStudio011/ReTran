namespace ReTran_App.Services.Preview;

/// <summary>
/// Wire contract of the annotated-frame preview between Core (writer) and this app (reader): the
/// annotated JPEG of the latest frame lives in a named shared-memory section, so it no longer
/// travels base64-encoded through the <c>frame.annotated</c> stdio notification.
/// Giao kèo dây của preview khung đã ghi chú giữa Core (bên ghi) và app này (bên đọc): JPEG đã ghi
/// chú của khung mới nhất nằm trong vùng shared memory có tên, nên nó không còn đi base64 qua
/// thông báo stdio <c>frame.annotated</c>.
/// </summary>
/// <remarks>
/// Header layout (fixed, mirrored byte-for-byte by <c>ReTran.Core/Serve/PreviewProtocol.cs</c>):
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
/// This app does not reference <c>ReTran.Core</c> (the same reason <c>CameraProtocol</c> mirrors the
/// native filter's C++ layout), so the layout is duplicated here and
/// <c>ReTran.Tests/PreviewProtocolTests.cs</c> asserts both constant sets stay equal.
/// App này không tham chiếu <c>ReTran.Core</c> (cùng lý do <c>CameraProtocol</c> nhân bản bố cục
/// C++ của filter native), nên bố cục được copy ở đây và
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
