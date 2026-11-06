using System.Text;

namespace ReTran_App.Services.Setup;

/// <summary>
/// Installs the OCR sidecar environment for the wheel flavor <see cref="SystemCheck"/> picked (spec §4
/// step 3, §5.7): rewrites <c>ReTran.OCR/pyproject.toml</c> so it pins the wheel the machine can actually
/// run, drives <c>uv sync</c> (plus the CPU wheel install on the CPU path), and reports which paddle wheel
/// ended up installed.
/// Cài môi trường sidecar OCR theo flavor wheel mà <see cref="SystemCheck"/> chọn (spec §4 bước 3, §5.7):
/// rewrite <c>ReTran.OCR/pyproject.toml</c> để pin đúng wheel mà máy chạy được, chạy <c>uv sync</c> (kèm
/// lệnh cài wheel CPU trên nhánh CPU), và báo wheel paddle nào đã cài xong.
/// </summary>
public sealed class UvSyncRunner
{
    /// <summary>
    /// Requirement <c>uv pip install</c> adds after <c>uv sync</c> on the CPU path: the CPU-only paddle
    /// wheel, the same 3.4.0 line as the GPU pin it replaces in pyproject.toml.
    /// Requirement mà <c>uv pip install</c> chạy sau <c>uv sync</c> trên nhánh CPU: wheel paddle bản CPU,
    /// đúng bản 3.4.0 như pin GPU mà nó thay thế trong pyproject.toml.
    /// </summary>
    public const string CpuPaddleRequirement = "paddlepaddle==3.4.0";

    /// <summary>
    /// Environment variable every uv invocation gets, pinned to <see cref="UvPythonPreferenceValue"/>: the
    /// wizard must never pick up a stray system Python (spec §5.7 — uv provisions its own managed CPython).
    /// Biến môi trường mà mọi lệnh uv nhận, ghim đúng <see cref="UvPythonPreferenceValue"/>: wizard không
    /// được dùng Python hệ thống lang thang (spec §5.7 — uv tự cung cấp CPython managed).
    /// </summary>
    public const string UvPythonPreferenceVariable = "UV_PYTHON_PREFERENCE";

    /// <summary>
    /// Value of <see cref="UvPythonPreferenceVariable"/>: only uv-managed Pythons.
    /// Giá trị của <see cref="UvPythonPreferenceVariable"/>: chỉ Python do uv quản lý.
    /// </summary>
    public const string UvPythonPreferenceValue = "only-managed";

    private const string PyprojectFileName = "pyproject.toml";

    // Variant khi cudaWheel null hoặc không có trong danh sách: pyproject HEAD pin cu129, nên cu129 là mặc
    // định trung thực nhất (SystemCheck chỉ phát hành "cu130"/"cu129"/"cu126").
    // Variant used when cudaWheel is null or unlisted: the HEAD pyproject pins cu129, so cu129 is the most
    // faithful default (SystemCheck only ever emits "cu130"/"cu129"/"cu126").
    private const string DefaultCudaWheel = "cu129";

    private const string PaddleIndexNamePrefix = "paddle-";
    private const string PaddleIndexUrlBase = "https://www.paddlepaddle.org.cn/packages/stable/";
    private const string GpuWheelPackage = "paddlepaddle-gpu";
    private const string CudnnPackage = "nvidia-cudnn-cu12";
    private const string PaddlePackage = "paddlepaddle";

    // Tên section TOML đã biết trong file này; [[tool.uv.index]] là array-of-tables (2 cặp ngoặc).
    // Known TOML section names in this file; [[tool.uv.index]] is an array-of-tables (two bracket pairs).
    private const string ProjectHeader = "[project]";
    private const string ToolUvIndexHeader = "[[tool.uv.index]]";
    private const string ToolUvSourcesHeader = "[tool.uv.sources]";

    private readonly IProcessRunner _runner;
    private readonly string _uvExe;
    private readonly string _ocrDir;

    /// <summary>
    /// Creates the runner. <paramref name="uvExe"/> and <paramref name="ocrDir"/> default to
    /// <see langword="null"/> rather than the <see cref="SetupPaths"/> values because C# requires
    /// compile-time constants as parameter defaults and those are static properties; null is resolved to the
    /// <see cref="SetupPaths"/> value here, so production stays <c>new UvSyncRunner(runner)</c> while tests
    /// pass a temp tree.
    /// Tạo runner. <paramref name="uvExe"/> và <paramref name="ocrDir"/> mặc định là
    /// <see langword="null"/> chứ không phải giá trị <see cref="SetupPaths"/> vì C# đòi hằng số tại thời điểm
    /// biên dịch cho giá trị mặc định của tham số, mà các member đó là static property; null được resolve
    /// thành giá trị <see cref="SetupPaths"/> ngay tại đây, nên production vẫn là
    /// <c>new UvSyncRunner(runner)</c> còn test truyền cây temp.
    /// </summary>
    /// <param name="runner">
    /// Process seam — the fake in tests, <see cref="ProcessRunner"/> in the app. / Seam tiến trình — fake
    /// trong test, <see cref="ProcessRunner"/> trong app.
    /// </param>
    /// <param name="uvExe">
    /// uv executable, or <see langword="null"/> for <see cref="SetupPaths.UvExe"/>. / Đường dẫn uv, hoặc
    /// <see langword="null"/> để lấy <see cref="SetupPaths.UvExe"/>.
    /// </param>
    /// <param name="ocrDir">
    /// Sidecar tree holding <c>pyproject.toml</c> and <c>.venv</c>, or <see langword="null"/> for
    /// <see cref="SetupPaths.OcrDir"/>. / Cây sidecar chứa <c>pyproject.toml</c> và <c>.venv</c>, hoặc
    /// <see langword="null"/> để lấy <see cref="SetupPaths.OcrDir"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runner"/> is null. / <paramref name="runner"/> null.
    /// </exception>
    public UvSyncRunner(IProcessRunner runner, string? uvExe = null, string? ocrDir = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _uvExe = uvExe ?? SetupPaths.UvExe;
        _ocrDir = ocrDir ?? SetupPaths.OcrDir;
    }

    /// <summary>
    /// The <c>pyproject.toml</c> this instance rewrites: <c>&lt;ocrDir&gt;\pyproject.toml</c>.
    /// File <c>pyproject.toml</c> mà instance này rewrite: <c>&lt;ocrDir&gt;\pyproject.toml</c>.
    /// </summary>
    public string PyprojectPath => Path.Combine(_ocrDir, PyprojectFileName);

    /// <summary>
    /// Output tail of the uv command that failed in the latest <see cref="SyncAsync"/> (or of the latest
    /// <see cref="InstalledPaddleFlavorAsync"/> probe), or <see langword="null"/> when that run succeeded.
    /// The error screen quotes it (Review Focus #4), and it is cleared at the start of every retry so a
    /// later attempt can never show a stale tail.
    /// Đuôi output của lệnh uv fail trong <see cref="SyncAsync"/> gần nhất (hoặc của probe
    /// <see cref="InstalledPaddleFlavorAsync"/> gần nhất), hoặc <see langword="null"/> khi lần đó thành
    /// công. Màn hình lỗi trích đúng chuỗi này (Review Focus #4), và nó được xoá ở đầu mỗi lần retry để lần
    /// chạy sau không bao giờ hiện tail cũ.
    /// </summary>
    public string? LastOutputTail { get; private set; }

    /// <summary>
    /// Rewrites <paramref name="pyprojectPath"/> so its dependency pins and uv index point at the wheel for
    /// this machine (spec §5.7, ruling 2026-11-05). Line/section text surgery, not a TOML parse: the file is
    /// small and its format is fixed by this repo.
    /// <list type="bullet">
    /// <item>CPU: drops the <c>paddlepaddle-gpu</c> and <c>nvidia-cudnn-cu12</c> dependency lines plus the
    /// <c>[[tool.uv.index]]</c> and <c>[tool.uv.sources]</c> sections; every other dependency and section
    /// stays. The CPU wheel itself is installed by <see cref="SyncAsync"/>, not pinned here.</item>
    /// <item>GPU: rewrites the index name/url to <c>paddle-&lt;variant&gt;</c> and
    /// <c>.../packages/stable/&lt;variant&gt;/</c>, repoints <c>[tool.uv.sources]</c> at that index, and for
    /// cu126/cu130 also drops the <c>nvidia-cudnn-cu12</c> pin (cu129 keeps it, matching the HEAD file — the
    /// paddle wheel pulls the right cudnn for the other variants).</item>
    /// </list>
    /// Idempotent and quiet: the file is written only when the content actually changes, and line endings
    /// plus the UTF-8 BOM state are preserved, so the cu129 case leaves the repo file byte-identical.
    /// Rewrite <paramref name="pyprojectPath"/> để pin dependency và index uv trỏ đúng wheel của máy (spec
    /// §5.7, ruling 2026-11-05). Thao tác text theo dòng/section, không parse TOML: file nhỏ và format đã
    /// được repo này cố định.
    /// <list type="bullet">
    /// <item>CPU: bỏ 2 dòng dependency <c>paddlepaddle-gpu</c> và <c>nvidia-cudnn-cu12</c> cùng section
    /// <c>[[tool.uv.index]]</c> và <c>[tool.uv.sources]</c>; mọi dependency và section khác nguyên vẹn. Wheel
    /// CPU do <see cref="SyncAsync"/> cài, không pin ở đây.</item>
    /// <item>GPU: rewrite name/url của index thành <c>paddle-&lt;variant&gt;</c> và
    /// <c>.../packages/stable/&lt;variant&gt;/</c>, trỏ lại <c>[tool.uv.sources]</c> vào index đó, và với
    /// cu126/cu130 bỏ luôn pin <c>nvidia-cudnn-cu12</c> (cu129 giữ pin đúng như file đi kèm — wheel paddle tự
    /// kéo đúng cudnn cho các variant còn lại).</item>
    /// </list>
    /// Idempotent và im lặng: chỉ ghi file khi nội dung thật đổi, giữ nguyên line ending và trạng thái BOM
    /// UTF-8, nên trường hợp cu129 để file repo y hệt từng byte.
    /// </summary>
    /// <param name="flavor">
    /// Wheel flavor to prepare. / Flavor wheel cần chuẩn bị.
    /// </param>
    /// <param name="cudaWheel">
    /// CUDA variant label ("cu130"/"cu129"/"cu126"), ignored on the CPU path. <see langword="null"/> or an
    /// unlisted label means cu129 — the variant the HEAD pyproject already pins.
    /// Nhãn biến thể CUDA ("cu130"/"cu129"/"cu126"), bỏ qua trên nhánh CPU. <see langword="null"/> hoặc nhãn
    /// không có trong danh sách ⇒ hiểu là cu129 — biến thể mà pyproject HEAD đang pin.
    /// </param>
    /// <param name="pyprojectPath">
    /// File to rewrite in place. / File được rewrite tại chỗ.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="pyprojectPath"/> is empty. / <paramref name="pyprojectPath"/> rỗng.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist — the wizard ships the sidecar tree beside the app, so a missing file means a
    /// broken install, not a flavor waiting to be created. / File không tồn tại — wizard đi kèm cây sidecar
    /// cạnh app, nên file thiếu là cài đặt hỏng, không phải flavor cần tạo.
    /// </exception>
    public static void ApplyFlavorToPyproject(OcrFlavor flavor, string? cudaWheel, string pyprojectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pyprojectPath);

        byte[] raw = File.ReadAllBytes(pyprojectPath);
        bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        int offset = hasBom ? 3 : 0;
        string original = Encoding.UTF8.GetString(raw, offset, raw.Length - offset);

        string updated = RewritePyproject(original, flavor, cudaWheel);
        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            // Không ghi: giữ nguyên từng byte (kể cả line ending và BOM) cho tới khi nội dung thật đổi.
            // No write: every byte (line endings and BOM included) stays put until the content differs.
            return;
        }

        File.WriteAllText(pyprojectPath, updated, new UTF8Encoding(hasBom));
    }

    /// <summary>
    /// Applies the flavor to <see cref="PyprojectPath"/> then runs uv. GPU: exactly one <c>uv sync</c>. CPU:
    /// <c>uv sync</c> then <c>uv pip install paddlepaddle==3.4.0</c>, in that order — sync resolves the
    /// CPU-only dependency set, then the CPU wheel is installed on top. Every command runs in
    /// <see cref="SetupPaths.OcrDir"/> (or the injected ocr dir) with
    /// <c>UV_PYTHON_PREFERENCE=only-managed</c> and no other flags.
    /// Áp flavor vào <see cref="PyprojectPath"/> rồi chạy uv. GPU: đúng một lệnh <c>uv sync</c>. CPU:
    /// <c>uv sync</c> rồi <c>uv pip install paddlepaddle==3.4.0</c>, đúng thứ tự — sync resolve bộ dependency
    /// chỉ-CPU, sau đó wheel CPU được cài chồng lên. Mọi lệnh chạy trong <see cref="SetupPaths.OcrDir"/> (hoặc
    /// ocr dir được inject) với <c>UV_PYTHON_PREFERENCE=only-managed</c> và không thêm cờ nào.
    /// </summary>
    /// <param name="flavor">
    /// Wheel flavor from <see cref="SystemCheckResult.Flavor"/>. / Flavor wheel từ <see cref="SystemCheckResult.Flavor"/>.
    /// </param>
    /// <param name="cudaWheel">
    /// CUDA variant from <see cref="SystemCheckResult.CudaWheel"/>; null means cu129. / Biến thể CUDA từ
    /// <see cref="SystemCheckResult.CudaWheel"/>; null ⇒ cu129.
    /// </param>
    /// <param name="ct">
    /// Cancellation token forwarded to the process runner; cancelling kills uv. / Token hủy chuyển cho process
    /// runner; hủy sẽ kill uv.
    /// </param>
    /// <returns>
    /// <c>0</c> when every command succeeded, otherwise the exit code of the FIRST failing command — its
    /// output is left in <see cref="LastOutputTail"/> for the retry/error UI (Review Focus #4). Commands after
    /// the failure are not run.
    /// <c>0</c> khi mọi lệnh thành công, ngược lại là exit code của lệnh FAIL ĐẦU TIÊN — output của lệnh đó
    /// được giữ trong <see cref="LastOutputTail"/> cho UI retry/lỗi (Review Focus #4). Lệnh sau lỗi không chạy.
    /// </returns>
    public async Task<int> SyncAsync(OcrFlavor flavor, string? cudaWheel, CancellationToken ct)
    {
        ApplyFlavorToPyproject(flavor, cudaWheel, PyprojectPath);
        LastOutputTail = null;

        (int syncExit, string syncTail) = await _runner
            .RunAsync(_uvExe, ["sync"], _ocrDir, BuildUvEnvironment(), ct)
            .ConfigureAwait(false);
        if (syncExit != 0)
        {
            LastOutputTail = syncTail;
            return syncExit;
        }

        if (flavor != OcrFlavor.Cpu) return 0;

        (int pipExit, string pipTail) = await _runner
            .RunAsync(_uvExe, ["pip", "install", CpuPaddleRequirement], _ocrDir, BuildUvEnvironment(), ct)
            .ConfigureAwait(false);
        if (pipExit != 0) LastOutputTail = pipTail;
        return pipExit;
    }

    /// <summary>
    /// Asks uv which paddle package the venv has (<c>uv pip list</c>) and maps it to the installed flavor:
    /// <c>paddlepaddle-gpu</c> → <see cref="OcrFlavor.Gpu"/>, a bare <c>paddlepaddle</c> →
    /// <see cref="OcrFlavor.Cpu"/>, neither (or uv itself failed, e.g. no venv yet) → null. Matching is on the
    /// package-name token, so <c>paddleocr</c> and other paddle-family packages never count.
    /// Hỏi uv xem venv có package paddle nào (<c>uv pip list</c>) rồi ánh xạ sang flavor đã cài:
    /// <c>paddlepaddle-gpu</c> → <see cref="OcrFlavor.Gpu"/>, <c>paddlepaddle</c> trần →
    /// <see cref="OcrFlavor.Cpu"/>, không có cả hai (hoặc uv fail, vd chưa có venv) → null. So khớp theo token
    /// tên package, nên <c>paddleocr</c> và các package họ paddle khác không bị tính.
    /// </summary>
    /// <param name="ct">
    /// Cancellation token; defaults to none. / Token hủy; mặc định không hủy.
    /// </param>
    /// <returns>
    /// The installed flavor, or null when uv did not report a paddle wheel — the caller then treats the
    /// install as missing. / Flavor đã cài, hoặc null khi uv không báo wheel paddle nào — caller coi như chưa cài.
    /// </returns>
    public async Task<OcrFlavor?> InstalledPaddleFlavorAsync(CancellationToken ct = default)
    {
        (int exit, string outputTail) = await _runner
            .RunAsync(_uvExe, ["pip", "list"], _ocrDir, BuildUvEnvironment(), ct)
            .ConfigureAwait(false);
        LastOutputTail = exit == 0 ? null : outputTail;
        return exit == 0 ? ParseInstalledFlavor(outputTail) : null;
    }

    /// <summary>
    /// Fresh environment instance for one uv call; never shared between calls.
    /// Môi trường mới cho từng lệnh uv; không dùng chung giữa các lần gọi.
    /// </summary>
    private static Dictionary<string, string> BuildUvEnvironment() =>
        new() { [UvPythonPreferenceVariable] = UvPythonPreferenceValue };

    /// <summary>
    /// The first token of each <c>uv pip list</c> row is the package name; GPU wins over CPU as soon as
    /// <c>paddlepaddle-gpu</c> appears.
    /// Token đầu của mỗi dòng <c>uv pip list</c> là tên package; GPU thắng CPU ngay khi
    /// <c>paddlepaddle-gpu</c> xuất hiện.
    /// </summary>
    private static OcrFlavor? ParseInstalledFlavor(string output)
    {
        bool cpuSeen = false;
        foreach (string row in output.Split('\n'))
        {
            string trimmed = row.Trim();
            if (trimmed.Length == 0) continue;

            string name = trimmed.Split(new[] { ' ', '\t' }, 2)[0];
            if (name.Equals(GpuWheelPackage, StringComparison.OrdinalIgnoreCase)) return OcrFlavor.Gpu;
            if (name.Equals(PaddlePackage, StringComparison.OrdinalIgnoreCase)) cpuSeen = true;
        }

        return cpuSeen ? OcrFlavor.Cpu : null;
    }

    /// <summary>
    /// Line-level rewrite of the whole file. Splits on newlines, remembers the dominant newline (CRLF wins
    /// when present) and whether the file ended with one, edits line by line, then rejoins — that is what
    /// keeps an unchanged cu129 file byte-identical.
    /// Rewrite ở mức dòng cho cả file. Tách theo xuống dòng, nhớ kiểu xuống dòng chủ đạo (có CRLF là CRLF) và
    /// việc file có kết thúc bằng xuống dòng hay không, sửa từng dòng rồi ghép lại — đó là cách giữ file
    /// cu129 không đổi ở mức byte.
    /// </summary>
    private static string RewritePyproject(string text, OcrFlavor flavor, string? cudaWheel)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] split = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        // Phần tử cuối rỗng ⇒ file kết thúc bằng xuống dòng; giữ lại chi tiết đó khi ghép.
        // The last element is empty when the file ends with a newline; that detail is kept on rejoin.
        bool endsWithNewline = split.Length > 1 && split[^1].Length == 0;
        int bodyCount = split.Length - (endsWithNewline ? 1 : 0);

        string? variant = flavor == OcrFlavor.Gpu ? NormalizeWheel(cudaWheel) : null;
        bool dropUvSections = flavor == OcrFlavor.Cpu;

        var output = new List<string>(split.Length);
        string section = string.Empty;
        for (int i = 0; i < bodyCount; i++)
        {
            string trimmed = split[i].Trim();

            if (IsSectionHeader(trimmed))
            {
                section = trimmed;
                if (dropUvSections && (section == ToolUvIndexHeader || section == ToolUvSourcesHeader))
                {
                    // Bỏ cả dòng trống phân cách ngay trước section để file CPU không còn rác cuối.
                    // Drop the blank separator line right before the section so the CPU file has no junk tail.
                    while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
                    while (i + 1 < bodyCount && !IsSectionHeader(split[i + 1].Trim())) i++;
                    continue;
                }
            }

            if (IsGpuPinLine(trimmed, section, flavor, variant)) continue;

            output.Add(RewriteSectionLine(split[i], trimmed, section, variant));
        }

        return string.Join(newline, output) + (endsWithNewline ? newline : string.Empty);
    }

    /// <summary>
    /// Variant label used for the index rewrite: null or an unlisted label falls back to
    /// <see cref="DefaultCudaWheel"/> (cu129), the variant the shipped pyproject already points at.
    /// Nhãn biến thể dùng để rewrite index: null hoặc nhãn lạ rơi về <see cref="DefaultCudaWheel"/> (cu129),
    /// biến thể mà pyproject đi kèm đang trỏ tới.
    /// </summary>
    private static string NormalizeWheel(string? cudaWheel)
        => cudaWheel is "cu126" or "cu130" ? cudaWheel : DefaultCudaWheel;

    /// <summary>
    /// A TOML table / array-of-tables header line, e.g. <c>[project]</c> or <c>[[tool.uv.index]]</c>. No
    /// content line in this file starts with '[' and ends with ']', so a shape test is enough.
    /// Dòng header của table / array-of-tables TOML, vd <c>[project]</c> hoặc <c>[[tool.uv.index]]</c>. Không
    /// có dòng nội dung nào trong file này bắt đầu bằng '[' và kết thúc bằng ']', nên xét hình dạng là đủ.
    /// </summary>
    private static bool IsSectionHeader(string trimmed)
        => trimmed.Length > 1 && trimmed[0] == '[' && trimmed[^1] == ']';

    /// <summary>
    /// Dependency lines to drop: on CPU both GPU pins; on GPU only the <c>nvidia-cudnn-cu12</c> pin, and only
    /// for cu126/cu130 (cu129 keeps it exactly as the shipped file has it). Restricted to the
    /// <c>[project]</c> section so a same-named key elsewhere is never touched.
    /// Dòng dependency cần bỏ: trên CPU bỏ cả 2 pin GPU; trên GPU chỉ bỏ pin <c>nvidia-cudnn-cu12</c>, và chỉ
    /// với cu126/cu130 (cu129 giữ pin đúng như file đi kèm). Giới hạn trong section <c>[project]</c> để khóa
    /// cùng tên ở chỗ khác không bị đụng.
    /// </summary>
    private static bool IsGpuPinLine(string trimmed, string section, OcrFlavor flavor, string? variant)
    {
        if (section != ProjectHeader) return false;
        if (flavor == OcrFlavor.Cpu)
        {
            return IsDependencyLine(trimmed, GpuWheelPackage) || IsDependencyLine(trimmed, CudnnPackage);
        }

        return variant != DefaultCudaWheel && IsDependencyLine(trimmed, CudnnPackage);
    }

    /// <summary>
    /// True for a quoted dependency entry for <paramref name="packageName"/>, e.g.
    /// <c>"paddlepaddle-gpu==3.4.0",</c>.
    /// True với một entry dependency có dấu bao quanh cho <paramref name="packageName"/>, vd
    /// <c>"paddlepaddle-gpu==3.4.0",</c>.
    /// </summary>
    private static bool IsDependencyLine(string trimmed, string packageName)
        => trimmed.StartsWith($"\"{packageName}", StringComparison.OrdinalIgnoreCase)
        || trimmed.StartsWith($"'{packageName}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Repoints the uv index entry and the sources entry at the target variant; anything outside those two
    /// sections is returned untouched — including its original indentation, which is why both the raw line
    /// and its trimmed form are passed in. Name and url are rewritten even for cu129 so a file left at
    /// another variant returns to the shipped state.
    /// Trỏ lại entry index uv và entry sources về biến thể đích; mọi thứ ngoài 2 section đó trả nguyên trạng
    /// — kể cả indent gốc, đó là lý do truyền cả dòng thô lẫn dòng đã trim. name và url được rewrite kể cả
    /// với cu129 để file đang ở biến thể khác quay về đúng trạng thái đi kèm.
    /// </summary>
    private static string RewriteSectionLine(string line, string trimmed, string section, string? variant)
    {
        if (variant is null) return line;

        if (section == ToolUvIndexHeader)
        {
            if (trimmed.StartsWith("name", StringComparison.OrdinalIgnoreCase)
                && TryReadQuotedValue(trimmed, out string indexName)
                && indexName.StartsWith(PaddleIndexNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return $"name = \"{PaddleIndexNamePrefix}{variant}\"";
            }

            if (trimmed.StartsWith("url", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains(PaddleIndexUrlBase, StringComparison.OrdinalIgnoreCase))
            {
                return $"url = \"{PaddleIndexUrlBase}{variant}/\"";
            }
        }
        else if (section == ToolUvSourcesHeader
                 && trimmed.StartsWith(GpuWheelPackage, StringComparison.OrdinalIgnoreCase))
        {
            return RepointIndexReference(line, $"{PaddleIndexNamePrefix}{variant}");
        }

        return line;
    }

    /// <summary>
    /// Replaces the quoted value of the first <c>index = "..."</c> on a sources line, keeping the rest of the
    /// line exactly as written.
    /// Thay giá trị trong dấu bao của <c>index = "..."</c> đầu tiên trên một dòng sources, giữ nguyên phần còn
    /// lại của dòng.
    /// </summary>
    private static string RepointIndexReference(string line, string indexName)
    {
        int keyword = line.IndexOf("index", StringComparison.OrdinalIgnoreCase);
        if (keyword < 0) return line;

        int open = line.IndexOf('"', keyword);
        if (open < 0) return line;
        int close = line.IndexOf('"', open + 1);
        if (close <= open) return line;

        return string.Concat(line.AsSpan(0, open + 1), indexName.AsSpan(), line.AsSpan(close));
    }

    /// <summary>
    /// Reads the quoted right-hand value of a <c>key = "value"</c> line (single or double quotes).
    /// Đọc giá trị bên phải có dấu bao của dòng <c>key = "value"</c> (nháy đơn hoặc nháy kép).
    /// </summary>
    private static bool TryReadQuotedValue(string line, out string value)
    {
        value = string.Empty;
        int equals = line.IndexOf('=');
        if (equals < 0) return false;

        string right = line[(equals + 1)..].Trim();
        if (right.Length < 2 || (right[0] != '"' && right[0] != '\'')) return false;

        char quote = right[0];
        int close = right.IndexOf(quote, 1);
        if (close < 0) return false;

        value = right[1..close];
        return true;
    }
}
