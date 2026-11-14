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
    bool Installed, bool InUse, bool Downloading);

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
    private static readonly HttpClient SharedClient = new();

    private readonly string _modelsDir = modelsDir ?? throw new ArgumentNullException(nameof(modelsDir));
    private readonly string _stateFile = stateFile ?? throw new ArgumentNullException(nameof(stateFile));
    private readonly Func<LlmModelSpec, string, CancellationToken, Task> _downloader = downloader ?? DefaultDownloadAsync;
    private readonly object _lock = new();
    private readonly Dictionary<string, Thread> _downloads = new();
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
                    IsInstalled(spec), _active == spec.Name, _downloads.ContainsKey(spec.Name)))
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
            _downloader(spec, partial, CancellationToken.None).GetAwaiter().GetResult();
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception)
        {
            // Keep the target absent so List() stays "not installed"; drop the failed partial.
            // Giữ đích vắng mặt để List() vẫn là "chưa cài"; xoá file .partial hỏng.
            try { File.Delete(partial); } catch (IOException) { }
        }
        finally
        {
            lock (_lock)
            {
                _downloads.Remove(spec.Name);
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

    private static async Task DefaultDownloadAsync(LlmModelSpec spec, string target, CancellationToken ct)
    {
        string url = $"https://huggingface.co/{spec.Repo}/resolve/main/{spec.File}";
        using HttpResponseMessage response =
            await SharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using FileStream destination = File.Create(target);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }
}
