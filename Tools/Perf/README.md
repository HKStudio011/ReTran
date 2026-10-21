# Tools/Perf — script & dữ liệu đo perf ReTran

Công cụ đo hiệu năng dùng cho các bảng số trong `Docs/performance.md` (đặc biệt §M5-fix). Các script
độc lập vị trí: suy ra repo root từ `$PSScriptRoot`, nên chạy được từ bất kỳ thư mục nào trong repo.

## Nội dung

| File | Vai trò |
| --- | --- |
| `probe_after.ps1` | Probe đo scripted sau tối ưu: ghi `captureFps` vào `%LOCALAPPDATA%\ReTran\core\config.json`, spawn `ReTran.Core.exe --serve`, trả lời `translate.request_cache` all-miss đúng như App, đo CPU/RAM Core, số khung + fps + KB/khung annotated, MB/s stdout, boxes, và đọc header MMF preview (`Local\ReTran.Preview.V1`, magic JPEG). Ghi CSV cạnh script (`probe_<Label>_<timestamp>.csv`). |
| `probe_diag.ps1` | Biến thể chẩn đoán: kế thừa stderr của Core (thấy log `[sidecar]` của Paddle), log reply ping/start, sample RAM/CPU sidecar + Core theo thời gian. Dùng khi nghi pipeline bị stall. |
| `annotbench/` | Benchmark code thật: project console reference `ReTran.Core`, gọi thẳng `FrameAnnotator.Annotate` trên frame synthetic 1920×1080 với `null`/0/63/150/300 box — in avg/p50/min ms và JPEG KB. |
| `data/` | CSV số đo thật — nguồn của các bảng trong `Docs/performance.md` (bảng dưới). |

## Cách chạy

```powershell
# Build trước (probe chạy ReTran.Core.exe Debug; annotbench build core qua ProjectReference)
dotnet build "ReTran Project.slnx"

# Probe đo (settle 40 s + đo 60 s, bỏ 15 s đầu — tham số mặc định)
pwsh -NoLogo -NoProfile -File Tools/Perf/probe_after.ps1 -SettleSeconds 40 -Seconds 60 -DiscardSeconds 15 -AnnotFps 10 -Label T7

# Probe chẩn đoán (nghi stall)
pwsh -NoLogo -NoProfile -File Tools/Perf/probe_diag.ps1 -Minutes 3 -SampleEvery 10 -AnnotFps 10

# Benchmark annotate code thật
dotnet run -c Debug --project Tools/Perf/annotbench
```

### Điều kiện đo (chuẩn hoá số)

- **Nội dung màn hình phải mô tả** — số box/khung là chuẩn hoá của mọi so sánh. Lượt đo probe gốc dùng
  **Notepad 260 dòng, maximize** (~63 box/khung); muốn so chéo hai lượt phải giữ cùng nội dung màn hình.
- **Máy đo:** i5-13400F (10C/16T), RTX 5060 Ti (paddlepaddle-gpu cu129), 2 màn hình HDR (screen 0 đi qua
  GDI fallback), nền máy không idle — số CPU ghi theo CPU-seconds per-process.
- `annotbench` đo bằng **Debug build** (khớp binary probe), 20 reps/N, avg/p50/min.

### Lưu ý

- `annotbench/` **không nằm trong `ReTran Project.slnx`** — build/chạy theo nhu cầu (`dotnet run`);
  có thể hỏng âm thầm nếu chữ ký `FrameAnnotator`/`AnnotSnapshot`/`AnnotatedFrame` đổi mà không ai chạy nó.
- Probe tạm thời **ghi đè** `%LOCALAPPDATA%\ReTran\core\config.json` bằng `{ "captureFps": N }` rồi khôi
  phục lại khi kết thúc — **đóng App trước khi probe** (App đang mở sẽ thấy config bị lột còn mỗi captureFps).
- `data/probe_T6b_*.csv` là **artifact lịch sử** của lần chẩn đoán stall stderr: chứa trường `stderr_log=`
  / `core_peak_wsMB=0` mà probe hiện tại không còn xuất — không tái tạo được bằng probe đã ship.

### Gotcha stderr (bắt buộc đọc)

**Đừng redirect stderr của Core rồi đọc lười** (`ReadToEndAsync` trong probe) — trong môi trường này nó
làm **stall load model Paddle** của sidecar: sidecar kẹt ~416 MB, 0 chu kỳ OCR trong 120 s
(xem `data/probe_T6b_*.csv`). Hai script kèm theo **kế thừa** stderr (`RedirectStandardError = $false`).
Harness Core tự viết phải drain stderr event-based (`BeginErrorReadLine`) hoặc kế thừa stderr.

## CSV ↔ tài liệu

| CSV | Nội dung | Nguồn trong docs |
| --- | --- | --- |
| `data/probe_T6c_20261021_070120.csv` | Sau fix, annotate 10 fps, Notepad 260 dòng | `Docs/performance.md` §M5-fix — cột "T6c" bảng trước→sau |
| `data/probe_T6d_20261021_070828.csv` | Sau fix, annotate 2 fps (A/B slope, cùng nội dung màn hình) | §M5-fix — đoạn A/B post-fix |
| `data/probe_T6e_20261021_071351.csv` | Lặp lại T6c | §M5-fix — cột "T6e — lặp lại" |
| `data/probe_T6b_20261021_064442.csv` | Run bị stall (stderr redirect, sidecar 416 MB) | §M5-fix — ghi chú 1 (gotcha stderr) |
| `data/ab_annotate_T1BASE.csv` | Baseline trước fix (20/10) | Plan Task 2–3 — đối chứng A/B của lượt baseline |
| `data/ab_annotate_T2BASE.csv` | A/B Task 2 (pool BGRA) — binary **no-pool** đối chứng | Plan Task 2 — parity A/B trong noise |
| `data/ab_annotate_T2.csv` | A/B Task 2 — binary **pool** | Plan Task 2 — parity A/B trong noise |
| `data/ab_annotate_T3.csv` | Round Task 3 (JPEG khung qua MMF `Local\ReTran.Preview.V1`) | Plan Task 3 — đường MMF |

Bốn CSV `ab_annotate_*` là bằng chứng **Task 2** (pool A/B parity `T2`/`T2BASE`) và **Task 3** (round MMF
`T3`/`T1BASE`) của plan `Docs/superpowers/plans/2026-10-19-pipeline-perf-fix.md`; kết luận của chúng nằm
trong plan + task report SDD (`.superpowers/sdd/2026-10-19-pipeline-perf-fix/task-{2,3}-report.md`) —
**không** nằm trong bảng nào của `Docs/performance.md`.

CSV mới do probe sinh ra nằm cạnh script (`Tools/Perf/probe_<Label>_<timestamp>.csv`) và **đã bị gitignore**
(rule `Tools/Perf/probe_*.csv` trong `.gitignore` — chỉ dành cho run output); nếu muốn giữ làm số liệu
chính thức, copy **có chủ đích** vào `data/` và thêm dòng vào bảng trên.
