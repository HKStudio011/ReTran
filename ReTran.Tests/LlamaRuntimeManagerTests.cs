using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ReTran.Core.JsonRpc;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlamaRuntimeManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"retran-runtime-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class GhHandler(string json) : HttpMessageHandler
    {
        public string? LastUrl;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private const string ReleasesJson = """
        [
          {
            "tag_name": "b200",
            "assets": [ { "name": "llama-b200-bin-win-cpu-x64.zip", "browser_download_url": "http://x/cpu200.zip" } ]
          },
          {
            "tag_name": "b190",
            "assets": [
              { "name": "llama-b190-bin-win-cuda-x64.zip", "browser_download_url": "http://x/cuda190.zip" },
              { "name": "cudart-llama-bin-win-cuda-12.4-x64.zip", "browser_download_url": "http://x/cudart190.zip" },
              { "name": "llama-b190-bin-win-avx2-x64.zip", "browser_download_url": "http://x/avx2190.zip" }
            ]
          }
        ]
        """;

    private const string LegacyAvx2ReleaseJson = """
        [
          {
            "tag_name": "b190",
            "assets": [
              { "name": "llama-b190-bin-win-avx2-x64.zip", "browser_download_url": "http://x/avx2190.zip" }
            ]
          }
        ]
        """;

    private const string VersionedReleasesJson = """
        [
          {
            "tag_name": "b12503",
            "assets": [
              { "name": "cudart-llama-b12503-bin-ubuntu-cuda-12.8-x64.tar.gz", "browser_download_url": "http://x/ubuntu-cudart.tar.gz" },
              { "name": "cudart-llama-bin-win-cuda-12.4-x64.zip", "browser_download_url": "http://x/cudart124.zip" },
              { "name": "cudart-llama-bin-win-cuda-13.5-x64.zip", "browser_download_url": "http://x/cudart135.zip" },
              { "name": "llama-b12503-bin-win-cpu-x64.zip", "browser_download_url": "http://x/cpu12503.zip" },
              { "name": "llama-b12503-bin-win-cuda-12.4-x64.zip", "browser_download_url": "http://x/cuda124.zip" },
              { "name": "llama-b12503-bin-win-cuda-13.5-x64.zip", "browser_download_url": "http://x/cuda135.zip" }
            ]
          }
        ]
        """;

    [Fact]
    public async Task Download_Cuda_PrefersCuda124AndMatchingWindowsCudart()
    {
        (LlamaRuntimeManager manager, RecordingZipDownloader downloader) = Create(VersionedReleasesJson);

        await manager.DownloadAsync("cuda", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "http://x/cuda124.zip", "http://x/cudart124.zip" },
            downloader.Uris.Select(u => u.ToString()));
    }

    [Fact]
    public async Task Status_CarriesProgress_WhileDownloading_AndClearsAfter()
    {
        // Tiến độ zip runtime (wizard vòng 2 §9): status mang `progress` khi đang tải, mất khi tải xong
        // (DownloadAndExtractAsync xoá entry sau khi downloader trả về, trước khi giải nén).
        var downloader = new RecordingZipDownloader();
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)), downloader.DownloadAsync);

        Assert.Null((await manager.GetStatusAsync(CancellationToken.None)).Progress);

        manager.ReportDownloadProgress("cuda190.zip", 100, 400, 50.0); // đúng tên asset mà DownloadAsync("cuda") sẽ tải
        LlmRuntimeStatus status = await manager.GetStatusAsync(CancellationToken.None);
        Assert.NotNull(status.Progress);
        Assert.Equal(100, status.Progress!.ReceivedBytes);
        Assert.Equal(400, status.Progress.TotalBytes);
        Assert.Equal(50.0, status.Progress.BytesPerSec);

        // Lượt tải thật (fake downloader trả ngay) → entry tiến độ bị xoá.
        await manager.DownloadAsync("cuda", CancellationToken.None);
        Assert.Null((await manager.GetStatusAsync(CancellationToken.None)).Progress);
    }

    private sealed class RecordingZipDownloader
    {
        public List<Uri> Uris { get; } = new();

        public Task DownloadAsync(Uri uri, string target, CancellationToken ct)
        {
            Uris.Add(uri);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream fs = File.Create(target);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            string entry = uri.ToString().Contains("cudart", StringComparison.Ordinal)
                ? "extra.dll"
                : "llama-server.exe";
            zip.CreateEntry(entry);
            return Task.CompletedTask;
        }
    }

    private static void WriteRuntimeZip(Uri uri, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using FileStream fs = File.Create(target);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        zip.CreateEntry(uri.ToString().Contains("cudart", StringComparison.Ordinal)
            ? "extra.dll"
            : "llama-server.exe");
    }

    private (LlamaRuntimeManager manager, RecordingZipDownloader downloader) Create(string json = ReleasesJson)
    {
        var downloader = new RecordingZipDownloader();
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(json)), downloader.DownloadAsync);
        return (manager, downloader);
    }

    [Fact]
    public async Task Check_PicksNewestReleaseWithCudaAsset()
    {
        (LlamaRuntimeManager manager, _) = Create();

        (string? current, string? latest) = await manager.CheckAsync("cuda", TestContext.Current.CancellationToken);

        Assert.Null(current);
        Assert.Equal("b190", latest); // b200 chỉ có CPU — phải bỏ qua. b200 is CPU-only and must be skipped.
    }

    [Fact]
    public async Task Check_Cpu_PicksNewestReleaseWithCurrentCpuAsset()
    {
        (LlamaRuntimeManager manager, _) = Create();

        (string? current, string? latest) = await manager.CheckAsync("cpu", TestContext.Current.CancellationToken);

        Assert.Null(current);
        Assert.Equal("b200", latest); // current upstream uses win-cpu-x64, not win-avx2-x64
    }

    [Fact]
    public async Task Check_NoMatchingAsset_ReturnsNullLatest()
    {
        const string cpuOnly = """
            [ { "tag_name": "b210", "assets": [ { "name": "llama-b210-bin-win-cpu-x64.zip", "browser_download_url": "http://x/cpu210.zip" } ] } ]
            """;
        (LlamaRuntimeManager manager, _) = Create(cpuOnly);

        (_, string? latest) = await manager.CheckAsync("cuda", TestContext.Current.CancellationToken);

        Assert.Null(latest);
    }

    [Fact]
    public async Task GetStatus_BeforeDownload_NotInstalled()
    {
        (LlamaRuntimeManager manager, _) = Create();

        LlmRuntimeStatus status = await manager.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Null(status.Tag);
        Assert.False(status.Installed);
        Assert.Null(status.ServerPath);
    }

    [Fact]
    public async Task Download_ExtractsZip_WritesRuntimeJson()
    {
        (LlamaRuntimeManager manager, RecordingZipDownloader downloader) = Create();

        await manager.DownloadAsync("cuda", TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "http://x/cuda190.zip", "http://x/cudart190.zip" },
            downloader.Uris.Select(u => u.ToString()));

        LlmRuntimeStatus status = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("b190", status.Tag);
        Assert.True(status.Installed);
        Assert.True(File.Exists(status.ServerPath));
        Assert.True(File.Exists(Path.Combine(_dir, "cuda", "b190", "extra.dll")));
        Assert.Equal("cuda", status.Flavor);

        (_, string? latest) = await manager.CheckAsync("cuda", TestContext.Current.CancellationToken);
        Assert.Equal("b190", latest); // Current == Latest sau khi tải mới nhất — nothing newer to fetch.
    }

    [Fact]
    public async Task CheckAndStatus_SameTagDifferentFlavor_RequiresReinstall()
    {
        (LlamaRuntimeManager manager, _) = Create();
        await manager.DownloadAsync("cuda", TestContext.Current.CancellationToken);

        (string? current, string? latest) = await manager.CheckAsync("cpu", TestContext.Current.CancellationToken);
        LlmRuntimeStatus cpuStatus = await manager.GetStatusAsync("cpu", TestContext.Current.CancellationToken);
        LlmRuntimeStatus cudaStatus = await manager.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Null(current); // b190 CUDA does not satisfy the CPU flavor, even though the tags match
        Assert.Equal("b200", latest);
        Assert.False(cpuStatus.Installed);
        Assert.True(cudaStatus.Installed);
        Assert.Equal("cuda", cudaStatus.Flavor);
        Assert.EndsWith(Path.Combine("cuda", "b190", "llama-server.exe"), cudaStatus.ServerPath);
    }

    [Fact]
    public async Task Download_Cpu_UsesAvx2Asset()
    {
        (LlamaRuntimeManager manager, RecordingZipDownloader downloader) = Create(LegacyAvx2ReleaseJson);

        await manager.DownloadAsync("cpu", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "http://x/avx2190.zip" }, downloader.Uris.Select(u => u.ToString()));
    }

    [Fact]
    public async Task Download_UnknownFlavor_InvalidParams()
    {
        (LlamaRuntimeManager manager, _) = Create();

        await Assert.ThrowsAsync<RpcMethodException>(() =>
            manager.DownloadAsync("vulkan", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Status_UpdateInFlight_HidesPreviousInstallUntilComplete()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool block = false;
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            async (uri, target, ct) =>
            {
                if (block)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                WriteRuntimeZip(uri, target);
            });
        await manager.DownloadAsync("cpu", TestContext.Current.CancellationToken);
        block = true;

        Task update = manager.DownloadAsync("cpu", TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            LlmRuntimeStatus status = await manager.GetStatusAsync(TestContext.Current.CancellationToken);

            Assert.True(status.Downloading);
            Assert.False(status.Installed); // the old b200 install must not make this repair look complete
            Assert.Equal("b200", status.Tag);
        }
        finally
        {
            release.TrySetResult();
        }
        await update;

        LlmRuntimeStatus completed = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(completed.Downloading);
        Assert.True(completed.Installed);
        Assert.False(completed.DownloadFailed);
    }

    [Fact]
    public async Task Status_UpdateFailure_KeepsPreviousInstallAndReportsFailure()
    {
        bool fail = false;
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            (uri, target, ct) =>
            {
                if (fail) throw new HttpRequestException("network down");
                WriteRuntimeZip(uri, target);
                return Task.CompletedTask;
            });
        await manager.DownloadAsync("cpu", TestContext.Current.CancellationToken);
        fail = true;

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            manager.DownloadAsync("cpu", TestContext.Current.CancellationToken));

        LlmRuntimeStatus status = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("network down", error.Message);
        Assert.False(status.Downloading);
        Assert.True(status.Installed); // the previous runtime remains usable after the failed update
        Assert.True(status.DownloadFailed); // ...but the update itself must not look successful
        Assert.Equal("network down", status.DownloadError);
    }

    [Fact]
    public async Task StartDownload_RegistersAttemptSynchronously_BeforeReturning()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            async (uri, target, ct) =>
            {
                calls++;
                if (calls > 1) await gate.Task.WaitAsync(ct);
                WriteRuntimeZip(uri, target);
            });
        await manager.DownloadAsync("cpu", TestContext.Current.CancellationToken);

        LlmRuntimeDownload download = manager.StartDownload("cpu", TestContext.Current.CancellationToken);
        LlmRuntimeStatus status = await manager.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(download.Attempt, status.Attempt);
        Assert.True(status.Downloading); // accepted → immediate poll cannot mistake the old install for completion
        Assert.False(status.Installed);
        Assert.False(status.DownloadFailed);
        gate.SetResult();
        await download.Task;
    }

    [Fact]
    public async Task QueuedAttempt_PreviousFailure_DoesNotFailOrPoisonNextSuccess()
    {
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            async (uri, target, ct) =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 2)
                {
                    enteredA.TrySetResult();
                    await releaseA.Task.WaitAsync(ct);
                    throw new HttpRequestException("attempt A failed");
                }
                if (call == 3)
                {
                    enteredB.TrySetResult();
                    await releaseB.Task.WaitAsync(ct);
                }
                WriteRuntimeZip(uri, target);
            });
        await manager.DownloadAsync("cpu", TestContext.Current.CancellationToken); // initial install, call 1

        LlmRuntimeDownload a = manager.StartDownload("cpu", TestContext.Current.CancellationToken);
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        LlmRuntimeDownload b = manager.StartDownload("cpu", TestContext.Current.CancellationToken);
        Assert.NotEqual(a.Attempt, b.Attempt);
        releaseA.SetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => a.Task);

        LlmRuntimeStatus queued = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(b.Attempt, queued.Attempt);
        Assert.True(queued.Downloading);
        Assert.False(queued.DownloadFailed); // failure belongs to A, not queued B

        releaseB.SetResult();
        await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await b.Task;
        LlmRuntimeStatus completed = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(b.Attempt, completed.Attempt);
        Assert.True(completed.Installed);
        Assert.False(completed.Downloading);
        Assert.False(completed.DownloadFailed);
    }

    [Fact]
    public async Task StartDownload_CancelledWhileQueued_ClearsDownloading()
    {
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            async (uri, target, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    enteredA.TrySetResult();
                    await releaseA.Task.WaitAsync(ct);
                }
                WriteRuntimeZip(uri, target);
            });

        LlmRuntimeDownload a = manager.StartDownload("cpu", TestContext.Current.CancellationToken);
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var cancelB = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        LlmRuntimeDownload b = manager.StartDownload("cpu", cancelB.Token);
        cancelB.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Task);
        releaseA.SetResult();
        await a.Task;

        LlmRuntimeStatus status = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(b.Attempt, status.Attempt);
        Assert.False(status.Downloading);
        Assert.False(status.DownloadFailed);
        Assert.True(status.Installed); // A completed; B was cancelled, not failed
    }

    [Fact]
    public async Task Download_ConcurrentCalls_SerializeExtraction()
    {
        // Reviewer finding #1: hai lượt tải chồng nhau từng giải nén đè cùng thư mục <tag>/ và ghi
        // runtime.json giao nhau. Khoá tuần tự phải giữ tối đa MỘT lượt tải/giải nén tại một thời
        // điểm; lượt thứ hai chờ lượt đầu xong.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maxActive = 0;
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            async (uri, target, ct) =>
            {
                int now = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maxActive, Math.Max(maxActive, now));
                await release.Task;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using FileStream fs = File.Create(target);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                zip.CreateEntry(uri.ToString().Contains("cudart", StringComparison.Ordinal)
                    ? "extra.dll"
                    : "llama-server.exe");
                Interlocked.Decrement(ref active);
            });

        Task first = manager.DownloadAsync("cpu", TestContext.Current.CancellationToken);
        Task second = manager.DownloadAsync("cpu", TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Download_FailureAfterMainExtraction_PreservesPreviousInstall()
    {
        bool failCudart = false;
        bool replacement = false;
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            (uri, target, ct) =>
            {
                if (failCudart && uri.ToString().Contains("cudart", StringComparison.Ordinal))
                {
                    throw new IOException("cudart failed");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using FileStream fs = File.Create(target);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                bool isCudart = uri.ToString().Contains("cudart", StringComparison.Ordinal);
                ZipArchiveEntry entry = zip.CreateEntry(isCudart ? "extra.dll" : "llama-server.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(replacement
                    ? isCudart ? "new-cudart" : "new-server"
                    : isCudart ? "old-cudart" : "old-server");
                return Task.CompletedTask;
            });
        await manager.DownloadAsync("cuda", TestContext.Current.CancellationToken);
        replacement = true;
        failCudart = true;

        await Assert.ThrowsAsync<IOException>(() =>
            manager.DownloadAsync("cuda", TestContext.Current.CancellationToken));

        string server = Path.Combine(_dir, "cuda", "b190", "llama-server.exe");
        string cudart = Path.Combine(_dir, "cuda", "b190", "extra.dll");
        Assert.Equal("old-server", File.ReadAllText(server));
        Assert.Equal("old-cudart", File.ReadAllText(cudart));
        LlmRuntimeStatus status = await manager.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.Installed);
        Assert.True(status.DownloadFailed);
    }

    [Fact]
    public async Task Download_TraversalZipEntry_Throws_AndWritesNothingOutside()
    {
        // Pin lưới an toàn supply-chain: entry "../evil.txt" phải bị BCL chặn khi giải nén (ném
        // IOException, không ghi file nào thoát thư mục đích) — và vì thế runtime.json cũng không
        // được ghi (tag chưa được coi là cài hợp lệ).
        var manager = new LlamaRuntimeManager(
            _dir, new HttpClient(new GhHandler(ReleasesJson)),
            (uri, target, ct) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using FileStream fs = File.Create(target);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                zip.CreateEntry("../evil.txt");
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IOException>(() =>
            manager.DownloadAsync("cpu", TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "runtime.json")));
    }
}
