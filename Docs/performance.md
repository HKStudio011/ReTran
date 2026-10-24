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

## M3 frame loop + perf gate (2026-09-26)

### Máy đo

| Thành phần | Giá trị | Nguồn |
|---|---|---|
| CPU | Intel Core i5-13400F, 10 cores / 16 logical processors | `Get-CimInstance Win32_Processor` |
| RAM | 68.475.105.280 bytes (~63,8 GiB) | `Get-CimInstance Win32_ComputerSystem.TotalPhysicalMemory` |
| GPU | NVIDIA GeForce RTX 5060 Ti — **không dùng trong measurement này** | `Get-CimInstance Win32_VideoController` |

- **Perf gate T2/T3 applied:** pin `PP-OCRv5_mobile_det` + `en_PP-OCRv5_mobile_rec` (bỏ doc-preprocess pipelines), downscale long-side **1280 px** trước detect (Python-side trong `decode_image_b64` — tên lúc đo là `decode_png_b64`, T4 (2026-10-21) đã đổi để nhận JPEG **hoặc** PNG — áp cho cả one-shot lẫn loop). Paddle **CPU build** → VRAM 0.
- **Fixture:** `%TEMP%\m3-perf-1080p.png` (1920×1080, Arial 64pt, 2 dòng) + `%TEMP%\m3-perf-640x96.png` (640×96, Arial 48pt) — tạo bằng script PIL đúng brief Step 3.
- **Nguồn loop đo được:** cửa sổ Notepad (`--hwnd`) chứa text fixture — vì `--monitor 0` fail (xem disclosure ở mục loop).

### Bối cảnh đo (background load — quan trọng khi đọc số)

Máy **không idle** trong toàn bộ window đo (user quyết định 2026-09-26: đo ngay, không tắt app — STOP gate đã gỡ). CPU nền (`Get-Counter '\Processor(_Total)\% Processor Time'`, 3 samples × 2s):

| Timepoint | Machine CPU % |
|---|---|
| Preflight lần đầu (13:51:17) | 54,59% |
| Trước đo — Step 2 (14:27:39) | **42,97%** |
| Sau toàn bộ đo (14:48:24) | 56,63% |

→ Wall-clock latency chịu contention (coi như upper-bound-ish). Attribution CPU dùng **per-process** ở mục "CPU / RAM" (phần contribution của pipeline, không phải số máy idle). Mọi verdict wall/RAM là **provisional**.

### Số liệu — one-shot (cold + warm N=5, hai fixture)

| Fixture | Cold (s) | Warm min | Warm median | Warm mean | Ghi chú |
|---|---|---|---|---|---|
| 1920×1080 | **14,72** (raw 14,7238413) | 8833 ms | **8951 ms** | 9363,4 ms | raw: 8991 · 11206 · 8836 · 8951 · 8833 |
| 640×96 | **7,43** (raw 7,4286935) | 7088 ms | **7456 ms** | 7470,8 ms | raw: 7542 · 7456 · 7092 · 7088 · 8176 |

- **Cold** = `Measure-Command` wall: exe spawn + sidecar spawn + net model-source check + model load + inference. **Warm** = `elapsed_ms` sidecar báo lại (decode + 1280 downscale + `engine.spot`).
- **Không chạy warm-up riêng** — model đã cache (stderr xác nhận `Model files already exist. Using cached files.`), nhưng mỗi run vẫn có network `Checking connectivity to the model hosters`.
- **Mỗi lần one-shot là sidecar/process mới** → `elapsed_ms` "warm" vẫn gồm first-call model init trong process đó; số steady-state thật chỉ có ở loop (frames 2–10).
- **So M2:** cold 1080p M2 = 30,4–30,5 s → CLI **timeout fail** (exit 1) → M3 **14,72 s pass**; 640×96 M2 wall 18–19 s → M3 **7,43 s**. Warm median M2 (steady, cùng sidecar) 1080p 35,467 s → M3 loop steady 8983 ms ≈ **4× nhanh hơn** (tham khảo — content/load khác nhau).

### Số liệu — loop throughput

Nguồn dùng được: **`--hwnd` (cửa sổ Notepad, fixture text)**. Cả 2 nguồn đều fail khi chạy **vanilla** (không workaround):

- `--monitor 0`: `timed out after 5s waiting for first frame.` (lệnh `capture screen` chi tiết hơn: `timed out after 5s waiting for the first frame on monitor 0 (on HDR displays the DXGI source skips non-BGRA frames by design - try an SDR monitor or 'capture window').`) — đúng lỗi HDR đã thấy ở M2.
- `--hwnd` (vanilla): sidecar crash giữa loop → `sidecar lost: sidecar exited`; root cause trong stderr: `UnicodeEncodeError: 'charmap' codec can't encode character '\u2261' in position 1929: character maps to <undefined>` — `jsonrpc.py` `print(out)` với `ensure_ascii=False` ghi ra stdout cp1252, payload chứa `≡` (icon menu hamburger của Win11 Notepad bị OCR đúng). **Không sửa code (measurement-only)** → workaround env `PYTHONUTF8=1` (chỉ đổi encoding stdout sidecar, không đổi logic OCR/capture).

Với `PYTHONUTF8=1` + `ocr loop --hwnd 0x3E1E78 --max-frames 10` → **exit 0, 10/10 frame**:

| Metric | Giá trị |
|---|---|
| wall | **100,08 s** |
| fps đạt được | **0,1 fps** (10 / 100,08 s) |
| `elapsed_ms` 10 frame | 15817 · 8377 · 8686 · 9071 · 12448 · 8546 · 9309 · 8983 · 9137 · 8798 |
| median (10 frame) | **9027 ms** |
| median frames 2–10 (steady; frame 1 = 15817 ms gồm model init) | **8983 ms** |

### CPU / RAM (trong lúc loop)

`ocr loop --hwnd 0x3E1E78 --fps 3 --max-frames 30` chạy nền (`Start-Job`, exe path tuyệt đối, `PYTHONUTF8=1`), sample **8 s** bằng `uv run --with psutil` (workdir `ReTran.OCR/`, `Start-Sleep 2` trước khi scan). `cpu_pct` = average per-process trong cửa sổ 8 s ≈ t+3 s … t+11 s của loop = **giai đoạn model-init + early frames** (không phải average cả 30-frame run). Filter cmdline `ReTran.Core`/`retran_core` — 3 dòng self-match (chính tool đo) đã loại khỏi số pipeline.

| PID | Name | cpu_pct | rss_mb | peak_mb | Vai trò |
|---|---|---|---|---|---|
| 12572 | ReTran.Core.exe | 5,0 | 79,3 | 91,4 | **core** (capture + PNG encode + RPC) |
| 45204 | python.exe | 258,2 | 542,8 | 689,9 | **sidecar worker** (làm OCR) |
| 1332 | python.exe | 0,0 | 4,6 | 4,6 | python cùng cmdline `-m retran_core serve` (parent proxy) |
| 41240 | python.exe | 0,0 | 4,6 | 4,6 | python cùng cmdline `-m retran_core serve` (thứ 3, xuất hiện sau ~6 s — helper, 0% CPU) |
| 10664 | python.exe | 0,6 | 21,7 | 21,7 | *self-match* — chính script psutil (cmdline chứa `retran_core`) |
| 50264 | uv.exe | 0,0 | 24,2 | 24,2 | *self-match* — `uv run` (cmdline chứa `retran_core`) |
| 52488 | pwsh.exe | 3,7 | 94,8 | 94,9 | *self-match* — shell chạy lệnh đo |

- **Pipeline (core + sidecar, loại self-match):** CPU **263,2%** (5,0 + 258,2 + 0,0 + 0,0) ≈ **16,5% tổng 16 luồng**; RSS sum **631,3 MB**; tree peak sum **790,5 MB**.
- Attribution xác minh bằng tree probe (run 5 frame): `ReTran.Core.exe (49464) → python -m retran_core serve (34752) → python cùng cmdline (44496, worker)`.
- Method note: `peak_mb` = `peak_wset` per-process; **sum theo tree = caveat M2** (không tách được peak thật từng tầng). Không đo được RAM idle riêng (sidecar chỉ tồn tại trong lúc loop).

### Đối chiếu budget (AGENTS.md)

| Row | M2 baseline | M3 measured | Budget | Verdict |
|---|---|---|---|---|
| Capture CPU | N/A (M2 chưa có capture) | core tổng **5,0%** (không tách được capture riêng) | < 2% | **provisional BREACH (core tổng, capture chưa tách)** |
| OCR throughput | 1080p median 35,467 s → 0,028 fps; 640×96 3,003 s → 0,33 fps | loop median **9027 ms** → ~0,11 fps (từ median) / 0,1 fps (từ wall); one-shot warm median 8951 / 7456 ms | ≤ ~333 ms (~3 fps) | **BREACH ~27×** (provisional) |
| OCR VRAM | 0 (paddle CPU build) | **0** (cùng CPU build) | < 1 GB | **PASS** |
| UI/overlay update | N/A | N/A | 10–15 fps | **N/A** |
| OBS export | N/A | N/A | ~10 fps | **N/A** |
| Translation | N/A | N/A | diff-based, không block pipeline | **N/A** |
| Tổng CPU (khi gaming+streaming) | idle 0,0%; active ~32,5% tổng | loop: core+sidecar **263,2% ≈ 16,5% tổng 16 luồng** | < 8% | **BREACH** (provisional) |
| Tổng RAM | steady 394,9 MB PASS; peak 11.309,7 MB BREACH | active RSS **631,3 MB**; peak tree sum **790,5 MB** | < 500 MB | **BREACH** (RSS + peak; provisional) |

### Ghi chú / hạn chế đo (đọc trước khi dùng số)

1. **Background 42,97–56,63% CPU** (bảng Bối cảnh) suốt window đo → wall-clock = upper-bound-ish; verdict **provisional**. Attribution per-process ở trên tách rõ contribution pipeline (core 5,0% + worker 258,2%).
2. **Loop vanilla fail cả 2 nguồn:** `--monitor 0` = HDR first-frame timeout (verbatim ở trên); `--hwnd` = sidecar crash `UnicodeEncodeError '≡'` (bug thật: stdout cp1252 vs `ensure_ascii=False`; `≡` = icon menu Notepad bị OCR đúng). Số loop lấy với workaround env **`PYTHONUTF8=1`** — disclosed, không sửa code.
3. **Trong quá trình đo có 1 lần gõ nhầm hex HWND** (`0xF11B6` ≠ handle thật `0xF11A6`) → first-frame timeout; đã chạy lại với handle đúng. Không dùng số của lần chạy nhầm.
4. **One-shot ≠ steady-state:** mỗi run spawn sidecar mới → `elapsed_ms` gồm first-call model init + net model-source check. Steady-state thật = loop frames 2–10 (**median 8983 ms**). Không tách download (model đã cache).
5. **Sample CPU/RAM 8 s đầu loop** (model-init + early frames), không phải average cả 30-frame run. `peak_mb` = per-process `peak_wset`, sum tree = caveat M2. Filter cmdline self-match (uv/pwsh/script) đã loại.
6. **Loop source = cửa sổ Notepad 1920×1080** (input downscale 1280 theo gate; nội dung gồm chrome/toolbar/icons của Notepad) — đo throughput, không so trực tiếp spot-count với fixture PNG.

### Lệnh reproduce

```powershell
# Step 1 — build
dotnet build "ReTran Project.slnx"

# Step 2 — background CPU snapshot (confounder, không STOP)
$samples = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 3).CounterSamples | ForEach-Object { $_.CookedValue }
"bg CPU avg: $(($samples | Measure-Object -Average).Average) %"

# Step 3 — fixtures (workdir ReTran.OCR/, uv context có PIL)
uv run python -c @"
import os
from PIL import Image, ImageDraw, ImageFont
t = os.environ['TEMP']
im = Image.new('RGB', (1920, 1080), 'white')
d = ImageDraw.Draw(im)
f = ImageFont.truetype('arial.ttf', 64)
d.text((40, 40), 'HELLO RETRAN M3 PERF TEST', fill='black', font=f)
d.text((40, 160), 'The quick brown fox jumps over the lazy dog.', fill='black', font=f)
im.save(os.path.join(t, 'm3-perf-1080p.png'))
im2 = Image.new('RGB', (640, 96), 'white')
d2 = ImageDraw.Draw(im2)
d2.text((10, 20), 'HELLO RETRAN - M2 OCR PERF TEST', fill='black', font=ImageFont.truetype('arial.ttf', 48))
im2.save(os.path.join(t, 'm3-perf-640x96.png'))
print('fixtures at', t)
"@

# Step 4 — one-shot cold + warm N=5 (workdir repo root)
$exe = "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe"
(Measure-Command { & $exe ocr "$env:TEMP\m3-perf-1080p.png" | Out-Null }).TotalSeconds
(Measure-Command { & $exe ocr "$env:TEMP\m3-perf-640x96.png" | Out-Null }).TotalSeconds
1..5 | ForEach-Object { (& $exe ocr "$env:TEMP\m3-perf-1080p.png" | ConvertFrom-Json).elapsed_ms }
1..5 | ForEach-Object { (& $exe ocr "$env:TEMP\m3-perf-640x96.png" | ConvertFrom-Json).elapsed_ms }

# Step 5 — loop throughput (vanilla: --monitor 0 fail HDR; --hwnd vanilla crash cp1252)
# workaround: PYTHONUTF8=1 + HWND Notepad chứa text fixture (Get-Process notepad → MainWindowHandle → hex)
# NOTE: 0x3E1E78 là handle CỦA SESSION ĐO CŨ — session-specific; derive lại theo dòng trên, đừng copy máy móc.
$env:PYTHONUTF8 = '1'
$sw = [Diagnostics.Stopwatch]::StartNew()
$out = & $exe ocr loop --hwnd 0x3E1E78 --max-frames 10
$sw.Stop()
"wall_s: $([math]::Round($sw.Elapsed.TotalSeconds,2))  achieved_fps: $([math]::Round(10/$sw.Elapsed.TotalSeconds,2))"
$out | ForEach-Object { ($_ | ConvertFrom-Json).elapsed_ms }

# Step 6 — CPU/RAM trong lúc loop (PYTHONUTF8=1; uv từ ReTran.OCR/, exe path tuyệt đối)
# Chạy standalone: tự set PYTHONUTF8 ở dưới (đừng phụ thuộc Step 5).
# 0x3E1E78 là handle session đo cũ — derive lại như Step 5 (Get-Process notepad → MainWindowHandle → hex);
# exe path tuyệt đối cũng là của máy đo — sửa lại theo máy mình.
$env:PYTHONUTF8 = '1'
$job = Start-Job -ScriptBlock { param($x) & $x ocr loop --hwnd 0x3E1E78 --fps 3 --max-frames 30 } `
  -ArgumentList 'D:\Code\ReTran Project\ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe'
Start-Sleep -Seconds 2
uv run --with psutil python -c @"
import psutil, time, json
procs = []
for p in psutil.process_iter(['name', 'cmdline']):
    try:
        name = p.info['name'] or ''
        cmd = ' '.join(p.info['cmdline'] or [])
        if 'ReTran.Core' in name or 'retran_core' in cmd:
            p.cpu_percent(None)
            procs.append(p)
    except psutil.Error:
        pass
time.sleep(8)
rows = [{'name': p.info['name'], 'pid': p.pid, 'cpu_pct': p.cpu_percent(None),
         'rss_mb': round(p.memory_info().rss / 2**20, 1),
         'peak_mb': round((p.memory_info().peak_wset or 0) / 2**20, 1)}
        for p in procs if p.is_running()]
print(json.dumps(rows, indent=2))
"@
Wait-Job $job | Receive-Job; Remove-Job $job -Force

# Machine info
Get-CimInstance Win32_Processor; Get-CimInstance Win32_ComputerSystem; Get-CimInstance Win32_VideoController
```

---

## M4 translation (2026-10-07)

### Máy đo

| Thành phần | Giá trị | Nguồn |
|---|---|---|
| CPU | Intel Core i5-13400F, 10 cores / 16 logical processors | `Get-CimInstance Win32_Processor` |
| RAM | 68.475.105.280 bytes (~63,8 GiB) | `Get-CimInstance Win32_ComputerSystem.TotalPhysicalMemory` |
| GPU | NVIDIA GeForce RTX 5060 Ti — **không dùng trong measurement này** | `Get-CimInstance Win32_VideoController` |

Backend giống M2/M3: `paddlepaddle` **CPU build** → VRAM 0. Không có GPU path trong M4 (chưa làm).

**Nguồn capture dùng được để đo:** `--hwnd` (cửa sổ thật). `screen`/`monitor 0` **không** dùng được trên máy này — DXGI không frame nào (đúng lỗi HDR đã ghi ở M3): `core.start` vẫn trả `{"running":true}`, loop chết sau ~5 s với `pipeline.state {"running":false,"reason":"capture-error"}`. Vì vậy test integration `ServeM4IntegrationTests.Serve_Start_EmitsFrameTranslated_AndRequestsCache_SkipEam` (ghim `screen`/monitor 0 theo spec) **skip-êm trên máy này**; đường `frame.translated` + `translate.request_cache` được verify bằng probe `--hwnd` (bên dưới) và bằng chính test đó khi tạm trỏ sang nguồn cửa sổ.

### Bối cảnh đo (background load)

Máy **không idle** trong window đo (không tắt được process của user — measurement-only).

| Timepoint | Machine CPU % (`Get-Counter '\Processor(_Total)\% Processor Time'`) |
|---|---|
| Trước khi bật pipeline (baseline) | 14,45 · 14,72 · 13,91 · 19,56 · 11,09 |
| Trong lúc probe serve `--hwnd` (run 1) | 80,44 · 83,63 · 78,15 |
| Trong lúc pipeline chạy từ UI App (App+Core+sidecar) | 86,35 · 85,28 · 83,92 |
| Trong lúc App+Core idle (không pipeline) | 44,42 · 34,02 · 44,88 |
| Sau khi dọn pipeline | 27,01 · 21,42 · 24,39 · 23,74 · 30,53 |

→ Wall-clock latency dưới đây **bị kéo dài bởi contention** (coi như upper-bound-ish). Attribution CPU dùng **per-process** (`Get-Process .CPU` delta / wall) như M2/M3; `% 1 core` = delta/wall×100, `% 16 luồng` = delta/wall/16×100.

### Số liệu — pipeline serve (`--hwnd`, cửa sổ thật), scripted stdio

Probe: spawn `ReTran.Core.exe --serve`, `core.ping` → `core.start {source:{kind:"window",hwnd}, translate:{provider:"google",src:"auto",dst:"vi"}}`, trả lời `translate.request_cache` bằng all-miss như App sẽ làm, chạy ~90–99 s rồi `core.stop`. **Hai run độc lập** (R1, R2).

| Metric | R1 | R2 |
|---|---|---|
| Spawn → `core.ping` → `"pong"` | **111,6 ms** | **99,6 ms** |
| `core.start` → `{"running":true}` | 125,6 ms (tức thời) | 115,0 ms |
| **`core.start` → `frame.translated` đầu tiên** | **13,07 s** | **12,87 s** |
| Số frame trong cửa sổ đo | 12 (92,81 s) | 12 (99,06 s) |
| fps đạt được (wall) | **0,129 fps** | **0,121 fps** |
| Chu kỳ steady (frame 2→12) | median ~7,13 s · min 7,03 s · max 7,76 s (mean 7,24 s) | median ~7,96 s · min 7,12 s · max 8,15 s (mean 7,84 s) |
| Boxes / frame | 31 (30 có text) | 31→33 (30→32 có text) |
| `translate.request_cache` | **1 request** (27 text, lúc 13,116 s) | **2 request** (27 text lúc 12,913 s; 13 text lúc 43,538 s) |
| `translate.put` | **0** | **0** |
| `core.stop` round-trip | 39,3 ms | 44,5 ms |

Đọc số trên:

- **Diff-based hoạt động:** nội dung cửa sổ không đổi ⇒ **không** request cache nào thêm trong ~80 s tiếp theo (R1). R2 có request thứ 2 vì nội dung cửa sổ **thật sự đổi** giữa chừng (Notepad đổi CRLF→LF, tiêu đề tab đổi) → chỉ 13 text mới được tra, không re-tra 30 text cũ.
- **Dịch không bao giờ block tick:** cả 27 text đều **dịch fail** (Google HTTP 429, xem dưới) nhưng chu kỳ vẫn đều ~7,1–7,8 s ⇒ `warn: translate failed ...` chỉ ghi stderr, tick không đứng. Đây là bằng chứng thực nghiệm cho spec §4.1/§4.2.
- Frame đầu 12,9–13,1 s = cold OCR (init model + inference đầu). Sidecar không warm-up riêng; model đã cache (stderr: `Model files already exist. Using cached files.`).

### Số liệu — CPU / RAM

**A. Pipeline chạy, điều khiển từ UI App thật** (`Bắt đầu` trên trang Điều khiển; nguồn = cửa sổ `ReTran App (msedgewebview2.exe)`; sample **50,5 s** giữa lúc pipeline đang chạy; App khởi động với `PYTHONUTF8=1`, xem disclosure 5):

| PID | Name | CPU Δ | % 1 core | % 16 luồng | RSS (MB) | Peak WS (MB) | Vai trò |
|---|---|---|---|---|---|---|---|
| 51984 | ReTran.App.exe | 0,36 CPU-s | 0,7% | **0,04%** | 220,9 | 221,7 | **App (UI + WebView + cache EF)** |
| 38568 | ReTran.Core.exe | 2,19 CPU-s | 4,3% | **0,27%** | 102,7 | 156,3 | **Core (capture + PNG + RPC)** |
| 21624 | python.exe | 0,00 CPU-s | 0,0% | 0,00% | 4,0 | 4,0 | sidecar parent proxy |
| 18560 | python.exe | 484,39 CPU-s | 959,5% | **59,97%** | 346,5 | 532,7 | **sidecar worker (OCR)** |
| — | **Tổng pipeline** | **486,94 CPU-s** | 964,5% | **60,28%** | **RSS sum 674,1** | **peak sum 914,7** | |

**B. Pipeline chạy, scripted stdio, không có App** (probe R2; sample **50,6 s**):

| PID | Name | CPU Δ | % 1 core | % 16 luồng | RSS (MB) | Peak WS (MB) |
|---|---|---|---|---|---|---|
| 46000 | ReTran.Core.exe | 3,66 CPU-s | 7,2% | 0,45% | 91,0 | 128,1 |
| 48652 | python.exe | 0,00 CPU-s | 0,0% | 0,00% | 4,0 | 4,1 |
| 2320 | python.exe | 441,94 CPU-s | 872,6% | 54,54% | 335,7 | 551,2 |
| — | **Tổng** | **445,6 CPU-s** | 879,8% | **55,0%** | **RSS sum 430,7** | **peak sum 683,4** |

**C. App + Core idle (Core đã spawn, sidecar đã lên, pipeline CHƯA start)** — sample **50,5 s**:

| PID | Name | CPU Δ | % 1 core | RSS (MB) | Peak WS (MB) |
|---|---|---|---|---|---|
| 7764 | ReTran.App.exe | 0,08 CPU-s | 0,2% | 200,4 | 206,3 |
| 6832 | ReTran.Core.exe | 0,00 CPU-s | 0,0% | 34,8 | 34,9 |
| 33172 | python.exe (sidecar parent) | 0,00 CPU-s | 0,0% | 4,0 | 4,1 |
| 5616 | python.exe (sidecar worker, chưa OCR) | 0,00 CPU-s | 0,0% | 31,6 | 31,7 |
| — | **Tổng** | **0,08 CPU-s** | **0,2%** | **RSS sum 270,8** | **peak sum 277,0** |

**D. Overlay/OBS feed (App đang chạy pipeline, `curl -N http://127.0.0.1:17863/events`)**:

| Metric | Giá trị |
|---|---|
| Số event `data:` trong 45 s | 8 (2 event đầu = snapshot bắt kịp) |
| Nhịp steady | 5,5–6,0 s / khung (median ~5,6 s ⇒ **~0,17 fps**) |
| Kích thước payload | **2.550 byte/khung** (`{"boxes":[{x,y,w,h,t}...]}`, toạ độ đã normalize 0–1) — **n = 1 mẫu duy nhất** (8 event trong cửa sổ 45 s đều dài đúng 2.550 B vì nội dung cửa sổ không đổi); chưa có mẫu đa dạng nội dung |

### Số liệu — latency dịch (Google)

**Không đo được latency dịch thành công**: endpoint `translate.googleapis.com` trả **HTTP 429 (Too Many Requests)** cho mọi request từ IP máy này trong suốt phiên đo. `ReTran.Core translate "hello world" --dst vi --src en` ×10 (cách nhau 3 s): **10/10 exit 1**, đều in `translate failed: Google translate endpoint returned HTTP 429.` Wall **491,7 / 574,3 (median) / 595,6 (mean) / 733 ms** (10 giá trị: 574,3 · 491,7 · 733,0 · 666,2 · 578,5 · 523,8 · 661,1 · 691,6 · 540,2 · 495,1; median theo quy ước **lower-median** = phần tử thứ 5 của 10 giá trị đã sort — cùng quy ước với M2/M3) — đây là **latency nhánh lỗi** (gồm spawn process), **không phải** latency dịch thành công.

Hệ quả trực tiếp trên số đo: `translated` của mọi box là `null` (hiển thị text gốc — đúng nhánh fail của spec §7), `translate.put` = 0, cache DB không có dòng nào ⇒ **không đo được** chi phí "cache hit = 0 network call" và **không đo được** latency dịch thực.

### E2E thật qua UI (không scripted)

| Bước | Kết quả |
|---|---|
| Mở App → badge `Core: …` | Core child được spawn tự động, `ReTran.Core.exe` + sidecar python lên |
| Trang Điều khiển render | Pipeline card (badge, dropdown Màn hình/Cửa sổ, `Làm mới danh sách`/`Bắt đầu`/`Dừng`), card Dịch, nav `Cài đặt` — tiếng Việt, đúng spec §6.1 |
| `core.list_sources` → dropdown Cửa sổ | Liệt kê cửa sổ thật kèm process: `Cửa sổ — m4-perf-fixture.txt - Notepad (notepad.exe)`, `… ReTran App (msedgewebview2.exe)`, `… Chat - Unsloth - Brave (brave.exe)`, … |
| Bấm `Bắt đầu` | badge → **● Đang chạy**; `frame.translated` về App; overlay SSE `/events` phát khung có text thật (`ReTran`, `Điều khiển`, …) |
| Overlay/OBS | `GET http://127.0.0.1:17863/overlay` → HTTP 200; `GET /events` (SSE) → 8 khung/45 s, 2.550 B/khung |
| Bấm `Dừng` | badge → **■ Đang dừng**; Core RSS 102,7 → 84 MB |
| Trang Cài đặt + `Lưu cài đặt` | Ghi `%LOCALAPPDATA%\ReTran\settings.json` = `{"provider":"google","src":"auto","dst":"vi","apiKey":null}` |
| Bấm `Bắt đầu` lại (phiên mới) | **không verify được** cache hit — cache rỗng vì Google 429 (xem "cần đo thật" #2) |
| Đổi `dst` trong Cài đặt → `Lưu cài đặt` → `Bắt đầu` lại → bản dịch đổi ngôn ngữ | **placeholder, chưa verify được** — cần provider trả bản dịch; ở phiên đo mọi lần dịch đều 429 nên không có mốc "bản dịch đổi" để so (xem "cần đo thật" #1). Đường *cấu hình* đã verify: `Lưu cài đặt` ghi `settings.json`, và `PipelineController` đọc `dst` khi start |

### Đối chiếu budget (AGENTS.md)

| Budget row | Budget | Measured (M4) | Verdict |
|---|---|---|---|
| Capture CPU | < 2% CPU | Core tổng (capture + PNG + RPC) **4,3% của 1 core = 0,27% tổng 16 luồng** (UI-driven) / **7,2% 1 core = 0,45%** (scripted). Chưa tách được riêng phần capture | **provisional BREACH** nếu đọc budget theo %1 core (4,3–7,2% > 2%); **PASS** nếu theo %16 luồng. Cùng cách đọc như M3 (Core tổng, capture chưa tách) |
| OCR throughput | ~3 fps (≤ ~333 ms/lượt) | steady **~7,1–8,0 s/lượt ⇒ 0,12–0,14 fps**; cold 12,9–13,1 s | **BREACH ~22–25×** (provisional — cùng nguyên nhân M3: paddle CPU build, contention nền 80–86%) |
| OCR VRAM | < 1 GB | **0** (paddle CPU build) | **PASS** |
| UI/overlay update | 10–15 fps | SSE overlay **~0,17 fps** nhưng **đây là nhịp bị giới hạn bởi OCR** (mỗi khung = 1 chu kỳ OCR), không phải giới hạn render; App CPU khi pipeline chạy chỉ 0,04% 16 luồng | **không kết luận** (thiếu số đo khi OCR nhanh — xem "cần đo thật") |
| OBS export | ~10 fps | Cùng feed `/events`, payload 2.550 B/khung; chưa nối OBS thật | **N/A** (chưa có OBS Browser Source thật trong phiên đo) |
| Translation | diff-based, không block pipeline, cache hit = 0 network call | diff-based **PASS** (1 request/27 text rồi im 80 s; nội dung đổi → chỉ 13 text mới). Không block **PASS** (27 dịch fail, tick vẫn ~7,1 s). **Cache hit chưa đo được** (Google 429) | **PASS một phần / chưa đo phần cache-hit** |
| Tổng CPU (khi gaming+streaming) | < 8% | Pipeline chạy: **60,28% tổng 16 luồng** (App 0,04% · Core 0,27% · **sidecar worker 59,97%**). Idle (chưa start): **0,2% 1 core** | **BREACH** (do OCR CPU; App+Core tự thân chỉ 0,31% — cùng verdict M3) |
| Tổng RAM | < 500 MB | Pipeline chạy: **RSS sum 674,1 MB** (App 220,9 · Core 102,7 · sidecar 350,5); **peak sum 914,7 MB**. Idle: RSS sum 270,8 MB / peak 277,0 MB | **BREACH khi chạy** (RSS + peak) / **PASS khi idle** (cùng caveat M2/M3: `peak_wset` theo tree không tách được từng tầng) |

### Quyết định tradeoff (user-approved, 2026-10-08)

Các số **BREACH** ở bảng trên (OCR throughput, tổng CPU, tổng RAM khi chạy) là **provisional** — đo dưới contention nền 78–86% CPU, chưa có game + OBS thật (placeholder #3), và sidecar vẫn là **paddle CPU build** chưa throttle. **User đã duyệt (2026-10-08) chấp nhận ship M4 với các số provisional này.**

**Điều kiện kèm theo (bắt buộc trước khi coi perf gate đạt):** việc xử lý sidecar OCR là hạng mục **bắt buộc**, gồm (1) chuyển paddle sang **GPU build** (`is_compiled_with_cuda() → False` hiện tại — placeholder #4) và (2) **throttle** OCR về budget ~3 fps (placeholder #6). Cho tới khi hai việc này xong, không verdict BREACH nào được đổi thành PASS do quyết định này — 8 placeholder ở bảng dưới giữ nguyên.

### Cần đo thật (placeholder — chưa có số ở M4)

| # | Cần đo | Vì sao chưa có |
|---|---|---|
| 1 | Latency dịch Google **thành công** (p50/p95) | Endpoint trả **HTTP 429** từ IP máy này suốt phiên đo (10/10 lần, cả probe lẫn CLI) |
| 2 | **Cache hit = 0 network call** (đo số request mạng khi bắt đầu lại phiên) | Cache DB rỗng vì (1) ⇒ không có dòng nào để hit |
| 3 | Budget CPU/RAM **khi gaming + streaming thật** (game + OBS) | Phiên đo không có game/OBS; chỉ có cửa sổ văn phòng. Số ở đây là **provisional** |
| 4 | **OCR trên GPU** (VRAM < 1 GB, ~3 fps) | `paddlepaddle` trên máy này là **CPU build** (`is_compiled_with_cuda() → False`); chưa cài bản GPU |
| 5 | Nguồn `screen` / full-screen capture | DXGI không frame nào trên máy này (lỗi HDR, xem M3) ⇒ mọi số capture đo bằng nguồn `window` |
| 6 | `frame.translated` ở tần 10–15 fps | Tần khung bị chi phối bởi OCR (~7 s/lượt); cần máy có GPU OCR để đo nhịp thật của tầng UI/overlay |
| 7 | **Đổi `dst` → `Lưu cài đặt` → `Bắt đầu` lại → bản dịch đổi ngôn ngữ** | Cần provider trả bản dịch (429 ở toàn bộ phiên đo, xem (1)) ⇒ không có mốc "bản dịch đổi" để so. Đường cấu hình (`Lưu cài đặt` ghi `settings.json`) đã verify |
| 8 | `translated` hiển thị **song song** với text gốc (overlay/OBS hiện cả hai khi dịch xong) | Ô `translated` chỉ toàn `null` vì dịch luôn fail; chưa quan sát được trạng thái "đã dịch" ngoài đời |

### Ghi chú / hạn chế đo (đọc trước khi dùng số)

1. **Background 78–86% CPU** suốt window pipeline (bảng Bối cảnh) → wall-clock = upper-bound-ish; verdict throughput/CPU **provisional**. Baseline *trước* khi bật pipeline chỉ 11–19,6%, phần chênh còn lại là tiến trình khác của user + chính pipeline.
2. **Google 429 là blocker của số đo M4** — mọi nhánh "dịch thành công / cache hit" đều không đo được. Nhánh fail đo rất tốt (không block tick).
3. **Hai hệ capture khác nhau** cho hai bảng số: probe scripted dùng cửa sổ Notepad 1161×714 (27–33 box), UI-driven dùng cửa sổ App 1424×635 (~2.550 B/khung). Không so chéo tuyệt đối giữa hai bảng.
4. **Sidecar là 2 process** (parent proxy ~4 MB + worker làm OCR) — footprint đo cả tree như M2/M3; `peak_wset` sum theo tree không tách được peak từng tầng.
5. **`PYTHONUTF8=1` là workaround env, không sửa code:** sidecar in ra stdout bằng cp1252 và **crash** (`UnicodeEncodeError: '≡'`) khi OCR đọc được ký tự ngoài cp1252 (icon hamburger `≡` của Notepad, hoặc text tiếng Việt). Đã tái hiện: chạy pipeline cửa sổ Notepad từ UI App **không** set env → badge **"Lỗi core: sidecar-exited"**; set `PYTHONUTF8=1` rồi chạy lại → chạy bình thường, khung về đều. Đây là **bug thật còn tồn tại** (M2 note 4 / M3 note 2), chưa fix trong production ⇒ cần fix encoding stdout của sidecar (hoặc ép UTF-8 trong `SidecarProcess`).
6. **Test integration: 2/3 assert thật, 1/3 assert 0 lần trên máy này.** `Serve_PingStopListSources_Roundtrip_SkipEamWithoutExe` và `Serve_StartWithBogusProvider_Rejects_SkipEamWithoutExe` assert thật (PASS). `Serve_Start_EmitsFrameTranslated_AndRequestsCache_SkipEam` ghim `screen`/monitor 0 nên **không có frame nào để assert** ở máy này (xem mục nguồn capture) và **assert 0 lần** — xUnit v2 không skip động được, nên nhánh đó in dòng `SKIPPED (env): …` qua `ITestOutputHelper` (xem `--logger "console;verbosity=detailed"`), output thật của phiên đo:
   `SKIPPED (env): pipeline.state {running:false, reason:"capture-error"} before any frame — the capture source or sidecar died in this environment`.
   **Lượt chờ hết giờ mà pipeline còn sống thì FAIL, không skip**; `pipeline.state {running:false}` cũng chỉ skip khi `reason` là `capture-error` / `sidecar-exited` — `internal` hoặc lý do lạ là **crash của loop** ⇒ FAIL (đã verify bằng mutation, xem dưới). Vì có 2 loại dấu hiệu, test dùng 2 marker: `SKIPPED (env): …` = **assert 0 lần**; `ENV NOTE: …` = assert đã chạy xong rồi mới gặp giới hạn môi trường (ví dụ frame đầu OK nhưng pipeline chết trước frame kế tiếp). Đường assert đó **đã** verify bằng 5 mutation trên bản test tạm trỏ nguồn cửa sổ: (a) đổi `seq` trong `frame.translated` thành string → FAIL tại parse `seq`; (b) cho `JsonRpcCacheChannel.LookupAsync` bỏ request `translate.request_cache` → FAIL tại `Assert.NotEmpty(CacheLookups)`; (c) đổi `src` trong request cache → FAIL tại `Assert.Equal("auto", src)`; (d) đổi tên method notify `frame.translated` → `frame.translated.MUTANT` (loop vẫn sống) → **FAIL sau 90 s** với message `no frame.translated within 90s while the pipeline was still alive …`; (e) đổi `reason` của `pipeline.state` thành giá trị lạ → **FAIL** với message `pipeline.state {running:false, reason:"…"} before any frame — the pipeline loop died on its own (crash, not an environment limit)`. Tất cả mutation đã revert.
7. **Số CPU ghi bằng `Get-Process .CPU` delta** (tổng CPU-seconds của process) thay vì `psutil.cpu_percent` của M3 — ít nhạy contention hơn, nhưng là giá trị trung bình cửa sổ 50 s, không phải mẫu tức thời.
8. **Cleanup sau đo:** App, Core, sidecar, Notepad đã đóng hết (0 tiến trình ReTran còn lại).

### Lệnh reproduce

```powershell
# --- 0. Build + fixture cửa sổ thật
dotnet build "ReTran Project.slnx"
Set-Content "$env:TEMP\m4-perf-fixture.txt" -Value @(
  'RETRAN M4 PERF FIXTURE'
  'The quick brown fox jumps over the lazy dog.'
  'Hello world, this is a translation pipeline test.'
  'Overlay annotation line three.'
)
Start-Process notepad "$env:TEMP\m4-perf-fixture.txt"
$hwnd = '0x{0:X}' -f (Get-Process notepad | Select-Object -First 1).MainWindowHandle   # session-specific, derive lại

# --- 1. Bối cảnh: background CPU (confounder)
$s = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 5).CounterSamples | ForEach-Object { $_.CookedValue }
"bg CPU: $($s -join ', ') %"

# --- 2. Latency CLI `translate` (Google) x10
$exe = "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe"
1..10 | ForEach-Object { $sw=[Diagnostics.Stopwatch]::StartNew(); & $exe translate "hello world" --dst vi --src en; $sw.Stop(); "$([math]::Round($sw.Elapsed.TotalMilliseconds,1)) ms" ; Start-Sleep 3 }

# --- 3. Pipeline serve scripted (probe tương đương %TEMP%\m4-perf-serve.ps1 trong báo cáo task)
#     spawn --serve, core.ping, core.start {source:{kind:"window",hwnd},translate:{provider:"google",src:"auto",dst:"vi"}},
#     reply translate.request_cache = {hits:{},misses:[texts]}, ghi timestamp mọi frame/request/put, cuối cùng core.stop.
#     $env:PYTHONUTF8 = '1'   # BẮT BUỘC — xem ghi chú 5
#     => ping ~100 ms · core.start→frame đầu ~12,9-13,1 s · chu kỳ ~7,1-8,0 s · 0-2 request cache

# --- 4. CPU/RAM khi pipeline chạy (per-process, 50 s)
#     m4-perf-cpu.ps1: Get-CimInstance Win32_Process lọc ReTran.Core.exe + python.exe có cmdline 'retran_core',
#     Get-Process .CPU / WorkingSet64 / PeakWorkingSet64 trước & sau 50 s
Get-Process ReTran.Core, python -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, CPU, WorkingSet64, PeakWorkingSet64

# --- 5. E2E qua UI thật (App phải chạy với PYTHONUTF8=1, xem ghi chú 5)
$env:PYTHONUTF8 = '1'
Start-Process "ReTran.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\ReTran.App.exe"
# trang Điều khiển: chọn nguồn (Màn hình / Cửa sổ) -> Bắt đầu -> badge "● Đang chạy" -> Dừng -> badge "■ Đang dừng"
# overlay/OBS feed:
curl.exe -sN --max-time 45 http://127.0.0.1:17863/events    # SSE {"boxes":[{x,y,w,h,t}...]}
Invoke-WebRequest http://127.0.0.1:17863/overlay -UseBasicParsing | Select-Object StatusCode
# trang Cài đặt -> Lưu cài đặt -> kiểm tra file:
Get-Content "$env:LOCALAPPDATA\ReTran\settings.json"
# dọn: Stop-Process ReTran.App, ReTran.Core, notepad + python sidecar

# --- Machine info
Get-CimInstance Win32_Processor; Get-CimInstance Win32_ComputerSystem; Get-CimInstance Win32_VideoController
```

---

## OCR GPU + HDR capture (2026-10-19)

### Bối cảnh đo

- User báo "dịch và OCR không hoạt động" trên máy dev (RTX 5060 Ti, 2 monitor **HDR**, session remote).
- Hai nguyên nhân gốc đo được:
  1. `ScreenCaptureSource` bỏ qua desktop HDR — DXGI duplication trả `R16G16B16A16_Float`, không có frame BGRA → source chết sau 5 s chờ frame đầu (`capture screen` CLI timeout).
  2. `paddlepaddle` trên Windows là bản **CPU-only** (`is_compiled_with_cuda() == False`) → inference ~25 s/khung, vượt `OcrPipeline.Timeout` 30 s khi máy có tải → mọi chu kỳ bị "cycle skipped".
- Fix: GDI BitBlt fallback (`ScreenCaptureSource`) + `paddlepaddle-gpu==3.3.1` từ index chính thức `paddlepaddle.org.cn/packages/stable/cu129` (Blackwell sm_120 cần CUDA ≥ 12.8; PyPI chỉ có `paddlepaddle-gpu` 2.6.2) + `nvidia-cudnn-cu12==9.9.0.52` (wheel Windows của paddle **không** bundle cuDNN) + đăng ký `site-packages/nvidia/*/bin` vào DLL search path trong `paddle_engine._create` + sidecar warmup fire-and-forget khi core `--serve` khởi động.

### Số liệu — OCR một ảnh fixture (cửa sổ thật 1424×745, 124 text box)

| Đường chạy | Thời gian |
| --- | --- |
| CPU `paddlepaddle 3.3.1` (trước) | 25 060 ms |
| GPU `paddlepaddle-gpu 3.3.1` cu129 (sau) | **1 304 ms** (19×) |
| Lần gọi đầu tiên incl. CUDA context init | ~30,5 s (warmup che trước khi pipeline chạy) |
| `capture screen` GDI 1920×1080 (PNG encode inline) | ~1 s/frame; 638 frame trong loop 122 s |

Máy: RTX 5060 Ti, CC 12.0, driver API 13.4 / runtime 12.9.

### Pipeline serve end-to-end (scripted stdio, notepad fixture, google auto→vi)

- `core.start` → `frame.annotated` đầu **+0,9 s**; `frame.translated` đầu **+6,6 s** (boxes = 84); chu kỳ OCR ~1 s; `translate.request_cache` round-trip OK.
- **Chưa đo với game thật** (chỉ desktop + notepad) và **chưa đo VRAM** sidecar — hai số này còn thiếu so với budget AGENTS.md.

### Lệnh reproduce (session-scoped)

Scripts đo nằm trong `%TEMP%\opencode\` của session: `warmup_probe4.ps1` (sidecar trực tiếp: ping → dummy → ảnh thật, in `elapsed_ms`) và `pipeline_test6/7.ps1` (core `--serve`, trả `translate.request_cache`, đếm `frame.annotated`/`frame.translated`). Derive lại theo mô tả nếu không còn file.

---

## M5 — Camera ảo + Preview (2026-10-19)

### Bối cảnh đo

| Thành phần | Giá trị | Nguồn |
| --- | --- | --- |
| CPU | Intel Core i5-13400F, 10 cores / 16 logical processors | `Win32_Processor` |
| GPU | NVIDIA GeForce RTX 5060 Ti (CC 12.0, driver API 13.4 / runtime 12.9) | `Win32_VideoController` + log paddle |
| Nguồn capture | **màn hình 0** 1920×1080 (HDR → đường GDI fallback), **game Reverse: 1999 đang render** (GPU ~8 %) | UI app + `Get-Process` + `\GPU Engine` |
| OBS | 32.2.2, canvas 1920×1080@60, source DShow tên `'OCR'` = device **ReTran Camera** (log mở device `13:17:31`); phiên này **không** stream/record | `%APPDATA%\obs-studio\logs` |
| Nền máy | 79–88 % CPU (Taskmgr, NVIDIA Broadcast, RustDesk, Docker, Brave, Avast…). Số CPU dưới đây là **CPU-seconds per-process** (ít nhạy contention hơn wall-clock) | `Get-Counter \Processor(_Total)` |
| Thời lượng | 2 cửa sổ liên tiếp: 279,4 s và 129,8 s, sampler 1 Hz | CSV `%TEMP%\opencode\perf_samples*.csv` |

GATE Task 4 (user, 2026-10-19): PASS cả 4 mục — OBS thấy khung thật + box + bản dịch; Stop giữ khung cuối; Start lại có khung mới; kill app → OBS đen ≤1 s, device vẫn trong danh sách, mở lại app → sống.

### Số đo (app + core + OCR, pipeline chạy, camera phát 8,5 fps)

| Process | CPU avg / max (%16 luồng) | CPU avg (1 core) | RAM avg / max | VRAM max |
| --- | --- | --- | --- | --- |
| ReTran.App | 3,05 / 5,47 | 0,49 core | 325 / 373 MB | 43 MB |
| ReTran.Core | 11,56 / 17,09 | 1,85 core | 923 / 1803 MB | 0 |
| OCR sidecar (cả 2 process) | 7,81 / 10,94 | 1,25 core | 1996 / 2025 MB | 362 MB |
| OBS (preview 1080p60 + source camera) | 5,24 / 7,32 | 0,84 core | 244 MB | 1869 MB |
| **Tổng app+core+ocr** | **22,42 / 33,50 %** | **3,59 core** | **3244 / 4116 MB** | **405 MB** |

- Camera ảo (đếm từ `seq` shared memory): **8,48–8,58 fps**.
- RAM Core **sawtooth 586 → 1803 → 586 MB** (cấp phát buffer lớn theo khung, không phải leak).
- Sidecar là **2 process**: trampoline venv ~4 MB (`.Path` khớp filter) + interpreter thật ~2 GB (`CommandLine` khớp) — lọc process bằng `.Path` sẽ **đo thiếu** CPU sidecar (bài học đo).
- Chưa tính vào tổng: process con **WebView2** (`msedgewebview2.exe`) của app + các process con khác.

### Đối chiếu budget

| Budget | Đo được | Kết luận |
| --- | --- | --- |
| Tổng CPU < 8 % (16 luồng) | 22,42 % avg / 33,50 % max | **FAIL (2,8×)** |
| Tổng RAM < 500 MB | 3244 MB avg / 4116 MB max | **FAIL (6,5×)** |
| OCR VRAM < 1 GB | 362 MB | PASS |
| Annotator ≤ 10 ms/khung | **~63 ms CPU/khung** (0,41 core ÷ 6,52 khung/s chênh A−B); wall-clock ~117 ms/khung so với timer 100 ms | **FAIL (~6×)** |
| IPC `frame.annotated` ≤ 1,5 MB/s | **2,58–3,05 MB/s** (306–364 KB/khung × 8,5 fps) | **FAIL (~2×)** |
| UI/camera ~10 fps | 8,5 fps | PASS (≈) |
| Capture < 2 % CPU | chưa tách riêng khỏi Core (Core tổng 11,6 %) | chưa kết luận |
| Camera filter CPU trong OBS | chưa tách được (OBS tổng 0,84 core) | chưa kết luận |

### A/B — chi phí nằm ở đâu (Core scripted `--serve`, cùng nguồn màn hình, không app)

Cơ chế cần biết: annotate loop chạy theo `CoreConfig.CaptureFps` (**10**, đọc lúc Core khởi động); tham số `captureFps` của `core.start` **đã được nối vào capture source** (T5, 2026-10-21) — nhịp **annotate** vẫn theo config đọc lúc khởi động. (Lịch sử: lúc đo A/B dưới đây tham số này **mới chỉ validate, chưa nối** — capture luôn 10 fps.)

| Lượt | Cấu hình | box/khung | annotated fps | IPC MB/s | KB/khung | Core CPU avg / max (1 core) | Core RAM avg / max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A | annotate 10 fps, all-miss | — | 8,52 | 2,58 | 306,5 | 73,4 / 112,4 % | 452 / 774 MB |
| B | annotate 2 fps, all-miss | — | 2,00 | 0,59 | 281,3 | 32,5 / 41,6 % | 369 / 529 MB |
| X | annotate 10 fps, màn hình đổi liên tục (game render) | — | 8,48 | 3,05 | 363,6 | 85,1 / 104,6 % | 473 / 788 MB |
| Y | annotate 10 fps, **all-hit** (mô phỏng cache app có sẵn) | — | 8,42 | 2,61 | 314,4 | 83,4 / 102,6 % | 379 / 498 MB |
| Z | annotate 10 fps, màn hình giàu chữ (Notepad 260 dòng, maximize) | — | 8,48 | 2,71 | 323,7 | 81,3 / 98,9 % | 377 / 530 MB |
| W | annotate 10 fps, **có đếm box** | **76,9** (max 84) | 8,78 | 2,93 | 337,6 | 80,8 / 102,8 % | 369 / 546 MB |

- **Đường annotate ≈ 0,41 core ở 8,5 fps** (A − B = 40,9 điểm ÷ 6,52 khung/s ⇒ **~63 ms CPU/khung**) và tạo 2,58 MB/s ⇒ vượt cả budget IPC lẫn budget 10 ms/khung.
- Suy ra phần còn lại: B = 32,5 điểm (gồm 2 khung annotate/s) ⇒ **≈ 0,2 core** cho capture loop + chu kỳ OCR + IO.
- Lượt B giữ nguyên số `frame.translated` (170 vs 178) ⇒ A/B tách đúng đường annotate, không đụng chu kỳ OCR.
- X (màn hình động) và Z (màn hình giàu chữ) **không** làm Core tăng vọt: 0,85 / 0,81 core — dải 0,73–0,85 core cho mọi trạng thái màn hình đã thử.
- Y (cache all-hit) **không** làm chu kỳ OCR dày lên: `cache_requests` 0,77/s so với 1,05/s ở X ⇒ chu kỳ bị chặn bởi OCR (~1 s/chu kỳ), không bởi bước dịch.
- W đếm được **~77 box/khung** trên màn hình thật (game + cửa sổ app/chat) — dùng để hiệu chỉnh mô hình chi phí bên dưới.

### Mô hình chi phí annotate (micro-benchmark GDI+)

Tái lập đúng thân `FrameAnnotator.Annotate` (downscale 1920×1080 → 1280×720, vẽ N box với 9 lượt `DrawString`/box, JPEG q70, base64) bằng PowerShell + System.Drawing, lấy ảnh chụp màn hình thật làm nguồn, 5 lần/N:

| Bước | 0 box | 50 box | 150 box |
| --- | --- | --- | --- |
| downscale (`DrawImage`) | 9,7 | 8,5 | 8,3 ms |
| **vẽ box + text (9 lượt/box)** | 6,8 | 22,9 | **54,8 ms** |
| JPEG q70 encode | 7,1 | 5,6 | 4,7 ms |
| base64 | 3,6\* | 0,3 | 0,4 ms |
| **tổng** | **27,1** | **37,3** | **68,2 ms** |
| JPEG KB | 47,9 | 212,3 | 387,2 KB |

\* 3,6 ms là JIT lần đầu của `ToBase64String`; các lượt sau ~0,3–0,4 ms ⇒ base64 không đáng kể.

- **Vẽ box/text ≈ 0,32 ms/box** (9 lượt `DrawString`/box: 8 lượt viền đen + 1 lượt chữ trắng) — ở màn hình thật (W: 77 box) chiếm ~25 ms/khung.
- Chi phí **cố định ~44 ms/khung**: downscale (~8,5) + JPEG (~5) + copy BGRA full-res vào `Bitmap` theo từng dòng (`Marshal.Copy`, 1080 dòng) + dựng chuỗi JSON (~338 KB → ~700 KB UTF-16) + ghi pipe + GC.
- Mô hình khớp W: 44 + 0,32×77 ≈ 69 ms/khung = đúng số suy từ đo ((80,8 − 20) ÷ 8,78).

### Chênh lệch app-driven vs scripted — kết luận

Core khi app chạy **1,85 core** (218 ms/khung) so với **0,73–0,85 core** (~92 ms/khung) khi scripted. Đã **loại** bằng thực nghiệm: cache hit (Y), màn hình động (X), mật độ chữ trên màn hình (Z), sidecar (process riêng, không tính vào CPU Core). Phần chênh khớp với hai yếu tố chưa tách được riêng:

1. **Nhiều box hơn khi app chạy**: màn hình lúc đo chứa *chính preview của app và preview OBS* (đã vẽ bản dịch) nên OCR bắt được cả text gốc lẫn text dịch; mô hình 0,32 ms/box ⇒ ~200 box ≈ +40 ms/khung.
2. **Contention**: cửa sổ app-driven chạy khi nền 79–88 % CPU (thêm app + WebView2 + preview OBS 60 fps), so với 59–71 % ở các lượt scripted; đường annotate là memory-bound (copy 8 MB + GDI+ + JPEG + chuỗi lớn) nên CPU-seconds tăng theo mức tranh chấp cache/memory.

Cả hai đều trỏ về **cùng chỗ sửa** (giảm khối lượng vẽ mỗi khung + giảm copy/chuỗi/transport), không phải capture hay OCR.

### Ghi chú / hạn chế đo

1. **Thiếu VRAM cho Core/App đo bằng counter `GPU Process Memory`** — Core/App ở 0 (chỉ OBS/sidecar hiện), nên VRAM thật của app (D3D/WebView) có thể cao hơn số 43 MB.
2. **`captureFps` đã được nối vào capture source** (T5, 2026-10-21; lúc đo A/B dưới đây **chưa nối** — xem trên) — nhịp annotate vẫn theo `CoreConfig.CaptureFps` đọc lúc khởi động; A/B dưới đây chỉ tách được annotate.
3. **Google 429** vẫn là blocker của dòng dịch (như M4): scripted all-miss liên tục fail `translate failed ... HTTP 429`.
4. **PASS/FAIL ở đây so với budget AGENTS.md §Performance** (< 8 % CPU, < 500 MB RAM, ≤10 ms/khung, ≤1,5 MB/s). Không mục nào PASS ở CPU/RAM/annotate/IPC ⇒ **cần tradeoff được duyệt hoặc fix trước khi coi M5 "ship"** — chi tiết hướng fix ở báo cáo session.
5. Số đo cũ (M4) **không so chéo tuyệt đối** được: M4 đo CPU `paddlepaddle` (25 s/khung, gần như không đốt Core) còn M5 đo GPU paddle (1,3 s/khung, chu kỳ dày hơn nhiều).
6. **Nội dung màn hình không được kiểm soát giữa các lượt** (màn hình thật của user: game, cửa sổ chat, OBS preview…). Đây là lý do W đếm box để hiệu chỉnh mô hình; muốn so tuyệt đối phải cố định nội dung màn hình.
7. **Đường gửi ảnh cho OCR lúc đo dùng PNG full-res base64** (`BgraPngEncoder.ToPngBase64`, `OcrLoop.cs` — dòng 206 ở bản trước T4, nay :215) — encode PNG 1920×1080 tốn kém hơn JPEG nhiều lần. **Đã fix (T4, 2026-10-21): giờ gửi JPEG q85** (`BgraJpegEncoder.ToJpegBase64`), sidecar `decode_image_b64` nhận cả PNG lẫn JPEG.

### Lệnh reproduce (session-scoped)

- `%TEMP%\opencode\perf_sample.ps1` / `perf_sample2.ps1` — sampler CPU/RAM/VRAM + fps camera từ `seq` (bản 2 lọc sidecar bằng `CommandLine` của CIM).
- `%TEMP%\opencode\ab_annotate.ps1` — A/B annotate (`-Single -SingleLabel ... -CacheMode miss|hit`): spawn Core `--serve`, trả cache, đếm frame/byte/box (`frame.translated`), đo Core CPU/RAM.
- `%TEMP%\opencode\screen_activity.ps1` — hash ảnh chụp màn hình 2 s/lần + GPU util của game + CPU máy (đối chứng "màn hình có đổi không").
- `%TEMP%\opencode\annotate_bench.ps1` — micro-benchmark GDI+ tái lập `FrameAnnotator` (downscale / vẽ N box / JPEG / base64).
- CSV thô: `%TEMP%\opencode\perf_samples.csv`, `perf_samples2.csv`, `ab_annotate_*.csv`.

> **Ghi chú (2026-10-21):** các CSV của lượt M5 (19/10) **đã bị dọn cùng `%TEMP%`** — đường dẫn ở danh sách trên chỉ còn giá trị mô tả phương pháp, derive lại theo mô tả nếu cần; CSV lượt 20/10 còn trong `%TEMP%\opencode`; **CSV T6c/T6d/T6e (21/10 — nguồn bảng trước→sau của §M5-fix) nằm cạnh probe script trong SDD workspace**: `.superpowers/sdd/2026-10-19-pipeline-perf-fix/probe_T6{c,d,e}_*.csv`. Script đo mới nằm trong SDD workspace + bench project (xem §M5-fix — Lệnh reproduce).

---

## M5-fix — sau tối ưu (2026-10-21)

### Bối cảnh đo

- **Máy:** như §M5 (i5-13400F 10c/16t, RTX 5060 Ti, `paddlepaddle-gpu` cu129). Nền máy vẫn không idle (user app đang chạy) — số CPU ghi theo CPU-seconds per-process như M5.
- **Điều kiện probe scripted:** annotate 10 fps, cache all-miss, settle 40 s + measure 60 s (**bỏ 15 s đầu**); spawn `ReTran.Core --serve` như lượt W của M5.
- **Màn hình:** Notepad 260 dòng maximize — **khác** lượt W 19/10 (màn hình game+chat), nên `boxes_avg` thấp hơn (63,1 vs 76,9); so sánh CPU giữa trước/sau cần đọc kèm benchmark theo box bên dưới, không so chéo box tuyệt đối.
- **Benchmark code thật:** project riêng `annotbench` (Debug, reference `ReTran.Core`, gọi thẳng `FrameAnnotator.Annotate`), frame synthetic 1920×1080, 20 reps.
- **App-driven chưa đo lại — pending user game session:** số app-driven cũ (Core 1,85 core / 923 MB) ở bảng §M5 chỉ là **mốc trước fix**, chưa có số sau fix để so.

### Trước → sau (scripted)

| Chỉ số | W — trước fix (19/10, màn hình game+chat) | T6c — sau fix (21/10) | T6e — lặp lại |
| --- | --- | --- | --- |
| `boxes_avg` (box/khung) | 76,9 | 63,1 | 63 |
| annotated | 8,78 fps | 391 frame @ 8,77 fps | 391 @ 9,0 fps |
| Core CPU avg / max (% 1 core) | 80,8 / 102,8 | **46,0 / 57,4** | **45,5 / 64,0** |
| Core RAM avg / max (MB) | 369 / 546 | **247 / 330** | 251 / 321 |
| `ann_line_KB_avg` | 337,6 | **0,1** | 0,1 |
| `rpc_MBps` (stdout) | 2,93 | **0,058** | 0,063 |
| `ann_w`×`ann_h` | 1920×1080 (bug: báo khung nguồn) | **1280×720** | 1280×720 |
| preview MMF `Local\ReTran.Preview.V1` | — | seq 1744, 1280×720, **JPEG 154 KB, magic `FF D8` OK** | 157 513 B, magic OK |
| `translated` / `cache_requests` | — | 171 / 2 | 178 / 1 |
| sidecar (process/RAM) | 1,25 core / ~2 GB / 362 MB VRAM (app-driven 19/10, không đo lại) | 2 process / ~2,0 GB | 2 process / ~1,73 GB |

A/B post-fix (T6c 10 fps vs T6d 2 fps: 46,0 % vs 11,0 %, 8,77 vs 1,72 fps, **cùng nội dung màn hình — 63 box = 63 box**) cho slope ≈ 4,9 điểm/khung ≈ 49 ms/khung — slope so sánh được nhờ cùng nội dung, nhưng **bị thổi phồng** bởi chênh tần chu kỳ OCR giữa hai lượt (3,9 vs 2,3 chu kỳ/s); số ms/khung chính xác dùng benchmark code thật bên dưới.

### Benchmark code thật (Debug, frame synthetic 1920×1080, 20 reps/N, avg / p50 / min)

| Snapshot | avg | p50 | min | JPEG |
| --- | --- | --- | --- | --- |
| `null` (không vẽ box) | 18,98 ms | 16,95 | 14,89 | 263,2 KB |
| 0 box | 21,96 ms | 21,91 | 17,34 | 263,2 KB |
| **63 box** | **37,59 ms** | 38,25 | 29,56 | 291,6 KB |
| 150 box | 48,38 ms | 45,37 | 37,50 | 307,8 KB |
| 300 box | 86,40 ms | 79,41 | 61,12 | 314,0 KB |

- **Sàn GDI+ đo được ≈ 19–22 ms cố định/khung:** downscale ~9 ms + JPEG ~5 ms + dest/ToArray — không giảm thêm được nếu giữ GDI+ + JPEG 1280-wide.
- **Reconciliation (trước vs sau):** pre-fix ≈ 37,6 (post-fix) **+ ~24** (vẽ 9 lượt `DrawString` @77 box: 33 ms so với pill 9 ms) **+ ~5** (copy BGRA + dựng JSON/base64 + ghi pipe) ≈ **66–68 ms** — khớp baseline đo được 63–69 ms/khung (M5: A/B ~63 ms, suy từ W ~69 ms).

### Đối chiếu budget (scripted; camera/VRAM không đo lại round này)

| Budget | Đo được | Kết luận |
| --- | --- | --- |
| IPC `frame.annotated` ≤ 1,5 MB/s | **0,058 MB/s** (2,93 → 0,058) | **PASS** — JPEG khung giờ qua MMF `Local\ReTran.Preview.V1`, stdio chỉ còn notify `{seq,w,h}` |
| Annotate ≤ 10 ms/khung (budget cũ) | **37,6 ms @63 box** | **FAIL (3,8×)** — sàn GDI+ 19–22 ms cố định; DoD plan (≤ 27 ms) cũng **miss** |
| Annotate — revision **≤ 40 ms/khung @ ~60 box** (benchmark code thật, Debug; user duyệt in principle 2026-10-21) | 37,6 ms @63 box | **PASS** theo revision **đề xuất** — không phải pass theo DoD cũ (≤ 27 ms, miss); revision đã ghi vào AGENTS.md |
| Core CPU scripted (target plan ≤ 0,55 core) | **0,455–0,46 core** (0,81 → −43 %) | **PASS** |
| Core RAM khi probe | max **321–330 MB** (546 → −40 %) | **PASS** (thấp hơn baseline rõ rệt) |
| Tổng CPU < 8 % / RAM < 500 MB | sidecar Paddle **~1,25 core / ~2 GB** (app-driven 19/10, không đo lại) | **vẫn FAIL** — nguyên nhân duy nhất còn lại là sidecar (open item 2026-10-19: chờ user quyết ONNX Runtime hay giữ Paddle) |
| Camera ~10 fps / OCR VRAM < 1 GB | 8,5–9 fps / 362 MB | không đổi (không đo lại round này) |

### Ghi chú / hạn chế đo

1. **Gotcha vận hành probe (2026-10-21):** redirect stderr của Core rồi đọc lười (`ReadToEndAsync` trong probe) làm **stall load model Paddle** của sidecar trong môi trường này — sidecar kẹt ~416 MB, **0 chu kỳ OCR trong 120 s**. Probe chạy được **kế thừa** stderr của Core. Harness Core tự viết phải drain stderr bằng reader event-based (`BeginErrorReadLine`) hoặc kế thừa stderr.
2. **Chênh A/B vs benchmark code thật:** slope A/B post-fix (≈49 ms/khung) bị thổi phồng bởi chênh tần chu kỳ OCR giữa hai lượt (3,9 vs 2,3 chu kỳ/s) — benchmark code thật (37,6 ms @63 box) là số/khung chính thức.
3. **App-driven chưa đo lại (pending user session):** cần mở App + game + OBS + camera thật ≥ 2 phút × 2 cửa sổ để đo lại sau fix (kỳ vọng Core ≤ 1,2 core theo DoD plan) và nhờ user xác nhận pill trên preview/camera bằng mắt.
4. **Google 429** vẫn là blocker của nhánh dịch thành công (như M4/M5) — `translated` ở bảng trên là số notification `frame.translated`, không phải bản dịch thành công.
5. **37,6 ms @63 box là upper-bound bảo thủ:** lượt benchmark chính thức đo dưới tải nền cao (min 29,56 ms); chạy lại cùng code (21/10) được **17,83 ms** @63 box (min 15,89) — coi 37,6 ms là trần, không phải số điển hình của máy.
6. **Reconciliation và cả hai verdict budget annotate ở trên (FAIL 3,8× / PASS theo revision ≤ 40 ms) đều dựa trên upper-bound 37,6 ms này** — đọc kèm ghi chú 5.

### Lệnh reproduce (M5-fix)

- Probe (đo) + probe chẩn đoán stall stderr: `Tools/Perf/probe_after.ps1`, `Tools/Perf/probe_diag.ps1` — hướng dẫn + điều kiện đo xem `Tools/Perf/README.md`.
- CSV thô T6c/T6d/T6e: `Tools/Perf/data/probe_T6{c,d,e}_*.csv` (nguồn bảng trước→sau ở trên; kèm `probe_T6b_*.csv` — bằng chứng run bị stall stderr). CSV khác của round fix 20–21/10 cũng nằm trong `Tools/Perf/data/`.
- Benchmark code thật: `Tools/Perf/annotbench/` — `dotnet run -c Debug --project Tools/Perf/annotbench` (reference `ReTran.Core`).
- Script `%TEMP%\opencode\` cũ của M5 (các script **gốc 2026-10-19** của lượt M5, khác các probe đã promote vào `Tools/Perf/` ở trên) **đã bị dọn 2026-10-21** (xem ghi chú ở mục reproduce của §M5).

---

## Annotate style v2 — tick + pill theo mockup (2026-10-22)

Đổi cách vẽ box ở **cả** preview/camera ảo (Core) và overlay trên game (App): tick teal
`#4FD8C4` 4 góc + pill tối phủ kín lòng box (inset 4 trái/trên/dưới, sát phải), chữ `#F3EEDD`.
Cỡ chữ scale theo **chiều cao box** (`clamp(0.5 × boxH, 12, 40) × FontScale`, thu nhỏ tới 9 px),
wrap tối đa 2 dòng; font cache theo cỡ nguyên 9–40 px.

### Benchmark code thật (Debug, frame synthetic 1920×1080, 20 reps/N)

| Snapshot | Trước (baseline 22/10) | Sau | Δ |
| --- | --- | --- | --- |
| 0 box | 11,95 ms | 11,65 ms | −0,3 ms |
| **63 box** | **19,37 ms** | **16,79 ms** | **−2,6 ms (−13 %)** |
| 150 box | 24,07 ms | 22,38 ms | −1,7 ms |
| 300 box | 35,78 ms | 30,61 ms | −5,2 ms |

- Budget annotate **≤ 40 ms/khung @ ~60 box**: **PASS** (16,8 ms @63 box).
- Nhanh hơn chủ yếu nhờ font cache + không tạo `Font`/format mới mỗi khung; chi phí tick + pill
  bo góc nằm trong nhiễu.
- Chưa đo app-driven/overlay thật trong game (cần user mở game) — số trên là benchmark code thật,
  cùng phương pháp với §M5-fix.

---

## M1 capture baseline

(chưa đo — ghi tại milestone có capture loop chạy thật)

---

## OCR model A/B — PP-OCRv5 mobile vs PP-OCRv6 (2026-10-25)

### Bối cảnh đo

- **Máy:** như §M5 (i5-13400F 10c/16t; RTX 5060 Ti — `nvidia-smi`: 16311 MiB). Stack xác minh lúc đo: `paddleocr 3.7.0` + `paddlepaddle-gpu 3.4.0` **CUDA build** (`is_compiled_with_cuda() → True`) + `paddlex 3.7.2`, `numpy 2.3.5`.
- **Bench:** `Tools/Perf/ocrbench/bench.py` — chạy đúng handler sản xuất `make_spot_handler(PaddleOcrEngine(det, rec))` qua đường base64 `{image}` (decode PNG + downscale 1280 + inference đều nằm trong `elapsed_ms` mà sidecar báo mỗi chu kỳ). Mỗi cặp model chạy **một process riêng** (A/B sạch, không chia sẻ VRAM/kết quả); **1 lượt warm-up bỏ qua** (nạp model + init CUDA) trước khi tính giờ — số dưới đây là steady-state.
- **Frames:** 24 PNG 1920×1080 chụp màn hình 0 thật bằng `ReTran.Core.exe capture screen` trong 28,6 s (màn HDR → đường GDI fallback như §OCR GPU); 7 nội dung khác nhau (SHA1). Nội dung = desktop thật của user lúc đo: cửa sổ terminal/agent đang soạn lệnh export Godot/Android (text kỹ thuật tiếng Anh), UI tiếng Việt, taskbar/tray — **không cố định nội dung**, đây là đo thực địa.
- **Model cache:** v5 mobile có sẵn sẵn trong `~/.paddlex/official_models`; v6 small tải lần đầu **30,1 MB trong warm-up** (không tính vào số).
- **Nền máy:** khá rảnh so với các đợt đo trước (M2–M5: 42–96%): CPU nền 4,4–9,3% trước khi chụp frame, 3–15% quanh các run bench — không có game/OBS.

### Số liệu (24 khung, steady-state sau 1 warm-up)

| Cặp (det + rec) | Warm-up (bỏ qua) | min | median | mean | max | spots/khung | VRAM (delta `nvidia-smi`) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| v5 mobile (`PP-OCRv5_mobile_det` + `en_PP-OCRv5_mobile_rec`) | 13,2 s | 196 | **213,0 ms** | 212,8 | 245 | **81,0** | +369 MiB |
| **v6 small — mặc định** (`PP-OCRv6_small_det` + `PP-OCRv6_small_rec`) | 27,1 s (gồm tải 30,1 MB) | 338 | **352,5 ms** | 355,1 | 390 | **58,2** | +707 MiB |
| v6 tiny (`PP-OCRv6_tiny_det` + `PP-OCRv6_tiny_rec`) | 5,6 s | 212 | **226,0 ms** | 227,6 | 245 | 74,0 | +425→505 MiB |

Chất lượng text trên **cùng một dòng UI tiếng Việt** (frame_01; text thật: "Tiếp tục nâng cấp bản dịch"):

| Cặp | Text nhận được | Nhận xét |
| --- | --- | --- |
| v5 mobile (rec en) | `Tiép tyc nang cap ban dich d:` | méo tiếng Việt hoàn toàn — rec en không hỗ trợ `vi` |
| v6 small | `Tiép tuc nâng cáp bàn djch d:` | gần đúng hơn nhiều, vẫn sai dấu ở vài chữ |
| v6 tiny | `Tiép tuc nâng cáp bân dich d` | tương tự v6 small, nhiễu ký tự hơi nhiều hơn |

Text kỹ thuật tiếng Anh: v6 small sạch nhất — v5 đọc `andzoid`, `LASTExITCoDE`; v6 tiny có lỗi ghép ký tự (`jlexport_presets.cfg` thay vì `export_presets.cfg`). Spot/khung thấp hơn của v6 small (58,2 vs 81,0) = det chọn lọc hơn, bớt box rác/fragment ⇒ giảm tải đường annotate + diff translation downstream.

### Kích thước request OCR — shm vs base64 (trên chính bộ 24 frame này)

| Đường | Kích thước request/khung | Nguồn |
| --- | --- | --- |
| `{image}` PNG base64 (đường bench) | **271 125 B (~265 KB)** mean | đo trong bench (PNG 198,6 KB → base64) |
| `{seq,width,height}` shm (đường production) | **36 B** | đo trong bench |
| Tham chiếu: JPEG base64 thời T4 | ~200 KB | e2e Task-8 (`8665f6e`), nội dung khác |
| Tham chiếu: pin test C# | params < 128 B cho khung 1080p | `RunAsync_SpotParams_AreSeqWidthHeight_NotImage` |

Request OCR qua shm nhỏ hơn ~7 500× so với PNG-base64 trên nội dung này. (Con số "~490 KB" ước tính khi lập kế hoạch không tái hiện trên desktop này — kích thước base64 phụ thuộc nội dung; máy này PNG 1080p chỉ 198,6 KB/khung.)

### Đối chiếu budget

| Budget | Đo được | Kết luận |
| --- | --- | --- |
| OCR ~3 fps (≤ ~333 ms/chu kỳ) | v5 mobile 213 ms median → **~4,7 fps**; v6 tiny 226 ms → **~4,4 fps**; v6 small 352,5 ms → **~2,8 fps** | v5 mobile / v6 tiny **PASS**; v6 small **≈ PASS** (thấp hơn mục tiêu ~3 fps khoảng 6% — đọc kèm hạn chế 1–2) |
| OCR VRAM < 1 GB | v5 mobile +369 MiB · v6 small +707 MiB · v6 tiny +425–505 MiB | **PASS** cả ba (xấp xỉ — hạn chế 3) |
| (tham chiếu) e2e shm Task-8, cặp v6 small, ít chữ | steady 261 ms/chu kỳ | transport shm không phải nút cổ chai |

**Verdict:** cặp mặc định **v6 small** đạt VRAM và đạt xấp xỉ ~3 fps trên nội dung dày chữ (58 box/khung); chậm hơn v5 mobile ~1,66× nhưng đổi lại đọc được text đa ngôn ngữ có dấu (v5-en méo hoàn toàn tiếng Việt) và box sạch hơn (58,2 vs 81,0 spots/khung). Không có lý do đổi mặc định; nếu cần ~4,4 fps thì **v6 tiny** là phương án giữa (chất lượng text hơi kém hơn v6 small, vẫn đọc được tiếng Việt tốt hơn hẳn v5-en).

### Ghi chú / hạn chế đo (đọc trước khi dùng số)

1. **Nội dung không kiểm soát** — desktop thật (terminal Godot/Android + UI tiếng Việt + taskbar). Ba cặp chạy trên **cùng một bộ 24 PNG** nên A/B là so sánh đúng trên cùng pixel; nhưng spot/khung khác nhau theo model (81/58/74) nên `elapsed_ms` là chi phí thực của từng cặp trên nội dung này, không phải "cùng một việc" — không khái quát hoá cho nội dung khác (mật độ chữ và số box quyết định).
2. **Đường `{image}` khác đường production shm:** bench tính decode PNG + downscale 1280 trong `elapsed_ms`; production shm gửi BGRA full-res 1920×1080 **không downscale** ⇒ det chạy trên ảnh lớn hơn. Tham chiếu chéo: e2e Task-8 (đường shm, cặp v6 small, ít box) steady 261 ms/chu kỳ. Không chạy A/B trên đường shm vì cần C# writer chạy đồng thời.
3. **VRAM là delta `nvidia-smi --query-gpu=memory.used` toàn GPU** quanh lúc nạp model — driver này **không hỗ trợ per-process** (`--query-computed-apps` bị từ chối với "Option not recognized", hành vi WDDM); số có thể dính nhiễu từ process GPU khác trong ~30 s run, nhưng after_warmup = after_loop với v5/v6 small nên nhiễu thấp.
4. **Nền 3–15% CPU** — thấp hơn hẳn mọi đợt đo trước (M2–M5: 42–96%); số này gần quiet-machine hơn, nhưng vẫn không phải máy trống tuyệt đối.
5. `elapsed_ms` là `int` ms (làm tròn xuống phía sidecar) — mỗi khung chỉ chính xác đến 1 ms; median/mean trên 24 khung thì đủ mượt.
6. Chất lượng text chỉ so bằng mắt trên vài dòng anchor (không có ground-truth/WER) — kết luận chất lượng là định tính, không định lượng.

### Lệnh reproduce

```powershell
# 1) Chụp ≥20 khung thật (Core exe Debug; màn HDR tự rơi vào GDI fallback — đúng cảnh báo trong stderr)
1..24 | ForEach-Object {
  & "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe" capture screen --monitor 0 `
    --out ("Tools\Perf\ocrbench\data\frame_{0:D2}.png" -f $_) | Out-Null
  Start-Sleep -Milliseconds 750
}
# 2) Mỗi cặp một process riêng; warm-up 1 lượt tự bỏ qua (v6 small lần đầu tự tải 30,1 MB)
uv run --project ReTran.OCR python Tools/Perf/ocrbench/bench.py --data Tools/Perf/ocrbench/data `
  --det PP-OCRv5_mobile_det --rec en_PP-OCRv5_mobile_rec --label v5_mobile `
  --csv Tools/Perf/ocrbench/bench_v5_mobile_20261025.csv
uv run --project ReTran.OCR python Tools/Perf/ocrbench/bench.py --data Tools/Perf/ocrbench/data `
  --det PP-OCRv6_small_det --rec PP-OCRv6_small_rec --label v6_small `
  --csv Tools/Perf/ocrbench/bench_v6_small_20261025.csv
uv run --project ReTran.OCR python Tools/Perf/ocrbench/bench.py --data Tools/Perf/ocrbench/data `
  --det PP-OCRv6_tiny_det --rec PP-OCRv6_tiny_rec --label v6_tiny `
  --csv Tools/Perf/ocrbench/bench_v6_tiny_20261025.csv
# CSV per-frame (elapsed_ms, spots, texts): Tools/Perf/ocrbench/bench_*_20261025.csv
# Log run đầy đủ (local, không commit): Tools/Perf/ocrbench/data/bench_*.log
```
