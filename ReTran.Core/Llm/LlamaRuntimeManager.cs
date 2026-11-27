using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReTran.Core.JsonRpc;

namespace ReTran.Core.Llm;

/// <summary>
/// Terminal or active state of the latest registered runtime download attempt.
/// Trạng thái đang chạy hoặc kết thúc của lượt tải runtime mới nhất đã đăng ký.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LlmRuntimeAttemptState>))]
public enum LlmRuntimeAttemptState
{
    /// <summary>No download attempt has been registered. Chưa có lượt tải nào được đăng ký.</summary>
    None,

    /// <summary>The attempt is queued, downloading, extracting, or committing. Lượt tải đang xếp hàng, tải, giải nén hoặc commit.</summary>
    Running,

    /// <summary>The attempt committed a verified server binary. Lượt tải đã commit một server binary được xác minh.</summary>
    Succeeded,

    /// <summary>The attempt ended with an error. Lượt tải kết thúc với lỗi.</summary>
    Failed,

    /// <summary>The caller cancelled the attempt. Caller đã hủy lượt tải.</summary>
    Cancelled,
}

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
    /// The latest download attempt failed (no matching release, network error, HTTP timeout, or invalid
    /// artifact) — the wizard fails the attempt fast instead of polling its full window. Lượt tải mới
    /// nhất đã fail (không có release khớp, lỗi mạng, HTTP timeout hoặc artifact không hợp lệ) — wizard
    /// fail nhanh thay vì poll đủ cửa sổ.
    /// </summary>
    public bool DownloadFailed { get; init; }

    /// <summary>
    /// Detailed error of the current failed attempt, or <see langword="null"/>. Lỗi chi tiết của
    /// attempt đang fail, hoặc <see langword="null"/> khi attempt hiện tại không fail.
    /// </summary>
    public string? DownloadError { get; init; }

    /// <summary>
    /// A download, extraction, or same-version repair is in flight; an older installed build must not
    /// be treated as completion of this attempt. Một lượt tải/giải nén/sửa cùng phiên bản đang chạy;
    /// không được coi bản cũ là dấu hiệu lượt này đã hoàn tất.
    /// </summary>
    public bool Downloading { get; init; }

    /// <summary>Flavor of the active installation (<c>cuda</c>/<c>cpu</c>), or <see langword="null"/> for a legacy/unknown state. Flavor của bản đang active (<c>cuda</c>/<c>cpu</c>), hoặc <see langword="null"/> khi state cũ/không rõ.</summary>
    public string? Flavor { get; init; }

    /// <summary>Latest registered download attempt ID; 0 before the first download. ID lượt tải gần nhất được đăng ký; 0 trước lượt đầu.</summary>
    public long Attempt { get; init; }

    /// <summary>State of <see cref="Attempt"/>, serialized by name over JSON-RPC. Trạng thái của <see cref="Attempt"/>, được serialize theo tên qua JSON-RPC.</summary>
    public LlmRuntimeAttemptState AttemptState { get; init; }
}

/// <summary>
/// One synchronously registered runtime download and its completion task.
/// Một lượt tải runtime được đăng ký đồng bộ và task hoàn tất của nó.
/// </summary>
/// <param name="Attempt">Unique ID within this manager instance. ID duy nhất trong instance manager.</param>
/// <param name="Task">Completion of the download/extraction/commit. Task hoàn tất tải/giải nén/commit.</param>
public sealed record LlmRuntimeDownload(long Attempt, Task Task);

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
/// <param name="errorWriter">Optional failure sink supplied by the host; null discards diagnostics. Sink lỗi tùy chọn do host cung cấp; null bỏ qua diagnostic.</param>
public sealed class LlamaRuntimeManager(
    string rootDir,
    HttpClient github,
    Func<Uri, string, CancellationToken, Task>? downloader = null,
    TextWriter? errorWriter = null)
{
    /// <summary>GitHub releases endpoint scanned for Windows build assets. Endpoint releases GitHub quét để tìm asset bản Windows.</summary>
    public static readonly string ReleasesUrl =
        "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30";

    private readonly string _rootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
    private readonly HttpClient _github = github ?? throw new ArgumentNullException(nameof(github));
    private readonly TextWriter _errorWriter = errorWriter ?? TextWriter.Null;

    // Resolved at use: the default downloader reports progress, so it must be an instance method (a
    // field initializer cannot reference one). Giải quyết lúc dùng: downloader mặc định báo tiến độ
    // nên phải là instance method (field initializer không được tham chiếu instance member).
    private readonly Func<Uri, string, CancellationToken, Task>? _downloader = downloader;

    /// <summary>In-flight zip download progress keyed by asset file name (≤ 1 entry — downloads are serialized). Tiến độ tải zip đang chạy theo tên asset (≤ 1 entry — tải được tuần tự hoá).</summary>
    private readonly Dictionary<string, LlmDownloadProgress> _progress = new();
    private readonly object _progressLock = new();

    // Attempt state is registered synchronously before StartDownload returns, including queued work.
    // Trạng thái attempt được đăng ký đồng bộ trước khi StartDownload trả về, kể cả lượt đang xếp hàng.
    private readonly object _attemptLock = new();
    private long _attempt;
    private LlmRuntimeAttemptState _attemptState;
    private string? _downloadError;
    private bool _downloading;

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
        (string? tag, string? installedFlavor) = ReadState();
        string? current = installedFlavor == flavor ? tag : null;
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
    public Task<LlmRuntimeStatus> GetStatusAsync(CancellationToken ct) => GetStatusCoreAsync(null);

    /// <summary>
    /// Reads whether the selected flavor is installed, independently of another flavor with the same tag.
    /// Đọc flavor được chọn đã cài hay chưa, độc lập với flavor khác có cùng tag.
    /// </summary>
    /// <param name="flavor"><c>cuda</c> or <c>cpu</c>. <c>cuda</c> hoặc <c>cpu</c>.</param>
    /// <param name="ct">Unused; present for symmetry. Không dùng; giữ cho đồng bộ chữ ký.</param>
    /// <returns>The flavor-selected status. Trạng thái theo flavor đã chọn.</returns>
    /// <exception cref="RpcMethodException">Thrown for an unknown flavor (-32602). Ném khi flavor lạ (-32602).</exception>
    public Task<LlmRuntimeStatus> GetStatusAsync(string flavor, CancellationToken ct)
    {
        _ = FlavorPattern(flavor);
        return GetStatusCoreAsync(flavor);
    }

    private Task<LlmRuntimeStatus> GetStatusCoreAsync(string? requestedFlavor)
    {
        (string? tag, string? activeFlavor) = ReadState();
        bool flavorMatches = requestedFlavor is null || requestedFlavor == activeFlavor;
        string? serverPath = flavorMatches && tag is not null && activeFlavor is not null
            ? Path.Combine(_rootDir, activeFlavor, tag, "llama-server.exe")
            : null;
        long attempt;
        LlmRuntimeAttemptState attemptState;
        bool downloading;
        bool downloadFailed;
        string? downloadError;
        lock (_attemptLock)
        {
            attempt = _attempt;
            attemptState = _attemptState;
            downloading = _downloading;
            downloadFailed = _attemptState == LlmRuntimeAttemptState.Failed;
            downloadError = downloadFailed ? _downloadError : null;
        }
        bool installed = serverPath is not null && File.Exists(serverPath) && !downloading;
        LlmDownloadProgress? progress;
        lock (_progressLock)
        {
            progress = _progress.Values.FirstOrDefault();
        }
        return Task.FromResult(new LlmRuntimeStatus(tag, installed, serverPath)
        {
            Progress = progress,
            DownloadFailed = downloadFailed,
            DownloadError = downloadError,
            Downloading = downloading,
            Flavor = activeFlavor,
            Attempt = attempt,
            AttemptState = attemptState,
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
    /// <exception cref="InvalidDataException">Thrown when the extracted build has no final <c>llama-server.exe</c>. Khi bản giải nén không có <c>llama-server.exe</c> ở thư mục final.</exception>
    public Task DownloadAsync(string flavor, CancellationToken ct) => StartDownload(flavor, ct).Task;

    /// <summary>
    /// Registers a queued/in-flight attempt before returning, then downloads and commits its build.
    /// Đăng ký attempt đang xếp hàng/chạy trước khi trả về, rồi tải và commit bản build.
    /// </summary>
    /// <param name="flavor"><c>cuda</c> or <c>cpu</c>. <c>cuda</c> hoặc <c>cpu</c>.</param>
    /// <param name="ct">Token used to cancel the download. Token dùng để hủy tải.</param>
    /// <returns>The attempt ID and its completion task. ID attempt và task hoàn tất của nó.</returns>
    /// <exception cref="RpcMethodException">Thrown for an unknown flavor (-32602). Ném khi flavor lạ (-32602).</exception>
    /// <exception cref="InvalidDataException">The completion task faults when the extracted build has no final <c>llama-server.exe</c>. Task hoàn tất fault khi bản giải nén không có <c>llama-server.exe</c> ở thư mục final.</exception>
    public LlmRuntimeDownload StartDownload(string flavor, CancellationToken ct)
    {
        _ = FlavorPattern(flavor);
        long attempt;
        lock (_attemptLock)
        {
            attempt = ++_attempt;
            _attemptState = LlmRuntimeAttemptState.Running;
            _downloadError = null;
            _downloading = true;
        }
        return new LlmRuntimeDownload(attempt, DownloadAttemptAsync(flavor, attempt, ct));
    }

    private async Task DownloadAttemptAsync(string flavor, long attempt, CancellationToken ct)
    {
        bool gateHeld = false;
        try
        {
            await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
            gateHeld = true;
            (string tag, string mainUrl, string? cudartUrl)? release =
                await FindReleaseAsync(flavor, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No llama.cpp release with a Windows x64 asset for flavor '{flavor}'.");

            string stagingDir = Path.Combine(
                _rootDir, ".staging", $"{flavor}-{release.Value.tag}-{Guid.NewGuid():N}");
            string finalDir = Path.Combine(_rootDir, flavor, release.Value.tag);
            Directory.CreateDirectory(stagingDir);
            try
            {
                await DownloadAndExtractAsync(release.Value.mainUrl, stagingDir, ct).ConfigureAwait(false);
                if (release.Value.cudartUrl is not null)
                {
                    await DownloadAndExtractAsync(release.Value.cudartUrl, stagingDir, ct).ConfigureAwait(false);
                }
                CommitStagedDirectory(stagingDir, finalDir, release.Value.tag, flavor);
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }
            lock (_attemptLock)
            {
                if (_attempt == attempt)
                {
                    _attemptState = LlmRuntimeAttemptState.Succeeded;
                    _downloadError = null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_attemptLock)
            {
                if (_attempt == attempt)
                {
                    _attemptState = LlmRuntimeAttemptState.Cancelled;
                    _downloadError = null;
                }
            }
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient timeout also surfaces as TaskCanceledException; only the caller token means
            // intentional cancellation. HttpClient timeout cũng ném TaskCanceledException; chỉ token
            // của caller mới biểu thị hủy chủ động.
            lock (_attemptLock)
            {
                if (_attempt == attempt)
                {
                    _attemptState = LlmRuntimeAttemptState.Failed;
                    _downloadError = ex.Message;
                }
            }
            _errorWriter.WriteLine($"llm.runtime.download failed for '{flavor}': {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            // The RPC observes the background task's fault; the attempt flag lets each caller fail fast
            // without inheriting a different attempt's failure. Direct callers still see the exception.
            // RPC quan sát lỗi task nền; cờ theo attempt giúp từng caller fail nhanh mà không kế thừa
            // lỗi của attempt khác. Caller trực tiếp vẫn nhận exception như cũ.
            lock (_attemptLock)
            {
                if (_attempt == attempt)
                {
                    _attemptState = LlmRuntimeAttemptState.Failed;
                    _downloadError = ex.Message;
                }
            }
            _errorWriter.WriteLine($"llm.runtime.download failed for '{flavor}': {ex.Message}");
            throw;
        }
        finally
        {
            lock (_attemptLock)
            {
                if (_attempt == attempt) _downloading = false;
            }
            if (gateHeld) _downloadGate.Release();
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
        string zipPath = Path.Combine(targetDir, assetName);
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

    private void CommitStagedDirectory(string stagingDir, string finalDir, string tag, string flavor)
    {
        string? backupDir = null;
        bool oldMoved = false;
        bool newMoved = false;
        Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
        try
        {
            if (Directory.Exists(finalDir))
            {
                backupDir = finalDir + ".backup-" + Guid.NewGuid().ToString("N");
                Directory.Move(finalDir, backupDir);
                oldMoved = true;
            }
            Directory.Move(stagingDir, finalDir);
            newMoved = true;
            string serverPath = Path.Combine(finalDir, "llama-server.exe");
            if (!File.Exists(serverPath))
            {
                throw new InvalidDataException(
                    $"The downloaded llama.cpp runtime does not contain llama-server.exe: {finalDir}");
            }
            WriteState(tag, flavor);
        }
        catch
        {
            if (newMoved && Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
            if (oldMoved && backupDir is not null && Directory.Exists(backupDir) && !Directory.Exists(finalDir))
            {
                Directory.Move(backupDir, finalDir);
            }
            throw;
        }
        if (backupDir is not null) TryDeleteDirectory(backupDir);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Orphan staging/backup data is never active; leave it for the next manual cleanup.
            // Dữ liệu staging/backup mồ côi không bao giờ active; để user dọn thủ công sau.
        }
    }

    private async Task<(string Tag, string MainUrl, string? CudartUrl)?> FindReleaseAsync(
        string flavor, CancellationToken ct)
    {
        _ = FlavorPattern(flavor);
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

                (string Name, string Url)? main = null;
                var cudartCandidates = new List<(string Name, string Url)>();
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
                    if (IsMainRuntimeAsset(name, flavor)
                        && (main is null || (flavor == "cuda" && CudaVersion(name) == "12.4")))
                    {
                        main = (name, url);
                    }
                    else if (flavor == "cuda"
                        && name.StartsWith("cudart-llama-bin-win-cuda-", StringComparison.Ordinal)
                        && name.EndsWith("-x64.zip", StringComparison.Ordinal))
                    {
                        cudartCandidates.Add((name, url));
                    }
                }

                if (main is not null)
                {
                    string? cudartUrl = null;
                    if (flavor == "cuda")
                    {
                        string? version = CudaVersion(main.Value.Name);
                        cudartUrl = version is null
                            ? cudartCandidates.FirstOrDefault().Url
                            : cudartCandidates.FirstOrDefault(a =>
                                a.Name == $"cudart-llama-bin-win-cuda-{version}-x64.zip").Url;
                        if (version is not null && cudartUrl is null) continue;
                    }
                    return (tagNode.GetString()!, main.Value.Url, cudartUrl);
                }
            }
        }

        return null;
    }

    private static bool IsMainRuntimeAsset(string name, string flavor)
    {
        if (!name.StartsWith("llama-b", StringComparison.Ordinal)) return false;
        if (flavor == "cpu")
        {
            return name.EndsWith("-bin-win-cpu-x64.zip", StringComparison.Ordinal)
                || name.EndsWith("-bin-win-avx2-x64.zip", StringComparison.Ordinal);
        }
        return name.Contains("-bin-win-cuda-", StringComparison.Ordinal)
            && name.EndsWith("-x64.zip", StringComparison.Ordinal);
    }

    private static string? CudaVersion(string name)
    {
        const string marker = "-bin-win-cuda-";
        const string suffix = "-x64.zip";
        int start = name.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        int end = name.IndexOf(suffix, start, StringComparison.Ordinal);
        return end > start ? name[start..end] : null;
    }

    private string StatePath => Path.Combine(_rootDir, "runtime.json");

    private (string? Tag, string? Flavor) ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return (null, null);
            JsonNode? root = JsonNode.Parse(File.ReadAllText(StatePath));
            return (root?["tag"]?.GetValue<string>(), root?["flavor"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return (null, null);
        }
    }

    private void WriteState(string tag, string flavor)
    {
        Directory.CreateDirectory(_rootDir);
        File.WriteAllText(StatePath, new JsonObject
        {
            ["tag"] = tag,
            ["flavor"] = flavor,
        }.ToJsonString());
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
