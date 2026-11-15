using System.Text.Json.Nodes;
using ReTran_App.Services;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="SetupCoordinator"/>: the first-run wizard state machine — retry ×3 on the
/// soft steps with the hard ones (Checking, VerifyingFiles) going straight to Failed without retry,
/// resume from <c>setup.json</c> (done steps skipped, language never re-asked), the Amendment-A2
/// flavor-switch pause (a different installed flavor must NOT be synced before the user confirms),
/// the GPU compute-capability gate on <c>sidecar.device</c> (CC &lt; 7.5 ⇒ distinct failure plus a
/// CPU-switch entry point that loops back into a <c>--group cpu</c> sync), and R59 full re-sync
/// retries when a failed sync leaves no venv behind. Every dependency is faked: an arg-aware process
/// runner behind the real <see cref="UvSyncRunner"/> (the same pattern as UvSyncRunnerTests), a
/// recording <see cref="ISetupCoreBridge"/>, and injected funcs for disk/network/file checks; state
/// and settings live in temp files.
/// Test cho <see cref="SetupCoordinator"/>: máy trạng thái wizard chạy lần đầu — retry ×3 ở các bước
/// mềm trong khi các bước cứng (Checking, VerifyingFiles) fail thẳng không retry, resume từ
/// <c>setup.json</c> (skip bước đã done, không hỏi lại ngôn ngữ), bước dừng xác nhận đổi flavor theo
/// Amendment A2 (flavor đang cài khác flavor đã check thì KHÔNG được sync trước khi user xác nhận),
/// cổng compute capability GPU trên verb <c>sidecar.device</c> (CC &lt; 7.5 ⇒ lỗi riêng kèm entry point
/// chuyển CPU, vòng lại bước sync <c>--group cpu</c>), và retry full re-sync theo R59 khi sync fail để
/// lại không venv. Mọi dependency đều là fake: process runner phân biệt tham số đứng sau
/// <see cref="UvSyncRunner"/> thật (pattern của UvSyncRunnerTests), <see cref="ISetupCoreBridge"/> ghi
/// lại calls, funcs inject cho đĩa/mạng/file; state và settings nằm trong file temp.
/// </summary>
public class SetupCoordinatorTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;

    // Message wizard giờ là key + args (SetupMessage) — helper resolve đúng như UI (I18nService, VI)
    // để các assert chuỗi hiện có vẫn kiểm tra nguyên văn thông điệp người dùng thấy.
    private static string Text(SetupMessage? message)
    {
        var i18n = new I18nService();
        i18n.Set("vi");
        return message is null ? string.Empty : i18n.Fmt(message.Key, message.Args.ToArray());
    }

    // Chuỗi WMI đo thật trên máy dev (branch 16 ⇒ họ 6xx, driver 617.42 ≥ sàn 576.02) ⇒ flavor GPU.
    // Measured on the dev machine (branch 16 ⇒ 6xx family, driver 617.42 ≥ the 576.02 floor) ⇒ GPU.
    private static readonly VideoAdapterInfo NvidiaCurrent =
        new("NVIDIA GeForce RTX 5060 Ti", true, "32.0.16.1742");

    private readonly string _dir;
    private readonly string _setupJson;
    private readonly string _settingsJson;
    private readonly FakeUv _uvFake;
    private readonly UvSyncRunner _syncRunner;
    private readonly FakeCoreBridge _bridge;
    private readonly AppSettingsService _settings;

    private bool _closeAppCalled;
    private int _changedCount;

    public SetupCoordinatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"retran-setupcoord-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _setupJson = Path.Combine(_dir, "setup.json");
        _settingsJson = Path.Combine(_dir, "settings.json");
        _uvFake = new FakeUv();
        _syncRunner = new UvSyncRunner(
            _uvFake, @"C:\retran-test-fake\runtimes\win-x64\uv.exe", Path.Combine(_dir, "ReTran.OCR"));
        _bridge = new FakeCoreBridge();
        _settings = new AppSettingsService(_settingsJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private static IVideoAdapterSource Adapters(params VideoAdapterInfo[] items) => new FakeAdapters(items);

    /// <summary>
    /// Builds a coordinator over the shared fakes; state and settings point at temp files, model
    /// polling runs at 10 ms so download tests finish in milliseconds. The host-reachability seam
    /// answers "reachable" by default; tests may replace it to hold the Checking step open. The
    /// download window (shared by the OCR-model and llm-runtime steps) defaults to 5 s; a test can
    /// shrink it to keep a never-finishing download cheap.
    /// Dựng coordinator trên các fake chung; state và settings trỏ file temp, poll model chạy 10 ms
    /// để test download kết thúc trong vài mili-giây. Seam probe mạng mặc định trả lời "tới được";
    /// test có thể thay nó để giữ bước Checking mở ra. Cửa sổ tải (dùng chung cho bước tải model OCR
    /// và runtime LLM) mặc định 5 s; test có thể thu nhỏ để lượt tải không bao giờ xong chạy rẻ.
    /// </summary>
    private SetupCoordinator NewCoordinator(
        IVideoAdapterSource? adapters = null,
        Func<string, long>? diskFree = null,
        Func<string, bool>? isHostReachable = null,
        Func<string, bool>? fileExists = null,
        TimeSpan? modelDownloadTimeout = null,
        Func<string, int>? countModelEntries = null)
    {
        var coordinator = new SetupCoordinator(
            adapters ?? Adapters(NvidiaCurrent),
            diskFree ?? (_ => 100 * GB),
            isHostReachable ?? (_ => true),
            _syncRunner,
            _bridge,
            _settings,
            () => _closeAppCalled = true,
            stateFactory: () => SetupState.Load(_setupJson),
            fileExists: fileExists ?? (_ => true),
            countModelEntries: countModelEntries ?? (_ => 0),
            modelPollInterval: TimeSpan.FromMilliseconds(10),
            modelDownloadTimeout: modelDownloadTimeout ?? TimeSpan.FromSeconds(5));
        coordinator.Changed += () => Interlocked.Increment(ref _changedCount);
        return coordinator;
    }

    private void WriteSetupJson(string json) => File.WriteAllText(_setupJson, json);

    private SetupState ReadSetupJson() => SetupState.Load(_setupJson);

    /// <summary>
    /// The flavor a recorded uv sync call installed — also asserts the exact Amendment-A2 argv shape
    /// (one frozen group sync, nothing else).
    /// Flavor mà lệnh uv sync đã ghi nhận cài — đồng thời assert đúng hình dạng argv A2 (một group sync
    /// --frozen duy nhất, không thêm gì khác).
    /// </summary>
    private static OcrFlavor FlavorOfSyncCall(string[] args)
    {
        Assert.Equal("sync", args[0]);
        Assert.Equal("--no-default-groups", args[1]);
        Assert.Equal("--group", args[2]);
        Assert.Equal("--frozen", args[4]);
        return args[3] == "gpu" ? OcrFlavor.Gpu : OcrFlavor.Cpu;
    }

    // ── brief: bước cơ bản của máy trạng thái ─────────────────────────────────────────────────────

    [Fact]
    public async Task Start_WithoutLanguage_PausesAtChoosingLanguage()
    {
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.ChoosingLanguage, coordinator.Step);
        Assert.Equal(OcrFlavor.Gpu, coordinator.Flavor);
        Assert.Null(coordinator.Error);
        // Checking passed and was persisted; the wizard now waits for the language answer.
        // Checking đạt và đã lưu; wizard chờ user trả lời ngôn ngữ.
        SetupState state = ReadSetupJson();
        Assert.Equal("gpu", state.Flavor);
        Assert.Null(state.Language);
        Assert.Equal("done", state.Steps!["checking"]);
        Assert.Empty(_uvFake.SyncCalls);
        Assert.Empty(_bridge.Calls);
    }

    [Fact]
    public async Task ContinueWithLanguage_RunsRemainingStepsAndCompletes()
    {
        var coordinator = NewCoordinator();
        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.ChoosingLanguage, coordinator.Step);

        await coordinator.ContinueWithLanguageAsync("vi", TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.True(coordinator.IsComplete);
        SetupState state = ReadSetupJson();
        Assert.True(state.IsComplete);
        Assert.Equal("vi", state.Language);
        // One full GPU sync; both models downloaded (the fake marks them installed on download).
        // Một lệnh sync GPU duy nhất; cả hai model được tải (fake đánh dấu đã cài khi download).
        Assert.Single(_uvFake.SyncCalls);
        Assert.Equal(OcrFlavor.Gpu, FlavorOfSyncCall(_uvFake.SyncCalls[0]));
        Assert.Equal([FakeCoreBridge.DetModel, FakeCoreBridge.RecModel], _bridge.Downloaded);
        AppSettings settings = _settings.Load();
        Assert.Equal(FakeCoreBridge.DetModel, settings.OcrDet);
        Assert.Equal(FakeCoreBridge.RecModel, settings.OcrRec);
    }

    [Fact]
    public async Task FailedStep_RetriesThreeTimes_ThenFails()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        for (int i = 0; i < SetupCoordinator.MaxRetries + 1; i++)
            _uvFake.SyncResults.Enqueue((1, $"sync-fail-{i + 1}"));
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.NotNull(coordinator.Error);
        // 1 initial attempt + MaxRetries (3) retries = exactly 4 uv sync calls.
        // 1 lần thử đầu + MaxRetries (3) lần retry = đúng 4 lệnh uv sync.
        Assert.Equal(SetupCoordinator.MaxRetries + 1, _uvFake.SyncCalls.Count);
        Assert.Empty(_bridge.Calls); // never reached the sidecar step / chưa tới bước sidecar
    }

    [Fact]
    public async Task Retry_RestartsAttemptCounter()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        for (int i = 0; i < SetupCoordinator.MaxRetries + 1; i++)
            _uvFake.SyncResults.Enqueue((1, "fail"));
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.Equal(SetupCoordinator.MaxRetries + 1, _uvFake.SyncCalls.Count);

        // The next attempt succeeds: the counter restarts from zero (no cap carried over).
        // Lần thử kế thành công: bộ đếm khởi động lại từ đầu (không mang trần cũ sang).
        await coordinator.RetryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Equal(SetupCoordinator.MaxRetries + 2, _uvFake.SyncCalls.Count); // 4 failed + 1 ok
    }

    [Fact]
    public async Task Resume_SkipsDoneSteps_KeepsLanguage()
    {
        // Everything through VerifyingSidecar is done and the language was already answered
        // (Review Focus #3): resume must not re-sync nor ask again.
        // Mọi thứ tới VerifyingSidecar đã xong và ngôn ngữ đã được trả lời (Review Focus #3):
        // resume không được sync lại hay hỏi lại.
        WriteSetupJson("""
            {
              "flavor": "gpu",
              "language": "ko",
              "steps": {
                "checking": "done",
                "choosinglanguage": "done",
                "verifyingfiles": "done",
                "syncing": "done",
                "verifysidecar": "done"
              }
            }
            """);
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Empty(_uvFake.SyncCalls); // no uv sync re-run / không chạy lại uv sync
        SetupState state = ReadSetupJson();
        Assert.Equal("ko", state.Language);
        Assert.True(state.IsComplete);
    }

    [Fact]
    public async Task AllModelsAlreadyInstalled_StillWritesSettingsPair()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        _bridge.Installed.Add(FakeCoreBridge.DetModel);
        _bridge.Installed.Add(FakeCoreBridge.RecModel);
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Empty(_bridge.Downloaded); // nothing to download / không có gì để tải
        AppSettings settings = _settings.Load();
        // Review Focus #5: Applying ALWAYS writes the pair, even when nothing was downloaded.
        // Review Focus #5: bước Applying LUÔN ghi cặp model, kể cả khi không tải model nào.
        Assert.Equal(FakeCoreBridge.DetModel, settings.OcrDet);
        Assert.Equal(FakeCoreBridge.RecModel, settings.OcrRec);
    }

    [Fact]
    public async Task PartialModel_IsDownloadedAgain_OnResume()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        // det installed; rec carries a .partial marker ⇒ the catalog reports installed=false.
        // det đã cài; rec còn marker .partial ⇒ catalog báo installed=false.
        _bridge.Installed.Add(FakeCoreBridge.DetModel);
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        // Only the missing model is requested again (Review Focus #2 — idempotent on the sidecar).
        // Chỉ model thiếu mới được yêu cầu tải lại (Review Focus #2 — idempotent phía sidecar).
        Assert.Equal([FakeCoreBridge.RecModel], _bridge.Downloaded);
    }

    [Fact]
    public async Task VerifyFiles_MissingUv_GoesToFailed_WithoutRetry()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        // uv.exe is present while Checking runs (probe #1) but gone by VerifyingFiles (probe #2):
        // the AV/SmartScreen quarantine window between the two steps.
        // uv.exe còn nguyên khi Checking chạy (lần dò #1) nhưng biến mất tới lúc VerifyingFiles
        // (lần dò #2): cửa sổ cách ly AV/SmartScreen giữa hai bước.
        int uvProbes = 0;
        Func<string, bool> fileExists = path =>
            path.EndsWith("uv.exe", StringComparison.OrdinalIgnoreCase) ? ++uvProbes == 1 : true;
        var coordinator = NewCoordinator(fileExists: fileExists);

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.NotNull(coordinator.Error);
        Assert.Contains("giải nén lại zip", Text(coordinator.Error), StringComparison.Ordinal);
        // Hard-fail step: exactly one VerifyingFiles probe (no auto-retry), nothing downstream ran.
        // Bước fail cứng: đúng 1 lần dò của VerifyingFiles (không retry tự động), không bước nào sau chạy.
        Assert.Equal(2, uvProbes);
        Assert.Empty(_uvFake.SyncCalls);
        Assert.Empty(_bridge.Calls);
    }

    [Fact]
    public async Task Check_FailHard_GoesToFailed_Directly()
    {
        var coordinator = NewCoordinator(diskFree: _ => 1 * GB); // the GPU flavor needs 8 GB

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.NotNull(coordinator.Error);
        Assert.Contains("GB", Text(coordinator.Error), StringComparison.Ordinal);
        Assert.Empty(_uvFake.SyncCalls);
        Assert.Empty(_bridge.Calls);
        // A hard Checking failure persists no "done" step at all.
        // Check fail cứng không lưu bất kỳ bước "done" nào.
        Assert.False(File.Exists(_setupJson));
    }

    [Fact]
    public async Task Check_FailHard_RetryRerunsTheGate_FromTheTop()
    {
        // Disk starts below the GPU floor (hard fail), then recovers: RetryAsync must re-run Checking
        // from the top — NOT jump into the pipeline at a later step.
        long freeBytes = 1 * GB;
        var coordinator = NewCoordinator(diskFree: _ => freeBytes);

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.Failed, coordinator.Step);

        freeBytes = 100 * GB; // the user freed space
        await coordinator.RetryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.ChoosingLanguage, coordinator.Step); // re-ran Checking, now waiting on language
        Assert.Empty(_uvFake.SyncCalls);
        Assert.Empty(_bridge.Calls);
    }

    [Fact]
    public async Task Check_FailHard_RetryWithPersistedLanguage_ProceedsIntoPipeline()
    {
        // Regression (review of b2fb834): the Checking retry branch used to fall through to
        // RunPipelineAsync(Checking) — Array.IndexOf returns -1 for a non-pipeline step, and
        // PipelineSteps[-1] throws IndexOutOfRangeException. Trigger: language already answered →
        // re-run Setup → Checking hard-fails (e.g. full disk) → user fixes it → clicks "Thử lại".
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        long freeBytes = 1 * GB; // below the GPU floor ⇒ Checking hard-fails
        var coordinator = NewCoordinator(diskFree: _ => freeBytes);

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.Failed, coordinator.Step);

        freeBytes = 100 * GB; // the user freed space, then retries
        await coordinator.RetryAsync(TestContext.Current.CancellationToken);

        // Must NOT throw — and must actually proceed into the pipeline (the first steps ran).
        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Single(_uvFake.SyncCalls); // VerifyingFiles passed ⇒ Syncing really ran
        Assert.Equal(OcrFlavor.Gpu, FlavorOfSyncCall(_uvFake.SyncCalls[0]));
    }

    [Fact]
    public async Task Skip_CallsCloseApp()
    {
        var coordinator = NewCoordinator();
        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.ChoosingLanguage, coordinator.Step);

        coordinator.Skip();

        Assert.True(_closeAppCalled);
        // Skip writes no state: the language is still unanswered on disk (spec §3.6).
        // Skip không ghi state gì thêm: ngôn ngữ vẫn chưa được trả lời trên đĩa (spec §3.6).
        Assert.Null(ReadSetupJson().Language);
    }

    [Fact]
    public async Task Changed_FiresAfterStateChanges()
    {
        var coordinator = NewCoordinator();
        int before = _changedCount;

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        int afterStart = _changedCount;
        Assert.True(afterStart > before, "Changed must fire while the wizard moves Checking → ChoosingLanguage");

        await coordinator.ContinueWithLanguageAsync("vi", TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.True(_changedCount > afterStart, "Changed must fire on every subsequent step transition through Done");
    }

    // ── Amendment A2: đổi flavor phải được user xác nhận ──────────────────────────────────────────

    [Fact]
    public async Task FlavorMismatch_PausesForConfirmation_NoSyncBeforeConfirm()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        _uvFake.Installed = OcrFlavor.Cpu; // the machine currently runs the CPU wheel
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        // The wizard pauses BEFORE any uv sync: installed (CPU) differs from checked (GPU).
        // Wizard DỪNG TRƯỚC mọi lệnh uv sync: bản đã cài (CPU) khác bản đã check (GPU).
        Assert.Equal(SetupStep.ConfirmingFlavorSwitch, coordinator.Step);
        Assert.NotNull(coordinator.PendingFlavorSwitch);
        Assert.Equal(OcrFlavor.Cpu, coordinator.PendingFlavorSwitch!.Installed);
        Assert.Equal(OcrFlavor.Gpu, coordinator.PendingFlavorSwitch.Target);
        Assert.Contains("Đổi sang bản mới", Text(coordinator.Notice), StringComparison.Ordinal);
        Assert.Contains("1,6 GB", Text(coordinator.Notice), StringComparison.Ordinal);
        Assert.Contains("0,3 GB", Text(coordinator.Notice), StringComparison.Ordinal);
        Assert.Empty(_uvFake.SyncCalls); // no sync before confirmation / không sync trước xác nhận

        // The user confirms: the checked flavor is synced and setup.json records it.
        // User xác nhận: sync đúng flavor đã check và setup.json ghi lại.
        await coordinator.ConfirmFlavorSwitchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Single(_uvFake.SyncCalls);
        Assert.Equal(OcrFlavor.Gpu, FlavorOfSyncCall(_uvFake.SyncCalls[0]));
        Assert.Equal("gpu", ReadSetupJson().Flavor);
    }

    // ── Addendum §3: cổng compute capability sống ở bước xác minh sidecar ─────────────────────────

    [Fact]
    public async Task GpuFlavor_LowCapability_Fails_AndCpuSwitchSyncsCpu()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        _uvFake.Installed = OcrFlavor.Gpu; // installed matches ⇒ no flavor-switch pause
        _bridge.DeviceResult = new JsonObject
        {
            ["device"] = "gpu:0",
            ["capability"] = new JsonArray(6, 1), // CC 6.1 < 7.5 (cu129 fatbins start at sm_75)
        };
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.NotNull(coordinator.Error);
        Assert.Contains("6.1", Text(coordinator.Error), StringComparison.Ordinal);
        Assert.Contains("PaddlePaddle 3.4.0", Text(coordinator.Error), StringComparison.Ordinal);
        Assert.True(coordinator.CanSwitchToCpuFlavor);

        // The CPU-switch entry point loops back into Syncing with flavor Cpu — the user does not have
        // to hunt for a "re-run setup" button (addendum §3).
        // Entry point chuyển CPU vòng lại bước Syncing với flavor Cpu — user không phải tự tìm nút
        // "chạy lại setup" (addendum §3).
        await coordinator.SwitchToCpuFlavorAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Equal(OcrFlavor.Cpu, FlavorOfSyncCall(_uvFake.SyncCalls[^1])); // --group cpu
        Assert.Equal("cpu", ReadSetupJson().Flavor);
        // The capability probe ran exactly once (GPU attempt only; the CPU path never asks).
        // Verb sidecar.device được gọi đúng 1 lần (chỉ ở lượt GPU; đường CPU không hỏi).
        Assert.Single(_bridge.Calls, static c => c.Method == "sidecar.device");
    }

    [Fact]
    public async Task GpuFlavor_NullCapability_FailsWithCcGateNotice()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        _uvFake.Installed = OcrFlavor.Gpu;
        _bridge.DeviceResult = new JsonObject { ["device"] = "cpu", ["capability"] = null };
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.NotNull(coordinator.Error);
        Assert.Contains("PaddlePaddle 3.4.0", Text(coordinator.Error), StringComparison.Ordinal);
        Assert.True(coordinator.CanSwitchToCpuFlavor);
    }

    [Fact]
    public async Task CpuFlavor_SidecarVerificationSkipsDeviceCheck()
    {
        WriteSetupJson("""{"flavor":"cpu","language":"vi"}""");
        _uvFake.Installed = null;
        var coordinator = NewCoordinator(adapters: Adapters()); // no NVIDIA ⇒ CPU flavor

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Single(_uvFake.SyncCalls);
        Assert.Equal(OcrFlavor.Cpu, FlavorOfSyncCall(_uvFake.SyncCalls[0]));
        // CPU flavor: ping + catalog are enough — the capability probe is never issued.
        // Flavor CPU: chỉ cần ping + catalog — không gọi verb kiểm tra capability.
        Assert.DoesNotContain(_bridge.Calls, static c => c.Method == "sidecar.device");
    }

    // ── T9: wizard tự tải runtime llama.cpp (spec 2026-11-14 D13) ─────────────────────────────────

    [Fact]
    public async Task LlmRuntime_DownloadsCudaFlavor_OnGpuMachine()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        // Máy GPU (đã qua cổng capability) ⇒ runtime cuda; bước persist để rerun không tải lại.
        Assert.Equal(["cuda"], _bridge.LlmRuntimeDownloads);
        Assert.Equal("done", ReadSetupJson().Steps!["downloadingllmruntime"]);
    }

    [Fact]
    public async Task LlmRuntime_DownloadsCpuFlavor_OnCpuMachine()
    {
        WriteSetupJson("""{"flavor":"cpu","language":"vi"}""");
        _uvFake.Installed = null;
        var coordinator = NewCoordinator(adapters: Adapters()); // no NVIDIA ⇒ CPU flavor

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Equal(["cpu"], _bridge.LlmRuntimeDownloads);
    }

    [Fact]
    public async Task LlmRuntime_AlreadyInstalled_SkipsDownload()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        _bridge.LlmRuntimeInstalled = true;
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        // Rerun trên máy đã có runtime: một lời gọi status là đủ, không tải lại.
        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Empty(_bridge.LlmRuntimeDownloads);
    }

    [Fact]
    public async Task LlmRuntime_NeverInstalls_FailsSoftly_ThenRetrySucceeds()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        _bridge.LlmRuntimeDownloadCompletes = false; // "network" that never finishes
        var coordinator = NewCoordinator(modelDownloadTimeout: TimeSpan.FromMilliseconds(150));

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        // Bước mềm như DownloadingModels: 4 lần thử (mỗi lần hết cửa sổ tải), rồi Failed kèm
        // hướng dẫn tải lại từ Settings — không chặn user bỏ qua phiên.
        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.NotNull(coordinator.Error);
        Assert.Contains("runtime llama.cpp", Text(coordinator.Error), StringComparison.Ordinal);
        Assert.Equal(SetupCoordinator.MaxRetries + 1, _bridge.LlmRuntimeDownloads.Count);

        // User bấm "Thử lại" với mạng hoạt động: bước xong, pipeline chạy tiếp tới Done.
        _bridge.LlmRuntimeDownloadCompletes = true;
        await coordinator.RetryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Equal(SetupCoordinator.MaxRetries + 2, _bridge.LlmRuntimeDownloads.Count);
    }

    [Fact]
    public async Task LlmRuntime_ResumeFromOldSetupJson_RunsTheNewStep()
    {
        // setup.json của bản cũ: mọi bước cũ đã done, chưa hoàn tất — bước mới vắng marker phải
        // chạy (và chỉ nó: các bước cũ vẫn được skip, không sync/không tải model lại).
        WriteSetupJson("""
            {
              "flavor": "gpu",
              "language": "vi",
              "steps": {
                "checking": "done",
                "choosinglanguage": "done",
                "verifyingfiles": "done",
                "syncing": "done",
                "verifysidecar": "done",
                "downloadingmodels": "done",
                "applying": "done"
              }
            }
            """);
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Equal(["cuda"], _bridge.LlmRuntimeDownloads);
        Assert.Empty(_uvFake.SyncCalls);
        Assert.Empty(_bridge.Downloaded);
        Assert.Equal("done", ReadSetupJson().Steps!["downloadingllmruntime"]);
    }

    // ── Checklist tiến độ Checking (2026-11-15) ──────────────────────────────────────────────────

    [Fact]
    public void Checks_BeforeAnyRun_Empty()
    {
        var coordinator = NewCoordinator();

        Assert.Empty(coordinator.Checks);
    }

    [Fact]
    public async Task CheckingProgress_Checks_ContainNineItems_InOrder_AllOk()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        // setup.json đã có language → lượt chạy đi trọn pipeline trên fake tới Done; Checking đã
        // điền xong danh sách trước đó và danh sách còn nguyên sau khi xong.
        Assert.Equal(SetupStep.Done, coordinator.Step);
        string[] expectedIds =
        [
            "files", "env", "models", "arch", "gpu", "disk",
            "net:pypi.org", "net:www.paddlepaddle.org.cn", "net:github.com",
        ];
        Assert.Equal(expectedIds, coordinator.Checks.Select(i => i.Id));
        Assert.All(coordinator.Checks, i => Assert.Equal(SetupCheckState.Ok, i.State));
        // fileExists fake mặc định true → env "đã có"; models fake 0 → "chưa có".
        Assert.Equal("setup.check.env.present",
            coordinator.Checks.Single(i => i.Id == "env").Detail!.Key);
        Assert.Equal("setup.check.models.absent",
            coordinator.Checks.Single(i => i.Id == "models").Detail!.Key);
    }

    [Fact]
    public async Task Checks_ResetOnRetry_NoStaleFailures()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        bool githubReachable = false;
        var coordinator = NewCoordinator(
            isHostReachable: host => githubReachable || host != "github.com");

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Failed, coordinator.Step);
        Assert.Contains(coordinator.Checks, i => i.Id == "net:github.com" && i.State == SetupCheckState.Fail);

        // Mạng sống lại: retry phải dựng lại danh sách sạch — không dính dòng ✗ cũ.
        githubReachable = true;
        await coordinator.RetryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.Equal(9, coordinator.Checks.Count);
        Assert.DoesNotContain(coordinator.Checks, i => i.State == SetupCheckState.Fail);
        Assert.Equal("net:github.com", coordinator.Checks.Last().Id);
        Assert.Equal(SetupCheckState.Ok, coordinator.Checks.Last().State);
    }

    // ── R59: sync fail có thể để lại KHÔNG venv ⇒ mỗi retry là full re-sync ────────────────────────

    [Fact]
    public async Task SyncFailure_LeavesNoVenv_EachRetryIsFullResync()
    {
        WriteSetupJson("""{"flavor":"gpu","language":"vi"}""");
        const string tail = "error: failed to install: paddlepaddle-gpu==3.4.0 (network timeout)";
        for (int i = 0; i < SetupCoordinator.MaxRetries + 1; i++)
            _uvFake.SyncResults.Enqueue((1, tail));
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Failed, coordinator.Step);
        // R59: a failed sync can leave NO venv at all (uv replaces the environment before it fails),
        // so every one of the 4 attempts must be a full re-sync — the identical frozen group argv,
        // never a resume/patch. The failing command's tail is surfaced on the error screen.
        // R59: sync fail có thể để lại KHÔNG còn venv (uv thay env trước khi fail), nên cả 4 lần thử
        // đều phải là full re-sync — cùng một argv group --frozen, không có chế độ "sửa tiếp". Đuôi
        // output của lệnh fail phải lên màn hình lỗi.
        Assert.Equal(SetupCoordinator.MaxRetries + 1, _uvFake.SyncCalls.Count);
        foreach (string[] args in _uvFake.SyncCalls)
            Assert.Equal(["sync", "--no-default-groups", "--group", "gpu", "--frozen"], args);
        Assert.Contains(tail, Text(coordinator.Error), StringComparison.Ordinal);
    }

    // ── resume nhanh cho máy đã xong ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_AlreadyComplete_SkipsToDoneWithoutWork()
    {
        WriteSetupJson("""
            {
              "flavor": "gpu",
              "language": "vi",
              "isComplete": true,
              "steps": {
                "checking": "done",
                "choosinglanguage": "done",
                "verifyingfiles": "done",
                "syncing": "done",
                "verifysidecar": "done",
                "downloadingmodels": "done",
                "applying": "done"
              }
            }
            """);
        var coordinator = NewCoordinator();

        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.True(coordinator.IsComplete);
        Assert.Empty(_uvFake.SyncCalls);
        Assert.Empty(_bridge.Calls);
    }

    // ── khởi tạo Step từ state đã lưu (micro-task 14c) ────────────────────────────────────────────

    [Fact]
    public void Construction_WithCompletePersistedState_StartsAtDone()
    {
        // The bug this pins: setup completed in a PREVIOUS session and the app restarts. SetupModal's
        // cheap path (NeedsSetup false) never calls StartAsync, so the singleton must not sit at
        // Checking — both the pipeline gate and the Settings recovery section read IsComplete off Step.
        // Bug được pin ở đây: setup đã xong ở PHIÊN TRƯỚC và app khởi động lại. Đường rẻ của SetupModal
        // (NeedsSetup false) không gọi StartAsync, nên singleton không được nằm ở Checking — cả gate
        // pipeline lẫn section khôi phục trong Settings đều đọc IsComplete từ Step.
        WriteSetupJson("""
            {
              "flavor": "gpu",
              "language": "vi",
              "isComplete": true,
              "steps": {
                "checking": "done",
                "choosinglanguage": "done",
                "verifyingfiles": "done",
                "syncing": "done",
                "verifysidecar": "done",
                "downloadingmodels": "done",
                "applying": "done"
              }
            }
            """);

        var coordinator = NewCoordinator(); // NO StartAsync call / KHÔNG gọi StartAsync

        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.True(coordinator.IsComplete);
        Assert.False(coordinator.IsRunning);
        Assert.True(PipelineGate.CanStartPipeline(coordinator.IsComplete, coordinator.IsRunning, coordinator.SkippedForSession));
    }

    [Fact]
    public void Construction_WithIncompletePersistedState_StartsAtChecking()
    {
        // The other side of the pin: an incomplete persisted state keeps the default initial step.
        // Mặt còn lại của pin: state đã lưu chưa hoàn tất giữ nguyên bước khởi tạo mặc định.
        WriteSetupJson("""{"flavor":"gpu","language":"vi","steps":{"checking":"done"}}""");

        var coordinator = NewCoordinator();

        Assert.Equal(SetupStep.Checking, coordinator.Step);
        Assert.False(coordinator.IsComplete);
    }

    // ── IsRunning: cửa sổ race của "chạy lại setup" (máy đã xong, rerun force) ────────────────────

    [Fact]
    public async Task ForcedRerun_IsRunningTrueDuringCheckingWindow_FalseAfterDone()
    {
        // Production sequence: setup completes IN-SESSION (Step = Done), then the user clicks "Chạy
        // lại setup" on the same long-lived coordinator. During the forced re-run's synchronous
        // Checking window Step still holds Done, so IsComplete alone passes — IsRunning is what closes
        // the window.
        // Trình tự production: setup xong TRONG PHIÊN (Step = Done), rồi user bấm "Chạy lại setup" trên
        // cùng một coordinator sống lâu. Trong cửa sổ Checking đồng bộ của lượt chạy lại, Step vẫn giữ
        // Done nên IsComplete đơn lẻ sẽ pass — chính IsRunning là thứ chặn cửa sổ đó.
        var blockProbe = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The network-probe seam doubles as the window's latch: once blocking is armed, the first probe
        // call marks entry into the Checking window and holds it open until the test releases it (the
        // token keeps a cancelled test from leaking the blocked worker thread).
        // Seam probe mạng đóng vai chốt cửa sổ: khi bật chặn, lần gọi probe đầu đánh dấu vào cửa sổ
        // Checking và giữ nó mở tới khi test cho phép tiếp tục (token để test bị hủy không rò rỉ luồng
        // worker đang chặn).
        Func<string, bool> probe = _ =>
        {
            if (!blockProbe) return true;
            entered.TrySetResult();
            release.Task.Wait(TestContext.Current.CancellationToken); // the blind window under test
            return true;
        };
        var coordinator = NewCoordinator(isHostReachable: probe);

        // Phase 1: complete the wizard in-session (language answer + full pipeline on the fast fakes).
        // Giai đoạn 1: hoàn tất wizard trong phiên (trả lời ngôn ngữ + toàn bộ pipeline trên fake nhanh).
        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.ChoosingLanguage, coordinator.Step);
        await coordinator.ContinueWithLanguageAsync("vi", TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.True(coordinator.IsComplete);

        // Phase 2: force the re-run. StartAsync runs synchronously into the Checking step, so it must
        // run on its own thread — exactly how SettingsPanel.RerunSetup invokes it (Task.Run, fire and
        // forget).
        // Giai đoạn 2: buộc chạy lại. StartAsync chạy đồng bộ thẳng vào bước Checking nên phải chạy trên
        // luồng riêng — đúng cách SettingsPanel.RerunSetup gọi nó (Task.Run, không await).
        blockProbe = true;
        Task startTask = Task.Run(
            () => coordinator.StartAsync(force: true, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // In flight: the run is in progress while Step still reads Done — IsComplete alone would pass
        // the gate, but with IsRunning the gate blocks.
        // Đang chạy: lượt run đang diễn ra trong khi Step vẫn đọc là Done — IsComplete đơn lẻ sẽ pass
        // gate, nhưng kèm IsRunning thì gate chặn.
        Assert.True(coordinator.IsRunning);
        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.True(coordinator.IsComplete);
        Assert.False(PipelineGate.CanStartPipeline(coordinator.IsComplete, coordinator.IsRunning, coordinator.SkippedForSession));

        release.SetResult();
        await startTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The run finished (every step was already done → straight back to Done): IsRunning is false.
        // Lượt chạy kết thúc (mọi bước đã done → quay thẳng về Done): IsRunning về false.
        Assert.Equal(SetupStep.Done, coordinator.Step);
        Assert.True(coordinator.IsComplete);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task IsRunning_FalseWhenRunPausesOrFails()
    {
        // Idle from construction: no operation in flight yet.
        // Rảnh ngay từ khi tạo: chưa có thao tác nào đang chạy.
        var coordinator = NewCoordinator();
        Assert.False(coordinator.IsRunning);

        // Pause: no language answered — StartAsync returns having paused at ChoosingLanguage; the run
        // is over even though the wizard still waits for user input.
        // Dừng chờ: chưa có ngôn ngữ — StartAsync trả về sau khi dừng ở ChoosingLanguage; lượt chạy
        // đã kết thúc dù wizard vẫn đang chờ user nhập liệu.
        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.ChoosingLanguage, coordinator.Step);
        Assert.False(coordinator.IsRunning);

        // Fail: hard Checking failure (disk below the GPU floor) — Failed; the run is over too.
        // Fail: Checking fail cứng (đĩa dưới sàn GPU) — Failed; lượt chạy cũng đã kết thúc.
        var failed = NewCoordinator(diskFree: _ => 1 * GB);
        await failed.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(SetupStep.Failed, failed.Step);
        Assert.False(failed.IsRunning);
    }

    // ── "Bỏ qua thiết lập" cho phiên này (popup xác nhận — spec 2026-11-13) ─────────────────────────

    [Fact]
    public void SkipForSession_SetsFlagAndPublishes_WithoutClosingApp()
    {
        var coordinator = NewCoordinator();
        int before = _changedCount;

        coordinator.SkipForSession();

        Assert.True(coordinator.SkippedForSession);
        Assert.True(_changedCount > before);
        Assert.False(_closeAppCalled);
        Assert.False(coordinator.IsComplete); // không ghi state: lần mở sau wizard nhắc lại
    }

    [Fact]
    public void Skip_ClosesApp()
    {
        var coordinator = NewCoordinator();

        coordinator.Skip();

        Assert.True(_closeAppCalled);
        Assert.False(coordinator.SkippedForSession);
    }

    [Fact]
    public async Task StartAsync_ClearsSessionSkip()
    {
        var coordinator = NewCoordinator();
        coordinator.SkipForSession();
        Assert.True(coordinator.SkippedForSession);

        // State trống → lượt chạy dừng ở ChoosingLanguage; chỉ cần chứng minh cờ bị thu hồi khi lượt chạy bắt đầu.
        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.False(coordinator.SkippedForSession);
    }

    [Fact]
    public async Task SecondStartAsync_WhileRunInFlight_IsANoOp()
    {
        // Chống double-click (popup + nút rerun): lời gọi thứ hai trong lúc một lượt đang chạy phải
        // no-op — TryBeginRun giữ _running, không có lượt chồng. (Cùng kỹ thuật latch probe mạng như
        // test ForcedRerun ở trên.)
        var blockProbe = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, bool> probe = _ =>
        {
            if (!blockProbe) return true;
            entered.TrySetResult();
            release.Task.Wait(TestContext.Current.CancellationToken);
            return true;
        };
        var coordinator = NewCoordinator(isHostReachable: probe);
        blockProbe = true;
        Task first = Task.Run(
            () => coordinator.StartAsync(ct: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Task second = coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(second.IsCompleted); // TryBeginRun false → trả về ngay, không chồng lượt
        Assert.True(coordinator.IsRunning);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(coordinator.IsRunning);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeAdapters(VideoAdapterInfo[] items) : IVideoAdapterSource
    {
        public IReadOnlyList<VideoAdapterInfo> GetAdapters() => items;
    }

    /// <summary>
    /// Arg-aware uv fake: <c>pip show</c> probes answer from <see cref="Installed"/> by exit code only
    /// (R55 — the exit code IS the answer), and every <c>sync</c> call is recorded as its full argv and
    /// answered from <see cref="SyncResults"/> (default: success). Never launches a process.
    /// Fake uv phân biệt theo tham số: lệnh <c>pip show</c> trả lời từ <see cref="Installed"/> chỉ bằng
    /// exit code (R55 — exit code CHÍNH LÀ câu trả lời), mọi lệnh <c>sync</c> được ghi lại toàn bộ argv
    /// và trả lời từ <see cref="SyncResults"/> (mặc định: thành công). Không khởi tiến trình nào.
    /// </summary>
    private sealed class FakeUv : IProcessRunner
    {
        public OcrFlavor? Installed { get; set; }

        public List<string[]> SyncCalls { get; } = [];

        public Queue<(int Exit, string Tail)> SyncResults { get; } = new();

        public Task<(int ExitCode, string OutputTail)> RunAsync(
            string fileName, IReadOnlyList<string> args, string workingDir,
            IDictionary<string, string>? extraEnv, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (args[0] == "pip")
            {
                // `uv pip show <package>`: exit 0 ⇔ that flavor's paddle wheel is installed.
                bool present = Installed is not null &&
                    ((Installed == OcrFlavor.Gpu && args[2] == "paddlepaddle-gpu") ||
                     (Installed == OcrFlavor.Cpu && args[2] == "paddlepaddle"));
                return Task.FromResult((present ? 0 : 1, string.Empty));
            }

            SyncCalls.Add([.. args]);
            (int ExitCode, string OutputTail) result =
                SyncResults.Count > 0 ? SyncResults.Dequeue() : (0, "Using CPython 3.13.2 (managed)");
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Recording <see cref="ISetupCoreBridge"/>: every call is stored; the model catalog answers from
    /// <see cref="Installed"/> and a download marks its model installed immediately (a fast network),
    /// so the coordinator's poll loop converges on the next <c>ocr.models.list</c>;
    /// <c>sidecar.device</c> returns <see cref="DeviceResult"/>. The llm-runtime verbs mirror that:
    /// <c>llm.runtime.status</c> answers from <see cref="LlmRuntimeInstalled"/> and a runtime
    /// download records its flavor and completes instantly unless
    /// <see cref="LlmRuntimeDownloadCompletes"/> is turned off (a "network" that never finishes).
    /// Fake <see cref="ISetupCoreBridge"/> ghi lại mọi lời gọi: catalog model trả lời từ
    /// <see cref="Installed"/>, một lệnh download đánh dấu model đó đã cài ngay lập tức (mạng nhanh) để
    /// vòng poll của coordinator hội tụ ở lần <c>ocr.models.list</c> kế tiếp; <c>sidecar.device</c> trả
    /// về <see cref="DeviceResult"/>. Hai verb runtime LLM phản chiếu đúng cách đó:
    /// <c>llm.runtime.status</c> trả lời từ <see cref="LlmRuntimeInstalled"/> và một lệnh tải runtime
    /// ghi lại flavor rồi hoàn tất ngay, trừ khi tắt <see cref="LlmRuntimeDownloadCompletes"/> (mạng
    /// không bao giờ xong).
    /// </summary>
    private sealed class FakeCoreBridge : ISetupCoreBridge
    {
        public const string DetModel = "PP-OCRv6_small_det";
        public const string RecModel = "PP-OCRv6_small_rec";

        public List<(string Method, JsonNode? Params)> Calls { get; } = [];

        public HashSet<string> Installed { get; } = [];

        public List<string> Downloaded { get; } = [];

        /// <summary>Whether <c>llm.runtime.status</c> reports the runtime installed. <c>llm.runtime.status</c> có báo runtime đã cài không.</summary>
        public bool LlmRuntimeInstalled { get; set; }

        /// <summary>When false, a runtime download records the flavor but never completes (dead network). Khi false, lệnh tải runtime ghi flavor nhưng không bao giờ xong (mạng chết).</summary>
        public bool LlmRuntimeDownloadCompletes { get; set; } = true;

        /// <summary>Flavors of accepted <c>llm.runtime.download</c> calls, in order. Các flavor của lệnh <c>llm.runtime.download</c> đã nhận, theo thứ tự.</summary>
        public List<string> LlmRuntimeDownloads { get; } = [];

        public int RestartCount { get; private set; }

        /// <summary>The dev machine's GPU (RTX 5060 Ti, CC 12.0 — measured 2026-11-08).</summary>
        public JsonNode? DeviceResult { get; set; } = new JsonObject
        {
            ["device"] = "gpu:0",
            ["capability"] = new JsonArray(12, 0),
        };

        public Task RestartAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RestartCount++;
            return Task.CompletedTask;
        }

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((method, @params));
            return method switch
            {
                "sidecar.ping" => Task.FromResult<JsonNode?>(JsonValue.Create("pong")),
                "ocr.models.list" => Task.FromResult<JsonNode?>(BuildList()),
                "ocr.models.recommend" => Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["det"] = DetModel,
                    ["rec"] = RecModel,
                }),
                "ocr.models.download" => Download(@params),
                "sidecar.device" => Task.FromResult(DeviceResult),
                "llm.runtime.status" => Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["tag"] = LlmRuntimeInstalled ? "b7234" : null,
                    ["installed"] = LlmRuntimeInstalled,
                    ["serverPath"] = LlmRuntimeInstalled
                        ? @"C:\fake\llm\runtime\b7234\llama-server.exe"
                        : null,
                }),
                "llm.runtime.download" => DownloadLlmRuntime(@params),
                _ => Task.FromResult<JsonNode?>(null),
            };
        }

        private Task<JsonNode?> Download(JsonNode? @params)
        {
            string name = @params!["name"]!.GetValue<string>();
            Downloaded.Add(name);
            Installed.Add(name); // the fake "download" completes immediately
            return Task.FromResult<JsonNode?>(new JsonObject { ["accepted"] = true });
        }

        private Task<JsonNode?> DownloadLlmRuntime(JsonNode? @params)
        {
            LlmRuntimeDownloads.Add(@params!["flavor"]!.GetValue<string>());
            if (LlmRuntimeDownloadCompletes) LlmRuntimeInstalled = true; // fast network unless disabled
            return Task.FromResult<JsonNode?>(new JsonObject { ["accepted"] = true });
        }

        private JsonNode BuildList()
        {
            var catalog = new JsonArray();
            foreach (string name in new[] { DetModel, RecModel })
            {
                catalog.Add(new JsonObject
                {
                    ["name"] = name,
                    ["kind"] = name.EndsWith("det", StringComparison.Ordinal) ? "det" : "rec",
                    ["installed"] = Installed.Contains(name),
                });
            }

            return new JsonObject
            {
                ["catalog"] = catalog,
                ["active"] = new JsonObject { ["det"] = DetModel, ["rec"] = RecModel },
            };
        }
    }
}
