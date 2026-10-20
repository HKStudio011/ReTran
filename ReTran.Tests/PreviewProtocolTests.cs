using CorePreview = ReTran.Core.Serve.PreviewProtocol;
using AppPreview = ReTran_App.Services.Preview.PreviewProtocol;

/// <summary>
/// App does not reference ReTran.Core, so the preview wire contract must be duplicated on both
/// sides (same pattern as the camera protocol). These tests assert the two constant sets match:
/// if someone changes one side only, the channel breaks silently — the test catches it.
/// App không tham chiếu ReTran.Core, nên hợp đồng dây của preview phải copy ở cả hai phía (cùng
/// pattern giao thức camera). Test assert hai bộ hằng số khớp nhau; nếu ai đó đổi một bên thì
/// channel vỡ âm thầm — test này sẽ bắt.
/// </summary>
public class PreviewProtocolTests
{
    [Fact]
    public void MapName_Matches()
    {
        Assert.Equal(CorePreview.MapName, AppPreview.MapName);
    }

    [Fact]
    public void MagicAndVersion_Match()
    {
        Assert.Equal(CorePreview.Magic, AppPreview.Magic);
        Assert.Equal(CorePreview.Version, AppPreview.Version);
    }

    [Fact]
    public void HeaderSizeAndCapacity_Match()
    {
        Assert.Equal(CorePreview.HeaderSize, AppPreview.HeaderSize);
        Assert.Equal(CorePreview.Capacity, AppPreview.Capacity);
        Assert.Equal(CorePreview.TotalSize, AppPreview.TotalSize);
    }

    [Fact]
    public void FieldOffsets_Match()
    {
        Assert.Equal(CorePreview.OffsetMagic, AppPreview.OffsetMagic);
        Assert.Equal(CorePreview.OffsetVersion, AppPreview.OffsetVersion);
        Assert.Equal(CorePreview.OffsetWidth, AppPreview.OffsetWidth);
        Assert.Equal(CorePreview.OffsetHeight, AppPreview.OffsetHeight);
        Assert.Equal(CorePreview.OffsetJpegLength, AppPreview.OffsetJpegLength);
        Assert.Equal(CorePreview.OffsetSeq, AppPreview.OffsetSeq);
        Assert.Equal(CorePreview.OffsetLastWriteTickMs, AppPreview.OffsetLastWriteTickMs);
        Assert.Equal(CorePreview.OffsetJpeg, AppPreview.OffsetJpeg);
    }

    [Fact]
    public void HeaderLayout_Uses32ByteHeaderAnd2MiBCapacity()
    {
        // The documented layout: header 32 bytes, capacity 2 MiB, jpeg right after the header.
        // Bố cục đã ghi: header 32 byte, capacity 2 MiB, jpeg nằm ngay sau header.
        Assert.Equal(0x45525052u, CorePreview.Magic); // 'RPRE'
        Assert.Equal(1u, CorePreview.Version);
        Assert.Equal(32, CorePreview.HeaderSize);
        Assert.Equal(2 * 1024 * 1024, CorePreview.Capacity);
        Assert.Equal(CorePreview.TotalSize, CorePreview.HeaderSize + (long)CorePreview.Capacity);
        Assert.Equal(0, CorePreview.OffsetMagic);
        Assert.Equal(4, CorePreview.OffsetVersion);
        Assert.Equal(8, CorePreview.OffsetWidth);
        Assert.Equal(12, CorePreview.OffsetHeight);
        Assert.Equal(16, CorePreview.OffsetJpegLength);
        Assert.Equal(20, CorePreview.OffsetSeq);
        Assert.Equal(24, CorePreview.OffsetLastWriteTickMs);
        Assert.Equal(CorePreview.OffsetJpeg, CorePreview.HeaderSize);
    }
}
