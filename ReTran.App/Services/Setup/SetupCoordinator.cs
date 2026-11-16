using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Setup;

/// <summary>
/// Wizard step the first-run Setup is in (spec §4). <see cref="ConfirmingFlavorSwitch"/> is an
/// Amendment-A2 addition: the Syncing step pauses there when the installed paddle flavor differs from
/// the one the system check decided, until the user explicitly confirms replacing it.
/// Bước wizard chạy lần đầu (spec §4). <see cref="ConfirmingFlavorSwitch"/> là bước thêm theo
/// Amendment A2: bước Syncing dừng ở đó khi flavor paddle đang cài khác flavor mà check system đã
/// quyết định, cho tới khi user xác nhận thay thế.
/// </summary>
public enum SetupStep
{
    /// <summary>Running the hard system gates (arch, uv.exe, flavor, disk, network). Chạy các cổng hệ thống cứng (arch, uv.exe, flavor, đĩa, mạng).</summary>
    Checking,

    /// <summary>Waiting for the user to pick a language. Chờ user chọn ngôn ngữ.</summary>
    ChoosingLanguage,

    /// <summary>Waiting for the user to tick the OCR model tiers (at least one) and pick "Dùng chính". Chờ user tick các tier model OCR (ít nhất 1) và chọn "Dùng chính".</summary>
    ChoosingOcrTier,

    /// <summary>Waiting for the user to answer the extra-language question (tick any or none). Chờ user trả lời màn ngôn ngữ bổ sung (tick nhiều hoặc không).</summary>
    ChoosingExtraLanguages,

    /// <summary>Re-verifying the bundled uv.exe on disk (the AV-quarantine window). Kiểm tra lại uv.exe đi kèm trên đĩa (cửa sổ cách ly AV).</summary>
    VerifyingFiles,

    /// <summary>Syncing the sidecar venv for the chosen flavor. Sync môi trường sidecar theo flavor đã chọn.</summary>
    Syncing,

    /// <summary>Paused: the installed flavor differs from the checked one and the user must confirm the switch. Dừng: flavor đang cài khác flavor đã check, user phải xác nhận đổi.</summary>
    ConfirmingFlavorSwitch,

    /// <summary>Restarting Core and verifying the sidecar (ping, catalog, GPU capability). Khởi động lại Core và xác minh sidecar (ping, catalog, capability GPU).</summary>
    VerifyingSidecar,

    /// <summary>Downloading the llama.cpp runtime for the LLM provider (spec 2026-11-14 D13 — never bundled with the zip). Tải runtime llama.cpp cho provider LLM (spec 2026-11-14 D13 — không bao giờ nằm trong zip).</summary>
    DownloadingLlmRuntime,

    /// <summary>Downloading the recommended OCR model pair. Tải cặp model OCR được recommend.</summary>
    DownloadingModels,

    /// <summary>Writing the model pair into settings.json. Ghi cặp model vào settings.json.</summary>
    Applying,

    /// <summary>The wizard finished and persisted its completion. Wizard hoàn tất và đã lưu trạng thái xong.</summary>
    Done,

    /// <summary>A step failed; <see cref="SetupCoordinator.Error"/> carries the message. Một bước thất bại; <see cref="SetupCoordinator.Error"/> mang thông điệp lỗi.</summary>
    Failed,
}

/// <summary>
/// The Core-process seam the wizard drives: JSON-RPC calls forwarded to the sidecar plus a
/// kill+start restart of the whole core process. Task 10 adapts <c>CoreProcessClient</c> to it.
/// Seam tiến trình Core mà wizard điều khiển: các lời gọi JSON-RPC chuyển tới sidecar kèm khởi động
/// lại toàn bộ tiến trình core kiểu kill+start. Task 10 sẽ thích ứng <c>CoreProcessClient</c> vào đây.
/// </summary>
public interface ISetupCoreBridge
{
    /// <summary>
    /// Calls one Core/sidecar JSON-RPC method and returns its result node.
    /// Gọi một method JSON-RPC của Core/sidecar và trả về node kết quả.
    /// </summary>
    /// <param name="method">Method name, e.g. <c>sidecar.ping</c>. Tên method, vd <c>sidecar.ping</c>.</param>
    /// <param name="params">Method params, or <see langword="null"/>. Tham số method, hoặc <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    /// <returns>The response result node. Node kết quả của phản hồi.</returns>
    Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct);

    /// <summary>
    /// Disposes and starts the core process again (the existing kill+start path).
    /// Dispose và khởi động lại tiến trình core (đường kill+start có sẵn).
    /// </summary>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    Task RestartAsync(CancellationToken ct);
}

/// <summary>
/// The pending flavor switch shown while the wizard sits in
/// <see cref="SetupStep.ConfirmingFlavorSwitch"/>: what is installed on this machine and what the
/// system check decided to install.
/// Đổi flavor đang chờ trong khi wizard ở <see cref="SetupStep.ConfirmingFlavorSwitch"/>: bản đang
/// cài trên máy và bản mà check system quyết định sẽ cài.
/// </summary>
/// <param name="Installed">Flavor currently in the venv (per <c>uv pip show</c>). Flavor đang có trong venv (theo <c>uv pip show</c>).</param>
/// <param name="Target">Flavor the system check decided for this machine. Flavor mà check system quyết định cho máy này.</param>
public sealed record FlavorSwitchInfo(OcrFlavor Installed, OcrFlavor Target);

/// <summary>
/// First-run Setup wizard orchestration as a testable state machine (spec §3, §4): hard gates first,
/// the OCR-tier / language / extra-language answers once, then the pipeline VerifyingFiles → Syncing →
/// VerifyingSidecar →
/// DownloadingLlmRuntime → DownloadingModels → Applying → Done. Soft steps retry up to
/// <see cref="MaxRetries"/> times before failing; the hard
/// ones (Checking, VerifyingFiles) fail immediately. Progress persists in <c>setup.json</c> after each
/// successful step, so a restart resumes where it stopped and never re-asks the language. Per
/// Amendment A2: the Syncing step pauses for explicit user confirmation when a DIFFERENT paddle
/// flavor is already installed (no sync call may happen before that), and the VerifyingSidecar step
/// gates the GPU flavor on the sidecar-reported compute capability (≥ 7.5 — the cu129 wheels only
/// fatbin sm_75/80/86/89/120) with a CPU-switch entry point instead of a dead end. R59: a failed
/// <c>uv sync</c> may leave no venv at all, so every retry is a full re-sync and the error screen
/// quotes <see cref="UvSyncRunner.LastOutputTail"/>.
/// Điều phối wizard Setup chạy lần đầu dưới dạng máy trạng thái test được (spec §3, §4): cổng cứng
/// trước, ba câu trả lời wizard (tier OCR / ngôn ngữ / ngôn ngữ bổ sung) hỏi một lần, rồi pipeline
/// VerifyingFiles → Syncing → VerifyingSidecar →
/// DownloadingLlmRuntime → DownloadingModels → Applying → Done. Các bước mềm retry tới <see cref="MaxRetries"/> lần trước
/// khi fail; các bước cứng (Checking, VerifyingFiles) fail ngay. Tiến độ lưu vào <c>setup.json</c> sau
/// mỗi bước thành công, nên khởi động lại sẽ chạy tiếp đúng chỗ dừng và không hỏi lại ngôn ngữ. Theo
/// Amendment A2: bước Syncing dừng chờ user xác nhận RÕ RÀNG khi flavor paddle KHÁC đang được cài
/// (không lời gọi sync nào xảy ra trước đó), và bước VerifyingSidecar gate flavor GPU theo compute
/// capability mà sidecar báo (≥ 7.5 — wheel cu129 chỉ có fatbin sm_75/80/86/89/120) kèm entry point
/// chuyển CPU thay vì đường cụt. R59: <c>uv sync</c> fail có thể để lại không còn venv, nên mỗi lần
/// retry là full re-sync và màn hình lỗi trích <see cref="UvSyncRunner.LastOutputTail"/>.
/// </summary>
public sealed class SetupCoordinator
{
    /// <summary>
    /// Retries per soft step after the first attempt (4 attempts total). Hằng số theo spec §3.
    /// Số lần retry cho mỗi bước mềm sau lần thử đầu (tổng 4 lần thử). Hằng số theo spec §3.
    /// </summary>
    public const int MaxRetries = 3;

    // Compute capability floor for the GPU flavor, encoded major*10+minor: the measured cu129 wheels
    // carry fatbin sm_75/80/86/89/120 only (addendum §3). Sàn compute capability của flavor GPU, mã
    // hóa major*10+minor: wheel cu129 đo thật chỉ có fatbin sm_75/80/86/89/120 (addendum §3).
    private const int MinGpuComputeCapabilityCode = 75;

    private static readonly TimeSpan DefaultModelPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultModelDownloadTimeout = TimeSpan.FromMinutes(60);

    // Pipeline steps in order; Checking and the pre-pipeline answers (OCR tiers, language, extra
    // languages) run before it, Done after it.
    // Các bước pipeline theo thứ tự; Checking và các câu hỏi pre-pipeline (tier OCR, ngôn ngữ, ngôn
    // ngữ bổ sung) chạy trước, Done chạy sau.
    private static readonly SetupStep[] PipelineSteps =
    [
        SetupStep.VerifyingFiles,
        SetupStep.Syncing,
        SetupStep.VerifyingSidecar,
        SetupStep.DownloadingLlmRuntime,
        SetupStep.DownloadingModels,
        SetupStep.Applying,
    ];

    private readonly IVideoAdapterSource _adapterSource;
    private readonly Func<string, long> _diskFreeBytes;
    private readonly Func<string, bool> _isHostReachable;
    private readonly UvSyncRunner _uvSync;
    private readonly ISetupCoreBridge _bridge;
    private readonly AppSettingsService _settings;
    private readonly Action _closeApp;
    private readonly Func<SetupState> _stateFactory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, int>? _countModelEntries;
    private readonly Func<string, bool> _modelDirExists;
    private readonly TimeSpan _modelPollInterval;
    private readonly TimeSpan _modelDownloadTimeout;
    private readonly ILogger<SetupCoordinator>? _logger;

    private SetupState _state;
    private int _running; // 0 idle / 1 a run is in progress (Interlocked) — guards against double starts
    private SetupCheckItem[] _checks = []; // snapshot checklist Checking — thay nguyên khối mỗi event

    /// <summary>
    /// Creates the coordinator. Every dependency is injectable so the whole machine runs on fakes in
    /// tests; the optional seams default to production values (<c>SetupState.Load()</c>,
    /// <see cref="File.Exists"/>, 2 s poll, 60 min download window).
    /// Tạo coordinator. Mọi dependency đều inject được để cả máy trạng thái chạy trên fake trong test;
    /// các seam optional mặc định về giá trị production (<c>SetupState.Load()</c>,
    /// <see cref="File.Exists"/>, poll 2 s, cửa sổ tải 60 phút).
    /// </summary>
    /// <param name="adapterSource">Display-adapter inventory for the flavor decision. Nguồn danh sách display adapter cho quyết định flavor.</param>
    /// <param name="diskFreeBytes">Free bytes on the volume containing the given path (checked once with the app dir). Số byte trống trên ổ chứa path được đưa (check một lần với thư mục app).</param>
    /// <param name="isHostReachable">Short-timeout HTTPS reachability probe per host. Probe HTTPS tới được hay không, timeout ngắn, theo từng host.</param>
    /// <param name="uvSync">The uv sync runner (flavor → one frozen group sync; installed-flavor probe). Runner uv sync (flavor → một lệnh group sync --frozen; dò flavor đã cài).</param>
    /// <param name="bridge">Core/sidecar JSON-RPC seam. Seam JSON-RPC Core/sidecar.</param>
    /// <param name="settings">App settings service — the Applying step writes the model pair through it. Dịch vụ cài đặt app — bước Applying ghi cặp model qua đây.</param>
    /// <param name="closeApp">Called by <see cref="Skip"/> to close the app (spec §3.6). Được gọi bởi <see cref="Skip"/> để đóng app (spec §3.6).</param>
    /// <param name="stateFactory">Loads <c>setup.json</c>; defaults to <c>SetupState.Load()</c>. Tests point it at a temp path. Đọc <c>setup.json</c>; mặc định <c>SetupState.Load()</c>. Test trỏ sang path temp.</param>
    /// <param name="fileExists">File-existence probe for uv.exe (Checking + VerifyingFiles); defaults to <see cref="File.Exists"/>. Probe tồn tại file cho uv.exe (Checking + VerifyingFiles); mặc định <see cref="File.Exists"/>.</param>
    /// <param name="countModelEntries">Counts installed models for the Checking checklist; defaults to the real models directory. Đếm model đã cài cho checklist Checking; mặc định là thư mục models thật.</param>
    /// <param name="modelDirExists">File-system probe for one model's directory under the models cache, used by the rerun reconcile; defaults to <see cref="Directory.Exists"/>. Probe file-system cho thư mục của một model trong cache model, dùng bởi reconcile khi chạy lại; mặc định <see cref="Directory.Exists"/>.</param>
    /// <param name="modelPollInterval">Delay between <c>ocr.models.list</c> polls while models download; default 2 s. Khoảng cách giữa các lần poll <c>ocr.models.list</c> khi đang tải model; mặc định 2 s.</param>
    /// <param name="modelDownloadTimeout">Total window per download attempt; default 60 min. Cửa sổ tổng cho mỗi lượt tải; mặc định 60 phút.</param>
    /// <param name="logger">DI logger, or <see langword="null"/> to log nothing. Logger DI, hoặc <see langword="null"/> để không ghi log.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null. Một dependency bắt buộc là null.</exception>
    public SetupCoordinator(
        IVideoAdapterSource adapterSource,
        Func<string, long> diskFreeBytes,
        Func<string, bool> isHostReachable,
        UvSyncRunner uvSync,
        ISetupCoreBridge bridge,
        AppSettingsService settings,
        Action closeApp,
        Func<SetupState>? stateFactory = null,
        Func<string, bool>? fileExists = null,
        Func<string, int>? countModelEntries = null,
        Func<string, bool>? modelDirExists = null,
        TimeSpan? modelPollInterval = null,
        TimeSpan? modelDownloadTimeout = null,
        ILogger<SetupCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(adapterSource);
        ArgumentNullException.ThrowIfNull(diskFreeBytes);
        ArgumentNullException.ThrowIfNull(isHostReachable);
        ArgumentNullException.ThrowIfNull(uvSync);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(closeApp);

        _adapterSource = adapterSource;
        _diskFreeBytes = diskFreeBytes;
        _isHostReachable = isHostReachable;
        _uvSync = uvSync;
        _bridge = bridge;
        _settings = settings;
        _closeApp = closeApp;
        _stateFactory = stateFactory ?? (static () => SetupState.Load());
        _fileExists = fileExists ?? File.Exists;
        _countModelEntries = countModelEntries;
        _modelDirExists = modelDirExists ?? Directory.Exists;
        _modelPollInterval = modelPollInterval ?? DefaultModelPollInterval;
        _modelDownloadTimeout = modelDownloadTimeout ?? DefaultModelDownloadTimeout;
        _logger = logger;
        _state = _stateFactory();

        // A fresh launch on an already-set-up machine must not leave the singleton stuck at Checking:
        // SetupModal's cheap path (NeedsSetup false) never calls StartAsync, and both the pipeline gate
        // and the Settings recovery section ("Chạy lại setup") read IsComplete off Step. Initialization
        // only — no Publish, because no subscribers exist at construction time.
        // Máy đã setup xong ở phiên trước khởi động lại không được để singleton kẹt ở Checking: đường rẻ
        // của SetupModal (NeedsSetup false) không bao giờ gọi StartAsync, và cả gate pipeline lẫn section
        // khôi phục trong Settings ("Chạy lại setup") đều đọc IsComplete từ Step. Chỉ là khởi tạo — không
        // Publish vì thời điểm construct chưa có subscriber nào.
        if (_state.IsComplete) Step = SetupStep.Done;
    }

    /// <summary>
    /// Current wizard step; starts at <see cref="SetupStep.Done"/> when the persisted state is already
    /// complete (a fresh launch on a set-up machine), otherwise at <see cref="SetupStep.Checking"/> before
    /// the first <see cref="StartAsync"/>. Bước wizard hiện tại; bắt đầu ở <see cref="SetupStep.Done"/> khi
    /// trạng thái đã lưu hoàn tất (khởi động mới trên máy đã setup), nếu không thì ở
    /// <see cref="SetupStep.Checking"/> trước lần <see cref="StartAsync"/> đầu.
    /// </summary>
    public SetupStep Step { get; private set; } = SetupStep.Checking;

    /// <summary>
    /// Localizable message of the current failure (the uv output tail travels as a format arg for
    /// sync failures); <see langword="null"/> outside <see cref="SetupStep.Failed"/>. The modal
    /// resolves it through <c>I18nService</c> in the current UI language.
    /// Thông điệp lỗi bản địa hoá được của lượt fail hiện tại (đuôi output uv đi kèm như tham số
    /// format với lỗi sync); <see langword="null"/> khi không ở <see cref="SetupStep.Failed"/>. Modal
    /// resolve qua <c>I18nService</c> theo ngôn ngữ UI hiện hành.
    /// </summary>
    public SetupMessage? Error { get; private set; }

    /// <summary>
    /// Advisory message from the system check (GPU→CPU downgrade reasons) or the pending
    /// flavor-switch explanation, kept for the UI to display; resolved via i18n like
    /// <see cref="Error"/>. Notice advisory từ check system (lý do hạ GPU→CPU) hoặc giải thích đổi
    /// flavor đang chờ, giữ cho UI hiển thị; resolve qua i18n như <see cref="Error"/>.
    /// </summary>
    public SetupMessage? Notice { get; private set; }

    /// <summary>
    /// Snapshot of the Checking checklist (latest event per Id, in display order); empty before the
    /// first run. Every event swaps the whole array, so a UI thread reading while the background run
    /// updates never sees a torn list; reset at the start of every <c>RunCheckingStep</c>.
    /// Snapshot checklist của bước Checking (mục mới nhất theo Id, đúng thứ tự hiển thị); rỗng trước
    /// lượt chạy đầu. Mỗi event thay nguyên mảng nên luồng UI đọc trong lúc thread nền cập nhật không
    /// bao giờ thấy danh sách bị xé; reset ở đầu mỗi <c>RunCheckingStep</c>.
    /// </summary>
    public IReadOnlyList<SetupCheckItem> Checks => _checks;

    /// <summary>
    /// Wheel flavor the wizard is installing (decided by Checking, or switched to CPU via
    /// <see cref="SwitchToCpuFlavorAsync"/>). Flavor wheel wizard đang cài (do Checking quyết định, hoặc đã chuyển sang CPU qua <see cref="SwitchToCpuFlavorAsync"/>).
    /// </summary>
    public OcrFlavor Flavor { get; private set; }

    /// <summary>
    /// Tier chosen as "Dùng chính" on the OCR-model screen (<c>tiny</c>/<c>small</c>/<c>medium</c>);
    /// <see langword="null"/> until the user answers. The language screen uses it to display the pair
    /// the wizard will run. Tier được chọn "Dùng chính" ở màn chọn model OCR; <see langword="null"/>
    /// khi chưa trả lời. Màn ngôn ngữ dùng nó để hiển thị cặp model wizard sẽ chạy.
    /// </summary>
    public string? ActiveTier => _state.ActiveTier;

    /// <summary>
    /// True once the wizard reached <see cref="SetupStep.Done"/>. Đúng khi wizard đã tới <see cref="SetupStep.Done"/>.
    /// </summary>
    public bool IsComplete => Step == SetupStep.Done;

    /// <summary>
    /// True while any coordinator operation (<see cref="StartAsync"/>, <see cref="ContinueWithOcrTierAsync"/>,
    /// <see cref="ContinueWithLanguageAsync"/>, <see cref="ContinueWithExtraLanguagesAsync"/>,
    /// <see cref="RetryAsync"/>, <see cref="ConfirmFlavorSwitchAsync"/>, <see cref="SwitchToCpuFlavorAsync"/>) is
    /// in flight; false once the run finishes, pauses for user input (language, flavor switch), or fails.
    /// Exposed so callers can tell "complete but re-running" apart from "complete and idle": during a forced
    /// re-run <see cref="Step"/> still holds <see cref="SetupStep.Done"/> until the first publish, so
    /// <see cref="IsComplete"/> alone cannot close that window.
    /// Đúng khi một thao tác của coordinator (<see cref="StartAsync"/>, <see cref="ContinueWithOcrTierAsync"/>,
    /// <see cref="ContinueWithLanguageAsync"/>, <see cref="ContinueWithExtraLanguagesAsync"/>,
    /// <see cref="RetryAsync"/>, <see cref="ConfirmFlavorSwitchAsync"/>, <see cref="SwitchToCpuFlavorAsync"/>)
    /// đang chạy; false khi lượt chạy kết thúc, dừng chờ user nhập liệu (ngôn ngữ, đổi flavor), hoặc fail.
    /// Mở ra để caller phân biệt "xong nhưng đang chạy lại" với "xong và rảnh": trong lúc "chạy lại setup",
    /// <see cref="Step"/> vẫn giữ <see cref="SetupStep.Done"/> cho tới publish đầu tiên, nên
    /// <see cref="IsComplete"/> đơn lẻ không đủ để chặn cửa sổ đó.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// True once the user chose "skip setup for this session" on the failure popup: the wizard stays
    /// hidden and the pipeline gate opens WITHOUT the setup being complete — nothing is persisted, so
    /// the next launch shows the wizard again. Cleared when an explicit run starts ("re-run setup").
    /// Đúng khi user chọn "bỏ qua thiết lập cho phiên này" trên popup màn lỗi: wizard ẩn và cổng pipeline
    /// mở dù setup chưa hoàn tất — không ghi state nào, nên lần mở app sau wizard hiện lại. Cờ được xóa
    /// khi một lượt chạy tường minh bắt đầu (đường "chạy lại setup").
    /// </summary>
    public bool SkippedForSession { get; private set; }

    /// <summary>
    /// The pending flavor switch while in <see cref="SetupStep.ConfirmingFlavorSwitch"/> (installed vs
    /// target); <see langword="null"} in every other step. Đổi flavor đang chờ khi ở <see cref="SetupStep.ConfirmingFlavorSwitch"/> (bản đã cài so với bản đích); <see langword="null"} ở mọi bước khác.
    /// </summary>
    public FlavorSwitchInfo? PendingFlavorSwitch { get; private set; }

    /// <summary>
    /// True on the CC-gate failure screen only: the GPU flavor is unsupported by this card, so the UI
    /// offers <see cref="SwitchToCpuFlavorAsync"/> as the forward action. Đúng duy nhất ở màn lỗi cổng capability: bản GPU không được card này hỗ trợ, UI đề nghị <see cref="SwitchToCpuFlavorAsync"/> là hành động tiếp theo.
    /// </summary>
    public bool CanSwitchToCpuFlavor { get; private set; }

    /// <summary>
    /// Raised after EVERY state change (step, error, notice, flavor, pending switch). The UI marshals
    /// it to its own thread, like <c>UpdateModal.OnChanged</c>. Được phát sau MỌI thay đổi trạng thái
    /// (bước, lỗi, notice, flavor, đổi đang chờ). UI tự marshal sang luồng của nó, như
    /// <c>UpdateModal.OnChanged</c>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Starts the wizard: re-runs the system check (it is pure and cheap, and the flavor must follow
    /// the CURRENT hardware), pauses at the first unanswered pre-pipeline question (OCR tiers →
    /// language → extra languages), then runs the pipeline from the first not-done step. A completed
    /// state short-circuits straight to <see cref="SetupStep.Done"/> unless <paramref name="force"/> is
    /// set (the "re-run setup" path — done steps are still skipped, so an unchanged machine passes
    /// through in seconds).
    /// Khởi động wizard: chạy lại check system (thuần và rẻ, flavor phải theo phần cứng HIỆN TẠI), dừng
    /// ở câu hỏi pre-pipeline đầu tiên chưa được trả lời (tier OCR → ngôn ngữ → ngôn ngữ bổ sung), rồi
    /// chạy pipeline từ bước chưa done đầu tiên. State đã xong thì nhảy thẳng tới
    /// <see cref="SetupStep.Done"/> trừ khi đặt <paramref name="force"/> (đường "chạy lại setup" — bước
    /// đã done vẫn được skip, nên máy không đổi gì sẽ đi qua trong vài giây).
    /// </summary>
    /// <param name="force">Bypasses the <c>IsComplete</c> short-circuit. Bỏ qua nhánh tắt <c>IsComplete</c>.</param>
    /// <param name="ct">Cancellation token; cancelling aborts the run (state is left where it was). Token hủy; hủy sẽ dừng lượt chạy (state giữ nguyên ở chỗ nó đang ở).</param>
    public async Task StartAsync(bool force = false, CancellationToken ct = default)
    {
        if (!TryBeginRun()) return;
        try
        {
            // An explicit run (the modal's first-run trigger or the Settings re-run) revokes the
            // session skip: after "skip setup", the way back IS this run, and the wizard must be
            // visible again while it runs.
            // Một lượt chạy tường minh (trigger first-run của modal hoặc rerun từ Settings) thu hồi cờ
            // skip phiên: sau "bỏ qua thiết lập", đường quay lại CHÍNH LÀ lượt chạy này, và wizard phải
            // hiện lại trong lúc nó chạy.
            if (SkippedForSession)
            {
                SkippedForSession = false;
                Publish();
            }

            _state = _stateFactory();
            if (!force && _state.IsComplete)
            {
                Step = SetupStep.Done;
                Publish();
                return;
            }

            StepOutcome outcome = RunCheckingStep();
            if (outcome == StepOutcome.Failed)
            {
                EnterFailed(SetupStep.Checking);
                return;
            }

            if (_state.Tiers is null)
            {
                Step = SetupStep.ChoosingOcrTier;
                Publish();
                return;
            }

            await ContinueAfterAnswersAsync(ct);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Continues after the user answered the language question: persists the code, marks the step done,
    /// and resumes — the extra-language question when unanswered, otherwise the remaining pipeline. A
    /// no-op when not in <see cref="SetupStep.ChoosingLanguage"/>.
    /// Tiếp tục sau khi user trả lời câu hỏi ngôn ngữ: lưu mã, đánh dấu bước xong, rồi chạy tiếp — màn
    /// ngôn ngữ bổ sung nếu chưa hỏi, còn lại là phần pipeline. Không làm gì nếu không ở
    /// <see cref="SetupStep.ChoosingLanguage"/>.
    /// </summary>
    /// <param name="langCode">Non-empty language code the user picked (e.g. <c>vi</c>). Mã ngôn ngữ user chọn, khác rỗng (vd <c>vi</c>).</param>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    /// <exception cref="ArgumentException"><paramref name="langCode"/> is null or empty. <paramref name="langCode"/> null hoặc rỗng.</exception>
    public async Task ContinueWithLanguageAsync(string langCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(langCode);
        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.ChoosingLanguage) return;

            _state = _state with
            {
                Language = langCode,
                Steps = WithStep(_state.Steps, StepKey(SetupStep.ChoosingLanguage), "done"),
            };
            _state.Save();

            await ContinueAfterAnswersAsync(ct);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Continues after the user answered the OCR-model screen: validates the ticked tiers and the
    /// "Dùng chính" choice, persists them, marks the step done, and resumes at the next unanswered
    /// question (language, then extra languages) or the pipeline. A no-op when not in
    /// <see cref="SetupStep.ChoosingOcrTier"/>.
    /// Tiếp tục sau khi user trả lời màn chọn model OCR: kiểm tra các tier đã tick và lựa chọn "Dùng
    /// chính", lưu lại, đánh dấu bước xong, rồi chạy tiếp ở câu hỏi chưa trả lời kế (ngôn ngữ, rồi
    /// ngôn ngữ bổ sung) hoặc pipeline. Không làm gì nếu không ở <see cref="SetupStep.ChoosingOcrTier"/>.
    /// </summary>
    /// <param name="tiers">Ticked tier names — at least one of <c>tiny</c>/<c>small</c>/<c>medium</c>. Tên các tier đã tick — ít nhất một trong <c>tiny</c>/<c>small</c>/<c>medium</c>.</param>
    /// <param name="activeTier">The "Dùng chính" tier; must be one of <paramref name="tiers"/>. Tier "Dùng chính"; phải nằm trong <paramref name="tiers"/>.</param>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    /// <exception cref="ArgumentException">The tier list is empty, holds an unknown tier, or <paramref name="activeTier"/> is not ticked. Danh sách tier rỗng, chứa tier lạ, hoặc <paramref name="activeTier"/> không nằm trong các tier đã tick.</exception>
    public async Task ContinueWithOcrTierAsync(IReadOnlyList<string> tiers, string activeTier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentException.ThrowIfNullOrEmpty(activeTier);
        if (tiers.Count == 0)
            throw new ArgumentException("Cần tick ít nhất một tier model OCR.", nameof(tiers));
        foreach (string tier in tiers)
        {
            if (!OcrModelChoices.Tiers.Contains(tier, StringComparer.Ordinal))
                throw new ArgumentException($"Tier model OCR không hợp lệ: '{tier}'.", nameof(tiers));
        }
        if (!tiers.Contains(activeTier, StringComparer.Ordinal))
            throw new ArgumentException("Tier \"Dùng chính\" phải nằm trong các tier đã tick.", nameof(activeTier));

        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.ChoosingOcrTier) return;

            _state = _state with
            {
                Tiers = [.. tiers],
                ActiveTier = activeTier,
                Steps = WithStep(_state.Steps, StepKey(SetupStep.ChoosingOcrTier), "done"),
            };
            _state.Save();
            ReconcileMarkers();

            await ContinueAfterAnswersAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Continues after the user answered the extra-language screen: validates the codes (normalized to
    /// lowercase, deduplicated), persists them, marks the step done, and runs the remaining pipeline.
    /// An empty list is a valid answer ("none"). A no-op when not in
    /// <see cref="SetupStep.ChoosingExtraLanguages"/>.
    /// Tiếp tục sau khi user trả lời màn ngôn ngữ bổ sung: kiểm tra mã (chuẩn hoá chữ thường, bỏ trùng),
    /// lưu lại, đánh dấu bước xong, rồi chạy phần pipeline còn lại. List rỗng là câu trả lời hợp lệ
    /// ("không chọn"). Không làm gì nếu không ở <see cref="SetupStep.ChoosingExtraLanguages"/>.
    /// </summary>
    /// <param name="extraLangs">Ticked extra-language codes (any subset of <see cref="OcrModelChoices.LegacyLanguageCodes"/>), possibly empty. Mã ngôn ngữ bổ sung đã tick (tập con bất kỳ của <see cref="OcrModelChoices.LegacyLanguageCodes"/>), có thể rỗng.</param>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    /// <exception cref="ArgumentException">A code is not one of the legacy-only languages. Một mã không thuộc nhóm ngôn ngữ chỉ có rec legacy.</exception>
    public async Task ContinueWithExtraLanguagesAsync(IReadOnlyList<string> extraLangs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extraLangs);
        var picks = new List<string>();
        foreach (string lang in extraLangs)
        {
            string code = lang?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!OcrModelChoices.LegacyLanguageCodes.Contains(code, StringComparer.Ordinal))
                throw new ArgumentException($"Ngôn ngữ bổ sung không hợp lệ: '{lang}'.", nameof(extraLangs));
            if (!picks.Contains(code, StringComparer.Ordinal)) picks.Add(code);
        }

        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.ChoosingExtraLanguages) return;

            _state = _state with
            {
                ExtraLangs = picks,
                Steps = WithStep(_state.Steps, StepKey(SetupStep.ChoosingExtraLanguages), "done"),
            };
            _state.Save();
            ReconcileMarkers();

            await RunPipelineAsync(SetupStep.VerifyingFiles, ct).ConfigureAwait(false);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Confirms the pending flavor switch (the user pressed "Đổi sang bản mới"): syncs the checked
    /// flavor — replacing what is installed — and continues the pipeline. A no-op when not in
    /// <see cref="SetupStep.ConfirmingFlavorSwitch"/>; NO sync call ever happens before this method.
    /// Xác nhận đổi flavor đang chờ (user bấm "Đổi sang bản mới"): sync flavor đã check — thay thế bản
    /// đang cài — rồi tiếp tục pipeline. Không làm gì nếu không ở <see cref="SetupStep.ConfirmingFlavorSwitch"/>;
    /// KHÔNG có lời gọi sync nào xảy ra trước method này.
    /// </summary>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    public async Task ConfirmFlavorSwitchAsync(CancellationToken ct = default)
    {
        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.ConfirmingFlavorSwitch) return;

            // The multi-minute sync below must show as Syncing — never as ConfirmingFlavorSwitch with a
            // null PendingFlavorSwitch, which would contradict the documented invariant of both.
            PendingFlavorSwitch = null;
            Step = SetupStep.Syncing;
            Publish();
            _logger?.LogInformation("setup: user xác nhận đổi flavor sang {Flavor}", Flavor);

            StepOutcome outcome = await RunSyncLoopAsync(ct).ConfigureAwait(false);
            if (outcome == StepOutcome.Failed)
            {
                EnterFailed(SetupStep.Syncing);
                return;
            }

            await RunPipelineAsync(SetupStep.VerifyingSidecar, ct).ConfigureAwait(false);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// CC-gate entry point (addendum §3): after the GPU flavor failed because this card's compute
    /// capability is below 7.5, switches to the CPU flavor and loops back into Syncing — the user does
    /// not have to find a "re-run setup" button. The switch itself is the confirmation for replacing
    /// the installed wheel, so no second pause happens. A no-op unless
    /// <see cref="CanSwitchToCpuFlavor"/> is set.
    /// Entry point cổng capability (addendum §3): sau khi bản GPU fail vì compute capability của card
    /// dưới 7.5, chuyển sang flavor CPU và vòng lại bước Syncing — user không phải tự tìm nút "chạy lại
    /// setup". Bản thân hành động chuyển này là sự xác nhận thay thế wheel đang cài, nên không có lần
    /// dừng thứ hai. Không làm gì trừ khi <see cref="CanSwitchToCpuFlavor"/> đang bật.
    /// </summary>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    public async Task SwitchToCpuFlavorAsync(CancellationToken ct = default)
    {
        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.Failed || !CanSwitchToCpuFlavor) return;

            Flavor = OcrFlavor.Cpu;
            _state = _state with { Flavor = FlavorName(OcrFlavor.Cpu) };
            _state.Save();
            // The multi-minute sync below must show as Syncing — never as Failed with a null Error,
            // which would contradict the documented invariant of both.
            Step = SetupStep.Syncing;
            Error = null;
            CanSwitchToCpuFlavor = false;
            Publish();
            _logger?.LogInformation("setup: chuyển sang flavor CPU theo yêu cầu user (cổng capability)");

            StepOutcome outcome = await RunSyncLoopAsync(ct).ConfigureAwait(false);
            if (outcome == StepOutcome.Failed)
            {
                EnterFailed(SetupStep.Syncing);
                return;
            }

            await RunPipelineAsync(SetupStep.VerifyingSidecar, ct).ConfigureAwait(false);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Re-runs the step that is currently Failed, with its attempt counter restarted from zero. A no-
    /// op when not in <see cref="SetupStep.Failed"/>. On the CC-gate failure the retry will fail again
    /// (the hardware cannot change mid-run) — use <see cref="SwitchToCpuFlavorAsync"/> there instead.
    /// Chạy lại bước đang Failed, bộ đếm retry khởi động lại từ đầu. Không làm gì nếu không ở
    /// <see cref="SetupStep.Failed"/>. Với lỗi cổng capability thì retry sẽ fail lại (phần cứng không đổi
    /// giữa chừng) — hãy dùng <see cref="SwitchToCpuFlavorAsync"/> ở đó.
    /// </summary>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    public async Task RetryAsync(CancellationToken ct = default)
    {
        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.Failed) return;

            Error = null;
            CanSwitchToCpuFlavor = false;
            Publish();

            // Checking sits BEFORE the pipeline, so a retry of it must re-run the gate itself (and may
            // pause at ChoosingLanguage again when no language was ever answered). The pipeline then
            // starts from its FIRST step — _lastFailedStep is not a pipeline step and must never be
            // passed to RunPipelineAsync (Array.IndexOf would return -1 for it).
            if (_lastFailedStep == SetupStep.Checking)
            {
                StepOutcome outcome = RunCheckingStep();
                if (outcome == StepOutcome.Failed)
                {
                    EnterFailed(SetupStep.Checking);
                    return;
                }

                if (_state.Tiers is null)
                {
                    Step = SetupStep.ChoosingOcrTier;
                    Publish();
                    return;
                }

                await ContinueAfterAnswersAsync(ct).ConfigureAwait(false);
                return;
            }

            await RunPipelineAsync(_lastFailedStep, ct).ConfigureAwait(false);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Gives up: closes the app via the injected callback (spec §3.6). Writes no state at all — the
    /// next launch simply resumes from the last persisted step. Since 2026-11-13 this is the popup's
    /// "Exit ReTran" choice: the "Skip" button on the failure screen opens the confirmation popup
    /// instead of calling this directly.
    /// Bỏ qua: đóng app qua callback được inject (spec §3.6). Không ghi thêm state gì — lần khởi động kế
    /// tiếp chỉ việc chạy lại từ bước đã lưu cuối. Từ 2026-11-13 đây là lựa chọn "Thoát ReTran" của
    /// popup: nút "Bỏ qua" trên màn lỗi mở popup xác nhận thay vì gọi thẳng vào đây.
    /// </summary>
    public void Skip() => _closeApp();

    /// <summary>
    /// The popup's "skip setup" choice: hides the wizard for the rest of the session and lets the
    /// pipeline start, without closing the app and without writing any state.
    /// Lựa chọn "bỏ qua thiết lập" của popup: ẩn wizard tới hết phiên và cho pipeline start, không đóng
    /// app và không ghi state nào.
    /// </summary>
    public void SkipForSession()
    {
        SkippedForSession = true;
        Publish();
    }

    // ── the pipeline ───────────────────────────────────────────────────────────────────────────────

    private enum StepOutcome { Done, Failed, Paused }

    private SetupStep _lastFailedStep = SetupStep.Checking;

    /// <summary>
    /// Runs <paramref name="fromStep"/> and every later pipeline step that is not yet done in
    /// <c>setup.json</c>; stops on a pause (flavor switch) or failure, finishes with Done.
    /// Chạy <paramref name="fromStep"/> và mọi bước pipeline sau chưa done trong <c>setup.json</c>;
    /// dừng khi gặp bước pause (đổi flavor) hoặc fail, kết thúc bằng Done.
    /// </summary>
    private async Task RunPipelineAsync(SetupStep fromStep, CancellationToken ct)
    {
        for (int i = Array.IndexOf(PipelineSteps, fromStep); i < PipelineSteps.Length; i++)
        {
            SetupStep step = PipelineSteps[i];
            if (IsStepDone(step)) continue;

            StepOutcome outcome = await RunStepAsync(step, ct).ConfigureAwait(false);
            if (outcome != StepOutcome.Done) return; // Paused or Failed — state already set by the step
        }

        EnterDone();
    }

    /// <summary>
    /// Resumes after a pre-pipeline answer was saved: pauses at the next unanswered question
    /// (language, then extra languages), or runs the pipeline from its first step when everything is
    /// answered.
    /// Chạy tiếp sau khi một câu trả lời pre-pipeline được lưu: dừng ở câu hỏi chưa trả lời kế
    /// (ngôn ngữ, rồi ngôn ngữ bổ sung), hoặc chạy pipeline từ bước đầu khi đã trả lời hết.
    /// </summary>
    private async Task ContinueAfterAnswersAsync(CancellationToken ct)
    {
        if (_state.Language is null)
        {
            Step = SetupStep.ChoosingLanguage;
            Publish();
            return;
        }

        if (_state.ExtraLangs is null)
        {
            Step = SetupStep.ChoosingExtraLanguages;
            Publish();
            return;
        }

        await RunPipelineAsync(SetupStep.VerifyingFiles, ct).ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunStepAsync(SetupStep step, CancellationToken ct)
    {
        Step = step;
        Publish();
        _logger?.LogInformation("setup: bước {Step}", step);

        StepOutcome outcome = step switch
        {
            SetupStep.VerifyingFiles => RunVerifyingFilesStep(),
            SetupStep.Syncing => await RunSyncStepAsync(ct).ConfigureAwait(false),
            SetupStep.VerifyingSidecar => await RunVerifySidecarStepAsync(ct).ConfigureAwait(false),
            SetupStep.DownloadingLlmRuntime => await RunDownloadLlmRuntimeStepAsync(ct).ConfigureAwait(false),
            SetupStep.DownloadingModels => await RunDownloadModelsStepAsync(ct).ConfigureAwait(false),
            SetupStep.Applying => RunApplyingStep(),
            _ => throw new InvalidOperationException($"Không phải bước pipeline: {step}"),
        };

        if (outcome == StepOutcome.Failed) EnterFailed(step);
        return outcome;
    }

    // ── step implementations ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checking (hard, no retry): runs <see cref="SystemCheck.Run"/> with the injected seams. On
    /// success it records the flavor + notice and invalidates every downstream done-marker when the
    /// DECIDED flavor changed since the last run (new card/driver, or CPU→GPU after a driver update) —
    /// those artifacts belong to the old wheel.
    /// Checking (cứng, không retry): chạy <see cref="SystemCheck.Run"/> với các seam đã inject. Thành
    /// công thì ghi flavor + notice và vô hiệu mọi marker done phía sau khi flavor ĐÃ QUYẾT ĐỊNH đổi so
    /// với lần chạy trước (card/driver mới, hoặc CPU→GPU sau khi cập nhật driver) — các artifact đó
    /// thuộc về wheel cũ.
    /// </summary>
    private StepOutcome RunCheckingStep()
    {
        // Reset danh sách checklist: lượt chạy lại phải sạch, không dính trạng thái của lượt trước.
        // Reset the checklist: a re-run must start clean, never keeping the previous run's states.
        _checks = [];
        SystemCheckResult result = SystemCheck.Run(
            _adapterSource, _diskFreeBytes, _isHostReachable, _fileExists,
            progress: OnCheckProgress, countModelEntries: _countModelEntries);
        if (!result.Ok)
        {
            Error = result.FailureReason;
            return StepOutcome.Failed;
        }

        Flavor = result.Flavor;
        Notice = result.Notice;

        if (TryParseFlavor(_state.Flavor) is { } previous && previous != result.Flavor)
            ClearPipelineSteps();

        _state = _state with
        {
            Flavor = FlavorName(result.Flavor),
            Steps = WithStep(_state.Steps, StepKey(SetupStep.Checking), "done"),
        };
        _state.Save();
        ReconcileMarkers();
        return StepOutcome.Done;
    }

    /// <summary>
    /// Folds one checklist event into <see cref="Checks"/> (replace by Id; a new Id appends — the
    /// Pending batch always arrives first, so replace is the normal path) and publishes. The array is
    /// swapped wholesale so a UI read during the background run can never tear.
    /// Ghép một sự kiện checklist vào <see cref="Checks"/> (thay theo Id; id mới thì append — batch
    /// Pending luôn đến trước nên đường thường là replace) và publish. Mảng được thay nguyên khối để
    /// lượt đọc của UI trong lúc thread nền chạy không bao giờ bị xé.
    /// </summary>
    private void OnCheckProgress(SetupCheckItem item)
    {
        var updated = new List<SetupCheckItem>(_checks);
        int index = updated.FindIndex(existing => existing.Id == item.Id);
        if (index >= 0) updated[index] = item;
        else updated.Add(item);
        _checks = [.. updated];
        Publish();
    }

    /// <summary>
    /// VerifyingFiles (hard, no retry): uv.exe must still be on disk right before syncing — the window
    /// where AV/SmartScreen quarantines it between Checking and Syncing.
    /// VerifyingFiles (cứng, không retry): uv.exe phải còn trên đĩa ngay trước khi sync — cửa sổ mà
    /// AV/SmartScreen có thể cách ly nó giữa Checking và Syncing.
    /// </summary>
    private StepOutcome RunVerifyingFilesStep()
    {
        if (!_fileExists(SetupPaths.UvExe))
        {
            Error = SetupMessage.Of("setup.fail.uvMissing", SetupPaths.UvExe);
            return StepOutcome.Failed;
        }

        MarkStepDone(SetupStep.VerifyingFiles);
        return StepOutcome.Done;
    }

    /// <summary>
    /// Syncing entry (Amendment A2): probes the installed paddle flavor first. Nothing installed →
    /// straight to the sync loop; a DIFFERENT flavor installed → pause in
    /// <see cref="SetupStep.ConfirmingFlavorSwitch"/> until the user confirms (no sync before that);
    /// the SAME flavor installed → fast path, mark done without any uv call ("re-run setup" smoke #8 —
    /// an exit-0 <c>uv pip show</c> already proves the venv carries this wheel).
    /// Vào bước Syncing (Amendment A2): dò flavor paddle đã cài trước. Chưa cài gì → thẳng vào vòng
    /// sync; đang cài flavor KHÁC → dừng ở <see cref="SetupStep.ConfirmingFlavorSwitch"/> cho tới khi
    /// user xác nhận (không sync trước đó); đã cài ĐÚNG flavor → đường nhanh, đánh dấu done không gọi uv
    /// nào ("chạy lại setup" smoke #8 — <c>uv pip show</c> exit 0 đã chứng minh venv mang wheel này).
    /// </summary>
    private async Task<StepOutcome> RunSyncStepAsync(CancellationToken ct)
    {
        OcrFlavor? installed = await _uvSync.InstalledPaddleFlavorAsync(ct).ConfigureAwait(false);

        if (installed is { } current && current != Flavor)
        {
            EnterFlavorSwitchPause(current);
            return StepOutcome.Paused;
        }

        if (installed == Flavor)
        {
            MarkStepDone(SetupStep.Syncing);
            return StepOutcome.Done;
        }

        return await RunSyncLoopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The sync loop: up to <see cref="MaxRetries"/> + 1 full re-syncs of the current flavor (R59 — a
    /// failed <c>uv sync</c> can leave no venv at all, so there is no resume/patch mode). On success it
    /// persists the flavor and marks the step done; on exhaustion it sets <see cref="Error"/> with the
    /// failing command's output tail.
    /// Vòng sync: tối đa <see cref="MaxRetries"/> + 1 lần full re-sync flavor hiện tại (R59 —
    /// <c>uv sync</c> fail có thể để lại không còn venv, nên không có chế độ "sửa tiếp"). Thành công thì
    /// lưu flavor và đánh dấu bước xong; hết lượt thì đặt <see cref="Error"/> kèm đuôi output của lệnh
    /// fail.
    /// </summary>
    private async Task<StepOutcome> RunSyncLoopAsync(CancellationToken ct)
    {
        int lastExit = 0;
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            lastExit = await _uvSync.SyncAsync(Flavor, ct).ConfigureAwait(false);
            if (lastExit == 0)
            {
                MarkStepDone(SetupStep.Syncing);
                return StepOutcome.Done;
            }

            _logger?.LogWarning("setup: uv sync flavor {Flavor} lần {Attempt}/{Total} exit {Exit}",
                Flavor, attempt, MaxRetries + 1, lastExit);
        }

        string? tail = _uvSync.LastOutputTail;
        string attempts = (MaxRetries + 1).ToString();
        Error = string.IsNullOrWhiteSpace(tail)
            ? SetupMessage.Of("setup.fail.sync", attempts, lastExit.ToString())
            : SetupMessage.Of("setup.fail.syncTail", attempts, lastExit.ToString(), tail!);
        return StepOutcome.Failed;
    }

    /// <summary>
    /// VerifyingSidecar (addendum §3): restarts Core (kill+start), requires the sidecar ping and a
    /// model catalog, then — GPU flavor only — calls <c>sidecar.device</c> and fails on a null or
    /// below-7.5 capability with the CC-gate message plus the CPU-switch entry point. The CC gate is
    /// deterministic (the card cannot change mid-run), so it does NOT retry; transient bridge failures
    /// do, up to <see cref="MaxRetries"/> times. CPU flavor: ping + catalog are enough.
    /// VerifyingSidecar (addendum §3): khởi động lại Core (kill+start), yêu cầu sidecar trả lời ping và
    /// có catalog model, rồi — chỉ với flavor GPU — gọi <c>sidecar.device</c> và fail khi capability
    /// null hoặc dưới 7.5 kèm thông điệp cổng CC + entry point chuyển CPU. Cổng CC là tất định (card
    /// không đổi giữa chừng) nên KHÔNG retry; lỗi bridge thoáng qua thì retry tới
    /// <see cref="MaxRetries"/> lần. Flavor CPU: ping + catalog là đủ.
    /// </summary>
    private async Task<StepOutcome> RunVerifySidecarStepAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            try
            {
                await _bridge.RestartAsync(ct).ConfigureAwait(false);
                if (!await IsSidecarAliveAsync(ct).ConfigureAwait(false)) continue;

                if (Flavor == OcrFlavor.Gpu)
                {
                    JsonNode? device = await _bridge.CallAsync("sidecar.device", null, ct).ConfigureAwait(false);
                    (bool known, int major, int minor) = TryParseCapability(device);
                    if (!known || major * 10 + minor < MinGpuComputeCapabilityCode)
                    {
                        Error = known
                            ? SetupMessage.Of("setup.fail.ccGate", $"{major}.{minor}")
                            : SetupMessage.Of("setup.fail.ccGateUnknown");
                        CanSwitchToCpuFlavor = true;
                        _logger?.LogWarning("setup: cổng capability GPU — known={Known} cc={Major}.{Minor}", known, major, minor);
                        return StepOutcome.Failed;
                    }
                }

                MarkStepDone(SetupStep.VerifyingSidecar);
                return StepOutcome.Done;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { /* the bridge timed out — count as a failed attempt */ }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "setup: xác minh sidecar lần {Attempt}/{Total} thất bại", attempt, MaxRetries + 1);
            }
        }

        Error = SetupMessage.Of("setup.fail.sidecar", (MaxRetries + 1).ToString());
        return StepOutcome.Failed;
    }

    /// <summary>
    /// Ping plus catalog: the sidecar is "alive" only when <c>sidecar.ping</c> answers "pong" AND
    /// <c>ocr.models.list</c> returns a catalog array.
    /// Ping kèm catalog: sidecar "sống" chỉ khi <c>sidecar.ping</c> trả "pong" VÀ <c>ocr.models.list</c>
    /// trả về mảng catalog.
    /// </summary>
    private async Task<bool> IsSidecarAliveAsync(CancellationToken ct)
    {
        JsonNode? pong = await _bridge.CallAsync("sidecar.ping", null, ct).ConfigureAwait(false);
        if (pong is not JsonValue value || !value.TryGetValue<string>(out string? reply) || reply != "pong")
            return false;

        JsonNode? list = await _bridge.CallAsync("ocr.models.list", null, ct).ConfigureAwait(false);
        return list is JsonObject obj && obj["catalog"] is JsonArray;
    }

    /// <summary>
    /// DownloadingLlmRuntime (spec 2026-11-14 D13): the llama.cpp runtime is never bundled with the
    /// release zip, so the wizard installs it here. The runtime flavor follows the machine — GPU
    /// wheel flavor (already past the VerifyingSidecar capability gate) ⇒ <c>cuda</c>, otherwise
    /// <c>cpu</c>. A runtime already on disk marks the step done off one status call ("re-run setup"
    /// pass-through); otherwise it fires <c>llm.runtime.download</c> and polls
    /// <c>llm.runtime.status</c> every <see cref="_modelPollInterval"/> until installed or the
    /// <see cref="_modelDownloadTimeout"/> window elapses (then a fresh attempt follows). Failure is
    /// soft like DownloadingModels: the failure popup offers retry/skip-for-session and Settings can
    /// download the runtime later.
    /// DownloadingLlmRuntime (spec 2026-11-14 D13): runtime llama.cpp không bao giờ nằm trong zip
    /// release, nên wizard cài nó ở đây. Flavor runtime theo máy — flavor wheel GPU (đã qua cổng
    /// capability ở VerifyingSidecar) ⇒ <c>cuda</c>, còn lại <c>cpu</c>. Runtime đã có trên đĩa thì
    /// đánh dấu bước xong bằng một lời gọi status ("chạy lại setup" đi thẳng qua); ngược lại gọi
    /// <c>llm.runtime.download</c> rồi poll <c>llm.runtime.status</c> mỗi
    /// <see cref="_modelPollInterval"/> cho tới khi installed hoặc hết cửa sổ
    /// <see cref="_modelDownloadTimeout"/> (lượt thử mới sẽ chạy). Thất bại là mềm như
    /// DownloadingModels: popup màn lỗi cho retry/bỏ qua phiên, và Settings có thể tải runtime sau.
    /// </summary>
    private async Task<StepOutcome> RunDownloadLlmRuntimeStepAsync(CancellationToken ct)
    {
        string runtimeFlavor = Flavor == OcrFlavor.Gpu ? "cuda" : "cpu";
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            try
            {
                if (await IsLlmRuntimeInstalledAsync(ct).ConfigureAwait(false))
                {
                    MarkStepDone(SetupStep.DownloadingLlmRuntime);
                    return StepOutcome.Done;
                }

                await _bridge
                    .CallAsync("llm.runtime.download", new JsonObject { ["flavor"] = runtimeFlavor }, ct)
                    .ConfigureAwait(false);

                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(_modelDownloadTimeout);
                while (true)
                {
                    if (await IsLlmRuntimeInstalledAsync(ct).ConfigureAwait(false)) break;
                    await Task.Delay(_modelPollInterval, window.Token).ConfigureAwait(false);
                }

                MarkStepDone(SetupStep.DownloadingLlmRuntime);
                return StepOutcome.Done;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { /* the download window elapsed — count as a failed attempt */ }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "setup: tải runtime LLM lần {Attempt}/{Total} thất bại", attempt, MaxRetries + 1);
            }
        }

        Error = SetupMessage.Of("setup.fail.llmRuntime",
            (MaxRetries + 1).ToString(), _modelDownloadTimeout.TotalMinutes.ToString("0"));
        return StepOutcome.Failed;
    }

    /// <summary>
    /// Whether <c>llm.runtime.status</c> reports an installed runtime (the server binary exists on
    /// disk); a malformed or missing field counts as not installed.
    /// <c>llm.runtime.status</c> có báo runtime đã cài không (binary server tồn tại trên đĩa); field
    /// thiếu hoặc sai định dạng coi như chưa cài.
    /// </summary>
    private async Task<bool> IsLlmRuntimeInstalledAsync(CancellationToken ct)
    {
        JsonNode? status = await _bridge.CallAsync("llm.runtime.status", null, ct).ConfigureAwait(false);
        return status is JsonObject obj
            && obj["installed"] is JsonValue value
            && value.TryGetValue<bool>(out bool installed)
            && installed;
    }

    /// <summary>
    /// DownloadingModels: downloads every model the saved answers need — the det+rec pair of every
    /// ticked tier, the main language's legacy rec when v6 does not cover it, and each extra
    /// language's legacy rec (<see cref="OcrModelChoices.NeededModels"/>) — skipping models the
    /// catalog reports installed (a <c>.partial</c> model counts as not installed — the sidecar
    /// re-downloads it idempotently), then polls <c>ocr.models.list</c> every
    /// <see cref="_modelPollInterval"/> until ALL needed models are installed or the
    /// <see cref="_modelDownloadTimeout"/> window elapses (a fresh attempt follows).
    /// DownloadingModels: tải mọi model mà câu trả lời đã lưu cần — cặp det+rec của từng tier được
    /// tick, rec legacy của ngôn ngữ chính khi ngoài phủ v6, và rec legacy của từng ngôn ngữ bổ sung
    /// (<see cref="OcrModelChoices.NeededModels"/>) — bỏ qua model catalog báo đã cài (model có
    /// <c>.partial</c> coi như chưa cài — sidecar tải lại idempotent), rồi poll <c>ocr.models.list</c>
    /// mỗi <see cref="_modelPollInterval"/> cho tới khi MỌI model cần đều installed hoặc hết cửa sổ
    /// <see cref="_modelDownloadTimeout"/> (lượt thử mới sẽ chạy).
    /// </summary>
    private async Task<StepOutcome> RunDownloadModelsStepAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            try
            {
                IReadOnlyList<string> needed = NeededModelsForState();

                JsonNode? list = await _bridge.CallAsync("ocr.models.list", null, ct).ConfigureAwait(false);
                foreach (string name in needed)
                {
                    if (!IsInstalledInCatalog(list, name))
                        await _bridge
                            .CallAsync("ocr.models.download", new JsonObject { ["name"] = name }, ct)
                            .ConfigureAwait(false);
                }

                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(_modelDownloadTimeout);
                while (true)
                {
                    list = await _bridge.CallAsync("ocr.models.list", null, ct).ConfigureAwait(false);
                    if (needed.All(name => IsInstalledInCatalog(list, name))) break;
                    await Task.Delay(_modelPollInterval, window.Token).ConfigureAwait(false);
                }

                MarkStepDone(SetupStep.DownloadingModels);
                return StepOutcome.Done;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { /* the download window elapsed — count as a failed attempt */ }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "setup: tải model lần {Attempt}/{Total} thất bại", attempt, MaxRetries + 1);
            }
        }

        Error = SetupMessage.Of("setup.fail.models",
            (MaxRetries + 1).ToString(), _modelDownloadTimeout.TotalMinutes.ToString("0"));
        return StepOutcome.Failed;
    }

    /// <summary>
    /// The models the download step must ensure for the saved answers, with the coordinator's
    /// defensive fallback (small tier, no extras) when a field is missing.
    /// Các model bước tải phải đảm bảo theo câu trả lời đã lưu, kèm fallback phòng thủ của
    /// coordinator (tier small, không extra) khi thiếu field.
    /// </summary>
    private IReadOnlyList<string> NeededModelsForState() =>
        OcrModelChoices.NeededModels(
            _state.Tiers ?? ["small"],
            _state.ActiveTier ?? "small",
            LanguageCode,
            _state.ExtraLangs ?? []);

    /// <summary>
    /// Applying (Review Focus #5 — ALWAYS runs, even when nothing was downloaded): computes the pair
    /// from the saved answers (<see cref="OcrModelChoices.ExpectedPair"/>) and writes it into settings
    /// via <see cref="AppSettingsService.Save"/>. No RPC — the pair is pure mirror data.
    /// Applying (Review Focus #5 — LUÔN chạy, kể cả khi không tải model nào): tính cặp từ câu trả lời
    /// đã lưu (<see cref="OcrModelChoices.ExpectedPair"/>) và ghi vào settings qua
    /// <see cref="AppSettingsService.Save"/>. Không RPC — cặp là dữ liệu mirror thuần.
    /// </summary>
    private StepOutcome RunApplyingStep()
    {
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            try
            {
                (string det, string rec) = OcrModelChoices.ExpectedPair(_state.ActiveTier ?? "small", LanguageCode);
                AppSettings settings = _settings.Load();
                _settings.Save(settings with { OcrDet = det, OcrRec = rec });
                MarkStepDone(SetupStep.Applying);
                return StepOutcome.Done;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "setup: áp dụng settings lần {Attempt}/{Total} thất bại", attempt, MaxRetries + 1);
            }
        }

        Error = SetupMessage.Of("setup.fail.applying", (MaxRetries + 1).ToString());
        return StepOutcome.Failed;
    }

    /// <summary>
    /// Whether the named model's directory exists under the models cache — a pure file-system check
    /// that also runs before the sidecar is alive.
    /// Thư mục của model tên này có nằm trong cache model không — kiểm tra file-system thuần, chạy
    /// được cả trước khi sidecar sống.
    /// </summary>
    private bool ModelInstalled(string modelName) =>
        _modelDirExists(Path.Combine(SetupPaths.ModelsDir, SetupState.OfficialModelsSubdir, modelName));

    /// <summary>
    /// Light consistency pass run at the end of Checking and after every saved answer: when a needed
    /// model is gone from disk the download step must run again, and when the settings pair diverges
    /// from the stored answers the applying step must run again. A machine where everything matches
    /// keeps every marker, so a rerun stays a fast pass-through. Persists only when it cleared
    /// something; a no-op until both answer fields exist.
    /// Lượt so khớp nhẹ chạy cuối bước Checking và sau mỗi câu trả lời được lưu: model cần mà biến mất
    /// khỏi đĩa ⇒ bước tải phải chạy lại; cặp trong settings lệch với câu trả lời đã lưu ⇒ bước áp
    /// dụng phải chạy lại. Máy khớp hết giữ nguyên mọi marker nên rerun đi qua nhanh. Chỉ lưu khi có
    /// xoá gì; không làm gì khi còn thiếu field trả lời.
    /// </summary>
    private void ReconcileMarkers()
    {
        if (_state.Tiers is null || _state.ExtraLangs is null) return;
        if (_state.Steps is null) return;

        var steps = new Dictionary<string, string>(_state.Steps);
        bool changed = false;

        if (NeededModelsForState().Any(name => !ModelInstalled(name))
            && steps.Remove(StepKey(SetupStep.DownloadingModels)))
        {
            changed = true;
        }

        (string det, string rec) = OcrModelChoices.ExpectedPair(_state.ActiveTier ?? "small", LanguageCode);
        AppSettings settings = _settings.Load();
        if ((settings.OcrDet != det || settings.OcrRec != rec)
            && steps.Remove(StepKey(SetupStep.Applying)))
        {
            changed = true;
        }

        if (changed)
        {
            _state = _state with { Steps = steps };
            _state.Save();
        }
    }

    // ── state transitions & helpers ────────────────────────────────────────────────────────────────

    private void EnterFlavorSwitchPause(OcrFlavor installed)
    {
        PendingFlavorSwitch = new FlavorSwitchInfo(installed, Flavor);
        Notice = SetupMessage.Of("setup.flavorSwitch.notice", Label(installed), Label(Flavor));
        Error = null;
        Step = SetupStep.ConfirmingFlavorSwitch;
        Publish();
        _logger?.LogInformation("setup: dừng chờ xác nhận đổi flavor ({Installed} → {Target})", installed, Flavor);
    }

    private void EnterFailed(SetupStep step)
    {
        _lastFailedStep = step;
        Step = SetupStep.Failed;
        Publish();
    }

    private void EnterDone()
    {
        _state = _state with { IsComplete = true };
        _state.Save();
        Error = null;
        Step = SetupStep.Done;
        Publish();
        _logger?.LogInformation("setup: wizard hoàn tất");
    }

    /// <summary>
    /// Marks a step done in the working state and persists it (only AFTER the step truly succeeded).
    /// Đánh dấu bước xong trong state làm việc và lưu xuống đĩa (chỉ SAU khi bước thực sự thành công).
    /// </summary>
    private void MarkStepDone(SetupStep step)
    {
        _state = _state with
        {
            Flavor = FlavorName(Flavor),
            Steps = WithStep(_state.Steps, StepKey(step), "done"),
        };
        _state.Save();
    }

    /// <summary>
    /// Whether the working state marks the step done in <c>setup.json</c>.
    /// State làm việc có đánh dấu bước đã done trong <c>setup.json</c> không.
    /// </summary>
    private bool IsStepDone(SetupStep step) =>
        _state.Steps is { } steps && steps.TryGetValue(StepKey(step), out string? value) && value == "done";

    /// <summary>
    /// Drops the done-markers of every pipeline step (used when the decided flavor changed since the
    /// last run). Persists.
    /// Bỏ các marker done của mọi bước pipeline (dùng khi flavor đã quyết định đổi so với lần chạy
    /// trước). Có lưu xuống đĩa.
    /// </summary>
    private void ClearPipelineSteps()
    {
        if (_state.Steps is null) return;

        var steps = new Dictionary<string, string>(_state.Steps);
        foreach (SetupStep step in PipelineSteps) steps.Remove(StepKey(step));
        _state = _state with { Steps = steps };
        _state.Save();
    }

    /// <summary>
    /// The single event-firing point: every public-surface mutation above is followed by this call so a
    /// state change can never skip the <see cref="Changed"/> event.
    /// Điểm phát sự kiện duy nhất: mọi thay đổi surface công khai phía trên đều được theo sau bởi lệnh
    /// gọi này để không có thay đổi trạng thái nào bỏ sót sự kiện <see cref="Changed"/>.
    /// </summary>
    private void Publish() => Changed?.Invoke();

    private bool TryBeginRun() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    private void EndRun() => Interlocked.Exchange(ref _running, 0);

    private string LanguageCode => _state.Language ?? "auto";

    private static string StepKey(SetupStep step) => step.ToString().ToLowerInvariant();

    private static string FlavorName(OcrFlavor flavor) => flavor == OcrFlavor.Gpu ? "gpu" : "cpu";

    private static OcrFlavor? TryParseFlavor(string? stored) => stored?.ToLowerInvariant() switch
    {
        "gpu" => OcrFlavor.Gpu,
        "cpu" => OcrFlavor.Cpu,
        _ => null,
    };

    /// <summary>
    /// Returns the steps dictionary with one key set, copying first so the record's shared reference is
    /// never mutated in place.
    /// Trả về dictionary steps với một khóa được đặt, copy trước để không sửa tại chỗ reference chung
    /// của record.
    /// </summary>
    private static Dictionary<string, string> WithStep(Dictionary<string, string>? steps, string key, string value)
    {
        var result = steps is null ? new Dictionary<string, string>() : new Dictionary<string, string>(steps);
        result[key] = value;
        return result;
    }

    /// <summary>
    /// Whether the catalog entry for <paramref name="name"/> reports installed; false when the list is
    /// malformed or the entry is missing.
    /// Entry catalog của <paramref name="name"/> có báo installed không; false khi list sai định dạng
    /// hoặc thiếu entry.
    /// </summary>
    private static bool IsInstalledInCatalog(JsonNode? list, string name)
    {
        if (list is not JsonObject obj || obj["catalog"] is not JsonArray catalog) return false;

        foreach (JsonNode? entry in catalog)
        {
            if (entry is not JsonObject e) continue;
            if (e["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out string? entryName)) continue;
            if (entryName != name) continue;
            return e["installed"] is JsonValue installedValue && installedValue.TryGetValue<bool>(out bool installed) && installed;
        }

        return false;
    }

    /// <summary>
    /// The [major, minor] capability out of a <c>sidecar.device</c> result; (false, 0, 0) when absent or
    /// malformed.
    /// Capability [major, minor] từ kết quả <c>sidecar.device</c>; (false, 0, 0) khi thiếu hoặc sai định
    /// dạng.
    /// </summary>
    private static (bool Known, int Major, int Minor) TryParseCapability(JsonNode? device)
    {
        if (device is not JsonObject obj || obj["capability"] is not JsonArray array || array.Count != 2)
            return (false, 0, 0);
        if (array[0] is not JsonValue majorValue || !majorValue.TryGetValue<int>(out int major)) return (false, 0, 0);
        if (array[1] is not JsonValue minorValue || !minorValue.TryGetValue<int>(out int minor)) return (false, 0, 0);
        return (true, major, minor);
    }

    private static string Label(OcrFlavor flavor) => flavor == OcrFlavor.Gpu ? "GPU" : "CPU";
}
