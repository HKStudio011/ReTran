namespace ReTran_App.Services.Setup;

/// <summary>
/// Installs the OCR sidecar environment for the wheel flavor <see cref="SystemCheck"/> picked (spec §4
/// step 3, §5.7) by selecting a dependency GROUP of the sidecar tree: exactly one
/// <c>uv sync --no-default-groups --group gpu|cpu --frozen</c> per install. Under Amendment A2 of the
/// 2026-11-04 merge plan the app never edits <c>ReTran.OCR/pyproject.toml</c> — that file ships in one
/// canonical shape holding both flavors, and the shipped <c>uv.lock</c> carries both paddle
/// distributions, so the resolution this team measured is precisely what the user's machine installs.
/// Cài môi trường sidecar OCR theo flavor wheel mà <see cref="SystemCheck"/> chọn (spec §4 bước 3, §5.7)
/// bằng cách chọn một dependency GROUP của cây sidecar: mỗi lần cài đúng một lệnh
/// <c>uv sync --no-default-groups --group gpu|cpu --frozen</c>. Theo Amendment A2 của plan merge
/// 2026-11-04, app không bao giờ sửa <c>ReTran.OCR/pyproject.toml</c> — file đó ship ở một hình dạng chuẩn
/// duy nhất chứa cả hai flavor, và <c>uv.lock</c> đi kèm mang cả hai distribution paddle, nên bản resolve
/// đã đo chính là thứ được cài trên máy user.
/// </summary>
public sealed class UvSyncRunner
{
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

    // Tên hai dependency-group của sidecar trong pyproject + lock (Amendment A2). `--no-default-groups`
    // loại group `dev` (pytest) ra khỏi cài đặt của user; `conflicts` trong [tool.uv] khiến uv từ chối
    // cài gpu và cpu cùng lúc, nên không bao giờ có chuyện wheel CPU chồng lên môi trường GPU đang chạy.
    // The sidecar's two dependency-group names in the pyproject and lock (Amendment A2).
    // `--no-default-groups` keeps the `dev` group (pytest) out of the user's install; `conflicts` in
    // [tool.uv] makes uv refuse gpu and cpu together, so a CPU wheel can never layer onto a running GPU
    // environment.
    private const string GpuGroup = "gpu";
    private const string CpuGroup = "cpu";

    // Package name of each wheel flavor — InstalledPaddleFlavorAsync probes exactly these two with
    // `uv pip show`. / Tên package của mỗi flavor wheel — InstalledPaddleFlavorAsync dò đúng hai tên này
    // bằng `uv pip show`.
    private const string GpuWheelPackage = "paddlepaddle-gpu";
    private const string PaddlePackage = "paddlepaddle";

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
    /// Sidecar tree holding <c>pyproject.toml</c>, <c>uv.lock</c> and <c>.venv</c>, or
    /// <see langword="null"/> for <see cref="SetupPaths.OcrDir"/>. / Cây sidecar chứa <c>pyproject.toml</c>,
    /// <c>uv.lock</c> và <c>.venv</c>, hoặc <see langword="null"/> để lấy <see cref="SetupPaths.OcrDir"/>.
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
    /// The sidecar <c>pyproject.toml</c> that sits beside the <c>.venv</c> this instance syncs:
    /// <c>&lt;ocrDir&gt;\pyproject.toml</c>. The runner never reads or writes it (Amendment A2) — the path
    /// exists so a caller can show or check the file, and so tests can prove it stays byte-identical.
    /// File <c>pyproject.toml</c> của sidecar nằm cạnh <c>.venv</c> mà instance này sync:
    /// <c>&lt;ocrDir&gt;\pyproject.toml</c>. Runner không đọc hay ghi file đó (Amendment A2) — đường dẫn
    /// tồn tại để caller hiển thị/kiểm tra file, và để test chứng minh nó không đổi một byte nào.
    /// </summary>
    public string PyprojectPath => Path.Combine(_ocrDir, PyprojectFileName);

    /// <summary>
    /// Output tail of the uv command that failed in the latest <see cref="SyncAsync"/>, or
    /// <see langword="null"/> when every command of that run succeeded. The error screen quotes it (Review
    /// Focus #4), and it is cleared at the start of every retry so a later attempt can never show a stale
    /// tail. <see cref="InstalledPaddleFlavorAsync"/> never writes it: for <c>uv pip show</c> the exit code
    /// IS the answer rather than an error, and probing the flavor must not erase the tail the error screen
    /// still needs.
    /// Đuôi output của lệnh uv fail trong <see cref="SyncAsync"/> gần nhất, hoặc <see langword="null"/> khi
    /// mọi lệnh của lần đó thành công. Màn hình lỗi trích đúng chuỗi này (Review Focus #4), và nó được xoá ở
    /// đầu mỗi lần retry để lần chạy sau không hiện tail cũ. <see cref="InstalledPaddleFlavorAsync"/> không
    /// ghi vào đây: với <c>uv pip show</c> thì exit code CHÍNH LÀ câu trả lời chứ không phải lỗi, và dò flavor
    /// không được xoá tail mà màn hình lỗi còn cần.
    /// </summary>
    public string? LastOutputTail { get; private set; }

    /// <summary>
    /// Runs the one uv command this flavor needs:
    /// <c>sync --no-default-groups --group gpu|cpu --frozen</c> in <see cref="SetupPaths.OcrDir"/> (or the
    /// injected ocr dir) with <c>UV_PYTHON_PREFERENCE=only-managed</c> and no other flags. The group names
    /// come from the sidecar's <c>[dependency-groups]</c>; <c>--no-default-groups</c> drops the
    /// <c>dev</c>/<c>gpu</c> defaults a dev machine uses, and <c>--frozen</c> installs the shipped
    /// <c>uv.lock</c> as-is instead of re-resolving it, which is what keeps a user's install identical to
    /// the set that was performance-measured.
    /// Chạy đúng một lệnh uv mà flavor này cần: <c>sync --no-default-groups --group gpu|cpu --frozen</c>
    /// trong <see cref="SetupPaths.OcrDir"/> (hoặc ocr dir được inject) với
    /// <c>UV_PYTHON_PREFERENCE=only-managed</c> và không thêm cờ nào. Tên group lấy từ
    /// <c>[dependency-groups]</c> của sidecar; <c>--no-default-groups</c> bỏ các group mặc định
    /// <c>dev</c>/<c>gpu</c> mà máy dev dùng, còn <c>--frozen</c> cài đúng <c>uv.lock</c> đi kèm thay vì
    /// resolve lại — đó là cách giữ bản cài trên máy user giống hệt bộ đã đo perf.
    /// </summary>
    /// <remarks>
    /// No <c>pyproject.toml</c> I/O, and no extra <c>uv pip install</c> step: the CPU wheel lives in the
    /// <c>cpu</c> group of the same lock, so both flavors are one command apart. uv refuses to resolve the
    /// two groups into one environment (<c>conflicts</c> in <c>[tool.uv]</c>), so switching flavors on a
    /// machine that already has the other wheel re-installs cleanly rather than mixing them.
    /// Không có I/O <c>pyproject.toml</c>, và không thêm bước <c>uv pip install</c>: wheel CPU nằm trong
    /// group <c>cpu</c> của cùng lock đó, nên hai flavor chỉ cách nhau một lệnh. uv từ chối resolve hai
    /// group vào cùng một môi trường (<c>conflicts</c> trong <c>[tool.uv]</c>), nên đổi flavor trên máy đã
    /// có wheel kia sẽ cài lại sạch chứ không trộn lẫn.
    /// </remarks>
    /// <param name="flavor">
    /// Wheel flavor from <see cref="SystemCheckResult.Flavor"/>. / Flavor wheel từ
    /// <see cref="SystemCheckResult.Flavor"/>.
    /// </param>
    /// <param name="ct">
    /// Cancellation token forwarded to the process runner; cancelling kills uv. / Token hủy chuyển cho process
    /// runner; hủy sẽ kill uv.
    /// </param>
    /// <returns>
    /// <c>0</c> when the command succeeded, otherwise its exit code — its output is left in
    /// <see cref="LastOutputTail"/> for the retry/error UI (Review Focus #4).
    /// <c>0</c> khi lệnh thành công, ngược lại là exit code của lệnh đó — output của nó được giữ trong
    /// <see cref="LastOutputTail"/> cho UI retry/lỗi (Review Focus #4).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="flavor"/> has no dependency-group name — the runner refuses to guess which wheel to
    /// install. / <paramref name="flavor"/> không có tên dependency-group — runner không đoán wheel nào để cài.
    /// </exception>
    public async Task<int> SyncAsync(OcrFlavor flavor, CancellationToken ct)
    {
        LastOutputTail = null;

        string group = flavor switch
        {
            OcrFlavor.Gpu => GpuGroup,
            OcrFlavor.Cpu => CpuGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(flavor), flavor,
                "OcrFlavor không có dependency-group paddle tương ứng."),
        };

        (int syncExit, string syncTail) = await _runner
            .RunAsync(_uvExe, ["sync", "--no-default-groups", "--group", group, "--frozen"],
                _ocrDir, BuildUvEnvironment(), ct)
            .ConfigureAwait(false);
        if (syncExit != 0) LastOutputTail = syncTail;
        return syncExit;
    }

    /// <summary>
    /// Asks uv which paddle wheel the venv has and maps it to the installed flavor: <c>uv pip show
    /// paddlepaddle-gpu</c> exiting 0 → <see cref="OcrFlavor.Gpu"/>; otherwise <c>uv pip show
    /// paddlepaddle</c> exiting 0 → <see cref="OcrFlavor.Cpu"/>; otherwise null (neither wheel is installed,
    /// or uv could not answer at all, e.g. no venv yet) and the caller re-runs <see cref="SyncAsync"/>.
    /// Hỏi uv xem venv mang wheel paddle nào rồi ánh xạ sang flavor đã cài: <c>uv pip show
    /// paddlepaddle-gpu</c> exit 0 → <see cref="OcrFlavor.Gpu"/>; ngược lại <c>uv pip show paddlepaddle</c>
    /// exit 0 → <see cref="OcrFlavor.Cpu"/>; nếu vẫn không ⇒ null (không wheel nào, hoặc uv không trả lời
    /// được, vd chưa có venv) và caller chạy lại <see cref="SyncAsync"/>.
    /// </summary>
    /// <remarks>
    /// Detection reads EXIT CODES, never the captured text: <see cref="ProcessRunner"/> keeps only the last
    /// few KB of a command's output and a package list is alphabetical, so on a large venv the
    /// <c>paddlepaddle*</c> lines sit outside that window and a parsed list would wrongly report "not
    /// installed" and sync again for nothing (R55). Measured with the vendored uv 0.13.7: an installed
    /// package exits 0, a missing package exits 1, and a missing venv exits 1 too — so any non-zero answer
    /// means "not this flavor". Probing by package name (not by list output) also keeps <c>paddleocr</c> and
    /// the rest of the paddle family from counting.
    /// Detection đọc EXIT CODE, không đọc text đã bắt: <see cref="ProcessRunner"/> chỉ giữ vài KB cuối output
    /// của lệnh và danh sách package xếp theo bảng chữ cái, nên trên venv lớn các dòng <c>paddlepaddle*</c>
    /// nằm ngoài cửa sổ đó và việc parse danh sách sẽ báo sai "chưa cài" rồi sync lại vô ích (R55). Đo thật
    /// với uv 0.13.7 đi kèm: package đã cài exit 0, package thiếu exit 1, và thiếu venv cũng exit 1 — nên mọi
    /// exit khác 0 đều có nghĩa "không phải flavor này". Dò theo tên package cũng khiến <c>paddleocr</c> và
    /// cả họ paddle không bị tính nhầm.
    /// </remarks>
    /// <param name="ct">
    /// Cancellation token; defaults to none. / Token hủy; mặc định không hủy.
    /// </param>
    /// <returns>
    /// The installed flavor, or null when uv did not confirm a paddle wheel — the caller then treats the
    /// install as missing. / Flavor đã cài, hoặc null khi uv không xác nhận wheel paddle nào — caller coi như
    /// chưa cài.
    /// </returns>
    public async Task<OcrFlavor?> InstalledPaddleFlavorAsync(CancellationToken ct = default)
    {
        (int gpuExit, _) = await _runner
            .RunAsync(_uvExe, ["pip", "show", GpuWheelPackage], _ocrDir, BuildUvEnvironment(), ct)
            .ConfigureAwait(false);
        if (gpuExit == 0) return OcrFlavor.Gpu;

        (int cpuExit, _) = await _runner
            .RunAsync(_uvExe, ["pip", "show", PaddlePackage], _ocrDir, BuildUvEnvironment(), ct)
            .ConfigureAwait(false);
        return cpuExit == 0 ? OcrFlavor.Cpu : null;
    }

    /// <summary>
    /// Fresh environment instance for one uv call; never shared between calls.
    /// Môi trường mới cho từng lệnh uv; không dùng chung giữa các lần gọi.
    /// </summary>
    private static Dictionary<string, string> BuildUvEnvironment() =>
        new() { [UvPythonPreferenceVariable] = UvPythonPreferenceValue };
}
