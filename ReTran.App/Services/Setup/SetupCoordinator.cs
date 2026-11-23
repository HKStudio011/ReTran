using System.Runtime.ExceptionServices;
using System.Text.Json;
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

    /// <summary>Waiting for the user to tick the OCR det/rec models (at least one of each). Chờ user tick model det/rec OCR (ít nhất 1 mỗi khối).</summary>
    ChoosingOcrModels,

    /// <summary>Waiting for the user to answer the language screen (tick any legacy rec, or none). Chờ user trả lời màn Ngôn ngữ (tick rec legacy bất kỳ, hoặc không).</summary>
    ChoosingLanguages,

    /// <summary>Waiting for the user to answer the LLM screen (tick any catalog model, or none). Chờ user trả lời màn LLM (tick model bất kỳ, hoặc không).</summary>
    ChoosingLlmModels,

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
/// One in-flight download shown on the running download steps: model name, bytes received/total and
/// the current speed (<see langword="null"/> when unknown).
/// Một lượt tải đang chạy hiển thị ở bước tải: tên model, số byte đã nhận/tổng và tốc độ hiện tại
/// (<see langword="null"/> khi chưa biết).
/// </summary>
/// <param name="Name">Model name. Tên model.</param>
/// <param name="ReceivedBytes">Bytes received so far. Số byte đã nhận.</param>
/// <param name="TotalBytes">Total bytes, or 0 when unknown. Tổng số byte, hoặc 0 khi chưa biết.</param>
/// <param name="BytesPerSec">Current speed in bytes/second, or <see langword="null"/>. Tốc độ hiện tại (byte/giây), hoặc <see langword="null"/>.</param>
/// <param name="Index">1-based position in the run's needed-model list (spec §9 "model 2/3"); 0 = outside the list (hide the counter). Vị trí 1-based trong danh sách model cần tải (spec §9 "model 2/3"); 0 = ngoài danh sách (ẩn bộ đếm).</param>
/// <param name="Total">Count of the needed-model list; 0 = hide the counter. Tổng số model cần tải; 0 = ẩn bộ đếm.</param>
public sealed record SetupDownloadInfo(
    string Name, long ReceivedBytes, long TotalBytes, double? BytesPerSec,
    int Index = 0, int Total = 0);

/// <summary>
/// First-run Setup wizard orchestration as a testable state machine (spec §3, §4): hard gates first,
/// the det/rec, language and LLM answers once, then the pipeline VerifyingFiles → Syncing →
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
/// trước, ba câu trả lời wizard (det/rec / Ngôn ngữ / LLM) hỏi một lần, rồi pipeline
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
    private static readonly JsonSerializerOptions DeletionJournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // Pipeline steps in order; Checking and the pre-pipeline answers (det/rec models, languages, LLM)
    // run before it, Done after it.
    // Các bước pipeline theo thứ tự; Checking và các câu hỏi pre-pipeline (model det/rec, Ngôn ngữ,
    // LLM) chạy trước, Done chạy sau.
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
    private readonly Func<string, string> _readText;
    private readonly string _llmRoot;
    private readonly string _deletionJournalPath;
    private readonly TimeSpan _modelPollInterval;
    private readonly TimeSpan _modelDownloadTimeout;
    private readonly ILogger<SetupCoordinator>? _logger;

    private SetupState _state;
    private bool _selectionPersistenceFailed;
    private ModelDeletionIntent? _deletionIntent;
    private bool _deletionJournalUnreadable;
    private bool _deletionSettingsWritten;
    private int _running; // 0 idle / 1 a run is in progress (Interlocked) - guards against double starts
    private SetupCheckItem[] _checks = []; // snapshot checklist Checking - thay nguyên khối mỗi event
    private SetupDownloadInfo[] _downloads = []; // snapshot tiến độ tải - thay nguyên khối mỗi poll

    // EMA (α = 0.3) of each download's bytes/sec — spec vòng 2 §9: ETA làm mượt để không nhảy loạn
    // theo từng poll. Key = download name; cleared when the step finishes.
    // EMA (α = 0,3) của tốc độ từng lượt tải — spec vòng 2 §9: ETA làm mượt để không nhảy loạn theo
    // từng poll. Key = tên download; xoá khi bước kết thúc.
    private readonly Dictionary<string, double> _emaBps = new();

    // True once the user explicitly confirmed the flavor swap in THIS run (ConfirmFlavorSwitch /
    // SwitchToCpuFlavor): a retry after a failed post-switch sync must NOT re-prompt the switch —
    // the switch itself was the confirmation (Amendment A2). BA review 2026-11-17.
    // True sau khi user xác nhận đổi flavor trong LƯỢT NÀY (ConfirmFlavorSwitch / SwitchToCpuFlavor):
    // retry sau sync fail không được hỏi lại — lượt chuyển chính là xác nhận (Amendment A2).
    // Review BA 2026-11-17.
    private bool _flavorSwitchConfirmed;

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
    /// <param name="modelDirExists">Installed-probe for one model directory under the models cache, used by the rerun reconcile; defaults to <see cref="IsModelDirInstalled"/> (the sidecar's strict rule — non-empty dir, no <c>.partial</c> marker). Probe "đã cài" cho thư mục của một model trong cache model, dùng bởi reconcile khi chạy lại; mặc định <see cref="IsModelDirInstalled"/> (luật nghiêm của sidecar — thư mục không rỗng, không có marker <c>.partial</c>).</param>
    /// <param name="readText">Text-read seam for selected-artifact JSON preflight; defaults to <see cref="File.ReadAllText"/>. Seam đọc text cho preflight JSON artifact đã chọn; mặc định <see cref="File.ReadAllText"/>.</param>
    /// <param name="llmRoot">Local LLM root for artifact preflight; defaults to <see cref="SetupPaths.LlmRoot"/>. Thư mục gốc LLM local cho preflight artifact; mặc định <see cref="SetupPaths.LlmRoot"/>.</param>
    /// <param name="deletionJournalPath">Persistent model-deletion intent journal path; defaults to <see cref="SetupPaths.ModelDeletionJsonPath"/>. Đường dẫn journal intent xoá model bền; mặc định <see cref="SetupPaths.ModelDeletionJsonPath"/>.</param>
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
        Func<string, string>? readText = null,
        string? llmRoot = null,
        string? deletionJournalPath = null,
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
        _modelDirExists = modelDirExists ?? IsModelDirInstalled;
        _readText = readText ?? File.ReadAllText;
        _llmRoot = llmRoot ?? SetupPaths.LlmRoot;
        _deletionJournalPath = deletionJournalPath ?? SetupPaths.ModelDeletionJsonPath;
        _modelPollInterval = modelPollInterval ?? DefaultModelPollInterval;
        _modelDownloadTimeout = modelDownloadTimeout ?? DefaultModelDownloadTimeout;
        _logger = logger;
        _state = _stateFactory();
        RestoreDeletionIntent();

        // A fresh launch on an already-set-up machine must not leave the singleton stuck at Checking:
        // SetupModal's cheap path (NeedsSetup false) never calls StartAsync, and both the pipeline gate
        // and the Settings recovery section ("Chạy lại setup") read IsComplete off Step. Initialization
        // only — no Publish, because no subscribers exist at construction time.
        // Máy đã setup xong ở phiên trước khởi động lại không được để singleton kẹt ở Checking: đường rẻ
        // của SetupModal (NeedsSetup false) không bao giờ gọi StartAsync, và cả gate pipeline lẫn section
        // khôi phục trong Settings ("Chạy lại setup") đều đọc IsComplete từ Step. Chỉ là khởi tạo — không
        // Publish vì thời điểm construct chưa có subscriber nào.
        if (_state.IsComplete && !_selectionPersistenceFailed) Step = SetupStep.Done;
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
    /// Snapshot of the in-flight downloads of the running download steps (8 and 9); empty outside
    /// them. Swapped wholesale like <see cref="Checks"/>, so a UI read never tears.
    /// Snapshot các lượt tải đang chạy của bước tải (8 và 9); rỗng ngoài các bước đó. Thay nguyên
    /// khối như <see cref="Checks"/> nên lượt đọc của UI không bao giờ bị xé.
    /// </summary>
    public IReadOnlyList<SetupDownloadInfo> Downloads => _downloads;

    /// <summary>
    /// Wheel flavor the wizard is installing (decided by Checking, or switched to CPU via
    /// <see cref="SwitchToCpuFlavorAsync"/>). Flavor wheel wizard đang cài (do Checking quyết định, hoặc đã chuyển sang CPU qua <see cref="SwitchToCpuFlavorAsync"/>).
    /// </summary>
    public OcrFlavor Flavor { get; private set; }

    /// <summary>
    /// Det model marked "Dùng chính" in step 2; <see langword="null"/> until answered.
    /// Model det được chấm "Dùng chính" ở bước 2; <see langword="null"/> khi chưa trả lời.
    /// </summary>
    public string? ActiveDet => _state.ActiveDet;

    /// <summary>
    /// Rec model marked "Dùng chính" in step 2 (the running rec unless <see cref="ActiveLang"/> is
    /// set); <see langword="null"/> until answered.
    /// Model rec được chấm "Dùng chính" ở bước 2 (rec đang chạy trừ khi có <see cref="ActiveLang"/>);
    /// <see langword="null"/> khi chưa trả lời.
    /// </summary>
    public string? ActiveRec => _state.ActiveRec;

    /// <summary>
    /// Legacy rec marked "Đọc chính" in step 3 — the running rec when set; <see langword="null"/>
    /// when the user did not mark one (or answered nothing).
    /// Rec legacy được chấm "Đọc chính" ở bước 3 — rec đang chạy khi có; <see langword="null"/> khi
    /// user không chấm (hoặc trả lời trống).
    /// </summary>
    public string? ActiveLang => _state.ActiveLang;

    /// <summary>
    /// LLM model marked "Dùng chính" in step 4; <see langword="null"/> when none (or nothing chosen).
    /// Model LLM được chấm "Dùng chính" ở bước 4; <see langword="null"/> khi không chấm (hoặc không chọn gì).
    /// </summary>
    public string? ActiveLlm => _state.ActiveLlm;

    /// <summary>
    /// True once the wizard reached <see cref="SetupStep.Done"/>. Đúng khi wizard đã tới <see cref="SetupStep.Done"/>.
    /// </summary>
    public bool IsComplete => Step == SetupStep.Done;

    /// <summary>
    /// True while any coordinator operation (<see cref="StartAsync"/>, <see cref="ContinueWithOcrModelsAsync"/>,
    /// <see cref="ContinueWithLanguagesAsync"/>, <see cref="ContinueWithLlmModelsAsync"/>,
    /// <see cref="RetryAsync"/>, <see cref="ConfirmFlavorSwitchAsync"/>, <see cref="SwitchToCpuFlavorAsync"/>) is
    /// in flight; false once the run finishes, pauses for user input (language, flavor switch), or fails.
    /// Exposed so callers can tell "complete but re-running" apart from "complete and idle": during a forced
    /// re-run <see cref="Step"/> still holds <see cref="SetupStep.Done"/> until the first publish, so
    /// <see cref="IsComplete"/> alone cannot close that window.
    /// Đúng khi một thao tác của coordinator (<see cref="StartAsync"/>, <see cref="ContinueWithOcrModelsAsync"/>,
    /// <see cref="ContinueWithLanguagesAsync"/>, <see cref="ContinueWithLlmModelsAsync"/>,
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
    /// target); <see langword="null"/> in every other step. Đổi flavor đang chờ khi ở <see cref="SetupStep.ConfirmingFlavorSwitch"/> (bản đã cài so với bản đích); <see langword="null"/> ở mọi bước khác.
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
    /// the CURRENT hardware), pauses at the first unanswered pre-pipeline question (det/rec models →
    /// languages → LLM), then runs the pipeline from the first not-done step. A completed
    /// state short-circuits straight to <see cref="SetupStep.Done"/> only while every selected artifact
    /// remains present. With <paramref name="force"/> (the Settings repair path), every step marker is
    /// cleared and persisted immediately, valid answers are preserved, only missing/invalid question
    /// screens are asked again, and the pipeline re-verifies without re-downloading installed models.
    /// Khởi động wizard: chạy lại check system (thuần và rẻ, flavor phải theo phần cứng HIỆN TẠI), dừng
    /// ở câu hỏi pre-pipeline đầu tiên chưa được trả lời (model det/rec → Ngôn ngữ → LLM), rồi
    /// chạy pipeline từ bước chưa done đầu tiên. State đã xong chỉ nhảy thẳng tới
    /// <see cref="SetupStep.Done"/> khi mọi artifact đã chọn còn đủ. Với <paramref name="force"/>
    /// (đường sửa lỗi trong Settings), mọi marker bước bị xoá và ghi ngay, câu trả lởi hợp lệ được giữ,
    /// chỉ màn thiếu/không hợp lệ được hỏi lại, pipeline xác minh lại và không tải lại model đã cài.
    /// </summary>
    /// <param name="force">Bypasses the complete-state short-circuit and clears step markers while preserving valid answers for repair. Bỏ qua nhánh tắt hoàn tất và xoá marker bước nhưng giữ câu trả lởi hợp lệ để sửa.</param>
    /// <param name="ct">Cancellation token; cancelling aborts the run (state is left where it was). Token hủy; hủy sẽ dừng lượt chạy (state giữ nguyên ở chỗ nó đang ở).</param>
    /// <remarks>Unexpected faults are logged and published as <see cref="SetupStep.Failed"/> before the run lock is released. Lỗi không mong đợi được ghi log và publish ở <see cref="SetupStep.Failed"/> trước khi nhả khóa lượt chạy.</remarks>
    /// <exception cref="OperationCanceledException">Thrown when the run is canceled; cancellation is not converted to a failure. Ném khi lượt chạy bị hủy; hủy không bị chuyển thành lỗi.</exception>
    public async Task StartAsync(bool force = false, CancellationToken ct = default)
    {
        if (!TryBeginRun()) return;
        try
        {
            if (!await ResolveDeletionBeforeSetupAsync(ct).ConfigureAwait(false)) return;

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

            // Run-scoped UX state must not leak across runs: a CC-gate failure in run N must not offer
            // a CPU switch in run N+1, and stale PendingFlavorSwitch/Notice contradict their documented
            // invariants (BA review 2026-11-17).
            // Trạng thái UX theo lượt không được lộ sang lượt khác: fail CC-gate ở lượt N không được
            // đề nghị đổi CPU ở lượt N+1; PendingFlavorSwitch/Notice cũ mù vi phạm invariant đã doc
            // (review BA 2026-11-17).
            CanSwitchToCpuFlavor = false;
            PendingFlavorSwitch = null;
            Notice = null;
            _flavorSwitchConfirmed = false;

            if (!_selectionPersistenceFailed) _state = _stateFactory();

            // A fresh app session cannot assume the Core from a previous session is still alive: the
            // VerifyingSidecar marker is SESSION truth, not disk truth. Drop it on every run start so
            // the step kill+starts+pings Core again (cheap) — otherwise a resume skips it and every
            // later RPC step dies with "Core not started" (BA 2026-11-17: Checking → llama.cpp →
            // failed 4/4, caught by the file log).
            // Phiên app mới không thể mặc định Core của phiên trước còn sống: marker VerifyingSidecar
            // là sự-thật-THEO-PHIÊN, không phải sự-thật-trên-đĩa. Bỏ marker này ở đầu mọi lượt chạy để
            // bước đó kill+start+ping Core lại (rẻ) — nếu không, resume sẽ skip nó và mọi bước RPC sau
            // chết với "Core not started" (BA 2026-11-17: Checking → llama.cpp → fail 4/4, file log bắt được).
            _state.Steps?.Remove(StepKey(SetupStep.VerifyingSidecar));

            if (force)
            {
                // The repair rerun clears every step marker, not the answers: valid selections survive,
                // while missing/invalid answers still pause at their own question screen. Persist immediately
                // so a crash cannot revive the old complete state. Catalog/file probes skip valid artifacts;
                // Syncing always runs its real frozen sync anyway (see RunSyncStepAsync).
                // Lượt rerun sửa lỗi chỉ xoá marker bước, giữ mọi câu trả lởi hợp lệ; màn nào thiếu hoặc
                // không hợp lệ vẫn dừng để hỏi. Lưu NGAY để crash không khôi phục trạng thái hoàn tất cũ.
                // Probe catalog/file bỏ qua artifact còn hợp lệ; Syncing vẫn chạy frozen sync thật
                // (xem RunSyncStepAsync).
                _state = _state with { Steps = null, IsComplete = false };
                _state.Save();
            }

            // The complete-state short-circuit only holds when the ARTIFACTS are still on disk (venv +
            // models): NeedsSetup fires on their loss (spec v1 §5.9), and a bare Done here voided that
            // trigger — a lost venv never got repaired and pipeline start failed every launch
            // (BA review 2026-11-17: dead path).
            // Mất tắt hoàn tất chỉ có hiệu lực khi ARTIFACTS còn trên đĩa (venv + models): NeedsSetup
            // bắn khi chúng mất (spec v1 §5.9), và Done trần vô hiệu hoá trigger đó — venv mất không
            // bao giờ được sửa, start pipeline fail mỗi lần mở app (review BA 2026-11-17: dead path).
            if (!force && !_selectionPersistenceFailed && _state.IsComplete && ArtifactsPresent())
            {
                Step = SetupStep.Done;
                Publish();
                return;
            }

            // Publish EARLY that the run is in Checking: on a machine whose previous run ended at
            // Done, Step still reads Done here — and the modal (IsComplete => Step == Done) would
            // stay HIDDEN for the entire check window (network probes, up to ~180 s), which looked
            // exactly like "bấm Chạy lại setup nhưng không có gì hiện ra" (BA 2026-11-17).
            // Publish SỚM rằng lượt chạy đang ở Checking: trên máy có lượt trước kết thúc ở Done,
            // Step vẫn đọc là Done tại đây — và modal (IsComplete => Step == Done) sẽ ẨN suốt cửa sổ
            // check (probe mạng, tới ~180 s), nhìn đúng như "bấm Chạy lại setup nhưng không có gì
            // hiện ra" (BA 2026-11-17).
            Step = SetupStep.Checking;
            Publish();
            _logger?.LogInformation("setup: run started (force={Force})", force);
            _logger?.LogInformation("setup: state complete={Complete}, artifacts present={Artifacts}",
                _state.IsComplete, ArtifactsPresent());

            StepOutcome outcome = RunCheckingStep();
            if (outcome == StepOutcome.Failed)
            {
                EnterFailed(SetupStep.Checking);
                return;
            }

            if (!HasOcrAnswers(_state))
            {
                Step = SetupStep.ChoosingOcrModels;
                Publish();
                _logger?.LogInformation("setup: paused at {Step}", Step);
                return;
            }

            await ContinueAfterAnswersAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SetupStep failedStep = Step == SetupStep.Failed ? _lastFailedStep : Step;
            if (failedStep != SetupStep.Checking && !PipelineSteps.Contains(failedStep))
                failedStep = SetupStep.Checking;
            _logger?.LogError(ex, "setup: run faulted at {Step}", failedStep);
            Error = SetupMessage.Of("setup.fail.unexpected", ex.Message);
            EnterFailed(failedStep);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Continues after the user answered the det/rec screen: validates the ticked det and rec models
    /// and their "Dùng chính" picks (at least one of each), persists them, marks the step done, and
    /// resumes at the next unanswered question or the pipeline. A no-op when not in
    /// <see cref="SetupStep.ChoosingOcrModels"/>.
    /// Tiếp tục sau khi user trả lời màn det/rec: kiểm tra các model det/rec đã tick và lựa chọn "Dùng
    /// chính" của chúng (ít nhất 1 mỗi khối), lưu lại, đánh dấu bước xong, rồi chạy tiếp ở câu hỏi kế
    /// hoặc pipeline. Không làm gì nếu không ở <see cref="SetupStep.ChoosingOcrModels"/>.
    /// </summary>
    /// <param name="dets">Ticked det model names — at least one. Tên model det đã tick — ít nhất 1.</param>
    /// <param name="activeDet">The "Dùng chính" det, one of <paramref name="dets"/>. Model det "Dùng chính", nằm trong <paramref name="dets"/>.</param>
    /// <param name="recs">Ticked rec model names — at least one. Tên model rec đã tick — ít nhất 1.</param>
    /// <param name="activeRec">The "Dùng chính" rec, one of <paramref name="recs"/>. Model rec "Dùng chính", nằm trong <paramref name="recs"/>.</param>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    /// <exception cref="ArgumentException">Empty list, unknown model, or an active pick outside its ticked list. List rỗng, model lạ, hoặc lựa chọn chính ngoài danh sách đã tick.</exception>
    public async Task ContinueWithOcrModelsAsync(IReadOnlyList<string> dets, string activeDet,
        IReadOnlyList<string> recs, string activeRec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dets);
        ArgumentNullException.ThrowIfNull(recs);
        ArgumentException.ThrowIfNullOrEmpty(activeDet);
        ArgumentException.ThrowIfNullOrEmpty(activeRec);
        if (dets.Count == 0)
            throw new ArgumentException("Cần tick ít nhất một model det.", nameof(dets));
        if (recs.Count == 0)
            throw new ArgumentException("Cần tick ít nhất một model rec.", nameof(recs));
        foreach (string det in dets)
        {
            if (!OcrModelChoices.DetModels().Contains(det, StringComparer.Ordinal))
                throw new ArgumentException($"Model det không hợp lệ: '{det}'.", nameof(dets));
        }
        foreach (string rec in recs)
        {
            if (!OcrModelChoices.RecModels().Contains(rec, StringComparer.Ordinal))
                throw new ArgumentException($"Model rec không hợp lệ: '{rec}'.", nameof(recs));
        }
        if (!dets.Contains(activeDet, StringComparer.Ordinal))
            throw new ArgumentException("Model det \"Dùng chính\" phải nằm trong các model đã tick.", nameof(activeDet));
        if (!recs.Contains(activeRec, StringComparer.Ordinal))
            throw new ArgumentException("Model rec \"Dùng chính\" phải nằm trong các model đã tick.", nameof(activeRec));

        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.ChoosingOcrModels) return;

            _state = _state with
            {
                Dets = [.. dets],
                ActiveDet = activeDet,
                Recs = [.. recs],
                ActiveRec = activeRec,
                Steps = WithStep(_state.Steps, StepKey(SetupStep.ChoosingOcrModels), "done"),
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
    /// Continues after the user answered the language screen: validates the ticked legacy rec names
    /// (normalized, deduplicated; may be empty) and the optional "Đọc chính" pick, persists them,
    /// marks the step done, and resumes. A no-op when not in
    /// <see cref="SetupStep.ChoosingLanguages"/>.
    /// Tiếp tục sau khi user trả lời màn Ngôn ngữ: kiểm tra tên rec legacy đã tick (bỏ trùng; có thể
    /// rỗng) và lựa chọn "Đọc chính" tuỳ chọn, lưu lại, đánh dấu bước xong, rồi chạy tiếp. Không làm
    /// gì nếu không ở <see cref="SetupStep.ChoosingLanguages"/>.
    /// </summary>
    /// <param name="langs">Ticked legacy rec model names (any subset of the step-3 variants), possibly empty. Tên rec legacy đã tick (tập con bất kỳ của các bản ở bước 3), có thể rỗng.</param>
    /// <param name="activeLang">The "Đọc chính" rec (one of <paramref name="langs"/>), or <see langword="null"/>. Rec "Đọc chính" (nằm trong <paramref name="langs"/>), hoặc <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    /// <exception cref="ArgumentException">An unknown rec name or an active pick outside the ticked list. Tên rec lạ hoặc lựa chọn chính ngoài danh sách đã tick.</exception>
    public async Task ContinueWithLanguagesAsync(IReadOnlyList<string> langs, string? activeLang, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(langs);
        var allowed = OcrModelChoices.LanguageChoices()
            .SelectMany(c => c.Variants)
            .ToHashSet(StringComparer.Ordinal);
        var picks = new List<string>();
        foreach (string lang in langs)
        {
            if (!allowed.Contains(lang))
                throw new ArgumentException($"Rec ngôn ngữ không hợp lệ: '{lang}'.", nameof(langs));
            if (!picks.Contains(lang, StringComparer.Ordinal)) picks.Add(lang);
        }
        if (activeLang is not null && !picks.Contains(activeLang, StringComparer.Ordinal))
            throw new ArgumentException("Rec \"Đọc chính\" phải nằm trong các rec đã tick.", nameof(activeLang));

        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.ChoosingLanguages) return;

            _state = _state with
            {
                Langs = picks,
                ActiveLang = activeLang,
                Steps = WithStep(_state.Steps, StepKey(SetupStep.ChoosingLanguages), "done"),
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
    /// Continues after the user answered the LLM screen: validates the ticked catalog names
    /// (deduplicated; may be empty) and the optional "Dùng chính" pick, persists them, marks the step
    /// done, and resumes. When anything is ticked the llama.cpp-download and model-download markers
    /// are cleared so those steps run again and fetch what is missing. A no-op when not in
    /// <see cref="SetupStep.ChoosingLlmModels"/>.
    /// Tiếp tục sau khi user trả lời màn LLM: kiểm tra tên catalog đã tick (bỏ trùng; có thể rỗng) và
    /// lựa chọn "Dùng chính" tuỳ chọn, lưu lại, đánh dấu bước xong, rồi chạy tiếp. Khi có tick, marker
    /// tải llama.cpp và tải model được xoá để hai bước đó chạy lại và tải phần còn thiếu. Không làm gì
    /// nếu không ở <see cref="SetupStep.ChoosingLlmModels"/>.
    /// </summary>
    /// <param name="llmModels">Ticked LLM catalog names, possibly empty. Tên model LLM đã tick, có thể rỗng.</param>
    /// <param name="activeLlm">The "Dùng chính" LLM (one of <paramref name="llmModels"/>), or <see langword="null"/>. Model LLM "Dùng chính" (nằm trong <paramref name="llmModels"/>), hoặc <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    /// <exception cref="ArgumentException">A blank name or an active pick outside the ticked list. Tên rỗng hoặc lựa chọn chính ngoài danh sách đã tick.</exception>
    public async Task ContinueWithLlmModelsAsync(IReadOnlyList<string> llmModels, string? activeLlm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(llmModels);
        var picks = new List<string>();
        foreach (string model in llmModels)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException($"Model LLM không hợp lệ: '{model}'.", nameof(llmModels));
            if (!picks.Contains(model, StringComparer.Ordinal)) picks.Add(model);
        }
        if (activeLlm is not null && !picks.Contains(activeLlm, StringComparer.Ordinal))
            throw new ArgumentException("Model LLM \"Dùng chính\" phải nằm trong các model đã tick.", nameof(activeLlm));

        if (!TryBeginRun()) return;
        try
        {
            if (Step != SetupStep.ChoosingLlmModels) return;

            Dictionary<string, string> steps = WithStep(_state.Steps, StepKey(SetupStep.ChoosingLlmModels), "done");
            if (picks.Count > 0)
            {
                // Có chọn LLM ⇒ hai bước tải phải chạy lại để tải bù (runtime llama.cpp + GGUF) — spec §10.
                steps.Remove(StepKey(SetupStep.DownloadingLlmRuntime));
                steps.Remove(StepKey(SetupStep.DownloadingModels));
            }
            _state = _state with { LlmModels = picks, ActiveLlm = activeLlm, Steps = steps };
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

            // The multi-minute sync below must show as Syncing - never as ConfirmingFlavorSwitch with a
            // null PendingFlavorSwitch, which would contradict the documented invariant of both.
            PendingFlavorSwitch = null;
            _flavorSwitchConfirmed = true; // lượt chuyển chính là xác nhận — retry không hỏi lại (A2)
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
            _flavorSwitchConfirmed = true; // lượt chuyển chính là xác nhận — retry không hỏi lại (A2)
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
            try
            {
                if (!await ResolveDeletionBeforeSetupAsync(ct).ConfigureAwait(false)) return;
            }
            catch (InvalidOperationException ex) when (_deletionIntent is not null)
            {
                _logger?.LogWarning(ex, "setup: deletion repair is still incomplete");
                return; // persistence helpers already published the fail-closed state
            }
            if (Step != SetupStep.Failed)
            {
                if (Step == SetupStep.ChoosingLlmModels
                    && SetupModelSelection.HasCompleteAnswers(_state))
                {
                    // A stale settings-only active model can fail closed even though every Setup
                    // answer is already valid. Finish the real Applying/write instead of re-asking.
                    Step = SetupStep.Failed;
                    _lastFailedStep = SetupStep.Checking;
                }
                else return;
            }

            Error = null;
            CanSwitchToCpuFlavor = false;
            Publish();

            // Checking sits BEFORE the pipeline, so a retry of it must re-run the gate itself (and may
            // pause at ChoosingOcrModels again when the answers were never given). The pipeline then
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

                if (!HasOcrAnswers(_state))
                {
                    Step = SetupStep.ChoosingOcrModels;
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
    /// Deletes an OCR model and reconciles the real catalog before changing any saved selection.
    /// Xoá model OCR và reconcile catalog thật trước khi thay đổi lựa chọn đã lưu.
    /// </summary>
    /// <param name="name">Catalog model to delete. Tên model catalog cần xoá.</param>
    /// <param name="installed">Other installed models known before the RPC. Các model khác được biết là còn cài trước RPC.</param>
    /// <param name="engineActive">Other engine-active models used for replacement preference. Các model engine active khác dùng để ưu tiên thay thế.</param>
    /// <param name="ct">Token used for the delete RPC; reconciliation uses an uncancelled read when the outcome is unknown. Token cho RPC xoá; reconcile dùng lượt đọc không bị hủy khi kết quả chưa rõ.</param>
    /// <returns>A task completed after confirmed deletion and consistent selection persistence, or after an acknowledged failed delete is reconciled. Task hoàn tất sau khi xoá được xác nhận và lựa chọn được lưu nhất quán, hoặc sau khi reconcile lượt xoá lỗi.</returns>
    public Task DeleteOcrModelAsync(
        string name, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> engineActive,
        CancellationToken ct = default) =>
        DeleteModelAsync("ocr", name, installed, engineActive, ct);

    /// <summary>
    /// Deletes a GGUF model and reconciles the real catalog before changing any saved selection.
    /// Xoá model GGUF và reconcile catalog thật trước khi thay đổi lựa chọn đã lưu.
    /// </summary>
    /// <param name="name">Catalog model to delete. Tên model catalog cần xoá.</param>
    /// <param name="installed">Other installed models known before the RPC. Các model khác được biết là còn cài trước RPC.</param>
    /// <param name="engineActive">Other engine-active models used for replacement preference. Các model engine active khác dùng để ưu tiên thay thế.</param>
    /// <param name="ct">Token used for the delete RPC; reconciliation uses an uncancelled read when the outcome is unknown. Token cho RPC xoá; reconcile dùng lượt đọc không bị hủy khi kết quả chưa rõ.</param>
    /// <returns>A task completed after confirmed deletion and consistent selection persistence, or after an acknowledged failed delete is reconciled. Task hoàn tất sau khi xoá được xác nhận và lựa chọn được lưu nhất quán, hoặc sau khi reconcile lượt xoá lỗi.</returns>
    public Task DeleteLlmModelAsync(
        string name, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> engineActive,
        CancellationToken ct = default) =>
        DeleteModelAsync("llm", name, installed, engineActive, ct);

    private async Task DeleteModelAsync(
        string kind, string name, IReadOnlyCollection<string> installed,
        IReadOnlyCollection<string> engineActive, CancellationToken ct)
    {
        if (!TryBeginRun()) throw new InvalidOperationException("Setup is busy; retry model deletion later.");
        try
        {
            BeginModelDeletion(name, installed, engineActive, kind);
            Exception? deleteError = null;
            try
            {
                await _bridge.CallAsync($"{kind}.models.delete",
                    new JsonObject { ["name"] = name }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                deleteError = ex;
            }

            if (deleteError is null)
            {
                MarkDeletedConfirmed(installed, engineActive);
                ApplyConfirmedDeletion();
                return;
            }

            DeletionCatalog? catalog = await TryReadDeletionCatalogAsync(
                kind, name, CancellationToken.None).ConfigureAwait(false);
            if (catalog?.TargetInstalled == true)
            {
                RollbackModelDeletion();
                ExceptionDispatchInfo.Capture(deleteError).Throw();
                throw new InvalidOperationException("Unreachable", deleteError);
            }
            if (catalog?.TargetInstalled == false)
            {
                MarkDeletedConfirmed(catalog.Installed, catalog.EngineActive);
                ApplyConfirmedDeletion();
                return;
            }

            string guidance = $"Model deletion outcome is unknown; no selections or downloads were changed. Retry repair. Original error: {deleteError.Message}";
            MarkDeletionPending(guidance);
            throw new InvalidOperationException(guidance, deleteError);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Persists a model-deletion intent before the engine RPC and closes the pipeline gate until repair completes.
    /// Ghi intent xoá model trước RPC engine và đóng cổng pipeline tới khi repair hoàn tất.
    /// </summary>
    /// <param name="name">Catalog name intended for deletion. Tên catalog dự định xoá.</param>
    /// <param name="installed">Models expected to remain installed. Các model dự kiến còn cài.</param>
    /// <param name="engineActive">Models active in the engine. Các model engine đang dùng.</param>
    /// <param name="kind">Optional catalog family (<c>ocr</c>/<c>llm</c>); null infers it from current selections. Nhóm catalog tùy chọn (<c>ocr</c>/<c>llm</c>); null suy ra từ lựa chọn hiện tại.</param>
    /// <exception cref="InvalidOperationException">Thrown when another deletion needs repair or the journal cannot be persisted. Ném khi một lượt xoá khác cần repair hoặc không ghi được journal.</exception>
    public void BeginModelDeletion(
        string name, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> engineActive,
        string? kind = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(engineActive);
        if (kind is not null && kind is not "ocr" and not "llm")
            throw new ArgumentException("Model kind must be 'ocr' or 'llm'.", nameof(kind));
        if (_deletionJournalUnreadable)
            throw new InvalidOperationException("The model-deletion journal is unreadable; repair is required.");
        if (_deletionIntent is { } existing)
        {
            if (existing.Name != name)
                throw new InvalidOperationException(
                    $"Model deletion '{existing.Name}' still requires repair; cannot start '{name}'.");
            return; // keep the original rollback snapshot and persisted lifecycle state
        }

        AppSettings settings = _settings.Load();
        var rollback = new ModelDeletionRollback(
            _state.Dets is null ? null : [.. _state.Dets], _state.ActiveDet,
            _state.Recs is null ? null : [.. _state.Recs], _state.ActiveRec,
            _state.Langs is null ? null : [.. _state.Langs], _state.ActiveLang,
            _state.LlmModels is null ? null : [.. _state.LlmModels], _state.ActiveLlm,
            _state.IsComplete, settings.OcrDet, settings.OcrRec, settings.LlmActiveModel);
        _deletionIntent = new ModelDeletionIntent(
            name, [.. installed], [.. engineActive], "Pending", rollback,
            kind ?? InferDeletionKind(_state, name));
        _deletionSettingsWritten = false;
        _selectionPersistenceFailed = true;
        SkippedForSession = false;
        Error = null;
        Step = SetupStep.Checking;
        try
        {
            SaveDeletionIntent();
            Publish();
        }
        catch (Exception ex)
        {
            throw FailDeletionPersistence("model-deletion.json", ex);
        }
    }

    /// <summary>
    /// Removes a successfully deleted model from setup and settings and publishes the resulting lifecycle state.
    /// Loại model đã được engine xoá thành công khỏi setup và settings rồi publish trạng thái lifecycle.
    /// </summary>
    /// <param name="name">Deleted catalog name. Tên catalog đã xoá.</param>
    /// <param name="installed">Models still installed after deletion. Các model còn cài sau khi xoá.</param>
    /// <param name="engineActive">Models active in the engine. Các model engine đang dùng.</param>
    /// <exception cref="InvalidOperationException">Thrown after the coordinator becomes incomplete when either state file cannot be persisted. Ném sau khi coordinator chuyển chưa hoàn tất nếu một file trạng thái không ghi được.</exception>
    public void RemoveDeletedModel(
        string name, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> engineActive)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(engineActive);
        if (_deletionIntent is null) BeginModelDeletion(name, installed, engineActive);
        if (_deletionIntent?.Name != name)
            throw new InvalidOperationException("A different model deletion still requires repair.");
        MarkDeletedConfirmed(installed, engineActive);
        ApplyConfirmedDeletion();
    }

    private void MarkDeletedConfirmed(
        IReadOnlyCollection<string> installed, IReadOnlyCollection<string> engineActive)
    {
        ModelDeletionIntent intent = _deletionIntent
            ?? throw new InvalidOperationException("No model-deletion intent exists.");
        _deletionIntent = intent with
        {
            State = "DeletedConfirmed",
            Installed = [.. installed.Where(name => name != intent.Name)],
            EngineActive = [.. engineActive.Where(name => name != intent.Name)],
        };
        try
        {
            SaveDeletionIntent();
        }
        catch (Exception ex)
        {
            throw FailDeletionPersistence("model-deletion.json", ex);
        }
    }

    private void ApplyConfirmedDeletion()
    {
        ModelDeletionIntent intent = _deletionIntent
            ?? throw new InvalidOperationException("No confirmed model deletion exists.");
        if (intent.State != "DeletedConfirmed")
            throw new InvalidOperationException("Pending deletion intent is not proof of deletion.");

        SetupState originalSetup = RestoreSetupSelections(_state, intent.Rollback);
        (SetupState updatedSetup, _) = SetupModelSelection.AfterDelete(
            originalSetup, _settings.Load(), intent.Name, intent.Installed, intent.EngineActive);
        _selectionPersistenceFailed = true;
        SkippedForSession = false;
        Error = null;
        Step = SetupStep.Checking;
        _state = updatedSetup with { IsComplete = false };
        Publish();

        string target = "setup.json";
        try
        {
            _state.Save(); // always fail-closed before the settings write
            target = "settings.json";
            _settings.Update(currentSettings =>
            {
                (SetupState latestSetup, AppSettings latestSettings) = SetupModelSelection.AfterDelete(
                    originalSetup, currentSettings, intent.Name, intent.Installed, intent.EngineActive);
                updatedSetup = latestSetup;
                return latestSettings;
            });
            _deletionSettingsWritten = true;
            if (updatedSetup.IsComplete)
            {
                target = "setup.json";
                _state = _state with { IsComplete = true };
                _state.Save();
            }

            target = "setup.json/settings.json";
            if (!DeletionStateConsistent(intent, requireCompleteAnswers: false))
                throw new InvalidOperationException("Model deletion state is still inconsistent.");

            target = "model-deletion.json";
            ClearDeletionIntent();
            Error = null;
            Step = _state.IsComplete ? SetupStep.Done : FirstIncompleteQuestionStep(_state);
            Publish();
        }
        catch (Exception ex)
        {
            throw FailDeletionPersistence(target, ex);
        }
    }

    private void RollbackModelDeletion()
    {
        ModelDeletionIntent intent = _deletionIntent
            ?? throw new InvalidOperationException("No model-deletion intent exists.");
        if (intent.State != "Pending")
            throw new InvalidOperationException("A confirmed deletion cannot be rolled back as still installed.");

        ModelDeletionRollback? rollback = intent.Rollback;
        SetupState restored = RestoreSetupSelections(_state, rollback);
        AppSettings current = _settings.Load();
        string ocrDet = rollback?.OcrDet ?? current.OcrDet;
        string ocrRec = rollback?.OcrRec ?? current.OcrRec;
        string llmActive = rollback?.LlmActiveModel ?? current.LlmActiveModel;
        bool wasComplete = rollback?.IsComplete == true && SetupModelSelection.HasCompleteAnswers(restored);

        _selectionPersistenceFailed = true;
        Error = null;
        Step = SetupStep.Checking;
        _state = restored with { IsComplete = false };
        Publish();
        string target = "setup.json";
        try
        {
            _state.Save();
            target = "settings.json";
            _settings.Update(latest => latest with
            {
                OcrDet = ocrDet,
                OcrRec = ocrRec,
                LlmActiveModel = llmActive,
            });
            _deletionSettingsWritten = true; // cleanup requires a real write, even for a rejected RPC
            target = "setup.json";
            _state = restored with { IsComplete = wasComplete };
            _state.Save();
            target = "setup.json/settings.json";
            if (!RollbackStateConsistent(rollback, wasComplete))
                throw new InvalidOperationException("Model deletion rollback is still inconsistent.");

            target = "model-deletion.json";
            ClearDeletionIntent();
            Error = null;
            Step = _state.IsComplete ? SetupStep.Done : FirstIncompleteQuestionStep(_state);
            Publish();
        }
        catch (Exception ex)
        {
            throw FailDeletionPersistence(target, ex);
        }
    }

    private bool RollbackStateConsistent(ModelDeletionRollback? rollback, bool wasComplete)
    {
        AppSettings settings = _settings.Load();
        if (rollback is null) return _state.IsComplete == wasComplete;
        return _state.IsComplete == wasComplete
            && SameNames(_state.Dets, rollback.Dets)
            && SameNames(_state.Recs, rollback.Recs)
            && SameNames(_state.Langs, rollback.Langs)
            && SameNames(_state.LlmModels, rollback.LlmModels)
            && _state.ActiveDet == rollback.ActiveDet
            && _state.ActiveRec == rollback.ActiveRec
            && _state.ActiveLang == rollback.ActiveLang
            && _state.ActiveLlm == rollback.ActiveLlm
            && settings.OcrDet == rollback.OcrDet
            && settings.OcrRec == rollback.OcrRec
            && settings.LlmActiveModel == rollback.LlmActiveModel;

        static bool SameNames(List<string>? left, List<string>? right) =>
            left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static SetupState RestoreSetupSelections(SetupState current, ModelDeletionRollback? rollback) =>
        rollback is null ? current : current with
        {
            Dets = rollback.Dets is null ? null : [.. rollback.Dets],
            ActiveDet = rollback.ActiveDet,
            Recs = rollback.Recs is null ? null : [.. rollback.Recs],
            ActiveRec = rollback.ActiveRec,
            Langs = rollback.Langs is null ? null : [.. rollback.Langs],
            ActiveLang = rollback.ActiveLang,
            LlmModels = rollback.LlmModels is null ? null : [.. rollback.LlmModels],
            ActiveLlm = rollback.ActiveLlm,
            IsComplete = rollback.IsComplete,
        };

    private static string? InferDeletionKind(SetupState state, string name)
    {
        bool ocr = (state.Dets?.Contains(name, StringComparer.Ordinal) == true)
            || (state.Recs?.Contains(name, StringComparer.Ordinal) == true)
            || (state.Langs?.Contains(name, StringComparer.Ordinal) == true);
        bool llm = state.LlmModels?.Contains(name, StringComparer.Ordinal) == true;
        return ocr == llm ? null : ocr ? "ocr" : "llm";
    }

    private async Task<bool> ResolveDeletionBeforeSetupAsync(CancellationToken ct)
    {
        if (_deletionIntent is not { } intent) return true;
        if (intent.State == "DeletedConfirmed")
        {
            ApplyConfirmedDeletion();
            return true;
        }

        DeletionCatalog? catalog = await FindDeletionCatalogAsync(intent, ct).ConfigureAwait(false);
        if (catalog?.TargetInstalled == true)
        {
            RollbackModelDeletion();
            return true;
        }
        if (catalog?.TargetInstalled == false)
        {
            MarkDeletedConfirmed(catalog.Installed, catalog.EngineActive);
            ApplyConfirmedDeletion();
            return true;
        }

        MarkDeletionPending("Model deletion is still unconfirmed; selections and downloads remain unchanged. Retry repair.");
        return false;
    }

    private async Task<DeletionCatalog?> FindDeletionCatalogAsync(ModelDeletionIntent intent, CancellationToken ct)
    {
        if (intent.Kind is "ocr" or "llm")
            return await TryReadDeletionCatalogAsync(intent.Kind, intent.Name, ct).ConfigureAwait(false);

        foreach (string kind in new[] { "ocr", "llm" })
        {
            DeletionCatalog? catalog = await TryReadDeletionCatalogAsync(kind, intent.Name, ct).ConfigureAwait(false);
            if (catalog?.TargetInstalled is not null) return catalog;
        }
        return null;
    }

    private async Task<DeletionCatalog?> TryReadDeletionCatalogAsync(
        string kind, string name, CancellationToken ct)
    {
        try
        {
            JsonNode? reply = await _bridge.CallAsync($"{kind}.models.list", null, ct).ConfigureAwait(false);
            JsonArray? entries = kind == "ocr"
                ? (reply as JsonObject)?["catalog"] as JsonArray
                : reply as JsonArray;
            if (entries is null) return null;
            var installed = new List<string>();
            var engineActive = new List<string>();
            bool found = false;
            bool targetInstalled = false;
            foreach (JsonNode? node in entries)
            {
                if (node is not JsonObject entry
                    || entry["name"] is not JsonValue nameValue
                    || !nameValue.TryGetValue(out string? entryName)) continue;
                bool isInstalled = entry["installed"] is JsonValue installedValue
                    && installedValue.TryGetValue(out bool installedFlag) && installedFlag;
                bool inUse = entry["inUse"] is JsonValue inUseValue
                    && inUseValue.TryGetValue(out bool inUseFlag) && inUseFlag;
                if (isInstalled) installed.Add(entryName);
                if (inUse) engineActive.Add(entryName);
                if (entryName == name)
                {
                    found = true;
                    targetInstalled = isInstalled;
                }
            }
            if (kind == "ocr" && reply is JsonObject root && root["active"] is JsonObject active)
            {
                foreach (string key in new[] { "det", "rec" })
                {
                    if (active[key]?.GetValue<string>() is { } activeName) engineActive.Add(activeName);
                }
            }
            return new DeletionCatalog(
                found ? targetInstalled : null,
                installed.Where(candidate => candidate != name).Distinct(StringComparer.Ordinal).ToArray(),
                engineActive.Where(candidate => candidate != name).Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "setup: could not reconcile {Kind} model deletion for {Name}", kind, name);
            return null;
        }
    }

    private void MarkDeletionPending(string message)
    {
        _selectionPersistenceFailed = true;
        _state = _state with { IsComplete = false };
        Error = SetupMessage.Of("setup.fail.unexpected", message);
        EnterFailed(SetupStep.Checking);
    }

    private sealed record DeletionCatalog(
        bool? TargetInstalled, IReadOnlyCollection<string> Installed, IReadOnlyCollection<string> EngineActive);

    private void RestoreDeletionIntent()
    {
        if (!File.Exists(_deletionJournalPath)) return;
        try
        {
            ModelDeletionIntent? intent = JsonSerializer.Deserialize<ModelDeletionIntent>(
                File.ReadAllText(_deletionJournalPath), DeletionJournalJsonOptions);
            if (intent is null || string.IsNullOrWhiteSpace(intent.Name)
                || intent.Installed is null || intent.EngineActive is null
                || intent.State is not ("Pending" or "DeletedConfirmed"))
            {
                throw new JsonException("Invalid model-deletion journal.");
            }

            _deletionIntent = intent;
            _deletionSettingsWritten = false;
            _selectionPersistenceFailed = true;
            SkippedForSession = false;
            if (intent.State == "Pending")
            {
                // Journal existence is only intent. Keep every answer untouched until Retry/Start
                // reconciles the live catalog; never turn a rejected RPC into AfterDelete on restart.
                _state = _state with { IsComplete = false };
                Error = SetupMessage.Of("settings.status.saveFailed");
                Step = SetupStep.Failed;
                _lastFailedStep = SetupStep.Checking;
                return;
            }

            SetupState original = RestoreSetupSelections(_state, intent.Rollback);
            (SetupState updatedSetup, _) = SetupModelSelection.AfterDelete(
                original, _settings.Load(), intent.Name, intent.Installed, intent.EngineActive);
            _state = updatedSetup with { IsComplete = false };
            Step = SetupStep.Checking;
            try
            {
                // DeletedConfirmed is durable proof, so repair may continue after restart. The journal
                // remains authoritative until a real settings write and consistency check complete.
                _state.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "setup: could not persist restored deletion intent");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _deletionJournalUnreadable = true;
            _selectionPersistenceFailed = true;
            SkippedForSession = false;
            _state = _state with { IsComplete = false };
            Error = SetupMessage.Of("settings.status.saveFailed");
            Step = SetupStep.Failed;
            _logger?.LogError(ex, "setup: deletion journal is unreadable");
        }
    }

    private void SaveDeletionIntent()
    {
        string? dir = Path.GetDirectoryName(_deletionJournalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = _deletionJournalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_deletionIntent, DeletionJournalJsonOptions));
        File.Move(tmp, _deletionJournalPath, overwrite: true);
    }

    private void ClearDeletionIntent()
    {
        if (_deletionIntent is not null && !_deletionSettingsWritten)
            throw new InvalidOperationException("Cannot clear deletion intent before settings.json is written.");
        File.Delete(_deletionJournalPath);
        _deletionIntent = null;
        _deletionJournalUnreadable = false;
        _deletionSettingsWritten = false;
        _selectionPersistenceFailed = false;
    }

    private InvalidOperationException FailDeletionPersistence(string target, Exception ex)
    {
        _selectionPersistenceFailed = true;
        _state = _state with { IsComplete = false };
        Error = SetupMessage.Of("settings.status.saveFailed");
        EnterFailed(SetupStep.Checking);
        _logger?.LogError(ex, "setup: deletion persistence failed for {Path}", target);
        return new InvalidOperationException(
            $"Model deletion could not update {target}; setup repair is required: {ex.Message}", ex);
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
        // A deletion journal is stronger than the session escape hatch: skipping is allowed again only
        // after setup/settings are consistent and the journal has been cleared.
        SkippedForSession = !_selectionPersistenceFailed
            && _deletionIntent is null
            && !_deletionJournalUnreadable;
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
            // Three steps ALWAYS execute when reached — their internals skip the expensive part:
            //   • the two download steps: a GGUF/runtime deleted after completion is re-fetched on the
            //     next run, and a bare done marker from an older session never hides a missing artifact
            //     (BA review 2026-11-17: the LLM half of reconcile);
            //   • Syncing: the real frozen sync validates/repairs the venv and picks up shipped
            //     pyproject/lock changes (BA 2026-11-17 — user directive: always run).
            // Applying also always runs while a deletion journal exists: an old done marker can never
            // substitute for the successful settings write required before journal cleanup.
            // Ba bước LUÔN thực thi khi tới lượt — phần bên trong tự bỏ qua phần đắt:
            //   • hai bước tải: GGUF/runtime bị xoá sau khi hoàn tất sẽ được tải bù ở lượt kế, marker
            //     done của phiên cũ không bao giờ che mất artifact còn thiếu (review BA 2026-11-17:
            //     nửa LLM của reconcile);
            //   • Syncing: frozen sync thật xác minh/sửa venv và nhận pyproject/lock mới theo bản
            //     cập nhật (BA 2026-11-17 — chỉ đạo của user: luôn chạy).
            bool deletionNeedsSettingsWrite = step == SetupStep.Applying && _deletionIntent is not null;
            if (IsStepDone(step)
                && step is not (SetupStep.DownloadingModels or SetupStep.DownloadingLlmRuntime or SetupStep.Syncing)
                && !deletionNeedsSettingsWrite)
            {
                continue;
            }

            StepOutcome outcome = await RunStepAsync(step, ct).ConfigureAwait(false);
            if (outcome != StepOutcome.Done) return; // Paused or Failed — state already set by the step
        }

        EnterDone();
    }

    /// <summary>
    /// Resumes after a pre-pipeline answer was saved: pauses at the next unanswered step-3/step-4
    /// question (languages, then LLM models), or runs the pipeline from its first step when everything
    /// is answered.
    /// Chạy tiếp sau khi một câu trả lời pre-pipeline được lưu: dừng ở câu hỏi bước 3/bước 4 chưa trả
    /// lời kế (Ngôn ngữ, rồi model LLM), hoặc chạy pipeline từ bước đầu khi đã trả lời hết.
    /// </summary>
    private static SetupStep FirstIncompleteQuestionStep(SetupState state)
    {
        if (!HasOcrAnswers(state)) return SetupStep.ChoosingOcrModels;
        if (state.Langs is null
            || (state.ActiveLang is not null
                && !state.Langs.Contains(state.ActiveLang, StringComparer.Ordinal)))
        {
            return SetupStep.ChoosingLanguages;
        }
        return SetupStep.ChoosingLlmModels;
    }

    private static bool HasOcrAnswers(SetupState state) =>
        state.Dets is { Count: > 0 }
        && state.Recs is { Count: > 0 }
        && state.ActiveDet is not null
        && state.Dets.Contains(state.ActiveDet, StringComparer.Ordinal)
        && state.ActiveRec is not null
        && state.Recs.Contains(state.ActiveRec, StringComparer.Ordinal);

    private async Task ContinueAfterAnswersAsync(CancellationToken ct)
    {
        if (!HasOcrAnswers(_state))
        {
            Step = SetupStep.ChoosingOcrModels;
            Publish();
            _logger?.LogInformation("setup: paused at {Step}", Step);
            return;
        }

        if (_state.Langs is null
            || (_state.ActiveLang is not null
                && !_state.Langs.Contains(_state.ActiveLang, StringComparer.Ordinal)))
        {
            Step = SetupStep.ChoosingLanguages;
            Publish();
            _logger?.LogInformation("setup: paused at {Step}", Step);
            return;
        }

        if (_state.LlmModels is null
            || (_state.ActiveLlm is not null
                && !_state.LlmModels.Contains(_state.ActiveLlm, StringComparer.Ordinal)))
        {
            Step = SetupStep.ChoosingLlmModels;
            Publish();
            _logger?.LogInformation("setup: paused at {Step}", Step);
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
            SetupStep.Applying => await RunApplyingStepAsync(ct).ConfigureAwait(false),
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
    /// the SAME flavor installed → STILL runs the real frozen sync, never a mark-done shortcut: the
    /// sync validates and repairs the venv (hidden environment damage) and picks up shipped
    /// pyproject/lock changes (a new library from an app update) — with a warm uv cache it is quick,
    /// and skipping it is exactly how a broken environment survives a "re-run setup" (BA 2026-11-17).
    /// Vào bước Syncing (Amendment A2): dò flavor paddle đã cài trước. Chưa cài gì → thẳng vào vòng
    /// sync; đang cài flavor KHÁC → dừng ở <see cref="SetupStep.ConfirmingFlavorSwitch"/> cho tới khi
    /// user xác nhận (không sync trước đó); đã cài ĐÚNG flavor → VẪN chạy frozen sync thật, không bao
    /// giờ chỉ đánh dấu done: sync xác minh và sửa venv (hỏng ngầm) đồng thời nhận thay đổi
    /// pyproject/lock theo bản cập nhật (thư viện mới) — cache uv ấm thì nhanh, và bỏ qua nó chính là
    /// cách một môi trường hỏng sống sót qua "chạy lại setup" (BA 2026-11-17).
    /// </summary>
    private async Task<StepOutcome> RunSyncStepAsync(CancellationToken ct)
    {
        OcrFlavor? installed = await _uvSync.InstalledPaddleFlavorAsync(ct).ConfigureAwait(false);

        if (installed is { } current && current != Flavor && !_flavorSwitchConfirmed)
        {
            EnterFlavorSwitchPause(current);
            return StepOutcome.Paused;
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
    /// <c>cpu</c>. A valid installed runtime skips when current matches latest or the check is offline;
    /// an older build updates, while a missing artifact repairs even at the same tag. Every decision logs
    /// flavor/current/latest/installed. Downloads poll <c>llm.runtime.status</c> every
    /// <see cref="_modelPollInterval"/> until the matching attempt commits or the
    /// <see cref="_modelDownloadTimeout"/> window elapses (then a fresh attempt follows). Failure is
    /// soft like DownloadingModels: the failure popup offers retry/skip-for-session and Settings can
    /// download the runtime later.
    /// DownloadingLlmRuntime (spec 2026-11-14 D13 / vòng 2 spec §7): runtime llama.cpp không bao giờ
    /// nằm trong zip release, nên wizard cài nó ở đây — nhưng chỉ khi bước 4 có chọn model LLM; bước 4
    /// để trống thì bước này tự đánh dấu done mà không gọi RPC nào. Flavor runtime theo máy — flavor
    /// wheel GPU (đã qua cổng capability ở VerifyingSidecar) ⇒ <c>cuda</c>, còn lại <c>cpu</c>. Runtime
    /// đã có artifact và current==latest (hoặc không check được khi offline) thì bỏ qua; bản cũ được cập
    /// nhật, artifact thiếu được repair kể cả cùng tag. Mọi quyết định log flavor/current/latest/installed.
    /// Lượt tải poll <c>llm.runtime.status</c> mỗi <see cref="_modelPollInterval"/> cho tới khi đúng attempt
    /// commit hoặc hết cửa sổ
    /// <see cref="_modelDownloadTimeout"/> (lượt thử mới sẽ chạy). Thất bại là mềm như
    /// DownloadingModels: popup màn lỗi cho retry/bỏ qua phiên, và Settings có thể tải runtime sau.
    /// </summary>
    private async Task<StepOutcome> RunDownloadLlmRuntimeStepAsync(CancellationToken ct)
    {
        // Bước 4 để trống ⇒ bỏ qua thật: không cần runtime, không gọi RPC nào (spec §7).
        if (_state.LlmModels is null or { Count: 0 })
        {
            MarkStepDone(SetupStep.DownloadingLlmRuntime);
            return StepOutcome.Done;
        }

        string runtimeFlavor = Flavor == OcrFlavor.Gpu ? "cuda" : "cpu";
        string? downloadError = null;
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            try
            {
                downloadError = null;
                bool installed = await IsLlmRuntimeInstalledAsync(runtimeFlavor, ct).ConfigureAwait(false);
                (string? current, string? latest) = await CheckLlmRuntimeAsync(runtimeFlavor, ct).ConfigureAwait(false);
                string action = installed
                    ? current is null || latest is null
                        ? "skip-check-unavailable"
                        : current == latest ? "skip-current" : "update"
                    : current is not null && current == latest
                        ? "repair-same-tag"
                        : current is null ? "install" : "repair";
                _logger?.LogInformation(
                    "setup: runtime llama.cpp decision flavor={Flavor} current={Current} latest={Latest} installed={Installed} action={Action}",
                    runtimeFlavor, current, latest, installed, action);
                if (installed && (current is null || latest is null || current == latest))
                {
                    // Valid same-tag installs still skip; an unavailable check remains a soft offline pass.
                    // Bản cùng tag còn artifact vẫn bỏ qua; không check được vẫn cho qua mềm khi offline.
                    MarkStepDone(SetupStep.DownloadingLlmRuntime);
                    return StepOutcome.Done;
                }

                JsonNode? accepted = await _bridge
                    .CallAsync("llm.runtime.download", new JsonObject { ["flavor"] = runtimeFlavor }, ct)
                    .ConfigureAwait(false);
                long downloadAttempt = accepted is JsonObject acceptedObj ? ReadLong(acceptedObj, "attempt") : 0;

                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(_modelDownloadTimeout);
                while (true)
                {
                    JsonNode? status = await _bridge.CallAsync("llm.runtime.status",
                        new JsonObject { ["flavor"] = runtimeFlavor }, ct).ConfigureAwait(false);
                    if (IsLlmRuntimeAttemptSuperseded(status, downloadAttempt))
                    {
                        throw new InvalidOperationException("llm runtime download superseded by a newer attempt");
                    }
                    // Failure of THIS attempt wins over any previous install; a queued/in-flight attempt
                    // cannot complete from that old binary either.
                    // Lỗi của attempt NÀY thắng mọi bản cũ; attempt xếp hàng/đang chạy cũng không
                    // được hoàn tất nhờ binary cũ.
                    string? attemptState = ReadString(status, "attemptState");
                    bool attemptStatePresent = status is JsonObject statusObj
                        && statusObj.ContainsKey("attemptState");
                    if (attemptState == "Cancelled")
                    {
                        throw new OperationCanceledException("llm runtime download was cancelled");
                    }
                    if (attemptState == "Failed" || IsLlmRuntimeDownloadFailed(status))
                    {
                        downloadError = ReadString(status, "downloadError");
                        throw new InvalidOperationException("llm runtime download failed (reported by status)");
                    }
                    bool committed = attemptState == "Succeeded";
                    // Only a genuinely absent field identifies legacy Core; present null/wrong-type data
                    // is schema drift and must never complete from an old binary.
                    // Chỉ field thật sự vắng mới nhận diện Core cũ; null/sai kiểu là schema drift và
                    // không bao giờ được hoàn tất nhờ binary cũ.
                    bool legacyComplete = !attemptStatePresent && !IsLlmRuntimeDownloading(status);
                    if ((committed || legacyComplete) && IsLlmRuntimeInstalled(status)) break;
                    // Tiến độ tải zip runtime (spec vòng 2 §9): status có `progress` → đưa vào snapshot
                    // và PUBLISH — không publish thì UI không bao giờ vẽ lại dòng % + ETA.
                    var downloads = ParseRuntimeDownloads(status);
                    _downloads = [.. downloads.Select(d => d with { BytesPerSec = SmoothBps(d.Name, d.BytesPerSec) })];
                    Publish();
                    await Task.Delay(_modelPollInterval, window.Token).ConfigureAwait(false);
                }

                _emaBps.Clear();
                _downloads = [];
                MarkStepDone(SetupStep.DownloadingLlmRuntime);
                return StepOutcome.Done;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { /* the download window elapsed - count as a failed attempt */ }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "setup: tải runtime LLM lần {Attempt}/{Total} thất bại", attempt, MaxRetries + 1);
            }
            finally
            {
                _emaBps.Clear(); // lượt thử mới đo speed lại từ đầu
            }
        }

        _downloads = [];
        Publish();
        Error = SetupMessage.Of(downloadError is null ? "setup.fail.llmRuntime" : "setup.fail.llmRuntimeDetail",
            (MaxRetries + 1).ToString(), _modelDownloadTimeout.TotalMinutes.ToString("0"), downloadError!);
        return StepOutcome.Failed;
    }

    /// <summary>
    /// The optional <c>progress</c> object of <c>llm.runtime.status</c> as a download entry named
    /// <c>llama.cpp</c> (the runtime zip is the only thing step 8 downloads); empty when the status
    /// carries no progress, so the UI keeps its indeterminate bar.
    /// Object <c>progress</c> tuỳ chọn của <c>llm.runtime.status</c> dưới dạng một entry tải tên
    /// <c>llama.cpp</c> (zip runtime là thứ duy nhất bước 8 tải); rỗng khi status không có tiến độ,
    /// để UI giữ thanh vô định.
    /// </summary>
    private static List<SetupDownloadInfo> ParseRuntimeDownloads(JsonNode? status)
    {
        var downloads = new List<SetupDownloadInfo>();
        if (status?["progress"] is JsonObject p)
        {
            downloads.Add(new SetupDownloadInfo(
                "llama.cpp", ReadLong(p, "receivedBytes"), ReadLong(p, "totalBytes"), ReadDouble(p, "bytesPerSec")));
        }
        return downloads;
    }

    /// <summary>
    /// EMA (α = 0.3) of one download's bytes/sec: the first sample passes through, later samples are
    /// smoothed so the ETA does not jump between polls (spec vòng 2 §9). A <see langword="null"/> speed
    /// keeps the last smoothed value (the UI then shows its reconnect note).
    /// EMA (α = 0,3) của tốc độ một lượt tải: mẫu đầu đi thẳng, các mẫu sau được làm mượt để ETA không
    /// nhảy giữa các poll (spec vòng 2 §9). Tốc độ <see langword="null"/> giữ giá trị mượt cuối (UI
    /// khi đó hiện note đang kết nối lại).
    /// </summary>
    /// <param name="name">Download name (EMA key). Tên download (khóa EMA).</param>
    /// <param name="bytesPerSec">Raw speed of this poll, or <see langword="null"/>. Tốc độ thô của poll này, hoặc <see langword="null"/>.</param>
    /// <returns>The smoothed speed, or <see langword="null"/> when nothing measured yet. Tốc độ đã mượt, hoặc <see langword="null"/> khi chưa đo được.</returns>
    private double? SmoothBps(string name, double? bytesPerSec)
    {
        if (bytesPerSec is not { } value)
        {
            return _emaBps.TryGetValue(name, out double previous) ? previous : null;
        }

        double smoothed = _emaBps.TryGetValue(name, out double prev) ? 0.3 * value + 0.7 * prev : value;
        _emaBps[name] = smoothed;
        return smoothed;
    }

    /// <summary>
    /// The (current, latest) llama.cpp tags from <c>llm.runtime.check</c>; (null, null) when the call
    /// fails (offline) so a network problem never blocks the wizard.
    /// Cặp tag llama.cpp (current, latest) từ <c>llm.runtime.check</c>; (null, null) khi lời gọi lỗi
    /// (mất mạng) để sự cố mạng không bao giờ chặn wizard.
    /// </summary>
    private async Task<(string? Current, string? Latest)> CheckLlmRuntimeAsync(string runtimeFlavor, CancellationToken ct)
    {
        try
        {
            JsonNode? check = await _bridge.CallAsync("llm.runtime.check",
                new JsonObject { ["flavor"] = runtimeFlavor }, ct).ConfigureAwait(false);
            return (ReadString(check, "current"), ReadString(check, "latest"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "setup: llm.runtime.check thất bại (mất mạng?) — coi bản đang cài là ok");
            return (null, null);
        }
    }

    private static string? ReadString(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;

    /// <summary>
    /// Whether <c>llm.runtime.status</c> reports an installed runtime (the server binary exists on
    /// disk); a malformed or missing field counts as not installed.
    /// <c>llm.runtime.status</c> có báo runtime đã cài không (binary server tồn tại trên đĩa); field
    /// thiếu hoặc sai định dạng coi như chưa cài.
    /// </summary>
    private async Task<bool> IsLlmRuntimeInstalledAsync(string runtimeFlavor, CancellationToken ct)
    {
        JsonNode? status = await _bridge.CallAsync("llm.runtime.status",
            new JsonObject { ["flavor"] = runtimeFlavor }, ct).ConfigureAwait(false);
        return IsLlmRuntimeInstalled(status);
    }

    private static bool IsLlmRuntimeDownloading(JsonNode? status) =>
        status is JsonObject obj
        && obj["downloading"] is JsonValue value
        && value.TryGetValue<bool>(out bool downloading)
        && downloading;

    private static bool IsLlmRuntimeAttemptSuperseded(JsonNode? status, long attempt) =>
        attempt > 0 && status is JsonObject obj && ReadLong(obj, "attempt") != attempt;

    /// <summary>
    /// The <c>installed</c> flag of one <c>llm.runtime.status</c> node; a malformed or missing field
    /// counts as not installed.
    /// Cờ <c>installed</c> của một node <c>llm.runtime.status</c>; field thiếu hoặc sai định dạng coi
    /// như chưa cài.
    /// </summary>
    private static bool IsLlmRuntimeInstalled(JsonNode? status) =>
        status is JsonObject obj
            && obj["installed"] is JsonValue value
            && value.TryGetValue<bool>(out bool installed)
            && installed;

    /// <summary>
    /// The <c>downloadFailed</c> flag of one <c>llm.runtime.status</c> node; a malformed or missing
    /// field counts as "not failed" (the poll window remains the backstop).
    /// Cờ <c>downloadFailed</c> của một node <c>llm.runtime.status</c>; field thiếu hoặc sai định dạng
    /// coi như "chưa fail" (cửa sổ poll vẫn là chốt an toàn).
    /// </summary>
    private static bool IsLlmRuntimeDownloadFailed(JsonNode? status) =>
        status is JsonObject obj
            && obj["downloadFailed"] is JsonValue value
            && value.TryGetValue<bool>(out bool failed)
            && failed;

    /// <summary>
    /// DownloadingModels: downloads every model the saved answers need — the det+rec pair of every
    /// ticked tier, the main language's legacy rec when v6 does not cover it, and each extra
    /// language's legacy rec (<see cref="OcrModelChoices.NeededModels"/>) — skipping models the
    /// catalog reports installed (a <c>.partial</c> model counts as not installed — the sidecar
    /// re-downloads it idempotently), then polls <c>ocr.models.list</c> every
    /// <see cref="_modelPollInterval"/> until ALL needed models are installed or the
    /// <see cref="_modelDownloadTimeout"/> window elapses (a fresh attempt follows). A needed model
    /// reporting <c>downloadError</c> while idle ends its attempt immediately after the progress snapshot.
    /// DownloadingModels: tải mọi model mà câu trả lời đã lưu cần — cặp det+rec của từng tier được
    /// tick, rec legacy của ngôn ngữ chính khi ngoài phủ v6, và rec legacy của từng ngôn ngữ bổ sung
    /// (<see cref="OcrModelChoices.NeededModels"/>) — bỏ qua model catalog báo đã cài (model có
    /// <c>.partial</c> coi như chưa cài — sidecar tải lại idempotent), rồi poll <c>ocr.models.list</c>
    /// mỗi <see cref="_modelPollInterval"/> cho tới khi MỌI model cần đều installed hoặc hết cửa sổ
    /// <see cref="_modelDownloadTimeout"/> (lượt thử mới sẽ chạy). Model cần tải báo <c>downloadError</c>
    /// khi đã dừng sẽ kết thúc lượt thử ngay sau khi snapshot tiến độ được publish.
    /// </summary>
    private async Task<StepOutcome> RunDownloadModelsStepAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            try
            {
                IReadOnlyList<string> needed = NeededModelsForState();
                IReadOnlyList<string> llmNeeded = _state.LlmModels ?? [];

                JsonNode? list = await _bridge.CallAsync("ocr.models.list", null, ct).ConfigureAwait(false);
                foreach (string name in needed)
                {
                    if (!IsInstalledInCatalog(list, name))
                        await _bridge
                            .CallAsync("ocr.models.download", new JsonObject { ["name"] = name }, ct)
                            .ConfigureAwait(false);
                }

                if (llmNeeded.Count > 0)
                {
                    JsonNode? llmList = await _bridge.CallAsync("llm.models.list", null, ct).ConfigureAwait(false);
                    foreach (string name in llmNeeded)
                    {
                        if (!IsInstalledInLlmCatalog(llmList, name))
                            await _bridge
                                .CallAsync("llm.models.download", new JsonObject { ["name"] = name }, ct)
                                .ConfigureAwait(false);
                    }
                }

                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(_modelDownloadTimeout);
                while (true)
                {
                    list = await _bridge.CallAsync("ocr.models.list", null, ct).ConfigureAwait(false);
                    bool ocrDone = needed.All(name => IsInstalledInCatalog(list, name));
                    var downloads = new List<SetupDownloadInfo>(ParseDownloads(list, "catalog"));

                    bool llmDone = true;
                    JsonNode? llmList = null;
                    if (llmNeeded.Count > 0)
                    {
                        llmList = await _bridge.CallAsync("llm.models.list", null, ct).ConfigureAwait(false);
                        llmDone = llmNeeded.All(name => IsInstalledInLlmCatalog(llmList, name));
                        downloads.AddRange(ParseDownloads(llmList, null));
                    }

                    // EMA-smooth the speeds and tag each entry with its "model i/N" counter (spec vòng 2
                    // §9) — position in the needed list, hidden (0/0) for entries outside it.
                    // Làm mượt tốc độ bằng EMA và gắn bộ đếm "model i/N" (spec vòng 2 §9) — vị trí trong
                    // danh sách cần tải, ẩn (0/0) với entry ngoài danh sách.
                    _downloads = [.. downloads.Select(d => d with
                    {
                        BytesPerSec = SmoothBps(d.Name, d.BytesPerSec),
                        Index = needed.Contains(d.Name) ? needed.ToList().IndexOf(d.Name) + 1 : 0,
                        Total = needed.Contains(d.Name) ? needed.Count : 0,
                    })];
                    Publish();
                    if (SetupModelSelection.FindFailedDownload(list, "catalog", needed) is { } ocrFailure)
                        throw new InvalidOperationException(
                            $"OCR model '{ocrFailure.Name}' download failed: {ocrFailure.Error}");
                    if (llmNeeded.Count > 0
                        && SetupModelSelection.FindFailedDownload(llmList, null, llmNeeded) is { } llmFailure)
                    {
                        throw new InvalidOperationException(
                            $"LLM model '{llmFailure.Name}' download failed: {llmFailure.Error}");
                    }
                    if (ocrDone && llmDone) break;
                    await Task.Delay(_modelPollInterval, window.Token).ConfigureAwait(false);
                }

                _emaBps.Clear();
                _downloads = [];
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

        _downloads = [];
        Error = SetupMessage.Of("setup.fail.models",
            (MaxRetries + 1).ToString(), _modelDownloadTimeout.TotalMinutes.ToString("0"));
        return StepOutcome.Failed;
    }

    /// <summary>
    /// Whether the LLM catalog entry for <paramref name="name"/> reports installed; false when the
    /// list is malformed or the entry is missing (the catalog is a flat JSON array).
    /// Entry catalog LLM của <paramref name="name"/> có báo installed không; false khi list sai định
    /// dạng hoặc thiếu entry (catalog là mảng JSON phẳng).
    /// </summary>
    private static bool IsInstalledInLlmCatalog(JsonNode? list, string name)
    {
        if (list is not JsonArray array) return false;

        foreach (JsonNode? entry in array)
        {
            if (entry is not JsonObject e) continue;
            if (e["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out string? entryName)) continue;
            if (entryName != name) continue;
            return e["installed"] is JsonValue installedValue && installedValue.TryGetValue<bool>(out bool installed) && installed;
        }

        return false;
    }

    /// <summary>
    /// The in-flight download entries carrying progress out of a list node: OCR lists keep their
    /// entries under <paramref name="arrayKey"/> (<c>catalog</c>); LLM lists are flat (null key).
    /// Các entry tải đang chạy có tiến độ trong một node list: list OCR để entry dưới
    /// <paramref name="arrayKey"/> (<c>catalog</c>); list LLM phẳng (key null).
    /// </summary>
    private static List<SetupDownloadInfo> ParseDownloads(JsonNode? list, string? arrayKey)
    {
        var downloads = new List<SetupDownloadInfo>();
        JsonArray? entries = arrayKey is null
            ? list as JsonArray
            : (list as JsonObject)?[arrayKey] as JsonArray;
        if (entries is null) return downloads;

        foreach (JsonNode? entry in entries)
        {
            if (entry is not JsonObject e) continue;
            if (e["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out string? name)) continue;
            if (e["progress"] is not JsonObject p) continue;
            downloads.Add(new SetupDownloadInfo(
                name, ReadLong(p, "receivedBytes"), ReadLong(p, "totalBytes"), ReadDouble(p, "bytesPerSec")));
        }

        return downloads;
    }

    private static long ReadLong(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<long>(out long value) ? value : 0;

    private static double? ReadDouble(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<double>(out double value) ? value : null;

    /// <summary>
    /// The models the download step must ensure for the saved answers, with the coordinator's
    /// defensive fallback (small det/rec, no legacy recs) when a field is missing.
    /// Các model bước tải phải đảm bảo theo câu trả lời đã lưu, kèm fallback phòng thủ của
    /// coordinator (det/rec small, không rec legacy) khi thiếu field.
    /// </summary>
    private IReadOnlyList<string> NeededModelsForState() =>
        OcrModelChoices.NeededModels(
            _state.Dets ?? ["PP-OCRv6_small_det"],
            _state.Recs ?? ["PP-OCRv6_small_rec"],
            _state.Langs ?? []);

    /// <summary>
    /// Applying always computes <see cref="OcrModelChoices.ExpectedPair"/>, saves that pair and
    /// <c>LlmActiveModel</c>, then calls <c>ocr.models.set</c>. Only a successful save and OCR RPC may
    /// mark the step done; either failure uses the existing retry loop. The later <c>llm.models.set</c>
    /// remains best-effort (a refusal only logs — settings are already written, spec §6).
    /// Applying (Review Focus #5 — LUÔN chạy, kể cả khi không tải model nào): tính cặp từ câu trả lời
    /// đã lưu (<see cref="OcrModelChoices.ExpectedPair"/> — rec legacy đang chấm thắng rec v6) và ghi
    /// cặp đó cùng <c>LlmActiveModel</c> vào settings qua <see cref="AppSettingsService.Update"/>. Khi có
    /// LLM "Dùng chính", gọi thêm <c>llm.models.set</c> kiểu best-effort (bị từ chối chỉ log — settings
    /// đã ghi, spec §6). Sau khi lưu settings, Applying gọi <c>ocr.models.set</c> và chỉ đánh dấu xong
    /// khi RPC này thành công; lỗi lưu hoặc RPC OCR đều đi qua vòng retry hiện có.
    /// </summary>
    private async Task<StepOutcome> RunApplyingStepAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            try
            {
                (string det, string rec) = OcrModelChoices.ExpectedPair(
                    _state.ActiveDet ?? "PP-OCRv6_small_det", _state.ActiveLang, _state.ActiveRec ?? "PP-OCRv6_small_rec");
                _settings.Update(settings => settings with
                {
                    OcrDet = det,
                    OcrRec = rec,
                    // A failed deletion must never resurrect the stale model when the repair answers "no LLM".
                    LlmActiveModel = _state.ActiveLlm
                        ?? (_selectionPersistenceFailed && _state.LlmModels is { Count: 0 }
                            ? string.Empty
                            : settings.LlmActiveModel),
                });
                if (_deletionIntent is not null) _deletionSettingsWritten = true;

                await _bridge
                    .CallAsync("ocr.models.set", new JsonObject { ["det"] = det, ["rec"] = rec }, ct)
                    .ConfigureAwait(false);

                if (_state.ActiveLlm is { } llm)
                {
                    try
                    {
                        await _bridge
                            .CallAsync("llm.models.set", new JsonObject { ["name"] = llm }, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "setup: llm.models.set thất bại cho {Model} (bỏ qua — settings vẫn ghi)", llm);
                    }
                }

                MarkStepDone(SetupStep.Applying);
                return StepOutcome.Done;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "setup: áp dụng settings lần {Attempt}/{Total} thất bại", attempt, MaxRetries + 1);
            }
        }

        Error = SetupMessage.Of("setup.fail.applying", (MaxRetries + 1).ToString());
        return StepOutcome.Failed;
    }

    /// <summary>
    /// Whether the named model's directory exists under the models cache - a pure file-system check
    /// that also runs before the sidecar is alive. The probe is the STRICT sidecar rule
    /// (<see cref="IsModelDirInstalled"/>), not a bare directory check: a half-downloaded model must
    /// never count as installed, or a repair rerun would skip re-downloading it.
    /// Thư mục của model tên này có nằm trong cache model không - kiểm tra file-system thuần, chạy
    /// được cả trước khi sidecar sống. Probe là luật NGHIÊM của sidecar
    /// (<see cref="IsModelDirInstalled"/>), không phải kiểm tra thư mục trần: model tải dở tuyệt đối
    /// không được coi là đã cài, nếu không lượt sửa lỗi sẽ bỏ qua không tải lại.
    /// </summary>
    private bool ModelInstalled(string modelName) =>
        _modelDirExists(Path.Combine(SetupPaths.ModelsDir, SetupState.OfficialModelsSubdir, modelName));

    /// <summary>
    /// Whether every selected artifact is still on disk: the sidecar Python, all selected OCR/GGUF
    /// files without partial markers, and the active llama.cpp runtime when LLM models are selected.
    /// Injected seams keep this a cheap file/JSON preflight in production and tests.
    /// Kiểm tra mọi artifact đã chọn còn trên đĩa: Python sidecar, tất cả OCR/GGUF đã chọn không có
    /// marker partial và runtime llama.cpp active khi có model LLM. Seam inject giữ preflight này chỉ
    /// đọc file/JSON rẻ trong production và test.
    /// </summary>
    private bool ArtifactsPresent() =>
        SetupModelSelection.ArtifactsPresent(
            _state, SetupPaths.VenvPythonExe, SetupPaths.ModelsDir, _llmRoot,
            _fileExists, _modelDirExists, _readText);

    /// <summary>
    /// The sidecar's installed rule mirrored (<c>ocr/models.py</c>): the model directory exists, is
    /// non-empty, and no <c>&lt;name&gt;.partial</c> marker sits next to it. An interrupted download
    /// leaves both the half-filled directory and the marker, so a bare <c>Directory.Exists</c> counted
    /// a half-downloaded model as installed and the repair rerun skipped it (BA report 2026-11-17).
    /// Luật "đã cài" của sidecar được mirror (<c>ocr/models.py</c>): thư mục model tồn tại, không
    /// rỗng, và không có marker <c>&lt;tên&gt;.partial</c> cạnh nó. Lượt tải dang dở để lại cả thư
    /// mục nửa vời lẫn marker, nên <c>Directory.Exists</c> trần đã từng coi model tải dở là đã cài và
    /// lượt sửa lỗi bỏ qua nó (báo cáo BA 2026-11-17).
    /// </summary>
    /// <param name="modelDir">Full path of the model directory under <c>official_models</c>. Đường dẫn đầy đủ của thư mục model trong <c>official_models</c>.</param>
    public static bool IsModelDirInstalled(string modelDir) =>
        Directory.Exists(modelDir)
        && Directory.EnumerateFileSystemEntries(modelDir).Any()
        && !File.Exists(modelDir + ".partial");

    /// <summary>
    /// Light consistency pass run at the end of Checking and after every saved answer: when a needed
    /// model is gone from disk the download step must run again, and when the settings pair (or the
    /// active LLM) diverges from the stored answers the applying step must run again. The OCR half
    /// needs the det/rec and language answers; the pair half needs the det/rec answers, so an old
    /// machine still mid-questions is repaired part by part. A machine where everything matches keeps
    /// every marker, so a rerun stays a fast pass-through. Persists only when it cleared something.
    /// Lượt so khớp nhẹ chạy cuối bước Checking và sau mỗi câu trả lời được lưu: model cần mà biến mất
    /// khỏi đĩa ⇒ bước tải phải chạy lại; cặp trong settings (hoặc LLM đang chạy) lệch với câu trả lời
    /// đã lưu ⇒ bước áp dụng phải chạy lại. Phần OCR cần câu trả lời det/rec + ngôn ngữ; phần cặp cần
    /// det/rec — máy cũ đang trả lời dở vẫn được chữa từng phần. Máy khớp hết giữ nguyên mọi marker nên
    /// rerun đi qua nhanh. Chỉ lưu khi có xoá gì.
    /// </summary>
    private void ReconcileMarkers()
    {
        if (_state.Steps is null) return;

        var steps = new Dictionary<string, string>(_state.Steps);
        bool changed = false;

        if (_state.Dets is not null && _state.Langs is not null
            && NeededModelsForState().Any(name => !ModelInstalled(name))
            && steps.Remove(StepKey(SetupStep.DownloadingModels)))
        {
            changed = true;
        }

        if (_state.Dets is not null)
        {
            (string det, string rec) = OcrModelChoices.ExpectedPair(
                _state.ActiveDet ?? "PP-OCRv6_small_det", _state.ActiveLang, _state.ActiveRec ?? "PP-OCRv6_small_rec");
            AppSettings settings = _settings.Load();
            bool llmMismatch = _state.ActiveLlm is not null
                ? settings.LlmActiveModel != _state.ActiveLlm
                : _deletionIntent is not null
                    && (_state.LlmModels is { Count: 0 }
                        ? !string.IsNullOrEmpty(settings.LlmActiveModel)
                        : settings.LlmActiveModel == _deletionIntent.Name);
            bool pairMismatch = settings.OcrDet != det || settings.OcrRec != rec || llmMismatch;
            if (pairMismatch && steps.Remove(StepKey(SetupStep.Applying)))
            {
                changed = true;
            }
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

    private bool DeletionStateConsistent(
        ModelDeletionIntent intent, bool requireCompleteAnswers = true)
    {
        bool completeAnswers = SetupModelSelection.HasCompleteAnswers(_state);
        if (requireCompleteAnswers && !completeAnswers) return false;
        AppSettings settings = _settings.Load();
        if (settings.LlmActiveModel == intent.Name) return false;
        if (_state.ActiveDet == intent.Name || _state.ActiveRec == intent.Name
            || _state.ActiveLang == intent.Name || _state.ActiveLlm == intent.Name)
        {
            return false;
        }
        if (ContainsModel(_state.Dets, intent.Name)
            || ContainsModel(_state.Recs, intent.Name)
            || ContainsModel(_state.Langs, intent.Name)
            || ContainsModel(_state.LlmModels, intent.Name))
        {
            return false;
        }
        if (!completeAnswers) return true;

        (string det, string rec) = OcrModelChoices.ExpectedPair(
            _state.ActiveDet!, _state.ActiveLang, _state.ActiveRec!);
        if (settings.OcrDet != det || settings.OcrRec != rec) return false;
        return _state.LlmModels is not { Count: 0 } || string.IsNullOrEmpty(settings.LlmActiveModel);

        static bool ContainsModel(List<string>? names, string name) =>
            names?.Contains(name, StringComparer.Ordinal) == true;
    }

    private void EnterDone()
    {
        if (_deletionJournalUnreadable
            || (_selectionPersistenceFailed && _deletionIntent is null)
            || (_deletionIntent is { } intent
                && (!_deletionSettingsWritten || !DeletionStateConsistent(intent))))
        {
            // Never clear a deletion failure by skipping Applying through an old marker.
            _state = _state with { IsComplete = false };
            Error = SetupMessage.Of("settings.status.saveFailed");
            EnterFailed(SetupStep.Applying);
            return;
        }

        _state = _state with { IsComplete = true };
        _state.Save();
        if (_deletionIntent is not null)
        {
            try
            {
                ClearDeletionIntent();
            }
            catch (Exception ex)
            {
                throw FailDeletionPersistence("model-deletion.json", ex);
            }
        }
        _selectionPersistenceFailed = false;
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
