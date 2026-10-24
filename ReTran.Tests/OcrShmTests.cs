using System.IO.MemoryMappedFiles;
using ReTran.Core.Capture;
using ReTran.Core.Ocr;
using Xunit;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

/// <summary>
/// Round-trip tests for the OCR shared-memory protocol: what <see cref="OcrShmWriter"/> publishes
/// is exactly what the sidecar's Python reader (and the test reader) sees.
/// Test vòng lặp cho giao thức shared memory OCR: thứ <see cref="OcrShmWriter"/> ghi đúng bằng
/// thứ reader Python phía sidecar (và reader trong test) đọc được.
/// </summary>
public class OcrShmTests
{
    /// <summary>Fresh section names per test so runs never collide. Tên section mới mỗi test để không đụng nhau.</summary>
    private static string UniqueName(string stem) =>
        $@"Local\ReTran.Tests.OcrShm.{stem}.{Guid.NewGuid():N}";

    /// <summary>Deterministic BGRA pixel pattern. Mẫu điểm ảnh BGRA tất định.</summary>
    private static byte[] Gradient(int width, int height, byte salt = 0)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 7 + salt) % 251);
        return pixels;
    }

    [Fact]
    public void ProtocolConstants_PinTheCrossLanguageContract()
    {
        // The name and fixed capacity are mirrored byte-for-byte by the Python side
        // (retran_core.ocr.shm); a one-sided edit breaks the channel silently.
        // Tên và capacity cố định được nhân bản byte-cho-byte phía Python
        // (retran_core.ocr.shm); sửa một bên sẽ làm channel vỡ âm thầm.
        Assert.Equal(@"Local\ReTran.Ocr.V1", OcrShm.Name);
        Assert.Equal(64, OcrShm.HeaderSize);
        Assert.Equal(3840, OcrShm.MaxWidth);
        Assert.Equal(2160, OcrShm.MaxHeight);
        Assert.Equal((long)3840 * 2160 * 4, OcrShm.PayloadCapacity);
        Assert.Equal(OcrShm.HeaderSize + OcrShm.PayloadCapacity, OcrShm.TotalSize);
        Assert.Equal(1, OcrShm.FormatBgra32);
    }

    [Fact]
    public void Publish_FirstFrame_WritesHeaderAndPayloadExactly()
    {
        string name = UniqueName("roundtrip");
        byte[] pixels = Gradient(4, 3);
        using var writer = new OcrShmWriter(name);

        long seq = writer.Publish(Frame.FromBgra(4, 3, pixels));

        // Fresh section: 0 -> odd while writing -> the first stable seq is 2.
        // Section mới: 0 -> lẻ khi đang ghi -> seq ổn định đầu tiên là 2.
        Assert.Equal(2, seq);

        using var mmf = MemoryMappedFile.OpenExisting(name);
        using var view = mmf.CreateViewAccessor(0, OcrShm.HeaderSize + pixels.Length, MemoryMappedFileAccess.Read);
        byte[] magic = new byte[4];
        view.ReadArray(0, magic, 0, 4);
        Assert.Equal(new byte[] { 0x52, 0x54, 0x4F, 0x43 }, magic); // "RTOC"
        Assert.Equal(1, view.ReadInt32(OcrShm.OffsetVersion));
        Assert.Equal(seq, view.ReadInt64(OcrShm.OffsetSeq));
        Assert.Equal(4, view.ReadInt32(OcrShm.OffsetWidth));
        Assert.Equal(3, view.ReadInt32(OcrShm.OffsetHeight));
        Assert.Equal(16, view.ReadInt32(OcrShm.OffsetStride));
        Assert.Equal(1, view.ReadInt32(OcrShm.OffsetFormat));
        byte[] payload = new byte[pixels.Length];
        view.ReadArray(OcrShm.HeaderSize, payload, 0, payload.Length);
        Assert.Equal(pixels, payload);
    }

    [Fact]
    public void Publish_SecondFrame_SeqIncrements_NewWriterContinuesSectionCounter()
    {
        string name = UniqueName("seq");
        // The sidecar's reader maps the section once and keeps it open, so the section (and its
        // last seq) survives a run's writer being disposed. Model that with a read handle held
        // while the first writer goes away.
        // Reader của sidecar map section một lần và giữ nó, nên section (và seq cuối) sống qua
        // lúc writer của một lượt chạy bị dispose. Mô hình hoá bằng handle đọc còn mở khi
        // writer thứ nhất kết thúc.
        using var firstWriter = new OcrShmWriter(name);
        using MemoryMappedFile reader = MemoryMappedFile.OpenExisting(name);

        long first = firstWriter.Publish(Frame.FromBgra(2, 2, new byte[16]));
        long second = firstWriter.Publish(Frame.FromBgra(2, 2, new byte[16]));
        Assert.Equal(first + 2, second);

        firstWriter.Dispose(); // the run ends; the reader keeps the section alive

        // A new run's writer must continue the section counter, so a seq a reader may still
        // hold from the previous run can never be reused.
        // Writer của lần chạy mới phải tiếp tục bộ đếm của section, để seq mà reader
        // còn giữ từ lần trước không bao giờ bị dùng lại.
        using var secondWriter = new OcrShmWriter(name);
        long third = secondWriter.Publish(Frame.FromBgra(2, 2, new byte[16]));
        Assert.Equal(second + 2, third);
    }

    [Fact]
    public void Publish_TwoFrameSizes_ShareTheFixedSection()
    {
        string name = UniqueName("sizes");
        byte[] big = Gradient(8, 2);
        byte[] small = Gradient(2, 2, salt: 17);
        using var writer = new OcrShmWriter(name);

        writer.Publish(Frame.FromBgra(8, 2, big));
        writer.Publish(Frame.FromBgra(2, 2, small));

        // Same fixed-capacity section: the small frame rewrites the header and the start of
        // the payload area, nothing is recreated.
        // Cùng một section capacity cố định: khung nhỏ ghi đè header và đầu vùng payload,
        // không tạo lại gì cả.
        using var mmf = MemoryMappedFile.OpenExisting(name);
        using var view = mmf.CreateViewAccessor(0, OcrShm.HeaderSize + big.Length, MemoryMappedFileAccess.Read);
        Assert.Equal(2, view.ReadInt32(OcrShm.OffsetWidth));
        Assert.Equal(2, view.ReadInt32(OcrShm.OffsetHeight));
        Assert.Equal(8, view.ReadInt32(OcrShm.OffsetStride));
        byte[] payload = new byte[small.Length];
        view.ReadArray(OcrShm.HeaderSize, payload, 0, payload.Length);
        Assert.Equal(small, payload);
    }

    [Fact]
    public void Publish_OversizeFrame_Throws_AndLeavesSeqlockStable()
    {
        string name = UniqueName("oversize");
        using var writer = new OcrShmWriter(name);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.Publish(Frame.FromBgra(OcrShm.MaxWidth + 1, 1, new byte[(OcrShm.MaxWidth + 1) * 4])));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.Publish(Frame.FromBgra(1, OcrShm.MaxHeight + 1, new byte[(OcrShm.MaxHeight + 1) * 4])));

        // A rejected frame must not consume a seq or leave the counter odd ("writing" forever).
        // Khung bị từ chối không được tiêu seq hay để bộ đếm lẻ ("đang ghi" mãi).
        Assert.Equal(2, writer.Publish(Frame.FromBgra(2, 2, new byte[16])));
    }
}
