# ReTran — Performance Budget Measurements

Mỗi milestone đo với game/thiết bị thật và ghi số vào đây.
Hard budget (spec §7 / AGENTS.md): capture < 2% CPU; OCR ~3 fps, VRAM < 1GB; UI 10–15 fps; OBS ~10 fps; tổng < 8% CPU + < 500 MB RAM.

Tất cả số dưới đây **đo thật trên máy này** (2026-09-23), không ước lượng. Lệnh đo và output thô ghi ở cuối mỗi phần.

---

## M2 OCR baseline (2026-09-23)

### Máy đo

| Thành phần | Giá trị | Nguồn |
|---|---|---|
| CPU | Intel Core i5-13400F, 10 cores / 16 logical processors | `Get-CimInstance Win32_Processor` |
| RAM | 68.475.105.280 bytes (~63,8 GiB) | `Get-CimInstance Win32_ComputerSystem.TotalPhysicalMemory` |
| GPU | NVIDIA GeForce RTX 5060 Ti — **không dùng trong measurement này** | `Get-CimInstance Win32_VideoController` |

- **Fixture chính:** `1920×1080` PNG, 3 dòng text, tạo bằng script PIL đúng theo brief Task 7 (lưu `%TEMP%\m2-perf-1080p.png`). Đây là fixture đo chính.
- **Fixture phụ (anchor so sánh):** `640×96` PNG "HELLO RETRAN" (font PIL mặc định, chữ nhỏ) — tạo thêm để đối chiếu với fixture 640×96 của T5.
- **Backend:** `paddlepaddle 3.3.1` **CPU build** — xác minh thật: `paddle.is_compiled_with_cuda() → False`, `paddle.device.get_device() → "cpu"`; `paddleocr 3.4.1`, PP-OCR mặc định, `lang=en`, `enable_mkldnn=False` (workaround PaddlePaddle/Paddle#77340 trong `paddle_engine.py`).
- **Phương pháp:** script driver `%TEMP%\m2-perf.py` (subprocess sidecar `python -m retran_core serve`, JSON-RPC stdio): 1 ping warm-up + 1 cold `ocr.spot` + **N=5 warm** `ocr.spot` liên tiếp; **RSS/CPU đo bằng `psutil` trên TOÀN BỘ process tree sidecar** (`Process.memory_info().rss/peak_wset`, `cpu_times` delta / wall) — vì sidecar chạy dạng **2 process** (parent ~4,6 MB proxy + worker cùng cmdline `python.exe -m retran_core serve` làm toàn bộ OCR); CPU nền máy đo bằng `psutil.cpu_percent`.

### Bối cảnh đo (background load — quan trọng khi đọc số)

Máy **không idle** trong toàn bộ window đo. Quy trình khác của user đang chạy (không bị tắt): `ComfyUI setup.py install` (nhiều `cicc.exe` compile), `Hermes`, `vmmemWSL`, `Brave`, `laya-mcp`, `unsloth studio`. CPU nền đo được:

| Timepoint | Machine CPU % |
|---|---|
| Trước run fixture nhỏ | 95,2% |
| Sau run fixture nhỏ | 77,5% |
| Trước run 1080p | 60,8% |
| Trong spot-loop 1080p (start → end) | 72,9% → 82,5% |
| Sau run 1080p | 96,7% |

→ **Wall-clock latency dưới đây bị kéo dài bởi contention; coi như upper-bound-ish.** CPU-seconds của process (ít nhạy contention hơn) ghi kèm. Sample top consumer (4s, không chạy OCR): `python(laya-mcp) ~134%`, `OpenCode ~96%`, `vmmemWSL ~84%`, `Hermes ~77%+54%`, `Brave ~45%`, nhiều `cicc.exe ~32–37%`.

### Số liệu — fixture 1920×1080 (chính)

| Metric | Giá trị | Ghi chú |
|---|---|---|
| Serve + sidecar ping (spawn → pong) | **0,667 s** | process→ready 0,676 s |
| RSS sidecar (tree) idle trước OCR | **37,1 MB** | parent 4,6 + worker 32,5 MB; `psutil` |
| CPU sidecar idle (2 s sample) | **0,0%** (1 core) | `cpu_times` delta / 2 s |
| Lần gọi đầu (cold: engine init + load model đã cache) | **57,985 s** | sidecar `elapsed_ms = 57983` |
| Warm 1 | **35,467 s** | |
| Warm 2 | **33,477 s** | |
| Warm 3 | **36,689 s** | |
| Warm 4 | **35,432 s** | |
| Warm 5 | **55,727 s** | |
| **Warm min / median / mean / max** (N=5) | **33,477 / 35,467 / 39,358 / 55,727 s** | median dùng cho fps ceiling |
| Spot-loop CPU (tree, trong 5 warm) | **519,5% / 1 core** ≈ 5,2 core ≈ **32,5% tổng 16 luồng** | 1022,4 CPU-s / 196,8 s wall |
| RSS sidecar (tree) sau warm | **394,9 MB** | steady state |
| **Peak working set sidecar (tree)** | **11.309,7 MB (~11,0 GiB)** | sum `peak_wset` — worker đạt 11.280 MB sau cold |
| Số spots fixture | **3** | texts: `fubtitle line number three...`, `HELLO RETRAN - M2 OCR PERF TEST`, `ne quick brown fox...` (2 lỗi char đầu — chất lượng rec, không phải perf) |
| **FPS ceiling (từ median warm)** | **0,028 fps** (≈ 1 frame / 35,5 s) | 1 / 35,467 s |
| VRAM | **0** | paddle CPU build — xác minh ở trên |

### Số liệu — fixture phụ 640×96 (anchor; T5 dùng cùng kích thước)

| Metric | Giá trị | Ghi chú |
|---|---|---|
| Serve + sidecar ping | **0,376 s** | process→ready 0,389 s |
| RSS idle (tree) / CPU idle | **36,9 MB / 0,0%** | |
| Cold call | **14,379 s** | `elapsed_ms = 14378` |
| Warm min / median / mean / max (N=5) | **2,010 / 3,003 / 3,014 / 3,809 s** | các lượt: 2,010 · 2,770 · 3,480 · 3,809 · 3,003 |
| Spot-loop CPU (tree) | **690,8% / 1 core** | 104,1 CPU-s / 15,07 s wall |
| RSS tree sau warm / peak WSS tree | **619,1 MB / 934,6 MB** | |
| Số spots | **0** | font PIL mặc định quá nhỏ → det không thấy text; timing vẫn hợp lệ (det+rec đã chạy) |
| FPS ceiling (median) | **0,333 fps** | 1 / 3,003 s |

### CLI one-shot — `ReTran.Core ocr <png>` (process → first result, end-to-end)

Mỗi lần CLI là cold (spawn sidecar mới + init model + network model-source check — thấy trong stderr: `Checking connectivity to the model hosters`).

| Run | Fixture | Wall | Exit | Kết quả |
|---|---|---|---|---|
| 1 | 1920×1080 | 30,466 s | **1** | `ocr failed: ocr.spot timed out after 30s (includes first-time model load).` |
| 2 | 1920×1080 | 30,362 s | **1** | cùng lỗi timeout |
| 3 | 1920×1080 | 30,300 s | **1** | cùng lỗi timeout (stderr xác nhận) |
| 4 | 640×96 | 19,028 s | 0 | pass |
| 5 | 640×96 | 18,186 s | 0 | pass |
| 6 | 640×96 | (stdout `elapsed_ms = 18199`) | 0 | `{"spots":[],"elapsed_ms":18199,"new_texts":[]}` |

**Cold 1080p (~58 s measured) vượt `OcrPipeline.Timeout = 30 s` → CLI 1080p fail (exit 1).** Fixture nhỏ pass (~18–19 s < 30 s).

### Đối chiếu budget (AGENTS.md)

| Budget row | Budget | Measured (M2) | Verdict |
|---|---|---|---|
| Capture CPU | < 2% CPU | chưa có capture trong M2 | **N/A** — đo ở milestone có capture loop |
| OCR throughput | ~3 fps (≤ ~333 ms/lượt) | 1080p median **35,467 ms (35,5 s) → 0,028 fps** (vượt ~106×); 640×96 median **3,003 ms → 0,33 fps** (vượt ~9×) | **BREACH** |
| OCR VRAM | < 1 GB | **0** (paddle CPU, `cuda_compiled=False`) | **PASS** |
| UI/overlay update | 10–15 fps | chưa có UI loop trong M2 | **N/A** |
| OBS export | ~10 fps | chưa có OBS trong M2 | **N/A** |
| Translation | diff-based, không block pipeline | chưa có translation trong M2 | **N/A** |
| Tổng CPU (khi gaming+streaming) | < 8% | idle sidecar **0,0%**; spot-loop đang chạy **~32,5% tổng CPU** (5,2 core); M2 chưa có pipeline loop liên tục | idle **PASS** / active **BREACH** nếu sustain — chốt lại khi có loop thật |
| Tổng RAM | < 500 MB | steady 1080p **394,9 MB** (PASS); **peak 11.309,7 MB** (1080p) & **934,6 MB** (640×96) | steady 1080p PASS / **peak BREACH**; 640×96 steady+và peak BREACH |

### Ghi chú / hạn chế đo (đọc trước khi dùng số)

1. **Background load 60–96% CPU** (bảng trên) suốt thời gian đo — wall-latency không phải quiet-machine baseline. Không tắt được process của user (measurement-only). CPU-seconds của process đã ghi kèm để giảm nhạy contention.
2. **Peak WSS ~11 GiB** là số đo thật (`peak_wset` Windows) — bất thường so với OCR 1080p; nguyên nhân gốc không điều tra được trong scope measurement-only (không sửa code). Steady-state chỉ ~395 MB.
3. **Enable_mkldnn=False** (crash workaround paddle#77340) + stderr CLI cho thấy init load cả `PP-OCRv5_server_det` (model server-size!) + doc-preproc models (`PP-LCNet_x1_0_doc_ori`, `UVDoc`, `PP-LCNet_x1_0_textline_ori`) + `en_PP-OCRv5_mobile_rec` — giải thích phần nào latency/RAM CPU-path.
4. **Sidecar là 2 process** (parent proxy ~4,6 MB + worker cùng cmdline làm OCR, tồn tại ngay sau ping) — footprint luôn đo cả tree; root cause process con không điều tra (scope measurement-only).
5. **Không reproduce "warm ~sub-second" của T5:** fixture T5 là 640×96 (Arial 48pt) và T5 chạy trong điều kiện load khác; tại thời điểm đo này fixture 640×96 median đã 3,0 s (máy 77–95% CPU nền), 1080p median 35,5 s. Số hiện tại là baseline mới, ghi nhận trung thực.
6. **Model cache:** stderr xác nhận `Model files already exist. Using cached files.` — cold time KHÔNG tính download; nhưng mỗi cold init có network `Checking connectivity to the model hosters` (chưa set `PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK`).
7. Chạy đầu tiên script N=10 bị timeout shell 180 s (dưới load) — **bị loại, không dùng làm số**; số chính thức là run N=5 phía trên.

### Lệnh đo (reproduce)

```powershell
# Fixture 1080p: uv run python %TEMP%\m2-make-fixture.py   (PIL, 3 dòng text — đúng script brief)
# Fixture 640x96: uv run python %TEMP%\m2-small-fixture.py
# Driver: (workdir ReTran.OCR/)
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:TEMP\m2-perf-1080p.png"))
uv run --with psutil "$env:TEMP\m2-perf.py" "D:\Code\ReTran Project" $b64 5
# CLI one-shot:
Measure-Command { & "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe" ocr "$env:TEMP\m2-perf-1080p.png" }
# CPU paddle xác minh:
uv run python -c "import paddle; print(paddle.is_compiled_with_cuda(), paddle.device.get_device())"
# Machine info:
Get-CimInstance Win32_Processor; Get-CimInstance Win32_ComputerSystem; Get-CimInstance Win32_VideoController
```

---

## M1 capture baseline

(chưa đo — ghi tại milestone có capture loop chạy thật)
