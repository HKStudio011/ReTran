using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="SetupCoordinator.IsModelDirInstalled"/> — the wizard's "installed" definition
/// must MIRROR the sidecar's (<c>ocr/models.py</c>): the model dir exists, is non-empty, and no
/// <c>&lt;name&gt;.partial</c> marker sits next to it. An interrupted download leaves both the
/// half-filled dir and the marker, so a bare Directory.Exists counted a half-downloaded model as
/// installed and the repair rerun skipped it (BA report 2026-11-17 — serious).
/// Test cho <see cref="SetupCoordinator.IsModelDirInstalled"/> — định nghĩa "đã cài" của wizard phải
/// KHỚP sidecar (<c>ocr/models.py</c>): thư mục model tồn tại, không rỗng, và không có marker
/// <c>&lt;tên&gt;.partial</c> cạnh nó. Lượt tải dang dở để lại cả thư mục nửa vời lẫn marker, nên
/// <c>Directory.Exists</c> trần đã từng coi model tải dở là đã cài và lượt sửa lỗi bỏ qua nó
/// (báo cáo BA 2026-11-17 — nghiêm trọng).
/// </summary>
public class SetupModelIntegrityTests : IDisposable
{
    private readonly string _official = Path.Combine(
        Path.GetTempPath(), $"retran-integrity-{Guid.NewGuid():N}", "official_models");

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(_official, ".."), recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string ModelDir(string name) => Path.Combine(_official, name);

    [Fact]
    public void DirWithFile_NoMarker_CountsInstalled()
    {
        Directory.CreateDirectory(ModelDir("PP-OCRv6_small_det"));
        File.WriteAllText(Path.Combine(ModelDir("PP-OCRv6_small_det"), "model.bin"), "x");

        Assert.True(SetupCoordinator.IsModelDirInstalled(ModelDir("PP-OCRv6_small_det")));
    }

    [Fact]
    public void PartialMarker_NextToDir_CountsNotInstalled()
    {
        // Giả lập tải dở: thư mục đã có file (.cache của paddlex/huggingface) + marker .partial cạnh nó.
        Directory.CreateDirectory(ModelDir("PP-OCRv6_medium_rec"));
        File.WriteAllText(Path.Combine(ModelDir("PP-OCRv6_medium_rec"), ".cachefile"), "x");
        File.WriteAllText(ModelDir("PP-OCRv6_medium_rec") + ".partial", "");

        Assert.False(SetupCoordinator.IsModelDirInstalled(ModelDir("PP-OCRv6_medium_rec")));
    }

    [Fact]
    public void EmptyDir_CountsNotInstalled()
    {
        Directory.CreateDirectory(ModelDir("PP-OCRv6_small_rec"));

        Assert.False(SetupCoordinator.IsModelDirInstalled(ModelDir("PP-OCRv6_small_rec")));
    }

    [Fact]
    public void MissingDir_CountsNotInstalled()
    {
        Assert.False(SetupCoordinator.IsModelDirInstalled(ModelDir("nope")));
    }
}
