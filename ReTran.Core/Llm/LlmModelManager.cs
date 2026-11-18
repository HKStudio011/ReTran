using System.Text.Json;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;

namespace ReTran.Core.Llm;

/// <summary>
/// Snapshot of one catalog model: how big, installed on disk, currently active, downloading now.
/// Ảnh chụp một model trong catalog: kích thước, đã cài trên đĩa, đang dùng, đang tải.
/// </summary>
/// <param name="Name">Catalog name. Tên catalog.</param>
/// <param name="Quant">Quantization label. Nhãn lượng tử hoá.</param>
/// <param name="SizeClass"><c>1.8B</c> or <c>7B</c>. Lớp kích thước.</param>
/// <param name="SizeBytes">Approximate size. Kích thước xấp xỉ.</param>
/// <param name="Installed">Final file exists and no download is pending. File hoàn chỉnh tồn tại và không còn lượt tải treo.</param>
/// <param name="InUse">This is the active model. Đây là model đang dùng.</param>
/// <param name="Downloading">A download thread is running. Có thread tải đang chạy.</param>
public sealed record LlmModelInfo(
    string Name, string Quant, string SizeClass, long SizeBytes,
    bool Installed, bool InUse, bool Downloading)
{
    /// <summary>
    /// In-flight download progress; <see langword="null"/> unless a download is running and
    /// reporting. Tiến độ tải đang chạy; <see langword="null"/> trừ khi có lượt tải đang báo tiến độ.
    /// </summary>
    public LlmDownloadProgress? Progress { get; init; }

    /// <summary>
    /// Error from the latest failed download, or <see langword="null"/> when none failed or a retry
    /// is in flight. Lỗi của lượt tải gần nhất thất bại, hoặc <see langword="null"/> khi không có
    /// lỗi hoặc đang thử lại.
    /// </summary>
    public string? DownloadError { get; init; }
}

/// <summary>
/// Byte-level progress of one in-flight download (wizard vòng 2 §9 — % + ETA).
/// Tiến độ theo byte của một lượt tải đang chạy (wizard vòng 2 §9 — % + ETA).
/// </summary>
/// <param name="ReceivedBytes">Bytes written so far. Số byte đã ghi.</param>
/// <param name="TotalBytes">Total bytes from content-length, or 0 when unknown. Tổng byte theo content-length, hoặc 0 khi chưa biết.</param>
/// <param name="BytesPerSec">Average speed so far in bytes/second, or <see langword="null"/>. Tốc độ trung bình tới giờ (byte/giây), hoặc <see langword="null"/>.</param>
public sealed record LlmDownloadProgress(long ReceivedBytes, long TotalBytes, double? BytesPerSec);

/// <summary>
/// Core-side manager for the curated GGUF catalog: filesystem-derived installed state (a
/// <c>.partial</c> marker means an in-flight download), background downloads on one thread per
/// model, an active-model pointer in <c>models.json</c> that can never be deleted, and
/// <see cref="RpcMethodException"/> (-32602) for every invalid request. Mirrors the OCR model
/// manager's contract.
/// Manager phía Core cho catalog GGUF tuyển chọn: trạng thái đã-cài suy từ filesystem (marker
/// <c>.partial</c> nghĩa là đang tải), tải nền một thread mỗi model, con trỏ model đang dùng trong
/// <c>models.json</c> không bao giờ bị xoá, và <see cref="RpcMethodException"/> (-32602) cho mọi
/// yêu cầu sai. Gương theo hợp đồng của quản lý model OCR.
/// </summary>
/// <param name="modelsDir">Directory holding the GGUF files. Thư mục chứa file GGUF.</param>
/// <param name="stateFile">JSON file storing the active model. File JSON lưu model đang dùng.</param>
/// <param name="downloader">Optional download implementation (target = partial path); tests inject a fake. Triển khai tải tuỳ chọn (đích = đường dẫn .partial); test inject bản giả.</param>
public sealed class LlmModelManager(
    string modelsDir, string stateFile,
    Func<LlmModelSpec, string, CancellationToken, Task>? downloader = null)
{
    private static readonly HttpClient SharedClient = HttpUa.CreateClient();

    private readonly string _modelsDir = modelsDir ?? throw new ArgumentNullException(nameof(modelsDir));
    private readonly string _stateFile = stateFile ?? throw new ArgumentNullException(nameof(stateFile));
    // Bound lazily at use: the default downloader reports progress, so it must be an instance method.
    // Gắn muộn lúc dùng: downloader mặc định báo tiến độ nên phải là instance method.
    private readonly Func<LlmModelSpec, string, CancellationToken, Task>? _downloader = downloader;
    private readonly object _lock = new();
    private readonly Dictionary<string, Thread> _downloads = new();
    private readonly Dictionary<string, LlmDownloadProgress> _progress = new();
    private readonly Dictionary<string, string> _downloadErrors = new();
    private string? _active = ReadActive(stateFile);

    /// <summary>Absolute path of one model's GGUF file. Đường dẫn tuyệt đối file GGUF của một model.</summary>
    /// <param name="name">Catalog name. Tên catalog.</param>
    /// <returns>The file path (may not exist yet). Đường dẫn file (có thể chưa tồn tại).</returns>
    /// <exception cref="RpcMethodException">Thrown for unknown names (-32602). Khi tên lạ (-32602).</exception>
    public string ModelPath(string name) => Path.Combine(_modelsDir, Find(name).File);

    /// <summary>The active catalog name, or <see langword="null"/> when none was chosen. Tên catalog đang dùng, hoặc <see langword="null"/> khi chưa chọn.</summary>
    public string? ActiveModel
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>The active model's file path, or <see langword="null"/> when none is chosen or installed. Đường dẫn file model đang dùng, hoặc <see langword="null"/> khi chưa chọn hoặc chưa cài.</summary>
    public string? ActiveModelPath
    {
        get
        {
            lock (_lock)
            {
                if (_active is null) return null;
                LlmModelSpec spec = Find(_active);
                return IsInstalled(spec) ? Path.Combine(_modelsDir, spec.File) : null;
            }
        }
    }

    /// <summary>
    /// Lists every catalog entry with filesystem-derived state. Liệt kê mọi mục catalog kèm trạng
    /// thái suy từ filesystem.
    /// </summary>
    /// <returns>The catalog in declaration order. Catalog theo thứ tự khai báo.</returns>
    public IReadOnlyList<LlmModelInfo> List()
    {
        lock (_lock)
        {
            return LlmModelCatalog.All
                .Select(spec => new LlmModelInfo(
                    spec.Name, spec.Quant, spec.SizeClass, spec.SizeBytes,
                    IsInstalled(spec), _active == spec.Name, _downloads.ContainsKey(spec.Name))
                {
                    Progress = _progress.TryGetValue(spec.Name, out LlmDownloadProgress? p) ? p : null,
                    DownloadError = _downloadErrors.TryGetValue(spec.Name, out string? error) ? error : null,
                })
                .ToList();
        }
    }

    /// <summary>
    /// Starts a background download unless the model is already downloading or installed.
    /// Khởi động tải nền trừ khi model đang tải hoặc đã cài.
    /// </summary>
    /// <param name="name">Catalog name. Tên catalog.</param>
    /// <returns><see langword="true"/> when a download was started. <see langword="true"/> khi đã khởi động tải.</returns>
    /// <exception cref="RpcMethodException">Thrown for unknown names (-32602). Khi tên lạ (-32602).</exception>
    public bool StartDownload(string name)
    {
        LlmModelSpec spec = Find(name);
        lock (_lock)
        {
            if (_downloads.ContainsKey(name) || IsInstalled(spec)) return false;
            _downloadErrors.Remove(name);
            var thread = new Thread(() => RunDownload(spec))
            {
                IsBackground = true,
                Name = $"LlmModelDownload-{name}",
            };
            _downloads[name] = thread;
            thread.Start();
            return true;
        }
    }

    /// <summary>
    /// Deletes a model's files; refuses the active model or an in-flight download.
    /// Xoá file của một model; từ chối model đang dùng hoặc đang tải.
    /// </summary>
    /// <param name="name">Catalog name. Tên catalog.</param>
    /// <exception cref="RpcMethodException">Thrown for unknown, active or downloading models (-32602). Khi model lạ, đang dùng hoặc đang tải (-32602).</exception>
    public void Delete(string name)
    {
        LlmModelSpec spec = Find(name);
        lock (_lock)
        {
            if (_downloads.ContainsKey(name))
            {
                throw new RpcMethodException(RpcErrorCodes.InvalidParams, $"model '{name}' is being downloaded");
            }
            if (_active == name)
            {
                throw new RpcMethodException(RpcErrorCodes.InvalidParams, $"model in use: '{name}' is the active model");
            }
            string path = Path.Combine(_modelsDir, spec.File);
            File.Delete(path);
            File.Delete(path + ".partial");
            _downloadErrors.Remove(name);
        }
    }

    /// <summary>
    /// Marks an installed model as the active one and persists the choice.
    /// Đánh dấu một model đã cài là model đang dùng và lưu lựa chọn.
    /// </summary>
    /// <param name="name">Catalog name. Tên catalog.</param>
    /// <exception cref="RpcMethodException">Thrown for unknown or not-installed models (-32602). Khi model lạ hoặc chưa cài (-32602).</exception>
    public void SetActive(string name)
    {
        LlmModelSpec spec = Find(name);
        lock (_lock)
        {
            if (!IsInstalled(spec))
            {
                throw new RpcMethodException(RpcErrorCodes.InvalidParams, $"model is not installed: {name}");
            }
            _active = name;
            string? dir = Path.GetDirectoryName(_stateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_stateFile, new JsonObject { ["active"] = name }.ToJsonString());
        }
    }

    private void RunDownload(LlmModelSpec spec)
    {
        string path = Path.Combine(_modelsDir, spec.File);
        string partial = path + ".partial";
        try
        {
            Directory.CreateDirectory(_modelsDir);
            Func<LlmModelSpec, string, CancellationToken, Task> download = _downloader ?? DefaultDownloadAsync;
            download(spec, partial, CancellationToken.None).GetAwaiter().GetResult();
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Keep the target absent so List() stays "not installed"; drop the failed partial, but
            // retain the reason so the RPC catalog and Settings can show why the background job ended.
            // Giữ đích vắng mặt và xoá .partial hỏng, nhưng lưu lý do để catalog RPC/Settings biết
            // vì sao job nền kết thúc thay vì nuốt lỗi.
            try { File.Delete(partial); } catch (IOException) { }
            lock (_lock)
            {
                _downloadErrors[spec.Name] = ex.Message;
            }
            Console.Error.WriteLine($"llm.models.download failed for '{spec.Name}': {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _downloads.Remove(spec.Name);
                _progress.Remove(spec.Name);
            }
        }
    }

    private bool IsInstalled(LlmModelSpec spec)
    {
        string path = Path.Combine(_modelsDir, spec.File);
        return File.Exists(path) && !File.Exists(path + ".partial");
    }

    private static LlmModelSpec Find(string name)
    {
        foreach (LlmModelSpec spec in LlmModelCatalog.All)
        {
            if (spec.Name == name) return spec;
        }
        throw new RpcMethodException(RpcErrorCodes.InvalidParams, $"unknown LLM model: {name}");
    }

    private static string? ReadActive(string stateFile)
    {
        try
        {
            if (!File.Exists(stateFile)) return null;
            JsonNode? root = JsonNode.Parse(File.ReadAllText(stateFile));
            return root?["active"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports byte-level progress of an in-flight download (called by the downloader; tests call it
    /// directly on a blocked fake). Báo tiến độ theo byte của một lượt tải (do downloader gọi; test
    /// gọi trực tiếp trên fake đang chặn).
    /// </summary>
    /// <param name="name">Catalog name. Tên catalog.</param>
    /// <param name="receivedBytes">Bytes written so far. Số byte đã ghi.</param>
    /// <param name="totalBytes">Total bytes, or 0 when unknown. Tổng byte, hoặc 0 khi chưa biết.</param>
    /// <param name="bytesPerSec">Average speed, or <see langword="null"/>. Tốc độ trung bình, hoặc <see langword="null"/>.</param>
    internal void ReportDownloadProgress(string name, long receivedBytes, long totalBytes, double? bytesPerSec)
    {
        lock (_lock)
        {
            _progress[name] = new LlmDownloadProgress(receivedBytes, totalBytes, bytesPerSec);
        }
    }

    private async Task DefaultDownloadAsync(LlmModelSpec spec, string target, CancellationToken ct)
    {
        string url = $"https://huggingface.co/{spec.Repo}/resolve/main/{spec.File}";
        using HttpResponseMessage response =
            await SharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using FileStream destination = File.Create(target);

        // Read in chunks so progress can be reported (wizard vòng 2 §9: % + ETA needs received/total/speed).
        // Đọc theo chunk để báo được tiến độ (wizard vòng 2 §9: % + ETA cần received/total/speed).
        byte[] buffer = new byte[81920];
        long received = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ReportDownloadProgress(spec.Name, 0, total, null);
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            double seconds = stopwatch.Elapsed.TotalSeconds;
            ReportDownloadProgress(spec.Name, received, total, seconds > 0.5 ? received / seconds : null);
        }
    }
}
