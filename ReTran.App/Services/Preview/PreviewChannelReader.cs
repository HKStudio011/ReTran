using System.IO.MemoryMappedFiles;

namespace ReTran_App.Services.Preview;

/// <summary>
/// One annotated frame read out of the preview shared memory. Một khung đã ghi chú đọc từ shared
/// memory preview.
/// </summary>
/// <param name="Seq">Seqlock counter of the section, advancing by two per written frame (even while stable). Bộ đếm seqlock của vùng, tăng hai đơn vị mỗi khung được ghi (chẵn khi ổn định).</param>
/// <param name="Width">Annotated frame width in pixels. Chiều rộng khung đã ghi chú (pixel).</param>
/// <param name="Height">Annotated frame height in pixels. Chiều cao khung đã ghi chú (pixel).</param>
/// <param name="Jpeg">Freshly allocated JPEG bytes. Mảng byte JPEG mới được cấp phát.</param>
public readonly record struct PreviewChannelFrame(long Seq, int Width, int Height, byte[] Jpeg);

/// <summary>
/// Reads annotated frames out of the preview shared memory Core publishes: seqlock check, bounded
/// retries, no torn frames. The caller opens it lazily because Core may not have created the
/// section yet (or may have restarted).
/// Đọc khung đã ghi chú từ shared memory preview do Core xuất bản: kiểm seqlock, thử lại có giới
/// hạn, không nhận khung bị xé. Bên gọi mở reader một cách lười vì Core có thể chưa tạo vùng nhớ
/// (hoặc đã khởi động lại).
/// </summary>
public sealed class PreviewChannelReader : IDisposable
{
    private const int MaxAttempts = 4;

    private readonly MemoryMappedFile _section;
    private readonly MemoryMappedViewAccessor _view;

    private PreviewChannelReader(MemoryMappedFile section, MemoryMappedViewAccessor view)
    {
        _section = section;
        _view = view;
    }

    /// <summary>
    /// Tries to open the preview section for reading. Thử mở vùng preview để đọc.
    /// </summary>
    /// <param name="mapName">Shared-memory name, or <see langword="null"/>/blank for <see cref="PreviewProtocol.MapName"/>. Tên shared memory, hoặc <see langword="null"/> rỗng để dùng <see cref="PreviewProtocol.MapName"/>.</param>
    /// <param name="reader">The opened reader, or <see langword="null"/> when the section is missing. Reader đã mở, hoặc <see langword="null"/> khi thiếu vùng nhớ.</param>
    /// <returns>True when the section was opened; the caller owns the reader. True khi mở được vùng nhớ; bên gọi chịu trách nhiệm dispose reader.</returns>
    public static bool TryOpen(string? mapName, out PreviewChannelReader? reader)
    {
        string name = string.IsNullOrWhiteSpace(mapName) ? PreviewProtocol.MapName : mapName;
        reader = null;
        try
        {
            MemoryMappedFile section = MemoryMappedFile.OpenExisting(name);
            MemoryMappedViewAccessor view = section.CreateViewAccessor(
                0, PreviewProtocol.TotalSize, MemoryMappedFileAccess.Read);
            reader = new PreviewChannelReader(section, view);
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
    /// <param name="frame">The frame, or <see langword="default"/> when none is readable. Khung đọc được, hoặc <see langword="default"/> khi không đọc được khung nào.</param>
    /// <returns>
    /// True when a stable, non-empty frame was read (magic and version matching). False while a
    /// write is in progress, before the first write, or after a torn-read retry budget.
    /// True khi đọc được khung ổn định, khác rỗng (magic và version khớp). False khi đang ghi, trước
    /// lần ghi đầu tiên, hoặc khi hết lượt thử lại vì khung bị xé.
    /// </returns>
    public bool TryRead(out PreviewChannelFrame frame)
    {
        frame = default;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            uint before = _view.ReadUInt32(PreviewProtocol.OffsetSeq);
            if ((before & 1u) != 0)
            {
                continue; // odd counter: a write is in flight. bộ đếm lẻ: đang ghi.
            }
            Thread.MemoryBarrier();

            uint magic = _view.ReadUInt32(PreviewProtocol.OffsetMagic);
            uint version = _view.ReadUInt32(PreviewProtocol.OffsetVersion);
            uint width = _view.ReadUInt32(PreviewProtocol.OffsetWidth);
            uint height = _view.ReadUInt32(PreviewProtocol.OffsetHeight);
            uint jpegLength = _view.ReadUInt32(PreviewProtocol.OffsetJpegLength);
            if (magic != PreviewProtocol.Magic
                || version != PreviewProtocol.Version
                || jpegLength == 0
                || jpegLength > PreviewProtocol.Capacity)
            {
                continue; // never written, foreign layout, or corrupt header. chưa ghi, bố cục lạ, hoặc header hỏng.
            }

            var jpeg = new byte[(int)jpegLength];
            _view.ReadArray(PreviewProtocol.OffsetJpeg, jpeg, 0, jpeg.Length);
            Thread.MemoryBarrier();

            if (before == _view.ReadUInt32(PreviewProtocol.OffsetSeq))
            {
                frame = new PreviewChannelFrame(before, (int)width, (int)height, jpeg);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Releases the reader's mappings. Giải phóng các mapping của reader.
    /// </summary>
    public void Dispose()
    {
        _view.Dispose();
        _section.Dispose();
    }
}
