using ReTran_App.Core;

/// <summary>
/// Kiểm thử thứ tự phân giải ReTran.Core.exe: env → cạnh bên (sibling) → repo-walk Debug.
/// Tests for the ReTran.Core.exe resolution order: env → sibling → repo-walk Debug.
/// </summary>
public class CoreLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _envDir;
    private readonly string _baseDir;

    public CoreLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"retran-corelocator-{Guid.NewGuid():N}");
        _envDir = Path.Combine(_root, "env");
        _baseDir = Path.Combine(_root, "base");
        Directory.CreateDirectory(_envDir);
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string CreateFakeCore(string dir)
    {
        string exe = Path.Combine(dir, "ReTran.Core.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return exe;
    }

    [Fact]
    public void FindCoreExe_EnvFileExists_ReturnsEnvPath()
    {
        string envExe = CreateFakeCore(_envDir);
        CreateFakeCore(_baseDir); // sibling cũng có — env vẫn phải thắng

        string? found = CoreLocator.FindCoreExe(envExe, _baseDir);

        Assert.Equal(envExe, found);
    }

    [Fact]
    public void FindCoreExe_EnvNull_SiblingExists_ReturnsSibling()
    {
        string sibling = CreateFakeCore(_baseDir);

        string? found = CoreLocator.FindCoreExe(null, _baseDir);

        Assert.Equal(sibling, found);
    }

    [Fact]
    public void FindCoreExe_EnvMissingFile_SiblingExists_ReturnsSibling()
    {
        string sibling = CreateFakeCore(_baseDir);
        string missing = Path.Combine(_envDir, "nope.exe");

        string? found = CoreLocator.FindCoreExe(missing, _baseDir);

        Assert.Equal(sibling, found);
    }

    [Fact]
    public void FindCoreExe_NothingFound_DoesNotThrow_ReturnsNullOrExistingFile()
    {
        // Temp dir không có file nào; đi lên parent cũng không có cấu trúc repo → không được ném lỗi.
        string? found = CoreLocator.FindCoreExe(null, _baseDir);

        if (found is not null)
            Assert.True(File.Exists(found), $"fallback phải trỏ tới file đã tồn tại hoặc null, got: {found}");
    }

    [Fact]
    public void FindCoreExe_EmptyEnvAndNoSibling_DoesNotThrow()
    {
        string? found = CoreLocator.FindCoreExe(string.Empty, _baseDir);

        Assert.True(found is null || File.Exists(found));
    }
}
