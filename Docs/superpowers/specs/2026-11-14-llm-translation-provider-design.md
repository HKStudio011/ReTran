# Thiết kế: Provider dịch LLM (GGUF local qua llama.cpp + API OpenAI-compatible)

- Ngày: 2026-11-14
- Trạng thái: **đề xuất — user yêu cầu "làm luôn, review một lần"** (2026-11-14)
- Ngữ cảnh: spec M4 (`2026-10-06`) đã chừa slot UI `LLM (OpenAI-compatible)` (disabled). Interface `ITranslator` (có `TranslateBatchAsync` từ plan batch 2026-11-13). User chốt hướng (thảo luận 2026-11-12): hỗ trợ **model GGUF Hy-MT2 (1.8B/7B)** chọn trong danh sách kiểu OCR; **API chuẩn OpenAI** (URL + key tuỳ chọn); **system/user prompt sửa được + khôi phục mặc định**; **tải/kiểm tra/cập nhật llama.cpp**.

## 0. Sự thật đã kiểm chứng (2026-11-12 → 14)

- **Hy-MT2** (Tencent, model card + 2 repo GGUF unsloth đã đọc): model **không có system prompt mặc định**; prompt user chuẩn (tiếng Anh): *"Translate the following text into `{target_lang}`. Note that you should only output the translated result without any additional explanation:\n\n`{source_text}`"*; **target_lang dùng tên đầy đủ** (Vietnamese, không phải `vi`); tham số khuyến nghị 1.8B/7B: `temperature 0.7, top_p 0.6, top_k 20, repetition_penalty 1.05`.
- **GGUF có sẵn** (unsloth): 1.8B — UD-Q4_K_XL **1,17 GB** (docs HF lấy làm ví dụ), Q4_K_M 1,13, Q5_K_M 1,30, Q6_K 1,47, Q8_0 1,91, UD-Q3_K_XL 0,99; 7B — UD-Q4_K_XL **4,78 GB**, Q4_K_M 4,62, Q5_K_M 5,37, Q6_K 6,16, Q8_0 7,98, UD-Q3_K_XL 3,96. File: `https://huggingface.co/unsloth/Hy-MT2-<1.8B|7B>-GGUF/resolve/main/<tên>.gguf`.
- **llama.cpp**: `llama-server` chuẩn OpenAI (`/v1/chat/completions`); bản Windows cuda nằm ở **nightly release assets** (`llama-b<NNNNN>-bin-win-cuda-x64.zip`, kèm `cudart-llama-bin-*.zip` khi cần); version ổn định hiện tại `v0.9.0` / nightly gần nhất `b12110` (2026-11-04).
- **Budget:** local LLM là **opt-in**, vượt budget VRAM hiện tại (OCR < 1 GB); 1.8B Q4 ≈ 1,2–1,5 GB VRAM, 7B Q4 ≈ 5–5,5 GB — phải cảnh báo trong UI.

## 1. Mục tiêu / Non-goals

**Mục tiêu**
- Provider **`llm`** với 2 mode: **local** (GGUF qua `llama-server` do Core quản lý vòng đời) và **api** (endpoint OpenAI-compatible: URL, key tuỳ chọn, model name).
- Quản lý **runtime llama.cpp**: kiểm tra bản mới, tải, cập nhật, trạng thái — UI kiểu OCR. **Tải tự động trong setup wizard lần đầu** (flavor theo SystemCheck: GPU → cuda, còn lại → cpu/avx2; runtime KHÔNG nằm trong zip release; lỗi mạng → wizard cho phép bỏ qua, tải lại từ Settings) — user directive 2026-11-14.
- Quản lý **model GGUF**: catalog 2 repo × 6 quant; tải (nền, có `.partial`), xoá, chọn active — UI kiểu OCR.
- **Prompt**: system + user sửa được, có "Khôi phục mặc định" (mặc định = prompt Hy-MT2 ở §0); placeholder `{source_text}`, `{source_lang}`, `{target_lang}` (điền tên đầy đủ tiếng Anh khi có map, fallback = mã).

**Non-goals**
- Batch nhiều text trong 1 prompt LLM (v1 giữ per-text qua default `TranslateBatchAsync` — an toàn alignment; batch prompt để sau).
- Streaming; fine-tune; UI chỉnh `-ngl`/quant tại chỗ; tự phát hiện endpoint ngoài OpenAI spec; DeepL (vẫn disabled).
- Tải model/runtime tự động khi chưa bấm: **mọi tải đều do người dùng bấm**.

## 2. Quyết định chính

| # | Quyết định | Ghi chú |
|---|-----------|---------|
| D1 | Provider name **`llm`** trong `TranslatorRegistry`. `core.start.translate.llm = { mode: "local"\|"api", baseUrl?, apiKey?, model?, systemPrompt?, userPrompt? }` — khi `provider=llm`: `mode` bắt buộc (thiếu/sai → `-32602`); local: `model` = tên catalog đã cài (thiếu chưa cài → `-32603` kèm code `llm-model-missing` giống OCR); api: `baseUrl` bắt buộc. `systemPrompt`/`userPrompt` vắng → dùng mặc định tại Core (cùng hằng số với UI). Messaging song ngữ như các provider khác. | *(my call)* |
| D2 | Core module mới `ReTran.Core/Llm/`: `LlmSettings` (record), `PromptTemplate` (apply/validate/`DefaultUserPrompt`/`DefaultSystemPrompt=""`), `LanguageNames` (map code→tên đầy đủ EN, ~40 mã incl `vi→Vietnamese`, thiếu thì fallback mã), `OpenAiChatClient` (POST `{baseUrl}/v1/chat/completions`, timeout **120 s** — generation local chậm; không retry; đọc `choices[0].message.content`, trim), `LlmTranslator : ITranslator` (`Name="llm"`; dựng messages: system nếu chuỗi khác rỗng + user đã fill; gửi `temperature=0.7, top_p=0.6, max_tokens=4096`; **local** gửi thêm `top_k=20, repeat_penalty=1.05` — llama.cpp chấp nhận field mở rộng, API remote không). | *(my call; số từ model card §0)* |
| D3 | `PromptTemplate.Apply` thay `{source_text}`, `{source_lang}`, `{target_lang}` (tên đầy đủ); template thiếu `{source_text}` → validation UI chặn lưu (Core vẫn ghép text ở cuối nếu thiếu? — KHÔNG: Core ném `FormatException` → lỗi dịch chu kỳ bình thường). | *(my call)* |
| D4 | **Batching v1 = per-text** (default impl). Lý do: alignment nhiều segment bằng prompt LLM chưa được kiểm chứng với model nhỏ; local không rate-limit. | *(my call)* |
| D5 | **Runtime manager** `LlamaRuntimeManager` (Core): trạng thái lưu `%LOCALAPPDATA%\ReTran\llm\runtime\runtime.json` `{ tag, downloadedAtUtc, dir }`. **Check**: GET `https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30`, chọn release mới nhất có asset khớp `llama-b*-bin-win-cuda-x64.zip` (+ optional `cudart-llama-bin-win-cuda-*.zip`); so tag với runtime.json. **Download/update**: tải zip (nền, `.partial`), giải nén `System.IO.Compression` vào `runtime/<tag>/`, cập nhật runtime.json. Có asset CPU `-bin-win-avx2-x64.zip` làm fallback khi user bấm "tải bản CPU" (nút phụ). | *(my call; asset pattern đã xác nhận qua HF card + release notes 2026-11-04)* |
| D6 | **Model manager** `LlmModelManager` (Core): catalog tĩnh 2 size × 6 quant (UD-Q4_K_XL mặc định, UD-Q3_K_XL, Q4_K_M, Q5_K_M, Q6_K, Q8_0 — size trong §0) kèm kích thước byte; `%LOCALAPPDATA%\ReTran\llm\models\<tên>.gguf`; tải nền + marker `.partial`; `list` suy `installed` từ filesystem (đúng pattern OCR `ModelManager`); `set` active model (lưu `%LOCALAPPDATA%\ReTran\llm\models.json`). Xoá bị chặn khi đang là active. | *(my call; mirror `ReTran.OCR/.../models.py`)* |
| D7 | **Server host** `LlamaServerHost` (Core): khi phiên bắt đầu với mode local: chọn cổng trống (bind 127.0.0.1:0 → lấy port → đóng), spawn `runtime/<tag>/llama-server.exe -m <gguf> --host 127.0.0.1 --port N -c 4096 -ngl 99 --jinja` (không `--jinja` nếu template nằm sẵn — dùng `--jinja` để chắc), poll `GET /health` tới 120 s (model load); sẵn sàng → translator gọi `/v1/chat/completions`; `core.stop`/dispose → kill process tree. Spawn/health lỗi → log stderr, `llm.server.status` trả `error` cho UI; chu kỳ dịch vẫn không crash (translate fail → giữ text gốc như M4). | *(my call)* |
| D8 | **RPC**: `llm.runtime.status` / `llm.runtime.download {flavor:"cuda"\|"cpu"}` / `llm.models.list` / `llm.models.download {name}` (`{accepted:true}` ngay) / `llm.models.delete {name}` / `llm.models.set {name}` — App gọi qua `PipelineController` (mirror `ocr.models.*`); core.start mang `translate.llm` (D1). | *(my call)* |
| D9 | **App UI**: section mới "LLM" trong `SettingsPanel.razor` (mirror section OCR): radio Local/API; local → hàng runtime (tag hiện có, nút Kiểm tra/Tải/Cập nhật + trạng thái) + bảng model (tên/quant/size/Đã cài/Đang dùng + Tải/Xoá/Đặt); api → ô `baseUrl`, `apiKey`, `model`; khối prompt (system textarea, user textarea, nút "Khôi phục mặc định", hint placeholder + cảnh báo VRAM khi local); mọi thứ lưu `settings.json` và áp từ lần Start kế (pattern OCR). | *(my call)* |
| D10 | **AppSettings** thêm: `LlmMode = "local"`, `LlmBaseUrl = ""`, `LlmApiKey = null`, `LlmApiModel = "Hy-MT2-1.8B"` (free text cho api), `LlmActiveModel = "Hy-MT2-1.8B-UD-Q4_K_XL"` (local), `LlmSystemPrompt = null`, `LlmUserPrompt = null` (null = mặc định Core/UI). Provider select: bật option `llm`. | *(my call)* |
| D11 | Prompt mặc định **đồng bộ 2 nơi**: hằng số Core (`PromptTemplate`) + hiển thị UI khi field null (UI lấy qua RPC? giữ đơn giản: UI có bản sao hằng số + test so khớp Core↔App như pattern `BoxAnnotationStyleSyncTests`). | *(my call)* |
| D12 | Budget: ghi mục `Docs/performance.md` "LLM provider" — số VRAM/RAM **đo thật khi user smoke** (deferred); ghi trước các con số file/kích thước + cơ chế; cảnh báo UI "ngoài budget OCR". | *(my call)* |
| D13 | **Setup tự tải runtime** (user directive 2026-11-14): bước mới trong `SetupCoordinator` sau `VerifyingSidecar` — gọi `llm.runtime.download {flavor}` qua `ISetupCoreBridge` rồi poll `llm.runtime.status` tới khi `installed` (reuse hằng số poll/timeout của bước tải model OCR); flavor = GPU khi SystemCheck + capability gate chọn GPU, ngược lại CPU (avx2). Runtime **không nằm trong zip release**. Thất bại → retry mềm ×4 như mọi bước; sau đó user có thể bỏ qua qua popup sẵn có và tải lại từ Settings. | *(user)* |

## 3. Thay đổi code theo file

### Core (`ReTran.Core`)
- **Mới** `Llm/LlmSettings.cs`, `Llm/PromptTemplate.cs`, `Llm/LanguageNames.cs`, `Llm/OpenAiChatClient.cs`, `Llm/LlmTranslator.cs`, `Llm/LlmModelManager.cs`, `Llm/LlamaRuntimeManager.cs`, `Llm/LlamaServerHost.cs` (+ `Llm/LlmRpcHandlers.cs` — bind các method D8).
- `Serve/PipelineHost.cs`: parse `translate.llm` (D1) → tạo translator + host theo mode; dispose host khi stop.
- `Program.cs`: register `llm` translator factory + RPC handlers; truyền HTTP client chung.

### App (`ReTran.App`)
- `Services/AppSettingsService.cs`: 7 field D10.
- `Core/PipelineController.cs`: gửi `translate.llm` khi provider=llm; thêm 6 method RPC D8.
- `Components/Shared/SettingsPanel.razor`: section LLM (D9) + i18n `llm.*` vi/en.
- `Services/I18nStrings.cs`: keys mới.
- (Không đụng overlay/preview.)

### Tests (`ReTran.Tests`)
- `PromptTemplateTests`: apply placeholders; thiếu `{source_text}` → FormatException; default khớp nội dung §0 (assert chuỗi).
- `LanguageNamesTests`: vi→Vietnamese; fallback mã lạ.
- `OpenAiChatClientTests` (fake handler): URL/body đúng (model, messages, params; local có top_k/repeat_penalty, api không), parse `choices[0].message.content`, HTTP lỗi → throw, JSON lỗi → FormatException.
- `LlmTranslatorTests`: end-to-end với fake client (prompt fill + trim + lỗi → ném).
- `LlmModelManagerTests`: list/installed theo filesystem + `.partial`; download ghi file + marker (downloader fake); delete chặn active; set ghi models.json.
- `LlamaRuntimeManagerTests`: check chọn asset mới nhất (JSON fake), so tag; download/extract với zip fake; runtime.json round-trip.
- `LlamaServerHostTests`: dựng args đúng (port/model), health poll với fake prober, stop kill (fake process).
- `PromptDefaultsSyncTests`: hằng số prompt Core ↔ bản App khớp (pattern sync test có sẵn).
- `PipelineHostTests` mở rộng: `translate.llm` thiếu mode → -32602; mode lạ → -32602; api thiếu baseUrl → -32602.
- `AppSettingsServiceTests`: round-trip 7 field mới.

## 4. Rủi ro / giới hạn đã biết

- GitHub/HF API có thể đổi shape → check/tải fail có thông báo, không crash; catalog model là tĩnh nên ít rủi ro.
- Cờ CLI llama.cpp có thể đổi giữa các nightly → dùng flag ổn định (`-m/--host/--port/-c/-ngl/--jinja`); smoke thật khi user chạy.
- Chat template Hy-MT2 trong GGUF: `--jinja` để dùng template nhúng; nếu template thiếu → model vẫn nhận prompt thô (chất lượng có thể giảm) — ghi chú.
- VRAM: local 7B có thể OOM trên GPU nhỏ → UI cảnh báo + lỗi server hiển thị trạng thái.
- Thời gian nạp model local 5–30 s ở lần Start đầu — trạng thái `loading` trên UI (llm.server.status).
- `top_k`/`repeat_penalty` gửi cho API remote sẽ bị một số server từ chối → chỉ gửi ở local (D2).

## 5. Ngoài phạm vi

Batch prompt nhiều segment; multi-user prompts theo ngôn ngữ; tải quant 1.25-bit (Tencent repo khác); quản lý VRAM/offload UI; ollama/LM Studio auto-discovery; streaming output.
