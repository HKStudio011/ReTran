# Plan: LLM translation provider (GGUF local + API OpenAI-compatible)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provider `llm` (2 mode: GGUF local qua `llama-server`; API OpenAI-compatible), quản lý llama.cpp + model GGUF kiểu OCR, prompt system/user sửa được với mặc định Hy-MT2.

**Architecture:** Core module `ReTran.Core/Llm/` (prompt, client, translator, 2 manager, server host) + RPC `llm.*` + `core.start.translate.llm`; App: settings mới + section LLM trong SettingsPanel + PipelineController methods; prompt mặc định đồng bộ 2 phía bằng sync test.

**Tech Stack:** .NET 10, System.Net.Http, System.IO.Compression, xUnit v3 — không dependency mới.

**Spec:** `Docs/superpowers/specs/2026-11-14-llm-translation-provider-design.md` (user "làm luôn, review một lần" 2026-11-14). Số liệu prompt/quant/llama.cpp đã kiểm chứng ghi ở spec §0.

## Global Constraints

- **Không `git add -A`**; commit đúng file mỗi task; không push; branch `master`.
- Build: `dotnet build "ReTran Project.slnx"` → 0 error (34 warning `xUnit1051` có sẵn).
- Test: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<FQN>"` (FQN bắt buộc). **Baseline trước plan: 789 passed / 0 failed.**
- **Không network trong test** — mọi HTTP qua `HttpMessageHandler` fake; downloader/extractor inject.
- Đường Google/DeepL giữ nguyên; `ITranslator.TranslateBatchAsync` giữ default per-text (v1); wire `translate.request_cache/put` không đổi.
- Doc comment C# song ngữ; chuỗi UI qua i18n vi/en.
- Prompt mặc định phải khớp **nguyên văn** chuỗi trong spec §0 (test ghim).

## Review Focus

1. **Số phận khi server local lỗi** (chưa tải model/runtime, spawn fail, load OOM): `core.start` báo lỗi rõ (`-32603` + code `llm-model-missing`/`llm-runtime-missing`), UI hiển thị; chu kỳ dịch không crash — text giữ nguyên. → Task 6 tests + Task 7 UI text.
2. **Prompt thiếu `{source_text}`** → lỗi rõ ràng (`FormatException`), không gửi nguyên text làm prompt rỗng nghĩa. → Task 1 `Apply_MissingSourceText_Throws`.
3. **Params gửi API remote không chứa field llama-only** (`top_k`/`repeat_penalty` chỉ mode local). → Task 2 `Complete_Remote_OmitsLlamaOnlyParams`.
4. **Xoá model đang active bị chặn**; `.partial` hiển thị là "đang tải" không phải "đã cài". → Task 3 tests.
5. **Runtime check chọn đúng asset mới nhất** (bỏ qua release thiếu asset win-cuda; không chọn nhầm `-bin-win-cpu`). → Task 4 `Check_PicksNewestReleaseWithCudaAsset`.
6. **Port local không đụng process khác** (lấy port trống thật, bind-thử trước khi spawn). → Task 5 `PickFreePort_IsBindable_AndNotReusedWhileHeld`.

---

### Task 1: `PromptTemplate` + `LanguageNames`

**Files:** Create `ReTran.Core/Llm/PromptTemplate.cs`, `ReTran.Core/Llm/LanguageNames.cs`, `ReTran.Tests/PromptTemplateTests.cs` (namespace `RetranCore.Tests`).

**Interfaces:**
```csharp
public static class PromptTemplate {
    public const string DefaultSystemPrompt = "";
    public const string DefaultUserPrompt = "Translate the following text into {target_lang}. Note that you should only output the translated result without any additional explanation:\n\n{source_text}";
    public static string Apply(string template, string src, string dst, string text); // thay {source_text}/{source_lang}/{target_lang}; thiếu {source_text} → FormatException
}
public static class LanguageNames { public static string For(string code); } // vi→Vietnamese, ja→Japanese, auto→auto, lạ→nguyên mã
```
- [ ] Steps: viết test failing (`Apply_FillsAllPlaceholders` vi→Vietnamese; `Apply_MissingSourceText_Throws`; `Defaults_MatchHyMt2Card` assert 2 const; `LanguageNames_KnownAndFallback`) → run fail → implement (~40 dòng mỗi file, map ~40 mã EN từ spec §0 + phổ biến) → pass + full suite ≥789 → commit `feat(core): prompt template and language names for the LLM provider`.

### Task 2: `LlmSettings` + `OpenAiChatClient` + `LlmTranslator`

**Files:** Create `ReTran.Core/Llm/LlmSettings.cs`, `Llm/OpenAiChatClient.cs`, `Llm/LlmTranslator.cs`, `ReTran.Tests/OpenAiChatClientTests.cs`, `ReTran.Tests/LlmTranslatorTests.cs`.

**Interfaces:**
```csharp
public sealed record LlmSettings(string Mode, string? BaseUrl, string? ApiKey, string? Model, string? SystemPrompt, string? UserPrompt) {
    public bool IsLocal => string.Equals(Mode, "local", StringComparison.OrdinalIgnoreCase);
    public string EffectiveSystem => SystemPrompt ?? PromptTemplate.DefaultSystemPrompt;
    public string EffectiveUser   => UserPrompt ?? PromptTemplate.DefaultUserPrompt;
}
public sealed class OpenAiChatClient(HttpClient client) {
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    public Task<string> CompleteAsync(string baseUrl, string? apiKey, string model, string systemPrompt, string userPrompt, bool local, CancellationToken ct);
}
public sealed class LlmTranslator(OpenAiChatClient client, LlmSettings settings, Func<Task<string>>? ensureEndpoint = null) : ITranslator {
    public string Name => "llm";
    public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct = default);
}
```
`CompleteAsync`: POST `{baseUrl}` + `/v1/chat/completions`; `Authorization: Bearer {apiKey}` khi có; body `{model, messages:[system? , user], temperature:0.7, top_p:0.6, max_tokens:4096}` + (`top_k:20, repeat_penalty:1.05` khi `local`); 120 s timeout→`TimeoutException`; non-200→`HttpRequestException` (kèm status + body ngắn); `choices[0].message.content` string → `Trim()`; shape sai→`FormatException`. `TranslateAsync`: `ensureEndpoint` (dùng cho lazy-start, gọi await trước nếu có) → `CompleteAsync(settings.BaseUrl!, apiKey, settings.Model!, EffectiveSystem, PromptTemplate.Apply(EffectiveUser, src, dst, text), settings.IsLocal, ct)`.
- [ ] Steps: tests (fake handler): `Complete_FillsMessages`; `Complete_Remote_OmitsLlamaOnlyParams`; `Complete_Local_AddsLlamaOnlyParams`; `Complete_Non200_Throws`; `Complete_BadShape_Throws`; `Translator_AppliesPromptAndTrims`; `Translator_EnsureEndpoint_CalledOncePerCall` → fail → implement → pass + suite → commit `feat(core): OpenAI-compatible chat client and LLM translator`.

### Task 3: `LlmModelCatalog` + `LlmModelManager`

**Files:** Create `ReTran.Core/Llm/LlmModelCatalog.cs`, `Llm/LlmModelManager.cs`, `ReTran.Tests/LlmModelManagerTests.cs`.

**Interfaces:**
```csharp
public sealed record LlmModelSpec(string Name, string Repo, string File, long SizeBytes, string Quant, string SizeClass);
// Catalog 12: {1.8B,7B} × {UD-Q4_K_XL (default), UD-Q3_K_XL, Q4_K_M, Q5_K_M, Q6_K, Q8_0} — size theo spec §0;
// Name vd "Hy-MT2-1.8B-UD-Q4_K_XL"; Repo "unsloth/Hy-MT2-1.8B-GGUF"; File "<Name>.gguf".
public sealed record LlmModelInfo(string Name, string Quant, string SizeClass, long SizeBytes, bool Installed, bool InUse);
public sealed class LlmModelManager(string modelsDir, string stateFile, Func<LlmModelSpec, string, CancellationToken, Task>? downloader = null) {
    public IReadOnlyList<LlmModelInfo> List();
    public bool StartDownload(string name);           // false khi đang tải; chạy thread nền, marker .partial, xoá marker khi xong
    public void Delete(string name);                  // RpcInvalidParams khi active/đang tải
    public void SetActive(string name);               // ghi models.json {"active": name}
    public string? ActiveModel { get; }
    public string? ActiveModelPath { get; }           // null khi chưa cài
    public string ModelPath(string name);
}
```
- [ ] Steps: tests (temp dir + fake downloader): `List_DerivesInstalledFromFilesystem`; `List_PartialMarker_NotInstalled`; `StartDownload_WritesFile_ThenInstalled`; `Delete_RefusesActive`; `Delete_RefusesDownloading`; `SetActive_RoundTrips`; `Catalog_HasTwelveSpecs_WithExpectedSizes` (spot-check 1.17GB/4.78GB) → fail → implement → pass → commit `feat(core): GGUF model catalog and manager for the LLM provider`.

### Task 4: `LlamaRuntimeManager`

**Files:** Create `ReTran.Core/Llm/LlamaRuntimeManager.cs`, `ReTran.Tests/LlamaRuntimeManagerTests.cs`.

**Interfaces:**
```csharp
public sealed record LlmRuntimeStatus(string? Tag, bool Installed, string? ServerPath);
public sealed class LlamaRuntimeManager(string rootDir, HttpClient github, Func<Uri, string, CancellationToken, Task>? downloader = null) {
    public static readonly string ReleasesUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30";
    public Task<(string? Current, string? Latest)> CheckAsync(CancellationToken ct);   // scan releases: first release có asset ^llama-b\d+-bin-win-cuda-x64\.zip$ → Latest; Current đọc runtime.json
    public Task<LlmRuntimeStatus> GetStatusAsync(CancellationToken ct);
    public Task DownloadAsync(string flavor, CancellationToken ct);  // "cuda" (mặc định) | "cpu" (avx2); tải zip (+ cudart zip nếu release có `${cudart}...`), giải nén vào rootDir/<tag>/, ghi runtime.json {"tag": ...}
}
```
- [ ] Steps: tests (fake handler với JSON 2 release — release mới thiếu asset, release cũ có): `Check_PicksNewestReleaseWithCudaAsset`; `Check_NoAsset_ReturnsNullLatest`; `GetStatus_BeforeDownload_NotInstalled`; `Download_ExtractsZip_WritesRuntimeJson` (zip fake bằng `ZipArchive` tạo 1 file `llama-server.exe`); `Download_Cpu_UsesAvx2Asset` → fail → implement → pass → commit `feat(core): llama.cpp runtime manager with check and download`.

### Task 5: `LlamaServerHost`

**Files:** Create `ReTran.Core/Llm/LlamaServerHost.cs`, `ReTran.Tests/LlamaServerHostTests.cs`.

**Interfaces:**
```csharp
public sealed class LlamaServerHost(string serverExe, string modelPath, int contextSize = 4096, int gpuLayers = 99) : IDisposable {
    public static string BuildArguments(string modelPath, int port, int contextSize, int gpuLayers); // testable — "-m \"...\" --host 127.0.0.1 --port N -c 4096 -ngl 99 --jinja"
    public static int PickFreePort();
    public int Port { get; }           // 0 trước khi Start
    public string BaseUrl => ...;      // "http://127.0.0.1:{Port}"
    public Task EnsureStartedAsync(CancellationToken ct);  // idempotent; spawn + poll GET /health tới 120 s; dùng Process.Start; lỗi → InvalidOperationException kèm stderr
    public void Dispose();             // kill tree
}
```
Phần test được: `BuildArguments` (có flag, có model path quoted); `PickFreePort` (trả port bind được + 2 lần liên tiếp cùng giữ listener khác nhau — gọi trước khi đóng); helper `WaitForHealthAsync(probe, timeout)` tách static để test với probe fake (true ngay / false mãi → timeout nhanh với timeout nhỏ). Spawn thật → smoke user.
- [ ] Steps: tests → implement → pass → commit `feat(core): llama-server host with deterministic args and health gating`.

### Task 6: Wire `translate.llm` + RPC `llm.*` (Core)

**Files:** Modify `ReTran.Core/Serve/PipelineHost.cs` (ParseStart + StartSpec), `ReTran.Core/Program.cs` (serve wiring: tạo 2 manager theo `%LOCALAPPDATA%\ReTran\llm\...`, register handlers, truyền `llmFactory`), `ReTran.Core/Llm/LlmRpcHandlers.cs` (new), tests: mở rộng `ReTran.Tests/PipelineHostTests.cs` + new `ReTran.Tests/LlmRpcHandlersTests.cs`.

**Interfaces:**
```csharp
// PipelineHost.ParseStart: provider "llm" → object translate.llm bắt buộc {mode}; local → model đã cài (khác → RpcMethodException -32603 code "llm-model-missing") + runtime có server (khác → "llm-runtime-missing");
// api → baseUrl bắt buộc (-32602). LlmSettings build; translator = llmFactory(settings, endpointProvider) — local: endpoint = lazy EnsureStarted của LlamaServerHost; translator bọc ensureEndpoint.
// StartSpec giữ translator thêm host để dispose khi phiên dừng (host chỉ tạo khi local).
// LlmRpcHandlers: MakeLlmHandlers(modelManager, runtimeManager) → "llm.models.list|download|delete|set", "llm.runtime.status|check|download" (params: {"flavor":"cuda"|"cpu"} tuỳ chọn).
```
- [ ] Steps: tests: `Start_LlmMissingMode_InvalidParams`; `Start_LlmUnknownMode_InvalidParams`; `Start_LlmApiWithoutBaseUrl_InvalidParams`; `Start_LlmLocalModelNotInstalled_ModelMissingCode`; `Start_LlmLocal_RunsWithFactoryTranslator` (fake factory); `LlmRpc_ModelsDownload_Accepted`; `LlmRpc_RuntimeDownload_BadFlavor_InvalidParams` → fail → implement → pass + full suite → commit `feat(core): wire the llm provider through core.start and llm.* RPC`.

### Task 7: App — settings + SettingsPanel LLM section + PipelineController

**Files:** Modify `ReTran.App/Services/AppSettingsService.cs`, `ReTran.App/Core/PipelineController.cs`, `ReTran.App/Components/Shared/SettingsPanel.razor`, `ReTran.App/Services/I18nStrings.cs`; Create `ReTran.Tests/PromptDefaultsSyncTests.cs`; mở rộng `ReTran.Tests/AppSettingsServiceTests.cs`.

**Interfaces:**
```csharp
// AppSettings + 7 tham số (spec D10): string LlmMode="local"; string LlmBaseUrl=""; string? LlmApiKey=null; string LlmApiModel="Hy-MT2-1.8B"; string LlmActiveModel="Hy-MT2-1.8B-UD-Q4_K_XL"; string? LlmSystemPrompt=null; string? LlmUserPrompt=null.
// PipelineController: StartAsync thêm ["llm"] trong ["translate"] khi Provider=="llm" (mode/baseUrl/apiKey/model/systemPrompt/userPrompt — clone node); + methods RPC: ListLlmModelsAsync() → IReadOnlyList<LlmModelInfo>; Download/Delete/SetLlmModelAsync; GetLlmRuntimeStatusAsync() → LlmRuntimeStatusInfo; CheckLlmRuntimeAsync(); DownloadLlmRuntimeAsync(flavor) — DTO records bản sao App (pattern OcrModelInfo).
// SettingsPanel: enable option `llm`; section "LLM" (i18n `llm.*` ~35 key vi/en): radio Local/API; local → runtime row (tag + Check/Download/Update + flavor select) + bảng model (Tải/Xoá/Đặt); api → baseUrl/apiKey/model; prompt block (system + user textarea + 2 nút Khôi phục mặc định + validation `{source_text}` trước khi lưu); hint VRAM + "áp dụng từ Start kế".
// PromptDefaultsSyncTests: hằng số App (bản sao trong SettingsPanel hoặc AppSettingsService) == `ReTran.Core.Llm.PromptTemplate` consts (byte-equal).
```
- [ ] Steps: tests (settings round-trip 7 field; sync prompt; DTO mapping nhẹ) → fail → implement (UI là phần lớn) → build + full suite → commit `feat(app): LLM provider settings, model manager UI and prompt editor`.

### Task 8: Verification + perf note + smoke list

- [ ] build + full suite (≥ 789 + số test mới) 0 failed; ghi `Docs/performance.md` mục "LLM provider": kích thước file quant (spec §0), cơ chế (per-text requests, 120 s timeout, lazy server start), **số VRAM/RAM chờ smoke thật — deferred cho user**; checklist smoke: tải runtime cuda → tải 1.8B-UD-Q4_K_XL → chọn provider LLM/local → Start → dịch chạy; đổi prompt + khôi phục mặc định; mode API với endpoint thật (nếu có); xoá model khi idle.

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** D1→T6; D2→T2; D3→T1; D4→T2 (per-text default, không thêm gì); D5→T4; D6→T3; D7→T5+T6 (lazy start — ruling ghi ở T6); D8→T6; D9→T7; D10→T7; D11→T7 sync test; D12→T8. ✔
2. **Step scan:** mỗi task TDD thu gọn (tests list + interfaces chi tiết; UI ở T7 mô tả hành vi, không transcript). ✔
3. **Type consistency:** `LlmSettings` T2 dùng ở T6/T7; `LlmModelInfo` T3 ↔ App DTO T7 (tên field giữ nguyên); `LlmRuntimeStatus` T4 ↔ App `LlmRuntimeStatusInfo` T7; handler names `llm.*` khớp spec D8. ✔
4. **Review Focus:** 6 dòng ghim: #1→T6+T7; #2→T1; #3→T2; #4→T3; #5→T4; #6→T5. ✔
5. **Proportion:** plan gọn hơn spec; chi tiết số liệu ở spec §0. ✔
