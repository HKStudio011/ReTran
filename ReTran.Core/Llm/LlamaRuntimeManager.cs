using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;

namespace ReTran.Core.Llm;

/// <summary>
/// Installed state of the llama.cpp runtime: the tag of the extracted build, whether the server
/// binary is on disk, and its path. Trạng thái runtime llama.cpp đã cài: tag của bản đã giải nén,
/// server binary có trên đĩa không, và đường dẫn của nó.
/// </summary>
/// <param name="Tag">Extracted build tag, e.g. <c>b12110</c>, or <see langword="null"/>. Tag bản đã giải nén, hoặc <see langword="null"/>.</param>
/// <param name="Installed">The <c>llama-server.exe</c> of that tag exists. <c>llama-server.exe</c> của tag đó tồn tại.</param>
/// <param name="ServerPath">Path to <c>llama-server.exe</c> (may not exist yet). Đường dẫn <c>llama-server.exe</c> (có thể chưa tồn tại).</param>
public sealed record LlmRuntimeStatus(string? Tag, bool Installed, string? ServerPath)
{
    /// <summary>
    /// In-flight zip download progress (wizard vòng 2 §9 — % + ETA); <see langword="null"/> unless a
    /// download is running and reporting. Tiến độ tải zip đang chạy; <see langword="null"/> trừ khi
    /// có lượt tải đang báo tiến độ.
    /// </summary>
    public LlmDownloadProgress? Progress { get; init; }

    /// <summary>
    /// The last download attempt failed permanently (no matching release / dead network) — the wizard
    /// fails the attempt fast instead of polling its full window. Lượt tải gần nhất fail vĩnh viễn
    /// (không có release khớp / mạng chết) — wizard fail nhanh thay vì poll đủ cửa sổ.
    /// </summary>
    public bool DownloadFailed { get; init; }

    /// <summary>
    /// A download, extraction, or same-version repair is in flight; an older installed build must not
    /// be treated as completion of this attempt. Một lượt tải/giải nén/sửa cùng phiên bản đang chạy;
    /// không được coi bản cũ là dấu hiệu lượt này đã hoàn tất.
    /// </summary>
    public bool Downloading { get; init; }
}

/// <summary>
/// Manages the llama.cpp runtime: checks the newest Windows build that has a matching asset on
/// the ggml-org GitHub releases, downloads and extracts it (CUDA build plus its bundled cudart
/// when the release ships one; an AVX2 CPU build as the fallback flavor), and records the
/// extracted tag in <c>runtime.json</c>.
/// Quản lý runtime llama.cpp: kiểm tra bản Windows mới nhất có asset phù hợp trên GitHub releases
/// của ggml-org, tải và giải nén (bản CUDA kèm cudart nếu release có; bản CPU AVX2 làm phương án
/// dự phòng), và ghi tag đã giải nén vào <c>runtime.json</c>.
/// </summary>
/// <param name="rootDir">Directory holding <c>&lt;tag&gt;/</c> build folders and <c>runtime.json</c>. Thư mục chứa các bản build theo tag và <c>runtime.json</c>.</param>
/// <param name="github">HTTP client for the GitHub API and asset downloads. HTTP client cho GitHub API và tải asset.</param>
/// <param name="downloader">Optional (uri, targetPath) downloader; tests inject a fake. Hàm tải tuỳ chọn (uri, đường dẫn đích); test inject bản giả.</param>
public sealed class LlamaRuntimeManager(
    string rootDir,
    HttpClient github,
    Func<Uri, string, CancellationToken, Task>? downloader = null)
{
    /// <summary>GitHub releases endpoint scanned for Windows build assets. Endpoint releases GitHub quét để tìm asset bản Windows.</summary>
    public static readonly string ReleasesUrl =
        "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30";

    private readonly string _rootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
    private readonly HttpClient _github = github ?? throw new ArgumentNullException(nameof(github));

    // Resolved at use: the default downloader reports progress, so it must be an instance method (a
    // field initializer cannot reference one). Giải quyết lúc dùng: downloader mặc định báo tiến độ
    // nên phải là instance method (field initializer không được tham chiếu instance member).
    private readonly Func<Uri, string, CancellationToken, Task>? _downloader = downloader;

    /// <summary>In-flight zip download progress keyed by asset file name (≤ 1 entry — downloads are serialized). Tiến độ tải zip đang chạy theo tên asset (≤ 1 entry — tải được tuần tự hoá).</summary>
    private readonly Dictionary<string, LlmDownloadProgress> _progress = new();
    private readonly object _progressLock = new();

    /// <summary>Set when the last download attempt failed permanently (status reports it so the wizard fails fast). Bật khi lượt tải gần nhất fail vĩnh viễn (status báo để wizard fail nhanh).</summary>
    private volatile bool _downloadFailed;

    /// <summary>True from acquisition of the download gate until the attempt finishes. True từ lúc lấy được khóa tải tới khi lượt tải kết thúc.</summary>
    private volatile bool _downloading;

    // One download at a time: two overlapping llm.runtime.download calls (setup + Settings, or a
    // double fire) would otherwise extract into the same <tag>/ directory and interleave the
    // runtime.json write. The second caller waits for the first, then runs its own download.
    // Mỗi lần tải một lượt: hai lời gọi llm.runtime.download chồng nhau (setup + Settings, hoặc
    // bắn đúp) nếu không sẽ giải nén vào cùng thư mục <tag>/ và ghi runtime.json xen kẽ. Nơi gọi
    // thứ hai chờ nơi đầu xong rồi mới chạy lượt tải của mình.
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    /// <summary>
    /// Reports the installed tag and the newest available tag for the selected flavor.
    /// Cho biết tag đã cài và tag mới nhất khả dụng cho flavor đã chọn.
    /// </summary>
    /// <param name="flavor"><c>cuda</c> or <c>cpu</c>. <c>cuda</c> hoặc <c>cpu</c>.</param>
    /// <param name="ct">Token used to cancel the check. Token dùng để hủy kiểm tra.</param>
    /// <returns>(current installed tag, newest matching release tag). (tag đã cài, tag release mới nhất khớp).</returns>
    /// <exception cref="RpcMethodException">Thrown for an unknown flavor (-32602). Ném khi flavor lạ (-32602).</exception>
    public async Task<(string? Current, string? Latest)> CheckAsync(string flavor, CancellationToken ct)
    {
        _ = FlavorPattern(flavor);
        string? current = ReadTag();
        (string Tag, string MainUrl, string? CudartUrl)? latest =
            await FindReleaseAsync(flavor, ct).ConfigureAwait(false);
        return (current, latest?.Tag);
    }

    /// <summary>
    /// Reads the installed runtime state from <c>runtime.json</c> and the filesystem.
    /// Đọc trạng thái runtime đã cài từ <c>runtime.json</c> và filesystem.
    /// </summary>
    /// <param name="ct">Unused; present for symmetry. Không dùng; giữ cho đồng bộ chữ ký.</param>
    /// <returns>The current status. Trạng thái hiện tại.</returns>
    public Task<LlmRuntimeStatus> GetStatusAsync(CancellationToken ct)
    {
        string? tag = ReadTag();
        string? serverPath = tag is null ? null : Path.Combine(_rootDir, tag, "llama-server.exe");
        bool installed = serverPath is not null && File.Exists(serverPath) && !_downloading;
        LlmDownloadProgress? progress;
        lock (_progressLock)
        {
            progress = _progress.Values.FirstOrDefault();
        }
        return Task.FromResult(new LlmRuntimeStatus(tag, installed, serverPath)
        {
            Progress = progress,
            DownloadFailed = _downloadFailed,
            Downloading = _downloading,
        });
    }

    /// <summary>
    /// Reports byte-level progress of the in-flight runtime zip download (called by the default
    /// downloader per chunk; tests call it directly on a blocked fake).
    /// Báo tiến độ theo byte của lượt tải zip runtime đang chạy (default downloader gọi theo chunk;
    /// test gọi trực tiếp trên fake đang chặn).
    /// </summary>
    /// <param name="assetName">Asset file name being downloaded (progress key). Tên file asset đang tải (khóa tiến độ).</param>
    /// <param name="receivedBytes">Bytes written so far. Số byte đã ghi.</param>
    /// <param name="totalBytes">Total bytes from content-length, or 0 when unknown. Tổng byte theo content-length, hoặc 0 khi chưa biết.</param>
    /// <param name="bytesPerSec">Average speed in bytes/second, or <see langword="null"/>. Tốc độ trung bình (byte/giây), hoặc <see langword="null"/>.</param>
    internal void ReportDownloadProgress(string assetName, long receivedBytes, long totalBytes, double? bytesPerSec)
    {
        lock (_progressLock)
        {
            _progress[assetName] = new LlmDownloadProgress(receivedBytes, totalBytes, bytesPerSec);
        }
    }

    /// <summary>
    /// Downloads and extracts the newest matching build for <paramref name="flavor"/>. Calls are
    /// serialized: a second caller waits until the in-flight download finishes, so extraction into
    /// <c>&lt;tag&gt;/</c> and the <c>runtime.json</c> write never interleave.
    /// Tải và giải nén bản build khớp mới nhất cho <paramref name="flavor"/>. Các lời gọi được tuần
    /// tự hoá: nơi gọi thứ hai chờ lượt tải đang chạy xong, nên giải nén vào <c>&lt;tag&gt;/</c> và
    /// ghi <c>runtime.json</c> không bao giờ xen kẽ nhau.
    /// </summary>
    /// <param name="flavor"><c>cuda</c> or <c>cpu</c>. <c>cuda</c> hoặc <c>cpu</c>.</param>
    /// <param name="ct">Token used to cancel the download. Token dùng để hủy tải.</param>
    /// <exception cref="RpcMethodException">Thrown for an unknown flavor (-32602). Khi flavor lạ (-32602).</exception>
    /// <exception cref="InvalidOperationException">Thrown when no release carries a matching asset. Khi không release nào có asset khớp.</exception>
    public async Task DownloadAsync(string flavor, CancellationToken ct)
    {
        _ = FlavorPattern(flavor); // validate before queueing / kiểm tra flavor trước khi xếp hàng
        _downloadFailed = false; // new attempt clears the previous failure / lượt mới xoá fail cũ

        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        _downloading = true;
        try
        {
            (string tag, string mainUrl, string? cudartUrl)? release =
                await FindReleaseAsync(flavor, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No llama.cpp release with a Windows asset matching '{FlavorPattern(flavor)}' was found.");

            string targetDir = Path.Combine(_rootDir, release.Value.tag);
            Directory.CreateDirectory(targetDir);

            await DownloadAndExtractAsync(release.Value.mainUrl, targetDir, ct).ConfigureAwait(false);
            if (release.Value.cudartUrl is not null)
            {
                await DownloadAndExtractAsync(release.Value.cudartUrl, targetDir, ct).ConfigureAwait(false);
            }

            WriteTag(release.Value.tag);
        }
        catch (OperationCanceledException)
        {
            throw; // user/timeout cancellation is surfaced by the poll window, not the flag / hủy do cửa sổ poll, không phải flag
        }
        catch (Exception ex)
        {
            // The RPC caller is fire-and-forget (Task.Run) — without this flag a permanent failure
            // (no matching release, dead network) would leave the wizard polling its full 60-minute
            // window ×4 attempts (BA review 2026-11-17). The exception is re-thrown so direct callers
            // (tests, zip-slip guard) still see it; the flag survives for the polling wizard.
            // Caller RPC là fire-and-forget (Task.Run) — không có flag này, fail vĩnh viễn (không có
            // release khớp, mạng chết) khiến wizard poll đủ cửa sổ 60 phút ×4 lượt (review BA
            // 2026-11-17). Exception được ném lại để caller trực tiếp (test, guard zip-slip) vẫn thấy;
            // flag giữ lại cho wizard đang poll.
            _downloadFailed = true;
            Console.Error.WriteLine($"llm.runtime.download failed for '{flavor}': {ex.Message}");
            throw;
        }
        finally
        {
            _downloading = false;
            _downloadGate.Release();
        }
    }

    private static string FlavorPattern(string flavor) => flavor switch
    {
        "cuda" => "-bin-win-cuda-x64.zip",
        "cpu" => "-bin-win-avx2-x64.zip",
        _ => throw new RpcMethodException(RpcErrorCodes.InvalidParams, $"unknown runtime flavor: {flavor}"),
    };

    private async Task DownloadAndExtractAsync(string url, string targetDir, CancellationToken ct)
    {
        var uri = new Uri(url);
        string assetName = Path.GetFileName(uri.LocalPath);
        string zipPath = Path.Combine(_rootDir, assetName);
        try
        {
            Func<Uri, string, CancellationToken, Task> download = _downloader ?? DefaultDownloadAsync;
            await download(uri, zipPath, ct).ConfigureAwait(false);
            // Download finished — the progress entry goes away before the (indeterminate) extraction.
            // Tải xong — entry tiến độ biến mất trước khi (giải nén không đo được).
            lock (_progressLock)
            {
                _progress.Remove(assetName);
            }
            ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
        }
        finally
        {
            lock (_progressLock)
            {
                _progress.Remove(assetName);
            }
            try { File.Delete(zipPath); } catch (IOException) { }
        }
    }

    private async Task<(string Tag, string MainUrl, string? CudartUrl)?> FindReleaseAsync(
        string flavor, CancellationToken ct)
    {
        string pattern = FlavorPattern(flavor);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        // GitHub API's documented preference for release lookups; the browser-like User-Agent is
        // already the client default (a UA-less client is answered 403).
        // Sở thích ghi rõ của GitHub API cho tra cứu release; User-Agent giống trình duyệt đã là
        // mặc định của client (client thiếu UA bị trả 403).
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _github
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new FormatException("GitHub releases response is not valid JSON.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("GitHub releases response is not an array.");
            }

            foreach (JsonElement release in doc.RootElement.EnumerateArray())
            {
                if (!release.TryGetProperty("tag_name", out JsonElement tagNode)
                    || tagNode.ValueKind != JsonValueKind.String
                    || !release.TryGetProperty("assets", out JsonElement assets)
                    || assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                string? mainUrl = null;
                string? cudartUrl = null;
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out JsonElement nameNode)
                        || nameNode.ValueKind != JsonValueKind.String
                        || !asset.TryGetProperty("browser_download_url", out JsonElement urlNode)
                        || urlNode.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string name = nameNode.GetString()!;
                    string url = urlNode.GetString()!;
                    if (mainUrl is null && name.StartsWith("llama-b", StringComparison.Ordinal)
                        && name.EndsWith(pattern, StringComparison.Ordinal))
                    {
                        mainUrl = url;
                    }
                    else if (flavor == "cuda" && cudartUrl is null
                        && name.StartsWith("cudart-llama-bin", StringComparison.Ordinal)
                        && name.Contains("cuda", StringComparison.Ordinal))
                    {
                        cudartUrl = url;
                    }
                }

                if (mainUrl is not null)
                {
                    return (tagNode.GetString()!, mainUrl, cudartUrl);
                }
            }
        }

        return null;
    }

    private string StatePath => Path.Combine(_rootDir, "runtime.json");

    private string? ReadTag()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            JsonNode? root = JsonNode.Parse(File.ReadAllText(StatePath));
            return root?["tag"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private void WriteTag(string tag)
    {
        Directory.CreateDirectory(_rootDir);
        File.WriteAllText(StatePath, new JsonObject { ["tag"] = tag }.ToJsonString());
    }

    private async Task DefaultDownloadAsync(Uri uri, string target, CancellationToken ct)
    {
        string assetName = Path.GetFileName(uri.LocalPath);
        using HttpResponseMessage response =
            await SharedClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using FileStream destination = File.Create(target);

        // Read in chunks so the wizard can show percent and ETA (same pattern as the GGUF downloads).
        // Đọc theo chunk để wizard hiện được phần trăm và ETA (cùng kiểu với các lượt tải GGUF).
        byte[] buffer = new byte[81920];
        long received = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ReportDownloadProgress(assetName, 0, total, null);
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            double seconds = stopwatch.Elapsed.TotalSeconds;
            ReportDownloadProgress(assetName, received, total, seconds > 0.5 ? received / seconds : null);
        }
    }

    private static readonly HttpClient SharedClient = HttpUa.CreateClient();
}
