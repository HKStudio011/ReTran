# OCR Models + Shared-Memory Transport — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** User chọn/tải/xoá được model OCR (PaddleOCR 3.7 + PP-OCRv6, v5 cho ngôn ngữ chưa phủ), ảnh OCR đi qua shared memory raw thay vì base64 JPEG.

**Architecture:** Sidecar Python sở hữu catalog + thư mục model (`PADDLE_PDX_CACHE_HOME`) và các RPC `ocr.models.*`; Core proxy RPC + gửi cặp det/rec trong `core.start`; ảnh OCR đi qua MMF `Local\ReTran.Ocr.V1` (Core ghi BGRA raw, sidecar đọc bằng `mmap`), JSON-RPC chỉ còn `{seq,w,h}`.

**Tech Stack:** Python 3.13 (uv), paddleocr ≥3.7 / paddlepaddle-gpu 3.4.0 (cu129); C# .NET 10 Core + MAUI App; JSON-RPC stdio; Windows named shared memory.

**Spec:** `Docs/superpowers/specs/2026-10-22-ocr-models-shm-http-overlay-design.md` (mục 6.1, 6.2, 6.5, D2/D3/D4/D7/D8)

## Global Constraints

- `paddleocr>=3.7.0`, `paddlepaddle-gpu==3.4.0` (index `paddle-cu129`), Python chạy qua `uv run` trong `ReTran.OCR/`.
- Thư mục model: `%LOCALAPPDATA%\ReTran\models`, truyền bằng env `PADDLE_PDX_CACHE_HOME` khi spawn sidecar (`SidecarProcess.StartAsync`).
- MMF tên `Local\ReTran.Ocr.V1`; payload BGRA32, không downscale, header có seqlock `seq`.
- **Không tải model ngầm**; `core.start` thiếu cặp đã cài → `-32603` message `ocr-model-missing`.
- Test: C# `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` (MTP, chạy full suite); Python `uv run pytest tests -v` (từ `ReTran.OCR/`).
- Doc comment song ngữ cho API C# public/protected.

## Review Focus

- Python `mmap` mở mapping tên `Local\...` do .NET tạo: nếu không mở được phải **fallback base64** và log rõ (Task 1 quyết định, Task 8 test fallback).
- Model tải dở (đứt mạng) không được tính `installed` (Task 4 test).
- Xoá model đang là cặp active → phải bị chặn (Task 4 test).
- `core.start` với cặp chưa cài → `-32603 ocr-model-missing`, app hiện hộp thoại (Task 6 + Task 7 test).
- Đọc frame khi writer ghi dở (seqlock) → retry ra frame nhất quán, không crash/không trả rác (Task 8 test hai phía).

---

### Task 1: Spike — xác nhận mmap C# ↔ Python trên `Local\ReTran.Ocr.V1`

**Files:**
- Create: `Tools/Perf/shm-probe/CSharpWriter/` (console tạm, ghi pattern + giữ mapping)
- Create: `Tools/Perf/shm-probe/read_probe.py` (đọc bằng `mmap`)

**Interfaces:**
- Produces: kết luận viết vào cuối task này trong plan/report: tên mapping Python mở được (`Local\ReTran.Ocr.V1` hay phải `ReTran.Ocr.V1`) + cách mở (`mmap.mmap(-1, size, tagname=..., access=ACCESS_READ)`), hoặc quyết định fallback.

- [ ] **Step 1: Viết C# writer tối giản** — `MemoryMappedFile.CreateOrOpen(@"Local\ReTran.Ocr.V1", 64)`, ghi `[0x4F,0x43,0x52,0x31]` + `int64 seq=7`, `Console.ReadLine()` để giữ mapping.
- [ ] **Step 2: Chạy writer nền, đọc bằng Python** — `uv run python Tools/Perf/shm-probe/read_probe.py`, in 4 byte + seq.
- [ ] **Step 3: Nếu không mở được** — thử tên không namespace (`ReTran.Ocr.V1`), ghi lại kết quả và chốt tên dùng cho Task 8.
- [ ] **Step 4: Ghi kết luận vào report + commit probe**

```bash
git add Tools/Perf/shm-probe
git commit -m "chore(tools): spike dotnet-python named shared memory"
```

---

### Task 2: Catalog model (Python)

**Files:**
- Create: `ReTran.OCR/src/retran_core/ocr/catalog.py`
- Test: `ReTran.OCR/tests/test_ocr_catalog.py`

**Interfaces:**
- Produces:
  ```python
  @dataclass(frozen=True)
  class ModelSpec:
      name: str
      kind: str          # "det" | "rec"
      langs: tuple[str, ...]
      size_mb: float
      group: str         # "v6" | "legacy"
  CATALOG: tuple[ModelSpec, ...]
  def spec(name: str) -> ModelSpec | None
  def recommended(src: str, dst: str) -> tuple[str, str]  # (det_name, rec_name)
  ```
  Nhóm `v6`: `PP-OCRv6_{tiny,small,medium}_{det,rec}` (98 ngôn ngữ zh/TC/en/ja + 46 Latin).
  Nhóm `legacy`: `korean_PP-OCRv5_mobile_rec` (ko), `cyrillic_PP-OCRv5_mobile_rec` (ru), `arabic_PP-OCRv5_mobile_rec`, `th_`, `ta_`, `te_`, `devanagari_`, `el_`, `eslav_`, `chinese_cht_PP-OCRv3_mobile_rec`.
- [ ] **Step 1: Test fail** — `test_ocr_catalog.py`: có đủ 3 tier v6 det/rec; `recommended("ko","vi")` → rec `korean_PP-OCRv5_mobile_rec`; `recommended("ru","vi")` → `cyrillic_...`; `recommended("auto","vi")` → cặp v6 small; mọi spec có `langs` khác rỗng, `kind` hợp lệ.
- [ ] **Step 2: Chạy** `uv run pytest tests/test_ocr_catalog.py -v` → FAIL (module chưa có).
- [ ] **Step 3: Viết catalog + `recommended`** (nguồn language theo `spec.lang`; `auto` → v6).
- [ ] **Step 4: Chạy lại** → PASS.
- [ ] **Step 5: Commit** `feat(ocr): add model catalog with v6 tiers and v5 language fallback`.

---

### Task 3: Engine nhận cặp det/rec

**Files:**
- Modify: `ReTran.OCR/src/retran_core/ocr/paddle_engine.py`
- Modify: `ReTran.OCR/src/retran_core/__main__.py` (dựng engine bằng cặp mặc định từ catalog thay vì `lang="en"`)
- Test: `ReTran.OCR/tests/test_perf_gate.py` (mở rộng `RecordingPaddleOCR`)

**Interfaces:**
- Produces: `PaddleOcrEngine(det_model: str, rec_model: str)`; `_create()` truyền `text_detection_model_name=det_model`, `text_recognition_model_name=rec_model`, bỏ `lang=`-based naming; giữ `lang` chỉ khi cần cảnh báo.
- [ ] **Step 1: Sửa test** — assert kwargs đúng cặp truyền vào.
- [ ] **Step 2: Chạy** `uv run pytest tests/test_perf_gate.py -v` → FAIL.
- [ ] **Step 3: Sửa engine.**
- [ ] **Step 4: Chạy lại** → PASS; thêm test `test_spot_uses_pinned_models`.
- [ ] **Step 5: Commit** `refactor(ocr): parameterize paddle engine by det/rec model names`.

---

### Task 4: Model manager sidecar + migrate từ `~/.paddlex`

**Files:**
- Create: `ReTran.OCR/src/retran_core/ocr/models.py`
- Modify: `ReTran.OCR/src/retran_core/__main__.py` (đăng ký handler)
- Test: `ReTran.OCR/tests/test_ocr_models.py`

**Interfaces:**
- Produces:
  ```python
  def model_cache_dir() -> Path            # env PADDLE_PDX_CACHE_HOME, default %LOCALAPPDATA%\ReTran\models
  class ModelManager:
      def __init__(self, cache_dir: Path, catalog=CATALOG, downloader=None)
      def list(self) -> dict               # {catalog:[{name,kind,langs,sizeMb,installed,inUse}], active:{det,rec}}
      def download(self, name: str) -> None      # chạy nền, tạo/xoá file .partial
      def delete(self, name: str) -> None        # raise ValueError nếu inUse
      def set_pair(self, det: str, rec: str) -> None
  def migrate_from_paddlex(cache_dir: Path, source: Path | None = None) -> list[str]
  def make_model_handlers(manager: ModelManager) -> dict[str, Handler]   # ocr.models.list/download/delete/set
  ```
  `installed` = thư mục `official_models/<name>` tồn tại, không rỗng, **không có** `.partial`.
- [ ] **Step 1: Test fail** (dùng `tmp_path` + `monkeypatch.setenv`): list trả catalog; download giả fail → không `installed`; download xong → `installed`; `delete` model inUse raise; `migrate_from_paddlex` copy đúng model thuộc catalog, bỏ model lạ, tạo marker `.migrated` và lần 2 không copy lại.
- [ ] **Step 2: Chạy** `uv run pytest tests/test_ocr_models.py -v` → FAIL.
- [ ] **Step 3: Cài đặt** (downloader injectable để test không cần mạng; mặc định gọi PaddleX qua `PaddleOCR(...)` với model name — hoặc `paddlex` downloader nếu có API công khai; chốt cách đơn giản chạy được).
- [ ] **Step 4: Chạy lại + full pytest** → PASS.
- [ ] **Step 5: Commit** `feat(ocr): sidecar model manager (list/download/delete/set) with paddlex migration`.

---

### Task 5: Nâng dependency lên 3.7 / PP-OCRv6

**Files:**
- Modify: `ReTran.OCR/pyproject.toml`, `ReTran.OCR/uv.lock`
- Modify: `ReTran.OCR/tests/test_paddle_integration.py` (dùng v6 tiny det/rec)
- Modify: `ReTran.OCR/src/retran_core/ocr/paddle_engine.py` (thử bỏ `enable_mkldnn=False`)

**Interfaces:**
- Consumes: Task 3 (engine nhận tên model).
- Produces: stack mới chạy được; ghi rõ trong commit/comment nếu `enable_mkldnn=False` phải giữ.
- [ ] **Step 1: Sửa pyproject** — `paddleocr>=3.7.0`, `paddlepaddle-gpu==3.4.0`, giữ index cu129.
- [ ] **Step 2: `uv sync`** (mạng) → xác nhận cài được trên Windows/cp313.
- [ ] **Step 3: Chạy full pytest** — kỳ vọng integration test tải model v6 tiny lần đầu; nếu lỗi oneDNN/PIR thì bật lại `enable_mkldnn=False` + comment lý do.
- [ ] **Step 4: Chạy `uv run pytest tests/test_paddle_integration.py -v`** → PASS (tìm được "HELLO").
- [ ] **Step 5: Commit** `chore(ocr): upgrade to paddleocr 3.7 + pp-ocrv6 tiny integration`.

---

### Task 6: Core — `core.start ocr` + RPC proxy + warmup

**Files:**
- Modify: `ReTran.Core/Serve/PipelineHost.cs`, `ReTran.Core/Program.cs`, `ReTran.Core/Ocr/OcrPipeline.cs` (chỉ thêm nếu cần), `ReTran.Core/OcrClient/SidecarProcess.cs` (env `PADDLE_PDX_CACHE_HOME`)
- Test: `ReTran.Tests/PipelineHostTests.cs`, `ReTran.Tests/OcrModelRpcTests.cs`

**Interfaces:**
- Produces:
  - `core.start` params thêm `ocr: {det, rec}` (bắt buộc nếu muốn OCR chạy) → `StartSpec.DetModel`, `StartSpec.RecModel`.
  - Lỗi: `RpcMethodException(RpcErrorCodes.InternalError, "ocr-model-missing: <det>/<rec>")` khi sidecar báo chưa cài.
  - RPC mới: `ocr.models.list` (params null), `ocr.models.download {name}`, `ocr.models.delete {name}`, `ocr.models.set {det, rec}` — proxy thẳng `ISidecarRpc`.
  - Bỏ warmup dummy trong `Program.cs` khi chưa có cặp model; warm sau `core.start` (fire-and-forget 1 spot ảnh nhỏ) hoặc sau `ocr.models.set`.
- [ ] **Step 1: Test fail** — `PipelineHostTests`: start có `ocr` → sidecar nhận `ocr.models.set` đúng cặp trước vòng đầu; start thiếu → ném `-32603` chứa `ocr-model-missing`; `OcrModelRpcTests` proxy 4 RPC (fake sidecar ghi lại method/params).
- [ ] **Step 2: Chạy suite** → FAIL.
- [ ] **Step 3: Cài đặt.**
- [ ] **Step 4: Chạy suite** → PASS.
- [ ] **Step 5: Commit** `feat(core): ocr model pair in core.start + ocr.models.* proxy`.

---

### Task 7: App — settings + UI model manager + first-run

**Files:**
- Modify: `ReTran.App/Services/AppSettingsService.cs`, `ReTran.App/Core/PipelineController.cs`, `ReTran.App/Components/Shared/SettingsPanel.razor`, `ReTran.App/Components/Pages/Home.razor`, `ReTran.App/Services/I18nStrings.cs`
- Test: `ReTran.Tests/AppSettingsTests.cs` (hoặc file hiện có), `ReTran.Tests/PipelineControllerTests.cs`

**Interfaces:**
- Produces:
  - `AppSettings` thêm `string OcrDet = "PP-OCRv6_small_det"`, `string OcrRec = "PP-OCRv6_small_rec"`.
  - `PipelineController`: `StartAsync` gửi `["ocr"] = {det, rec}`; expose `OcrModelMissing` (bool) khi Core trả lỗi `ocr-model-missing`; `Task<IReadOnlyList<OcrModelInfo>> ListOcrModelsAsync(ct)`, `Task DownloadOcrModelAsync(name, ct)`, `Task DeleteOcrModelAsync(name, ct)`, `Task SetOcrModelPairAsync(det, rec, ct)`.
  - UI: mục OCR trong `SettingsPanel` (bảng + Tải/Xoá/Chọn, hiện model đang dùng, nhãn ngôn ngữ), hộp thoại first-run trong `Home` khi `ListOcrModelsAsync` không có model `installed`.
- [ ] **Step 1: Test fail** — AppSettings round-trip giữ `OcrDet/OcrRec`; PipelineController gửi `ocr` trong params; lỗi `ocr-model-missing` set cờ.
- [ ] **Step 2: Chạy suite** → FAIL.
- [ ] **Step 3: Cài đặt + UI + i18n (vi/en).**
- [ ] **Step 4: Chạy suite + checklist tay** — fresh profile (xoá `%LOCALAPPDATA%\ReTran\models`) → hộp thoại first-run; tải small; đổi sang tiny; xoá tiny (bị chặn nếu đang dùng).
- [ ] **Step 5: Commit** `feat(app): ocr model manager ui + first-run picker`.

---

### Task 8: Ảnh OCR qua shared memory raw

**Files:**
- Create: `ReTran.Core/Ocr/OcrShm.cs`; `ReTran.OCR/src/retran_core/ocr/shm.py`
- Modify: `ReTran.Core/Cli/OcrLoop.cs` (ghi shm + `SpotAsync(seq,w,h)`), `ReTran.Core/Ocr/OcrPipeline.cs` (overload mới), `ReTran.OCR/src/retran_core/ocr/handlers.py` (`ocr.spot` nhận `{seq,w,h}` hoặc `{image}` fallback), `ReTran.OCR/src/retran_core/__main__.py`
- Test: `ReTran.Tests/OcrShmTests.cs`, `ReTran.OCR/tests/test_ocr_shm.py`, cập nhật `test_ocr_handlers.py`

**Interfaces:**
- Produces:
  - C#: `sealed class OcrShmWriter : IDisposable { OcrShmWriter(string name); long Publish(Frame frame); }` — header 64 B: `magic 'RTOC'`, `version=1`, `seq` (int64, lẻ khi đang ghi), `width`, `height`, `stride`, `format=1 (BGRA32)`.
  - Python: `class ShmReader: def __init__(self, name: str); def read(self, seq: int) -> np.ndarray` (BGRA HxWx4, seqlock retry tối đa 100 lần, raise `ShmUnavailable`).
  - `ocr.spot` params: `{"seq": n, "width": w, "height": h}` → đọc shm; `{"image": b64}` → đường cũ (fallback).
  - `OcrPipeline.SpotAsync(long seq, int width, int height, CancellationToken ct = default)`.
- [ ] **Step 1: Test fail** — C#: header round-trip + `Publish` trả seq tăng; Python: reader đọc đúng frame do writer Python ghi mô phỏng + retry khi seq đổi giữa chừng; handler với `{seq,w,h}` dùng reader giả, và `{image}` vẫn chạy như cũ.
- [ ] **Step 2: Chạy** C# suite + `uv run pytest tests/test_ocr_shm.py tests/test_ocr_handlers.py -v` → FAIL.
- [ ] **Step 3: Cài đặt cả hai phía + nối `OcrLoop`** (dùng tên mapping Task 1 xác nhận; nếu Task 1 thất bại → giữ base64 và dừng task này, ghi lý do).
- [ ] **Step 4: Chạy lại** tất cả → PASS; kiểm tra tay: `rpc_MBps` của ocr.spot ≈ 0 khi pipeline chạy.
- [ ] **Step 5: Commit** `feat(ocr): raw frame transport over shared memory`.

---

### Task 9: Benchmark A/B v5 ↔ v6 + cập nhật docs

**Files:**
- Create: `Tools/Perf/ocrbench/bench.py` (chạy `make_spot_handler` trên ≥20 ảnh PNG lưu sẵn, in spot count/text/time)
- Modify: `Docs/performance.md` (mục mới: OCR v6 A/B + budget)
- Modify: `AGENTS.md` nếu cần ghi stack mới (paddleocr 3.7/PP-OCRv6)

**Interfaces:**
- Consumes: Task 5 + Task 8.
- [ ] **Step 1: Lưu bộ ảnh test** — 20 khung PNG 1920×1080 từ capture thật (dùng `ReTran.Core capture window/screen --out`).
- [ ] **Step 2: Chạy bench v5** (checkout model v5 trước) và **v6** (sau), ghi CSV.
- [ ] **Step 3: Ghi kết quả vào `Docs/performance.md`** — số spot/khung, thời gian/chu kỳ, VRAM/RAM, verdict budget.
- [ ] **Step 4: Chạy full suite hai phía** → PASS.
- [ ] **Step 5: Commit** `docs(perf): pp-ocrv6 a/b vs v5 + budget verdict`.
