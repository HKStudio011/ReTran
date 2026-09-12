# ReTran — Phân tích ý tưởng & Kế hoạch triển khai (v2 — đã chốt yêu cầu)

> **For Hermes:** Dùng skill subagent-driven-development để thực thi từng task khi user phê duyệt.
> **Nguyên tắc tối cao (từ AGENTS.md):** KHÔNG sửa code khi chưa có sự đồng ý rõ ràng của user. Mỗi milestone phải được user xác nhận trước khi bắt đầu.

**Goal:** Ứng dụng desktop Windows: capture màn hình / cửa sổ / game (kể cả exclusive fullscreen, kiểu OBS) → OCR định vị text → dịch (nhiều provider) → hiển thị trong app → xuất frame chú thích sang OBS — **chạy mượt trong lúc user vừa chơi game vừa stream**.

**Architecture:** C# (.NET 10 MAUI Blazor Hybrid) làm shell UI + core pipeline (capture, orchestrator, OBS client). Python sidecar (FastAPI) chạy PaddleOCR + model dịch local. Rust cdylib (`retran-hook`) — DLL inject vào tiến trình game hook D3D11 `Present` để capture exclusive fullscreen (cơ chế giống OBS GPU hook). SQLite + EF Core lưu cache dịch per-context và dữ liệu ứng dụng; JSON cho settings. OCR/translation đứng sau interface (`IOcrEngine`, `ITranslator`) để thay engine/provider bất kỳ lúc nào.

**Tech Stack:** C# 10 / .NET 10 (MAUI Blazor Hybrid, Win32 interop, EF Core + SQLite), Python 3.11 + PaddleOCR 3.x + FastAPI, TypeScript + Tailwind v4 (vite-project), Rust (hook DLL), obs-websocket. GPU: RTX 5060 Ti 16GB (CUDA) — đã kiểm tra máy thật.

**Yêu cầu đã chốt từ user:**
1. App = MAUI Blazor Hybrid chứa UI (giữ nguyên hướng scaffold hiện tại).
2. Dịch hỗ trợ **tất cả**: Google Translate API, DeepL API, model local, LLM theo chuẩn OpenAI — người dùng tự chọn trong UI.
3. Capture **hỗ trợ cả 3 mode** gồm exclusive fullscreen (giống OBS).
4. Code C# **đa ngôn ngữ: tiếng Việt + tiếng Anh** (doc comments song ngữ EN/VI).
5. **Tối ưu hiệu năng là yêu cầu cứng**: app chạy đồng thời với game + stream, không được làm tụt fps game.
6. **Persistence: SQLite + EF Core** làm CSDL ứng dụng; cache text đã dịch **theo ngữ cảnh** — từng game/phần mềm (tên process), hoặc của màn hình khi chỉ quay màn hình; JSON cho settings.

---

## PHẦN 1 — PHÂN TÍCH Ý TƯỞNG

### 1.1 Độ khả thi: CAO (có 2 khối cần spike sớm)

| Khối | Công nghệ | Mức độ chín | Rủi ro chính |
|---|---|---|---|
| Capture màn hình | DXGI Desktop Duplication (C# Win32 interop) | Rất cao — chuẩn của mọi recorder | API khó dùng, xử lý display mode change |
| Capture cửa sổ | `PrintWindow` / BitBlt theo HWND | Rất cao | App DRM có thể trả frame đen |
| **Capture exclusive FS** | **GPU hook: inject DLL (Rust) vào game, hook D3D11 `Present`, copy back buffer → shared memory; app đọc qua shared memory + named event** | Cao — OBS làm đúng cách này đã 10+ năm, tài liệu công khai nhiều | (a) Game anti-cheat từ chối/kill tiến trình có foreign DLL injected — **không bypass được**, chấp nhận giới hạn; (b) D3D12 game cần hook riêng (`Present` của D3D12); (c) user phải đồng ý inject từng process |
| OCR | PaddleOCR PP-OCRv5/v6 scene spotting (det + rec, trả box) | Rất cao — 89k stars | **Hỗ trợ CUDA cho RTX 50xx (Blackwell sm_120) chưa chắc** → spike M0; fallback ONNX Runtime |
| Dịch | 4 provider: Google API / DeepL / local model (NLLB, ONNX trong sidecar) / LLM OpenAI-compatible | Rất cao | Chi phí API; latency LLM cao → chỉ hợp text ít thay đổi |
| Xuất OBS | obs-websocket → image source | Cao — chuẩn cộng đồng | Giới hạn ~10fps ở v1 |

### 1.2 Phân tích hiệu năng (yêu cầu tối ưu là trung tâm)

Bối cảnh: game + OBS stream đang chạy, app chỉ được "gánh" thêm một phần nhỏ. Chiến lược thiết kế:

| Thành phần | Chiến lược | Ngân sách |
|---|---|---|
| Capture màn hình/cửa sổ | DXGI (GPU→CPU copy, không tốn CPU xử lý) | < 2% CPU |
| Capture exclusive FS | Hook chỉ copy back buffer trong `Present` (đã nằm trên GPU pipeline), shared memory + event — app đọc passively | < 1% CPU phía game, ~0.5% phía app |
| OCR | **Throttle ~3 fps** (text game/UI thay đổi chậm, không cần 60fps); model mobile mặc định; drop frame nếu inference chưa xong; GPU riêng biệt với game nhờ VRAM 16GB dư | VRAM < 1 GB, không tranh contention CPU nặng |
| Dịch | **Diff-based**: chỉ gửi text mới xuất hiện; cache per-context trong SQLite (ngữ cảnh game/app/màn hình × text × src × dst) → hit rate cao vì HUD/menu lặp lại; fully async, không block pipeline | API call giảm ~80–90% nhờ cache+diff |
| UI overlay | Throttle 10–15 fps cập nhật canvas | Negligible |
| OBS export | Image source cập nhật ~10fps qua obs-websocket | Negligible |
| **Tổng cộng** | | **< 8% CPU, < 500 MB RAM khi game+stream đang chạy** |

Quy tắc: mọi milestone phải đo footprint thật (game đang chạy + stream) và ghi `Docs/performance.md`. Feature nào phá ngân sách mà không có tradeoff được user chấp nhận → không ship.

### 1.3 Rủi ro & xử lý

| # | Rủi ro | Xử lý |
|---|---|---|
| R1 | PaddlePaddle GPU chưa hỗ trợ RTX 50xx trên Windows | **Spike M0 bắt buộc**: đo ms/frame thật. Fallback theo thứ tự: ONNX Runtime (CUDA/DirectML EP) với model PP-OCR export ONNX → Paddle CPU + mobile model |
| R2 | Anti-cheat (EAC, BattlEye…) kill game khi có foreign DLL injected vào exclusive FS | Không bypass — thiết kế chấp nhận: UI cảnh báo rõ "process này dùng anti-cheat, hook có thể bị chặn"; user tự quyết; mode window/borderless luôn là fallback. Đây đúng giới hạn mà OBS cũng gặp với game online có anti-cheat gắt (OBS giải bằng Game Capture ở chế độ riêng) |
| R3 | D3D12 games không dùng D3D11 `Present` | Hook DLL xử lý cả D3D11 + D3D12 (2 hook points); spike với 1 game D3D12 thật trong M-Hook |
| R4 | LLM provider latency cao cho text thay đổi nhanh | UI đánh dấu loại nội dung: "menu/dialog" (ít đổi → LLM OK) vs HUD; mặc định dùng API nhanh, LLM là tùy chọn của user |
| R5 | Chi phí API dịch khi chơi lâu | Diff + cache + option giới hạn số dòng dịch/frame; local model luôn available như fallback offline |
| R6 | Hai project C# hiện là template trùng lặp, `ReTran Core` chưa trong .slnx | Restructure ngay M0 (user đã đồng ý hướng: App = MAUI Blazor Hybrid chứa UI) |

### 1.4 Cấu trúc repository (chốt theo yêu cầu "App chứa UI")

```
ReTran Project/
├── ReTran.slnx
├── src/
│   ├── ReTran.App/            # .NET 10 MAUI Blazor Hybrid — UI + composition root
│   │                          #   (chuyển từ "ReTran App"; vite-project di chuyển vào đây)
│   ├── ReTran.Core/           # class library thuần: interfaces + pipeline, test được không cần UI
│   │                          #   IFrameSource, IOcrEngine, ITranslator, IObsSink, FramePipeline
│   └── ReTran.Capture.Win/    # class library Win32 interop: DXGI, PrintWindow, shared-memory reader
├── native/
│   └── retran-hook/           # Rust cdylib — D3D11/D3D12 Present hook cho exclusive FS (OBS-style)
├── ocr-service/               # Python sidecar: FastAPI + PaddleOCR + local translation model, venv riêng
└── Docs/                      # ADR, spike reports, performance.md, usage.md
```

- `ReTran.Core` là class library (không phải MAUI app như template hiện tại).
- Folder gốc `ReTran Core/`: giữ `vite-project` di chuyển vào `src/ReTran.App/`, phần template thừa xóa khi restructure.

### 1.5 Câu hỏi mở còn lại (không chặn M0)

| Câu hỏi | Chặn | Gợi ý mặc định |
|---|---|---|
| Game cụ thể nào là "test horse" cho exclusive FS + anti-cheat? (cần 1 game D3D11 offline, 1 game D3D12) | M-Hook | User chỉ định khi đến milestone đó |
| LLM OpenAI-compatible: endpoint mặc định chạy local (Ollama trên RTX 5060 Ti) hay cloud? | M3 (chỉ provider LLM) | Local Ollama — không tốn tiền, GPU đã có; cloud là tùy chọn cấu hình |

---

## PHẦN 2 — KẾ HOẠCH TRIỂN KHAI

Hai track chạy **song song** từ đầu:
- **Track A (pipeline):** M0 → M1 → M2 → M3 → M4
- **Track B (exclusive FS hook):** M-Hook — độc lập với pipeline, chỉ cần kết quả của spike capture (M0)

### Track A

#### Milestone 0 — Nền tảng & Spike (~2–3 ngày)

**Task 0.1: Git init + .gitignore**
- `git init`; `.gitignore`: bin/obj, node_modules, venv, __pycache__, target/, wwwroot/build.
- Verify: `git status` không hiện folder build.

**Task 0.2: Restructure theo 1.4 (user đã duyệt hướng)**
- Tạo `src/ReTran.Core.csproj` (class lib net10.0), `src/ReTran.Capture.Win.csproj` (net10.0-windows).
- Di chuyển UI: `vite-project` → `src/ReTran.App/vite-project`; cập nhật `.slnx`.
- Verify: `dotnet build ReTran.slnx -f net10.0-windows10.0.19041.0` → 0 errors.

**Task 0.3: Spike OCR (quyết định R1)**
- `ocr-service/venv` (Python 3.11) + `paddlepaddle-gpu` + `paddleocr`.
- Chạy PP-OCRv5 mobile trên ảnh game thật: đo **ms/frame, VRAM, RAM**, xác nhận CUDA chạy thật (không fallback CPU).
- Deliverable: `Docs/spikes/ocr-benchmark.md` + quyết định backend.

**Task 0.4: Spike capture (nền cho cả 2 track)**
- Console C#: DXGI Desktop Duplication — đo fps, ms/frame, CPU%.
- `PrintWindow` với cửa sổ thường + game borderless — kiểm tra frame không đen.
- Deliverable: `Docs/spikes/capture-benchmark.md`.

**Task 0.5: ADR-001** — kiến trúc tổng thể (cấu trúc project, OCR backend, sidecar protocol, hook design) vào `Docs/adr/001-architecture.md`.

**Hoàn thành khi:** spike có số đo thật; user duyệt ADR.

#### Milestone 1 — Capture MVP trong app (~3–5 ngày)

**Task 1.1: `IFrameSource` + `DxgiFrameSource`** (`src/ReTran.Capture.Win/`)
- Interface (doc song ngữ EN/VI theo quy ước AGENTS.md): `StartAsync()`, `StopAsync()`, `event FrameAvailable(Frame)`; `Frame = { BgraPixels, Width, Height, TimestampMs }`.
- DXGI Desktop Duplication, enum monitor.
- Test: logic thuần (frame drop, mode change) bằng xUnit + fake; verify thủ công bằng console host đo fps thật.

**Task 1.2: `WindowFrameSource`** — EnumWindows liệt kê cửa sổ có tên, capture theo HWND.

**Task 1.3: UI chọn nguồn + preview** (`src/ReTran.App/Components/Pages/Capture.razor` + vite-project)
- Dropdown (màn hình / cửa sổ), Start/Stop, canvas preview ≥ 15fps (JS interop — phương án truyền frame chốt khi code, ưu tiên đơn giản).

**Task 1.4: Frame buffer + backpressure** — chỉ giữ frame mới nhất cho OCR; đo CPU% của toàn module trong `Docs/performance.md`.

**Hoàn thành khi:** capture màn hình + cửa sổ chạy ổn định trong app, số đo footprint đạt ngân sách.

#### Milestone 2 — OCR sidecar + hiển thị text (~1 tuần)

**Task 2.1: OCR Python service** (`ocr-service/`)
- FastAPI `POST /ocr` (PNG/JPEG) → `{ "items": [ {"bbox": [[x,y]x4], "text", "score" } ] }`.
- Warm-up model khi start; query param chọn model mobile/server.
- Verify: `curl` ảnh test → box đúng vị trí; đo ms/frame với GPU thật.

**Task 2.2: `IOcrEngine` + `SidecarOcrEngine`** (`src/ReTran.Core/`)
- `Task<OcrResult> RecognizeAsync(Frame, CancellationToken)`; C# HTTP client; app tự spawn/quản lý sidecar process.

**Task 2.3: `FramePipeline`** — nhận frame mới nhất → OCR (throttle ~3fps, drop nếu busy) → event `OcrResultAvailable`. Mục tiêu latency capture→kết quả **≤ 500ms**; đo thật.

**Task 2.4: UI kết quả** — vẽ box + text lên canvas; bảng danh sách bên cạnh; đo accuracy trên 5–10 ảnh game thật của user → `Docs/spikes/ocr-accuracy.md`.

**Hoàn thành khi:** chạy live với game borderless đang chơi: box + text hiện đúng, latency + footprint đạt ngân sách.

#### Milestone 3 — Dịch đa provider + persistence (~5–6 ngày)

**Task 3.1: SQLite + EF Core foundation** (`src/ReTran.Core/Data/`)
- `ReTranDbContext` (EF Core + `Microsoft.EntityFrameworkCore.Sqlite`), DB file `%LOCALAPPDATA%\ReTran\retran.db`.
- Tables: `TranslationCache` (ContextKey, SourceText, SrcLang, DstLang, TranslatedText, UpdatedAtUtc, Hits — unique index `(ContextKey, SourceText, SrcLang, DstLang)`), `ProcessAllowlist` (cho Track B). EF migrations từ đầu.
- Settings app: JSON (`settings.json` cạnh DB) — provider keys, capture defaults, OBS config. Không đưa settings vào DB.

**Task 3.2: `ITranslator` + `TranslationContext`**
- `TranslateAsync(IReadOnlyList<string>, TranslationContext, srcLang, dstLang)` (batch); config: provider, API key, endpoint, ngôn ngữ.
- `TranslationContext` = danh tính nguồn nội dung: tên process (+ window title) của game/app khi capture cửa sổ/hook, hoặc `screen:<monitor>` khi chỉ quay màn hình. Resolve tự động từ `IFrameSource` đang hoạt động — user không cần cấu hình thủ công.

**Task 3.3: Bốn providers**
- `GoogleTranslator` (official API)
- `DeepLTranslator`
- `LocalTranslator` — NLLB/m2m100 qua ONNX Runtime **chạy trong Python sidecar** (endpoint `/translate`), offline, không tốn key
- `OpenAiCompatibleTranslator` — bất kỳ endpoint chuẩn OpenAI (Ollama local mặc định, cloud tùy chọn)

**Task 3.4: Diff + cache per-context layer** (`TranslationCacheService`)
- Runtime diff theo phiên: chỉ gửi text mới xuất hiện kể từ frame OCR trước.
- Cache bền trong SQLite theo `(ContextKey, SourceText, SrcLang, DstLang)` → cùng một dòng "Missions" trong game A và game B (hoặc trên màn hình) là 2 bản ghi riêng; hit → trả về ngay không gọi API, tăng `Hits`.
- Đo hit rate + số API call trên phiên chơi thật → `Docs/performance.md`.

**Task 3.5: UI** — cột "Gốc | Dịch", option ẩn text gốc; chọn provider trong settings; quản lý cache (xem/xóa theo ngữ cảnh).

**Hoàn thành khi:** khung hình song song gốc/dịch live; cache per-context + diff hoạt động (số đo hit rate thật); đổi provider không cần restart app; DB migrations chạy sạch.

#### Milestone 4 — Xuất OBS + hoàn thiện (~1 tuần)

- **Task 4.1:** `IObsSink` + obs-websocket client (URL, password, tên image source trong settings).
- **Task 4.2:** Render frame chú thích → cập nhật image source ~10fps.
- **Task 4.3:** Settings UI đầy đủ: nguồn capture, ngôn ngữ, provider dịch + keys, OBS, hotkey start/stop, allowlist inject (cho Track B).
- **Task 4.4:** `Docs/usage.md` + smoke test end-to-end: game → app → OBS thấy frame chú thích cập nhật.

**Hoàn thành khi:** stream OBS hiện frame với text dịch live; footprint tổng đạt ngân sách khi game+stream chạy.

### Track B — Exclusive Fullscreen Hook (song song, bắt đầu sau M0)

#### Milestone H1 — Spike hook (~3–4 ngày, độc lập)

**Task H1.1: Thiết kế giao tiếp shared memory**
- Layout: 1 frame BGRA + metadata (width, height, timestamp, sequence) trong `CreateFileMapping` named; `PostMessage`/named event để app biết có frame mới. Ghi ADR-002.

**Task H1.2: Rust cdylib `retran-hook`** (`native/retran-hook/`)
- Hook D3D11 `IDXGISwapChain::Present`: copy back buffer (staging texture → `Map`) vào shared memory, tăng sequence.
- Install/uninstall sạch; guard chống re-inject; log ra file để debug trong tiến trình game.

**Task H1.3: Injector C#** (`src/ReTran.Capture.Win/ProcessInjector.cs`)
- `CreateRemoteThread` + `LoadLibraryW`; consent UI: user phải tick "cho phép inject vào [process]" + thêm vào allowlist.
- Spike test: game D3D11 offline thật (user chỉ định) → app nhận frame, đo CPU% phía game.

**Task H1.4: D3D12 support** — hook `ID3D12CommandQueue::Present`/swap chain tương đương; test với game D3D12 thật.

**Hoàn thành khi:** exclusive FS game offline chạy → app nhận frame < 5ms latency, CPU phía game < 1%, unhook sạch không crash game.

#### Milestone H2 — Tích hợp vào pipeline (~2–3 ngày, sau M1)

- `HookFrameSource` implement `IFrameSource`, đọc shared memory (passive read).
- UI: thêm mode "Game (exclusive FS)" + cảnh báo anti-cheat rõ ràng; allowlist trong settings.
- Test với 2 game thật (D3D11 + D3D12) + đo footprint khi stream đồng thời.

### Milestone cuối — Tối ưu & đóng gói (~1 tuần, sau M4 + H2)

- Benchmark tổng: fps game có/không có app, VRAM, RAM → `Docs/performance.md`.
- Nếu sidecar là bottleneck: cân nhắc ONNX Runtime in-process (interface đã sẵn).
- Đóng gói: publish C# self-contained; Python service theo kết quả spike (venv + launcher script hoặc PyInstaller/Nuitka); installer.

---

## PHẦN 3 — LỘ TRÌNH TỔNG

```
Tuần 1:   M0 (spike) ─────────────┐
        M-Hook H1 (song song) ────┤
Tuần 2:   M1 (capture MVP)        │
        H1 tiếp tục → H2 tích hợp │
Tuần 3:   M2 (OCR)                │
Tuần 4:   M3 (dịch)               │
Tuần 5:   M4 (OBS + hoàn thiện)   │
Tuần 6:   Tối ưu + đóng gói       ┘
```

Mỗi milestone: user review kết quả + số đo → xác nhận → tiếp. Track B không block Track A và ngược lại.

## PHẦN 4 — XÁC MINH CHUNG (mọi milestone)

- Mọi claim "chạy được" phải kèm số đo thật (fps, ms, CPU%, RAM/VRAM) ghi trong `Docs/`.
- Đo footprint **luôn với game + stream đang chạy** (yêu cầu tối ưu là trung tâm).
- Build chuẩn: `dotnet build ReTran.slnx -f net10.0-windows10.0.19041.0` → 0 errors.
- Python service luôn trong `ocr-service/venv`; Rust build bằng `cargo` (cần cài trước H1).
- Doc comments C# song ngữ EN/VI theo quy ước AGENTS.md.
- Commit sau mỗi task (git init ở M0).
