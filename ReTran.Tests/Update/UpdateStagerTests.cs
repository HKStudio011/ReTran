using ReTran.Updater;

namespace ReTran.Tests.Update;

/// <summary>
/// Test staging/unlock/copy của updater — cốt lõi chống "extract dở dang làm
/// lẫn runtime hai kiểu trong appDir" (bug host ".NET missing" sau update).
/// </summary>
public class UpdateStagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rb-stager-" + Guid.NewGuid().ToString("N"));

    private string StagingDir => Path.Combine(_root, "staging");
    private string AppDir => Path.Combine(_root, "app");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void CreateDirs()
    {
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(AppDir);
    }

    private static string MakeZip(string dir, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "update.zip");
        using var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }

    [Fact]
    public void ExtractToStaging_ExtractsFilesAndSubdirectories()
    {
        var zip = MakeZip(_root, ("app.exe", "exe"), ("wwwroot/index.html", "<html/>"));

        var staging = UpdateStager.ExtractToStaging(zip);

        try
        {
            Assert.Equal("exe", File.ReadAllText(Path.Combine(staging, "app.exe")));
            Assert.Equal("<html/>", File.ReadAllText(Path.Combine(staging, "wwwroot", "index.html")));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public void ExtractToStaging_TwoExtractions_DoNotDeleteOrOverwriteEachOther()
    {
        var firstZip = MakeZip(Path.Combine(_root, "first"),
            ("app.exe", "first"), ("first-only.txt", "keep"));
        var secondZip = MakeZip(Path.Combine(_root, "second"),
            ("app.exe", "second"), ("second-only.txt", "new"));
        string? firstStaging = null;
        string? secondStaging = null;

        try
        {
            firstStaging = UpdateStager.ExtractToStaging(firstZip);
            var sentinel = Path.Combine(firstStaging, "sentinel.txt");
            File.WriteAllText(sentinel, "still owned by first extraction");

            secondStaging = UpdateStager.ExtractToStaging(secondZip);

            Assert.NotEqual(firstStaging, secondStaging);
            Assert.Equal("first", File.ReadAllText(Path.Combine(firstStaging, "app.exe")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(firstStaging, "first-only.txt")));
            Assert.Equal("still owned by first extraction", File.ReadAllText(sentinel));
            Assert.Equal("second", File.ReadAllText(Path.Combine(secondStaging, "app.exe")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(secondStaging, "second-only.txt")));
            Assert.False(File.Exists(Path.Combine(secondStaging, "first-only.txt")));
        }
        finally
        {
            foreach (var staging in new[] { firstStaging, secondStaging })
            {
                if (staging is not null && Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void ExtractToStaging_WhenZipBroken_ThrowsWithoutTouchingAppDir()
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "bad.zip");
        File.WriteAllText(zip, "not a zip");
        CreateDirs();
        var sentinel = Path.Combine(AppDir, "untouched.txt");
        File.WriteAllText(sentinel, "x");

        Assert.ThrowsAny<InvalidDataException>(() => UpdateStager.ExtractToStaging(zip));

        // Trích fail ở staging — appDir không được đụng tới
        Assert.Equal("x", File.ReadAllText(sentinel));
    }

    [Fact]
    public void WaitUntilWritable_WhenAllUnlocked_ReturnsTrue()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        File.WriteAllText(Path.Combine(AppDir, "a.dll"), "old");

        var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(5));

        Assert.True(ok);
    }

    [Fact]
    public void WaitUntilWritable_WhenFileLocked_ReturnsFalseAfterTimeout()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        var target = Path.Combine(AppDir, "a.dll");
        File.WriteAllText(target, "old");

        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromMilliseconds(400));

            Assert.False(ok);
        }
    }

    [Fact]
    public async Task WaitUntilWritable_WhenLockReleasedDuringWait_ReturnsTrue()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        var target = Path.Combine(AppDir, "a.dll");
        File.WriteAllText(target, "old");

        var handle = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var releaser = Task.Run(async () =>
        {
            await Task.Delay(700);
            handle.Dispose();
        });

        var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(10));
        await releaser;

        Assert.True(ok);
    }

    [Fact]
    public void WaitUntilWritable_WhenFileOnlyInStaging_IgnoresIt()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "brand-new.dll"), "new");

        // File mới chưa có ở appDir — không thể bị lock, không được block
        var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(5));

        Assert.True(ok);
    }

    [Fact]
    public void CopyDirectory_OverwritesExisting_AndCreatesSubdirectories()
    {
        CreateDirs();
        Directory.CreateDirectory(Path.Combine(StagingDir, "wwwroot"));
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        File.WriteAllText(Path.Combine(StagingDir, "wwwroot", "index.html"), "<html/>");
        File.WriteAllText(Path.Combine(AppDir, "a.dll"), "old");

        UpdateStager.CopyDirectory(StagingDir, AppDir);

        Assert.Equal("new", File.ReadAllText(Path.Combine(AppDir, "a.dll")));
        Assert.Equal("<html/>", File.ReadAllText(Path.Combine(AppDir, "wwwroot", "index.html")));
    }

    [Fact]
    public void CopyDirectory_PreservesForeignFilesAndDirectories_InAppDir()
    {
        // Hàng rào (spec 2026-11-03-first-run-setup-wizard §5.2): user state (.venv, models)
        // phải sống sót qua update — updater chỉ ghi đè, không bao giờ xoá trong appDir.
        CreateDirs();
        Directory.CreateDirectory(Path.Combine(AppDir, ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(AppDir, ".venv", "Scripts", "python.exe"), "py");
        Directory.CreateDirectory(Path.Combine(AppDir, "models", "official_models", "m"));
        File.WriteAllText(Path.Combine(AppDir, "models", "official_models", "m", "f.txt"), "model");
        File.WriteAllText(Path.Combine(AppDir, "extra.txt"), "extra");
        File.WriteAllText(Path.Combine(StagingDir, "ReTran.App.exe"), "new-exe");

        UpdateStager.CopyDirectory(StagingDir, AppDir);

        Assert.Equal("py", File.ReadAllText(Path.Combine(AppDir, ".venv", "Scripts", "python.exe")));
        Assert.Equal("model", File.ReadAllText(Path.Combine(AppDir, "models", "official_models", "m", "f.txt")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(AppDir, "extra.txt")));
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(AppDir, "ReTran.App.exe")));
    }

    private const string SelfContainedRuntimeConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}""";

    private const string FrameworkDependentRuntimeConfig =
        """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}""";

    /// <summary>Staging đúng layout self-contained: runtimeconfig includedFrameworks + hostfxr cạnh nhau.</summary>
    private void CreateSelfContainedStaging(string runtimeConfig = SelfContainedRuntimeConfig, bool hostfxr = true)
    {
        Directory.CreateDirectory(StagingDir);
        File.WriteAllText(Path.Combine(StagingDir, "ReTran.App.runtimeconfig.json"), runtimeConfig);
        if (hostfxr)
            File.WriteAllText(Path.Combine(StagingDir, "hostfxr.dll"), "bin");
    }

    [Fact]
    public void VerifySelfContained_WhenSelfContainedLayout_ReturnsTrue()
    {
        CreateSelfContainedStaging();

        Assert.True(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenFrameworkDependentRuntimeConfig_ReturnsFalse()
    {
        // Zip FDD ghi đè appDir = bug ".NET missing" — phải chặn trước khi copy
        CreateSelfContainedStaging(runtimeConfig: FrameworkDependentRuntimeConfig);

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenHostfxrMissing_ReturnsFalse()
    {
        CreateSelfContainedStaging(hostfxr: false);

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenRuntimeConfigMissing_ReturnsFalse()
    {
        Directory.CreateDirectory(StagingDir);
        File.WriteAllText(Path.Combine(StagingDir, "hostfxr.dll"), "bin");

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenRuntimeConfigBrokenJson_ReturnsFalse()
    {
        CreateSelfContainedStaging(runtimeConfig: "{not json");

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }
}
