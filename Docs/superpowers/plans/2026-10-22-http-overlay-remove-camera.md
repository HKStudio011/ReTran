# HTTP Overlay + Xoá Camera ảo — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** OBS nhận overlay dịch qua HTTP (Browser Source), app không còn xuất camera ảo.

**Architecture:** App chạy `HttpListener` loopback phục vụ một trang trong suốt `/overlay` + SSE `/events` phát box JSON từ `OverlayState` (cùng nguồn với overlay cửa sổ). Sau khi overlay chạy được, xoá toàn bộ camera ảo (writer shm, DI, UI, project native, tests).

**Tech Stack:** C# .NET 10 (MAUI Blazor Hybrid), `HttpListener`, SSE; xUnit v3 (MTP runner).

**Spec:** `Docs/superpowers/specs/2026-10-22-ocr-models-shm-http-overlay-design.md` (mục 6.3, 6.4, D1/D6)

## Global Constraints

- Chỉ bind `127.0.0.1`; port mặc định **17863**, bận thì +1 (tối đa 10 lần).
- File test chạy bằng: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` — runner MTP **không lọc theo class**, chạy full suite.
- Doc comment C# song ngữ (Anh trước, Việt sau) cho mọi API public/protected.
- Không đụng preview (`Local\ReTran.Preview.V1`, `frame.annotated`) và Core annotate loop.
- Không tự gỡ đăng ký DShow trong app — chỉ ghi hướng dẫn unregister một lần.

## Review Focus

- Port 17863 đang bị app khác chiếm → server phải tự sang port kế và UI hiển thị **port thật** (Task 2 có test UI lấy `server.Port`).
- Chưa có frame nào được publish → `/events` phải gửi ngay snapshot rỗng, không treo client (Task 1 test `Events_SendsInitialSnapshot`).
- Client ngắt giữa SSE (OBS tắt source) → server không được chết hay rò subscription (Task 1 test `Events_ClientDisconnects_ServerSurvives`).
- Trang overlay khi chưa có box → trong suốt hoàn toàn, không nền (Task 2 kiểm tra CSS `background: transparent` + JS không tạo phần tử thừa).
- Xoá camera không sót tham chiếu (`ReTran.Camera`, `ICameraOutput`, `CameraRegistrar`) → grep gate ở Task 3.

---

### Task 1: OverlayFeedServer (HTTP + SSE)

**Files:**
- Create: `ReTran.App/Services/OverlayFeed/OverlayFeedServer.cs`
- Test: `ReTran.Tests/Overlay/OverlayFeedServerTests.cs`

**Interfaces:**
- Consumes: `OverlayState` (`Latest`, `Changed`, `Publish(OverlayFrame)`), `OverlayFrame(FrameWidth, FrameHeight, TimestampUtc, Boxes)`, `OverlayBox(X, Y, W, H, Text, Translated)` — tất cả đã có trong `ReTran_App.Core`.
- Produces:
  ```csharp
  public sealed class OverlayFeedServer : IAsyncDisposable
  {
      public OverlayFeedServer(OverlayState state, int port = 17863, TimeSpan? heartbeat = null);
      public int Port { get; }
      public Task StartAsync(CancellationToken ct);
      public ValueTask DisposeAsync();
  }
  ```
  Endpoint: `GET /overlay` (HTML), `GET /events` (SSE `data: {"seq":n,"boxes":[{"x":..,"y":..,"w":..,"h":..,"text":"..","translated":".."}]}`), `:ping` mỗi `heartbeat` (mặc định 15 s).

- [ ] **Step 1: Viết test fail**

`ReTran.Tests/Overlay/OverlayFeedServerTests.cs` — helper `FreePort()` (TcpListener cổng 0 → lấy port → stop), `HttpClient`:

```csharp
[Fact] public async Task Start_ServesOverlayPage()
// GET /overlay → 200, Content-Type chứa "text/html"

[Fact] public async Task Events_SendsInitialSnapshot()
// GET /events (ResponseHeadersRead) → dòng data: đầu tiên parse được, boxes == []

[Fact] public async Task Events_PushesPublishedBoxes()
// đọc snapshot, rồi state.Publish(new OverlayFrame(1920,1080,UtcNow,
//   [new OverlayBox(0.5,0.25,0.2,0.1,"A","B")]))
// → data: kế tiếp: seq > 0, boxes[0] có x=0.5 y=0.25 w=0.2 h=0.1 text="A" translated="B"

[Fact] public async Task PortBusy_FallsBackToNextPort()
// giữ 1 cổng bằng TcpListener, tạo server với cổng đó → server.Port > cổng đó

[Fact] public async Task Events_ClientDisconnects_ServerSurvives()
// mở /events rồi dispose response; Publish thêm 3 frame; GET /overlay vẫn 200

[Fact] public async Task Events_SendsHeartbeat()
// server với heartbeat=100ms → trong 3s phải đọc được ít nhất 1 dòng ":ping"
```

- [ ] **Step 2: Chạy suite xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: FAIL — `OverlayFeedServer` chưa tồn tại (compile error).

- [ ] **Step 3: Cài đặt**

`HttpListener` prefix `http://127.0.0.1:{port}/`; vòng lặp `GetContextAsync`; SSE: ghi snapshot từ `state.Latest` (hoặc frame rỗng khi null), subscribe `Changed` → serialize vào cùng writer có lock, bỏ qua write lỗi và unsubscribe client đó; heartbeat bằng `PeriodicTimer` ghi `:ping\n\n`. Port bận (`HttpListenerException` mã 32) → thử port+1 tối đa 10 lần. Trang `/overlay` trả HTML tối thiểu (Task 2 thay bằng file thật).

- [ ] **Step 4: Chạy suite xác nhận pass**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: PASS toàn bộ (329 cũ + 6 mới).

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/OverlayFeed/OverlayFeedServer.cs ReTran.Tests/Overlay/OverlayFeedServerTests.cs
git commit -m "feat(app): add loopback overlay feed server (HTTP + SSE)"
```

---

### Task 2: Trang /overlay + nút Copy URL trong app

**Files:**
- Create: `ReTran.App/wwwroot/overlay-feed/overlay.html`, `overlay.js`, `overlay.css`
- Modify: `ReTran.App/Services/OverlayFeed/OverlayFeedServer.cs` (phục vụ file tĩnh), `ReTran.App/MauiProgram.cs` (DI singleton + start), `ReTran.App/Components/Pages/Home.razor` (card + nút), `ReTran.App/Services/I18nStrings.cs` (`deck.overlay.*` vi/en), `ReTran.App/Components/Shared/SettingsPanel.razor` nếu cần
- Test: `ReTran.Tests/Overlay/OverlayFeedServerTests.cs` (thêm case)

**Interfaces:**
- Produces: `GET /overlay/overlay.js`, `GET /overlay/overlay.css`; `OverlayFeedServer.Port` dùng để dựng URL; DI: `builder.Services.AddSingleton<OverlayState>(); builder.Services.AddSingleton<OverlayFeedServer>();` và start idempotent trong `Home.OnInitializedAsync` (`await Feed.StartAsync(...)` bọc try/catch).
- Trang JS: `new EventSource('/events')`, mỗi event dựng lại các div `.box` với `left/top/width/height` theo %, `.tick` 4 góc teal `#4FD8C4`, `.pill` nền `rgba(5,12,14,.86)`, chữ `#F3EEDD`, `font-size: calc(<h>% ...)` suy từ chiều cao box; nền `body { background: transparent; }`.

- [ ] **Step 1: Viết test fail**

```csharp
[Fact] public async Task Overlay_Assets_AreServed()
// GET /overlay/overlay.js → 200 chứa "EventSource"; GET /overlay/overlay.css → 200
```

- [ ] **Step 2: Chạy suite xác nhận fail**
- [ ] **Step 3: Viết 3 file tĩnh + wiring DI + card UI**

Card thay card Camera: tiêu đề `deck.overlay.title`, URL `http://127.0.0.1:{Port}/overlay`, nút Copy dùng `Js.CopyTextAsync`, ghi chú 2 dòng hướng dẫn OBS (Browser Source 1920×1080). Xoá `CopyCameraAsync` khỏi Home (Task 3 dọn nốt phần còn lại).

- [ ] **Step 4: Chạy suite + thử tay**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`; mở app → copy URL → dán vào OBS Browser Source 1920×1080, chạy pipeline ở chế độ subtitle → box khớp text game (sai số ≲2%).
- [ ] **Step 5: Commit**

```bash
git add ReTran.App/wwwroot/overlay-feed ReTran.App/Services/OverlayFeed ReTran.App/MauiProgram.cs ReTran.App/Components/Pages/Home.razor ReTran.App/Services/I18nStrings.cs ReTran.Tests/Overlay/OverlayFeedServerTests.cs
git commit -m "feat(app): serve transparent OBS overlay page with copy-url card"
```

---

### Task 3: Xoá hẳn camera ảo

**Files:**
- Delete: `ReTran.Camera/` (cả thư mục), `ReTran.App/Services/Camera/` (cả thư mục), `ReTran.Tests/Camera/` (4 file), các test camera trong `ReTran.Tests/Overlay/OverlayWindowServiceTests.cs`
- Modify: `ReTran Project.slnx` (bỏ mục `ReTran.Camera`), `ReTran.App/ReTran.App.csproj` (bỏ target `CopyCameraDll`), `ReTran.App/MainPage.xaml.cs` (bỏ using + `new CameraRegistrar().EnsureRegistered()`), `ReTran.App/MauiProgram.cs` (bỏ `ICameraOutput`), `ReTran.App/Core/PipelineController.cs` (bỏ `_camera`, tham số ctor, block submit, `Hold()`, using), `ReTran.App/Components/Pages/Home.razor` (bỏ mọi dòng còn lại của camera), `ReTran.App/Services/I18nStrings.cs` (bỏ string camera), `ReTran.Tests/PipelineControllerTests.cs` + test nào còn tham chiếu

**Interfaces:**
- Consumes: Task 2 đã có card overlay thay thế.
- Produces: `PipelineController` không còn tham số `ICameraOutput`; app không publish `Local\ReTran.Camera.V1`.

- [ ] **Step 1: Liệt kê tham chiếu**

Run: `grep -rn "Camera\|camera" ReTran.App ReTran.Tests ReTran.Core --include=*.cs --include=*.razor --include=*.csproj`
Expected: danh sách để xoá (chỉ còn string UI sẽ xoá trong bước 2; Core không có).

- [ ] **Step 2: Xoá code + test, sửa ctor PipelineController và các test gọi nó**
- [ ] **Step 3: Chạy full suite**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: PASS (số test giảm do xoá test camera).

- [ ] **Step 4: Grep gate (A8)**

Run: `grep -rn "ReTran.Camera\|CameraRegistrar\|ReTran.Camera.V1\|ICameraOutput" ReTran.App ReTran.Tests ReTran.Core ReTran.Project.slnx`
Expected: **0 kết quả**.

- [ ] **Step 5: Ghi hướng dẫn dọn máy + commit**

Thêm mục ngắn vào `Docs/` (release note hoặc README): chạy `regsvr32 /u "<đường dẫn cũ>\ReTran.Camera.dll"` **trước khi** xoá DLL build cũ.

```bash
git add -A
git commit -m "chore: remove virtual camera (code, native project, tests, ui)"
```
