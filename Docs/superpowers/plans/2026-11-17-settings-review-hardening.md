# Settings Review Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sửa các lỗi trạng thái, download, đồng bộ và UI trong Settings mà review ba trục đã phát hiện, không làm hỏng wizard hoặc pipeline hiện có.

**Architecture:** Core giữ sự thật về runtime/model/server và phơi trạng thái lỗi qua JSON-RPC; `PipelineController` map đầy đủ wire shape; `SettingsPanel` chỉ poll/đồng bộ và render, không suy ra thành công từ artifact cũ. Các quyết định thuần (chọn flavor, giữ pick/deferred, validate prompt/JSON) được tách thành helper nhỏ khi cần để test trực tiếp.

**Tech Stack:** .NET 10, C#, Razor/Blazor Hybrid, JSON-RPC, xUnit v3, Tailwind CSS hiện có.

**Spec:**
- `Docs/superpowers/specs/2026-11-14-llm-translation-provider-design.md` (D5–D13: runtime/model/server/provider/prompt).
- `Docs/superpowers/specs/2026-10-26-ocr-engine-settings-overlay-simplify-design.md` (C5/C9/C12/C14 và §8: OCR params).
- `Docs/superpowers/specs/2026-10-22-ocr-models-shm-http-overlay-design.md` (C8–C11: download nền, deferred pair, settings là nguồn lựa chọn).
- `Docs/superpowers/specs/2026-11-17-wizard-detrec-langs-llm-design.md` §11 (nhóm Settings và rerun).
- Review Settings ba trục ngày 2026-11-17: 26 finding đã được user duyệt sửa bằng “ok làm đi”/“tiếp”.

## Global Constraints

- Làm việc trên `master` theo quyết định đã ghi ở ledger trước; không tạo worktree trong repo có WIP của user. Không đụng `retran-ui-v2.html`.
- Không `git add -A`; chỉ stage file thuộc task. Không push nếu chưa có approval tại bước push.
- Mọi production behavior mới/sửa phải có test RED được quan sát trước, sau đó GREEN; cuối mỗi task chạy focused suite, cuối plan chạy full suite.
- Runtime `DownloadAsync` phải rethrow sau khi ghi trạng thái lỗi; giữ test zip-slip và hành vi caller trực tiếp.
- Catalog/ngôn ngữ/quant/SizeClass tiếp tục lấy từ source catalog; không thêm tier hoặc danh sách hardcoded mới.
- “Không chọn LLM” trong wizard vẫn không tự đổi provider hoặc xoá `LlmActiveModel` đang có.
- Không ghi đè edit chưa lưu khi wizard hoàn tất; catalog có thể refresh độc lập với form.
- Giữ XML doc song ngữ cho public API thay đổi; code/log/comment theo convention hiện có.
- Không thêm package hoặc thay dependency cho các fix này.

## Review Focus

- Runtime cũ vẫn còn file trong khi update/flavor repair đang chạy hoặc fail: UI phải thấy đang tải/lỗi, không báo thành công giả. Task 1–2 pin bằng status và poll tests.
- Hai lượt download/runtime check cạnh tranh hoặc cùng tag: trạng thái in-flight phải tuần tự và completion không phụ thuộc tag đổi. Task 1–2 pin bằng blocked downloader và same-tag completion.
- Catalog trả rỗng/lỗi trong khi user đã có lựa chọn hoặc edit chưa lưu: giữ dữ liệu hợp lệ cuối và giữ edit. Task 4–5 pin bằng reload tests.
- `core.stop` hoặc chuyển local→API khi server vừa start/lỗi: process tree phải được dispose đúng một lần, không giữ VRAM. Task 6 pin bằng lifecycle tests.
- JSON rỗng/malformed, prompt trắng, RPC schema/server status lỗi: Settings hiển thị lỗi inline và không ghi file sai. Task 7 pin bằng validation/controller tests.

---

### Task 1: Runtime status và check theo flavor ở Core

**Files:**
- Modify: `ReTran.Core/Llm/LlamaRuntimeManager.cs`
- Modify: `ReTran.Core/Llm/LlmRpcHandlers.cs`
- Test: `ReTran.Tests/LlamaRuntimeManagerTests.cs`
- Test: `ReTran.Tests/LlmRpcHandlersTests.cs`

**Interfaces:**
- Produces: `LlamaRuntimeManager.CheckAsync(string flavor, CancellationToken ct)`; `LlmRuntimeStatus.Downloading` và `DownloadFailed` độc lập với artifact cũ.
- Consumes: `llm.runtime.download { flavor }` đã có; `llm.runtime.check` nhận cùng optional `flavor`.

- [ ] **Step 1: Viết test RED** cho ba hành vi: update cùng tag đang chạy thì `Installed=false`/`Downloading=true`; update fail với install cũ thì `DownloadFailed=true`; `CheckAsync("cpu")` chọn `b190` từ AVX2 và `CheckAsync("cuda")` bỏ CPU-only `b200`. RPC test assert `llm.runtime.check {flavor:"cpu"}` chuyển flavor xuống manager.
- [ ] **Step 2: Chạy RED**: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.LlamaRuntimeManagerTests"`; expected: fail vì chưa có `Downloading`, signature flavor hoặc failure bị install cũ che.
- [ ] **Step 3: Implement tối thiểu**: cờ in-flight được bật sau khi acquire `_downloadGate`, clear trong `finally`; status dùng `installed && !downloading`, `DownloadFailed = _downloadFailed` độc lập; `CheckAsync` validate và forward flavor; RPC dùng `OptionalFlavor`.
- [ ] **Step 4: Chạy GREEN**: hai focused classes trên phải pass, traversal vẫn throw.
- [ ] **Step 5: Commit** riêng Core runtime + tests.

### Task 2: App map runtime progress/failure và Settings poll đúng completion

**Files:**
- Modify: `ReTran.App/Core/PipelineController.cs`
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor`
- Modify: `ReTran.App/Services/I18nStrings.cs`
- Test: `ReTran.Tests/PipelineControllerTests.cs`
- Test: helper UI thuần mới nếu cần để test poll mà không chờ wall-clock.

**Interfaces:**
- Consumes: Task 1 `progress`, `downloadFailed`, `downloading`.
- Produces: `LlmRuntimeStatusInfo.Progress`, `.DownloadFailed`, `.Downloading`; completion = đã cài, không downloading, không failed.

- [ ] **Step 1: Mở rộng `LlmRuntime_MapsStatusAndCheck_SendsFlavor` thành fixture RED** có `progress {receivedBytes:100,totalBytes:400,bytesPerSec:50}`, `downloading:true`, `downloadFailed:true`; assert App map đủ. Thêm test helper poll: same-tag repair hoàn tất khi installed ổn định; failed dừng ngay; không có chuỗi 300 poll giả.
- [ ] **Step 2: Chạy RED** class `PipelineControllerTests` và helper mới; expected: thiếu property hoặc same-tag không complete/failure không fail-fast.
- [ ] **Step 3: Implement**: DTO init properties + parse; Settings poll dừng lỗi ngay, coi same-tag installed là thành công, sau thành công luôn gọi lại `CheckLlmRuntimeAsync` theo flavor để badge Update chính xác; toast/timeout lỗi có chuỗi rõ.
- [ ] **Step 4: Chạy GREEN** focused tests và build App.
- [ ] **Step 5: Commit** riêng App runtime + tests.

### Task 3: Wizard gửi flavor thật khi kiểm tra runtime

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs`
- Test: `ReTran.Tests/Setup/SetupCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 1 `llm.runtime.check {flavor}`.
- Produces: check dùng cùng `runtimeFlavor` mà bước download dùng (`cuda` trên đường GPU, `cpu` ngoài GPU).

- [ ] **Step 1: Viết test RED** cho GPU và CPU: fake bridge ghi params của `llm.runtime.check`; assert lần lượt `cuda` và `cpu`, download/check không lệch flavor.
- [ ] **Step 2: Chạy RED**: `SetupCoordinatorTests`; expected: check hiện gửi params null.
- [ ] **Step 3: Implement** bằng `JsonObject { ["flavor"] = runtimeFlavor }`, giữ catch mất mạng không chặn wizard.
- [ ] **Step 4: Chạy GREEN** toàn `SetupCoordinatorTests`.
- [ ] **Step 5: Commit** coordinator + tests.

### Task 4: Giữ OCR deferred picks và resync sau wizard không ghi đè edit

**Files:**
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor`
- Create: helper state thuần nhỏ nếu cần trong `ReTran.App/Components/Shared/` hoặc `ReTran.App/Services/`.
- Test: test helper mới dưới `ReTran.Tests/`.

**Interfaces:**
- Consumes: `SettingsStore.Load()`, `Pipeline.ListOcrModelsAsync`, `Setup.Changed`.
- Produces: InUse chỉ khởi tạo picks lần đầu, sau apply-ngay hoặc resync sạch; reload thường/deferred không đổi picks.

- [ ] **Step 1: Viết test RED** cho state transitions: reload sau deferred giữ det/rec user chọn; apply-ngay nhận InUse mới; wizard Done nạp settings mới khi form sạch; wizard Done giữ field đang dirty nhưng vẫn refresh catalog.
- [ ] **Step 2: Chạy RED** helper tests; expected: reload hiện luôn ghi đè InUse/không có transition resync.
- [ ] **Step 3: Implement** cờ nguồn đồng bộ rõ ràng và snapshot/dirty comparison cho form; `OnSetupChanged` chỉ resync một lần ở transition sang complete, marshal về UI và bỏ qua sau dispose.
- [ ] **Step 4: Chạy GREEN** helper + build App.
- [ ] **Step 5: Commit** Settings synchronization + tests.

### Task 5: GGUF failure nhìn thấy được và catalog rỗng không xoá dữ liệu tốt

**Files:**
- Modify: `ReTran.Core/Llm/LlmModelManager.cs`
- Modify: `ReTran.App/Core/PipelineController.cs`
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor`
- Modify: `ReTran.App/Services/I18nStrings.cs`
- Test: `ReTran.Tests/LlmModelManagerTests.cs`
- Test: `ReTran.Tests/PipelineControllerTests.cs`
- Test: helper catalog state nếu tách khỏi Razor.

**Interfaces:**
- Produces: `LlmModelInfo.DownloadError` ở Core và App, `null` khi không có lỗi; lỗi mới thay lỗi cũ ở lượt download kế.

- [ ] **Step 1: Viết test RED**: downloader ném `"disk full"` → List trả `DownloadError="disk full"`, Installed/Downloading false; retry xoá lỗi cũ; App map `downloadError`; reload trả array rỗng giữ catalog non-empty trước đó và báo trạng thái load failed.
- [ ] **Step 2: Chạy RED** các focused classes; expected: catch hiện nuốt exception/App bỏ field/rỗng được coi thành công.
- [ ] **Step 3: Implement** dictionary lỗi theo model dưới lock, clear khi start/delete/retry thành công theo semantics tối thiểu; log stderr; UI render lỗi từng model và giữ last-valid catalog khi response rỗng.
- [ ] **Step 4: Chạy GREEN** focused tests + build.
- [ ] **Step 5: Commit** model failure/catalog + tests.

### Task 6: `core.stop` và chuyển local→API giải phóng llama.cpp

**Files:**
- Modify: `ReTran.Core/Program.cs`
- Modify: `ReTran.Core/Llm/LlamaServerHost.cs` chỉ nếu lifecycle test chứng minh cần.
- Test: test lifecycle/RPC phù hợp dưới `ReTran.Tests/`.

**Interfaces:**
- Consumes: `core.stop`, provider switch, existing `LlamaServerHost.Dispose()`.
- Produces: một đường cleanup dùng chung, idempotent, dispose cả server reference và clear reference.

- [ ] **Step 1: Viết test RED** bằng fake host/process seam ở boundary thật: stop sau local start gọi kill/dispose; start API sau local dispose host cũ; stop lặp không double-dispose.
- [ ] **Step 2: Chạy RED** và ghi nhận đúng nhánh còn giữ server.
- [ ] **Step 3: Implement** local function cleanup được `core.stop`, factory switch và shutdown dùng chung; không giữ reference tới process đã dispose.
- [ ] **Step 4: Chạy GREEN** lifecycle + Core serve tests.
- [ ] **Step 5: Commit** server cleanup + tests.

### Task 7: Hoàn thiện Settings theo spec OCR/LLM

**Files:**
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor`
- Modify: `ReTran.App/Core/PipelineController.cs`
- Modify: `ReTran.App/Services/I18nStrings.cs`
- Modify: Core RPC/server status chỉ ở field còn thiếu.
- Test: `ReTran.Tests/PipelineControllerTests.cs`, validation/helper tests và i18n tests.

**Interfaces:**
- Consumes: model/runtime progress và failure từ Task 1–5.
- Produces: JSON textarea + inline errors; per-model OCR progress; GGUF/runtime/server lỗi; restore chỉ `ocrEngine`; LLM active thống nhất và prompt validation.

- [ ] **Step 1: Viết test RED** từng nhóm nhỏ: JSON malformed/rỗng → lỗi inline và không Save; Restore gửi `{}` nhưng giữ `ocrFps`, `captureFps`, merge settings; wire parse `llm.server.status`; prompt trắng khi provider llm bị chặn; Start local gọi `llm.models.set` cho active đã lưu trước `core.start`.
- [ ] **Step 2: Chạy RED** từng nhóm, không viết production trước khi thấy failure đúng.
- [ ] **Step 3: Implement UI tối thiểu**: control JSON theo schema, progress `%/ETA` từng dòng OCR, failure text, server status loading/error/ready, bỏ hint `/v1` trùng, một nguồn active local, nút Refresh/empty states theo finding.
- [ ] **Step 4: Chạy GREEN** focused + build frontend/App.
- [ ] **Step 5: Commit** spec UI + tests.

### Task 8: Standards, i18n và chất lượng hiển thị

**Files:**
- Modify: `ReTran.App/Services/I18nStrings.cs`
- Modify: `ReTran.App/Services/AppSettingsService.cs`
- Modify: `ReTran.Core/HttpUa.cs` và App `HttpUa.cs` nếu doc chưa đủ.
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor`
- Modify: `ReTran.App/Services/FileLoggerProvider.cs` nếu remarks còn mâu thuẫn.
- Test: `ReTran.Tests/I18nServiceTests.cs` và test validation liên quan.

**Interfaces:**
- Produces: bản dịch VN đầy đủ, docs khớp exception thật, save-scope note đúng, dispose chặn render thật, style dùng chung không đổi thiết kế.

- [ ] **Step 1: Viết test RED** cho mọi `LanguageName` VN còn trả English và các key mới có đủ vi/en; test dispose/render helper nếu tách được.
- [ ] **Step 2: Chạy RED** focused i18n/state tests.
- [ ] **Step 3: Implement** doc/i18n/dispose/style consolidation; `Refresh` có busy/empty/error rõ, không refactor ngoài finding.
- [ ] **Step 4: Chạy GREEN** focused + `dotnet build "ReTran Project.slnx"` + `npm run build` trong `ReTran.App/vite-project`.
- [ ] **Step 5: Commit** standards batch.

### Task 9: Full verification, review và báo cáo

**Files:**
- Modify: `.superpowers/sdd/2026-11-17-settings-review-hardening/progress.md` (ledger local).
- Modify: checklist plan này (local ticks theo convention repo).

- [ ] **Step 1: Chạy full C# suite**: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`; expected 0 failed. Nếu `OverlayFeedServerTests` SSE hoặc `LineGrouperTests` timing fail, rerun một lần trước khi kết luận.
- [ ] **Step 2: Chạy sidecar regression** từ `ReTran.OCR/`: `uv run python -m pytest tests`; expected toàn bộ pass (baseline 100).
- [ ] **Step 3: Review toàn diff** bằng fresh subagent theo Standards/Spec/regression; re-grade finding, sửa Critical/Important bằng một fix pass TDD, ghi Minor deferred vào ledger.
- [ ] **Step 4: Ghi số test, commit cuối và các ruling vào ledger**. Chỉ hỏi push khi suite xanh và diff đã review.
- [ ] **Step 5: Báo cáo tiếng Việt** các thay đổi, test thật, deferred minors và smoke checklist: rerun wizard, deferred OCR pair, runtime same-tag/update/fail, LLM local/API, core.stop giải phóng server.
