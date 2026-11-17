# Plan: Màn “Chọn model OCR” + “Ngôn ngữ bổ sung” (setup wizard)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm màn A “Chọn model OCR” (tick nhiều tier PP-OCRv6 ≥1, dòng đã cài tick+khoá, radio “Dùng chính”) trước màn ngôn ngữ, và màn B “Ngôn ngữ bổ sung” (rec legacy ngoài phủ v6, tick nhiều/không) sau màn ngôn ngữ; tải/áp dụng theo lựa chọn; rerun tự reconcile.

**Architecture:** Helper thuần mới `OcrModelChoices` (mirror catalog + luật chọn, testable) dùng chung cho modal + coordinator; `SetupCoordinator` thêm 2 pre-pipeline pause (như ChoosingLanguage) + reconcile marker bằng filesystem; DownloadingModels/Applying đọc answers từ `setup.json`; KHÔNG sửa sidecar/Core.

**Tech Stack:** .NET 10 (App), xUnit v3, Blazor. Không dependency mới.

**Spec:** `Docs/superpowers/specs/2026-11-16-ocr-tier-and-extra-langs-design.md` (đã duyệt 2026-11-16; mockup `mockup-model-extra-langs.html` đã duyệt — gồm A4/B3 “đã cài → tick + khoá”).

## Global Constraints

- Không `git add -A`; commit đúng file mỗi task; `master`; không push. Git: `& "C:\Program Files\Git\cmd\git.exe" -C "D:\Code\ReTran Project" ...`.
- Build `dotnet build "ReTran Project.slnx"` → 0 error. Filter test cần FQN (`SystemCheckTests`… global; Core cần `RetranCore.Tests.`). **Baseline sau plan checklist: full suite 872 = 870 pass + 2 skip** (2 skip do ComfyUI giữ VRAM).
- Doc comment C# song ngữ; i18n vi/en parity (`I18nServiceTests` ghim).
- **Không đụng:** `ReTran.OCR/src/**` (sidecar/catalog.py), `ReTran.Core/**`, `SystemCheck`, Settings panel, màn Checking.
- Model trên đĩa: `<SetupPaths.ModelsDir>/official_models/<tên-model>/` (tên dir = tên model — đã đo 2026-11-16).
- Luật chọn (spec §4/§5 + feedback user 16-11): dòng **đã cài đủ** → tick + **khoá** (không bỏ tick); bán phần → tick nhưng tự do; máy sạch → chỉ Small tự do; A **ít nhất 1**; B không ràng buộc; radio “Dùng chính” chỉ trên dòng đã tick.

## Review Focus

1. **Máy sạch**: pause đúng thứ tự A → Language → B rồi mới pipeline; A chặn khi 0 tick. Test: `Start_NoAnswers_PausesAtOcrTier`, `ContinueTier_ZeroTiers_Throws`, `Flow_Order_TierThenLanguageThenExtra`.
2. **Máy đã setup (answers đủ)**: không pause màn nào + không tải lại + cặp không đổi (pass-through). Test: `Flow_AllAnswered_SkipsPauses` + `Reconcile_AllPresent_NoClears`.
3. **Đổi lựa chọn ở lần rerun đầu** (máy cũ chưa có answers): tick xong phải kịp clear marker tải/apply để TẢI BÙ — reconcile sau A/B. Test: `ContinueExtra_NewChoices_ClearsMarkers_AndDownloads`.
4. **Reconcile chỉ đúng khi lệch**: model thiếu trên đĩa → clear `downloadingmodels`; cặp lệch settings → clear `applying`; khớp → không clear. Tests: `Reconcile_MissingModel_ClearsDownload`, `Reconcile_PairMismatch_ClearsApplying`.
5. **Cặp đúng theo tier + ngoài phủ**: main=auto → `(tier_det, tier_rec)`; main=ko → `(tier_det, korean_rec)`; extra lang chỉ tải, không đổi cặp. Tests: `Applying_TierAwarePair`, `DownloadSet_TiersLegacyMainExtras`.

---

### Task 1: `OcrModelChoices` (helper thuần) + tên ta/te

**Files:**
- Create: `ReTran.App/Services/Setup/OcrModelChoices.cs`
- Modify: `ReTran.App/Services/I18nStrings.cs` (`LanguageNames`: +`["ta"]="தமிழ்"`, +`["te"]="తెలుగు"`)
- Test: `ReTran.Tests/Setup/OcrModelChoicesTests.cs` (new), `ReTran.Tests/I18nServiceTests.cs` (+1 test)

**Interfaces:**
```csharp
namespace ReTran_App.Services.Setup;

/// <summary>...song ngữ...</summary>
public sealed record TierPreselect(IReadOnlyList<string> Ticked, IReadOnlyList<string> Locked, string ActiveTier);

public static class OcrModelChoices
{
    public static IReadOnlyList<string> Tiers { get; }            // ["tiny","small","medium"]
    public static string TierDet(string tier);                    // PP-OCRv6_{tier}_det
    public static string TierRec(string tier);                    // PP-OCRv6_{tier}_rec
    public static string? TierOfDet(string? detModel);            // "PP-OCRv6_medium_det" -> "medium"
    public static IReadOnlyList<string> LegacyLanguageCodes { get; } // ["ko","ru","ar","th","ta","te","hi","el"]
    public static string? LegacyRecFor(string lang);              // routing mirror: rec legacy đầu tiên phủ lang; null nếu không
    public static bool IsV6Covered(string langOrAuto);            // "auto" hoặc thuộc langs của DefaultRec (mirror), so khớp không phân biệt hoa thường
    public static float SizeMbOf(string modelName);               // mirror size, 0 nếu lạ
    public static TierPreselect PreselectTiers(IReadOnlyCollection<string> installedModels, string? currentDet, string? currentRec);
    public static IReadOnlyList<string> PreselectLegacyLanguages(IReadOnlyCollection<string> installedModels);
    public static IReadOnlyList<string> NeededModels(IReadOnlyList<string> tiers, string activeTier, string mainLang, IReadOnlyList<string> extraLangs);
    public static (string Det, string Rec) ExpectedPair(string activeTier, string mainLang);
    public static HashSet<string> ScanInstalledModels(string modelsDir);  // đọc FS: dir con của official_models/
}
```
Luật chọn (pin bằng test): full(t)=det∧rec có → tick+khoá; bán phần → tick, không khoá; không tick nào → ticked `["small"]` tự do; `ActiveTier` = tier của `currentDet` nếu ∈ ticked, else `"small"` nếu ticked, else tier đầu theo thứ tự. `NeededModels` = distinct: mọi `TierDet/TierRec` của `tiers` + `LegacyRecFor(mainLang)` khi `!IsV6Covered` + `LegacyRecFor(e)` cho mỗi extra. `ExpectedPair` = `(TierDet(activeTier), IsV6Covered(main) ? TierRec(activeTier) : LegacyRecFor(main))`.

- [x] **Step 1: Viết test failing** — `OcrModelChoicesTests`:
  `TierModelNames_ComposeCorrectly` (3 tier × det/rec; `TierOfDet` cả 3 + chuỗi lạ → null) · `LegacyRecFor_MapsTheEightLanguages` (ko→`korean_PP-OCRv5_mobile_rec`, ru→`cyrillic_…`, ar→`arabic_…`, th→`th_…`, ta→`ta_…`, te→`te_…`, hi→`devanagari_…`, el→`el_…`; `"fr"`/`"xx"` → null) · `IsV6Covered_AutoCoveredTrue_OthersFalse` (auto/vi/en/ja/zh true; ko/ru/th/el/hi/ta/te false) · `PreselectTiers_Full_ TicksAndLocks` (installed small đủ, current small/small → Ticked [small], Locked [small], Active small) · `PreselectTiers_Partial_TicksUnlocked` (chỉ `small_det`) · `PreselectTiers_Clean_DefaultsSmallFree` · `PreselectTiers_ActiveFromLegacyPair` (current det=`PP-OCRv6_tiny_det`, rec=`korean_…` → Active tiny khi tiny ticked) · `PreselectLegacyLanguages_InstalledOnly` · `NeededModels_TiersPlusLegacyPlusExtras` (tiers [small,medium], main ko, extras [ru] → 6 tên đúng, không trùng) · `NeededModels_AutoNoExtras_TierModelsOnly` · `ExpectedPair_Auto_V6Pair` + `ExpectedPair_Korean_TierDetPlusKorRec` · `ScanInstalledModels_ReadsTempDirs` (temp dir + 2 dir con + 1 file → 2 tên). `I18nServiceTests`: `LanguageNames_HaveNativeTamilTelugu` (`LanguageName("ta")=="தமிழ்"`, `"te"=="తెలుగు"`).
- [x] **Step 2: Chạy — fail** (CS0246). `--filter-class "OcrModelChoicesTests"` (global).
- [x] **Step 3: Implement** — helper mirror từ `OcrCatalogDisplay` (rec legacy = rec có tên KHÔNG bắt đầu `"PP-OCRv6"` đầu tiên phủ lang — cùng xấp xỉ `PairFor` cũ); `ScanInstalledModels` = `Directory.EnumerateDirectories(Path.Combine(modelsDir, SetupState.OfficialModelsSubdir))` lấy `Path.GetFileName`, thiếu dir → empty, lỗi → empty. Thêm 2 tên ta/te vào `LanguageNames`.
- [x] **Step 4: Pass** — `OcrModelChoicesTests` + `I18nServiceTests` xanh.
- [x] **Step 5: Commit** — `feat(setup): model-tier choice rules for the wizard`

---

### Task 2: `SetupState` + flow A/B (pause/continue) + fixture tests

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupState.cs` (3 field mới cuối record + docs)
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs` (enum + `StartAsync`/`ContinueWithLanguageAsync` + 2 continue mới + property `ActiveTier`)
- Test: `ReTran.Tests/Setup/SetupStateTests.cs` (+roundtrip), `ReTran.Tests/Setup/SetupCoordinatorTests.cs` (fixtures + ~5 test mới)

**Interfaces:**
```csharp
// SetupState: positional cuối, mặc định null:
//   List<string>? Tiers = null, string? ActiveTier = null, List<string>? ExtraLangs = null
// SetupCoordinator — thêm:
public enum SetupStep { ..., ChoosingOcrTier, ChoosingExtraLanguages, ... }   // chèn SAU VerifyingSidecar? KHÔNG — enum chỉ cần thêm 2 giá trị (vị trí trong enum không quan trọng; KHÔNG vào PipelineSteps)
public string? ActiveTier => _state.ActiveTier;                                // modal dùng cho màn ngôn ngữ
public Task ContinueWithOcrTierAsync(IReadOnlyList<string> tiers, string activeTier, CancellationToken ct = default);
public Task ContinueWithExtraLanguagesAsync(IReadOnlyList<string> extraLangs, CancellationToken ct = default);
```
Flow: `StartAsync` (sau Checking thành công): `Tiers is null` → `Step=ChoosingOcrTier; Publish(); return;` → `Language is null` → `ChoosingLanguage` (như cũ) → `ExtraLangs is null` → `ChoosingExtraLanguages` → else `RunPipelineAsync(VerifyingFiles)`. `ContinueWithOcrTierAsync`: validate (`tiers.Count ≥ 1`, subset `OcrModelChoices.Tiers`, `activeTier ∈ tiers`; sai → `ArgumentException`), lưu `Tiers`/`ActiveTier` + marker `StepKey(ChoosingOcrTier)`, save; rồi tiếp: Language null → pause Language; ExtraLangs null → pause B; else pipeline. `ContinueWithLanguageAsync`: thêm nhánh ExtraLangs null → pause B (trước khi vào pipeline). `ContinueWithExtraLanguagesAsync`: validate mọi phần tử ∈ `LegacyLanguageCodes` (sai → `ArgumentException`), dedupe giữ thứ tự, lưu + marker, → pipeline. Marker keys tự sinh `StepKey` = lowercase tên enum (`choosingocrtier`, `choosingextralanguages`).

- [x] **Step 1: Test failing** — `SetupStateTests`: `TiersActiveExtra_RoundTrip` + `LegacyJson_NullNewFields` + `ExtraLangs_EmptyList_DistinctFromNull`. `SetupCoordinatorTests` test mới: `Start_NoAnswers_PausesAtOcrTier` (fixture chỉ flavor+language → Step=ChoosingOcrTier sau StartAsync), `Flow_Order_TierThenLanguageThenExtra` (fixture flavor-only: Start→A; ContinueA→Language; ContinueLanguage→B; ContinueB→Done, `_bridge.Downloaded` chứa đủ bộ), `ContinueTier_ZeroTiers_Throws` + `ContinueTier_BadActiveTier_Throws` + `ContinueExtra_UnknownLang_Throws`, `Flow_AllAnswered_SkipsPauses` (fixture đủ 3 answers → StartAsync → Done không pause, không download mới), `ActiveTier_Exposed`.
- [x] **Step 2: Chạy — fail**.
- [x] **Step 3: Implement** + **cập nhật fixtures cũ**: mọi fixture `WriteSetupJson` KHÔNG phải case `isComplete:true`/“file cũ” chủ đích → thêm `"tiers":["small"],"activeTier":"small","extraLangs":[]` (giữ nguyên ý nghĩa từng test; các test cũ resume/pipeline phải đi thẳng như trước).
- [x] **Step 4: Pass** — `SetupStateTests` + `SetupCoordinatorTests` xanh (toàn bộ, kể cả test cũ).
- [x] **Step 5: Commit** — `feat(setup): tier and extra-language pauses in the wizard flow`

---

### Task 3: Tải theo lựa chọn + Applying + reconcile

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs` (`RunDownloadModelsStepAsync`, `RunApplyingStepAsync`, `ReconcileMarkers`, ctor seam `modelDirExists`, gọi reconcile ở cuối `RunCheckingStep` + sau lưu answers ở 2 continue)
- Test: `ReTran.Tests/Setup/SetupCoordinatorTests.cs` (~7 test)

**Interfaces:**
```csharp
// ctor: thêm Func<string, bool>? modelDirExists = null  (sau countModelEntries; default Directory.Exists)
// nội bộ:
private bool ModelInstalled(string modelName)   // _modelDirExists(Path.Combine(SetupPaths.ModelsDir, SetupState.OfficialModelsSubdir, name))
private void ReconcileMarkers()                 // answers đủ mới chạy; clear marker khi lệch; save khi có thay đổi
// Fallback khi state thiếu (defensive): tiers=["small"], activeTier="small", extraLangs=[]
```
`RunDownloadModelsStepAsync`: needed = `OcrModelChoices.NeededModels(tiers, activeTier, LanguageCode, extras)`; list qua bridge → `download` mọi tên chưa installed; **poll tới khi MỌI tên needed installed** (retry/cửa sổ như cũ, dùng `_modelPollInterval`/`_modelDownloadTimeout`). Bỏ hẳn lượt gọi `recommend`.
`RunApplyingStepAsync`: `(det, rec) = OcrModelChoices.ExpectedPair(activeTier, LanguageCode)`; `_settings.Save(... OcrDet=det, OcrRec=rec)` (bỏ recommend).
`ReconcileMarkers`: nếu `Tiers is null || ExtraLangs is null` → return; needed thiếu trên đĩa (qua `ModelInstalled`) → xoá marker `downloadingmodels`; `ExpectedPair ≠ settings.OcrDet/OcrRec` → xoá marker `applying`; có xoá → save.
- [x] **Step 1: Test failing** — `DownloadSet_TiersLegacyMainExtras` (tick small+medium, main ko, extra ru → `_bridge.Downloaded` = đúng 6 tên, đúng thứ tự needed) · `DownloadSet_SkipsInstalled` (modelDirExists trả true cho small_det/rec → không download 2 tên đó) · `Applying_TierAwarePair` (auto+tick medium → settings medium/medium; main ko + activeTier tiny → `tiny_det` + `korean_…`) · `Reconcile_MissingModel_ClearsDownload` (fixture đủ answers + steps done + `downloadingmodels:done`; modelDirExists false cho `medium_rec` khi tiers chứa medium → StartAsync → download được gọi cho medium_rec + step chạy lại → Done; các step khác vẫn skip) · `Reconcile_PairMismatch_ClearsApplying` (settings pair tiny; answers activeTier medium → applying chạy lại → settings = medium pair) · `Reconcile_AllPresent_NoClears` (`_bridge.Downloaded` rỗng + Done, mọi step skip) · `ContinueExtra_NewChoices_ClearsMarkers_AndDownloads` (fixture đủ answers cũ + done; `ContinueWithExtraLanguagesAsync(["ko"])` → korean rec được download + settings không đổi; biến thể: answers mới ở A cũng clear được).
- [x] **Step 2: fail** → **Step 3: implement** → **Step 4: pass** (`SetupCoordinatorTests` toàn bộ).
- [x] **Step 5: Commit** — `feat(setup): tier-aware downloads, applying and rerun reconcile`

---

### Task 4: UI 2 màn + màn ngôn ngữ theo tier + i18n

**Files:**
- Modify: `ReTran.App/Components/Shared/SetupModal.razor` (2 case mới + state/init/handlers + CSS scoped + sửa case ChoosingLanguage dùng tier)
- Modify: `ReTran.App/Services/I18nStrings.cs` (~18 key ×2)
- Test: không unit (pattern); build + `I18nServiceTests`

**Interfaces / hành vi UI (theo mockup đã duyệt):**
- Case `ChoosingOcrTier`: title `setup.tier.title` + hint “(ít nhất 1)”; 3 dòng từ `OcrModelChoices.Tiers`: checkbox (checked+disabled nếu locked, tag `setup.tier.installed`), tên `setup.tier.name.<tier>` + mô tả `setup.tier.desc.<tier>`, dòng mono `TierDet(tier) · TierRec(tier) · {size} MB`, status `setup.tier.willDownload` (“sẽ tải ~{0}”) khi thiếu phần nào; radio “Dùng chính” (`setup.tier.use`) **chỉ trên dòng ticked**; tổng `setup.tier.total` (Σ phần thiếu, “0 MB” khi đủ); nút `setup.next` disabled khi 0 ticked + dòng `setup.tier.minOne`. State khởi tạo 1 lần khi vào case: `OcrModelChoices.ScanInstalledModels(SetupPaths.ModelsDir)` + `SettingsStore.Load()` → `PreselectTiers`; cách tính phần thiếu: đối chiếu installed set từng model trong dòng. Handler: `ContinueWithOcrTierAsync(_tierTicks, _activeTierPick)`.
- Case `ChoosingExtraLanguages`: title `setup.extra.title` + hint `setup.extra.hint`; dòng `setup.extra.note`; 8 dòng từ `LegacyLanguageCodes` (check+disabled nếu rec installed + tag; name = `I18nStrings.LanguageName`; mono rec + size); tổng `setup.extra.total`; nút `setup.next`. Handler: `ContinueWithExtraLanguagesAsync(_extraTicks)`.
- Case `ChoosingLanguage`: `PairFor(code)` dùng `Coordinator.ActiveTier ?? "small"` → det `TierDet`, rec `IsV6Covered ? TierRec : LegacyRecFor` (thay `DefaultDet/DefaultRec`); `OptionMeta`/`SelectedTotalMb`/`Covers/SizeMb` chuyển sang `OcrModelChoices`.
- CSS scoped: thêm `.setup-choices` (như mockup: grid 18px/1fr/auto, checkbox accent, `.det` mono, `.use` radio, `.tag-installed`, disabled opacity) — **không rebuild Vite**.
- i18n (VI / EN) — thêm trong block setup: `setup.tier.title` “Chọn model OCR”/“Choose OCR models” · `setup.tier.hint` “(ít nhất 1)”/“(at least 1)” · `setup.tier.name.tiny|small|medium` “Tiny/Small/Medium” · `setup.tier.desc.tiny` “nhẹ nhất, tải nhanh nhất”/“lightest, fastest to download” · `setup.tier.desc.small` “khuyến nghị”/“recommended” · `setup.tier.desc.medium` “chính xác nhất, tải lâu nhất”/“most accurate, longest download” · `setup.tier.use` “Dùng chính”/“Use as primary” · `setup.tier.installed` “đã cài”/“installed” · `setup.tier.willDownload` “sẽ tải ~{0}”/“will download ~{0}” · `setup.tier.total` “Tổng tải: {0}”/“Total download: {0}” · `setup.tier.minOne` “Chọn ít nhất 1 bản để tiếp tục.”/“Pick at least one to continue.” · `setup.extra.title` “Ngôn ngữ bổ sung”/“Extra languages” · `setup.extra.hint` “(ngoài phủ PP-OCRv6 — có thể để trống)”/“(outside PP-OCRv6 coverage — optional)” · `setup.extra.note` “Tick để tải sẵn model nhận chữ riêng; cặp đang chạy vẫn theo ngôn ngữ chính — đổi dùng sau ở Cài đặt.”/“Tick to pre-download dedicated recognition models; the running pair still follows the main language — switch later in Settings.” · `setup.extra.total` “Tải thêm: {0}”/“Extra download: {0}” · `setup.next` “Tiếp tục”/“Continue”.
- [x] **Step 1: Implement UI + keys** → **Step 2: build 0 error + `I18nServiceTests` pass** → **Step 3: Commit** — `feat(setup): tier and extra-language screens`

---

### Task 5: Xác minh + board + review + báo cáo

- [x] **Step 1:** build + focused (`OcrModelChoicesTests`, `SetupStateTests`, `SetupCoordinatorTests`, `I18nServiceTests`).
- [x] **Step 2:** full suite ≥ 872 + số test mới (0 failed; 2 skip VRAM đã biết; flaky-under-load có thể gặp — chạy lại lượt 2).
- [x] **Step 3:** Regenerate board `visual-compare/index.template.html`: thêm 2 figure màn mới (copy từ `mockup-model-extra-langs.html`), cập nhật flow strip + badge; regenerate `index.html`; mở bằng `Start-Process` (browser tool của session hiện không khả dụng).
- [x] **Step 4:** Final review: thử `ocr_review` (tool OpenCodeReview mới: `from`/`to` + backgroundFile spec) — nếu tool lỗi/không dùng được → reviewer subagent như các plan trước (code-reviewer template, model mạnh nhất khả dụng). Re-grade + fix pass nếu Critical/Important. *(Kết quả: `ocr_review` chưa cấu hình LLM endpoint → dùng reviewer subagent; verdict Yes, 0C/0I/1M.)*
- [x] **Step 5:** Ledger + báo cáo cuối (rulings, deferred minors, smoke checklist, menu push).

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** A (§4) → T1/T2/T4; B (§5) → T1/T2/T4; màn ngôn ngữ theo tier (§6) → T4; tải/áp dụng (§7) → T3; reconcile (§8) → T3; state/resume (§9) → T2; i18n/tên (§10) → T1/T4; files (§11) → T1–T4. ✔
2. **Step scan:** mỗi step một hành động; không “TBD”; code block chỉ nơi signature+luật không tự suy được (interface T1, keys T4). ✔
3. **Type consistency:** `TierPreselect`/`OcrModelChoices` (T1) dùng ở T3/T4; `ContinueWithOcrTierAsync(tiers, activeTier)` (T2) khớp handler T4; `ActiveTier` (T2) khớp T4; marker keys suy từ enum (T2) khớp reconcile (T3). ✔
4. **Review Focus:** 5 dòng đều có test pin ở T2/T3/T1. ✔
5. **Proportion:** plan ngắn hơn spec; không transcribe thân hàm (luật chọn mô tả bằng lời + test). ✔
