using ReTran_App.Services.Camera;

/// <summary>
/// Lifecycle tests for <see cref="CameraOutputService"/>: it must create the shared-memory section
/// only when there is something to show, and refuse to write again after disposal.
/// Test vòng đời cho <see cref="CameraOutputService"/>: chỉ tạo vùng shared memory khi có nội dung
/// để hiển thị, và từ chối ghi tiếp sau khi đã dispose.
/// </summary>
public class CameraOutputServiceTests
{
    private static string UniqueName(string stem) => $@"Local\ReTran.Camera.Test.{stem}.{Guid.NewGuid():N}";

    [Fact]
    public void Hold_WithoutAnyFrame_CreatesNoSection()
    {
        string frameName = UniqueName("frame");
        using var service = new CameraOutputService(frameName, UniqueName("heartbeat"));

        service.Hold();

        Assert.False(CameraFrameReader.TryOpen(frameName, out CameraFrameReader? reader));
        Assert.Null(reader);
    }

    [Fact]
    public void Dispose_WithoutSubmit_DoesNotThrow()
    {
        var service = new CameraOutputService(UniqueName("frame"), UniqueName("heartbeat"));

        service.Dispose();
    }

    [Fact]
    public void Submit_AfterDispose_Throws()
    {
        var service = new CameraOutputService(UniqueName("frame"), UniqueName("heartbeat"));
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.Submit(1, 2, 2, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Hold_AfterDispose_DoesNotThrow()
    {
        var service = new CameraOutputService(UniqueName("frame"), UniqueName("heartbeat"));
        service.Dispose();

        // Shutdown paths call Hold() last; disposing first must not turn that into a crash.
        // Đường tắt app có thể gọi Hold() sau cùng; dispose trước không được biến nó thành crash.
        service.Hold();
    }
}
