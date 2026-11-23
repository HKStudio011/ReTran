using System.Diagnostics;
using ReTran.Core.JsonRpc;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlmModelManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"retran-llm-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private LlmModelManager Create(Func<LlmModelSpec, string, CancellationToken, Task>? downloader = null)
    {
        Directory.CreateDirectory(_dir);
        return new LlmModelManager(
            Path.Combine(_dir, "models"),
            Path.Combine(_dir, "models.json"),
            downloader);
    }

    private static Task WriteBytesAsync(LlmModelSpec spec, string path, CancellationToken ct) =>
        File.WriteAllBytesAsync(path, new byte[4], ct);

    private static void CreateInstalled(LlmModelManager manager, string name)
    {
        string path = manager.ModelPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4]);
    }

    [Fact]
    public void Catalog_HasTwelveSpecs_WithExpectedShapes()
    {
        Assert.Equal(12, LlmModelCatalog.All.Count);
        LlmModelSpec small = LlmModelCatalog.All.Single(m => m.Name == "Hy-MT2-1.8B-UD-Q4_K_XL");
        Assert.Equal("unsloth/Hy-MT2-1.8B-GGUF", small.Repo);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL.gguf", small.File);
        Assert.Equal("UD-Q4_K_XL", small.Quant);
        Assert.Equal("1.8B", small.SizeClass);
        Assert.InRange(small.SizeBytes, 1_100_000_000, 1_300_000_000);

        LlmModelSpec large = LlmModelCatalog.All.Single(m => m.Name == "Hy-MT2-7B-UD-Q4_K_XL");
        Assert.Equal("7B", large.SizeClass);
        Assert.InRange(large.SizeBytes, 4_600_000_000, 5_000_000_000);
    }

    [Fact]
    public void List_DerivesInstalledFromFilesystem()
    {
        LlmModelManager manager = Create();

        Assert.All(manager.List(), info => Assert.False(info.Installed));

        CreateInstalled(manager, "Hy-MT2-1.8B-UD-Q4_K_XL");
        LlmModelInfo info2 = manager.List().Single(i => i.Name == "Hy-MT2-1.8B-UD-Q4_K_XL");
        Assert.True(info2.Installed);
        Assert.False(info2.InUse);
    }

    [Fact]
    public void List_PartialMarker_NotInstalled()
    {
        LlmModelManager manager = Create();
        string path = manager.ModelPath("Hy-MT2-1.8B-Q4_K_M");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".partial", new byte[4]);

        Assert.False(manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q4_K_M").Installed);
    }

    [Fact]
    public async Task StartDownload_Deduplicates_AndBecomesInstalled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlmModelManager manager = Create(async (spec, path, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            await WriteBytesAsync(spec, path, ct);
        });

        Assert.True(manager.StartDownload("Hy-MT2-1.8B-Q6_K"));
        Assert.False(manager.StartDownload("Hy-MT2-1.8B-Q6_K")); // đang tải → không nhận thêm
        Assert.True(manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q6_K").Downloading);
        Assert.False(manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q6_K").Installed);

        gate.SetResult();
        Assert.True(await WaitUntilInstalledAsync(manager, "Hy-MT2-1.8B-Q6_K"));
        Assert.False(manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q6_K").Downloading);
    }

    [Fact]
    public async Task FailedDownload_ReportsError_AndRetryClearsIt()
    {
        int attempts = 0;
        var retryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlmModelManager manager = Create(async (spec, path, ct) =>
        {
            attempts++;
            if (attempts == 1) throw new IOException("disk full");
            await retryGate.Task.WaitAsync(ct);
            await WriteBytesAsync(spec, path, ct);
        });
        const string name = "Hy-MT2-1.8B-Q6_K";

        Assert.True(manager.StartDownload(name));
        await WaitUntilDownloadStopsAsync(manager, name);
        LlmModelInfo failed = manager.List().Single(i => i.Name == name);
        Assert.False(failed.Installed);
        Assert.False(failed.Downloading);
        Assert.Equal("disk full", failed.DownloadError);

        Assert.True(manager.StartDownload(name));
        LlmModelInfo retrying = manager.List().Single(i => i.Name == name);
        Assert.Null(retrying.DownloadError);
        Assert.True(retrying.Downloading);
        retryGate.SetResult();
        Assert.True(await WaitUntilInstalledAsync(manager, name));
        Assert.Null(manager.List().Single(i => i.Name == name).DownloadError);
    }

    [Fact]
    public async Task Delete_RefusesActive_RefusesDownloading()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlmModelManager manager = Create(async (spec, path, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            await WriteBytesAsync(spec, path, ct);
        });

        CreateInstalled(manager, "Hy-MT2-1.8B-Q5_K_M");
        manager.SetActive("Hy-MT2-1.8B-Q5_K_M");
        Assert.Throws<RpcMethodException>(() => manager.Delete("Hy-MT2-1.8B-Q5_K_M"));

        Assert.True(manager.StartDownload("Hy-MT2-1.8B-Q8_0"));
        Assert.Throws<RpcMethodException>(() => manager.Delete("Hy-MT2-1.8B-Q8_0"));
        gate.SetResult();
        Assert.True(await WaitUntilInstalledAsync(manager, "Hy-MT2-1.8B-Q8_0")); // join the background file work before Dispose deletes the fixture
    }

    [Fact]
    public void Delete_RemovesInstalledFile()
    {
        LlmModelManager manager = Create();
        CreateInstalled(manager, "Hy-MT2-1.8B-Q4_K_M");

        manager.Delete("Hy-MT2-1.8B-Q4_K_M");

        Assert.False(File.Exists(manager.ModelPath("Hy-MT2-1.8B-Q4_K_M")));
        Assert.False(manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q4_K_M").Installed);
    }

    [Fact]
    public void SetActive_RequiresInstalled_AndRoundTrips()
    {
        LlmModelManager manager = Create();
        Assert.Throws<RpcMethodException>(() => manager.SetActive("Hy-MT2-1.8B-Q4_K_M"));

        CreateInstalled(manager, "Hy-MT2-1.8B-Q4_K_M");
        manager.SetActive("Hy-MT2-1.8B-Q4_K_M");
        Assert.Equal("Hy-MT2-1.8B-Q4_K_M", manager.ActiveModel);
        Assert.Equal(manager.ModelPath("Hy-MT2-1.8B-Q4_K_M"), manager.ActiveModelPath);
        Assert.True(manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q4_K_M").InUse);

        // Manager mới đọc lại state file — lựa chọn bền qua process.
        LlmModelManager reopened = new(
            Path.Combine(_dir, "models"), Path.Combine(_dir, "models.json"));
        Assert.Equal("Hy-MT2-1.8B-Q4_K_M", reopened.ActiveModel);
    }

    [Fact]
    public void UnknownModel_InvalidParams()
    {
        LlmModelManager manager = Create();
        Assert.Throws<RpcMethodException>(() => manager.ModelPath("nope"));
        Assert.Throws<RpcMethodException>(() => manager.Delete("nope"));
    }

    [Fact]
    public async Task Progress_AppearsDuringDownload_AndClears()
    {
        // Tiến độ byte chỉ có khi lượt tải đang chạy và có báo (spec vòng 2 §9); xong thì biến mất.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlmModelManager manager = Create(async (spec, path, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            await WriteBytesAsync(spec, path, ct);
        });

        Assert.True(manager.StartDownload("Hy-MT2-1.8B-Q6_K"));
        manager.ReportDownloadProgress("Hy-MT2-1.8B-Q6_K", 100, 400, 50.0);

        LlmModelInfo info = manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q6_K");
        Assert.NotNull(info.Progress);
        Assert.Equal(100, info.Progress!.ReceivedBytes);
        Assert.Equal(400, info.Progress.TotalBytes);
        Assert.Equal(50.0, info.Progress.BytesPerSec);

        gate.SetResult();
        Assert.True(await WaitUntilInstalledAsync(manager, "Hy-MT2-1.8B-Q6_K"));
        Assert.Null(manager.List().Single(i => i.Name == "Hy-MT2-1.8B-Q6_K").Progress);
    }

    private static async Task WaitUntilDownloadStopsAsync(LlmModelManager manager, string name)
    {
        var sw = Stopwatch.StartNew();
        while (manager.List().Single(i => i.Name == name).Downloading
               && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.False(manager.List().Single(i => i.Name == name).Downloading);
    }

    private static async Task<bool> WaitUntilInstalledAsync(LlmModelManager manager, string name)
    {
        var sw = Stopwatch.StartNew();
        while (!manager.List().Single(i => i.Name == name).Installed
               && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return manager.List().Single(i => i.Name == name).Installed;
    }
}
