# Spec: Dọn dẹp + OCR GPU performance ladder

- **Ngày:** 2026-09-26
- **Trạng thái:** Approved (user chọn Phương án A qua brainstorming session)
- **Phạm vi:** cắt dependency/dead code + tăng tốc OCR (GPU ladder). **Không đổi kiến trúc.**

## 1. Bối cảnh

**Hiệu năng:** paddle đang chạy CPU build. Baseline M2 (`Docs/performance.md`): warm median 1080p **35,467 s** vs budget **333 ms** → BREACH ~106×. M3 perf gate (pin `PP-OCRv5_mobile_det` + rec mobile, tắt 3 pipeline doc-preprocess, downscale 1280px) đã code (T1–T5) nhưng **T6 đo baseline chưa chạy**.

**Tinh gọn (khảo sát 2026-09-26, số liệu thật):**

| Hạng mục | Số liệu |
|---|---|
| `jquery` + `@types/jquery` | bundle `main.js` = 103.429 B, **0 usage** `$(`/jQuery trong app |
| `torch>=2.14` (pyproject) | **0 `import torch`** trong src/test — ~2 GB mỗi `uv sync` |
| `AppSettings.cs` (69 dòng) | **0 reference** ngoài chính nó — dead code |
| MAUI template leftovers | `Counter.razor`, `Weather.razor`, `NavMenu` link, Bootstrap CSS (~0,8 MB tracked) |
| MAUI TFMs | 4 platform (android/ios/maccatalyst/windows) cho app **Windows-only** |
| Trùng lặp | 2 encoder PNG (`PngWriter`/`BgraPngEncoder`) cùng logic; `FindCoreExe` × 2; `FindRepoRoot` × 2 |
| `CommandDispatcher` usage | quảng bá `translate` — chưa có code (M4) |
| `ReTran.OCR.pyproj` | liệt kê 13 file `.pyc` + 4 thư mục `__pycache__` |

Tình trạng tích hợp: App chỉ gọi được `core.ping`; UI chưa nối OCR loop; DemoFeed đang là đường chạy thật duy nhất của overlay. (Ngoài spec này — App wiring thuộc milestone riêng.)

## 2. Quyết định đã chốt (brainstorming)

| # | Câu hỏi | Quyết định |
|---|---|---|
| 1 | Ưu tiên hiệu năng | **OCR pipeline latency** (budget 333 ms/lượt) |
| 2 | Mức tinh gọn | **Dọn dẹp + cắt deps** — không đổi kiến trúc |
| 3 | Chiến lược GPU | **Ladder:** `paddlepaddle-gpu` → ONNX Runtime GPU (CUDA) → ONNX DirectML |
| 4 | Baseline | **Đo T6 CPU baseline trước** khi đổi engine (máy idle) |
| 5 | Phương án tổng thể | **A — ladder có đo lường:** T6 → dọn dẹp → GPU từng nấc, mỗi bước có số đo |

## 3. Trình tự 3 phase

### Phase 0 — T6 baseline (đo, không code)

Chạy đúng plan M3 Task 6 (`Docs/superpowers/plans/2026-09-24-m3-frame-loop.md`):

1. Preflight máy idle: avg CPU ≤ 20% (`Get-Counter '\Processor(_Total)\% Processor Time'`) — nếu > 20% thì **dừng, báo user đóng ComfyUI/Hermes/WSL rồi đo lại**.
2. Fixtures 1920×1080 + 640×96; one-shot cold + warm N=5; loop throughput (`ocr loop --max-frames 10`); CPU/RAM trong lúc loop.
3. Ghi section **M3** vào `Docs/performance.md` (trước section M1), kèm acceptance table PASS/BREACH.

**Exit:** section M3 commit — có số CPU+mobile-det để đối chiếu GPU.

### Phase 1 — Dọn dẹp (không đổi hành vi)

**1A. Cắt dependency thừa**

- Bỏ `jquery` + `@types/jquery` khỏi `ReTran.App/vite-project/package.json`; sửa `src/ts/main.ts` bỏ export `$`. Rebuild → ghi kích thước bundle mới vào report.
- Bỏ `torch>=2.14` khỏi `ReTran.OCR/pyproject.toml` → `uv sync` + `uv run pytest tests -v`. *Nếu paddleocr khai báo torch là dependency của nó → uv vẫn cài → ghi nhận là transitive, KHÔNG ép gỡ (giữ sidecar chạy được).*
- `ReTran.OCR.pyproj`: bỏ các entry `.pyc`/`__pycache__`; giữ pyproj trong slnx (VS dùng để mở Python).

**1B. Xóa dead code + template leftovers**

- Xóa `ReTran.App/Core/AppSettings.cs` (0 ref).
- Xóa `Pages/Counter.razor`, `Pages/Weather.razor`; gỡ link corresponding trong `NavMenu.razor`.
- Bootstrap CSS: sau khi gỡ template pages, rà class usage còn lại; chỉ gỡ link `bootstrap.min.css` + xóa `wwwroot/lib/bootstrap` nếu **không còn usage** — nếu còn thì giữ (không refactor CSS trong spec này).
- Sửa usage string `CommandDispatcher`: bỏ chữ `translate` (đến M4 mới thêm lại).

**Giữ lại có chủ đích (không phải bỏ sót):**

- Debug buttons Start Core/Ping/Stop ở `MainPage.xaml` — là harness của `AppiumSmokeTests` (test duy nhất chứng minh App↔Core). Không đụng.
- `DemoFeed` + `IOverlayFeed` — đường chạy thật hiện tại của overlay, có 3 file test.

**1C. Gộp trùng lặp**

- `PngWriter.WriteBgra` delegate sang `BgraPngEncoder.ToPngBytes` (giữ 2 public surface vì `CaptureCliTests`/`BgraPngEncoderTests` đang test cả 2; gộp phần encode về 1 chỗ).
- `FindCoreExe`: extract 1 helper `public static` trong `ReTran.App` (tests đã reference assembly App) — `MainPage.xaml.cs` + `AppiumSmokeTests` dùng chung.
- `SidecarProcessTests.FindRepoRoot` → gọi `OcrBootstrap.FindRepoRoot()` (đã public, `OcrLoopCliTests` đang xài).

**1D. MAUI TFM Windows-only**

- `ReTran.App.csproj`: chỉ còn `net10.0-windows10.0.19041.0` (bỏ android/ios/maccatalyst → bỏ 3 workload).
- Verify: `dotnet build "ReTran Project.slnx"` + full test suite sạch.

**Không gộp (ngoài phạm vi — user chọn "không đổi kiến trúc"):** 2 stack JSON-RPC (App↔Core vs Core↔sidecar), interface 1-impl (`IOverlayFeed`, `IJsonRpcTransport`, `ISidecarRpc`, `ICoreConfigLoader`).

**Exit:** build sạch, C# 70 tests + Python 15 tests pass (điều chỉnh test chạm file bị xóa, KHÔNG bớt assertion).

### Phase 2 — GPU ladder (mỗi nấc 1 probe + 1 số đo)

**Nấc 2a — `paddlepaddle-gpu` (thử trước, ít code nhất)**

- Probe: cài `paddlepaddle-gpu` vào venv → `paddle.is_compiled_with_cuda()`, chạy 1 phép tính nhỏ trên RTX 5060 Ti, rồi `ocr.spot` fixture thật.
- **Rủi ro chính:** RTX 5060 Ti = Blackwell `sm_120` cần CUDA ≥ 12.8; wheel paddle 3.3.x có thể ship CUDA cũ hơn → fail. Probe ghi output trung thực.
- Nếu **OK:** giữ nguyên `PaddleOcrEngine`/`PaddleOCR` API — chỉ đổi dep + `enable_mkldnn` irrelevant trên GPU; đo lại.
- Nếu **fail:** sang 2b.

**Nấc 2b — ONNX Runtime GPU (CUDA EP)**

- Export `PP-OCRv5_mobile_det` + `en_PP-OCRv5_mobile_rec` sang ONNX (tool paddle2onnx/paddlex từ hệ paddle — giữ paddle chỉ làm **tool export + parity reference**, không runtime).
- Viết `OnnxOcrEngine` implement đúng `OcrEngine` protocol (preprocess → det → NMS → crop → rec → CTC decode).
- Dep: thêm `onnxruntime-gpu`; runtime engine đổi sau interface — **C# side không đổi 1 dòng** (contract `ocr.spot` giữ nguyên).
- **Parity test (bắt buộc):** cùng fixture, ONNX engine trả text khớp paddle engine → otherwise fail.

**Nấc 2c — DirectML fallback**

- Cùng bộ ONNX models + code 2b, chỉ đổi EP sang `onnxruntime-directml` (DX12, không cần cài CUDA/cuDNN, chạy mọi GPU Windows).
- EP chọn qua cấu hình/auto-detect (`cuda` thử trước → fallback `dml`) — nằm trong Python engine, không ra ngoài sidecar contract.

**Exit mỗi nấc:** probe output ghi vào report; warm median đo được (methodology như T6, máy idle).

### Phase 3 — Đo lại full gate + chốt

1. One-shot cold + warm N=5 (2 fixture), loop throughput 10 frame, CPU/RAM **+ VRAM** (GPU path) trong lúc loop.
2. Ghi section mới vào `Docs/performance.md` (giữ M2/M3 làm baseline so sánh).
3. **Acceptance:** warm ≤ 333 ms → PASS; VRAM < 1 GB (budget AGENTS.md); > → present số cho user chốt tradeoff. Không claim PASS không có số đo.

## 4. Perf gate & testing (xuyên suốt)

- Preflight idle ≤ 20% CPU trước mọi lần đo; disclosure background load trong doc.
- Commands: `dotnet build "ReTran Project.slnx"`; `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`; `uv run pytest tests -v` (từ `ReTran.OCR/`).
- Baseline test: C# **70** / Python **15** — không giảm test, không giảm assertion; skip-êm qua early return.
- Commit nhỏ theo từng task; stage đúng file của task.

## 5. Out of scope

- Translation (M4), App UI nối OCR loop, OBS export, Rust game hook.
- Gộp 2 stack JSON-RPC; gỡ interface 1-impl (kiến trúc giữ nguyên).
- EF Core/SQLite (chưa có gì, không thêm trong spec này).
- Refactor CSS/Bootstrap beyond gỡ link khi unused; xoá debug buttons MainPage.
- Quay lại mkldnn trên CPU path (workaround paddle#77340 vẫn đúng cho CPU fallback).

## 6. Risks

| Risk | Xử lý |
|---|---|
| Paddle GPU wheel không hỗ trợ `sm_120` | Đã plan fallback 2b/2c — ladder không blocked |
| ONNX engine viết tay sai postprocess | Parity test bắt buộc (text khớp paddle trên fixture) |
| `torch` là transitive của paddleocr | Ghi nhận, giữ lại — không ép gỡ làm sidecar chết |
| Gỡ Bootstrap còn usage sót | Rà class trước khi gỡ link; không refactor CSS |
| MAUI TFM đổi → NU1201 | Tests giữ windows TFM khớp App — verify build |
| Đo khi máy bận | Preflight hard gate: > 20% → dừng, không đo |
