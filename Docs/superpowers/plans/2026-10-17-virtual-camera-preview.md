# Camera ảo + Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thay đường ra OBS bằng camera ảo kiểu OBS Virtual Camera (khung capture + box + bản dịch) kèm màn hình preview lớn kiểu OBS trong trang điều khiển, và bỏ hẳn HTTP feed.

**Architecture:** Core render khung annotated (downscale ≤1280w, GDI+ draw box + translated, JPEG q70) rồi đẩy notification `frame.annotated` ≤10 fps. App nhận → `PreviewState` (preview `<img>` ≤15 fps) + `CameraOutputService` ghi BGRA vào shared memory (seqlock); filter camera native `ReTran.Camera.dll` (DirectShow source, C++/MSVC x64, đăng ký HKCU per-user) đọc shared memory, convert YUY2 1280×720@30, ra frame đen khi app đóng (heartbeat), giữ khung cuối khi pipeline dừng (flag HOLD).

**Tech Stack:** .NET 10 (C#: annotator dùng `System.Drawing.Common`, mmap qua `MemoryMappedFile`), C++/MSVC x64 DirectShow (`IFilterMapper2`), xUnit v3.

**Spec:** `Docs/superpowers/specs/2026-10-17-virtual-camera-preview-design.md`

## Global Constraints

- Camera device: **YUY2 1280×720@30 fps**, tên **"ReTran Camera"**, CLSID `{B7E6A1C4-52D8-4F3A-9C11-6D2E8F40A917}`; đăng ký **HKCU per-user, không admin**, idempotent mỗi lần app mở.
- Vòng đời: app đóng → device vẫn thấy, **ra hình đen**; pipeline Start → phát hình; pipeline Stop → **giữ khung cuối** (không đen).
- Notification mới: method `"frame.annotated"`, params `{seq: long, w: int, h: int, jpeg: string(base64)}`.
- Shared memory: frame `Local\ReTran.Camera.V1` (header 48 byte, magic `0x4D414352`, version 1, BGRA), heartbeat `Local\ReTran.Camera.Heartbeat` (writer-only; filter Open mỗi lần check → mất là writer đã thoát).
- JPEG: quality **70**, maxWidth **1280** (downscale giữ tỉ lệ, không upscale). Annotate ≤ **10 fps**; preview refresh ≥ **100 ms/lần**; style box: viền `#22C55E` 2 px, chữ dịch vẽ ngay dưới box, font Segoe UI size clamp `12..(0.02×width_scaled)`.
- Budget: tổng < 8% CPU, < 500 MB RAM khi gaming + streaming; annotator ≤ 10 ms/khung; IPC ≤ 1.5 MB/s; ghi số vào `Docs/performance.md`.
- Giữ nguyên: overlay local đè game, pipeline capture/OCR/translate, Python sidecar (không sửa file nào trong `ReTran.OCR/`).
- Bỏ hẳn HTTP feed: không còn tham chiếu `OverlayFeedServer`/`IOverlayFeedServer`, port **17863** biến mất, `wwwroot/overlay/` xóa.
- Mọi chuỗi UI mới: i18n đủ **vi + en**. Doc comment C#: song ngữ Anh/Việt.
- Build/test xanh trước mỗi commit: `dotnet build "ReTran Project.slnx"` và `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`. Commit đúng file của task.

## Review Focus

1. **JSON line ~200 KB (JPEG base64) qua stdio JSON-RPC** — nếu reader có buffer/line limit thì `frame.annotated` bị cắt/lỗi → test round-trip dòng 200 KB ở Task 1.
2. **Writer thoát mà filter đang giữ handle mmap → hình treo cũ thay vì đen** — test heartbeat: sau khi `Dispose()` writer, `OpenFileMapping("Local\ReTran.Camera.Heartbeat")` fail (Task 4) + bước manual đóng app quan sát OBS đen.
3. **Pipeline Stop phải giữ khung cuối, không đen** — test: sau `Hold()`, flags bit0 set, seq/pixels giữ nguyên, heartbeat vẫn còn (Task 4).
4. **Đăng ký per-user không chắc hiện ở cả 3 app** (OBS dshow / Discord MF / Zoom) — gate manual ở Task 3; fail → dừng, báo user chọn phương án cần admin (HKLM).
5. **Preview 10 fps data-URL nặng trong WebView** — throttle ≥100 ms pin ở Task 2, đo lại ở Task 6 (không unit test được, acceptance tay).

---

### Task 1: Core — FrameAnnotator + notification `frame.annotated`

**Files:**
- Create: `ReTran.Core/Serve/AnnotationSnapshot.cs`
- Create: `ReTran.Core/Serve/FrameAnnotator.cs`
- Modify: `ReTran.Core/Serve/TranslateCycleSink.cs` (publish snapshot trong `OnCycleAsync`)
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (ctor + annotation loop song song OcrLoop)
- Modify: `ReTran.Core/Program.cs` (truyền annotator thật vào `PipelineHost`)
- Modify: `ReTran.Core/ReTran.Core.csproj` (thêm `System.Drawing.Common` 10.*)
- Test: `ReTran.Tests/FrameAnnotatorTests.cs`, `ReTran.Tests/AnnotationSnapshotTests.cs`, sửa `ReTran.Tests/TranslateCycleSinkTests.cs`, `ReTran.Tests/PipelineHostTests.cs`, thêm big-line case vào `ReTran.Tests/JsonRpcClientTests.cs`

**Interfaces:**
- Consumes: `Capture.Frame` (`Frame.FromBgra(w,h,pixels)`, `Pixels` BGRA), `LatestFrameBuffer` (`Push`/`TryTakeLatest`), `ICaptureSource.FrameArrived`, `JsonRpcServer.NotifyAsync(method, node)`.
- Produces (Task 2 và sau dùng):
  - `public sealed record AnnotBox(double X, double Y, double W, double H, string Text, string? Translated);` (toạ độ chuẩn hoá 0–1)
  - `public sealed record AnnotSnapshot(int FrameWidth, int FrameHeight, IReadOnlyList<AnnotBox> Boxes);`
  - `public sealed class AnnotationSnapshot { void Publish(int frameWidth, int frameHeight, IReadOnlyList<AnnotBox> boxes); AnnotSnapshot? TryGet(); }` (thread-safe)
  - `public interface IFrameAnnotator { byte[] Annotate(Frame frame, AnnotSnapshot? snapshot); }` → trả `byte[]` JPEG thô (không base64); `PipelineHost` base64 khi notify.
  - Notification `"frame.annotated"` params `{seq, w, h, jpeg}` (base64) — seq long tăng dần bắt đầu 1 mỗi phiên.
  - `PipelineHost` ctor thêm tham số tùy chọn `IFrameAnnotator? annotator = null, int annotatorFps = 10` (null = tắt, test cũ không vỡ).

- [ ] **Step 1: Viết failing tests**

`AnnotationSnapshotTests`: `PublishThenTryGet_ReturnsLatestSnapshot` (2 lần publish → lấy bản thứ 2); `TryGet_BeforePublish_ReturnsNull`.

`FrameAnnotatorTests` (dùng thật `FrameAnnotator`, System.Drawing decode để assert):
- `Annotate_WithoutSnapshot_ReturnsJpegCappedAt1280`: frame `Frame.FromBgra(2560, 1440, pixels màu đơn)` → decode JPEG → `width == 1280`, `height == 720`.
- `Annotate_WithSnapshot_DrawsGreenBox`: frame 640×360 màu đen + snapshot box `(0.1,0.1,0.3,0.1,"Hello","Xin chào")` → scan region box, đếm pixel `G>150 && R<120 && B<120` > 0.
- `Annotate_RendersVietnameseAndSymbol_WithoutThrowing`: text `"Xin chào ∑ ẻ"` → không ném, JPEG != null.
- `AnnotatorFrameCopy_SurvivesSourceDispose`: handler copy (clone) pixel từ frame nguồn, `source.Dispose()` sau đó `Annotate(copy)` vẫn ra JPEG hợp lệ (bảo vệ race: OcrLoop dispose chung instance — xem Step 3).

`PipelineHostTests` (mẫu test notification có sẵn):
- `Start_EmitsFrameAnnotated_WithJpegPayload`: fake source + annotator giả trả `[1,2,3]` → chờ notification `"frame.annotated"`, assert `seq==1`, `w/h` đúng frame, `jpeg` base64 decode = `[1,2,3]`.
- `Stop_AnnotationLoop_StopsEmitting`: sau `core.stop` (StopAsync) không còn notification mới (dùng channel đếm, chờ 300 ms im lặng).

`TranslateCycleSinkTests` (sửa ctor cũ cho snapshot mới):
- `OnCycle_PublishesAnnotationSnapshot`: gọi `OnCycleAsync` với boxes có `translated` → `snapshot.TryGet()` chứa box/text/translated + `FrameWidth/Height`.

`JsonRpcClientTests`:
- `Can_Roundtrip_200KB_Params`: in-proc client/server, params chứa chuỗi 200_000 ký tự ASCII → reply nguyên vẹn.

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: FAIL — `AnnotationSnapshot`/`FrameAnnotator` chưa tồn tại (compile error) + big-line test fail nếu có limit.

- [ ] **Step 3: Implement**

- csproj: `<PackageReference Include="System.Drawing.Common" Version="10.*" />` (đồng bộ version App đang dùng — tra trong `ReTran.App.csproj`).
- `AnnotationSnapshot`: lock đơn giản, `Publish` ghi đè, `TryGet` trả snapshot copy.
- `FrameAnnotator.Annotate`: tính tỉ lệ scale `min(1, 1280f/frame.Width)` → `Bitmap` mới (PixelFormat Format32bppArgb, LockBits nạp `frame.Pixels`), nếu snapshot != null scale box theo tỉ lệ rồi vẽ (`Pen(Color.FromArgb(255,34,197,94), 2f)` + chữ `Brush` trắng outline đen, font size clamp `12..0.02×w`), `Image.Save(ms, ImageFormat.Jpeg)` với `EncoderParameters` quality 70 → `ms.ToArray()`.
- `TranslateCycleSink`: ctor thêm `AnnotationSnapshot snapshot` (param cuối, `ArgumentNullException.ThrowIfNull`); trong `OnCycleAsync` nơi dựng `boxes` JSON, đồng thời gom `List<AnnotBox>` → `snapshot.Publish(frameWidth, frameHeight, list)`.
- `PipelineHost`:
  - session thêm `LatestFrameBuffer AnnotBuffer` + `Task? AnnotLoop`.
  - `StartAsync`: nếu `_annotator != null` → `EventHandler<FrameArrivedEventArgs> onAnnotFrame = (_, e) => annotBuffer.Push(Frame.FromBgra(e.Frame.Width, e.Frame.Height, (byte[])e.Frame.Pixels.Clone()));` (**clone — frame là instance chung, OcrLoop có quyền dispose**, ghi chú trong code) ; đăng ký vào `source.FrameArrived`; `session.AnnotLoop = Task.Run(AnnotateLoopAsync(session, …))`.
  - `AnnotateLoopAsync`: `PeriodicTimer(TimeSpan.FromMilliseconds(1000.0/annotatorFps))`, mỗi tick `TryTakeLatest` → dispose frame cũ → `jpeg = _annotator.Annotate(frame, snapshot.TryGet())` → `server.NotifyAsync("frame.annotated", {seq++, w=frame.Width, h=frame.Height, jpeg=Convert.ToBase64String(jpeg)})`; thoát khi `session.Cts` cancel; `finally` unsubscribe handler (mẫu giống OcrLoop).
  - snapshot: tạo trong `StartAsync`, truyền vào sink; seq reset mỗi phiên.
  - Notify lỗi nuốt + warn stderr (mẫu `NotifyStateSafeAsync`).
- `Program.cs`: truyền `new FrameAnnotator()` + annotatorFps từ `CoreConfig.OcrFps`? **Không** — annotatorFps độc lập, ghim 10 (đúng spec "capture fps ≤10"; `captureFps` config đã có, dùng `config.CaptureFps`).

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet build "ReTran Project.slnx"` rồi `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: build 0 error (chỉ còn 4 xUnit1051 cũ), test tất cả PASS (247 cũ + mới).

- [ ] **Step 5: Commit**

```bash
git add ReTran.Core/Serve/AnnotationSnapshot.cs ReTran.Core/Serve/FrameAnnotator.cs ReTran.Core/Serve/TranslateCycleSink.cs ReTran.Core/Serve/PipelineHost.cs ReTran.Core/Program.cs ReTran.Core/ReTran.Core.csproj ReTran.Tests/FrameAnnotatorTests.cs ReTran.Tests/AnnotationSnapshotTests.cs ReTran.Tests/TranslateCycleSinkTests.cs ReTran.Tests/PipelineHostTests.cs ReTran.Tests/JsonRpcClientTests.cs
git commit -m "feat(core): annotate frames and emit frame.annotated notification"
```

---

### Task 2: App — PreviewState + preview pane layout A

**Files:**
- Create: `ReTran.App/Core/PreviewState.cs`
- Modify: `ReTran.App/Core/PipelineController.cs` (case `"frame.annotated"` → `HandleFrameAnnotated`)
- Modify: `ReTran.App/Components/Pages/Home.razor` (preview pane layout A + badge)
- Modify: `ReTran.App/Services/I18nStrings.cs` (keys mới vi/en)
- Modify: `ReTran.App/MauiProgram.cs` (DI singleton `PreviewState`)
- Test: `ReTran.Tests/PreviewStateTests.cs`, sửa `ReTran.Tests/PipelineControllerTests.cs`

**Interfaces:**
- Consumes (Task 1): notification `"frame.annotated"` `{seq,w,h,jpeg}`.
- Produces (Task 4 dùng): `public sealed record PreviewFrame(long Seq, int W, int H, string JpegBase64);` + `public sealed class PreviewState { PreviewFrame? Latest { get; } event Action<PreviewFrame>? Changed; void Publish(PreviewFrame frame); }`
  - `PipelineController` ctor thêm `PreviewState preview` (trước tham số `log`… thêm cuối cho ít vỡ nhất — chốt: thêm vào **cuối**, optional `PreviewState? preview = null` để test cũ không vỡ; prod DI truyền singleton).

- [ ] **Step 1: Viết failing tests**

`PreviewStateTests`: `Publish_SetsLatestAndRaisesChanged` (Latest đúng, event nhận đúng frame); `PublishTwice_LatestIsLast`.

`PipelineControllerTests` (mẫu inject notification có sẵn):
- `FrameAnnotated_Notification_PublishesPreview`: bắn notification `frame.annotated` với seq/w/h/jpeg → `preview.Latest` đúng 4 field.

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: FAIL (type `PreviewState` chưa tồn tại).

- [ ] **Step 3: Implement**

- `PreviewState`: class đơn, lock, `Publish` ghi `Latest` + `Changed?.Invoke`.
- `PipelineController`: case `"frame.annotated"` trong switch notification → `HandleFrameAnnotated(JsonNode? p)` parse `seq/w/h/jpeg` (mẫu parse `HandleFrameTranslated`, ép `JsonValueKind.String` cho jpeg) → `_preview?.Publish(...)`.
- i18n keys (vi/en): `home.preview.title` ("Xem trước"/"Preview"), `home.preview.running` ("Đang phát"/"Live"), `home.preview.stopped` ("Đang dừng"/"Stopped"), `home.preview.demo` ("Bản demo"/"Demo"), `home.preview.idle` ("Chưa có khung hình"/"No frame yet").
- `Home.razor` layout A (ghim cấu trúc):
  - Vùng preview **trên cùng**, trước lưới card: khung tỉ lệ 16:9, nền `#0f172a`, `<img class="w-full h-full object-contain">` với `src="@_previewSrc"`; nếu `PreviewState.Latest == null` → text `home.preview.idle` mờ.
  - Badge góc trái trên preview: pipeline running → `home.preview.running` (xanh `#22c55e`), stopped → `home.preview.stopped` (vàng); `_feed is not null` (demo đang bật) → badge `home.preview.demo` đè thêm.
  - Code-behind: inject `PreviewState`; subscribe `Changed` → **throttle ≥100 ms** (so sánh `Environment.TickCount64` lần render cuối) → `_previewSrc = $"data:image/jpeg;base64,{f.JpegBase64}"` + `InvokeAsync(StateHasChanged)`; unsubscribe `DisposeAsync`.
  - Card "OBS" **chưa** sửa ở task này (Task 5 dọn).
- `MauiProgram`: `AddSingleton<PreviewState>()`.

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet build "ReTran Project.slnx"` và `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: xanh.

- [ ] **Step 5: Chạy tay app (kiểm preview)**

Chạy `ReTran.App.exe`, Start pipeline nguồn window → preview hiện khung annotated ≤1 s; Stop → giữ khung + badge "Đang dừng". (Nội dung chi tiết ảnh camera là Task 4.)

- [ ] **Step 6: Commit**

```bash
git add ReTran.App/Core/PreviewState.cs ReTran.App/Core/PipelineController.cs ReTran.App/Components/Pages/Home.razor ReTran.App/Services/I18nStrings.cs ReTran.App/MauiProgram.cs ReTran.Tests/PreviewStateTests.cs ReTran.Tests/PipelineControllerTests.cs
git commit -m "feat(app): OBS-style preview pane fed by frame.annotated"
```

---

### Task 3: Native — filter ReTran.Camera (skeleton) + đăng ký HKCU + GATE visible

**Files:**
- Create: `ReTran.Camera/ReTran.Camera.vcxproj` (x64, DynamicLibrary, MSVC v143+, Windows SDK)
- Create: `ReTran.Camera/src/dllmain.cpp`, `ReTran.Camera/src/filter.h`, `ReTran.Camera/src/filter.cpp` (source filter + 1 pin, YUY2 1280×720@30), `ReTran.Camera/src/register.cpp` (exports), `ReTran.Camera/src/guids.cpp`
- Modify: `ReTran Project.slnx` (thêm project)
- Modify: `ReTran.App/ReTran.App.csproj` (`AfterBuild` target copy DLL → output nếu tồn tại)
- Create: `ReTran.App/Services/Camera/CameraRegistrar.cs`
- Modify: `ReTran.App/MainPage.xaml.cs` (gọi `EnsureRegistered()` lúc khởi động — cùng chỗ đang lấy `OverlayFeedServer` từ DI)
- Test: `ReTran.Tests/CameraRegistrarTests.cs`

**Interfaces:**
- Consumes: —
- Produces (Task 4 dùng): DLL exports `extern "C" __declspec(dllexport) HRESULT __stdcall ReTranCamera_Register();` và `ReTranCamera_Unregister();`; CLSID `{B7E6A1C4-52D8-4F3A-9C11-6D2E8F40A917}`; FriendlyName `"ReTran Camera"`; `public static class CameraRegistrar { void EnsureRegistered(string cameraDllPath); }` (ctor nhận `Action<string>` seam cho test + path mặc định = `AppContext.BaseDirectory\ReTran.Camera.dll`).

- [x] **Step 1: Dựng vcxproj + filter skeleton, build được**

> Deviation (đã verify): SDK `strmbase.lib` quá cũ (v143, LNK2001 với v145) → vendor `baseclasses/` từ `microsoft/Windows-classic-samples` @ `434f600` (kèm LICENSE + VENDOR.md), dùng `/FS`; vcxproj cần `PlatformToolset=v145` tường minh. Pin cần thêm `IAMStreamConfig` (OBS/dshow require) và `IKsPropertySet` (`Get(AMPROPSETID_Pin, AMPROPERTY_PIN_CATEGORY)=PIN_CATEGORY_CAPTURE` — ffmpeg skip device nếu thiếu) + `DECLARE_IUNKNOWN` ở derived class. `biHeight` xuất **dương** (ffmpeg dshow truyền thẳng qua không `abs()`).

Run: `msbuild "ReTran Project.slnx" /p:Platform=x64 /p:Configuration=Debug` (sau khi thêm slnx)
Expected: build ra `ReTran.Camera.dll` không lỗi.

- [x] **Step 2: Đăng ký per-user (IFilterMapper2) trong `register.cpp`**

> Verified empirical (regsvr32 round-trip): register exit 0 → HKCU CLSID + `...\Instance\{CLSID}` đúng key, unregister sạch, idempotent, double-unregister OK. Ghi HKCR redirect sang HKCU **chỉ khi key đích đã tồn tại** → pre-create `RegCreateKeyExW(HKCU, ...)` bắt buộc; `szInstance = nullptr` (instance subkey = CLSID); cleanup (`UnregisterFilter` + `AMovieSetupUnregisterServer`) phải chạy **trước** pre-create.

- `ReTranCamera_Register`: `CoInitializeEx` (apartment dùng được từ bất kỳ thread nào — gọi `COINIT_MULTITHREADED` rồi `CoUninitialize` cuối), tạo `CLSID_FilterMapper2` → **pre-create key** `HKCU\Software\Classes\CLSID\{CLSID}` trước khi `RegisterFilter` (để ghi vào HKCU thay vì HKLM — cần admin mới ghi HKLM\Software\Classes) → `RegisterFilter(CLSID, L"ReTran Camera", &regid, &CLSID_VideoInputDeviceCategory, &merit, pin type YUY2)` — merit `0x400000`.
- `ReTranCamera_Unregister`: `UnregisterFilter(... VFW cat ...)`.
- Idempotent: gọi lại không nhân đôi (RegisterFilter mặc định `RegFilterFlags::Standard` — nếu đã có thì thay thế; verify ở Step 4).

- [x] **Step 3: CameraRegistrar (C#) + test RED→GREEN** (263/263 pass; delegate `StdCall`, never-throws)

Test `CameraRegistrarTests`:
- `EnsureRegistered_InvokesRegistrarWithDllPath`: spy `Action<string>` → assert nhận `Path.Combine(AppContext.BaseDirectory, "ReTran.Camera.dll")`.
- `EnsureRegistered_MissingDll_DoesNotThrow`: path không tồn tại → im lặng (không ném).

Implement: `CameraRegistrar` P/Invoke `LoadLibrary` + `GetProcAddress("ReTranCamera_Register")` + `Marshal.GetDelegateForFunctionPointer` → invoke; missing file/proc → nuốt (log nếu có logger — không có thì nuốt im lặng, giống CoreLocator "never throws").

Wiring: `MainPage.xaml.cs` startup — `new CameraRegistrar().EnsureRegistered(Path.Combine(AppContext.BaseDirectory, "ReTran.Camera.dll"))` (chạy **sau** khi copy DLL Step 4; nếu thiếu DLL → im lặng).

- [x] **Step 4: Copy DLL vào output App** (target name `CopyCameraDll` với `AfterTargets=Build`; verify timestamp copy == source)

> **Verify tự động hóa của Task 3 (ngoài plan):** ffmpeg grab 1 frame: `ffmpeg -f dshow -video_size 1280x720 -pixel_format yuyv422 -framerate 30 -i "video=ReTran Camera" -frames:v 1 -pix_fmt rgb24 -f rawvideo out.raw` → exit 0, đúng 2 764 800 byte, pixel xanh đồng nhất RGB ≈ (22,208,90) so với (34,197,94) — lệch nhỏ do range conversion YUV↔RGB, chấp nhận được.

`ReTran.App.csproj` thêm target:

```xml
<Target Name="CopyCameraDll" AfterTargets="Build"
        Condition="Exists('$(MSBuildProjectDirectory)\..\ReTran.Camera\x64\$(Configuration)\ReTran.Camera.dll')">
  <Copy SourceFiles="..\ReTran.Camera\x64\$(Configuration)\ReTran.Camera.dll"
        DestinationFolder="$(OutDir)" />
</Target>
```

Run build → verify `ReTran.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\ReTran.Camera.dll` tồn tại.

- [ ] **Step 5: GATE manual — device thấy ở OBS + Discord + Zoom**

Chạy `ReTran.App.exe` (đăng ký chạy lúc mở). Kiểm tuần tự:
1. **OBS**: Sources → + → Video Capture Device → dropdown hiện **ReTran Camera** → thêm → thấy khung màu xanh `#22C55E`.
2. **Zoom**: Settings → Video → Camera dropdown → thấy + hiện màu.
3. **Discord**: Settings → Voice & Video → Video Device → thấy + hiện màu.
4. **App đóng hoàn toàn** (kill process) → device vẫn còn trong danh sách (skeleton chưa có heartbeat — màu cũ/tĩnh OK ở bước này).

Expected: cả 3 app thấy device.
**Nếu fail (per-user hive không được enumerate):** DỪNG task, báo user lựa chọn — (A) đăng ký HKLM cần admin 1 lần lúc mở app (UAC prompt), (B) bỏ acceptance Discord/Zoom. Không tự chọn.

- [ ] **Step 6: Commit**

```bash
git add "ReTran Project.slnx" ReTran.Camera/ ReTran.App/ReTran.App.csproj ReTran.App/Services/Camera/CameraRegistrar.cs ReTran.App/MainPage.xaml.cs ReTran.Tests/CameraRegistrarTests.cs
git commit -m "feat(camera): native DirectShow source filter skeleton + per-user registration"
```

---

### Task 4: Shared memory + CameraOutputService + đọc frame thật + vòng đời

**Files:**
- Create: `ReTran.App/Services/Camera/CameraProtocol.cs` (header layout + `CameraFrameWriter` + test-reader `CameraFrameReader`)
- Create: `ReTran.App/Services/Camera/ICameraOutput.cs`
- Create: `ReTran.App/Services/Camera/CameraOutputService.cs`
- Modify: `ReTran.App/Core/PipelineController.cs` (Submit ở `HandleFrameAnnotated`, Hold khi pipeline stop)
- Modify: `ReTran.App/MainPage.xaml.cs` (dispose service lúc app exit — cùng chỗ từng dispose feed)
- Modify: `ReTran.App/MauiProgram.cs` (DI `ICameraOutput`)
- Modify: `ReTran.Camera/src/filter.cpp` (đọc shared memory thật + heartbeat → black/hold/live)
- Modify: `ReTran.Camera/src/filter.cpp` thêm export test `ReTranCamera_ConvertBgraToYuy2`
- Test: `ReTran.Tests/CameraProtocolTests.cs`, `ReTran.Tests/CameraOutputServiceTests.cs`, sửa `ReTran.Tests/PipelineControllerTests.cs`

**Interfaces:**
- Consumes (Task 2): `PreviewState` (không đổi), `PipelineController.HandleFrameAnnotated`; (Task 3): DLL exports, CLSID.
- Produces: (cho UI/docs) service `public interface ICameraOutput : IDisposable { void Submit(long seq, int w, int h, byte[] jpeg); void Hold(); }`
  - Header layout (ghim, khớp C++ `#pragma pack(8)` → dùng explicit offsets):

```
off  u32  magic    = 0x4D414352   // "RCam" LE
 4   u32  version  = 1
 8   u32  width    = 1280
12   u32  height   = 720
16   u32  stride   = 5120         // width*4
20   u32  format   = 0            // 0 = BGRA32
24   u32  seq      // seqlock: lẻ = đang ghi, chẵn = ổn định
28   u32  flags    // bit0 = HOLD
32   u64  lastWriteTickMs          // Environment.TickCount64 lúc ghi gần nhất
40   u32  reserved0
44   u32  reserved1
48   byte pixels[height*stride]    // BGRA, tổng = 3,686,448
```

  - MMF tên `Local\ReTran.Camera.V1` (size cố định trên); heartbeat `Local\ReTran.Camera.Heartbeat` (4 byte, tạo lúc `Submit` đầu tiên, **chỉ writer giữ handle**).
  - Semantics filter: heartbeat không mở được → **đen**; flags HOLD → hiển thị frame cuối; còn lại → đọc seqlock (retry ≤4, fail → frame cuối đã lưu / đen).

- [ ] **Step 1: Viết failing tests**

`CameraProtocolTests`:
- `Writer_Publish_ReaderSeesHeaderAndPixels`: 2×2 BGRA màu đã biết (encode JPEG lossless-ish quality 100 trong test, decode lại để biết kỳ vọng) → `CameraOutputService.Submit` → `CameraFrameReader.Open` thấy magic/version/w/h/stride, seq ≥1, pixels khớp kỳ vọng từng byte.
- `Writer_Dispose_RemovesHeartbeat`: sau `Dispose()`, `OpenFileMappingW("Local\ReTran.Camera.Heartbeat")` trả 0 (native interop `[DllImport("kernel32")]` trong test).
- `Submit_AfterHold_SetsHoldFlag`: `Hold()` → header flags bit0 set, seq không đổi; `Submit` tiếp → flag tự clear + seq tăng (pipeline chạy lại).

`CameraOutputServiceTests` (thay `PipelineControllerTests` fake `ICameraOutput`):
- `FrameAnnotated_CallsCameraSubmit`: notification → `_camera.Submit(seq, w, h, Convert.FromBase64String(jpeg))`.
- `PipelineStopped_CallsCameraHold`: bắn `pipeline.state {running:false, reason:"…"}` → `Hold()` được gọi.

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` → FAIL (chưa có type).

- [ ] **Step 3: Implement C#**

- `CameraProtocol`: hằng số offsets; `CameraFrameWriter : IDisposable` — `MemoryMappedFile.CreateOrOpen(name, TotalSize)`, `Create(name, 4)` cho heartbeat (nội dung 1); `WriteFrame(byte[] bgra1280x720, bool hold)` — set `seq|=1` (volatile/`Interlocked`), memcpy pixels, ghi header (magic/…/lastWriteTick/flags), `seq=seq+1` (chẵn). Dispose → đóng cả 2 handle (không `Dispose` mapping object — để filter tự thấy mất heartbeat).
- `CameraFrameReader` (dùng cho test; public): mở mapping, `TryRead(out header, out byte[] pixels)` — kiểm seqlock như filter.
- `CameraOutputService : ICameraOutput` — lazy tạo writer ở `Submit` đầu; `Submit`: decode JPEG (System.Drawing `Image.FromStream` → `Bitmap` → LockBits BGRA) → scale/letterbox **fit 1280×720** (nhân tỉ lệ, pad đen nếu lệch tỉ lệ — ghim: **pad đen**, không crop) → `WriteFrame`. `Hold` → `WriteFrame(last, hold:true)` (giữ nguyên pixels). JPEG khác kích thước giữa 2 Submit → vẫn fit lại từ đầu.
- `PipelineController`: `_camera` field (optional `ICameraOutput? camera = null` — thêm cuối ctor); `HandleFrameAnnotated` sau khi publish preview → `_camera?.Submit(...)`; trong handler `pipeline.state` `running==false` (nơi hiện raise `PipelineStateChanged`) → `_camera?.Hold()`.
- `MauiProgram`: `AddSingleton<ICameraOutput, CameraOutputService>()`; `MainPage` exit path: dispose `ICameraOutput` (mẫu chỗ từng `_feedServer` — **Task 5 sẽ xóa feed**, chỗ này ghép vào thay thế luôn nhưng **chỉ sửa phần thêm dispose camera**, không xóa feed ở task này nếu Task 5 chưa chạy — commit đúng file).

- [ ] **Step 4: Implement native (filter.cpp + convert export)**

- `FillBuffer`: mở `OpenFileMappingW(L"Local\ReTran.Camera.Heartbeat")` mỗi ~500 ms (không giữ handle) → fail → memset đen. Còn sống → map frame section (mở 1 lần, giữ) → đọc seqlock (retry 4, `MemoryBarrier`) → `ConvertBgraToYuy2` (BT.601 limited: `Y=(66R+129G+25B+128)>>8+16`, `U=(-38R-74G+112B+128)>>8+128`, `V=(112R-94G-18B+128)>>8+128`) → ghi ra sample; fail seqlock → dùng buffer convert gần nhất (hoặc đen nếu chưa từng).
- Flags HOLD → vẫn hiện frame cuối (không phân biệt với live —内容 giữ nguyên).
- Export `ReTranCamera_ConvertBgraToYuy2(const unsigned char* bgra, unsigned char* yuy2, unsigned int pixelCount)` (để test C# P/Invoke).
- Build: `msbuild "ReTran Project.slnx" /p:Platform=x64`.

- [ ] **Step 5: Chạy verify PASS (C#) + round-trip native**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Thêm 1 test optional `CameraNativeInteropTests.ConvertBgraToYuy2_WhiteIs235_BlackIs16`: `if (!File.Exists(dll)) return;` (mẫu skip của `SidecarProcessTests`) → P/Invoke export, input pixel trắng/đen → Y = 235/16.

- [ ] **Step 6: Verify manual end-to-end**

Chạy app + OBS:
1. Start pipeline (window) → OBS "ReTran Camera" hiện khung thật + box + bản dịch; preview trong app giống hệt OBS.
2. Stop pipeline → cả 2 giữ khung cuối (không đen).
3. Start lại → khung mới.
4. Kill app → OBS **đen** trong ≤1 s; device vẫn trong danh sách; mở app lại → sống.

- [ ] **Step 7: Commit**

```bash
git add ReTran.App/Services/Camera/ ReTran.App/Core/PipelineController.cs ReTran.App/MainPage.xaml.cs ReTran.App/MauiProgram.cs ReTran.Camera/src/ ReTran.Tests/CameraProtocolTests.cs ReTran.Tests/CameraOutputServiceTests.cs ReTran.Tests/PipelineControllerTests.cs ReTran.Tests/CameraNativeInteropTests.cs
git commit -m "feat(camera): shared-memory frame protocol + service + real frames in filter"
```

---

### Task 5: Dọn hẳn HTTP feed (OBS browser source)

**Files:**
- Delete: `ReTran.App/Services/OverlayFeedServer.cs`, `ReTran.App/Services/IOverlayFeedServer.cs`, `ReTran.App/wwwroot/overlay/` (toàn bộ), `ReTran.Tests/OverlayFeedServerTests.cs`
- Modify: `ReTran.App/Services/Update/UpdateStopper.cs` (bỏ `IOverlayFeedServer` + bước 3)
- Modify: `ReTran.Tests/Update/UpdateStopperTests.cs` (bỏ feed fixtures/helpers)
- Modify: `ReTran.App/MainPage.xaml.cs`, `ReTran.App/MauiProgram.cs`, `ReTran.App/Components/Pages/Home.razor` (bỏ inject `FeedServer`, card "OBS", `ObsUrl`)
- Modify: `ReTran.App/Services/I18nStrings.cs` (bỏ key `home.label.obsUrl`)
- Modify: `ReTran.App/Components/Pages/Home.razor` thay card "OBS" bằng card thông tin **Camera ảo** (tên device + ghi chú "Tự chạy cùng pipeline", i18n vi/en)

**Interfaces:**
- Consumes: Task 2/4 (preview + camera đã thay thế đầu ra OBS).
- Produces: không — đây là tháo gỡ.

- [ ] **Step 1: Xóa + sửa, giữ các tham chiếu còn lại**

Grep gate: `grep -r "OverlayFeedServer\|IOverlayFeedServer\|17863\|/overlay" ReTran.App ReTran.Tests` → **0 kết quả** (trừ commit history). Lưu ý: chuỗi `"/overlay"` trong route Razor trang overlay cũng nằm trong `wwwroot/overlay` + routing — xóa luôn.

- [ ] **Step 2: Sửa UpdateStopper + test**

- `UpdateStopper`: bỏ param `IOverlayFeedServer feed`, bỏ bước (3); update XML doc (song ngữ, nêu feed đã bị lược bỏ theo spec camera).
- `UpdateStopperTests`: bỏ `AssertFeedDisposed`, ctor 3→2 deps.

- [ ] **Step 3: Chạy verify**

Run: `dotnet build "ReTran Project.slnx"` && `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` && grep gate ở Step 1.
Expected: xanh, 0 tham chiếu.

- [ ] **Step 4: Chạy tay** — app mở → không còn lắng nghe 17863 (`netstat -ano | findstr 17863` rỗng), card Camera ảo hiển thị.

- [ ] **Step 5: Commit**

```bash
git add -A ReTran.App ReTran.Tests
git commit -m "feat(app)!: remove HTTP overlay feed, camera card replaces OBS card"
```

---

### Task 6: Docs + đo performance + acceptance

**Files:**
- Modify: `Docs/performance.md` (mục mới "Camera ảo + Preview")
- Modify: `AGENTS.md` (OBS Export → camera ảo; Repository Layout thêm `ReTran.Camera/`; Tech Stack thêm C++)
- Modify: `Docs/superpowers/specs/2026-10-17-virtual-camera-preview-design.md` (đánh dấu Accepted nếu user đồng ý)

**Interfaces:** — (docs only)

- [ ] **Step 1: Đo với game thật (window source, model đã cache)**

Chạy app + pipeline ≥2 phút, game/vid đang chạy, ghi số: CPU% trung bình app + core + python (Task Manager/`typeperf`), annotator ms/khung (log tạm), IPC MB/s (log tạm byte `frame.annotated`), camera filter CPU% trong OBS, preview mượt. So budget §Global: < 8% CPU tổng, ≤10 ms/khung, ≤1.5 MB/s.

- [ ] **Step 2: Ghi `Docs/performance.md`** — bảng số đo + kết luận pass/fail (fail → ghi tradeoff cần duyệt).

- [ ] **Step 3: Cập nhật `AGENTS.md`** — mục OBS Export: browser source bị gỡ, camera ảo là đường ra; Repository Layout + Tech Stack.

- [ ] **Step 4: Chạy acceptance checklist của spec §8** (7 mục) — tự check 1,3(với OBS),5,6; nhờ user check 1 (Zoom/Discord), 2, 4 (layout nhìn OK).

- [ ] **Step 5: Commit**

```bash
git add Docs/performance.md AGENTS.md Docs/superpowers/specs/2026-10-17-virtual-camera-preview-design.md
git commit -m "docs: record camera/preview performance and update AGENTS"
```
