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

        (string? current, string? latest) = await manager.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(current);
        Assert.Equal("b190", latest); // b200 chỉ có CPU — phải bỏ qua. b200 is CPU-only and must be skipped.
    }

    [Fact]
    public async Task Check_NoMatchingAsset_ReturnsNullLatest()
    {
        const string cpuOnly = """
            [ { "tag_name": "b210", "assets": [ { "name": "llama-b210-bin-win-cpu-x64.zip", "browser_download_url": "http://x/cpu210.zip" } ] } ]
            """;
        (LlamaRuntimeManager manager, _) = Create(cpuOnly);

        (_, string? latest) = await manager.CheckAsync(TestContext.Current.CancellationToken);

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
        Assert.True(File.Exists(Path.Combine(_dir, "b190", "extra.dll")));

        (_, string? latest) = await manager.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal("b190", latest); // Current == Latest sau khi tải mới nhất — nothing newer to fetch.
    }

    [Fact]
    public async Task Download_Cpu_UsesAvx2Asset()
    {
        (LlamaRuntimeManager manager, RecordingZipDownloader downloader) = Create();

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
