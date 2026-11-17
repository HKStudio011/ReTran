# Wizard vòng 2 (det/rec + Ngôn ngữ + LLM + tiến độ) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thay 2 màn A/B (16-11) bằng 3 màn mới — Bước 2 "Chọn model OCR" (2 khối det/rec, ≥1 mỗi khối, chấm "Dùng chính" mỗi khối), Bước 3 "Ngôn ngữ" (tick nhiều/trống + chấm "Đọc chính"), Bước 4 "Chọn LLM" (nhóm SizeClass động, tick nhiều/trống) — bỏ màn "chọn ngôn ngữ chính"; bước 8 llama.cpp có điều kiện + cập nhật khi cũ; tải model hiện % + ETA; mọi danh sách model suy từ nguồn catalog (không hardcode).

**Architecture:** `OcrModelChoices` chuyển từ "token cỡ + 8 mã ngôn ngữ hardcode" sang **derive thuần từ `OcrCatalogDisplay`** (catalog injectable cho test). `SetupState` +8 field (tên model) + migration một lần từ file 16-11. Coordinator: 3 pre-pipeline pause mới + 3 continue + tải theo lựa chọn + Applying ghi cặp + `LlmActiveModel` + reconcile mở rộng. Core: LLM catalog chuyển sang file JSON + tiến độ GGUF/runtime; sidecar: tiến độ tải OCR; App: parse tiến độ + helper %/ETA.

**Tech Stack:** .NET 10 (App/Core/Tests), Blazor, xUnit v3; Python sidecar (uv, CPython 3.13, PaddleOCR).

**Spec:** `Docs/superpowers/specs/2026-11-17-wizard-detrec-langs-llm-design.md` (authority — đọc trước mọi task). Mockup đã duyệt: `.superpowers/sdd/2026-11-04-first-run-setup-wizard/visual-compare/mockup-detrec-langs-llm.html`.

## Global Constraints

- Git Windows: `& "C:\Program Files\Git\cmd\git.exe" -C "D:\Code\ReTran Project" ...`; nhánh `master`; commit **đúng file của task**, KHÔNG `git add -A`; không push khi chưa được hỏi.
- Build `dotnet build "ReTran Project.slnx"` → 0 error. Test thường: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` (filter class: `-- --filter-class "TenClass"`). **Baseline full suite hiện tại: 907 = 905 pass + 2 skip** (2 skip VRAM). Flaky-known: `OverlayFeedServerTests` (SSE) + `LineGrouperTests` (timing) — chạy lại lượt 2 trước khi kết luận regression.
- Sidecar: mọi lệnh qua `uv run` từ `ReTran.OCR/` (vd `uv run pytest tests -v`); KHÔNG dùng system Python; không pollute venv khác.
- Doc comment C# **song ngữ** (EN trước, VN sau); i18n **vi/en parity** (I18nServiceTests ghim); UI không hardcode chuỗi.
- **Không đụng**: capture, OCR engine runtime, overlay server, màn Checking/Failed/FlavorSwitch/popup, `retran-ui-v2.html` (WIP của user), `ReTran.OCR/src/retran_core/ocr/catalog.py` (chỉ `models.py` được sửa ở Task 7).
- Dữ liệu từ nguồn: không hardcode cỡ/ngôn ngữ/quant; nhãn/tên lạ phải có fallback hiển thị (không vỡ UI, không hiện key i18n).
- **Legacy để build xanh từng task**: enum `ChoosingLanguage`/`ChoosingOcrTier`/`ChoosingExtraLanguages` + `ContinueWithOcrTierAsync`/`ContinueWithExtraLanguagesAsync` + API cũ của `OcrModelChoices` (`Tiers`, `LegacyLanguageCodes`, `TierDet/TierRec/TierOfDet`, `PreselectTiers`, `PreselectLegacyLanguages`, `NeededModels`/`ExpectedPair` cũ) **được GIỮ tới Task 9**, Task 9 dọn sạch.
- Drift test `OcrCatalogDisplayTests.DisplayCatalog_MatchesPythonCatalog` phải luôn xanh (không sửa mirror/catalog đợt này).

## Review Focus

1. **Máy cũ (file 16-11)**: rerun không hỏi lại (ngoài đúng 1 lượt bước 4) và **giữ nguyên cặp đang chạy** (activeLang suy từ `language` legacy cũ). Test: `Migration_OldFile_MapsNamesAndActiveLang`, `Flow_AllAnswered_SkipsPauses`.
2. **Ca trống**: bước 3/4 không tick ⇒ không chấm, `activeLang`/`activeLlm` = null; LLM trống ⇒ bước 8 mark done KHÔNG gọi `llm.runtime.*`. Tests: `Languages_Empty_NoActive`, `Llm_Empty_SkipsRuntimeDownload`.
3. **Catalog lạ**: thêm `PP-OCRv6_big_det/rec`, thêm/xoá legacy rec ⇒ danh sách tự đúng, không vỡ; `eslav` không tạo "Bản", sibling `*_server` cùng langs thì hiện 2 "Bản". Tests: `Derive_UnknownSize_ShowsWithFallback`, `LanguageChoices_EslavAbsorbed_ServerSiblingShown`.
4. **%+ETA thiếu dữ liệu**: thiếu `totalBytes` ⇒ indeterminate; speed 0/âm ⇒ "đang kết nối lại…"; không NaN/âm. Test: `Format_MissingData_NoNaN`.
5. **Runtime cũ / mất mạng**: tag cũ ⇒ tải/cập nhật; `latest` null (mất mạng) ⇒ skip như bản ok, không chặn wizard. Tests: `LlmRuntime_Outdated_ReDownloads`, `LlmRuntime_Offline_SkipsAsOk`.

---

### Task 1: `SetupState` v2 + migration

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupState.cs`
- Test: `ReTran.Tests/Setup/SetupStateTests.cs`

**Interfaces:**
```csharp
// Thêm cuối record (positional, mặc định null):
//   List<string>? Dets = null, string? ActiveDet = null,
//   List<string>? Recs = null, string? ActiveRec = null,
//   List<string>? Langs = null, string? ActiveLang = null,
//   List<string>? LlmModels = null, string? ActiveLlm = null
// Migration: trong Load, sau deserialize — nếu Dets is null && Tiers is not null:
//   Dets  = tiers.Select(t => $"PP-OCRv6_{t}_det"),  Recs = tiers.Select(t => $"PP-OCRv6_{t}_rec")
//   ActiveDet = $"PP-OCRv6_{activeTier}_det", ActiveRec = $"PP-OCRv6_{activeTier}_rec"
//   Langs = extraLangs ∪ {language nếu Language != null && !OcrModelChoices.IsV6Covered(language)
//                                                   && OcrModelChoices.LegacyRecFor(language) != null}
//   ActiveLang = legacy ? LegacyRecFor(language) : null      // rec name (spec §10)
//   (Language giữ nguyên trong file cũ để đọc; wizard không ghi mới)
```
`IsV6Covered`/`LegacyRecFor` là API **hiện có** (giữ tên qua Task 2).

- [x] **Step 1: Test failing** — `SetupStateTests`: `V2Fields_RoundTrip` (8 field sống qua Save→Load), `Migration_OldFile_MapsNamesAndActiveLang` (file `{"flavor":"gpu","language":"ko","tiers":["small","medium"],"activeTier":"small","extraLangs":["ru"]}` → `Dets=["PP-OCRv6_small_det","PP-OCRv6_medium_det"]`, `Recs=[..._rec]`, `ActiveDet/ActiveRec = small`, `Langs` chứa `cyrillic_PP-OCRv5_mobile_rec` VÀ `korean_PP-OCRv5_mobile_rec`, `ActiveLang = korean_PP-OCRv5_mobile_rec`), `Migration_OldFile_AutoOrV6Language_ActiveLangNull` (`language":"vi"`/`"auto"` → ActiveLang null), `LegacyJson_NewFieldsNull`.
- [x] **Step 2: Chạy — fail** (`--filter-class "SetupStateNeedsSetupTests"`; lưu ý tên class thật). Kỳ vọng: compile fail/assert fail.
- [x] **Step 3: Implement** — thêm 8 positional param + `<param>` docs song ngữ; hàm migration private gọi trong `Load` (chỉ khi `Dets is null && Tiers is not null`); migration **không tự Save** (chỉ in-memory; Save xảy ra ở continue đầu tiên như luồng thường).
- [x] **Step 4: Pass** — `SetupStateNeedsSetupTests` xanh (cả test cũ roundtrip — cập nhật test "đủ tập khóa JSON" nếu danh sách key đổi: thêm `dets/activeDet/recs/activeRec/langs/activeLang/llmModels/activeLlm`).
- [x] **Step 5: Commit** — `feat(setup): wizard v2 state fields and migration`

---

### Task 2: `OcrModelChoices` v2 — derive từ catalog

**Files:**
- Modify: `ReTran.App/Services/Setup/OcrModelChoices.cs`
- Test: `ReTran.Tests/Setup/OcrModelChoicesTests.cs`

**Interfaces (THÊM; API cũ giữ tới Task 9):**
```csharp
// Mọi hàm nhận catalog optional để test tiêm catalog giả: IReadOnlyList<OcrModelInfo>? catalog = null → OcrCatalogDisplay.Models
public static IReadOnlyList<string> DetModels(IReadOnlyList<OcrModelInfo>? c = null);      // kind=="det", thứ tự catalog
public static IReadOnlyList<string> RecModels(IReadOnlyList<OcrModelInfo>? c = null);      // kind=="rec" && name bắt đầu "PP-OCRv6"
public static string SizeToken(string modelName);                                          // "PP-OCRv6_medium_det" → "medium"; lạ → nguyên tên
public static IReadOnlyList<string> SizeTokens(IReadOnlyList<OcrModelInfo>? c = null);     // distinct token theo catalog, vd tiny/small/medium (thêm big là tự có)
public sealed record LanguageChoice(string PrimaryLang, IReadOnlyList<string> Variants);   // Variants = tên rec legacy đã lọc
public static IReadOnlyList<LanguageChoice> LanguageChoices(IReadOnlyList<OcrModelInfo>? c = null);
//   rule: rec không-v6; "ngôn ngữ chính" = lang đầu khác "en" (entry chỉ có en → bỏ);
//   chỉ giữ nhóm có primary ngoài phủ v6 (dùng IsV6Covered); trong nhóm BỎ entry có langs là TẬP CON THẬT của entry khác (eslav ⊂ cyrillic).
public sealed record ModelPreselect(IReadOnlyList<string> Ticked, IReadOnlyList<string> Locked, string Active);
public static ModelPreselect PreselectModels(IReadOnlyCollection<string> installed, string? current, IReadOnlyList<string> candidates);
//   ticked = candidates có ≥1 thành viên đã cài & khoá khi ĐỦ (hai nửa det/rec dùng chung: caller truyền candidates per khối);
//   máy sạch → ticked=[candidates ưu tiên "small" nếu có, else đầu]; Active = current nếu ticked, else small nếu ticked, else ticked[0].
public static IReadOnlyList<string> NeededModels(
    IReadOnlyList<string> dets, IReadOnlyList<string> recs, IReadOnlyList<string> langs, IReadOnlyList<string>? c = null);
//   = dets ∪ recs ∪ langs (dedupe, giữ thứ tự)
public static (string Det, string Rec) ExpectedPair(string activeDet, string? activeLangRec, string activeRec);
//   = (activeDet, activeLangRec ?? activeRec)
```
Lưu ý "đủ det+rec" cho khối: khối det đủ = chính det đó đã cài; giữ luật 16-11: installed ⟺ tên có trong `installedModels`.

- [x] **Step 1: Test failing** — `OcrModelChoicesTests` (viết mới/thay phần cũ):
  `DetModels_And_RecModels_FromCatalog` (catalog thật: 3 det, 3 rec v6) ·
  `SizeTokens_IncludesCatalogToken_And_BigAppears` (catalog giả + `PP-OCRv6_big_det` ⇒ token `big` xuất hiện; `SizeToken("PP-OCRv6_big_rec")=="big"`) ·
  `LanguageChoices_EightToday` (đúng 8: ru/ko/ar/th/ta/te/hi/el theo thứ tự catalog, mỗi nhóm 1 variant `cyrillic_…_mobile_rec` cho ru…) ·
  `LanguageChoices_EslavAbsorbed_ServerSiblingShown` (catalog thật: không có nhóm nào chứa `eslav_…`; catalog giả + `korean_PP-OCRv5_server_rec` cùng langs `["ko","en"]` ⇒ nhóm ru/ko có 2 variants) ·
  `LanguageChoices_PlainEnglishEntry_Dropped` (catalog giả entry `latin_...` langs `["fr","en"]` → primary `fr` ngoài vùng phủ v6? *(fr phủ v6 → nhóm bỏ)* — pin luật "primary ngoài phủ v6") ·
  `PreselectModels_*` (đủ/khoá, bán phần, máy sạch, active theo current) ·
  `NeededModels_Union_NoDup` · `ExpectedPair_LegacyWins`/`ExpectedPair_FallbackRec`.
- [x] **Step 2: fail** → **Step 3: implement** → **Step 4: pass** (`OcrModelChoicesTests` toàn bộ; test cũ của API legacy vẫn xanh vì API còn).
- [x] **Step 5: Commit** — `feat(setup): derive model choices from the OCR catalog`

---

### Task 3: Flow coordinator v2 — 3 pause/continue mới

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs`
- Test: `ReTran.Tests/Setup/SetupCoordinatorTests.cs`

**Interfaces:**
```csharp
public enum SetupStep { ..., ChoosingOcrModels, ChoosingLanguages, ChoosingLlmModels, ... }  // THÊM; giữ member cũ
public string? ActiveDet  => _state.ActiveDet;   // + ActiveRec, ActiveLang, ActiveLlm
public Task ContinueWithOcrModelsAsync(IReadOnlyList<string> dets, string activeDet,
                                       IReadOnlyList<string> recs, string activeRec, CancellationToken ct = default);
public Task ContinueWithLanguagesAsync(IReadOnlyList<string> langs, string? activeLang, CancellationToken ct = default);
public Task ContinueWithLlmModelsAsync(IReadOnlyList<string> llmModels, string? activeLlm, CancellationToken ct = default);
```
Chuỗi `StartAsync` (sau Checking): `Dets is null` → ChoosingOcrModels; `Langs is null` → ChoosingLanguages; `LlmModels is null` → ChoosingLlmModels; else pipeline (`ContinueAfterAnswersAsync` cập nhật theo chuỗi này; RetryAsync nhánh Checking cũng vậy).
Validate (trước `TryBeginRun`): dets.Count ≥ 1 & mọi det ∈ `DetModels()` & activeDet ∈ dets; recs tương tự (`RecModels()`); langs mọi phần tử ∈ tập langs của `LanguageChoices()`; activeLang null hoặc ∈ langs; llmModels phần tử khác rỗng; activeLlm null hoặc ∈ llmModels. Sai → `ArgumentException`.
`ContinueWithLlmModelsAsync` khi lưu: nếu `llmModels.Count > 0` ⇒ **xoá marker** `downloadingllmruntime` + `downloadingmodels` (để tải bù — spec §7/§10). Marker mới: `choosingocrmodels`, `choosinglanguages`, `choosingllmmodels` (tự sinh qua `StepKey`).

- [x] **Step 1: Test failing** — `Start_NoAnswers_PausesAtOcrModels` (file cũ chỉ `language` → sau StartAsync: `ChoosingOcrModels`; `ActiveDet` null) · `Flow_Order_ModelsThenLanguagesThenLlm` (flavor-only: Start→OcrModels; continue(dets,recs)→Languages; continue→LlmModels; continue→Done + downloads như cũ) · `ContinueModels_ZeroDet_Throws` / `ZeroRec_Throws` / `BadActive_Throws` · `ContinueLanguages_BadLang_Throws` · `ContinueLlm_BadActive_Throws` · `Flow_AllAnswered_SkipsPauses` (đủ 8 field + markers done ⇒ Done, `_bridge.Calls` rỗng) · `ActiveProps_Exposed`.
- [x] **Step 2: fail** → **Step 3: implement + cập nhật fixtures cũ**: mọi fixture có `tiers/activeTier/extraLangs` (không phải case cũ chủ đích) đổi sang `dets/recs/activeDet/activeRec/langs/activeLang` + thêm `llmModels: []`; các fixture có `language` — giữ (migration tự chạy) hoặc thay thẳng nếu test cần chủ đích file cũ; các test hành vi cũ đi qua chuỗi 3 pause mới (thay `ContinueWithLanguageAsync` bằng `ContinueWithOcrModelsAsync`…). `Continue*` cũ giữ nguyên (dead) — không xoá.
- [x] **Step 4: pass** — `SetupCoordinatorTests` toàn bộ xanh.
- [x] **Step 5: Commit** — `feat(setup): det/rec, language and LLM pauses in the wizard flow`

---

### Task 4: Tải + Applying + Reconcile v2

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs` (+ property/record tiến độ)
- Test: `ReTran.Tests/Setup/SetupCoordinatorTests.cs`

**Interfaces:**
```csharp
public sealed record SetupDownloadInfo(string Name, long ReceivedBytes, long TotalBytes, double? BytesPerSec);
public IReadOnlyList<SetupDownloadInfo> Downloads => _downloads;   // snapshot thay nguyên khối như Checks
```
`RunDownloadModelsStepAsync` v2: needed OCR = `NeededModels(dets, recs, langs)` (mọi tên cần đều là model sidecar) → như cũ (list → download thiếu → poll mọi tên installed). Sau đó **pha LLM (GGUF)**: nếu `llmModels ≠ []` → `llm.models.list` (Core) → `llm.models.download` cho model chưa `installed` → poll `llm.models.list` tới khi mọi tên `installed`. Trong lúc poll: cập nhật `_downloads` từ `progress` trả về (nếu có — Task 7/6; khi chưa có thì rỗng). Bỏ hẳn các bước dùng API cũ (`Tiers`…).
`RunApplyingStep` v2: `(det, rec) = ExpectedPair(ActiveDet ?? "PP-OCRv6_small_det", ActiveLang, ActiveRec ?? "PP-OCRv6_small_rec")` → ghi `OcrDet/OcrRec`; nếu `ActiveLlm ≠ null` → ghi thêm `LlmActiveModel` + gọi `llm.models.set` **best-effort** (catch → log warn; settings vẫn ghi) — spec §6.
`ReconcileMarkers` v2: OCR như 16-11 nhưng trên needed mới (`NeededModels(dets, recs, langs)`); cặp lệch = `ExpectedPair(...) ≠ settings.OcrDet/OcrRec`; **thêm**: `ActiveLlm ≠ null && settings.LlmActiveModel ≠ ActiveLlm` ⇒ clear `applying`. (GGUF/runtime không filesystem-check — đã clear theo answer-change ở Task 3 + tự verify khi bước chạy.)
Ctor: thêm seam `Func<string, bool>? modelDirExists` đã có — dùng tiếp; **KHÔNG** thêm seam mới cho LLM.

- [x] **Step 1: Test failing** — `DownloadSet_V2_TiersLegacyLlm` (dets [small_det], recs [small_rec], langs [korean_rec], llmModels [Hy-MT2-1.8B-UD-Q4_K_XL] ⇒ `_bridge.Downloaded` = 3 model OCR + `_bridge.LlmDownloaded = [1.8B…]`; poll cả 2 pha) · `Applying_V2_PairAndLlmActive` (activeDet medium_det + activeLang korean_rec + activeLlm 7B-Q5 ⇒ settings: OcrDet=medium_det, OcrRec=korean_rec, LlmActiveModel=7B-Q5 + có gọi `llm.models.set`) · `Applying_LlmSetFails_StillWritesSettings` (RPC throw ⇒ settings vẫn có LlmActiveModel, không Failed) · `Reconcile_MissingOcrModel_ClearsDownload` (giữ 16-11, dữ liệu v2) · `Reconcile_LlmActiveMismatch_ClearsApplying` · `Reconcile_AllPresent_NoClears` · `ContinueLlm_NewChoices_ClearsMarkers_AndDownloads` (answers LLM mới ⇒ marker 8/9 clear khi có tick — hành vi Task 3, pin tại đây với tải thật).
- [x] **Step 2: fail** → **Step 3: implement** (giữ API cũ dead) → **Step 4: pass** — `SetupCoordinatorTests` toàn bộ.
- [x] **Step 5: Commit** — `feat(setup): v2 downloads, applying and reconcile`

---

### Task 5: Bước 8 có điều kiện + cập nhật runtime

**Files:**
- Modify: `ReTran.Core/Llm/LlamaRuntimeManager.cs` (nếu cần), `ReTran.Core/Llm/LlmRpcHandlers.cs` (`llm.runtime.status` thêm `latestTag`), `ReTran.App/Services/Setup/SetupCoordinator.cs` (`RunDownloadLlmRuntimeStepAsync`), `ReTran.App/Core/PipelineController.cs` (parse `latestTag` nếu status đi qua đây — kiểm tra call site thực tế trong task)
- Test: `ReTran.Tests` (Core LLM tests hiện có + `SetupCoordinatorTests`)

**Interfaces:**
```csharp
// Core: LlamaRuntimeManager.CheckAsync đã có (Current, Latest) — status handler thêm:
//   ["latestTag"] = latest (best-effort; mất mạng/exception ⇒ null)
// Coordinator RunDownloadLlmRuntimeStepAsync v2:
//   if (_state.LlmModels is null or { Count: 0 }) { MarkStepDone; return Done; }   // KHÔNG gọi RPC nào
//   ... status: !installed ⇒ tải như cũ; installed && latestTag != null && latestTag != tag ⇒ tải/cập nhật;
//   installed && (latestTag == null || == tag) ⇒ MarkStepDone.
```
Fake bridge test: thêm `LlmRuntimeTag`/`LlmRuntimeLatestTag` settable.

- [x] **Step 1: Test failing** — `Llm_Empty_SkipsRuntimeDownload` (llmModels=[] ⇒ Done, `_bridge.LlmRuntimeDownloads` rỗng, KHÔNG call `llm.runtime.status`) · `LlmRuntime_Outdated_ReDownloads` (tag `b7000`, latest `b7234` ⇒ có download) · `LlmRuntime_UpToDate_Skips` (tag==latest ⇒ không download) · `LlmRuntime_Offline_SkipsAsOk` (latest null ⇒ không download, Done) · Core test: status handler trả `latestTag` (fake GitHub) + `null` khi lỗi.
- [x] **Step 2: fail** → **Step 3: implement** → **Step 4: pass** (class liên quan: Core LLM tests + `SetupCoordinatorTests`). *(Kết quả: dùng `llm.runtime.check` có sẵn — không đụng Core; xem ruling T5 trong ledger.)*
- [x] **Step 5: Commit** — `feat(setup): conditional llama.cpp step with outdated update`

---

### Task 6: LLM catalog từ file dữ liệu + tiến độ GGUF (Core)

**Files:**
- Create: `ReTran.Core/Llm/llm-models.json` (12 entry như `LlmModelCatalog.All` hiện tại; csproj `CopyToOutputDirectory`)
- Modify: `ReTran.Core/Llm/LlmModelCatalog.cs` (nạp JSON; API `All` giữ nguyên), `ReTran.Core/Llm/LlmModelManager.cs` (đếm bytes khi tải GGUF + `progress` trong list), `ReTran.Core/Llm/LlmRpcHandlers.cs` (nếu list shape đi qua đây)
- Test: `ReTran.Tests` (Core LLM tests — file hiện có)

**Interfaces:**
```csharp
// LlmModelCatalog: Load(path = null → file cạnh assembly); file thiếu/hỏng ⇒ rỗng + log 1 lần (không crash).
// LlmModelInfo list (llm.models.list) mỗi entry thêm optional: ["progress"] = { "receivedBytes":…, "totalBytes":…, "bytesPerSec":… }
// LlmModelManager: theo dõi trong lúc download (stream chunk; total từ HTTP content-length nếu có).
```
- [x] **Step 1: Test failing** — `Catalog_LoadsFromJson_TwelveEntries` · `Catalog_Malformed_ReturnsEmptyNoThrow` (tiêm path) · `List_ReportsProgressDuringDownload` (downloader giả ghi bytes ⇒ progress xuất hiện, xong thì mất).
- [x] **Step 2: fail** → **Step 3: implement** → **Step 4: pass** — các test Core LLM hiện có vẫn xanh (cập nhật nếu assert shape list).
- [x] **Step 5: Commit** — `feat(llm): catalog from data file and download progress`

---

### Task 7: Tiến độ tải OCR (sidecar)

**Files:**
- Modify: `ReTran.OCR/src/retran_core/ocr/models.py` (`ModelManager._download_async` đếm bytes — tải theo stream chunk nếu hiện chưa stream; `list()` thêm `progress` optional từng entry) — **KHÔNG sửa `catalog.py`**
- Test: `ReTran.OCR/tests/` (file test của models hiện có)

**Interfaces:**
```python
# manager.list() → entry thêm: "progress": {"receivedBytes": int, "totalBytes": int, "bytesPerSec": float | None}  # chỉ khi đang tải
# ModelManager giữ dict _progress: name → (received, total, bps) — cập nhật trong vòng đọc chunk; xoá khi xong/lỗi.
```
- [x] **Step 1: Test failing** — `test_progress_reported_while_downloading` (nguồn giả stream từng chunk có content-length ⇒ list() có progress, đúng bytes) · `test_progress_cleared_after_download` · `test_progress_absent_when_not_downloading`.
- [x] **Step 2: fail** — `uv run pytest tests -v` (từ `ReTran.OCR/`). *(Trên máy này dùng `uv run python -m pytest …` — trampoline lỗi; xem note T7 trong ledger.)*
- [x] **Step 3: implement** → **Step 4: pass** — full sidecar suite xanh.
- [x] **Step 5: Commit** — `feat(ocr): report model download progress from the sidecar`

---

### Task 8: App parse tiến độ + helper %/ETA

**Files:**
- Modify: `ReTran.App/Core/PipelineController.cs` (parse `progress` của `ocr.models.list` + `llm.models.list`/`llm.runtime.status` vào record tương ứng — thêm property optional `Progress`)
- Create: `ReTran.App/Services/Setup/DownloadProgressText.cs` (helper thuần)
- Test: `ReTran.Tests/Setup/DownloadProgressTextTests.cs` (+ test parse nếu có file test hiện hữu của PipelineController)

**Interfaces:**
```csharp
public static class DownloadProgressText
{
    public static int? Percent(long received, long total);                 // null khi total <= 0
    public static string? Eta(long received, long total, double? bytesPerSec); // null khi thiếu dữ liệu; EMA làm mượt do caller
    public static string Format(long received, long total, double? bytesPerSec); // "43% · còn ~2 phút" | "43%" | "đang kết nối lại…" (i18n keys truyền vào — helper nhận chuỗi đã dịch? KHÔNG: helper trả tuple (int? percent, string? eta) + UI ghép i18n)
}
```
Chốt: helper **không** chứa chuỗi i18n — trả `(int? Percent, TimeSpan? Eta)`; UI (Task 9) ghép `setup.progress.*`.

- [x] **Step 1: Test failing** — `Percent_Normal`, `Percent_UnknownTotal_Null`, `Eta_Normal` (100 MB còn 40 MB @ 1 MB/s → 40 s), `Eta_ZeroOrNegativeSpeed_Null`, `Format_MissingData_NoNaN`.
- [x] **Step 2: fail** → **Step 3: implement** → **Step 4: pass** (+ build).
- [x] **Step 5: Commit** — `feat(app): parse download progress and compute percent and eta`

---

### Task 9: UI — 3 màn mới + i18n + tiến độ ở màn chạy + dọn legacy

**Files:**
- Modify: `ReTran.App/Components/Shared/SetupModal.razor`, `ReTran.App/Services/I18nStrings.cs`
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs` + `OcrModelChoices.cs` (XOÁ legacy: enum 3 member cũ + 2 continue cũ + API cũ đã liệt kê ở Global Constraints)
- Test: build + `I18nServiceTests` (parity tự ghim)

**Hành vi UI (theo mockup đã duyệt `mockup-detrec-langs-llm.html`):**
- `ChoosingOcrModels`: 2 khối, heading `setup.models.det.sect` / `setup.models.rec.sect`; dòng theo `DetModels()`/`RecModels()` (tên cỡ từ `SizeToken` + nhãn `setup.tier.name.<token>`/`setup.tier.desc.<token>` **fallback token** khi thiếu key); tick + khoá + tag "đã cài"; "sẽ tải ~{0}" khi thiếu (dòng tick); chấm `setup.models.use` trên dòng tick mỗi khối (auto-gán dòng tick đầu khi chưa có; bỏ tick dòng giữ ⇒ dời dòng kế); tổng `setup.models.total` (Σ thiếu của ticked); nút khoá tới khi ≥1 mỗi khối + `setup.models.minOne`. Preselect: `PreselectModels(installed, settings.OcrDet/OcrRec, per block)` + `ScanInstalledModels` như 16-11.
- `ChoosingLanguages`: dòng từ `LanguageChoices()` (nhãn native qua `I18nStrings.LanguageName(primaryLang)`, fallback mã); chọn "Bản:" (`setup.langs.variant`) khi nhóm ≥2 variants (hôm nay không có); tick/khoá/tag; chấm `setup.langs.read` như trên; tổng `setup.langs.total`; hint `setup.langs.hint` + note `setup.langs.note`.
- `ChoosingLlmModels`: gọi `Pipeline.ListLlmModelsAsync` (Core sống từ app start — kiểm tra trong task; nếu null/đang tải → "đang nạp…" + thử lại); nhóm động theo `SizeClass` (header `setup.llm.group` fmt `{0}`, **fallback thuần giá trị class**); dòng: `Name` + `Quant` + size; tick/khoá/tag; chấm `setup.llm.use`; tổng `setup.llm.total`; hint `setup.llm.hint` + `setup.llm.skip`.
- Case chạy (bước 8/9): dưới nhãn bước, nếu `Coordinator.Downloads` không rỗng → mỗi dòng `Name — {Percent}% · {ETA}` (i18n `setup.progress.*`), else thanh vô định như cũ. Xoá case `ChoosingLanguage`/`ChoosingOcrTier`/`ChoosingExtraLanguages` + handler cũ.
- i18n (~25 key ×2): `setup.models.*` (title/hint/det.sect/rec.sect/minOne/use/installed/willDownload/total), `setup.langs.*` (title/hint/note/read/variant/total), `setup.llm.*` (title/hint/note/group/use/installed/willDownload/total/skip), `setup.progress.eta/count/retry`, `setup.next` tái dùng.
- [x] **Step 1: implement UI + keys** → **Step 2: build 0 error + `I18nServiceTests` pass + full build** → **Step 3: Commit** — `feat(setup): det/rec, language and LLM screens`

---

### Task 10: Settings — nhóm rõ OCR / Ngôn ngữ / LLM

**Files:**
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor`, `ReTran.App/Services/I18nStrings.cs`
- Test: build + `I18nServiceTests`

- [x] **Step 1: implement** — tiêu đề nhóm rõ: "Model OCR — Model nhận diện khung chữ (det)" / "— Model nhận dạng chữ (rec)" (đã có 2 select — chỉ làm rõ nhóm); bảng model tách nhóm **Ngôn ngữ** (rec legacy) / **OCR v6** / khác; mục LLM: nhóm theo SizeClass động (đồng bộ Task 9). Không đổi chức năng tải/xoá/đặt.
- [x] **Step 2: build + parity** → **Step 3: Commit** — `feat(settings): clear model groups for OCR, language and LLM`

---

### Task 11: Xác minh + board + review + báo cáo

- [x] **Step 1:** build 0 error; focused: `SetupStateNeedsSetupTests`, `OcrModelChoicesTests`, `SetupCoordinatorTests`, `I18nServiceTests`, Core LLM tests, sidecar `uv run python -m pytest tests` (trampoline `uv run pytest` lỗi máy này — xem T7 note).
- [x] **Step 2:** full suite (≥ 907 + số test mới; 0 failed; 2 skip; flaky chạy lại lượt 2). *(Kết quả: 932 = 930 + 2, lượt 1 sạch.)*
- [x] **Step 3:** cập nhật board `visual-compare/index.template.html`: flow 11 bước vòng 2 + 3 figure màn mới (copy từ mockup đã duyệt) + badge; regenerate `index.html`; mở `Start-Process` + preview.
- [x] **Step 4:** final review — reviewer subagent fresh context (model mạnh nhất khả dụng; `ocr_review` chưa cấu hình LLM endpoint — nhắc lại trong báo cáo), range = base trước commit đầu ↔ HEAD, package theo script `review-package`; re-grade + fix pass nếu Critical/Important. *(Kết quả: verdict "Ready to merge: Yes" — 0 Critical; fix wave `b676ade`; re-review scoped sạch.)*
- [x] **Step 5:** ledger + báo cáo cuối (rulings, deferred minors, smoke checklist, menu push).

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** §1–§2 → T3/T9; §3 flow → T3; §4 bước 2 → T2/T9; §5 bước 3 → T2/T9; §6 bước 4 → T3/T4/T9; §7 bước 8 → T5; §8 nguồn dữ liệu → T2/T6; §9 %+ETA → T7/T6/T8/T9; §10 state+migration → T1/T3/T4; §11 Settings → T10; §12 i18n → T9/T10; §13 files → các task; §14 tests → rải theo task; §15/§16 → T11. ✔
2. **Step scan:** mỗi step một hành động; không TBD; code block chỉ ở Interfaces (chữ ký + luật không tự suy được). ✔
3. **Type consistency:** `ModelPreselect`/`LanguageChoice`/`SetupDownloadInfo` khớp giữa T2/T4/T9; `Continue*` khớp handler; marker keys suy từ `StepKey`; `ExpectedPair(activeDet, activeLang, activeRec)` dùng thống nhất T4/reconcile. ✔
4. **Review Focus:** 5 dòng đều có test pin ở task sở hữu (liệt kê ngay sau mỗi dòng). ✔
5. **Proportion:** plan ngắn hơn spec đáng kể; không transcribe thân hàm. ✔
