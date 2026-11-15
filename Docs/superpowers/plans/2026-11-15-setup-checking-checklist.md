# Plan: Checklist kiểm tra LIVE cho màn Checking (setup wizard)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Màn Checking của setup wizard hiển thị danh sách hạng mục kiểm tra cập nhật LIVE (chờ → đang kiểm tra → ✓/⚠/✗), thêm 2 mục thông tin (env, model), gộp cảnh báo GPU→CPU vào list (màn 1b nhập vào 1).

**Architecture:** `SystemCheck` (thuần, chạy trên thread nền) phát sự kiện tiến độ từng mục qua callback optional; `SetupCoordinator` gom thành `Checks` (mảng thay-thế-nguyên-khối, publish mỗi event); `SetupModal` render list + CSS scoped trong modal (không đụng Vite). Logic/kết quả check giữ nguyên, trừ **một chủ ý**: cổng uv.exe chuyển lên TRƯỚC cổng arch để thứ tự thực thi = thứ tự hiển thị (xem Task 1, step pin order).

**Tech Stack:** .NET 10 (App), xUnit v3, Blazor. Không dependency mới.

**Spec:** `Docs/superpowers/specs/2026-11-15-setup-checking-checklist-design.md` (đã duyệt 2026-11-15; mockup: `.superpowers/sdd/2026-11-04-first-run-setup-wizard/visual-compare/mockup-checking.html`).

## Global Constraints

- Không `git add -A`; commit đúng file mỗi task; branch `master`; không push.
- Build `dotnet build "ReTran Project.slnx"` → 0 error (34 warning xUnit1051 có sẵn).
- Test filter cần FQN: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<FQN>"`. Baseline: **863 = 861 passed + 2 skipped** (2 skip do ComfyUI giữ VRAM; isolated vẫn pass khi GPU rảnh).
- Doc comment C# song ngữ; chuỗi UI qua i18n vi/en (test parity `I18nServiceTests` ghim).
- Không rebuild Vite cho đổi UI này (style nằm trong `<style>` scoped của SetupModal).
- `SetupMessage` (key i18n + args) là kiểu chuẩn cho message wizard — tái dùng, không thêm cơ chế mới.

## Review Focus

1. **Callback tiến độ ném lỗi** — không được phá kết quả check (bọc try/catch quanh mỗi lần gọi callback). Test: `Progress_CallbackThrows_CheckStillSucceeds`.
2. **Re-run sau khi Failed** — `Checks` phải reset, không dính trạng thái ✗ cũ. Test: `Checks_ResetOnRetry_NoStaleFailures`.
3. **UI đọc `Checks` khi thread nền còn ghi** — không torn read: cập nhật bằng mảng thay-thế-nguyên-khối (`_checks = ... .ToArray()`), getter trả snapshot. Pin bằng test snapshot (`Checks_AfterRun_...` + ghi chú code).
4. **Mạng fail giữa chừng** — danh sách dừng đúng host lỗi, các mục sau giữ Pending, kết quả `!Ok` + message `setup.fail.network`. Test: `Progress_NetworkFail_StopsAtFailingHost_LastItemFails`.
5. **Đổi thứ tự gate (uv trước arch)** — máy vừa sai arch vừa thiếu uv.exe giờ báo lỗi uv; pin bằng test. Test: `UvMissing_WithWrongArch_ReportsUv_AndFilesItemFails`.

---

### Task 1: `SetupCheckItem` + kênh tiến độ trong `SystemCheck`

**Files:**
- Create: `ReTran.App/Services/Setup/SetupCheckItem.cs`
- Modify: `ReTran.App/Services/Setup/SystemCheck.cs` (Run + DecideFlavor + helpers)
- Modify: `ReTran.App/Services/Setup/SetupState.cs:19` (`private const string OfficialModelsSubdir` → `internal const`)
- Test: `ReTran.Tests/Setup/SystemCheckTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum SetupCheckState { Pending, Running, Ok, Warn, Fail }
public sealed record SetupCheckItem(string Id, SetupCheckState State, SetupMessage Label, SetupMessage? Detail = null);
// Run mở rộng (append cuối, optional — call site cũ không đổi):
public static SystemCheckResult Run(
    IVideoAdapterSource adapters, Func<string, long> diskFreeBytes, Func<string, bool> isHostReachable,
    Func<string, bool>? fileExists = null, Func<Architecture>? osArchitecture = null,
    Action<SetupCheckItem>? progress = null, Func<string, int>? countModelEntries = null);
```
- IDs & thứ tự (thứ tự phát = thứ tự hiển thị): `files`, `env`, `models`, `arch`, `gpu`, `disk`, `net:{host}` (3 host: `pypi.org`, `www.paddlepaddle.org.cn`, `github.com`).
- Label keys (Task 3 điền nội dung i18n; task này chỉ assert key/args):
  `setup.check.files.label` · `setup.check.env.label` · `setup.check.models.label` · `setup.check.arch.label` ({0}=`Windows x64`) · `setup.check.gpu.label` ({0}=tên card, {1}=driver product "617.42" hoặc "—") · `setup.check.gpu.none` (không có card NVIDIA) · `setup.check.disk.label` ({0}=GB trống) · `setup.check.net.label` ({0}=host).
- Detail keys: `setup.check.env.present` / `.absent` · `setup.check.models.present` ({0}=số mục) / `.absent` · `setup.check.gpu.build.gpu` / `.cpu` · `setup.check.disk.detail` ({0}=GB cần, {1}=GPU/CPU) · GPU warn dùng lại `setup.notice.*` · fail dùng lại `setup.fail.uvMissing/arch/disk/network`.
- Trình tự: phát `Pending` cho cả 9 mục trước, rồi từng mục `Running` → `Ok|Warn|Fail`. Callback bọc try/catch (nuốt lỗi báo cáo).
- `DecideFlavor` trả thêm phần tử thứ 4 `SetupMessage GpuLabel` (đổi tuple — private, không ảnh hưởng public).
- **Chủ ý:** cổng `uv.exe` chuyển lên TRƯỚC cổng arch (để thứ tự chạy = thứ tự hiển thị `files → env → models → arch → …`). Trường hợp hiếm cả-hai-fail giờ báo `setup.fail.uvMissing` thay vì arch.

- [ ] **Step 1: Viết test failing** — thêm vào `SystemCheckTests.cs` (dùng helper `Check(...)`, `FileSystem(...)`, `Adapters(...)` sẵn có):
  - `Progress_EmitsPendingThenRunningAndResults_InOrder`: thu `List<SetupCheckItem>` qua `SystemCheck.Run(..., progress: list.Add)` (adapters NvidiaCurrent, đủ điều kiện); assert: 9 event đầu = `Pending` với ids đúng thứ tự trên; các event sau theo cặp `Running` → kết quả cho từng mục theo đúng thứ tự; 3 mục net Ok theo host; event cuối là `net:github.com` Ok; `result.Ok`.
  - `UvMissing_WithWrongArch_ReportsUv_AndFilesItemFails`: `uvPresent:false` + `osArchitecture: () => Architecture.Arm64`; assert `files` Running→Fail (Detail key `setup.fail.uvMissing`), KHÔNG có event `arch:*`, `result.FailureReason!.Key == "setup.fail.uvMissing"`.
  - `Progress_EnvAndModels_ReportPresence`: run với `venvPresent:true` → env Detail key `.present`; `venvPresent:false` → `.absent`; `countModelEntries: _ => 3` → models Detail args `["3"]` + key `.present`; `_ => 0` → `.absent`.
  - `Progress_NetworkFail_StopsAtFailingHost_LastItemFails`: `isHostReachable` false chỉ với `github.com`; assert 3 event `net:*` Running, event cuối `net:github.com` Running→Fail (Detail `setup.fail.network`), không event nào sau đó; `!result.Ok`.
  - `Progress_GpuWarn_EmitsNoticeAsDetail`: NVIDIA driver cũ (vd `NvidiaWithDriver(560, 76)`) → `gpu` event `Warn` + Detail key `setup.notice.oldDriver`.
  - `Progress_CallbackThrows_CheckStillSucceeds`: `progress: _ => throw new InvalidOperationException("x")` → `Run` trả `Ok=true`, không ném.

- [ ] **Step 2: Chạy test — phải fail** (compile fail: `SetupCheckItem` chưa có).
  Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SystemCheckTests"` → FAIL CS0246.

- [ ] **Step 3: Implement** —
  - `SetupCheckItem.cs`: enum + record + doc comment song ngữ (record: Id định danh để UI cập nhật theo Id; Label/Detail là `SetupMessage` để modal resolve i18n).
  - `SetupState.cs`: `internal const string OfficialModelsSubdir = "official_models";`
  - `SystemCheck.cs`:
    - `Run` nhận `progress`, `countModelEntries`; `countModelEntries ??= DefaultCountModelEntries` với impl: `Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0` trong try/catch `(Exception) → 0`; dir = `Path.Combine(SetupPaths.ModelsDir, SetupState.OfficialModelsSubdir)`.
    - helper `private static void Emit(Action<SetupCheckItem>? progress, SetupCheckItem item)` — try/catch nuốt.
    - phát `Pending` cho 9 mục trước (const array ids/labels), rồi luồng: `files` (gate uv — chuyển lên đầu) → `env` (từ `venvPresent`) → `models` (count) → `arch` (gate) → `gpu` (`DecideFlavor`, Warn nếu notice ≠ null với Detail = notice; ngược lại Ok + Detail build key) → `disk` (gate; Ok label = GB trống `ToString("0.##")`, Detail = min GB + `"GPU"/"CPU"`) → `net` từng host (`Running` → probe → Ok hoặc Fail + Detail `setup.fail.network` rồi `return Fail(...)` như cũ).
    - `DecideFlavor` trả `(OcrFlavor, SetupMessage? Notice, string? CudaWheel, SetupMessage GpuLabel)`; GpuLabel: có card → `setup.check.gpu.label` args (name, driverDisplay) với driverDisplay = product version từ `TryGetNvidiaProductVersion` ("617.42") else raw/"—"; không card → `setup.check.gpu.none`.
  - Giữ nguyên mọi message/gate/threshold hiện có (chỉ thêm emit + đổi vị trí khối uv gate).

- [ ] **Step 4: Chạy test — phải pass.** `SystemCheckTests` toàn bộ (60 + 6 mới) pass.

- [ ] **Step 5: Commit** — `git add ReTran.App/Services/Setup/SetupCheckItem.cs ReTran.App/Services/Setup/SystemCheck.cs ReTran.App/Services/Setup/SetupState.cs ReTran.Tests/Setup/SystemCheckTests.cs` → `feat(setup): report per-item progress from the checking step`

---

### Task 2: `SetupCoordinator.Checks` + seam `countModelEntries`

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs` (ctor + field + `Checks` + `RunCheckingStep`)
- Test: `ReTran.Tests/Setup/SetupCoordinatorTests.cs`

**Interfaces:**
- Produces: `public IReadOnlyList<SetupCheckItem> Checks { get; }` — snapshot mảng, thay-thế-nguyên-khối mỗi event (`_checks = _checks.Select(i => i.Id == item.Id ? item : i).ToArray()`, event mới id lạ thì append — thực tế Pending đến trước nên luôn replace); reset `_checks = []` đầu `RunCheckingStep`; `Publish()` mỗi event.
- Ctor thêm `Func<string, int>? countModelEntries = null` ngay sau `fileExists` (named-arg callers không đổi; MauiProgram truyền positional tới `closeApp` rồi `logger:` named — không đổi).
- `RunCheckingStep` truyền `progress: OnCheckProgress, countModelEntries: _countModelEntries` vào `SystemCheck.Run`.
- Consumes: Task 1 (`SetupCheckItem`, `Run` signature mới).

- [ ] **Step 1: Viết test failing** — trong `SetupCoordinatorTests.cs`:
  - Cập nhật helper `NewCoordinator(...)`: thêm param `Func<string, int>? countModelEntries = null` truyền xuống ctor với default `_ => 0` (hermetic).
  - `CheckingProgress_Checks_ContainNineItems_InOrder_AllOk`: `WriteSetupJson("""{"flavor":"gpu","language":"vi"}""")`; `StartAsync` (dừng ở ChoosingLanguage sau Checking); assert `Checks.Count == 9`, ids đúng thứ tự (`files, env, models, arch, gpu, disk, net:pypi.org, net:www.paddlepaddle.org.cn, net:github.com`), tất cả `Ok`, models Detail key `.absent` (fake 0).
  - `Checks_ResetOnRetry_NoStaleFailures`: `NewCoordinator(isHostReachable: host => host != "github.com")` → `StartAsync` → `Failed`; đổi cờ fake (biến local điều khiển) → `RetryAsync` → `Done`; assert `Checks.Count == 9`, không item nào `Fail`, `net:github.com` cuối = `Ok`.
  - `Checks_BeforeAnyRun_Empty`: coordinator mới → `Assert.Empty(coordinator.Checks)`.
- [ ] **Step 2: Chạy test — fail** (`Checks` chưa tồn tại; FQN `SetupCoordinatorTests`).
- [ ] **Step 3: Implement** như Interfaces (doc comment song ngữ cho `Checks` + param mới; comment nêu lý do mảng thay-thế-nguyên-khối: UI đọc khi thread nền ghi).
- [ ] **Step 4: Chạy test — pass** (`SetupCoordinatorTests` 32 + 3 mới).
- [ ] **Step 5: Commit** — `git add` 2 file → `feat(setup): expose live check items on the coordinator`

---

### Task 3: UI checklist + i18n

**Files:**
- Modify: `ReTran.App/Services/I18nStrings.cs` (2 block VI/EN)
- Modify: `ReTran.App/Components/Shared/SetupModal.razor` (case `Checking` + `<style>` scoped)

**Interfaces:**
- Consumes: `SetupCheckItem`/`SetupCheckState` (Task 1), `Coordinator.Checks` (Task 2), glyph + `L.Fmt(label.Key, label.Args.ToArray())` cho Label/Detail.
- Markup màn Checking: giữ `<p>@(L["setup.checking"])</p>`, **bỏ** khối `Coordinator.Notice` (nội dung đã nằm ở item `gpu` Warn), thêm:
```razor
@if (Coordinator.Checks.Count > 0)
{
    <ul class="setup-checks">
        @foreach (SetupCheckItem item in Coordinator.Checks)
        {
            <li class="@CheckItemClass(item.State)">
                <span class="mark">@if (item.State == SetupCheckState.Running) { <span class="rt-spinner"></span> } else { @CheckGlyph(item.State) }</span>
                <span class="lbl">@L.Fmt(item.Label.Key, item.Label.Args.ToArray())</span>
                @if (item.Detail is { } det) { <span class="det">@L.Fmt(det.Key, det.Args.ToArray())</span> }
            </li>
        }
    </ul>
}
```
  helpers: `CheckItemClass` (`pending|running|ok|warn|fail`), `CheckGlyph` (`· ✓ ⚠ ✗`).
- `<style>` scoped (trong block style sẵn có của modal) theo mockup: `.setup-checks` grid gap 7px, `max-height:264px; overflow-y:auto`, `li` grid `18px 1fr`, `.mark` 18×18, `.det` faint 11px + `overflow-wrap:anywhere`, màu theo class (`ok→--rt-data`, `warn→--rt-tally`, `fail→--rt-danger`, `pending→--rt-faint`), `.rt-spinner` (11px, border-top tally, `rt-mock-spin .9s linear infinite` + `@keyframes`).
- i18n (VI / EN) — thêm vào đúng 2 block setup sẵn có:
  - `setup.check.files.label`: "Tệp cài đặt (uv.exe)" / "Setup files (uv.exe)"
  - `setup.check.env.label`: "Môi trường OCR (venv)" / "OCR environment (venv)"
  - `setup.check.models.label`: "Model OCR có sẵn" / "OCR models on disk"
  - `setup.check.arch.label`: "Kiến trúc: {0}" / "Architecture: {0}"
  - `setup.check.gpu.label`: "Card đồ hoạ: {0} · driver {1}" / "Graphics: {0} · driver {1}"
  - `setup.check.gpu.none`: "Không có GPU NVIDIA" / "No NVIDIA GPU"
  - `setup.check.disk.label`: "Đĩa trống: {0} GB" / "Free disk: {0} GB"
  - `setup.check.net.label`: "Mạng: {0}" / "Network: {0}"
  - `setup.check.env.present`: "đã có — bước cài sẽ kiểm tra lại flavor" / "already present — the sync step will re-check the flavor"
  - `setup.check.env.absent`: "chưa có — sẽ cài ở bước sau" / "not present — will be installed in a later step"
  - `setup.check.models.present`: "đã có sẵn ({0} mục)" / "already present ({0} entries)"
  - `setup.check.models.absent`: "chưa có — sẽ tải ở bước sau" / "not present — will be downloaded in a later step"
  - `setup.check.gpu.build.gpu`: "bản GPU (CUDA 12.9)" / "GPU build (CUDA 12.9)"
  - `setup.check.gpu.build.cpu`: "bản CPU" / "CPU build"
  - `setup.check.disk.detail`: "cần {0} GB (bản {1})" / "needs {0} GB ({1} build)"
- [ ] **Step 1: Implement** markup + helpers + style + keys (UI không unit-test theo convention; xác minh ở Task 4 bằng board).
- [ ] **Step 2: Build + parity** — `dotnet build` 0 error; `dotnet test ... -- --filter-class "I18nServiceTests"` pass (parity key mới).
- [ ] **Step 3: Commit** — `git add ReTran.App/Components/Shared/SetupModal.razor ReTran.App/Services/I18nStrings.cs` → `feat(setup): live checklist UI on the checking screen`

---

### Task 4: Xác minh + board visual

**Files:** scratch (không commit): `.superpowers/sdd/2026-11-04-first-run-setup-wizard/visual-compare/index.template.html`

- [ ] **Step 1:** `dotnet build "ReTran Project.slnx"` → 0 error.
- [ ] **Step 2:** Focused: `SystemCheckTests` + `SetupCoordinatorTests` + `I18nServiceTests` pass.
- [ ] **Step 3:** Full suite `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` → ≥ 863 (861 pass + 2 skip GPU-busy; nếu ComfyUI đã tắt thì đủ 863 pass cũng được).
- [ ] **Step 4:** Cập nhật board: thay 2 figure Checking (1 & 1b) trong `index.template.html` bằng 3 trạng thái mới (copy markup từ `mockup-checking.html`), sửa caption/"quan sát", regenerate `index.html` bằng script inline CSS+fonts sẵn có, `browser.preview` lại cho user.
- [ ] **Step 5:** Không commit (scratch ignored); báo cáo cuối + smoke checklist (chạy lại setup trên máy thật: xem 3 dòng mạng hiện dần; máy đã setup → env/models "đã có"; rút mạng → ✗ đúng host).

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** checklist live (4.1–4.3) → T1/T2/T3; env+models → T1; notice gộp → T1 (gpu Warn) + T3 (bỏ paragraph); i18n → T3; xác minh/board → T4. Calls §5 của spec: order=thực thi (T1), env/models thông tin (T1), đếm official_models (T1), style scoped (T3), không tách component (đúng — không task nào tách). ✔
2. **Step scan:** mỗi step một hành động; không step "TBD"; code block chỉ ở chỗ signature+test không tự quyết (markup modal, vì cần đúng class/CSS đã duyệt qua mockup). ✔
3. **Type consistency:** `SetupCheckItem`/`SetupCheckState` T1 → dùng ở T2 dB T3; `Checks` T2 → T3; keys T1 assert = keys T3 điền — đối chiếu từng key ✔ (danh sách key chép khớp hai chỗ).
4. **Review Focus:** 5 dòng đều có test pin trong T1/T2 ✔.
5. **Proportion:** plan ngắn hơn spec; không transcribe body thuật toán (chuỗi/count để implementer viết theo signature + test). ✔
