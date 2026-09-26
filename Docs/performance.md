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

- **Perf gate T2/T3 applied:** pin `PP-OCRv5_mobile_det` + `en_PP-OCRv5_mobile_rec` (bỏ doc-preprocess pipelines), downscale long-side **1280 px** trước detect (Python-side trong `decode_png_b64` — áp cho cả one-shot lẫn loop). Paddle **CPU build** → VRAM 0.
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

## M1 capture baseline

(chưa đo — ghi tại milestone có capture loop chạy thật)
