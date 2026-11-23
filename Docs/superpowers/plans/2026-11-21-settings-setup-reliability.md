# Settings/Setup Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sửa toàn bộ lỗi Settings/Setup đã duyệt: local LLM nạp ngay khi Start với popup thật, OCR Applying đồng bộ engine, repair/deletion đúng, Setup fail rõ, runtime attempt đáng tin và settings write an toàn.

**Architecture:** Core giữ một `LlamaServerSlot` dùng chung cho prewarm và translator; RPC start trả attempt ngay để App poll được trên JSON-RPC tuần tự. `PipelineController` làm single-flight Start, `MainLayout` render popup dùng chung. Setup/Settings dùng helper thuần cho completion/deletion, runtime dùng attempt-state tường minh và `AppSettingsService.Update` serialize read-modify-write.

**Tech Stack:** .NET 10/C#, Blazor Hybrid Razor, Win32 process counters, JSON-RPC stdio, xUnit v3/MTP, Vite/TypeScript/CSS.

**Spec:** `Docs/superpowers/specs/2026-11-20-llm-start-progress-and-ocr-selection-design.md`

## Global Constraints

- Giao tiếp/tài liệu bằng tiếng Việt; public C# API docs song ngữ English rồi Vietnamese.
- Không thêm package/dependency; không đổi `-c 4096 -ngl 99 --jinja`; không preload ngoài Start; không giữ llama-server sau Stop/Start lỗi.
- Stall: sample 5 giây; gia hạn khi IO ≥1 MiB, RAM high-water ≥1 MiB, page faults ≥64 hoặc CPU ≥5% một core; stall window 600 giây; counter unreadable dùng fixed 600 giây.
- Không dùng WorkingSet làm percent; chỉ 100% khi healthy; counter unavailable thì thanh vô định.
- TDD: test fail trước, implement tối thiểu, focused tests pass rồi mới commit. Không sửa test để che hành vi sai.
- Không xoá model thật trên máy trong test; không chờ timeout thật; không `git add -A`; không push.
- Không mở phạm vi sang Base URL `/v1`, capture, tray hay refactor DI toàn Core.

## Review Focus

- RPC tuần tự: `llm.server.start` phải trả accepted trước health và status vẫn đọc được — Task 1 Step 1, `StartRpc_ReturnsBeforeHealth_AndStatusIsReadable`.
- Load dài: hoạt động được gia hạn, stall mới timeout, unreadable counters có ceiling — Task 1 Step 1, `HealthWait_ActivityExtends_StallFails_UnreadableCaps`.
- Cancel/rollback: hủy trước/sau `core.start`, Start lỗi và caller trùng không giữ server/running — Task 1 Step 5, `Start_CancelAndFailure_Cleanup`.
- Deletion partial failure: mọi file/pick mất model đã xoá hoặc fail-closed, không Start được — Task 2 Step 3, `Delete_PersistenceFailure_FailsClosed`.
- Runtime binary cũ: HTTP timeout/cancel không được suy ra success — Task 4 Step 1, `RuntimePoll_UncommittedAttempt_NeverCompletes`.

---

### Task 1: Local LLM prewarm, progress RPC và popup Start

**Files:**
- Create: `ReTran.Core/Llm/LlamaProcessMetrics.cs`, `ReTran.Core/Llm/LlmServerRpc.cs`
- Modify: `ReTran.Core/Llm/ILlamaServerHost.cs`, `LlamaServerHost.cs`, `LlamaServerSlot.cs`, `ReTran.Core/Program.cs`
- Modify: `ReTran.App/Core/PipelineController.cs`, `ReTran.App/Components/Pages/Home.razor`, `Components/Layout/MainLayout.razor`, `Services/I18nStrings.cs`, `vite-project/src/css/deck.css`
- Create: `ReTran.App/Components/Shared/LlmStartupModal.razor`
- Test: `ReTran.Tests/LlamaServerHostTests.cs`, `LlamaServerSlotTests.cs`, `PipelineControllerTests.cs`
- Create: `ReTran.Tests/LlmServerRpcTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record LlamaProcessSample(TimeSpan CpuTime, long WorkingSetBytes, long ReadBytes, long PageFaults);`
  - `public interface ILlamaProcessMetrics { bool TryRead(Process process, out LlamaProcessSample sample); }`
  - `public sealed record LlmLoadProgressSnapshot(string Phase, long? BytesLoaded, long? BytesTotal, double? BytesPerSec, bool ProgressAvailable);`
  - `ILlamaServerHost.LoadProgress { get; }`
  - `LlamaServerSlot.Start(string modelName, string serverExe, string modelPath) → long attempt`; existing `GetOrCreate/EnsureStartedAsync/Stop` remain.
  - `LlmServerRpc.Register(JsonRpcServer, LlmModelManager, LlamaRuntimeManager, LlamaServerSlot)` registers `llm.server.start/stop`; `llm.server.status` adds `attempt/phase/bytesLoaded/bytesTotal/bytesPerSec/progressAvailable` without removing old fields.
  - `PipelineController.LlmStartup`, `LlmStartupChanged`, `CancelLlmStartupAsync(CancellationToken)`; `LlmStartupInfo(long Attempt, string Model, string Phase, long? BytesLoaded, long? BytesTotal, double? BytesPerSec, bool ProgressAvailable, string? Error)`.

- [ ] **Step 1: Viết test fail cho progress/stall và RPC bất đồng bộ**

Thêm:
- `HealthWait_ActivityExtends_StallFails_UnreadableCaps`: từng tín hiệu được gia hạn; không activity fail sau 600 giây; unreadable metrics giữ fixed ceiling. Dùng fake `TimeProvider`/metrics, không ngủ thật.
- `Progress_MonotonicClamped_UntilHealthy`: bytes lùi không làm percent lùi; trước health ≤99%, healthy=100%; missing counter ⇒ `ProgressAvailable=false`.
- `Dispose_CancelsHealthWaitPromptly` và `HealthWait_ProcessExit_ReturnsStderrTail`.
- `StartRpc_ReturnsBeforeHealth_AndStatusIsReadable`: fake host chưa healthy, start trả attempt rồi mới release health; status request sau đó vẫn được xử lý.

Run:
```powershell
dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-method "*HealthWait_*" --filter-method "*StartRpc_*" --filter-method "*Progress_Monotonic*"
```
Expected: FAIL vì metrics/progress/RPC chưa tồn tại.

- [ ] **Step 2: Implement metrics, host, slot và `LlmServerRpc`**

- Win32 implementation đọc IO/page faults/RAM/CPU từ đúng `Process`; lỗi handle/exit trả false.
- `LlamaServerHost` nhận optional metrics/`TimeProvider` cho test; giữ launch args và health probe hiện có, thêm early-exit/cancel/stall/progress.
- `LlamaServerSlot.Start` là idempotent theo model, tăng attempt khi tạo host mới, chạy health ở nền và observe fault. Stop cancel health rồi dispose đúng host; callback cũ không ghi attempt mới.
- `LlmServerRpc` resolve model/runtime như `Program.LlmFactory`; start trả `{accepted:true,attempt,model}` ngay. Program đăng ký RPC và dùng cùng slot cho translator.

Run:
```powershell
dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.LlamaServerHostTests" --filter-class "RetranCore.Tests.LlamaServerSlotTests" --filter-class "RetranCore.Tests.LlmServerRpcTests"
```
Expected: PASS.

- [ ] **Step 3: Viết test fail cho Start flow của App**

Trong `PipelineControllerTests`, fake peer kiểm tra đúng thứ tự và side effects:
- `Start_LocalLlmm_PrewarmsBeforeCoreStart`: `models.set → server.start → status ready → core.start → IsRunning`.
- `Start_CancelBeforeCoreStart_StopsServerWithoutRunning`.
- `Start_CancelAfterCoreStart_RollsBackWithCoreStop`.
- `Start_CoreStartFailure_StopsLlmAndDoesNotRun`.
- `Start_SecondCallerRejected_AndSourceUnchanged`.
- `Start_GoogleOrApi_DoesNotPrewarm`.
- Status parse test cho mọi field progress mới.

Run:
```powershell
dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-method "*Start_LocalLlmm*" --filter-method "*Start_Cancel*" --filter-method "*Start_CoreStartFailure*" --filter-method "*Start_SecondCaller*" --filter-method "*Start_GoogleOrApi*"
```
Expected: FAIL vì chưa có prewarm/cancel/single-flight.

- [ ] **Step 4: Implement single-flight Start trong `PipelineController`**

- Gate Start; caller thứ hai nhận `InvalidOperationException("pipeline start is already in progress")`.
- Local LLM prewarm/poll attempt 500 ms; status superseded/error fail rõ.
- Giữ một linked CTS cho Start/cancel. Chưa `core.start`: `llm.server.stop`; đã gửi: đợi RPC kết thúc rồi `core.stop` rollback. Chỉ đặt running sau `core.start` thành công.
- Publish snapshot mọi phase; callback cũ không publish attempt mới. Google/API đi đường cũ.

Run lại Step 3 và toàn bộ `PipelineControllerTests`. Expected: PASS.

- [ ] **Step 5: Thêm popup dùng chung và semantics Hủy của Home**

`LlmStartupModal` subscribe concrete `PipelineController` qua MainLayout: model, phase, percent/indeterminate, loaded/total, speed/ETA nếu có, error, Hủy/đang hủy/Đóng. Không backdrop-close; có `role=dialog`, `aria-modal`, nhãn progress và focus. Home bắt `OperationCanceledException` riêng, không `_pipelineError`. Thêm VI/EN và CSS theo modal hiện có.

Run:
```powershell
npm run build
```
tại `ReTran.App/vite-project`. Expected: PASS.

- [ ] **Step 6: Chạy regression gate và commit**

Run:
```powershell
dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.LlamaServerHostTests" --filter-class "RetranCore.Tests.LlamaServerSlotTests" --filter-class "RetranCore.Tests.LlmServerRpcTests" --filter-class "PipelineControllerTests"
```
Expected: PASS. Commit:
```powershell
git add -- "ReTran.Core/Llm" "ReTran.Core/Program.cs" "ReTran.App/Core/PipelineController.cs" "ReTran.App/Components/Pages/Home.razor" "ReTran.App/Components/Layout/MainLayout.razor" "ReTran.App/Components/Shared/LlmStartupModal.razor" "ReTran.App/Services/I18nStrings.cs" "ReTran.App/vite-project/src/css/deck.css" "ReTran.App/wwwroot/build" "ReTran.Tests/LlamaServerHostTests.cs" "ReTran.Tests/LlamaServerSlotTests.cs" "ReTran.Tests/LlmServerRpcTests.cs" "ReTran.Tests/PipelineControllerTests.cs"
git commit -m "feat(llm): load local model with visible startup progress"
```

### Task 2: OCR Applying, rerun/artifact integrity và deletion

**Files:**
- Create: `ReTran.App/Services/Setup/SetupModelSelection.cs`
- Modify: `SetupState.cs`, `SetupPaths.cs`, `SetupCoordinator.cs`, `ReTran.App/Components/Shared/SettingsPanel.razor`
- Test: `ReTran.Tests/Setup/SetupStateTests.cs`, `SetupCoordinatorTests.cs`
- Create: `ReTran.Tests/Setup/SetupModelSelectionTests.cs`

**Interfaces:**
- Produces:
  - `SetupModelSelection.HasCompleteAnswers(SetupState state) → bool`: đủ `Dets/Recs/Langs/LlmModels`; active det/rec/lang/llm thuộc list tương ứng; det/rec bắt buộc, LLM optional.
  - `SetupModelSelection.AfterDelete(SetupState, AppSettings, string deletedName, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> engineActive) → (SetupState Setup, AppSettings Settings)`: loại model khỏi mọi list; replacement cùng slot ưu tiên engine active rồi candidate installed đầu tiên; không candidate thì `string.Empty` và `IsComplete=false`.
  - `SetupModelSelection.ArtifactsPresent(state, venvPath, modelsDir, llmRoot, fileExists, modelDirExists, readText) → bool`: kiểm tra mọi selected OCR/GGUF, `.partial`, runtime active và venv.
  - `SetupCoordinator.RemoveDeletedModel(string name, IReadOnlyCollection<string> installed, IReadOnlyCollection<string> engineActive)`: persist fail-closed và publish.

- [ ] **Step 1: Viết test fail cho completion, artifact và deletion**

Thêm:
- `HasCompleteAnswers_RejectsMissingRecsAndInvalidActiveFields`, gồm `ActiveLang` ngoài `Langs`.
- `ArtifactsPresent_SelectedArtifactMissing_ReturnsFalse` cho OCR, GGUF, `.partial`, runtime exe và venv; artifact hợp lệ trả true.
- `AfterDelete_RemovesEveryReference_AndPrefersEngineActive`.
- `AfterDelete_NoCandidate_ClearsSettingsAndFailsClosed`.

Run `dotnet test ... -- --filter-class "SetupModelSelectionTests"`. Expected: FAIL vì helper chưa tồn tại.

- [ ] **Step 2: Implement helper và harden `SetupState.NeedsSetup`**

`NormalizeCompletion` dùng `HasCompleteAnswers`; cheap preflight chỉ đọc file/JSON qua seams. `SetupPaths` thêm `LlmRoot`. Không spawn Core/Python/network.

Run `SetupModelSelectionTests` và `SetupStateTests`. Expected: PASS.

- [ ] **Step 3: Viết test fail cho rerun, Applying và deletion fail-closed**

- `ForceRerun_PreservesAnswersAndRepairsOnlyMissingArtifacts`.
- `Applying_CallsOcrModelsSetBeforeDone`.
- `Applying_OcrSetFailure_DoesNotComplete_AndRetryIsIdempotent`.
- `RemoveDeletedModel_UpdatesSetupSettingsAndPublishes`.
- `Delete_PersistenceFailure_FailsClosed` cho lỗi từng file; Start gate không mở.

Run `dotnet test ... -- --filter-method "*ForceRerun_*" --filter-method "*Applying_*" --filter-method "*RemoveDeletedModel_*" --filter-method "*Delete_PersistenceFailure*"`. Expected: FAIL.

- [ ] **Step 4: Implement coordinator và Settings deletion**

- Force chỉ xoá markers/`IsComplete`, giữ answers; Reconcile/ArtifactsPresent tải bù phần thiếu.
- Applying: Save cặp OCR → `ocr.models.set` → Done; lỗi RPC đi retry hiện có, không complete giả.
- `RemoveDeletedModel` đánh dấu incomplete trước persist, dùng `AfterDelete`, cập nhật settings/UI state và publish; mọi lỗi đều fail-closed kèm lỗi rõ.
- Settings gọi coordinator sau RPC delete thành công rồi refresh; không tự xoá model active.

Run toàn bộ `SetupCoordinatorTests`, `SetupStateTests`, `SetupModelSelectionTests`. Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -- "ReTran.App/Services/Setup" "ReTran.App/Components/Shared/SettingsPanel.razor" "ReTran.Tests/Setup"
git commit -m "fix(setup): preserve selections and synchronize model lifecycle"
```

### Task 3: Setup fault, empty catalog và fail-fast download

**Files:**
- Modify: `SetupModelSelection.cs`, `SetupCoordinator.cs`, `SetupModal.razor`, `I18nStrings.cs`
- Test: `SetupCoordinatorTests.cs`
- Create: `ReTran.Tests/Setup/SetupModalStateTests.cs`

**Interfaces:**
- Produces: `SetupModelSelection.FindFailedDownload(JsonNode? list, string? arrayKey, IReadOnlyCollection<string> needed) → (string Name, string Error)?`.
- Produces: `SetupModelSelection.CanContinueLlm(bool busy, bool loading) → !busy && !loading`; `SetupModal` dùng `_llmLoading` tách loading/loaded-empty/loadFailed.

- [ ] **Step 1: Viết test fail**

- `StartAsync_SaveFault_PublishesFailedAndReleasesRun`.
- `FindFailedDownload_OnlyReturnsNeededFailedIdleModel`.
- `LlmCatalog_LoadedEmpty_CanContinueWithoutAutoAnswer`.
- `DownloadModels_DownloadError_FailsAttemptImmediately` và retry đủ 4 lần mới Failed; không đợi poll interval.

Run các filter method trên. Expected: FAIL.

- [ ] **Step 2: Implement fault boundary, empty state và fail-fast**

`StartAsync` giữ cancel, log exception khác, set Error/Failed/publish trước `EndRun`. Modal chỉ spinner khi loading; loaded-empty/loadFailed đều cho bỏ qua rõ ràng, không auto trả lởi rỗng trước khi tải xong. Poll phát hiện `downloadError && !downloading` thì throw attempt ngay; retry tối đa hiện có, không phân loại lỗi mới.

Run:
```powershell
dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SetupCoordinatorTests" --filter-class "SetupModalStateTests"
```
Expected: PASS.

- [ ] **Step 3: Commit**

```powershell
git add -- "ReTran.App/Services/Setup" "ReTran.App/Components/Shared/SetupModal.razor" "ReTran.App/Services/I18nStrings.cs" "ReTran.Tests/Setup"
git commit -m "fix(setup): surface failures and stop stalled downloads early"
```

### Task 4: Runtime attempt-state và artifact commit

**Files:**
- Modify: `ReTran.Core/Llm/LlamaRuntimeManager.cs`, `ReTran.App/Core/LlmRuntimePoll.cs`, `PipelineController.cs`, `ReTran.App/Components/Shared/SettingsPanel.razor`, `SetupCoordinator.cs`
- Test: `ReTran.Tests/LlamaRuntimeManagerTests.cs`, `LlmRuntimePollTests.cs`, `SetupCoordinatorTests.cs`

**Interfaces:**
- Produces: `LlmRuntimeAttemptState { None, Running, Succeeded, Failed, Cancelled }`; `LlmRuntimeStatus.AttemptState`.
- `LlmRuntimePollOutcome.Cancelled`; `Evaluate` chỉ `Completed` khi đúng attempt đã `Succeeded` và installed.

- [ ] **Step 1: Viết test fail**

- `RuntimePoll_UncommittedAttempt_NeverCompletes` cho Failed/Cancelled/Running với binary cũ.
- `Download_HttpTimeout_MarksFailed`.
- `Download_CallerCancellation_MarksCancelled`.
- `Download_MissingServerAfterExtract_RollsBackAndDoesNotWriteState`.
- `Setup_MissingArtifactSameTag_ReportsRepairNotUpdate`; valid same-tag vẫn skip.

Run `LlamaRuntimeManagerTests`, `LlmRuntimePollTests` và method Setup tương ứng. Expected: FAIL.

- [ ] **Step 2: Implement attempt-state và verified commit**

Manager set Running khi start, Succeeded chỉ sau verified commit; HTTP timeout ⇒ Failed; caller-token cancel ⇒ Cancelled; mọi trạng thái được serialize. `CommitStagedDirectory` yêu cầu final `llama-server.exe` tồn tại trước `WriteState`, rollback backup khi fail. App map state/toast; Setup log flavor/current/latest/installed và repair cùng tag.

Run toàn bộ ba test class. Expected: PASS.

- [ ] **Step 3: Commit**

```powershell
git add -- "ReTran.Core/Llm/LlamaRuntimeManager.cs" "ReTran.App/Core/LlmRuntimePoll.cs" "ReTran.App/Core/PipelineController.cs" "ReTran.App/Components/Shared/SettingsPanel.razor" "ReTran.App/Services/Setup/SetupCoordinator.cs" "ReTran.Tests/LlamaRuntimeManagerTests.cs" "ReTran.Tests/LlmRuntimePollTests.cs" "ReTran.Tests/Setup/SetupCoordinatorTests.cs"
git commit -m "fix(runtime): distinguish failed attempts from installed state"
```

### Task 5: Atomic settings updates và flavor revision

**Files:**
- Modify: `AppSettingsService.cs`, `SettingsPanelSync.cs`, `SettingsPanel.razor`, `Home.razor`, `FloatingMenuService.cs`, `OverlayWindowService.cs`, `SetupCoordinator.cs`
- Test: `AppSettingsServiceTests.cs`, `SettingsPanelSyncTests.cs`, các writer tests hiện có.

**Interfaces:**
- Produces: `AppSettingsService.Update(Func<AppSettings,AppSettings> update) → AppSettings` giữ per-full-path lock cho read-modify-write; Load/Save dùng cùng lock và unique temp.
- `SettingsPanelSync.IsCurrentFlavor(string requested, string current, int requestedRevision, int currentRevision) → bool`.

- [ ] **Step 1: Viết test fail**

- `Update_ConcurrentWriters_PreserveBothFields`: nhiều writer/service instance cùng path, một đổi `OcrDet`, một đổi `Dst`; không IOException và cuối cùng giữ cả hai.
- `Update_NullUpdater_Throws`.
- `IsCurrentFlavor_RejectsStaleFlavorOrRevision`.

Run `AppSettingsServiceTests`, `SettingsPanelSyncTests`. Expected: FAIL.

- [ ] **Step 2: Implement `Update` và migrate mọi writer**

Dùng per-path lock, private Load/SaveCore và unique temp + cleanup. Chuyển đúng các call sites: SettingsPanel (4), Home (1), FloatingMenuService (1), OverlayWindowService (1), SetupCoordinator/Applying và deletion. Không để `Load with → Save` trong production.

Run toàn bộ writer tests. Expected: PASS.

- [ ] **Step 3: Implement flavor guard**

Disable selector khi busy; capture flavor/attempt cục bộ; revision tăng khi đổi; mọi response/toast chỉ apply khi `IsCurrentFlavor` true. Không đọc `_llmFlavor` mới sau await của request cũ.

Run `SettingsPanelSyncTests` và build App. Expected: PASS.

- [ ] **Step 4: Commit**

```powershell
git add -- "ReTran.App/Services/AppSettingsService.cs" "ReTran.App/Core/SettingsPanelSync.cs" "ReTran.App/Components/Shared/SettingsPanel.razor" "ReTran.App/Components/Pages/Home.razor" "ReTran.App/Services/FloatingMenu/FloatingMenuService.cs" "ReTran.App/Services/OverlayWindow/OverlayWindowService.cs" "ReTran.App/Services/Setup/SetupCoordinator.cs" "ReTran.Tests/AppSettingsServiceTests.cs" "ReTran.Tests/SettingsPanelSyncTests.cs" "ReTran.Tests/FloatingMenuServiceTests.cs" "ReTran.Tests/Overlay" "ReTran.Tests/Setup"
git commit -m "fix(settings): serialize updates and reject stale flavor responses"
```

### Task 6: Docs, full verification và review

**Files:**
- Modify: `ReTran.App/Services/Setup/SystemCheck.cs` (doc Syncing), `Docs/performance.md` (số đo nếu có)
- Modify: `Docs/superpowers/specs/2026-11-20-llm-start-progress-and-ocr-selection-design.md` nếu quyết định implementation thay đổi có lý do.

- [ ] **Step 1: Hoàn thiện docs/logging và kiểm diff**

Cập nhật comment force rerun/frozen-sync và logger trong file đã chạm; không refactor DI ngoài phạm vi. Run `git diff --check`, `git status --short`; chỉ file thuộc plan và 2 tài liệu untracked đã có từ trước.

- [ ] **Step 2: Build và full automated suites**

```powershell
npm run build
dotnet build "ReTran Project.slnx" -c Release
dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"
```
`npm` tại `ReTran.App/vite-project`; các lệnh khác ở repo root. Expected: tất cả PASS. Nếu đổi Python, thêm `uv run pytest tests -v` tại `ReTran.OCR`.

- [ ] **Step 3: Smoke thật và ghi số đo/blocker**

Trên Release với artifact hợp lệ: local 1.8B cold/warm; Home + floating menu; cancel; Google/API; OCR medium sau remount. Ghi thởi gian health, RAM và overhead poll vào `Docs/performance.md`. Không có runtime/model phụ/game phù hợp thì ghi đúng mục chưa kiểm chứng; không tuyên bố đã đo model hàng trăm GB hoặc budget game.

- [ ] **Step 4: Review toàn bộ diff**

Chạy `ocr_review` trên phạm vi toàn bộ commits của plan nếu endpoint đã cấu hình; nếu vẫn thiếu endpoint, ghi blocker và dùng reviewer độc lập cho Standards + Spec. Chỉ sửa finding được xác nhận, rồi chạy lại verification bị ảnh hưởng.

- [ ] **Step 5: Commit tài liệu/verification**

```powershell
git add -- "Docs/superpowers/plans/2026-11-21-settings-setup-reliability.md" "Docs/superpowers/specs/2026-11-20-llm-start-progress-and-ocr-selection-design.md" "Docs/performance.md" "ReTran.App/Services/Setup/SystemCheck.cs"
git commit -m "docs: record settings and setup reliability verification"
```
Không push nếu chưa có approval riêng.

#### Kết quả Task 6 (2026-11-23)

- `npm run build` pass sau `npm ci` (node_modules ban đầu thiếu TypeScript); Release solution build pass, 0 error / 34 analyzer warning; full C# suite **1099 pass, 0 fail, 2 skip / 1101**. Không có file Python nào đổi trong implementation range nên không chạy sidecar suite theo điều kiện của plan.
- `git diff --check` pass cho cả implementation range và working tree trước commit tài liệu. OpenCodeReview health xác nhận vẫn thiếu LLM endpoint; không cấu hình credential và không tuyên bố OCR review đã chạy. Reviewer độc lập toàn branch thuộc lượt dispatch riêng của controller.
- Smoke RPC/model thật trên `ReTran.Core` Release bằng harness và fixture cô lập: local 1.8B với `b12532` cold **59,653 s**, warm **2,225 s**; cancel giữa load dọn process thành công; số RAM/poll và giới hạn harness được ghi đầy đủ trong `Docs/performance.md`.
- OCR medium: `ocr.models.set` + list lại giữ `medium/medium`; CPU inference thật pass 4.825 ms / 2 spots. GPU bị chặn bởi `cudnn64_9.dll` error 126.
- `SystemCheck.DefaultCountModelEntries` đã sửa doc để không còn đồng nhất cache-count với completion theo selected artifacts. Comment force/frozen trong `SetupCoordinator` đã đúng semantics. Một comment cũ tại `SettingsPanel.razor:1599` và `Console.Error` tại `LlamaRuntimeManager` được ghi nhận nhưng **không sửa** vì Task 6 chỉ được phép đổi documentation trong `SystemCheck.cs`, không mở product-code/DI round mới.
- Chưa kiểm chứng E2E App/Home/floating menu/Google/API/Setup Razor do không thể cô lập known-folder của App/Core `Program` mà không ghi state thật. `b12546` không ready; active `b12556` thiếu binary. Không có gaming-budget, huge-model hay real-stall claim. Chi tiết lệnh/output/raw evidence/blocker: `.superpowers/sdd/2026-11-21-settings-setup-reliability/task-6-report.md`.
