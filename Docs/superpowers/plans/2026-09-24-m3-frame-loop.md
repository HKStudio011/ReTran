# M3 Frame Loop + Perf Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Continuous `ocr loop` CLI (capture → 3 fps OCR → JSON-lines of `new_texts`) plus the perf gate (mobile det, 1280px downscale, honest re-measurement) that makes 3 fps meaningful.

**Architecture:** The loop reuses M1 capture sources (event → `LatestFrameBuffer` drop-if-busy) and M2 `OcrPipeline` (session-seen `OcrDiff`, 30 s timeout, sidecar over stdio). A new `OcrLoop` engine owns cadence (`PeriodicTimer` at `OcrFps`), frame encode (new in-memory BGRA→PNG), per-cycle error mapping, and JSON-lines emit. Perf gate lives entirely Python-side in the sidecar (model pins + downscale before detect); serve mode, App, and translation are untouched (translation = M4).

**Tech Stack:** .NET 10 (`ReTran.Core` net10.0-windows; tests xUnit v2 TFM `net10.0-windows10.0.19041.0`), System.Drawing (PNG encode, already used by `PngWriter`), Python 3.13 uv venv (`ReTran.OCR/`, paddleocr 3.4.1 / paddlepaddle 3.3.1 CPU).

## Global Constraints

(All values copied verbatim from the approved spec `Docs/superpowers/specs/2026-09-24-m3-frame-loop-design.md`.)

- Spec governs: `Docs/superpowers/specs/2026-09-24-m3-frame-loop-design.md`.
- CLI shape: `ocr loop (--monitor N | --hwnd 0xHEX) [--fps 1..60] [--max-frames N]` — exactly one source flag required.
- stdout carries ONLY compact JSON lines; every log/warning/error goes to stderr.
- JSON line keys exactly: `seq` (long, from 1, no reset), `at` (ISO-8601 UTC `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`), `new_texts` (array, may be empty), `spots_count` (int), `elapsed_ms` (int). Emit even when `new_texts` is empty; do NOT emit when no new frame arrived that tick.
- Exit codes: **0** = clean stop (Ctrl+C or `--max-frames` reached); **1** = infra/runtime (first-frame timeout 5 s, sidecar died, spawn/repo/python failure); **2** = usage (bad args, both/neither source flags, invalid `--fps`/`--monitor`, monitor index out of range).
- Cadence: tick = `1000/OcrFps` ms; default fps = `CoreConfig.OcrFps` (3), `--fps` overrides; no new frame → skip tick (no OCR, no emit, never re-OCR a consumed frame); drop-if-busy via `LatestFrameBuffer` (no queue).
- Per-cycle errors: `TimeoutException` → stderr warn `warn: cycle skipped (...)`, continue; encode failure → same warn, continue; `SidecarRpcException` → stderr `sidecar lost: ...`, exit 1; cancellation (Ctrl+C) → stop source, dispose sidecar, exit 0.
- First-frame wait timeout = 5 s (`CaptureCommands.FirstFrameTimeoutSeconds`), polling 50 ms.
- Perf gate (Python): `text_detection_model_name="PP-OCRv5_mobile_det"`, `use_doc_orientation_classify=False`, `use_doc_unwarping=False`, `use_textline_orientation=False`, keep `lang`, `enable_mkldnn=False` (PaddlePaddle/Paddle#77340 stays).
- Downscale: long side cap `MAX_INPUT_DIM = 1280`, aspect preserved, Python-side inside the decode path before detection.
- Warm-up script in tests must construct `PaddleOCR` with the SAME kwargs as `PaddleOcrEngine._create` (comment: keep in sync).
- Acceptance: perf numbers measured on an idle machine (bg load sampled and disclosed; if avg > 20% → stop and report, do not measure), numbers appended to `Docs/performance.md`; PASS/BREACH presented to the user at final review — never claim PASS without measured numbers.
- Out of scope: translation (M4), App push/UI, serve-mode loop (sequential `JsonRpcServer` unchanged), game hook, OBS, M2 D/A-items ledger.
- Tests: skip-êm via early return (never fail on environment, no `Assert.Skip`); no `Console.WriteLine` in test bodies (inject `TextWriter`s / use the redirect harness); exactly one `[Fact]`/`[Theory]` count as stated per task.
- Python tests only via `uv run pytest tests -v` from `ReTran.OCR/`. C# tests: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`.
- New publics: bilingual `///` docs (EN sentence first, VI after); Python publics Google-style docstrings.
- Commit messages exactly as each task states; one commit per task; stage only that task's files.
- Test-count baselines: **C# 55, Python 11** → targets: T1 C# 59 (55+4); T2 Py 12 (11+1); T3 Py 15 (12+3); T4 C# 66 (59+7); T5 C# 70 (66+4); T6 unchanged.

---

### Task 1: In-memory BGRA→PNG encoder

**Files:**
- Create: `ReTran.Core/Capture/BgraPngEncoder.cs`
- Test: `ReTran.Tests/BgraPngEncoderTests.cs`

**Interfaces:**
- Consumes: `ReTran.Core.Capture.Frame` (`Width`, `Height`, `Stride => Width * 4`, `byte[] Pixels`, `static Frame.FromBgra(int,int,byte[])`).
- Produces (relied on by Task 4):
  - `public static byte[] BgraPngEncoder.ToPngBytes(Frame frame)` — PNG file bytes, no I/O.
  - `public static string BgraPngEncoder.ToPngBase64(Frame frame)` — base64 of those bytes (transport form for `ocr.spot`).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Drawing;
using System.Text;
using ReTran.Core.Capture;
using Xunit;

namespace RetranCore.Tests;

public class BgraPngEncoderTests
{
    [Fact]
    public void ToPngBytes_HasPngSignature_AndMatchesDimensions()
    {
        Frame frame = Frame.FromBgra(2, 2, new byte[2 * 2 * 4]);
        byte[] png = BgraPngEncoder.ToPngBytes(frame);
        Assert.True(png.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        Assert.Equal(2, bmp.Width);
        Assert.Equal(2, bmp.Height);
    }

    [Fact]
    public void ToPngBytes_RoundtripsPixelColor()
    {
        // BGRA memory layout == Format32bppArgb little-endian (B,G,R,A bytes).
        byte[] pixels = new byte[4];
        pixels[0] = 10; // B
        pixels[1] = 20; // G
        pixels[2] = 30; // R
        pixels[3] = 255; // A
        Frame frame = Frame.FromBgra(1, 1, pixels);

        using var ms = new MemoryStream(BgraPngEncoder.ToPngBytes(frame));
        using var bmp = new Bitmap(ms);
        Color c = bmp.GetPixel(0, 0);
        Assert.Equal(255, c.A);
        Assert.Equal(30, c.R);
        Assert.Equal(20, c.G);
        Assert.Equal(10, c.B);
    }

    [Fact]
    public void ToPngBase64_DecodesToPngBytes()
    {
        Frame frame = Frame.FromBgra(1, 1, new byte[] { 1, 2, 3, 4 });
        byte[] roundtrip = Convert.FromBase64String(BgraPngEncoder.ToPngBase64(frame));
        Assert.Equal(BgraPngEncoder.ToPngBytes(frame), roundtrip);
    }

    [Fact]
    public void ToPngBytes_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BgraPngEncoder.ToPngBytes(null!));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter BgraPngEncoderTests`
Expected: FAIL compile — `The name 'BgraPngEncoder' does not exist`.

- [ ] **Step 3: Write the implementation**

`ReTran.Core/Capture/BgraPngEncoder.cs`:

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ReTran.Core.Capture;

/// <summary>
/// Encodes a <see cref="Frame"/> (BGRA) to in-memory PNG bytes via System.Drawing.
/// Mã hóa một <see cref="Frame"/> (BGRA) thành byte PNG trong bộ nhớ qua System.Drawing.
/// </summary>
public static class BgraPngEncoder
{
    /// <summary>
    /// Encodes <paramref name="frame"/> to PNG bytes without touching the file system.
    /// Mã hóa <paramref name="frame"/> thành byte PNG, không ghi tệp.
    /// </summary>
    /// <param name="frame">Frame to encode (BGRA, Stride = Width * 4). Frame cần mã hóa.</param>
    /// <returns>The PNG file bytes. Byte tệp PNG.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null.</exception>
    public static byte[] ToPngBytes(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GCHandle handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                frame.Width, frame.Height, frame.Stride,
                PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Encodes <paramref name="frame"/> to a base64 PNG string (the ocr.spot transport form).
    /// Mã hóa <paramref name="frame"/> thành chuỗi base64 PNG (dạng gửi ocr.spot).
    /// </summary>
    /// <param name="frame">Frame to encode. Frame cần mã hóa.</param>
    /// <returns>Base64 PNG text. Chuỗi base64 PNG.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null.</exception>
    public static string ToPngBase64(Frame frame) => Convert.ToBase64String(ToPngBytes(frame));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter BgraPngEncoderTests`
Expected: PASS 4/4. Then full suite: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0` → **59 passed, 0 failed** (55 + 4 new; re-baseline if the repo already differs).

- [ ] **Step 5: Commit**

```bash
git add ReTran.Core/Capture/BgraPngEncoder.cs ReTran.Tests/BgraPngEncoderTests.cs
git commit -m "feat(capture): in-memory BGRA to PNG encoder"
```

---

### Task 2: Perf gate — mobile det + drop doc/textline pipelines

**Files:**
- Modify: `ReTran.OCR/src/retran_core/ocr/paddle_engine.py` (`_create`, lines 45–56)
- Create: `ReTran.OCR/tests/test_perf_gate.py`
- Modify: `ReTran.Tests/OcrCliTests.cs` (ONLY the warm-up script string inside `TryWarmUpModel`, lines 73–81 — keep in sync with `_create`)

**Interfaces:**
- Consumes: installed `paddleocr==3.4.1` `PaddleOCR.__init__` params verified from the venv: `text_detection_model_name`, `use_doc_orientation_classify`, `use_doc_unwarping`, `use_textline_orientation`, `lang`, `enable_mkldnn` (via `**kwargs` common args). Model id `PP-OCRv5_mobile_det` verified present (`paddlex/configs/modules/text_detection/PP-OCRv5_mobile_det.yaml`, `paddlex/utils/custom_device_list.py:216`).
- Produces: same public surface (`PaddleOcrEngine(lang: str)` / `spot(...) -> list[Spot]`) — no signature change. Task 6 measures the effect.

- [ ] **Step 1: Write the failing test**

`ReTran.OCR/tests/test_perf_gate.py`:

```python
"""M3 perf-gate unit tests: engine constructor kwargs (no real paddle needed)."""
from __future__ import annotations

import sys
import types

import numpy as np

from retran_core.ocr.paddle_engine import PaddleOcrEngine


class RecordingPaddleOCR:
    """Stands in for paddleocr.PaddleOCR and records constructor kwargs."""

    last_kwargs: dict = {}

    def __init__(self, **kwargs):
        RecordingPaddleOCR.last_kwargs = kwargs

    def predict(self, image):
        return [
            {
                "dt_polys": np.array([[[10, 10], [60, 10], [60, 30], [10, 30]]]),
                "rec_texts": ["HI"],
                "rec_scores": [0.98],
            }
        ]


def test_engine_uses_mobile_det_and_disables_doc_pipelines(monkeypatch):
    fake = types.ModuleType("paddleocr")
    fake.PaddleOCR = RecordingPaddleOCR
    monkeypatch.setitem(sys.modules, "paddleocr", fake)

    engine = PaddleOcrEngine(lang="en")
    spots = engine.spot(np.zeros((40, 80, 3), dtype=np.uint8))

    kwargs = RecordingPaddleOCR.last_kwargs
    assert kwargs["lang"] == "en"
    assert kwargs["enable_mkldnn"] is False
    assert kwargs["text_detection_model_name"] == "PP-OCRv5_mobile_det"
    assert kwargs["use_doc_orientation_classify"] is False
    assert kwargs["use_doc_unwarping"] is False
    assert kwargs["use_textline_orientation"] is False
    # rec stays whatever lang resolves (en_PP-OCRv5_mobile_rec) — never pinned here:
    assert "text_recognition_model_name" not in kwargs
    # parse path still works with the fake predict payload:
    assert [s.text for s in spots] == ["HI"]
    assert spots[0].box == [[10.0, 10.0], [60.0, 10.0], [60.0, 30.0], [10.0, 30.0]]
```

- [ ] **Step 2: Run test to verify it fails**

Run (from `ReTran.OCR/`): `uv run pytest tests/test_perf_gate.py -v`
Expected: FAIL — `assert 'PP-OCRv5_server_det' == 'PP-OCRv5_mobile_det'` (current code passes no det pin, kwargs lack the key → `KeyError: 'text_detection_model_name'`).

- [ ] **Step 3: Write minimal implementation**

`ReTran.OCR/src/retran_core/ocr/paddle_engine.py` — replace the `_create` body (lines 45–56) with:

```python
    def _create(self) -> Any:
        try:
            from paddleocr import PaddleOCR
        except ImportError as exc:
            raise RuntimeError(f"paddleocr not installed: {exc}") from exc
        try:
            # enable_mkldnn=False: paddlepaddle 3.3.x oneDNN/PIR path crashes on
            # PP-OCRv5 (ConvertPirAttribute2RuntimeAttribute, onednn_instruction.cc;
            # see PaddlePaddle/Paddle#77340). Official workaround: disable oneDNN.
            # M3 perf gate: lang="en" would resolve PP-OCRv5_server_det plus the
            # doc-orientation/unwarping/textline pipelines — three extra models and
            # most of the M2 latency (Docs/performance.md M2 baseline). Game/UI
            # text does not need the doc preprocessor; pin mobile det instead.
            return PaddleOCR(
                lang=self.lang,
                enable_mkldnn=False,
                text_detection_model_name="PP-OCRv5_mobile_det",
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
            )
        except Exception as exc:
            raise RuntimeError(f"model init failed: {exc}") from exc
```

- [ ] **Step 4: Sync the warm-up script (keep-in-sync rule)**

In `ReTran.Tests/OcrCliTests.cs`, `TryWarmUpModel` writes this python script (lines 73–81). Replace the `File.WriteAllText` script string with exactly:

```python
        File.WriteAllText(script, """
            import sys
            import numpy as np
            from PIL import Image
            from paddleocr import PaddleOCR
            # Keep kwargs in sync with PaddleOcrEngine._create (M3 perf gate):
            img = np.array(Image.open(sys.argv[1]).convert("RGB"))[:, :, ::-1]
            PaddleOCR(
                lang="en",
                enable_mkldnn=False,
                text_detection_model_name="PP-OCRv5_mobile_det",
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
            ).predict(img)
            print("WARM_OK")
            """)
```

(Only this string changes in the file; no test bodies touched.)

- [ ] **Step 5: Run tests to verify they pass**

Run (from `ReTran.OCR/`): `uv run pytest tests -v`
Expected: **12 passed** (11 + 1 new; includes real `test_paddle_spot_finds_hello` now running mobile det — if it FAILS to detect `HELLO` on the fixture, that is a real finding: report DONE_WITH_CONCERNS with the engine output, do not weaken the pin).

Run C# sanity (warm-up script edit compiles): `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter OcrCliTests`
Expected: 4 passed (integration may take longer first run while mobile models download).

- [ ] **Step 6: Commit**

```bash
git add ReTran.OCR/src/retran_core/ocr/paddle_engine.py ReTran.OCR/tests/test_perf_gate.py ReTran.Tests/OcrCliTests.cs
git commit -m "feat(ocr): perf gate - mobile det, drop doc/textline pipelines"
```

---

### Task 3: Perf gate — downscale input over 1280 px before detection

**Files:**
- Modify: `ReTran.OCR/src/retran_core/ocr/image.py`
- Modify: `ReTran.OCR/tests/test_perf_gate.py` (append 3 tests)

**Interfaces:**
- Consumes: PIL (`pillow>=12.3.0` already a dep).
- Produces: `retran_core.ocr.image.MAX_INPUT_DIM: int = 1280`; `decode_png_b64(image_b64) -> NDArray[np.uint8]` now returns the downscaled image when the long side exceeds 1280 (HWC BGR uint8 — same type, aspect preserved).

- [ ] **Step 1: Write the failing tests**

Append to `ReTran.OCR/tests/test_perf_gate.py`:

```python
import base64
import io

from PIL import Image

from retran_core.ocr.image import MAX_INPUT_DIM, decode_png_b64


def _png_b64(w: int, h: int) -> str:
    """Solid white RGB PNG as base64 ASCII."""
    buf = io.BytesIO()
    Image.new("RGB", (w, h), (255, 255, 255)).save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def test_downscale_caps_long_side_at_max_input_dim():
    assert MAX_INPUT_DIM == 1280
    arr = decode_png_b64(_png_b64(1920, 1080))
    assert arr.shape == (720, 1280, 3)  # 1920x1080 -> 1280x720, aspect kept


def test_downscale_leaves_small_input_untouched():
    arr = decode_png_b64(_png_b64(800, 600))
    assert arr.shape == (600, 800, 3)


def test_downscale_handles_extreme_aspect_ratio():
    arr = decode_png_b64(_png_b64(2000, 500))
    assert arr.shape == (320, 1280, 3)
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `ReTran.OCR/`): `uv run pytest tests/test_perf_gate.py -v`
Expected: FAIL — `ModuleNotFoundError: cannot import name 'MAX_INPUT_DIM' from 'retran_core.ocr.image'`.

- [ ] **Step 3: Write minimal implementation**

`ReTran.OCR/src/retran_core/ocr/image.py` — update docstring, add the constant, insert the thumbnail step:

```python
"""PNG base64 decode to the BGR array paddle expects (with M3 downscale cap)."""
from __future__ import annotations

import base64
import io

import numpy as np
import numpy.typing as npt
from PIL import Image

MAX_INPUT_DIM = 1280


def decode_png_b64(image_b64: str) -> npt.NDArray[np.uint8]:
    """Decode a base64 PNG string to an HWC uint8 BGR array.

    Inputs whose long side exceeds ``MAX_INPUT_DIM`` (1280) are downscaled
    with aspect preserved before conversion — detection latency scales with
    input size (Docs/performance.md M2 baseline).

    Args:
        image_b64: Base64-encoded PNG bytes, no data-url prefix.

    Returns:
        Image pixels in BGR channel order (paddle/OpenCV convention).

    Raises:
        ValueError: The input is not valid base64 or not a decodable PNG.
    """
    try:
        raw = base64.b64decode(image_b64, validate=True)
        rgb = Image.open(io.BytesIO(raw)).convert("RGB")
    except Exception as exc:
        raise ValueError(f"invalid PNG image: {exc}") from exc
    rgb.thumbnail((MAX_INPUT_DIM, MAX_INPUT_DIM), Image.Resampling.BILINEAR)
    return np.asarray(rgb, dtype=np.uint8)[:, :, ::-1].copy()
```

(`thumbnail` is a no-op when nothing exceeds the box — small inputs pass through unchanged.)

- [ ] **Step 4: Run tests to verify they pass**

Run (from `ReTran.OCR/`): `uv run pytest tests -v`
Expected: **15 passed** (12 + 3 new), 0 failed.

- [ ] **Step 5: Commit**

```bash
git add ReTran.OCR/src/retran_core/ocr/image.py ReTran.OCR/tests/test_perf_gate.py
git commit -m "feat(ocr): downscale input over 1280px before detection"
```

---

### Task 4: Loop engine — `OcrLoop` (cadence, emit, error mapping)

**Files:**
- Create: `ReTran.Core/Cli/OcrLoop.cs`
- Create: `ReTran.Tests/LoopTestDoubles.cs` (shared `FakeCaptureSource` + `SyncTextWriter`)
- Create: `ReTran.Tests/OcrLoopTests.cs`
- Test: 7 new facts → C# baseline 59 (post-T1) → **66**

**Interfaces:**
- Consumes: `BgraPngEncoder.ToPngBase64` (T1), `OcrPipeline.SpotAsync(string, CancellationToken) -> Task<JsonObject>` with result keys `spots`/`elapsed_ms`/`new_texts`, `OcrDiff`, `ISidecarRpc`, `SidecarRpcException(int, string)`, `LatestFrameBuffer` (`Push`, `TryTakeLatest(out Frame?)` marks consumed), `ICaptureSource`, `CaptureCommands.FirstFrameTimeoutSeconds` (=5, public const), `CaptureCommands.ParseHwnd(string) -> nint` (public), `CoreConfig.OcrFps`.
- Produces (relied on by Task 5):
  - `public sealed record ReTran.Core.Cli.OcrLoopOptions(bool UseWindow, int Monitor, nint Hwnd, int Fps, int MaxFrames)`
  - `public static string OcrLoop.Usage`
  - `public static OcrLoopOptions? OcrLoop.TryParse(string[] args, int defaultFps, out string? error)`
  - `public static ICaptureSource OcrLoop.CreateSource(OcrLoopOptions options)` (throws `ArgumentOutOfRangeException` for a bad monitor index)
  - `public static Task<int> OcrLoop.RunAsync(OcrLoopOptions options, ICaptureSource source, OcrPipeline pipeline, TextWriter stdout, TextWriter stderr, CancellationToken ct)` → 0 clean / 1 fatal

- [ ] **Step 1: Write the failing tests**

`ReTran.Tests/LoopTestDoubles.cs`:

```csharp
using System.Text;
using ReTran.Core.Capture;

namespace RetranCore.Tests;

/// <summary>
/// Fake capture source: frames queued before Start() flush on Start; later raises fire immediately.
/// Capture source giả: frame xếp hàng trước Start sẽ phát lúc Start; raise sau đó phát ngay.
/// </summary>
internal sealed class FakeCaptureSource : ICaptureSource
{
    private readonly Queue<Frame> _pending = new();

    public CaptureTarget Target { get; } = new ScreenTarget(0);
    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;
    public bool IsRunning { get; private set; }

    public void Start()
    {
        IsRunning = true;
        lock (_pending)
        {
            while (_pending.Count > 0)
                FrameArrived?.Invoke(this, new FrameArrivedEventArgs(_pending.Dequeue()));
        }
    }

    public void Stop() => IsRunning = false;

    public void Dispose()
    {
        lock (_pending)
        {
            while (_pending.Count > 0)
                _pending.Dequeue().Dispose();
        }
    }

    /// <summary>Queues (pre-Start) or raises a frame. Xếp hàng (trước Start) hoặc phát frame.</summary>
    public void Raise(Frame frame)
    {
        lock (_pending)
        {
            if (IsRunning)
                FrameArrived?.Invoke(this, new FrameArrivedEventArgs(frame));
            else
                _pending.Enqueue(frame);
        }
    }
}

/// <summary>
/// Thread-safe line-capturing writer with a wait-for-N-lines handshake.
/// Ghi dòng an toàn luồng kèm chờ đủ N dòng.
/// </summary>
internal sealed class SyncTextWriter : TextWriter
{
    private readonly object _lock = new();
    private readonly List<string> _lines = [];

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            _lines.Add(value ?? "");
            Monitor.PulseAll(_lock);
        }
    }

    public string[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> lines were written (false on timeout).</summary>
    public bool WaitForLineCount(int count, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (_lines.Count < count)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return false;
                Monitor.Wait(_lock, remaining);
            }
            return true;
        }
    }
}
```

`ReTran.Tests/OcrLoopTests.cs`:

```csharp
using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class OcrLoopTests
{
    private sealed class FakeRpc : ISidecarRpc
    {
        private readonly Func<int, CancellationToken, Task<JsonNode?>> _onCall;
        private int _calls;

        public FakeRpc(Func<int, CancellationToken, Task<JsonNode?>>? onCall = null)
            => _onCall = onCall ?? ((_, _) => Task.FromResult<JsonNode?>(SpotResult("HI")));

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _calls);
            return _onCall(call, ct);
        }
    }

    private static JsonObject SpotResult(params string[] texts)
    {
        var spotArray = new JsonArray(texts
            .Select(t => (JsonNode)new JsonObject
            {
                ["box"] = new JsonArray(
                    new JsonArray(0, 0), new JsonArray(1, 0),
                    new JsonArray(1, 1), new JsonArray(0, 1)),
                ["text"] = t,
                ["score"] = 0.9,
            })
            .ToArray());
        return new JsonObject
        {
            ["spots"] = spotArray,
            ["elapsed_ms"] = 5,
            ["new_texts"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()),
        };
    }

    private static Frame TinyFrame() => Frame.FromBgra(2, 2, new byte[16]);

    [Fact]
    public void TryParse_ValidArgs_ReturnsOptions()
    {
        var a = OcrLoop.TryParse(["--monitor", "1", "--fps", "5", "--max-frames", "7"], 3, out string? errA);
        Assert.Null(errA);
        Assert.NotNull(a);
        Assert.False(a.UseWindow);
        Assert.Equal(1, a.Monitor);
        Assert.Equal(5, a.Fps);
        Assert.Equal(7, a.MaxFrames);

        var b = OcrLoop.TryParse(["--hwnd", "0x1F0"], 3, out _);
        Assert.NotNull(b);
        Assert.True(b.UseWindow);
        Assert.Equal((nint)0x1F0, b.Hwnd);

        var c = OcrLoop.TryParse(["--monitor", "0"], 9, out _); // defaultFps used when --fps absent
        Assert.NotNull(c);
        Assert.Equal(9, c.Fps);
    }

    [Fact]
    public void TryParse_InvalidArgs_ReturnsNullWithError()
    {
        string[][] cases =
        [
            [],
            ["--monitor", "0", "--hwnd", "0x10"],   // both sources
            ["--monitor", "abc"],
            ["--monitor", "-1"],
            ["--hwnd"],                              // missing value
            ["--fps", "0"],
            ["--fps", "61"],
            ["--max-frames", "-1"],
            ["--bogus"],
        ];
        foreach (string[] args in cases)
        {
            Assert.Null(OcrLoop.TryParse(args, 3, out string? error));
            Assert.False(string.IsNullOrEmpty(error), $"no error for: [{string.Join(' ', args)}]");
        }
    }

    [Fact]
    public async Task RunAsync_MaxFrames_EmitsThatManyLines_ThenReturns0()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 2);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame()); // delivered on Start; consumed by the first-frame wait
        var pipeline = new OcrPipeline(new FakeRpc(), new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None));

        await Task.Delay(400); // first-frame wait polls every 50 ms — A consumed by now
        source.Raise(TinyFrame());
        Assert.True(await stdout.WaitForLineCount(1, TimeSpan.FromSeconds(10)), "no line 1");
        source.Raise(TinyFrame());
        Assert.True(await stdout.WaitForLineCount(2, TimeSpan.FromSeconds(10)), "no line 2");

        Assert.Equal(0, await run);
        string[] lines = stdout.Snapshot();
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonNode.Parse(lines[0])!["seq"]!.GetValue<long>());
        Assert.Equal(2, JsonNode.Parse(lines[1])!["seq"]!.GetValue<long>());
        JsonObject parsed = JsonNode.Parse(lines[1])!.AsObject();
        Assert.NotNull(parsed["at"]);
        Assert.NotNull(parsed["new_texts"]);
        Assert.Equal(1, parsed["spots_count"]!.GetValue<int>());
        Assert.Equal(5, parsed["elapsed_ms"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_NoNewFrame_CancelsCleanlyWithoutEmit()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 0);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame());
        var pipeline = new OcrPipeline(new FakeRpc(), new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();
        using var cts = new CancellationTokenSource();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, cts.Token));

        await Task.Delay(1500); // first frame consumed, >=1 tick elapsed with an empty buffer
        Assert.Empty(stdout.Snapshot()); // skipped ticks emit nothing
        cts.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task RunAsync_FirstFrameTimeout_Returns1()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 60, MaxFrames: 1);
        using var source = new FakeCaptureSource(); // never raises
        var pipeline = new OcrPipeline(new FakeRpc(), new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        int exit = await OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("timed out", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stdout.Snapshot());
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task RunAsync_TimeoutCycle_WarnsAndContinues()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 1);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame());
        var rpc = new FakeRpc((call, ct) =>
            call == 1
                ? Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => (JsonNode?)null,
                    TaskContinuationOptions.ExecuteSynchronously)
                : Task.FromResult<JsonNode?>(SpotResult("HI")));
        var pipeline = new OcrPipeline(rpc, new OcrDiff()) { Timeout = TimeSpan.FromMilliseconds(300) };
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None));

        await Task.Delay(400);   // first frame (A) consumed
        source.Raise(TinyFrame()); // cycle 1 -> times out at ~1.3 s -> warn + skip
        await Task.Delay(1500);
        source.Raise(TinyFrame()); // cycle 2 -> fast -> emits
        Assert.True(await stdout.WaitForLineCount(1, TimeSpan.FromSeconds(10)), "no line after timeout cycle");

        Assert.Equal(0, await run);
        Assert.Contains("cycle skipped", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(stdout.Snapshot());
    }

    [Fact]
    public async Task RunAsync_SidecarDead_Returns1()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 0);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame());
        var rpc = new FakeRpc((_, _) =>
            throw new SidecarRpcException(-32603, "sidecar exited"));
        var pipeline = new OcrPipeline(rpc, new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None));

        await Task.Delay(400);
        source.Raise(TinyFrame()); // processed on the first tick -> fatal

        Assert.Equal(1, await run);
        Assert.Contains("sidecar lost", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stdout.Snapshot());
        Assert.False(source.IsRunning);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter OcrLoopTests`
Expected: FAIL compile — `The name 'OcrLoop' does not exist` (doubles compile fine).

- [ ] **Step 3: Write the implementation**

`ReTran.Core/Cli/OcrLoop.cs`:

```csharp
using System.Globalization;
using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;

namespace ReTran.Core.Cli;

/// <summary>
/// Options for a continuous <c>ocr loop</c> run.
/// Tùy chọn cho lần chạy <c>ocr loop</c> liên tục.
/// </summary>
/// <param name="UseWindow">True to capture a window (--hwnd), false a monitor. True để bắt cửa sổ, false để bắt màn hình.</param>
/// <param name="Monitor">Monitor index for screen capture. Chỉ số màn hình khi bắt màn hình.</param>
/// <param name="Hwnd">Window handle for window capture. Handle cửa sổ khi bắt cửa sổ.</param>
/// <param name="Fps">OCR tick rate, 1..60. Tần suất tick OCR, 1..60.</param>
/// <param name="MaxFrames">Stop after this many processed frames; 0 = unlimited. Dừng sau số khung đã xử lý; 0 = không giới hạn.</param>
public sealed record OcrLoopOptions(bool UseWindow, int Monitor, nint Hwnd, int Fps, int MaxFrames);

/// <summary>
/// Continuous OCR loop engine: capture buffer -> periodic tick -> pipeline -> JSON lines on stdout.
/// Engine vòng lặp OCR liên tục: buffer capture -> tick định kỳ -> pipeline -> JSON lines ra stdout.
/// </summary>
public static class OcrLoop
{
    /// <summary>
    /// Usage line for the <c>loop</c> subcommand. Dòng hướng dẫn dùng cho subcommand <c>loop</c>.
    /// </summary>
    public static string Usage =>
        "usage: ReTran.Core ocr loop (--monitor N | --hwnd 0xHEX) [--fps 1..60] [--max-frames N]";

    /// <summary>
    /// Parses loop arguments; exactly one source flag is required.
    /// Phân tích đối số loop; phải có đúng một cờ nguồn.
    /// </summary>
    /// <param name="args">Arguments after <c>loop</c>. Đối số sau <c>loop</c>.</param>
    /// <param name="defaultFps">Tick rate used when --fps is absent (CoreConfig.OcrFps). Tần suất dùng khi thiếu --fps.</param>
    /// <param name="error">Null on success; otherwise the reason. Không null khi thành công; ngược lại là lý do.</param>
    /// <returns>Parsed options, or null when invalid. Tùy chọn đã parse, hoặc null nếu không hợp lệ.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="args"/> is null.</exception>
    public static OcrLoopOptions? TryParse(string[] args, int defaultFps, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        bool hasMonitor = false;
        bool hasWindow = false;
        int monitor = 0;
        nint hwnd = 0;
        int fps = defaultFps is < 1 or > 60 ? 3 : defaultFps;
        int maxFrames = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--monitor":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out monitor)
                        || monitor < 0)
                    {
                        error = "invalid --monitor (expected a decimal index >= 0).";
                        return null;
                    }
                    hasMonitor = true;
                    break;
                case "--hwnd":
                    if (i + 1 >= args.Length)
                    {
                        error = "missing value for --hwnd (expected 0xHEX or decimal).";
                        return null;
                    }
                    try
                    {
                        hwnd = CaptureCommands.ParseHwnd(args[++i]);
                    }
                    catch (FormatException ex)
                    {
                        error = ex.Message;
                        return null;
                    }
                    hasWindow = true;
                    break;
                case "--fps":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out fps)
                        || fps is < 1 or > 60)
                    {
                        error = "invalid --fps (expected 1..60).";
                        return null;
                    }
                    break;
                case "--max-frames":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxFrames)
                        || maxFrames < 0)
                    {
                        error = "invalid --max-frames (expected >= 0).";
                        return null;
                    }
                    break;
                default:
                    error = $"unknown argument '{args[i]}'.";
                    return null;
            }
        }

        if (hasMonitor == hasWindow) // both, or neither
        {
            error = "exactly one source required: --monitor N or --hwnd 0xHEX.";
            return null;
        }
        return new OcrLoopOptions(hasWindow, monitor, hwnd, fps, maxFrames);
    }

    /// <summary>
    /// Creates the capture source described by <paramref name="options"/>.
    /// Tạo capture source theo <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Parsed loop options. Tùy chọn loop đã parse.</param>
    /// <returns>The source (not started). Source (chưa Start).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Monitor index out of range. Chỉ số màn hình ngoài khoảng.</exception>
    public static ICaptureSource CreateSource(OcrLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.UseWindow
            ? new WindowCaptureSource(new WindowTarget(options.Hwnd), logger: null)
            : new ScreenCaptureSource(new ScreenTarget(options.Monitor), logger: null);
    }

    /// <summary>
    /// Runs the loop until max-frames is reached, <paramref name="ct"/> is cancelled, or a fatal error occurs.
    /// Chạy vòng lặp đến khi đủ max-frames, <paramref name="ct"/> bị hủy, hoặc lỗi nghiêm trọng.
    /// </summary>
    /// <param name="options">Loop options (fps, max-frames). Tùy chọn loop.</param>
    /// <param name="source">Capture source; started and stopped by this method. Capture source; method này Start/Stop.</param>
    /// <param name="pipeline">OCR pipeline shared for the whole run (session-seen diff). Pipeline OCR dùng chung cả lượt.</param>
    /// <param name="stdout">JSON-lines sink (stdout in production). Nơi ghi JSON lines (stdout khi chạy thật).</param>
    /// <param name="stderr">Warnings/errors sink. Nơi ghi cảnh báo/lỗi.</param>
    /// <param name="ct">Cancellation (Ctrl+C). Token hủy (Ctrl+C).</param>
    /// <returns>0 = clean stop; 1 = first-frame timeout or sidecar lost. 0 = dừng sạch; 1 = hết chờ frame đầu hoặc mất sidecar.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null.</exception>
    public static async Task<int> RunAsync(
        OcrLoopOptions options,
        ICaptureSource source,
        OcrPipeline pipeline,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        using var buffer = new LatestFrameBuffer();
        EventHandler<FrameArrivedEventArgs> onFrame = (_, e) => buffer.Push(e.Frame);
        source.FrameArrived += onFrame;
        try
        {
            source.Start();

            // First frame: same 5 s contract as the capture CLI.
            bool gotFirst = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(CaptureCommands.FirstFrameTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                if (buffer.TryTakeLatest(out Frame? first))
                {
                    first?.Dispose();
                    gotFirst = true;
                    break;
                }
                Thread.Sleep(50);
            }
            if (!gotFirst)
            {
                stderr.WriteLine(
                    $"timed out after {CaptureCommands.FirstFrameTimeoutSeconds}s waiting for first frame.");
                return 1;
            }

            long seq = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / options.Fps));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (options.MaxFrames > 0 && seq >= options.MaxFrames)
                    break;
                if (!buffer.TryTakeLatest(out Frame? frame) || frame is null)
                    continue; // no new frame -> skip the tick: no OCR, no emit, never re-OCR

                try
                {
                    using (frame)
                    {
                        string b64 = BgraPngEncoder.ToPngBase64(frame);
                        JsonObject result = await pipeline.SpotAsync(b64, ct);
                        seq++;
                        var line = new JsonObject
                        {
                            ["seq"] = seq,
                            ["at"] = DateTime.UtcNow.ToString(
                                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                            ["new_texts"] = result["new_texts"]?.DeepClone() ?? new JsonArray(),
                            ["spots_count"] = result["spots"] is JsonArray spots ? spots.Count : 0,
                            ["elapsed_ms"] = result["elapsed_ms"]?.DeepClone() ?? 0,
                        };
                        stdout.WriteLine(line.ToJsonString());
                        stdout.Flush();
                        if (options.MaxFrames > 0 && seq >= options.MaxFrames)
                            break;
                    }
                }
                catch (SidecarRpcException)
                {
                    throw; // fatal: sidecar lost
                }
                catch (OperationCanceledException)
                {
                    throw; // Ctrl+C mid-call
                }
                catch (Exception ex)
                {
                    // Per-cycle timeout or encode failure: warn and continue.
                    stderr.WriteLine($"warn: cycle skipped ({ex.Message})");
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0; // Ctrl+C -> clean stop
        }
        catch (SidecarRpcException ex)
        {
            stderr.WriteLine($"sidecar lost: {ex.Message}");
            return 1;
        }
        finally
        {
            source.FrameArrived -= onFrame;
            try
            {
                source.Stop();
            }
            catch (InvalidOperationException)
            {
                // never started / already stopped
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter OcrLoopTests`
Expected: PASS 7/7 (suite of this task ~8–10 s: the first-frame-timeout fact waits the full 5 s).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`
Expected: **66 passed, 0 failed** (59 + 7).

- [ ] **Step 6: Commit**

```bash
git add ReTran.Core/Cli/OcrLoop.cs ReTran.Tests/LoopTestDoubles.cs ReTran.Tests/OcrLoopTests.cs
git commit -m "feat(ocr): frame loop engine with JSON-lines output"
```

---

### Task 5: `ocr loop` CLI wiring — sources, Ctrl+C, integration test

**Files:**
- Modify: `ReTran.Core/Cli/OcrCommands.cs` (loop branch + `RunLoopAsync`)
- Modify: `ReTran.Core/Program.cs` (comment only: `// translate handler lands in M3.` → `// translate handler lands in M4.`)
- Create: `ReTran.Tests/OcrTestSupport.cs` (extracted shared helpers)
- Modify: `ReTran.Tests/OcrCliTests.cs` (delete the 3 private helpers; call `OcrTestSupport.*`)
- Create: `ReTran.Tests/OcrLoopCliTests.cs` (3 usage facts + 1 integration fact)
- Test: 4 new facts → C# 66 → **70**

**Interfaces:**
- Consumes: everything Task 4 produces; `OcrTestSupport` extraction sources = `OcrCliTests` private helpers (verbatim move; `TryWarmUpModel` already carries the T2 gate kwargs); `SidecarProcess.StartAsync(string, string, CancellationToken)`, `OcrBootstrap.FindRepoRoot/ResolvePythonExe`, `CoreConfig.LoadFrom(CoreConfig.DefaultPath)`.
- Produces: CLI behavior `ReTran.Core ocr loop ...` (exit 0/1/2 per Global Constraints); shared test helpers `OcrTestSupport.RunDispatcherAsync / CreateTextFixturePng / TryWarmUpModel / FrameFromPng`.

- [ ] **Step 1: Write the failing tests**

`ReTran.Tests/OcrTestSupport.cs`:

```csharp
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Config;
using ReTran.Core.Ocr;

namespace RetranCore.Tests;

/// <summary>
/// Shared OCR test helpers extracted from OcrCliTests (fixture, warm-up, redirect harness).
/// Helper OCR dùng chung trích từ OcrCliTests (fixture, warm-up, harness redirect).
/// </summary>
internal static class OcrTestSupport
{
    /// <summary>Runs the dispatcher with Console redirected; snapshots stdout/stderr AFTER the await.</summary>
    public static async Task<(string Stdout, string Stderr, int Exit)> RunDispatcherAsync(
        CommandDispatcher d, string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        using var so = new StringWriter();
        using var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            int exit = await d.DispatchAsync(args);
            return (so.ToString(), se.ToString(), exit);
        }
        finally { Console.SetOut(origOut); Console.SetError(origErr); }
    }

    /// <summary>Draws HELLO RETRAN on white; returns the temp PNG path (caller deletes).</summary>
    public static string CreateTextFixturePng()
    {
        string path = Path.Combine(Path.GetTempPath(), $"retran-ocr-fixture-{Guid.NewGuid():N}.png");
        using var bmp = new Bitmap(640, 96);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var font = new Font("Arial", 48f, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("HELLO RETRAN", font, Brushes.Black, 10f, 20f);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Runs one python snippet that loads PaddleOCR and OCRs the fixture (warms the model cache).</summary>
    public static async Task<bool> TryWarmUpModel(string python, string repoRoot, string fixturePng)
    {
        string script = Path.Combine(Path.GetTempPath(), $"retran-warmup-{Guid.NewGuid():N}.py");
        File.WriteAllText(script, """
            import sys
            import numpy as np
            from PIL import Image
            from paddleocr import PaddleOCR
            # Keep kwargs in sync with PaddleOcrEngine._create (M3 perf gate):
            img = np.array(Image.open(sys.argv[1]).convert("RGB"))[:, :, ::-1]
            PaddleOCR(
                lang="en",
                enable_mkldnn=False,
                text_detection_model_name="PP-OCRv5_mobile_det",
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
            ).predict(img)
            print("WARM_OK")
            """);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(fixturePng);
            using var p = Process.Start(psi)!;
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            bool exited = p.WaitForExit(300_000);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            _ = await errTask;
            string stdout = await outTask;
            return p.ExitCode == 0 && stdout.Contains("WARM_OK");
        }
        catch { return false; }
        finally { if (File.Exists(script)) File.Delete(script); }
    }

    /// <summary>Loads a PNG file as an owned BGRA Frame (handles stride padding).</summary>
    public static Frame FrameFromPng(string pngPath)
    {
        using var bmp = new Bitmap(pngPath);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride),
                    pixels, y * bmp.Width * 4, bmp.Width * 4);
            return Frame.FromBgra(bmp.Width, bmp.Height, pixels);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
```

`ReTran.Tests/OcrLoopCliTests.cs`:

```csharp
using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Config;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class OcrLoopCliTests
{
    [Fact]
    public async Task Ocr_Loop_BareArgs_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (stdout, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["ocr", "loop"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Ocr_Loop_BothSources_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["ocr", "loop", "--monitor", "0", "--hwnd", "0x1234"]);
        Assert.Equal(2, exit);
        Assert.Contains("exactly one source", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ocr_Loop_InvalidArgs_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        string[][] cases =
        [
            ["ocr", "loop", "--monitor", "abc"],   // bad index
            ["ocr", "loop", "--fps", "0"],         // fps out of range
            ["ocr", "loop", "--monitor", "99"],    // monitor out of range (CreateSource, still pre-sidecar)
            ["ocr", "loop", "--bogus"],
        ];
        foreach (string[] args in cases)
        {
            var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, args);
            Assert.True(exit == 2, $"expected exit 2 for [{string.Join(' ', args)}], got {exit}: {stderr}");
        }
    }

    [Fact]
    public async Task OcrLoop_RealSidecar_FakeSource_EmitsTwoJsonLines()
    {
        // Infra gate: repo + venv + paddle warm-up — otherwise skip (never fail on environment).
        if (!OperatingSystem.IsWindows()) return;
        string? repoRoot = OcrBootstrap.FindRepoRoot();
        if (repoRoot is null) return;
        string python = OcrBootstrap.ResolvePythonExe(new CoreConfig(), repoRoot) ?? "";
        if (python.Length == 0 || !File.Exists(python)) return;

        string fixture = OcrTestSupport.CreateTextFixturePng();
        try
        {
            if (!await OcrTestSupport.TryWarmUpModel(python, repoRoot, fixture)) return;

            await using var sp = await SidecarProcess.StartAsync(python, repoRoot, CancellationToken.None);
            var pipeline = new OcrPipeline(sp, new OcrDiff());
            var options = new OcrLoopOptions(false, 0, 0, Fps: 5, MaxFrames: 2);
            using var source = new FakeCaptureSource();
            var stdout = new SyncTextWriter();
            var stderr = new StringWriter();

            Task<int> run = Task.Run(() =>
                OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None));

            using var feederCts = new CancellationTokenSource();
            Task feeder = Task.Run(async () =>
            {
                while (!feederCts.IsCancellationRequested && !run.IsCompleted)
                {
                    source.Raise(OcrTestSupport.FrameFromPng(fixture));
                    try { await Task.Delay(100, feederCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });

            Assert.True(await stdout.WaitForLineCount(2, TimeSpan.FromSeconds(60)), "no 2 lines");
            feederCts.Cancel();
            int exit = await run;
            Assert.True(exit == 0, $"loop failed (exit {exit}): {stderr}");

            string[] lines = stdout.Snapshot();
            Assert.Equal(2, lines.Length);
            JsonObject first = JsonNode.Parse(lines[0])!.AsObject();
            JsonObject second = JsonNode.Parse(lines[1])!.AsObject();
            Assert.Equal(1, first["seq"]!.GetValue<long>());
            Assert.Equal(2, second["seq"]!.GetValue<long>());
            Assert.NotNull(first["new_texts"] as JsonArray);
            Assert.NotNull(second["at"]!.GetValue<string>());
        }
        finally { File.Delete(fixture); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter OcrLoopCliTests`
Expected: FAIL — `Ocr_Loop_BareArgs_UsageExit2` gets exit **2 only by luck of the one-shot path**: today `ocr loop` hits `args.Length != 1` → prints the one-shot usage → exit 2, and `Ocr_Loop_BothSources...` FAILS (one-shot path: file-not-found → exit 1, no "exactly one source"). Compile also fails on `OcrTestSupport` missing until Step 3 creates it — write Step 3 first if the compiler blocks running; then re-run to confirm the behavioral failures above.

- [ ] **Step 3: Create `OcrTestSupport` and slim `OcrCliTests`**

Create `ReTran.Tests/OcrTestSupport.cs` with the Step-1 content.

Then edit `ReTran.Tests/OcrCliTests.cs` (bodies of the 4 tests stay semantically identical):
1. Delete the private methods `TryWarmUpModel` (lines 69–110), `CreateTextFixturePng` (112–123), `RunDispatcherAsync` (125–142).
2. Replace call sites:
   - line 17: `await RunDispatcherAsync(d, ["ocr"])` → `await OcrTestSupport.RunDispatcherAsync(d, ["ocr"])`
   - line 27: `await RunDispatcherAsync(d,` → `await OcrTestSupport.RunDispatcherAsync(d,`
   - line 51: `CreateTextFixturePng()` → `OcrTestSupport.CreateTextFixturePng()`
   - line 54: `await TryWarmUpModel(...)` → `await OcrTestSupport.TryWarmUpModel(...)`
   - line 58: `await RunDispatcherAsync(d, ["ocr", fixture])` → `await OcrTestSupport.RunDispatcherAsync(d, ["ocr", fixture])`
3. Remove now-unused `using System.Diagnostics;` from `OcrCliTests.cs` if the compiler flags it (keep if still referenced).

- [ ] **Step 4: Write the implementation — loop branch in `OcrCommands`**

In `ReTran.Core/Cli/OcrCommands.cs`, insert at the very top of `RunAsync` (before the `args.Length != 1` check):

```csharp
        if (args.Length >= 1 && string.Equals(args[0], "loop", StringComparison.OrdinalIgnoreCase))
        {
            return await RunLoopAsync(args[1..]);
        }
```

Append the new private method (after `RunAsync`), and update the class doc line to mention the loop:

```csharp
    private static async Task<int> RunLoopAsync(string[] args)
    {
        CoreConfig config = CoreConfig.LoadFrom(CoreConfig.DefaultPath);
        OcrLoopOptions? options = OcrLoop.TryParse(args, config.OcrFps, out string? parseError);
        if (options is null)
        {
            Console.Error.WriteLine(OcrLoop.Usage);
            if (parseError is not null)
                Console.Error.WriteLine(parseError);
            return 2;
        }

        // Validate the source BEFORE spawning the sidecar (fast exit 2 for bad --monitor).
        ICaptureSource source;
        try
        {
            source = OcrLoop.CreateSource(options);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.Error.WriteLine($"invalid source: {ex.Message}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // we shut down in order; do not kill the process
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            string? repoRoot = OcrBootstrap.FindRepoRoot();
            if (repoRoot is null)
            {
                Console.Error.WriteLine("repo root not found (ReTran.OCR/pyproject.toml above cwd).");
                return 1;
            }
            string? python = OcrBootstrap.ResolvePythonExe(config, repoRoot);
            if (python is null)
            {
                Console.Error.WriteLine("python not found (set PythonExe in config or run `uv sync` in ReTran.OCR/).");
                return 1;
            }
            await using var sp = await SidecarProcess.StartAsync(python, repoRoot, cts.Token);
            var pipeline = new OcrPipeline(sp, new OcrDiff());
            return await OcrLoop.RunAsync(options, source, pipeline, Console.Out, Console.Error, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ocr loop failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            source.Dispose();
        }
    }
```

Add usings if missing: `ReTran.Core.Capture` (for `ICaptureSource`). `ReTran.Core.Ocr`/`OcrClient`/`Config` already present.

In `ReTran.Core/Program.cs` line ~30 replace:

```csharp
    // translate handler lands in M3.
```
with
```csharp
    // translate handler lands in M4.
```

Also update the one-shot usage line in `OcrCommands.RunAsync` error message to hint the loop (optional but recommended):

```csharp
            Console.Error.WriteLine("usage: ReTran.Core ocr <input.png>   (or: ocr loop --help-style usage via 'ocr loop' with bad args)");
```
Do NOT change this line if it would break `Ocr_WithoutArgs_ReturnsUsageExit2` (that test only asserts `Contains("usage")` — a plain keep-current-text is fine). Prefer leaving the original one-shot usage text untouched.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0 --filter OcrLoopCliTests`
Expected: 4 passed (integration runs real sidecar with mobile det; first run may download models).

Run: `dotnet test "ReTran.Tests/ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`
Expected: **70 passed, 0 failed** (66 + 4; the refactor must not change `OcrCliTests` outcomes — if its 4 facts regress, fix the extraction, do not touch assertions).

- [ ] **Step 6: Manual smoke (paste output into the report)**

```powershell
& "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe" ocr loop --monitor 0 --fps 3 --max-frames 3
# expect: 3 JSON lines on stdout; Ctrl+C test: run without --max-frames, press Ctrl+C -> exits 0 promptly
& "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe" ocr loop
# expect: usage + "exactly one source required", exit 2 ($LASTEXITCODE)
```
If `--monitor 0` fails on this machine (HDR/duplicate — seen in M2 T5), retry with a real window: start Notepad, then `ocr loop --hwnd 0x<MainWindowHandle> --max-frames 3`, and disclose which path was used.

- [ ] **Step 7: Commit**

```bash
git add ReTran.Core/Cli/OcrCommands.cs ReTran.Core/Program.cs ReTran.Tests/OcrTestSupport.cs ReTran.Tests/OcrCliTests.cs ReTran.Tests/OcrLoopCliTests.cs
git commit -m "feat(ocr): ocr loop CLI with sources, Ctrl+C shutdown, integration test"
```

---

### Task 6: Perf-gate measurement + `Docs/performance.md` M3 section

**Files:**
- Modify: `Docs/performance.md` (insert a new section immediately before the line `## M1 capture baseline`)
- Test: none (measurement task) — counts unchanged (C# 70, Python 15)

**Interfaces:**
- Consumes: the shipped loop (`ReTran.Core ocr loop`), the one-shot CLI, the T2/T3 gate (mobile det + 1280 cap), M2's `Docs/performance.md` method/disclosure style.
- Produces: measured M3 numbers (cold/warm one-shot both fixtures, loop throughput via `elapsed_ms` lines, CPU/RAM) + an explicit acceptance table for the user's tradeoff decision at final review.

- [ ] **Step 1: Idle-machine preflight (hard gate)**

```powershell
$samples = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 3).CounterSamples | ForEach-Object { $_.CookedValue }
$avg = ($samples | Measure-Object -Average).Average
"bg CPU avg: $avg %"
```
If `$avg -gt 20` → **stop**: report `NEEDS_CONTEXT` "machine not idle (avg XX%) — ask the user to close ComfyUI/Hermes/WSL, then re-dispatch" (spec §4.3: idle is mandatory for acceptance). If ≤ 20 → proceed and record the value + time for the doc's "Bối cảnh đo" subsection.

- [ ] **Step 2: Build fixtures**

```powershell
uv run python -c @"
from PIL import Image, ImageDraw, ImageFont
im = Image.new('RGB', (1920, 1080), 'white')
d = ImageDraw.Draw(im)
f = ImageFont.truetype('arial.ttf', 64)
d.text((40, 40), 'HELLO RETRAN M3 PERF TEST', fill='black', font=f)
d.text((40, 160), 'The quick brown fox jumps over the lazy dog.', fill='black', font=f)
im.save('perf-1080p.png')
im2 = Image.new('RGB', (640, 96), 'white')
d2 = ImageDraw.Draw(im2)
d2.text((10, 20), 'HELLO RETRAN - M2 OCR PERF TEST', fill='black', font=ImageFont.truetype('arial.ttf', 48))
im2.save('perf-640x96.png')
"@   # run from ReTran.OCR/ (uv context); move outputs to repo root or note their paths
```

- [ ] **Step 3: One-shot measurements (cold + warm N=5)**

```powershell
$exe = "ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe"
# cold (fresh sidecar + model load each run): 1 run per fixture, wall-clock
Measure-Command { & $exe ocr perf-1080p.png | Out-Null }.TotalSeconds
Measure-Command { & $exe ocr perf-640x96.png | Out-Null }.TotalSeconds
# warm: 5 runs per fixture; capture elapsed_ms from the JSON each time
1..5 | ForEach-Object { (& $exe ocr perf-1080p.png | ConvertFrom-Json).elapsed_ms }
1..5 | ForEach-Object { (& $exe ocr perf-640x96.png | ConvertFrom-Json).elapsed_ms }
```
Record min/median/mean of each series (state N).

- [ ] **Step 4: Loop throughput**

```powershell
# preferred: screen
$out = & $exe ocr loop --monitor 0 --fps 3 --max-frames 10
# fallback (HDR screen issue): window of a Notepad showing fixture text
#   notepad perf-text.txt; then --hwnd 0x<MainWindowHandle>
$out | ForEach-Object { ($_ | ConvertFrom-Json).elapsed_ms }
```
Derive achieved fps (10 frames / wall time) and `elapsed_ms` median. Disclose which source path worked. If BOTH sources fail on this machine, record the failure verbatim and fall back to one-shot numbers for the acceptance table with an explicit "loop not measurable on this machine (reason)" note.

- [ ] **Step 5: CPU / RAM during the loop**

```powershell
$out = & $exe ocr loop --monitor 0 --fps 3 --max-frames 10   # keep stdout captured
uv run --with psutil python -c @"
import psutil, time, json, subprocess, sys
# find this run's processes by name after starting the loop separately if needed:
procs = [p for p in psutil.process_iter(['name','cpu_percent','memory_info'])
         if p.info['name'] and ('ReTran.Core' in p.info['name'] or 'python' in p.info['name'].lower())]
for p in procs: p.cpu_percent(None)
time.sleep(8)
rows = [{'name': p.info['name'],
         'cpu_pct': p.cpu_percent(None),
         'rss_mb': p.memory_info().rss / 2**20,
         'peak_mb': (p.memory_info().peak_wset or 0) / 2**20}
        for p in procs if p.is_running()]
print(json.dumps(rows, indent=2))
"@
```
Refine as needed (start the loop first, sample while it runs, ≥8 s window); state the method in the doc exactly as done — including that `peak_mb` is per-process `peak_wset` summed over the tree (M2 caveat applies).

- [ ] **Step 6: Write the M3 section**

Insert immediately before `## M1 capture baseline` in `Docs/performance.md`, mirroring the M2 subsection structure:

```markdown
## M3 frame loop + perf gate (2026-09-24)

### Máy đo
(Same machine table style as M2; note: T2/T3 applied — mobile det, 1280 cap.)

### Bối cảnh đo (background load — quan trọng khi đọc số)
(avg from Step 1 + time; if >20% this section must say so and the verdict is provisional.)

### Số liệu — one-shot (cold + warm N=5, hai fixture)
| Fixture | Cold (s) | Warm min | Warm median | Warm mean | Ghi chú |
(min/median/mean from Step 3; compare vs M2 baseline rows.)

### Số liệu — loop throughput
(source used, fps achieved, elapsed_ms median over 10 frames; disclosure if fallback used.)

### CPU / RAM (trong lúc loop)
(rows from Step 5 + method note; tree-peak = sum caveat.)

### Đối chiếu budget (AGENTS.md)
| Row | M2 baseline | M3 measured | Budget | Verdict |
(OCR ≤333 ms/call → verdict per measured median; idle RAM; total CPU; VRAM 0 CPU paddle; rows still N/A marked N/A.)

### Ghi chú / hạn chế đo
(disclosures: what failed, fallbacks used, confounders.)

### Lệnh reproduce
(exact commands from Steps 1–5.)
```

Numbers must be copied from the actual outputs of Steps 1–5 — no estimation (Global Constraint). If any measurement failed, the row says so instead of being omitted.

- [ ] **Step 7: Self-review + commit**

Check: every number in the new section traces to a Step 1–5 output you ran; verdicts explicit (PASS/BREACH/N/A per budget row, or "provisional" if load was borderline); no product code touched; fixture PNGs and temp scripts NOT staged.

```bash
git add Docs/performance.md
git commit -m "docs(perf): M3 frame-loop + perf-gate measured numbers"
```

---

## Self-Review (plan author)

**Spec coverage:** §2 decisions → T4 (architecture A) + T5 (CLI/sources) + T6 (acceptance); §3 data flow → T1+T4; §4 perf gate → T2, T3, T6 (all 5 sub-items); §5 output contract → T4 emit + exit codes T5; §6 error/shutdown → T4 (timeout/sidecar/first-frame) + T5 (Ctrl+C, source validation); §7 testing → each task's test steps (unit + integration + perf); §8 out-of-scope → Global Constraints; §9 risks → T6 preflight/fallbacks.

**Type consistency checked:** `OcrLoopOptions(bool UseWindow, int Monitor, nint Hwnd, int Fps, int MaxFrames)` — identical in T4 definition, T4 tests (positional `false, 0, 0` + named `Fps:`/`MaxFrames:`), T5 tests, T5 `RunLoopAsync`. `RunAsync(options, source, pipeline, stdout, stderr, ct) -> Task<int>` consistent everywhere. `OcrTestSupport.TryWarmUpModel(string, string, string) -> Task<bool>` matches both call sites. `BgraPngEncoder.ToPngBase64(Frame)` used only in T4.

**Known adjustment vs spec §7 (disclosed):** the loop integration test uses `FakeCaptureSource` + real sidecar (deterministic) instead of driving DXGI directly — screen/window capture is exercised by the Task 5 manual smoke (spec's integration requirement "sidecar thật → 2 JSON lines" is still met; capture sources get their own env-dependent smoke, consistent with the M2 HDR finding).
