# Pipeline hot-path performance fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Giảm chi phí hot path của Core (annotate frame + transport) để tiến gần budget AGENTS.md: Core scripted 0,81 → ≤ 0,55 core, IPC stdio 2,9 → ~0 MB/s, annotate ~63 → ≤ 27 ms/khung, bớt churn bộ nhớ — **không đụng** vào capture/OCR/translate hay giao kèo camera ảo.

**Architecture:** Ba nhóm thay đổi, theo thứ tự đo được:
1. **Annotate (GDI+)**: bỏ copy BGRA 8 MB/khung (Bitmap trỏ thẳng vào `frame.Pixels` pinned), đổi text từ 9 lượt `DrawString`/box sang **pill** (nền `Color.FromArgb(180,0,0,0)` + 1 `DrawString`/box — user duyệt 2026-10-19, ảnh mẫu `%TEMP%\opencode\annotate_style_pill.png`), và trả kèm **kích thước khung annotated** (fix luôn bug `frame.annotated` báo 1920×1080).
2. **Transport**: JPEG của khung annotated đi qua **shared memory** `Local\ReTran.Preview.V1` (Core ghi / App đọc, seqlock như camera); `frame.annotated` chỉ còn `{seq,w,h}` ⇒ IPC stdio về ~0. Bỏ luôn clone 8 MB/khung capture bằng buffer pool.
3. **OCR transport**: gửi **JPEG q85 thay PNG** cho sidecar (PNG 28 ms/233 KB → JPEG 10 ms/201 KB, accuracy-neutral vì sidecar vẫn tự cap 1280), + nối `captureFps` vào capture source (gap đã ghi ở `ParseStart`).

**Tech Stack:** .NET 10 (C#: System.Drawing.Common, `MemoryMappedFile`, xUnit v3), Python 3.13 sidecar (PIL), PowerShell cho probe đo.

**Nguồn số (baseline 2026-10-19):** `Docs/performance.md` §"M5 — Camera ảo + Preview".

## Baseline — số đo trước fix (2026-10-19)

| Hạng mục | Số đo | Nguồn |
| --- | --- | --- |
| Core scripted (annotate 10 fps, màn hình game, 76,9 box/khung) | **0,81 core**, IPC 2,93 MB/s, dòng 337,6 KB | probe W |
| Core app-driven (app + game + OBS preview) | **1,85 core** (218 ms/khung), RAM 923 MB avg / 1803 max | phiên app-driven |
| Annotate/khung | **~63 ms** (A−B) · 69 ms (suy từ W) | A/B, W |
| — vẽ box + text (77 box, `offsets9`) | **33 ms** (150 box: 49 ms) | `annotate_styles.ps1` |
| — downscale 1920→1280 | 8,5 ms | `annotate_bench.ps1` |
| — JPEG q70 encode | 5 ms | `annotate_bench.ps1` |
| — copy BGRA 8 MB (`Marshal.Copy` từng dòng) + JSON/base64/pipe/GC | ~18–28 ms (phần dư) | suy từ chênh |
| PNG encode 1920×1080 cho OCR (1 chu kỳ/s) | **28 ms**, 233 KB (JPEG q85: 10 ms, 201 KB) | `png_vs_jpeg.ps1` |
| Clone frame cho annotate | 10 lần/s × 8 MB = **80 MB/s garbage** | `PipelineHost.cs:156` |
| Sidecar (Paddle GPU) | 1,25 core / ~2 GB RAM / 362 MB VRAM | app-driven — **ngoài scope plan này** |

## Target sau fix (kỳ vọng, sẽ đo lại ở Task 6)

| Hạng mục | Trước | Sau (kỳ vọng) | Cách đạt |
| --- | --- | --- | --- |
| Annotate/khung | ~63 ms | **~22–27 ms** | pill (−24) + bỏ copy (−5…10) + bỏ base64/JSON (−3…5) |
| Core scripted | 0,81 core | **~0,45–0,55 core** | annotate + churn |
| IPC stdio | 2,93 MB/s | **~0** | preview qua MMF |
| Core RAM max (probe) | 530–788 MB | thấp hơn rõ rệt | pool + MMF |
| CPU tổng app+core+ocr | 22,4 % | ~14–16 % | vẫn > budget 8 % vì sidecar 1,25 core — **quyết định riêng** |
| Annotate budget 10 ms | FAIL 6× | còn FAIL ~2,5× | GDI+ JPEG (5 ms) + downscale (8,5 ms) là sàn — cần user duyệt revise budget |

## Global Constraints

- **Kiểu hiển thị mới (user duyệt 2026-10-19):** pill — `FillRectangle(Color.FromArgb(180,0,0,0))` cỡ `MeasureString + 4×2 px`, `DrawString` trắng 1 lượt/box; giữ viền box `#22C55E` 2 px và vị trí text dưới box (`y + h + 2`); font giữ nguyên `Segoe UI`, size `max(12, 0.02×outW)`, `GraphicsUnit.Pixel`.
- **Đổi shape notification `frame.annotated`:** từ `{seq,w,h,jpeg}` → `{seq,w,h}` (w/h là kích thước khung **annotated** ≤1280, không phải khung nguồn). Core + App phải nằm cùng commit — không hỗ trợ lẫn phiên bản.
- **MMF mới:** `Local\ReTran.Preview.V1` (chỉ Core↔App; camera `Local\ReTran.Camera.V1` + heartbeat + filter C++ **không đổi**; không sửa gì trong `ReTran.Camera/`).
- `frame.translated` payload, overlay local, capture/OCR/translate/diff, camera lifecycle (HOLD/đen) giữ nguyên.
- Sidecar: chỉ đổi **decode ảnh** (nhận JPEG lẫn PNG) — không đổi model, tham số, `MAX_INPUT_DIM = 1280`.
- Doc comment C# **song ngữ Anh/Việt**; comment/tên code tiếng Anh; plan/báo cáo tiếng Việt.
- Test xanh trước mỗi commit: `dotnet build "ReTran Project.slnx"` + `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`; task Python thêm `cd ReTran.OCR; uv run pytest tests -v`.
- **Không push** (user chưa yêu cầu). Mỗi task 1 commit đúng file của task.

## Review Focus (rủi ro cần soi khi review)

1. **Pin lifetime**: `Bitmap` trỏ vào `frame.Pixels` — `GCHandle` phải sống hết `DrawImage`; Bitmap không được thoát ra ngoài scope `try/finally` của pin.
2. **`frame.annotated` đổi shape**: mọi consumer (`PipelineController`, `PipelineHostTests`, `JsonRpcClientTests`) phải cập nhật cùng commit; test "dòng 200 KB" ở `JsonRpcClientTests:359` giữ nguyên (vẫn là test chịu tải reader).
3. **MMF preview**: seqlock đúng như camera; reader retry có giới hạn; payload vượt capacity → drop frame + warn, **không** chết annotate loop; App mở MMF muộn (Core chưa tạo) → retry ≤ 1 lần/s.
4. **Pool**: `Frame.Dispose()` idempotent (double-dispose chỉ trả buffer 1 lần); mọi đường drop (`LatestFrameBuffer.Push`/`Dispose`) trả buffer về pool; không giữ tham chiếu buffer sau khi trả.
5. **Pill che nội dung**: đã duyệt bằng ảnh mẫu — reviewer không cần tranh luận lại style.
6. **JPEG cho OCR**: phải có test so PNG vs JPEG (mean abs diff nhỏ) để chứng minh không lệch dữ liệu vào model; giữ `MAX_INPUT_DIM=1280` thumbnail như cũ.

---

### Task 1: FrameAnnotator — pill + không copy BGRA + trả kích thước annotated

**Files:**
- Modify: `ReTran.Core/Serve/AnnotationSnapshot.cs` (thêm record + đổi `IFrameAnnotator`)
- Modify: `ReTran.Core/Serve/FrameAnnotator.cs`
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (call site `Annotate` + notify `w/h`)
- Test: `ReTran.Tests/FrameAnnotatorTests.cs` (4 call site + 3 test mới), `ReTran.Tests/PipelineHostTests.cs` (`FakeAnnotator`)

**Interfaces:**
- Consumes: `Frame` (`Width`, `Height`, `Stride`, `Pixels` BGRA tightly packed), `AnnotSnapshot(FrameWidth, FrameHeight, Boxes)`, `AnnotBox(X,Y,W,H,Text,Translated)` (toạ độ 0..1).
- Produces:
  ```csharp
  /// <summary>Annotated frame: JPEG bytes plus the output size. / Khung đã ghi chú: byte JPEG kèm kích thước đầu ra.</summary>
  public sealed record AnnotatedFrame(byte[] Jpeg, int Width, int Height);

  public interface IFrameAnnotator
  {
      AnnotatedFrame Annotate(Frame frame, AnnotSnapshot? snapshot);
  }
  ```

- [ ] **Step 1: Viết test RED** trong `FrameAnnotatorTests.cs`:
  - `Annotate_ReturnsAnnotatedSize_NotSourceSize`: `SolidFrame(2560,1440,…)` → `AnnotatedFrame` có `Width == 1280`, `Height == 720` (JPEG decode ra đúng cỡ này).
  - `Annotate_DrawsPillBehindText`: frame **trắng** 640×360 + box `(0.1,0.1,0.3,0.1,"Hello","Xin chào")` → decode JPEG → box ở `x=64, y=36, w=192, h=36`, text ở `ly = 36+36+2 = 74`, font 12,8 px ⇒ pill ≈ `x 62..124, y 73..90`.
    - **Pill tồn tại**: đếm pixel tối (`R,G,B < 60`) trong vùng `x 60..135, y 73..92` → `> 200`.
    - **Chữ trắng trên pill**: đếm pixel trắng (`> 230` cả 3 kênh) trong vùng `x 64..120, y 76..88` → `> 20` (vùng này nằm trong pill nên pixel trắng chỉ có thể là chữ).
  - `Annotate_LargeFrame_DoesNotCopyFullPixels`: frame 1920×1080, warm-up 1 lần, đo `GC.GetAllocatedBytesForCurrentThread()` trước/sau lần 2 → delta **< 4 MB** (trước fix ~8,5 MB do `CopyPixels`).
  - Cập nhật 4 call site cũ sang `AnnotatedFrame` (`.Jpeg`), giữ nguyên assert hiện có.
- [ ] **Step 2: Chạy RED**: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "FrameAnnotatorTests"` → 3 test mới fail (record chưa có / pill chưa có / allocation > 4 MB).
- [ ] **Step 3: Implement**
  - `AnnotationSnapshot.cs`: thêm `AnnotatedFrame`, đổi `IFrameAnnotator.Annotate` trả `AnnotatedFrame` (doc song ngữ).
  - `FrameAnnotator.Annotate`: bỏ `CopyPixels`; thay bằng
    ```csharp
    GCHandle pin = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
    try
    {
        using var source = new Bitmap(frame.Width, frame.Height, frame.Stride,
            PixelFormat.Format32bppArgb, pin.AddrOfPinnedObject());
        using var dest = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dest))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(source, 0, 0, outW, outH);
            g.CompositingMode = CompositingMode.SourceOver;
            if (snapshot is not null) DrawBoxes(g, snapshot, scale, outW, outH);
        }
        using var stream = new MemoryStream();
        dest.Save(stream, JpegEncoder(), JpegParameters());
        return new AnnotatedFrame(stream.ToArray(), outW, outH);
    }
    finally { pin.Free(); }
    ```
  - `DrawBoxes`: bỏ `outlineBrush` + vòng 8 offset; mỗi box: `DrawRectangle(pen, …)` → `label` → `SizeF size = g.MeasureString(label, font)` → `g.FillRectangle(pillBrush, lx - 2, ly - 1, size.Width + 4, size.Height + 2)` → `g.DrawString(label, font, textBrush, lx, ly)`. Clamp pill rect vào canvas.
  - `PipelineHost.cs:446`: `AnnotatedFrame annotated = annotator.Annotate(frame, session.Snapshot.TryGet());` và notify `["w"] = annotated.Width, ["h"] = annotated.Height` (giữ `["jpeg"]` **ở task này**, Task 3 sẽ bỏ).
  - `PipelineHostTests.FakeAnnotator`: trả `new AnnotatedFrame(jpeg, w, h)`.
- [ ] **Step 4: GREEN**: `dotnet build "ReTran Project.slnx"` + `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` (toàn bộ, phải xanh; `PipelineHostTests` notify giờ assert `w == 1280` với nguồn 1920 — cập nhật test hiện có nếu nó assert 1920).
- [ ] **Step 5: Evidence (đo thật)**: `pwsh -NoLogo -NoProfile -File "$env:TEMP\opencode\ab_annotate.ps1" -Seconds 60 -SettleSeconds 35 -DiscardSeconds 20 -Single -SingleLabel T1 -SingleAnnotFps 10` → ghi lại `core_cpu1core_avg` (target **0,45–0,55**, baseline W 0,81), `ann_line_KB_avg` (~338 KB — chưa đổi), `core_ramMB_max`.
- [ ] **Step 6: Commit** — `perf(core): pill annotation, no full-frame copy, annotated size in frame.annotated`

---

### Task 2: Bỏ clone 8 MB mỗi khung capture — buffer pool cho frame annotate

**Files:**
- Create: `ReTran.Core/Capture/BgraPool.cs`
- Modify: `ReTran.Core/Capture/Frame.cs`
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (handler clone tại `:152–164`; `Session` giữ pool)
- Test: new `ReTran.Tests/BgraPoolTests.cs`, new `ReTran.Tests/FramePoolTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  /// <summary>Reusable BGRA buffers. / Bộ đệm BGRA tái sử dụng.</summary>
  public interface IPixelPool
  {
      byte[] Rent(int length);
      void Return(byte[] buffer);
  }
  public sealed class BgraPool : IPixelPool { /* ConcurrentBag<byte[]>; trả array sai cỡ ⇒ bỏ */ }
  ```
  `Frame`: `static Frame FromBgra(int width, int height, byte[] pixels, IPixelPool? pool = null)`; `Dispose()` → `if (_pool is not null) _pool.Return(Pixels); Pixels = [];` (idempotent: nếu `Pixels.Length == 0` thì return luôn).
- Consumes: `LatestFrameBuffer.Push` drop cũ đã gọi `Dispose()` (⇒ buffer về pool), `AnnotateLoopAsync` gọi `frame.Dispose()` trong `finally` (⇒ về pool).

- [ ] **Step 1: RED** — `BgraPoolTests`: rent→return→rent trả **cùng mảng**; rent sai cỡ cấp mảng mới (không tự resize); `Return(null)`/`Return([])` không ném. `FramePoolTests`: `Frame.FromBgra(w,h,pixels,pool)` + `Dispose()` ⇒ pool nhận đúng mảng đó; double-dispose chỉ trả **1 lần**; `FromBgra` không pool giữ hành vi cũ (`Pixels` rỗng sau dispose).
- [ ] **Step 2: Implement** `BgraPool` + `Frame`; `PipelineHost`: thêm `Session.PixelPool` (tạo khi `trackAnnotatedFrames`), handler:
  ```csharp
  byte[] source = e.Frame.Pixels;
  byte[] buffer = session.PixelPool.Rent(source.Length);
  Array.Copy(source, buffer, source.Length);
  session.AnnotBuffer!.Push(Frame.FromBgra(e.Frame.Width, e.Frame.Height, buffer, session.PixelPool));
  ```
  (`Rent` trả mảng **đúng bằng** `source.Length` — pool kiểm tra và cấp mới nếu lệch; `Frame.FromBgra` ném nếu `pixels.Length != height*stride`.)
- [ ] **Step 3: GREEN** — full `dotnet test` xanh (đặc biệt `PipelineHostTests`, `FrameAnnotatorTests.AnnotatorFrameCopy_SurvivesSourceDispose`).
- [ ] **Step 4: Evidence** — probe `T2` (cùng lệnh Task 1) → so `core_ramMB_max` / `core_peak_wsMB` với T1 (kỳ vọng giảm).
- [ ] **Step 5: Commit** — `perf(core): pool BGRA buffers for the annotate frame (no 8 MB clone per capture)`

---

### Task 3: Preview qua shared memory — bỏ base64/JSON lớn khỏi stdio

**Files:**
- Create: `ReTran.Core/Serve/PreviewProtocol.cs` (hằng số + layout, mirror `CameraProtocol` style)
- Create: `ReTran.App/Services/Preview/PreviewProtocol.cs` (mirror y hệt — cùng pattern duplicate của camera)
- Create: `ReTran.App/Services/Preview/PreviewChannelReader.cs`
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (tạo/huỷ writer theo session; ghi MMF trước notify; notify bỏ `jpeg`)
- Modify: `ReTran.App/Core/PipelineController.cs` (`HandleFrameAnnotated` đọc MMF)
- Test: new `ReTran.Tests/PreviewChannelTests.cs`, new `ReTran.Tests/PreviewProtocolTests.cs`, update `ReTran.Tests/PipelineControllerTests.cs`, `ReTran.Tests/PipelineHostTests.cs`

**Interfaces:**
- Layout (header 32 byte, mirror byte-for-byte giữa Core/App):
  ```
  off  size  field
   0    4    magic   = 0x45525052 ('RPRE')
   4    4    version = 1
   8    4    width   (khung annotated)
  12    4    height
  16    4    jpegLength
  20    4    seq     (seqlock: lẻ = đang ghi, chẵn = ổn định)
  24    8    lastWriteTickMs
  32   ...   jpeg bytes
  ```
  `Capacity = 2 * 1024 * 1024` (JPEG 1280-wide q70 ~150–400 KB; vượt ⇒ `ArgumentException`, loop bắt + warn + drop khung).
- Produces (Core): `sealed class PreviewChannelWriter : IDisposable` — `PreviewChannelWriter(string? mapName = null)`, `void Write(int width, int height, ReadOnlySpan<byte> jpeg)`.
- Produces (App): `sealed class PreviewChannelReader : IDisposable` — `static bool TryOpen(string? mapName, out PreviewChannelReader? reader)`, `bool TryRead(out PreviewChannelFrame frame)` với `readonly record struct PreviewChannelFrame(long Seq, int Width, int Height, byte[] Jpeg)` (retry ≤ 4 lần như `CameraFrameReader`).
- Consumes (App): `ICameraOutput.Submit(long seq, int w, int h, byte[] jpeg)` (không đổi), `PreviewState.Publish(PreviewFrame(seq,w,h,base64))` (không đổi — WebView vẫn cần data URL).

- [ ] **Step 1: RED** — `PreviewChannelTests`: writer→reader round-trip (unique name qua `Guid`), `Seq` tăng 2/lần, payload đọc đúng byte-for-byte, magic sai ⇒ `TryRead` false, payload > capacity ⇒ `Write` ném. `PreviewProtocolTests`: hằng số/offset của Core == App. `PipelineControllerTests` (dùng writer thật + tên unique): notify `{seq,w,h}` ⇒ preview publish đúng base64 + camera nhận **cùng byte**; chưa có MMF ⇒ không publish, không ném; seq cũ hơn `_lastPreviewSeq` ⇒ bỏ qua.
- [ ] **Step 2: Implement** — Core: writer tạo trong `StartAsync` (chỉ khi `_annotator is not null`), `Write(annotated.Width, annotated.Height, annotated.Jpeg)` ngay trước `NotifyAsync` (payload `{seq,w,h}`), dispose trong `EndSession`; lỗi tạo/ghi ⇒ `warn` + tắt channel, **không** chết loop. App: `PipelineController` nhận `string previewMapName = PreviewProtocol.MapName` (tham số ctor tuỳ chọn) + giữ `PreviewChannelReader?` lazy, `EnsurePreviewChannel()` retry ≤ 1 lần/s; publish/camera theo `frame` đọc được.
- [ ] **Step 3: GREEN** — full `dotnet test` xanh.
- [ ] **Step 4: Evidence** — probe `T3`: `rpc_MBps` **≤ 0,05** (baseline 2,93), `ann_line_KB_avg` → ~0,1 KB; `core_cpu1core_avg` giảm tiếp so T2. (Lưu ý: `ann_line_KB_avg` nhỏ là **đúng thiết kế mới** — kích thước JPEG giờ đo qua MMF.)
- [ ] **Step 5: Commit** — `perf(core,app)!: send annotated frames over shared memory, drop jpeg from frame.annotated`

---

### Task 4: OCR transport — JPEG thay PNG

**Files:**
- Create: `ReTran.Core/Capture/BgraJpegEncoder.cs`
- Modify: `ReTran.Core/Cli/OcrLoop.cs:206`, `ReTran.Core/Ocr/OcrPipeline.cs` (rename param + doc)
- Modify: `ReTran.OCR/src/retran_core/ocr/image.py` (`decode_png_b64` → `decode_image_b64`), `ReTran.OCR/src/retran_core/ocr/handlers.py` (10 call site), test sidecar
- Test: new `ReTran.Tests/BgraJpegEncoderTests.cs`, update `ReTran.Tests/OcrLoopTests.cs`; `ReTran.OCR/tests/test_ocr_handlers.py`, `test_paddle_integration.py`, `test_perf_gate.py`

**Interfaces:**
- Produces: `BgraJpegEncoder.ToJpegBytes(Frame frame, long quality = 85)` (magic `FF D8`), `ToJpegBase64(Frame)` — cùng shape `BgraPngEncoder`.
- Sidecar: `decode_image_b64(image_b64: str) -> npt.NDArray[np.uint8]` — doc "base64 PNG **hoặc** JPEG"; giữ `MAX_INPUT_DIM = 1280` thumbnail + BGR trả về như cũ; lỗi ⇒ `ValueError("invalid image: …")`.

- [ ] **Step 1: RED** — `BgraJpegEncoderTests`: bytes bắt đầu `FF D8`, decode ra đúng W×H, cùng frame JPEG nhỏ hơn PNG; `OcrLoopTests`: payload gửi `SpotAsync` bắt đầu `FF D8` (fake pipeline ghi lại chuỗi); sidecar: `decode_image_b64` nhận PNG **và** JPEG, ảnh JPEG cùng nguồn sai khác trung bình `< 3` so với PNG, base64 rác ⇒ `ValueError`.
- [ ] **Step 2: Implement** C# + đổi tên Python (giữ tham số RPC `image` nguyên).
- [ ] **Step 3: GREEN** — `dotnet test` + `cd ReTran.OCR; uv run pytest tests -v`.
- [ ] **Step 4: Evidence** — số đã đo: PNG 28 ms/233 KB vs JPEG q85 10 ms/201 KB (`png_vs_jpeg.ps1`, 5 lần, màn hình thật) → ~18 ms tiết kiệm mỗi chu kỳ OCR (~0,02 core).
- [ ] **Step 5: Commit** — `perf(ocr): send JPEG instead of PNG to the sidecar`

---

### Task 5: Nối `captureFps` vào capture source (gap spec §3.1)

**Files:**
- Modify: `ReTran.Core/Cli/OcrLoop.cs` (`OcrLoopOptions` + `CreateSource`)
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (`StartSpec`/`ToOptions` + comment `:374–377`)
- Test: `ReTran.Tests/PipelineHostTests.cs` (factory nhận `CaptureFps`), update ctor call sites nếu vỡ

**Interfaces:**
- `public sealed record OcrLoopOptions(bool UseWindow, int Monitor, nint Hwnd, int Fps, int MaxFrames, int CaptureFps = 10);`
- `OcrLoop.CreateSource`: `new ScreenCaptureSource(new ScreenTarget(options.Monitor), logger: null, fps: options.CaptureFps)` / `new WindowCaptureSource(new WindowTarget(options.Hwnd), logger: null, fps: options.CaptureFps)`.
- `Session.ToOptions()` truyền `CaptureFps` (field đã có trong `StartSpec`); comment cũ ở `ParseStart` xoá.

- [ ] **Step 1: RED** — `PipelineHostTests`: `core.start` với `captureFps: 5` ⇒ source factory nhận `options.CaptureFps == 5`; `captureFps: 0` ⇒ `InvalidParams`; không truyền ⇒ bằng config (10).
- [ ] **Step 2: Implement**; annotate loop vẫn dùng `_annotatorFps` như cũ (đổi hành vi annotate là việc khác — ghi chú trong doc comment).
- [ ] **Step 3: GREEN** — full `dotnet test`.
- [ ] **Step 4: Evidence** — probe `T5` với config `captureFps=5` (`%LOCALAPPDATA%\ReTran\core\config.json`, nhớ **xoá lại** sau đo): Core CPU giảm ~0,05–0,1 core so với T3 cùng điều kiện; hoặc chỉ ghi nhận test-level nếu chênh dưới nhiễu.
- [ ] **Step 5: Commit** — `fix(core): plumb captureFps from core.start into the capture source`

---

### Task 6: Đo lại + docs + đề xuất revision budget

**Files:**
- Modify: `Docs/performance.md` (mục mới `### M5-fix — sau tối ưu (2026-10-…)`)
- Modify: `AGENTS.md` (budget rows — **chỉ sau khi user duyệt**)
- Modify: `Docs/superpowers/specs/2026-10-17-virtual-camera-preview-design.md` (ghi revision: preview qua MMF, payload `frame.annotated` mới, link plan này)
- Modify: plan này (tick + số cuối)

- [ ] **Step 1: Đo app-driven**: mở `ReTran.App` + game + OBS preview + device camera (không stream), sampler `perf_sample2.ps1` ≥ 2 phút × 2 cửa sổ; ghi app/core/sidecar CPU-RAM, camera fps (`Δseq/2`), và kiểm tra preview + camera đúng pill (nhờ user xác nhận hình). — **chưa đo lại (pending user game session, 2026-10-21)**; số app-driven cũ (Core 1,85 core / RAM 923 MB avg / 1803 max) ở bảng M5 chỉ là **mốc trước fix**.
- [x] **Step 2: Đo scripted**: probe `T6` cùng tham số W (60 s) để so 1-1 với baseline 0,81 core / 2,93 MB/s. — T6c/T6e (Notepad 260 dòng, settle 40 s + measure 60 s bỏ 15 s đầu): Core **0,455–0,46 core** (baseline 0,81; −43 %), `rpc_MBps` **0,058–0,063** (từ 2,93), `ann_line_KB_avg` **0,1 KB** (từ 337,6), Core RAM max **321–330 MB** (từ 546); A/B post-fix T6c 10 fps (46,0 %, 8,77 fps) vs T6d 2 fps (11,0 %, 1,72 fps).
- [x] **Step 3: Ghi `Docs/performance.md`**: bảng trước→sau (annotate ms/khung, Core cpu, IPC stdio, RAM max, camera fps) + kết luận PASS/FAIL mới + đề xuất revision budget (annotate ≤ 25 ms/khung; IPC stdio "≈0 — frame qua MMF"; RAM tổng vẫn chờ quyết định sidecar). — §M5-fix (2026-10-21) đã ghi; revision chốt theo benchmark code thật: **annotate ≤ 40 ms/khung @ ~60 box** (37,6 ms @63 box đo được — không đạt 27 ms của DoD vì sàn GDI+ 19–22 ms cố định); IPC/CPU/RAM-Core PASS-or-improved; sidecar ~1,25 core / ~2 GB là blocker của budget tổng.
- [x] **Step 4: Cập nhật `AGENTS.md`** (nếu user duyệt revision) + revision note trong spec. — user duyệt in principle; đã thêm 2 hàng budget (annotate ≤ 40 ms/khung @ ~60 box; JSON-RPC stdio ≈ 0 cho khung qua MMF, khác ≤ 1,5 MB/s) + note sidecar; spec camera/preview có `## Revision (2026-10-21)` (khung qua `Local\ReTran.Preview.V1`, `frame.annotated` = `{seq,w,h}`).
- [x] **Step 5: Commit** — `docs: record M5 hot-path fixes and revised budgets`

**Kết quả Task 6 (2026-10-21, docs-only):** scripted — Core 0,455–0,46 core (PASS ≤ 0,55) · IPC 0,058 MB/s (PASS theo budget ≤ 1,5; mốc DoD 0,05 chỉ hụt 0,008) · `ann_line` 0,1 KB (PASS ≤ 0,2) · Core RAM max 321–330 MB (thấp hơn baseline 546). **Annotate 37,6 ms @63 box không đạt 27 ms** (sàn GDI+ ~19–22 ms cố định: downscale ~9 + JPEG ~5 + dest/ToArray) → revision **≤ 40 ms/khung @ ~60 box** (user duyệt in principle, đã ghi AGENTS.md). **Sidecar Paddle ~1,25 core / ~2 GB là blocker duy nhất còn lại của budget tổng** (open item — chờ user quyết ONNX Runtime hay giữ Paddle). App-driven **chưa đo lại — pending user session**.

---

## Acceptance (Definition of Done)

1. `dotnet build "ReTran Project.slnx"` sạch; `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` xanh toàn bộ; `cd ReTran.OCR; uv run pytest tests -v` xanh.
2. Probe scripted (60 s, màn hình thật): **Core ≤ 0,55 core**, **IPC stdio ≤ 0,05 MB/s**, `ann_line_KB_avg` ≤ 0,2 KB.
3. Annotate/khung ≤ 27 ms (suy từ core CPU − capture/OCR/IO nền ~0,2 core), đo bằng cùng phương pháp A−B nếu cần.
4. App-driven: camera 8–9 fps, preview hiển thị pill đúng, **user xác nhận bằng mắt**; Core CPU thấp hơn baseline 1,85 core rõ rệt (kỳ vọng ≤ 1,2).
5. RAM Core khi probe thấp hơn baseline (max 530–788 MB); **không** hứa được budget RAM tổng < 500 MB (sidecar ~2 GB — open item).
6. Docs: `Docs/performance.md` §M5-fix có bảng trước/sau + kết luận; `AGENTS.md` chỉ sửa khi user duyệt; spec có revision note.

## Out of scope / Open items

- **Sidecar Paddle GPU** (1,25 core, ~2 GB RAM, 362 MB VRAM): user chưa quyết (2026-10-19) — giữ nguyên; hướng khác là milestone ONNX Runtime GPU.
- **Budget CPU tổng < 8 %**: không đạt được chỉ bằng plan này (sidecar chiếm ~10 % một mình) — Task 6 nêu rõ khi đề xuất revise budget.
- **Dịch thật (Google)**: đang 429 liên tục — nhánh dịch thành công chưa đo được (đã ghi ở §M5 Ghi chú).
- WebView2 child-process chưa tính vào số app (gap đo — ghi chú, không fix ở đây).
