using System.IO.MemoryMappedFiles;
using ReTran_App.Services.Preview;
using CorePreview = ReTran.Core.Serve;
using AppProtocol = ReTran_App.Services.Preview.PreviewProtocol;

namespace RetranCore.Tests;

/// <summary>
/// Round-trip tests over the preview shared-memory channel: what the Core writer publishes is
/// exactly what the App reader sees (seqlock, seq monotonicity, visibility).
/// Test vòng lặp qua channel shared memory preview: thứ writer phía Core ghi đúng bằng thứ
/// reader phía App đọc được (seqlock, seq tăng dần, khả năng thấy).
/// </summary>
public class PreviewChannelTests
{
    /// <summary>Fresh shared-memory names per test so parallel runs never collide. Tên shared memory mới cho mỗi test để chạy song song không đụng nhau.</summary>
    private static string UniqueName(string stem) => $@"Local\ReTran.Preview.Test.{stem}.{Guid.NewGuid():N}";

    [Fact]
    public void TryOpen_WithoutWriter_ReturnsFalse()
    {
        Assert.False(PreviewChannelReader.TryOpen(UniqueName("absent"), out PreviewChannelReader? reader));
        Assert.Null(reader);
    }

    [Fact]
    public void TryOpen_NullMapName_UsesDefaultName()
    {
        // Pass-through: opening with a null name resolves to the default map name and must not
        // throw (the real channel may simply be absent -> TryOpen returns false).
        // Đi qua: truyền null sẽ resolve ra tên map mặc định và không được ném (channel thật có
        // thể không tồn tại -> TryOpen trả false).
        bool opened = PreviewChannelReader.TryOpen(null, out PreviewChannelReader? reader);
        reader?.Dispose();
        if (opened)
        {
            Assert.NotNull(reader);
        }
    }

    [Fact]
    public void Write_ReaderSeesSamePixelsAndHeader()
    {
        string mapName = UniqueName("frame");
        using var writer = new CorePreview.PreviewChannelWriter(mapName);

        byte[] jpeg = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        writer.Write(1280, 720, jpeg);

        Assert.True(PreviewChannelReader.TryOpen(mapName, out PreviewChannelReader? reader));
        using (reader)
        {
            Assert.True(reader!.TryRead(out PreviewChannelFrame frame));
            Assert.Equal(2, frame.Seq); // first stable seqlock value: odd-in-progress 1 published as 2
            Assert.Equal(1280, frame.Width);
            Assert.Equal(720, frame.Height);
            Assert.Equal(jpeg, frame.Jpeg);
        }
    }

    [Fact]
    public void Write_SequenceAdvancesByTwoPerFrame()
    {
        string mapName = UniqueName("frame");
        using var writer = new CorePreview.PreviewChannelWriter(mapName);
        Assert.True(PreviewChannelReader.TryOpen(mapName, out PreviewChannelReader? reader));
        using (reader)
        {
            writer.Write(1, 1, [1]);
            Assert.True(reader!.TryRead(out PreviewChannelFrame first));
            Assert.Equal(2, first.Seq);

            writer.Write(1, 1, [2]);
            Assert.True(reader.TryRead(out PreviewChannelFrame second));
            Assert.Equal(4, second.Seq);

            writer.Write(1, 1, [3]);
            Assert.True(reader.TryRead(out PreviewChannelFrame third));
            Assert.Equal(6, third.Seq);
        }
    }

    [Fact]
    public void SecondWriterGeneration_ContinuesSequenceAndPayload()
    {
        // The section outlives a writer generation (the App's reader keeps it open across sessions),
        // so the seqlock counter must be continuous per SECTION: an old-session frame must never
        // out-rank a new-session frame. Regression for the poisoned-guard scenario, where a stale
        // notification after a session restart made the App freeze the preview until the new
        // writer's counter caught up with the old session's last seq.
        // Vùng nhớ sống lâu hơn một thế hệ writer (reader phía App giữ nó qua các phiên), nên bộ đếm
        // seqlock phải liên tục theo VÙNG NHỚ: khung của phiên cũ không bao giờ được xếp trên khung
        // của phiên mới. Test hồi quy cho kịch bản "guard bị nhiễm độc": một thông báo cũ sau khi
        // restart phiên từng làm App đóng băng preview cho tới khi bộ đếm writer mới đuổi kịp
        // seq cuối của phiên cũ.
        string mapName = UniqueName("generations");
        var first = new CorePreview.PreviewChannelWriter(mapName);
        Assert.True(PreviewChannelReader.TryOpen(mapName, out PreviewChannelReader? reader));
        using (reader)
        {
            first.Write(2, 2, [1, 1, 1]);
            first.Write(2, 2, [2, 2, 2]);
            Assert.True(reader!.TryRead(out PreviewChannelFrame frame));
            Assert.Equal([2, 2, 2], frame.Jpeg);
            long lastOfFirstGeneration = frame.Seq;
            Assert.Equal(4L, lastOfFirstGeneration); // two writes: 2 then 4

            // The session ends but the App's reader keeps the section (and its last frame) alive,
            // so the next writer adopts it instead of starting from a zeroed counter.
            // Phiên kết thúc nhưng reader phía App vẫn giữ vùng nhớ (và khung cuối), nên writer kế
            // tiếp dùng lại nó thay vì bắt đầu từ bộ đếm 0.
            first.Dispose();

            using var second = new CorePreview.PreviewChannelWriter(mapName);
            second.Write(2, 2, [3, 3, 3]);

            Assert.True(reader.TryRead(out PreviewChannelFrame afterRestart));
            Assert.Equal([3, 3, 3], afterRestart.Jpeg);
            Assert.Equal(2, afterRestart.Width);
            Assert.Equal(2, afterRestart.Height);
            Assert.True(
                afterRestart.Seq > lastOfFirstGeneration,
                $"seq must keep increasing across writer generations: {lastOfFirstGeneration} -> {afterRestart.Seq}");
        }
    }

    [Fact]
    public void Write_OversizeJpeg_Throws()
    {
        using var writer = new CorePreview.PreviewChannelWriter(UniqueName("frame"));

        Assert.Throws<ArgumentException>(() =>
            writer.Write(2, 2, new byte[AppProtocol.Capacity + 1]));
    }

    [Fact]
    public void TryRead_OddSeq_WriteInProgress_ReturnsFalse()
    {
        string mapName = UniqueName("frame");
        using var writer = new CorePreview.PreviewChannelWriter(mapName);
        writer.Write(2, 2, [1, 2, 3]);
        Assert.True(PreviewChannelReader.TryOpen(mapName, out PreviewChannelReader? reader));
        using (reader)
        {
            // Simulate a squashed write-in-progress by making the counter odd in place.
            // Giả lập "đang ghi" bằng cách đặt bộ đếm thành lẻ tại chỗ.
            using MemoryMappedFile section = MemoryMappedFile.OpenExisting(mapName);
            using MemoryMappedViewAccessor view = section.CreateViewAccessor(
                0, AppProtocol.TotalSize, MemoryMappedFileAccess.ReadWrite);
            uint seq = view.ReadUInt32(AppProtocol.OffsetSeq);
            view.Write(AppProtocol.OffsetSeq, seq | 1u);

            Assert.False(reader!.TryRead(out PreviewChannelFrame _));
        }
    }

    [Fact]
    public void TryRead_WrongMagic_ReturnsFalse()
    {
        string mapName = UniqueName("frame");
        using var writer = new CorePreview.PreviewChannelWriter(mapName);
        writer.Write(2, 2, [1, 2, 3]);
        Assert.True(PreviewChannelReader.TryOpen(mapName, out PreviewChannelReader? reader));
        using (reader)
        {
            using MemoryMappedFile section = MemoryMappedFile.OpenExisting(mapName);
            using MemoryMappedViewAccessor view = section.CreateViewAccessor(
                0, AppProtocol.TotalSize, MemoryMappedFileAccess.ReadWrite);
            view.Write(AppProtocol.OffsetMagic, 0xFFFFFFFFu);

            Assert.False(reader!.TryRead(out PreviewChannelFrame _));
        }
    }

    [Fact]
    public void TryRead_EmptyJpegLength_ReturnsFalse()
    {
        // A never-written section has magic 0 and jpegLength 0: absent frame, not a frame.
        // Vùng chưa từng được ghi có magic 0 và jpegLength 0: không có khung, không phải khung.
        string mapName = UniqueName("empty");
        using var writer = new CorePreview.PreviewChannelWriter(mapName);

        Assert.True(PreviewChannelReader.TryOpen(mapName, out PreviewChannelReader? reader));
        using (reader)
        {
            Assert.False(reader!.TryRead(out PreviewChannelFrame _));
        }
    }
}
