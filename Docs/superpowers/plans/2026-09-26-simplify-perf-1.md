# Simplify + Perf — Plan 1 (Baseline + Cleanup + GPU Probe) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đo T6 baseline OCR (CPU + mobile det) — máy đang tải cũng được, ghi background load làm confounder — dọn dẹp dependency/dead code/trùng lặp không đổi hành vi, và probe paddlepaddle-gpu trên RTX 5060 Ti để chốt hướng Plan 2 (ONNX hay ở lại paddle).

**Architecture:** Không đổi kiến trúc — spec `Docs/superpowers/specs/2026-09-26-cleanup-gpu-perf-design.md` governs. Task 1 là measurement (không code). Task 2–7 là dọn dẹp có kiểm thử: mỗi task giữ nguyên test suite (C# 70 / Python 15) và chỉ cắt phần chết/trùng. Task 8 probe GPU trong **venv riêng** (`%TEMP%`), không đụng venv dự án; kết quả ghi vào appendix của spec để Plan 2 (ONNX/DirectML + đo lại) viết trên sự thật.

**Tech Stack:** .NET 10 (solution `ReTran Project.slnx`, tests TFM `net10.0-windows10.0.19041.0`), Vite + TS (`ReTran.App/vite-project` → `../wwwroot/build`, assets TRACKED), uv/Python 3.13 (`ReTran.OCR/`), PowerShell 7 (đo `Get-Counter`, `Measure-Command`, `Start-Job`).

## Global Constraints

(Copy verbatim từ spec `2026-09-26-cleanup-gpu-perf-design.md`.)

- Spec governs: `Docs/superpowers/specs/2026-09-26-cleanup-gpu-perf-design.md`.
- Preflight đo (quyết định user 2026-09-26 — thay idle hard gate): vẫn chạy `Get-Counter` (3 sample × 2s) để **ghi background load làm confounder**, nhưng **không STOP** khi > 20%. Attribution (pipeline tốn bao nhiêu) dùng **per-process CPU** của `ReTran.Core.exe` + sidecar (Step 6 psutil) — số này có ý nghĩa cả khi máy bận. Gate < 8% CPU khi gaming+streaming đo ở Phase 3 (Plan 2) dưới tải thật.
- Không claim PASS/BREACH mà không có số đo thật; mọi số trong `Docs/performance.md` copy từ output thật của step đo, không ước lượng.
- Test baseline: C# **70** / Python **15** — không giảm test, không giảm assertion; skip-êm qua early return (không `Assert.Skip`); không `Console.WriteLine` trong test body.
- Commands: `dotnet build "ReTran Project.slnx"`; `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`; `uv run pytest tests -v` (từ `ReTran.OCR/`); `npm run build` (từ `ReTran.App/vite-project`).
- New publics: bilingual `///` docs (EN sentence trước, VI sau); Python publics Google-style docstring.
- Commit nhỏ mỗi task; stage đúng file của task; message đúng như mỗi task nêu.
- Không đổi kiến trúc: 2 stack JSON-RPC và interface 1-impl giữ nguyên; contract `ocr.spot` giữ nguyên; debug buttons `MainPage` (harness Appium) và `DemoFeed` GIỮ LẠI.
- Bootstrap CSS **giữ** (NavMenu/MainLayout còn dùng class bootstrap — Task 4 Step 4 xác nhận trước khi dọn).
- Probe Task 8 chạy trong venv %TEMP% riêng — cấm `uv pip install`/`uv add` vào venv dự án `ReTran.OCR/.venv`.

---

### Task 1: T6 baseline measurement (Phase 0 — đo, không code)

**Files:**
- Modify: `Docs/performance.md` (chèn section M3 ngay trước dòng `## M1 capture baseline`)
- Test: none (measurement task) — counts giữ nguyên (C# 70, Python 15)

**Interfaces:**
- Consumes: exe đã build (`ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe` — đã có perf gate mobile det + 1280 cap từ M3 T1–T5), loop CLI `ocr loop`, M3 plan Task 6 làm checklist đo.
- Produces: section M3 trong `Docs/performance.md` (số CPU+mobile-det baseline) — số tham chiếu để Plan 2 đối chiếu GPU.

- [ ] **Step 1: Build exe**

```powershell
dotnet build "ReTran Project.slnx"
```
Expected: Build succeeded, 0 lỗi. (Không build → Task sau dùng exe cũ/ thiếu.)

- [ ] **Step 2: Background CPU snapshot (confounder — không STOP)**

```powershell
$samples = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 3).CounterSamples | ForEach-Object { $_.CookedValue }
$avg = ($samples | Measure-Object -Average).Average
"bg CPU avg: $avg %"
```

- Ghi lại giá trị + thời điểm — đây là confounder, đưa vào section "Bối cảnh đo" của doc (bắt buộc, kể cả ≤ 20%).
- **Không STOP** khi > 20% (quyết định user 2026-09-26: đo ngay khi máy bận; attribution dựa vào per-process CPU ở Step 6).

- [ ] **Step 3: Tạo fixtures**

Chạy từ `ReTran.OCR/` (uv context có PIL):

```powershell
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
```
Expected: `fixtures at C:\Users\trucn\AppData\Local\Temp` (hoặc TEMP path tương ứng).

- [ ] **Step 4: One-shot cold + warm N=5 (2 fixture)**

Chạy từ repo root:

```powershell
$exe = "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe"
(Measure-Command { & $exe ocr "$env:TEMP\m3-perf-1080p.png" | Out-Null }).TotalSeconds
(Measure-Command { & $exe ocr "$env:TEMP\m3-perf-640x96.png" | Out-Null }).TotalSeconds
1..5 | ForEach-Object { (& $exe ocr "$env:TEMP\m3-perf-1080p.png" | ConvertFrom-Json).elapsed_ms }
1..5 | ForEach-Object { (& $exe ocr "$env:TEMP\m3-perf-640x96.png" | ConvertFrom-Json).elapsed_ms }
```
Expected: 2 số cold (s), 2 dãy 5 số `elapsed_ms`. Ghi min/median/mean mỗi dãy. (Lần đầu có thể tải model — chạy warm-up 1 lần trước nếu muốn tách download; ghi rõ trong doc cách làm.)

- [ ] **Step 5: Loop throughput**

```powershell
$sw = [Diagnostics.Stopwatch]::StartNew()
$out = & $exe ocr loop --monitor 0 --fps 3 --max-frames 10
$sw.Stop()
"wall_s: $([math]::Round($sw.Elapsed.TotalSeconds,2))  achieved_fps: $([math]::Round(10/$sw.Elapsed.TotalSeconds,2))"
$out | ForEach-Object { ($_ | ConvertFrom-Json).elapsed_ms }
```
Expected: 10 dòng JSON → ghi `elapsed_ms` median + fps đạt được.

- **Fallback nếu `--monitor 0` fail** (HDR/duplicate — đã thấy ở M2): mở Notepad chứa text fixture, chạy `ocr loop --hwnd 0x<MainWindowHandle> --max-frames 10`; ghi rõ trong doc nguồn nào xài được. Cả 2 nguồn fail → ghi failure verbatim + dùng số one-shot cho acceptance table với ghi chú "loop not measurable (lý do)".

- [ ] **Step 6: CPU/RAM trong lúc loop**

```powershell
$job = Start-Job -ScriptBlock { & "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe" ocr loop --monitor 0 --fps 3 --max-frames 30 }
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
rows = [{'name': p.info['name'],
         'cpu_pct': p.cpu_percent(None),
         'rss_mb': round(p.memory_info().rss / 2**20, 1),
         'peak_mb': round((p.memory_info().peak_wset or 0) / 2**20, 1)}
        for p in procs if p.is_running()]
print(json.dumps(rows, indent=2))
"@
Receive-Job $job; Remove-Job $job -Force
```
(Cmdline filter `retran_core` chỉ bắt sidecar của run này, không bắt python máy.)

Expected: rows JSON (core + sidecar parent + worker). Method note cho doc: `peak_mb` = `peak_wset` per-process, sum theo tree = caveat M2. Chạy từ `ReTran.OCR/` cho lệnh `uv run` (hoặc dùng full path `uv run --project` — ghi rõ cách chạy thật trong doc).

- [ ] **Step 7: Ghi section M3 vào `Docs/performance.md`**

Chèn **ngay trước** dòng `## M1 capture baseline`, mirror cấu trúc section M2:

```markdown
## M3 frame loop + perf gate (2026-09-26)

### Máy đo
(Bảng style M2: CPU/RAM/GPU — note: T2/T3 applied — mobile det + 1280 cap.)

### Bối cảnh đo (background load — quan trọng khi đọc số)
(avg từ Step 2 + giờ đo; section này bắt buộc nêu rõ background load — số có thể chịu contention, attribution dùng per-process CPU ở "CPU / RAM".)

### Số liệu — one-shot (cold + warm N=5, hai fixture)
| Fixture | Cold (s) | Warm min | Warm median | Warm mean | Ghi chú |
(số Step 4; so với baseline M2.)

### Số liệu — loop throughput
(fps đạt được, elapsed_ms median / 10 frame, nguồn dùng được + disclosure fallback.)

### CPU / RAM (trong lúc loop)
(rows Step 6 + method note; tree-peak = sum caveat.)

### Đối chiếu budget (AGENTS.md)
| Row | M2 baseline | M3 measured | Budget | Verdict |
(OCR ≤333 ms → verdict theo median đo được; VRAM 0 (CPU paddle); RAM idle/active; các row chưa có → N/A.)

### Ghi chú / hạn chế đo
(disclosure: gì fail, fallback nào xài, confounders.)

### Lệnh reproduce
(lệnh thật từ Steps 1–6.)
```

Mọi số **copy từ output thật Steps 2–6** — không ước lượng. Row đo fail → ghi "đo thất bại (lý do)", không bỏ row.

- [ ] **Step 8: Self-review + commit**

Check: mọi số trace về output Step 2–6; verdict rõ PASS/BREACH/N/A (hoặc provisional nếu load borderline); không đụng product code; fixture PNG/temp script KHÔNG stage.

```bash
git add Docs/performance.md
git commit -m "docs(perf): M3 frame-loop + perf-gate measured numbers"
```

---

### Task 2: Gỡ jquery khỏi frontend

**Files:**
- Modify: `ReTran.App/vite-project/src/ts/main.ts`
- Modify: `ReTran.App/vite-project/package.json`
- Modify: `ReTran.App/vite-project/package-lock.json` (npm install)
- Modify: `ReTran.App/wwwroot/build/assets/*` (build output — TRACKED trong git)

**Interfaces:**
- Consumes: không có — đã xác minh 0 usage `$(`/jQuery trong `ReTran.App` (.razor/.cs/.html/.ts trừ chính main.ts) và `theme-manager.ts` không import jquery.
- Produces: bundle không còn jquery; `main.ts` xuất ra `applyTheme/applyColor/setTheme/setColor/initTheme/copyText` (giữ nguyên các export khác — `index.html` load `build/assets/main.js` module).

- [ ] **Step 1: Ghi kích thước bundle trước**

```powershell
(Get-Item "ReTran.App\wwwroot\build\assets\main.js").Length
```
Expected: **103429** (baseline khảo sát). Ghi lại.

- [ ] **Step 2: Sửa `main.ts`**

Thay **toàn bộ nội dung** file bằng:

```typescript
import '../css/style.css';

export { applyTheme, applyColor, setTheme, setColor, initTheme } from './theme-manager'

/** Copies text to the clipboard. Sao chép văn bản vào clipboard. */
export async function copyText(text: string): Promise<void> {
  await navigator.clipboard.writeText(text);
}
```

(Xóa dòng `import $ from "jquery";` và `export { $ };` — đã xác minh không nơi nào dùng `$`.)

- [ ] **Step 3: Sửa `package.json`**

Thay **toàn bộ nội dung** file bằng:

```json
{
  "name": "vite-project",
  "private": true,
  "version": "0.0.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc && vite build",
    "preview": "vite preview",
    "watch": "chokidar \"../**/*.razor\" \"src/**/*.{ts,js}\" \"src/**/*.css\" -c \"tsc && vite build\""
  },
  "devDependencies": {
    "@tailwindcss/vite": "^4.3.3",
    "chokidar-cli": "^3.0.0",
    "tailwindcss": "^4.3.3",
    "typescript": "~7.0.2",
    "vite": "^8.2.2"
  }
}
```

(Xóa `"@types/jquery"` và cả block `"dependencies"`.)

- [ ] **Step 4: Cập nhật lockfile + verify jquery biến mất**

```powershell
# workdir: ReTran.App\vite-project
npm install
Select-String -Path package-lock.json -Pattern '"jquery"' | Measure-Object | Select-Object -ExpandProperty Count
```
Expected: `npm install` thành công; count = **0**.

- [ ] **Step 5: Build + verify bundle**

```powershell
# workdir: ReTran.App\vite-project
npm run build
(Get-Item "..\wwwroot\build\assets\main.js").Length
Select-String -Path "..\wwwroot\build\assets\main.js" -Pattern 'jquery' | Measure-Object | Select-Object -ExpandProperty Count
```
Expected: tsc + vite build 0 lỗi (tsconfig `strict` + `noUnusedLocals` sẽ bắt import chết); size **nhỏ hơn đáng kể** (ghi số mới); jquery count = **0**.

- [ ] **Step 6: Sanity build solution**

```powershell
dotnet build "ReTran Project.slnx"
```
Expected: Build succeeded (bundle là static content của App).

- [ ] **Step 7: Commit**

```bash
git add ReTran.App/vite-project/package.json ReTran.App/vite-project/package-lock.json ReTran.App/vite-project/src/ts/main.ts ReTran.App/wwwroot/build
git commit -m "chore(app): drop unused jquery from frontend bundle"
```

---

### Task 3: Gỡ torch khỏi Python deps

**Files:**
- Modify: `ReTran.OCR/pyproject.toml` (bỏ 1 dòng)
- Modify: `ReTran.OCR/uv.lock` (uv sync)

**Interfaces:**
- Consumes: fact từ `uv.lock`: chỉ `retran-core` (project ta) khai `torch` — `paddleocr` deps = paddlex/pyyaml/requests/typing-extensions, `paddlepaddle` deps không có torch, `paddlex` deps không có torch → `uv sync` sẽ prune hẳn torch.
- Produces: venv nhẹ hơn (~nhiều GB); sidecar vẫn chạy (pytest integration thật sẽ chứng minh).

- [ ] **Step 1: Sửa `pyproject.toml`**

Thay block dependencies thành (bỏ đúng dòng `"torch>=2.14",`):

```toml
dependencies = [
    "numpy>=2.5.3",
    "paddleocr>=3.4.1",
    "paddlepaddle>=3.3.1",
    "pillow>=12.3.0",
]
```

- [ ] **Step 2: Sync + verify torch biến mất**

```powershell
# workdir: ReTran.OCR
uv sync
uv pip list | Select-String -Pattern 'torch'
```
Expected: `uv sync` in ra danh sách **Removed … torch …** (và các package chỉ torch kéo vào, vd filelock/jinja2 còn hay không do package khác — không quan tâm, chỉ cần torch biến mất); bước 2 không output (0 match).

- Nếu `uv pip list` **vẫn có torch** → ghi lại (paddleocr/paddlex kéo transitive — nhưng lock evidence nói không), STOP task này, báo lại thay vì ép gỡ.

- [ ] **Step 3: Chạy full Python suite (sidecar thật)**

```powershell
# workdir: ReTran.OCR
uv run pytest tests -v
```
Expected: **15 passed, 0 failed** — `test_paddle_integration.py` load PaddleOCR thật → chứng minh paddle stack không cần torch.

- [ ] **Step 4: Commit**

```bash
git add ReTran.OCR/pyproject.toml ReTran.OCR/uv.lock
git commit -m "chore(ocr): drop unused torch dependency"
```

---

### Task 4: Xóa dead code + MAUI template leftovers

**Files:**
- Delete: `ReTran.App/Core/AppSettings.cs`
- Delete: `ReTran.App/Components/Pages/Counter.razor`
- Delete: `ReTran.App/Components/Pages/Weather.razor`
- Modify: `ReTran.App/Components/Layout/NavMenu.razor` (gỡ 2 nav item)
- Modify: `ReTran.Core/Cli/CommandDispatcher.cs:19` (bỏ `translate` khỏi usage)

**Interfaces:**
- Consumes: khảo sát — `AppSettings` 0 reference ngoài file; `Counter`/`Weather` chỉ được link từ `NavMenu` (grep `.razor/.cs/.xaml`: 0 test, 0 route khác); `CommandDispatcherTests` không assert chuỗi usage.
- Produces: usage mới `ReTran.Core <serve|version|ocr|capture>` (translate quay lại ở M4); NavMenu còn đúng 1 item Home. **Bootstrap CSS giữ nguyên** (Step 4 xác nhận).

- [ ] **Step 1: Xóa 3 file**

```powershell
Remove-Item "ReTran.App\Core\AppSettings.cs", "ReTran.App\Components\Pages\Counter.razor", "ReTran.App\Components\Pages\Weather.razor"
```

- [ ] **Step 2: Gỡ 2 nav item trong `NavMenu.razor`**

Thay **toàn bộ nội dung** file bằng:

```razor
<div class="top-row ps-3 navbar navbar-dark">
    <div class="container-fluid">
        <a class="navbar-brand" href="">ReTran App</a>
    </div>
</div>

<input type="checkbox" title="Navigation menu" class="navbar-toggler" />

<div class="nav-scrollable" onclick="document.querySelector('.navbar-toggler').click()">
    <nav class="nav flex-column">
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
                <span class="bi bi-house-door-fill-nav-menu" aria-hidden="true"></span> Home
            </NavLink>
        </div>
    </nav>
</div>
```

- [ ] **Step 3: Bỏ `translate` khỏi usage `CommandDispatcher`**

`ReTran.Core/Cli/CommandDispatcher.cs` dòng 19 — thay:

```csharp
            Console.Error.WriteLine("usage: ReTran.Core <serve|version|ocr|translate|capture> [args]");
```

bằng:

```csharp
            Console.Error.WriteLine("usage: ReTran.Core <serve|version|ocr|capture> [args]");
```

- [ ] **Step 4: Xác nhận Bootstrap còn usage (quy tắc spec) — KHÔNG gỡ link**

```powershell
Select-String -Path "ReTran.App\Components\**\*.razor" -Pattern 'navbar|nav-link|container-fluid|flex-column|px-\d|ps-\d' | Measure-Object | Select-Object -ExpandProperty Count
```
Expected: **count > 0** (NavMenu `navbar/nav-link/container-fluid/ps-3/px-3/flex-column` + MainLayout `px-4`) → **giữ** `lib/bootstrap` link trong `index.html` + thư mục `wwwroot/lib/bootstrap`. Chỉ gỡ nếu count = 0 (không xảy ra với nội dung hiện tại — ghi lại vào report nếu khác).

- [ ] **Step 5: Dọn `.pyc` trong `ReTran.OCR/ReTran.OCR.pyproj`**

Trong `ReTran.OCR/ReTran.OCR.pyproj` xóa:

- Cả 3 dòng `<Folder Include="...\__pycache__\" />` (dòng 29, 30, 32 — `src\retran_core\ocr\__pycache__\`, `src\retran_core\__pycache__\`, `tests\__pycache__\`)
- Toàn bộ `<ItemGroup>` chứa 13 dòng `<Content Include="...\*.pyc" />` (dòng 34–47)

**Giữ nguyên:** `<Folder Include="src\" />`, `src\retran_core\`, `src\retran_core\ocr\`, `tests\` và cả `<ItemGroup>` `<Compile …>` (dòng 48–61), import/Target. (pyproj không tham gia build — `Build=false` trong slnx — chỉ VS listing.)

- [ ] **Step 6: Build + full suite**

```powershell
dotnet build "ReTran Project.slnx"
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0
```
Expected: build 0 lỗi; **70 passed, 0 failed** (không test nào đụng tới Counter/Weather/AppSettings — đã grep).

- [ ] **Step 7: Commit**

```bash
git add ReTran.App/Core/AppSettings.cs ReTran.App/Components/Pages/Counter.razor ReTran.App/Components/Pages/Weather.razor ReTran.App/Components/Layout/NavMenu.razor ReTran.Core/Cli/CommandDispatcher.cs ReTran.OCR/ReTran.OCR.pyproj
git commit -m "chore: remove MAUI template leftovers and dead settings code"
```

---

### Task 5: Gộp encoder PNG (PngWriter → BgraPngEncoder)

**Files:**
- Modify: `ReTran.Core/Capture/PngWriter.cs` (giữ public surface, gộp phần encode)

**Interfaces:**
- Consumes: `BgraPngEncoder.ToPngBytes(Frame) -> byte[]` (M3 T1, đã có test 4 fact).
- Produces: `PngWriter.WriteBgra(string path, Frame frame)` — **ký hiệu giữ nguyên** (dùng tại `CaptureCommands.cs:118,152`, test `CaptureCliTests.cs:16`); hành vi: tạo thư mục cha + ghi PNG — không đổi.

- [ ] **Step 1: Baseline test hiện có**

```powershell
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter "FullyQualifiedName~CaptureCliTests|FullyQualifiedName~BgraPngEncoderTests"
```
Expected: **6 passed** (CaptureCli 2 + BgraPngEncoder 4).

- [ ] **Step 2: Thay toàn bộ nội dung `PngWriter.cs`**

```csharp
namespace ReTran.Core.Capture;

/// <summary>
/// Writes a <see cref="Frame"/> (BGRA) to a PNG file via <see cref="BgraPngEncoder"/>.
/// Ghi <see cref="Frame"/> (BGRA) ra tệp PNG qua <see cref="BgraPngEncoder"/>.
/// </summary>
public static class PngWriter
{
    /// <summary>
    /// Writes <paramref name="frame"/> to <paramref name="path"/> as PNG (creates the parent directory when missing).
    /// Ghi <paramref name="frame"/> ra <paramref name="path"/> dưới dạng PNG (tự tạo thư mục cha khi chưa có).
    /// </summary>
    /// <param name="path">Destination file path. Đường dẫn tệp đích.</param>
    /// <param name="frame">Frame to encode (BGRA layout matches Format32bppArgb on little-endian Windows). Frame cần mã hóa.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> or <paramref name="frame"/> is null.</exception>
    public static void WriteBgra(string path, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(frame);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, BgraPngEncoder.ToPngBytes(frame));
    }
}
```

(Toàn bộ encode GCHandle/Bitmap dời sang `BgraPngEncoder` — 1 chỗ duy nhất; usings `System.Drawing*` không còn cần.)

- [ ] **Step 3: Chạy lại test của task**

```powershell
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter "FullyQualifiedName~CaptureCliTests|FullyQualifiedName~BgraPngEncoderTests"
```
Expected: **6 passed** (viết file PNG vẫn đọc lại được).

- [ ] **Step 4: Full suite**

```powershell
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0
```
Expected: **70 passed, 0 failed**.

- [ ] **Step 5: Commit**

```bash
git add ReTran.Core/Capture/PngWriter.cs
git commit -m "refactor(capture): route PngWriter through BgraPngEncoder"
```

---

### Task 6: Gộp path helpers (CoreLocator + OcrBootstrap.FindRepoRoot)

**Files:**
- Create: `ReTran.App/Core/CoreLocator.cs`
- Modify: `ReTran.App/MainPage.xaml.cs` (dùng helper, xóa method private)
- Modify: `ReTran.Tests/AppiumSmokeTests.cs` (dùng helper, xóa method mirror)
- Modify: `ReTran.Tests/SidecarProcessTests.cs` (dùng `OcrBootstrap.FindRepoRoot`, xóa method private)

**Interfaces:**
- Consumes: `ReTran.Core.Ocr.OcrBootstrap.FindRepoRoot(string? startDir = null) -> string?` (public, đã có test `OcrCliTests.FindRepoRoot_FindsDirectoryContainingOcrProject`).
- Produces (dùng ngay trong task này):
  - `public static string? ReTran_App.Core.CoreLocator.FindCoreExe()` — `RETRAN_CORE_EXE` env (tồn tại) thắng, nếu không đi lên từ `AppContext.BaseDirectory` tìm `ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe`; **null khi không thấy** (khác bản MainPage cũ là throw — MainPage tự wrap throw).
  - Tests reference assembly `ReTran.App` rồi (csproj refs cả App) → `using ReTran_App.Core;` là đủ.

**Lưu ý hợp nhất:** bản MainPage đi lên từ `AppContext.BaseDirectory`, bản test đi lên từ `Directory.GetCurrentDirectory()` — hợp nhất về `AppContext.BaseDirectory` (bin dir test cũng đi ngược tới repo root được; cwd test mặc định cũng là bin dir → không đổi hành vi thực tế).

- [ ] **Step 1: Tạo `ReTran.App/Core/CoreLocator.cs`**

```csharp
namespace ReTran_App.Core;

/// <summary>
/// Resolves the ReTran.Core.exe path shared by MainPage and the Appium smoke test.
/// Tìm đường dẫn ReTran.Core.exe dùng chung cho MainPage và smoke test Appium.
/// </summary>
public static class CoreLocator
{
    /// <summary>
    /// Returns the file named by RETRAN_CORE_EXE when it exists; otherwise walks up from the app base
    /// directory to the repo root and returns the default Debug build path. Null when nothing is found.
    /// Trả về tệp do RETRAN_CORE_EXE chỉ tới khi tồn tại; nếu không thì đi lên từ thư mục gốc app
    /// tới repo root và trả về đường dẫn build Debug mặc định. Null khi không tìm thấy.
    /// </summary>
    public static string? FindCoreExe()
    {
        string? env = Environment.GetEnvironmentVariable("RETRAN_CORE_EXE");
        if (env is not null && File.Exists(env)) return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "ReTran.Core", "bin", "Debug", "net10.0-windows", "ReTran.Core.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
```

- [ ] **Step 2: MainPage dùng helper**

`ReTran.App/MainPage.xaml.cs`:

a) Trong `OnStartCoreClicked` (dòng ~48), thay:

```csharp
                string exe = FindCoreExe();
```

bằng:

```csharp
                string exe = CoreLocator.FindCoreExe()
                    ?? throw new FileNotFoundException("ReTran.Core.exe not found. Build it first or set RETRAN_CORE_EXE.");
```

b) Xóa **toàn bộ** method private `FindCoreExe` kèm XML doc (dòng 88–107, bắt đầu `/// <summary>` qua dấu `}` cuối method).

- [ ] **Step 3: AppiumSmokeTests dùng helper**

`ReTran.Tests/AppiumSmokeTests.cs`:

a) Thêm using sau `using OpenQA.Selenium.Appium.Windows;`:

```csharp
using ReTran_App.Core;
```

b) Dòng ~31 thay:

```csharp
        if (CoreLocator.FindCoreExe() is null) return; // Core not resolvable (no exe, no RETRAN_CORE_EXE) -> skip
```

c) Xóa method private `FindCoreExe` + XML doc (dòng ~177–191, bắt đầu `/// <summary>Mirrors MainPage.FindCoreExe()...`).

- [ ] **Step 4: SidecarProcessTests dùng OcrBootstrap**

`ReTran.Tests/SidecarProcessTests.cs`:

a) Thêm using sau `using ReTran.Core.OcrClient;`:

```csharp
using ReTran.Core.Ocr;
```

b) Hai dòng gọi (dòng 13 và 25) thay từng dòng:

```csharp
        if (OcrBootstrap.FindRepoRoot() is not string repoRoot) return;
```

c) Xóa method private `FindRepoRoot` + XML doc (dòng 36–43).

- [ ] **Step 5: Build + full suite**

```powershell
dotnet build "ReTran Project.slnx"
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0
```
Expected: build 0 lỗi; **70 passed, 0 failed** (`SidecarProcessTests` 2 fact chạy path `OcrBootstrap` mới; `AppiumSmokeTests` guard qua `CoreLocator` — Appium không lên thì silent skip như cũ).

- [ ] **Step 6: Commit**

```bash
git add ReTran.App/Core/CoreLocator.cs ReTran.App/MainPage.xaml.cs ReTran.Tests/AppiumSmokeTests.cs ReTran.Tests/SidecarProcessTests.cs
git commit -m "refactor: dedupe FindCoreExe and FindRepoRoot helpers"
```

---

### Task 7: MAUI App Windows-only TFM

**Files:**
- Modify: `ReTran.App/ReTran.App.csproj` (4 TFM → 1, bỏ dòng chết android/ios/maccatalyst)

**Interfaces:**
- Consumes: fact — product là Windows-only (DXGI/HWND capture, Appium test windows-only); `ReTran.Tests` TFM `net10.0-windows10.0.19041.0` đã khớp windows TFM của App.
- Produces: `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>` (singular); bỏ 3 workload target (android/ios/maccatalyst) khỏi build graph.

- [ ] **Step 1: Sửa `ReTran.App.csproj`**

a) Thay block TFM (dòng 4–6) + xóa comment MacCatalyst (dòng 8–13):

```xml
    <PropertyGroup>
        <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>

        <OutputType>Exe</OutputType>
```

b) Xóa 3 dòng `SupportedOSPlatformVersion` android/ios/maccatalyst (dòng 44–46); **giữ** 2 dòng windows (`SupportedOSPlatformVersion` + `TargetPlatformMinVersion` condition windows) nguyên vẹn.

- [ ] **Step 2: Build solution**

```powershell
dotnet build "ReTran Project.slnx"
```
Expected: Build succeeded, 0 lỗi, 0 NU1201 (Tests TFM khớp App TFM mới).

- [ ] **Step 3: Full suite**

```powershell
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0
```
Expected: **70 passed, 0 failed**.

- [ ] **Step 4: Commit**

```bash
git add ReTran.App/ReTran.App.csproj
git commit -m "chore(app): windows-only target framework"
```

---

### Task 8: Probe paddlepaddle-gpu trên RTX 5060 Ti (venv riêng)

**Files:**
- Create/Modify: `Docs/superpowers/specs/2026-09-26-cleanup-gpu-perf-design.md` (append section `## Appendix: Kết quả probe GPU`)
- Temp: `%TEMP%\retran-gpu-probe\` (venv), `%TEMP%\retran-gpu-probe.py` (script) — **KHÔNG stage**

**Interfaces:**
- Consumes: spec Phase 2 nấc 2a (ladder paddle GPU → ONNX → DirectML); kwargs đồng bộ `PaddleOcrEngine._create` (mobile det + mobile rec + mkldnn off + 3 doc pipeline off); fixture `m3-perf-1080p.png` từ Task 1 Step 3.
- Produces: kết quả trung thực trả lời: (a) wheel `paddlepaddle-gpu` có cài được trên py3.13 không, (b) CUDA có nhận RTX 5060 Ti (Blackwell sm_120) không, (c) `ocr.spot` GPU chạy được + latency bao nhiêu → Plan 2 viết trên số này.

**Ràng buộc cứng:** mọi lệnh cài chạy với `--python %TEMP%\retran-gpu-probe\Scripts\python.exe` (venv probe) — **không bao giờ** `uv pip install`/`uv add` vào `ReTran.OCR/.venv`.

- [ ] **Step 1: Ghi thông tin GPU**

```powershell
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion | Format-List
```
Expected: RTX 5060 Ti + driver version — ghi vào appendix.

- [ ] **Step 2: Tạo venv probe + thử cài (chiến lược 1)**

```powershell
$probe = "$env:TEMP\retran-gpu-probe"
uv venv $probe --python 3.13
uv pip install --python "$probe\Scripts\python.exe" paddleocr "paddlepaddle-gpu>=3.3.1" numpy pillow 2>&1 | Tee-Object -FilePath "$env:TEMP\retran-gpu-probe-install.log"
```

- Thành công → Step 3.
- **LỖI** (không có wheel cp313 / conflict `paddlepaddle` CPU vs `paddlepaddle-gpu` / version không match) → ghi **verbatim error** vào appendix, thử chiến lược 2:

```powershell
uv pip install --python "$probe\Scripts\python.exe" "paddlepaddle-gpu>=3.3.1" 2>&1 | Tee-Object -FilePath "$env:TEMP\retran-gpu-probe-install2.log"
```
- Vẫn lỗi → **bỏ qua Step 3–4**, ghi "paddle GPU không cài được (lý do verbatim)" → appendix nói **chuyển ONNX (Plan 2 nấc 2b)**, nhảy Step 6.

- [ ] **Step 3: Probe CUDA nhận GPU**

Tạo `%TEMP%\retran-gpu-probe.py`:

```python
"""Probe whether paddlepaddle-gpu sees the RTX 5060 Ti (Blackwell). Nghiệm thu paddle GPU có thấy RTX 5060 Ti."""
import sys
import time

import paddle

print("cuda_compiled:", paddle.is_compiled_with_cuda())
print("device:", paddle.device.get_device())
try:
    x = paddle.randn([2048, 2048])
    t0 = time.perf_counter()
    y = paddle.mm(x, x)
    y.numpy()
    print(f"mm_ok_ms: {(time.perf_counter() - t0) * 1000:.1f} place={y.place}")
except Exception as exc:  # noqa: BLE001 - probe must report, never crash
    print(f"FAIL mm: {exc}")
    sys.exit(2)
```

Chạy:

```powershell
& "$env:TEMP\retran-gpu-probe\Scripts\python.exe" "$env:TEMP\retran-gpu-probe.py"
```

- `cuda_compiled: True` + `place=gpu:0` → **paddle GPU OK**, tiếp Step 4.
- `cuda_compiled: False` hoặc `FAIL mm` (sm_120 unsupported / CUDA lib missing) → ghi output verbatim → appendix: **sang ONNX (Plan 2)**, nhảy Step 6.

- [ ] **Step 4: Probe OCR thật trên GPU**

Nếu Step 2 chưa cài paddleocr (chỉ cài paddlepaddle-gpu):

```powershell
uv pip install --python "$env:TEMP\retran-gpu-probe\Scripts\python.exe" paddleocr
```

Mở rộng `%TEMP%\retran-gpu-probe.py` — append:

```python
from paddleocr import PaddleOCR
from PIL import Image
import numpy as np
import os

ocr = PaddleOCR(
    # Keep kwargs in sync with PaddleOcrEngine._create (M3 perf gate):
    lang="en",
    enable_mkldnn=False,
    text_detection_model_name="PP-OCRv5_mobile_det",
    text_recognition_model_name="en_PP-OCRv5_mobile_rec",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
)
img_path = os.path.join(os.environ["TEMP"], "m3-perf-1080p.png")
img = np.array(Image.open(img_path).convert("RGB"))[:, :, ::-1]
t0 = time.perf_counter()
res = ocr.predict(img)
print(f"spot_ms: {(time.perf_counter() - t0) * 1000:.1f}")
print("texts:", [t for page in res for t in page["rec_texts"]])
```

Chạy lại cùng lệnh. Expected ghi vào appendix: `spot_ms` (cold: gồm model init — tách ghi 2 lần chạy: lần 1 cold, lần 2 warm) + texts phát hiện. **So sánh `spot_ms` warm với baseline Task 1** (median one-shot CPU) → nhân số lần cải thiện.

*(PaddleOCR init trên GPU thử tải model — nếu model chưa có cache sẽ download; ghi chú trong appendix.)*

- [ ] **Step 5: Ghi appendix vào spec**

Append cuối `Docs/superpowers/specs/2026-09-26-cleanup-gpu-perf-design.md`:

```markdown
## Appendix: Kết quả probe GPU (điều tra cho Plan 2)

- **Ngày / máy:** <ngày>, GPU <tên + driver> (Step 1).
- **Chiến lược cài (Step 2):** <OK | FAIL verbatim từ log>.
- **CUDA probe (Step 3):** `cuda_compiled=…`, `device=…`, `mm_ok_ms=… place=…` (hoặc FAIL verbatim).
- **OCR probe (Step 4):** cold `spot_ms=…`, warm `spot_ms=…`, texts=[…]. Baseline CPU Task 1 median = … ms → cải thiện ×…
- **Kết luận cho Plan 2:** <paddle GPU khả dụng → Plan 2 chỉ measure+config | paddle GPU fail → ONNX Runtime (nấc 2b) với lý do …>.
```

Mọi số copy từ output thật — không ước lượng; lệnh fail thì ghi fail.

- [ ] **Step 6: Dọn temp + Commit**

```powershell
Remove-Item -Recurse -Force "$env:TEMP\retran-gpu-probe"
Remove-Item -Force "$env:TEMP\retran-gpu-probe.py", "$env:TEMP\retran-gpu-probe-install*.log" -ErrorAction SilentlyContinue
```

```bash
git add Docs/superpowers/specs/2026-09-26-cleanup-gpu-perf-design.md
git commit -m "docs: paddle GPU Blackwell probe results for plan 2"
```

---

## Self-Review (plan author)

**Spec coverage:**
- Phase 0 (T6) → Task 1 (mọi bước M3 T6: preflight, fixtures, one-shot, loop, CPU/RAM, section M3, acceptance) ✅
- Phase 1A jquery/torch/pyproj → Task 2 (jquery), Task 3 (torch), Task 4 Step 5 (pyproj `.pyc`) ✅
- Phase 1B dead code/template → Task 4 (AppSettings, Counter, Weather, NavMenu, usage translate, bootstrap rà) ✅; MainPage debug buttons giữ — spec nói giữ ✅
- Phase 1C trùng lặp → Task 5 (PNG encoder), Task 6 (FindCoreExe + FindRepoRoot) ✅
- Phase 1D TFM → Task 7 ✅
- Phase 2 nấc 2a probe → Task 8 (venv riêng, ladder stop-conditions, appendix) ✅
- Phase 2 2b/2c + Phase 3 → **Plan 2** (khi probe xong — đúng scope đã chia) ✅
- Out-of-scope không đụng: JSON-RPC stack, 1-impl interfaces, DemoFeed, EF — không task nào chạm ✅

**Placeholder scan:** Task 1/8 có bước "copy số từ output thật" — đây là measurement output (không thể viết trước), mọi lệnh đã cụ thể; không có TBD/TODO/không có code block thiếu cho step code.

**Type consistency:** `CoreLocator.FindCoreExe() -> string?` khớp 2 call site (MainPage wrap throw, Appium guard null); `PngWriter.WriteBgra(string, Frame)` ký hiệu giữ nguyên cho 3 call site; `OcrBootstrap.FindRepoRoot()` optional param khớp call không arg.
