# Plan: Ngưỡng gộp dòng chỉnh được + OBS theo một công tắc + menu nổi (gộp Plan 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đưa 3 ngưỡng gộp OCR (`LineGrouper`) ra sheet Settings dưới dạng 3 ô số, làm lớp phủ OBS chạy/ẩn theo đúng công tắc `Lớp phủ` (và xóa sạch khi Stop pipeline), rồi chạy tiếp toàn bộ menu nổi desktop của Plan 2.

**Architecture:** Ngưỡng đi theo đường cấu hình đã có cho OCR: `AppSettings` → `core.start` (`ocr.merge*`) → `PipelineHost.ParseStart` validate → `OcrPipeline` → `LineGrouper.Group(spots, thresholds)`. Phía hiển thị: một singleton `OverlayVisibility` (= `OverlayOnGame && pipeline.IsRunning`, edge-triggered) gates `OverlayFeedServer`, và `OverlayState.Clear()` làm snapshot cho client mới kết nối là rỗng sau khi dừng. Menu nổi giữ nguyên thiết kế Plan 2 (window thứ hai trong `Win32OverlayHost`, layout thuần + renderer GDI+ + service nối intent).

**Tech Stack:** C# .NET 10 (`ReTran.Core` net10.0-windows, `ReTran.App` MAUI Blazor Hybrid), xUnit v3 (MTP runner), System.Text.Json.Nodes, GDI+ cho menu, không dependency mới.

**Spec:** `Docs/superpowers/specs/2026-11-03-merge-thresholds-and-obs-visibility-design.md` (phần mới, user duyệt 2026-11-04) **và** `Docs/superpowers/specs/2026-11-01-floating-menu-tray-design.md` (menu nổi + tray, đã duyệt). Phần menu nổi thi công theo plan `Docs/superpowers/plans/2026-11-02-floating-menu.md` — Task 16–23 của plan này trỏ vào đó, không chép lại. **Gộp 2026-11-06:** thêm phần **setup wizard lần đầu + update** (spec `Docs/superpowers/specs/2026-11-03-first-run-setup-wizard-design.md`, plan `Docs/superpowers/plans/2026-11-04-first-run-setup-wizard.md`) — setup Tasks 1–6 đã xong trên `master`, phần còn lại là **Task 10–15** của plan này. Plan có 24 task; branch đã rebase lên `master`@`84b90ff`, baseline **525 passed / 0 failed**.

## Global Constraints

- **Không `git add -A`.** Repo có ~90 file WIP của user. Mỗi commit chỉ `git add` đúng các file của task. Không push. Branch `master`.
- **Không đụng `ReTran.App/wwwroot/build/`** bằng tay (output). Khi sửa CSS/Razor thì `npm run build` trong `ReTran.App/vite-project/` để xem, nhưng **không commit** output.
- Build chuẩn: `dotnet build "ReTran Project.slnx"` → 0 error, 34 warning `xUnit1051` có sẵn.
- Test chuẩn: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<TênĐầyĐủ>"`. Test Core mang prefix `RetranCore.Tests.`; test App ở **global namespace** (tên trần). Khi nghi ngờ: `--list-tests`. **Baseline: 454 passed / 0 failed (2026-11-03); 525 passed / 0 failed sau rebase lên `master`@`84b90ff` (2026-11-06) — con số 525 là sàn cho mọi task từ Task 6 trở đi.**
- `ReTran.App` **không** tham chiếu `ReTran.Core` (csproj chỉ có `ReTran.Updater`); `ReTran.Tests` tham chiếu cả hai. ⇒ App không được `using ReTran.Core.Ocr`; giá trị mặc định ngưỡng lặp lại ở App là hằng số riêng, và có test so khớp hai phía (Task 6).
- Wire `core.start`: key ngưỡng là **optional**; thiếu/`null` ⇒ default = hành vi hiện tại. Sai kiểu hoặc ngoài dải ⇒ `RpcMethodException` code `RpcErrorCodes.InvalidParams` (-32602), message nêu tên key. Không start nửa vời.
- Dải ngưỡng (Spec K2, dùng y hệt ở App và Core): gap `0.1..2.0`, height `1.0..3.0`, overlap `0.0..1.0`. Default: `0.6`, `1.35`, `0.5`.
- i18n: key mới phải vào **cả** block `Vi` lẫn `En` trong `ReTran.App/Services/I18nStrings.cs` (giữ chân bằng `I18nServiceTests.EveryKey_Exists_In_Both_Languages`). Trước khi sửa file, `git status --short` + đọc lại file (nhiều task chạm cùng file).
- Doc comment C# public API song ngữ Anh → Việt theo `AGENTS.md`; `internal` chỉ doc khi hành vi không hiển nhiên.
- Không sửa `ReTran.App/wwwroot/overlay-feed/*` (Spec §3: `boxes: []` đã là "xóa sạch").
- Ngân sách hiệu năng `AGENTS.md`: không thread/process mới; UI/overlay redraw ≤ 10–15 fps; không traffic SSE vô ích khi overlay tắt.

## Review Focus

Sáu failure mode spec ngụ ý nhưng không task nào tự phủ — mỗi dòng đã ghim test vào task sở hữu:

1. **`settings.json` cũ không có 3 key ngưỡng** ⇒ ngưỡng phải đúng bằng `0.6 / 1.35 / 0.5`, không phải `0` hay `1` (STJ + default tham số record dễ biến thành 0). → Task 1 `MergeThresholdParams_Absent_DefaultsCurrent`, Task 2 `Group_OneArgOverload_EqualsDefaultThresholds`.
2. **`core.start` tự tay gửi ngưỡng sai kiểu/ ngoài dải** ⇒ `-32602` nêu đúng tên key và **không** tạo session nửa vời. → Task 3 (`Start_MergeThreshold*_*` cases).
3. **OBS Browser Source kết nối *sau* khi Stop/ẩn** ⇒ snapshot phải rỗng, không dựng lại chữ của phiên cũ (`_state.Latest` vẫn còn nếu không Clear). → Task 8 `Clear_SnapshotIsBoxesEmpty`, Task 9 `ClientConnecting_WhileHidden_GetsEmptySnapshot`.
4. **Một lần dừng nổ hai event** (`PipelineStateChanged` + `StateChanged`, xem `PipelineController.cs:627-628` và `:824-825`) ⇒ feed phải gửi **đúng một** payload `boxes: []`. → Task 9 `Hidden_Edge_TriggersOnce_OnBothStopPaths`.
5. **`OverlayOnGame` default là `false`** ⇒ sau thay đổi này, ai chưa từng bật `Lớp phủ` sẽ thấy OBS **trắng hoàn toàn** — đúng theo quyết định U3 (một công tắc) nhưng là thay đổi nhìn thấy được, phải nói rõ trên thẻ OBS của trang Home, không để người dùng tưởng OBS hỏng. → Task 9 (key i18n + chữ trên thẻ OBS) + checklist smoke Task 24.
6. **Ô số nhập rỗng / dùng dấu phẩy thập phân / ngoài dải** ⇒ không `Save`, không gửi `core.start`, hiện lỗi đỏ cạnh nút Lưu (sheet đang hiển thị theo culture). → Task 6 (`OcrMergeThresholdsTests`), Task 7 (`SaveOcrParamsAsync` chặn trước khi save).
7. **Sai ánh xạ phiên bản driver NVIDIA → chọn wheel CUDA cao hơn máy chạy nổi** (`SystemCheck.cs:401` suy số trăm từ DDI major, thật nằm ở segment branch thứ 3) ⇒ wizard cài cu130 cho máy chỉ đủ cu126, theo hướng **không** fail-safe; fixture tổng hợp `31.0.15.…` đang ghim chặt format sai nên suite không phát hiện. → Task 10 (fixture WMI thật `32.0.15.6094→cu126`, `32.0.15.8163`→CPU, `32.0.16.2114`→cu130).
8. **Wizard tự tay sửa `pyproject.toml` trên máy user** ⇒ `uv.lock` ship kèm bị vô hiệu (mọi máy sạch resolve lại từ dải `>=`, lệch bộ đã đo perf), và một bản sửa sai (CPU→GPU kiểu incremental) cho ra pyproject **không còn** dependency GPU lẫn index paddle. → Amendment A2: một `pyproject.toml` chuẩn duy nhất + `dependency-groups` `gpu`/`cpu`, `uv sync --group …` chọn flavor, **không** sửa file.

---

## Amendment A2 (2026-11-07) — một target CUDA, `dependency-groups`, không sửa `pyproject.toml`

User chốt, dựa trên số đo (ledger `2026-11-04-merge-thresholds/progress.md`, mục "PaddlePaddle GPU dependencies
audit" + "Measured: what the GPU OCR path actually loads"). Phần này **thay thế** mọi mục trong plan/spec nói về
"thang 3 nấc wheel cu130/cu129/cu126" và "ghi đè `pyproject.toml` theo flavor":

- **Bằng chứng:** index paddle không có `paddlepaddle-gpu` 3.4.0 cho cp313/win_amd64 ở nấc `cu126` (tối đa 3.3.1)
  ⇒ nấc đó không cài được với pin `==3.4.0`; wheel `cu129` chứa fatbin **sm_75/80/86/89/120** (đo bằng
  `cuobjdump --list-elf --list-ptx phi.dll`) ⇒ GTX 1650/1660 (CC 7.5) chạy được, Pascal/Maxwell/Volta thì không;
  OCR det+rec thật chỉ cần **cuDNN + cuBLAS(+Lt) + nvrtc** (probe với PATH bỏ CUDA toolkit: `HELLO RETRAN 0.992`,
  `gpu:0`), `cufft/cusolver/cusparse` không được nạp ⇒ thêm 5 gói paddle khai báo = +955 MB tải về, vô ích;
  `paddlepaddle` (CPU) `3.4.0` chỉ có trên index paddle `stable/cpu/` (PyPI tối đa 3.3.1).
- **`ReTran.OCR/pyproject.toml` (một file chuẩn, app không bao giờ sửa):**
  - `[project].dependencies` = phần không phụ thuộc flavor: `paddleocr==3.7.0`, `paddlex`, `shared-memory`,
    `pillow`, `numpy`.
  - `[dependency-groups]`: `gpu = ["paddlepaddle-gpu==3.4.0", "nvidia-cudnn-cu12==9.9.0.52",
    "nvidia-cublas-cu12==12.9.0.13"]` (cublas ghim đúng bản paddle khai báo, thay cho 12.9.2.10 suy ra từ cudnn),
    `cpu = ["paddlepaddle==3.4.0"]`.
  - `[tool.uv]`: `conflicts = [[{ group = "gpu" }, { group = "cpu" }]]`, `default-groups = ["dev", "gpu"]`
    (để `uv sync` / `uv run pytest` của dev giữ nguyên hành vi theo AGENTS.md).
  - hai `[[tool.uv.index]]` `explicit = true`: `paddle-cu129` = `…/stable/cu129/`, `paddle-cpu` = `…/stable/cpu/`;
    `[tool.uv.sources]`: `paddlepaddle-gpu → paddle-cu129`, `paddlepaddle → paddle-cpu`.
  - `uv lock` một lần, **commit `ReTran.OCR/uv.lock`** (93 packages, đo 2026-11-07) và ship nó:
    thêm Content item `..\ReTran.OCR\uv.lock` (`CopyToOutputDirectory=PreserveNewest`) vào
    `ReTran.App/ReTran.App.csproj` (cụm item `ReTran.OCR`, dòng ~92-95).
- **`UvSyncRunner`:** `SidecarFlavor` còn **`Gpu` | `Cpu`**. argv thật:
  `uv sync --no-default-groups --group gpu|cpu --frozen` (`--frozen` = dùng đúng lock ship kèm, không re-resolve).
  **Xoá** máy chế rewrite/pyproject-head: `ApplyFlavorToPyproject`, `RewritePyproject`, `VariantPyprojectFor`,
  `CanonicalizeGpuPyproject`, `IsPyprojectVariantOfHead`, `ReadPyprojectCurrent`, `ReadPyprojectHead` + các test
  of chúng. Giữ phát hiện flavor theo **R55** (`uv pip show`, exit code), map `paddlepaddle-gpu`→`Gpu`,
  `paddlepaddle`→`Cpu`.
- **`SystemCheck`:** một nấc GPU duy nhất — **driver ≥ 576.02** (sàn CUDA 12.9 trên Windows, `57_602`) → GPU
  (`CudaWheel = "cu129"`); còn lại CPU. `MinDriverForCu130`/`MinDriverForCu126` + nhãn `cu130`/`cu126` bị xoá.
  `MinFreeBytesCpu`: 5 GB → **2 GB** (đo: wheel paddle CPU 105,5 MB). `FormatMinDriver()` phải in ra **576.02**
  (notice cũ đangquote 560.76). Fixture cũ mong `cu126`/`cu130` (545.00, 560.76, 570.00, 580.00,
  `32.0.15.6094`) ⇒ nay mong **CPU + notice** (hoặc `cu129` nếu driver ≥ 576.02).
  **Cửa compute capability KHÔNG nằm ở `SystemCheck`**: WMI `Win32_VideoController` không lộ CC, và suy CC từ
  tên card là bịa số liệu (vi phạm ràng buộc "không bịa hành vi"). Số đo thật duy nhất có được là từ chính
  sidecar sau khi cài (`paddle.device.get_device_capability()`, đo trên máy này = `(12, 0)`), nên rule
  **CC ≥ 7.5** (fatbin trong wheel cu129: sm_75/80/86/89/120) nằm ở **bước xác minh sidecar của Setup
  (Task 12)**: nếu GPU không chạy được model, Setup báo "card không được bản GPU hỗ trợ" và cho user chọn lại
  sang bản CPU (chạy lại Setup). `SystemCheck` giữ seam `osArchitecture` (x64) như cũ, không thêm gate giả.
  Notice (chữ tiếng Việt trong modal, không chặn): driver < 576.02 →
  "Driver NVIDIA … cũ hơn 576.02 — Setup sẽ cài bản CPU. Cập nhật driver rồi chạy lại Setup để dùng GPU.";
  Task 12 thêm notice khi venv đang mang flavor khác flavor sẽ cài (nêu flavour hiện tại/flavour mới + sẽ tải lại).
- **Cửa phiên bản CPython (R59, đo 2026-11-07 qua Task 11b):** `requires-python = "==3.13.*"` (KHÔNG dùng
  `>=3.13`). Với `UV_PYTHON_PREFERENCE=only-managed`, uv 0.13.7 chọn managed **mới nhất** thoả spec — trên máy dev
  là **3.14.9** — rồi **XOÁ `ReTran.OCR\.venv` hiện có** (`.venv\Lib\site-packages\paddle.libs\*.dll` là bản
  cp313-only ⇒ `uv sync` exit 2 vì mọi wheel paddle chỉ có ABI cp313). Trên worktree, `.venv` là junction nên hành
  vi đó **hủy luôn venv GPU của checkout chính** (đã xảy ra, đã khôi phục bằng `--python 3.13`). Vì vậy version
  Python phải ghim cứng trong `pyproject.toml` (một nguồn sự thật, đã ship kèm), không truyền `--python` từ code,
  và `uv sync` trên máy sạch sẽ **tự tải** CPython 3.13 managed từ python-build-standalone (đã đo:
  `cpython-install-managed-3.13.18-windows-x86_64-msvc`). Hệ quả cho wizard: một lần sync fail có thể để lại
  trạng thái **không còn venv**, nên bước retry của Setup phải là sync lại toàn bộ chứ không phải "sửa tiếp".

- **Sàn disk (§10 spec):** GPU **8 GB** (đo sau re-lock Task 11b/11d: venv **3,73 GB** = paddle ~1.73 GB +
  cudnn ~1.06 GB + cublas ~0.74 GB; lock ship kèm **không** cài nvrtc — OCR GPU thật đo chạy được không có nó;
  tải về: wheel GPU 869 MB + cudnn 768 MB + cublas 553 MB), CPU **2 GB** (wheel paddle CPU **105,5 MB**, venv
  tạm đo **0,67 GB**).
- **Test flavor CPU mà không phá `.venv` dev:** đặt `UV_PROJECT_ENVIRONMENT=<venv tạm>` khi test (worktree dùng
  junction `.venv` vào checkout chính — tuyệt đối không `uv sync --group cpu` vào `.venv` thật).

---

### Task 1: `AppSettings` + 10 tham số mới (thay thế Task 1 của Plan 2)

**Files:**
- Modify: `ReTran.App/Services/AppSettingsService.cs` (record `AppSettings` :33-41, thêm 10 tham số cuối sau `OcrMergeLines`)
- Test: `ReTran.Tests/AppSettingsServiceTests.cs` (mở rộng)

**Interfaces:**
- Consumes: không.
- Produces: record 21 tham số. Bảy tham số menu/nguồn **đúng như Plan 2 Task 1** (`bool FloatingMenuOn = true`, `bool FloatingMenuCollapsed = false`, `int? FloatingMenuX = null`, `int? FloatingMenuY = null`, `bool SourceUseWindow = false`, `int SourceMonitor = 0`, `string? SourceHwnd = null`) và ba ngưỡng:
  ```csharp
  double OcrMergeGap = 0.6,
  double OcrMergeHeight = 1.35,
  double OcrMergeOverlap = 0.5
  ```
  Task 5 đọc `settings.OcrMergeGap/Height/Overlap`; Task 16 (Plan 2 Task 2) và Task 22 (Plan 2 Task 8) đọc bộ bảy menu/nguồn. **Đây là lần duy nhất chữ ký record bị đổi** — không task nào sau đó được thêm tham số vào `AppSettings`.

- [ ] **Step 1: Viết test failing** — thêm 4 test vào class có sẵn (theo pattern `OcrMergeLines_Absent_DefaultsTrue:167`):
  - `MenuAndSourceParams_RoundTrip` + `MenuAndSourceParams_Absent_DefaultsSafe`: chép nguyên văn hai test trong Plan 2 Task 1 Step 1.
  - `MergeThresholdParams_RoundTrip`: `AppSettings.Default with { OcrMergeGap = 0.25, OcrMergeHeight = 2.0, OcrMergeOverlap = 0.0 }` → Save/Load ra đúng ba giá trị (đặc biệt `0.0` phải là `0.0`, không thành default).
  - `MergeThresholdParams_Absent_DefaultsCurrent`: file `{"provider":"google","src":"auto","dst":"vi"}` → `Assert.Equal(0.6, loaded.OcrMergeGap)`, `Assert.Equal(1.35, loaded.OcrMergeHeight)`, `Assert.Equal(0.5, loaded.OcrMergeOverlap)` (so sánh exact — đây là default của chính record, không qua phép tính nào).
- [ ] **Step 2: Chạy test — xác nhận fail** (`AppSettingsServiceTests`, compile error).
- [ ] **Step 3: Implement** — thêm 10 tham số cuối theo thứ tự trên, mỗi tham số một `<param>` song ngữ; riêng ba ngưỡng ghi rõ dải hợp lệ và "gửi trong `core.start` dưới dạng `ocr.mergeGapRatio`…". Cập nhật `<summary>` của record nếu nó liệt kê nhóm tham số.
- [ ] **Step 4: Chạy test — pass** + `dotnet build "ReTran Project.slnx"` 0 error.
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.App/Services/AppSettingsService.cs" "ReTran.Tests/AppSettingsServiceTests.cs"
  git commit -m "feat(settings): persist floating menu state, capture source, and merge-line thresholds"
  ```

---

### Task 2: Core — `MergeThresholds` + overload `LineGrouper.Group(spots, thresholds)`

**Files:**
- Create: `ReTran.Core/Ocr/MergeThresholds.cs`
- Modify: `ReTran.Core/Ocr/LineGrouper.cs` (`Group:42`, `CanMerge:146-156`)
- Test: `ReTran.Tests/LineGrouperTests.cs` (mở rộng; namespace `RetranCore.Tests`, đã có helper `Spot(x,y,w,h,text)` và helper đọc `paragraph`)

**Interfaces:**
- Consumes: các const có sẵn `LineGrouper.MaxGapRatio/MaxHeightRatio/MinOverlapRatio` (giữ nguyên, không rename — Spec §3).
- Produces (namespace `ReTran.Core.Ocr`):
  ```csharp
  public readonly record struct MergeThresholds(double GapRatio, double HeightRatio, double MinOverlapRatio)
  {
      public static MergeThresholds Default { get; }   // = (MaxGapRatio, MaxHeightRatio, MinOverlapRatio)
      public bool IsValid();                          // dải K2: 0.1..2.0 / 1.0..3.0 / 0..1
  }
  ```
  vẫn là `public static class LineGrouper` (không `partial`), thêm overload:
  ```csharp
  public static JsonArray Group(JsonArray spots);                                  // như cũ == Default
  public static JsonArray Group(JsonArray spots, MergeThresholds thresholds);      // mới
  ```
  `CanMerge` đổi thành `private static bool CanMerge(Box a, Box b, MergeThresholds t)`; mọi chỗ dùng const trong `CanMerge` đổi sang `t.GapRatio/HeightRatio/MinOverlapRatio`. Task 3 gọi overload 2 tham số.

- [ ] **Step 1: Viết test failing** — thêm vào `LineGrouperTests` (dụng helper `Spot` pixel có sẵn, và đọc kết quả qua `Group(spots, thresholds)`):
  - `DefaultThresholds_MatchConsts`: `MergeThresholds.Default == new MergeThresholds(0.6, 1.35, 0.5)`.
  - `Group_OneArgOverload_EqualsDefaultThresholds`: cùng một input (2 dòng gap = 14 px, cao 24) cho cả hai overload ⇒ output giống hệt nhau (1 khối gộp); và input gap = 20 px ⇒ **2 spot riêng** ở cả hai overload (chứng minh default = 0.6×24 = 14.4).
  - `Group_GapRatio2_MergesLinesConstsWouldSplit`: 2 dòng cùng cột, gap 30 px, cao 24 → `Group(spots, new MergeThresholds(2.0, 1.35, 0.5))` ⇒ 1 block `paragraph: true`.
  - `Group_HeightRatio3_MergesBigWithSmall` / `Group_HeightRatio1_KeepsThemApart`: cao 24 và 40 (ratio 1.67) → merge với `1.0..3.0` case đầu, không merge với `height = 1.0`.
  - `Group_OverlapRatio0_MergesSideBySideColumns` / `Group_OverlapRatio0p9_KeepsColumnsApart`: hai dòng cùng hàng, không chồng nhau (case đầu ⇒ 1 block; case sau ⇒ 2 spot).
  - `IsValid_Ranges`: `[Theory]` — `(0.6,1.35,0.5)=true`, `(0.09,1.35,0.5)=false`, `(2.01,…) =false`, `(0.6,0.99,0.5)=false`, `(0.6,1.35,1.01)=false`, `(0.6,1.35,0.0)=true`.
- [ ] **Step 2: Chạy test — xác nhận fail** (`RetranCore.Tests.LineGrouperTests`).
- [ ] **Step 3: Implement** — `MergeThresholds.cs` theo interface trên; `Default` khởi tạo từ ba const; `IsValid` là pattern `v is >= lo and <= hi`. Trong `LineGrouper`: giữ overload 1 tham số delegate sang overload 2 với `MergeThresholds.Default`; threading `thresholds` qua vòng scan (`CanMerge(g[^1].Box, line.Box, thresholds)`, `LineGrouper.cs:79`).
- [ ] **Step 4: Chạy test — pass** (cả class cũ + mới).
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.Core/Ocr/MergeThresholds.cs" "ReTran.Core/Ocr/LineGrouper.cs" "ReTran.Tests/LineGrouperTests.cs"
  git commit -m "feat(core): MergeThresholds struct + configurable LineGrouper merge thresholds"
  ```

---

### Task 3: `OcrPipeline` + `core.start ocr.merge*` parse/validate

**Files:**
- Modify: `ReTran.Core/Ocr/OcrPipeline.cs` (constructor :37-44, `SpotAsync` :120)
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (khối `ocr` trong `ParseStart` :649-669, `StartSpec` :1041-1062, nơi dựng `new OcrPipeline(...)`)
- Test: `ReTran.Tests/OcrPipelineTests.cs`, `ReTran.Tests/PipelineHostTests.cs` (mở rộng)

**Interfaces:**
- Consumes: `MergeThresholds` + `LineGrouper.Group(JsonArray, MergeThresholds)` (Task 2).
- Produces:
  ```csharp
  public OcrPipeline(ISidecarRpc rpc, OcrDiff diff, bool mergeLines = false, MergeThresholds? merge = null)
  ```
  `_merge` là field `MergeThresholds` (null ⇒ `MergeThresholds.Default`); `SpotAsync` gọi `LineGrouper.Group(spotsArr, _merge)`.
  Wire: `ocr.mergeGapRatio`, `ocr.mergeHeightRatio`, `ocr.mergeOverlapRatio` (số). `StartSpec` (record :1041) thêm positional cuối `MergeThresholds Merge`. **Task 3 không chạm `OcrLoopOptions`**: đường serve dựng pipeline trực tiếp từ spec tại `PipelineHost.cs:213`, còn `StartSpec.ToOptions()` (`:1062`) chỉ nuôi capture source (`:202`) + loop (`:261`) nên không cần ngưỡng. Task 4 mới mở `OcrLoopOptions` và khi đó `ToOptions()` truyền `Merge` luôn (parity với CLI).

- [ ] **Step 1: Viết test failing**
  - `OcrPipelineTests`: `SpotAsync_MergeOn_CustomGap_MergesWhatDefaultSplits` — `FakeSidecarRpc` trả 2 dòng gap 30 px cao 24; `new OcrPipeline(fake, new OcrDiff(), mergeLines: true, new MergeThresholds(2.0, 1.35, 0.5))` ⇒ `spots` có 1 spot `paragraph: true`, `new_texts == ["HELLO WORLD"]`. Giữ các test cũ (default) nguyên.
  - `PipelineHostTests` (dùng helper `StartParams()` + `host.StartAsync(parameters, ct)` như các test `Start_*`): với `parameters["ocr"] = new JsonObject { ["det"]="d", ["rec"]="r", ["mergeLines"]=true, ["mergeGapRatio"]=0.25, ["mergeHeightRatio"]=2.0, ["mergeOverlapRatio"]=0.0 }` → start không ném; ba key `null` → không ném; ba key thiếu → không ném; và các case lỗi: `mergeGapRatio = "x"` → `-32602` message chứa `mergeGapRatio`; `mergeGapRatio = 2.5` → `-32602`; `mergeHeightRatio = 0.5` → `-32602`; `mergeOverlapRatio = 1.5` → `-32602`.
- [ ] **Step 2: Chạy test — xác nhận fail** (compile error / `-32602` chưa có).
- [ ] **Step 3: Implement**
  - `OcrPipeline`: thêm tham số 4 + field; doc `<param>` song ngữ.
  - `PipelineHost.ParseStart`: trong khối `ocr`, đọc ba key bằng một helper riêng `private static double ParseRatio(JsonObject ocr, string key, double fallback, double lo, double hi)` — `null`/thiếu ⇒ `fallback`; không phải `JsonValue` số ⇒ `-32602` `"'ocr.<key>' must be a number between lo..hi."`; ngoài dải ⇒ cùng lỗi. Dùng helper cho cả ba để message nhất quán.
  - Dựng pipeline: `new OcrPipeline(rpc, new OcrDiff(), spec.MergeLines, spec.Merge)`.
- [ ] **Step 4: Chạy test — pass** (`RetranCore.Tests.OcrPipelineTests`, `RetranCore.Tests.PipelineHostTests`).
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.Core/Ocr/OcrPipeline.cs" "ReTran.Core/Serve/PipelineHost.cs" "ReTran.Tests/OcrPipelineTests.cs" "ReTran.Tests/PipelineHostTests.cs"
  git commit -m "feat(core): core.start ocr.mergeGapRatio/mergeHeightRatio/mergeOverlapRatio parsing"
  ```

---

### Task 4: CLI `--merge-gap` / `--merge-height` / `--merge-overlap`

**Files:**
- Modify: `ReTran.Core/Cli/OcrLoop.cs` (`OcrLoopOptions` :25, `Usage` :36, `TryParse` :48-119)
- Modify: `ReTran.Core/Cli/OcrCommands.cs` (`TryParseOneShot` :85-120, call site trong `RunAsync`)
- Test: `ReTran.Tests/OcrLoopCliTests.cs` (mở rộng)

**Interfaces:**
- Consumes: `MergeThresholds` (Task 2), `OcrPipeline(..., MergeThresholds?)` (Task 3).
- Produces: `OcrLoopOptions` thêm positional cuối `MergeThresholds Merge = default-…` (dùng `MergeThresholds.Default`); `OcrLoop.TryParse` nhận `--merge-gap <x>`, `--merge-height <x>`, `--merge-overlap <x>`, parse `double` với `CultureInfo.InvariantCulture`, sai/dải ⇒ `error` nêu tên flag; `TryParseOneShot` đổi signature thành `internal static bool TryParseOneShot(string[] args, out string? input, out bool mergeLines, out MergeThresholds merge, out string? error)`; `Usage` nối thêm `[--merge-gap N] [--merge-height N] [--merge-overlap N]`.

- [ ] **Step 1: Viết test failing** (`OcrLoopCliTests`)
  - `TryParse_MergeFlags_SetsThresholds`: `["--monitor","0","--merge-lines","--merge-gap","0.25","--merge-height","2","--merge-overlap","0"]` ⇒ `options.Merge == new MergeThresholds(0.25, 2.0, 0.0)`.
  - `TryParse_MergeFlagsAbsent_DefaultThresholds`: ⇒ `options.Merge == MergeThresholds.Default`.
  - `TryParse_MergeFlagMissingValue_Errors`: `["--merge-gap"]` ⇒ `null` + error chứa `--merge-gap`.
  - `TryParse_MergeFlagOutOfRange_Errors`: `--merge-gap 2.5` ⇒ `null` + error chứa `--merge-gap`.
  - One-shot: `TryParseOneShot(["a.png","--merge-lines","--merge-gap","0.3"])` ⇒ `input=="a.png"`, `mergeLines`, `merge.GapRatio==0.3`; `["a.png","--merge-gap"]` ⇒ error.
- [ ] **Step 2: Chạy test — xác nhận fail.**
- [ ] **Step 3: Implement** — duyệt args bằng switch hiện có, mỗi flag mới đọc `args[++i]` và validate bằng `MergeThresholds.IsValid` trên bộ ba đã gộp (báo lỗi tên flag cụ thể). Gọi `new OcrPipeline(sp, new OcrDiff(), options.MergeLines, options.Merge)` tại `OcrCommands.RunLoopAsync` và một-shot tương ứng.
- [ ] **Step 4: Chạy test — pass** (`RetranCore.Tests.OcrLoopCliTests`).
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.Core/Cli/OcrLoop.cs" "ReTran.Core/Cli/OcrCommands.cs" "ReTran.Tests/OcrLoopCliTests.cs"
  git commit -m "feat(core): CLI --merge-gap/--merge-height/--merge-overlap for ocr and ocr loop"
  ```

---

### Task 5: App — gửi ba ngưỡng trong `core.start`

**Files:**
- Modify: `ReTran.App/Core/PipelineController.cs` (`StartAsync` :238-240, ngay sau `ocr["mergeLines"]`)
- Test: `ReTran.Tests/PipelineControllerTests.cs` (mở rộng)

**Interfaces:**
- Consumes: `AppSettings.OcrMergeGap/OcrMergeHeight/OcrMergeOverlap` (Task 1), wire Task 3.
- Produces: `core.start` params luôn mang `ocr.mergeGapRatio|mergeHeightRatio|mergeOverlapRatio` là số (không phải chuỗi).

- [ ] **Step 1: Viết test failing** — theo pattern fake peer đã có (`call.Params["ocr"]!["ocrFps"]`): `Start_SendsMergeThresholdsFromSettings` — settings `with { OcrMergeGap = 0.2, OcrMergeHeight = 1.1, OcrMergeOverlap = 0.0 }` ⇒ ba giá trị số đúng; và với `AppSettings.Default` ⇒ `0.6/1.35/0.5`.
- [ ] **Step 2: Chạy test — xác nhận fail** (`PipelineControllerTests`, global namespace).
- [ ] **Step 3: Implement** — ba dòng gán `ocr["merge…"] = settings.OcrMerge…;` cạnh `ocr["mergeLines"] = settings.OcrMergeLines;` (`:239-240`).
- [ ] **Step 4: Chạy test — pass.**
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.App/Core/PipelineController.cs" "ReTran.Tests/PipelineControllerTests.cs"
  git commit -m "feat(app): send merge-line thresholds on core.start"
  ```

---

### Task 6: Helper thuần `OcrMergeThresholds` (App)

**Files:**
- Create: `ReTran.App/Services/OcrMergeThresholds.cs`
- Test: `ReTran.Tests/OcrMergeThresholdsTests.cs` (global namespace)

**Interfaces:**
- Consumes: không (App không tham chiếu Core).
- Produces (namespace `ReTran_App.Services`):
  ```csharp
  public static class OcrMergeThresholds
  {
      public const double DefaultGap = 0.6, DefaultHeight = 1.35, DefaultOverlap = 0.5;
      public static bool TryParseRatio(string? text, out double value);  // trim; chấp nhận BOTH "0.6" và "0,6"; null/rỗng/không parse => false
      public static bool InGapRange(double v);      // 0.1..2.0
      public static bool InHeightRange(double v);   // 1.0..3.0
      public static bool InOverlapRange(double v);  // 0.0..1.0
      public static bool AllValid(double gap, double height, double overlap);
  }
  ```
  Task 7 dùng trong `SaveOcrParamsAsync`.

- [ ] **Step 1: Viết test failing**
  - `TryParseRatio_CommaAndDot`: `"0.6"` và `"0,6"` ⇒ cùng `0.6`; `" 1.35 "` ⇒ `1.35`; `""`, `null`, `"abc"`, `"1e5"` ⇒ `false`.
  - `Ranges_Bounds`: `[Theory]` các biên hợp lệ `0.09→false`, `0.1→true`, `2.0→true`, `2.01→false` cho gap; `0.99→false`, `1.0→true`, `3.0→true`, `3.01→false` cho height; `-0.01→false`, `0.0→true`, `1.0→true`, `1.01→false` cho overlap.
  - `AppDefaults_MatchCoreConsts`: `Assert.Equal(LineGrouper.MaxGapRatio, OcrMergeThresholds.DefaultGap)` và hai cái kia (file test tham chiếu cả hai project — chống trôi giá trị giữa App và Core).
- [ ] **Step 2: Chạy test — xác nhận fail.**
- [ ] **Step 3: Implement** — parse bằng cách thử `double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, …)` rồi thử lại với `NumberFormatInfo { NumberDecimalSeparator = "," }`; **không** dùng `CurrentCulture` (kết quả phụ thuộc máy chạy test).
- [ ] **Step 4: Chạy test — pass.**
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.App/Services/OcrMergeThresholds.cs" "ReTran.Tests/OcrMergeThresholdsTests.cs"
  git commit -m "feat(app): pure OcrMergeThresholds parse/range helper shared by the settings sheet"
  ```

---

### Task 7: Sheet Settings — 3 hàng số (layout A) + i18n

**Files:**
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor` (khối checkbox :218-222, fields :332, `OnInitialized` :354, `ApplySettingsToOcrState` :601, `SaveOcrParamsAsync` :648-667, `RestoreOcrParamsAsync` :693)
- Modify: `ReTran.App/Services/I18nStrings.cs` (block `Vi` cạnh `deck.ocr.mergeLines.*` :207-208; block `En` cạnh key En tương ứng ~:508)
- Test: `ReTran.Tests/I18nServiceTests.cs` (đã có `EveryKey_Exists_In_Both_Languages` — không cần thêm test; không có harness bUnit cho component, Spec §5.12)

**Interfaces:**
- Consumes: `OcrMergeThresholds` (Task 6), `AppSettings.OcrMerge*` (Task 1).
- Produces: ba state string `_mergeGapText`, `_mergeHeightText`, `_mergeOverlapText`; key i18n `deck.ocr.mergeGap.label|hint`, `deck.ocr.mergeHeight.label|hint`, `deck.ocr.mergeOverlap.label|hint`, `deck.ocr.mergeThresholds.invalid`; checkbox tắt ⇒ ba ô `disabled`.

- [ ] **Step 1: Đọc lại `I18nStrings.cs`** (`git status --short` + đọc vùng xung quanh) — nhiều task chạm file này.
- [ ] **Step 2: Thêm key** — cả `Vi` lẫn `En`, chữ **nguyên văn** (user đã duyệt layout + hint):
  - Vi: `Khoảng cách dòng tối đa` / hint `Hai dòng cách nhau quá 0,6 × chiều cao chữ thì không cùng khối. 0,1–2,0. Chữ cao 24 px → quá ~14 px là tách.`; `Chênh chiều cao chữ` / `Hai dòng lệch chiều cao quá 1,35 lần thì không gộp. 1,0–3,0. 1,0 = chỉ chữ cùng cỡ.`; `Chồng ngang tối thiểu` / `Hai dòng phải chồng lên nhau ít nhất 0,5 chiều rộng dòng nhỏ hơn. 0–1. Giảm = cho phép lệch cột (text căn giữa).`; `deck.ocr.mergeThresholds.invalid` = `Ngưỡng gộp dòng phải nằm trong dải cho phép.`
  - En: `Max line gap` / `Lines more than 0.6 × line height apart stop being one block. 0.1–2.0. 24 px text → past ~14 px they split.`; `Max height difference` / `Lines differing in height by more than 1.35× are not merged. 1.0–3.0. 1.0 = same size only.`; `Min horizontal overlap` / `Lines must overlap by at least 0.5 of the narrower line's width. 0–1. Lower = tolerate offset columns (centered text).`; `Merge-line thresholds must be within range.`
- [ ] **Step 3: Markup** — trong cùng section, ngay dưới `<div class="rt-srow">` của checkbox (`:218-222`), ba hàng theo khuôn hàng `rt-input` số đã có ở `:205-214`:
  ```razor
  <div class="rt-srow rt-srow--num">
      <span>@(L["deck.ocr.mergeGap.label"])</span>
      <input type="text" class="rt-input rt-mono" inputmode="decimal"
             aria-label="@(L["deck.ocr.mergeGap.label"])"
             disabled="@(!_mergeLines)" @bind="_mergeGapText" />
      <p class="rt-hint">@(L["deck.ocr.mergeGap.hint"])</p>
  </div>
  ```
  (lặp lại cho height/overlap). Thêm rule `.rt-srow--num` vào `ReTran.App/vite-project/src/css/deck.css` ngay sau block `:has(> input[type="checkbox"])` (:759-763): `grid-template-columns: 1fr 78px; column-gap: 10px; align-items: center;` và `.rt-srow--num > .rt-hint { grid-row: 2; grid-column: 1 / -1; }` — cùng kiểu grid mà hàng checkbox đang dùng, không tự chế layout mới.
- [ ] **Step 4: Nối state** — khai 3 field string, init từ `current.OcrMerge*` trong `OnInitialized`, cập nhật trong `ApplySettingsToOcrState`, `RestoreOcrParamsAsync` gán lại `OcrMergeThresholds.Default*` (dạng text), và `SaveOcrParamsAsync`: parse cả ba trước, nếu `!OcrMergeThresholds.AllValid(...)` ⇒ `_ocrStatusText = L["deck.ocr.mergeThresholds.invalid"]`, `_ocrStatusError = true`, **return** không save; nếu ổn thì thêm `OcrMergeGap = gap, OcrMergeHeight = height, OcrMergeOverlap = overlap` vào `with { ... }` hiện có (một lần `Save` duy nhất, cùng `_mergeLines`).
- [ ] **Step 5: Build + test + frontend** — `dotnet build "ReTran Project.slnx"` 0 error; `dotnet test … -- --filter-class "I18nServiceTests"` pass; trong `ReTran.App/vite-project/`: `npm run build` (không commit `wwwroot/build`).
- [ ] **Step 6: Commit**
  ```bash
  git add "ReTran.App/Components/Shared/SettingsPanel.razor" "ReTran.App/Services/I18nStrings.cs" "ReTran.App/vite-project/src/css/deck.css"
  git commit -m "feat(app): merge-line threshold fields in the OCR settings section"
  ```

---

### Task 8: `OverlayState.Clear()` + gọi khi pipeline dừng

**Files:**
- Modify: `ReTran.App/Core/OverlayState.cs` (thêm `Clear()`)
- Modify: `ReTran.App/Core/PipelineController.cs` (`StopAsync` :275, core-exit :627-628, `HandlePipelineState` :824-825)
- Test: `ReTran.Tests/OverlayStateTests.cs`, `ReTran.Tests/PipelineControllerTests.cs` (mở rộng)

**Interfaces:**
- Consumes: `PipelineController.StateChanged` / `PipelineStateChanged` (đã có).
- Produces:
  ```csharp
  public void Clear()   // OverlayState: lock CHẶN (không TryEnter — Clear không phải khung hình đến dồn, xem Spec §5.6/K10)
  ```
  Đặt `_latest = null` và nổ `Changed` **một lần** (không nổ khi `_latest` đã null). Task 9 dựa vào `Latest == null` ⇒ snapshot rỗng.

- [ ] **Step 1: Viết test failing**
  - `OverlayStateTests.Clear_ResetsLatest_AndRaisesOnce`: publish 1 frame → `Clear()` → `Latest is null`, `Changed` đếm = 1; `Clear()` lần nữa → không nổ thêm.
  - `PipelineControllerTests.Stop_ClearsOverlayState`: publish frame qua `HandleFrameTranslated` (dùng đường inject notification có sẵn trong file test) → `StopAsync` → `overlay.Latest is null`.
  - `PipelineControllerTests.PipelineStateNotification_Stopped_ClearsOverlayState` và `CoreExited_ClearsOverlayState` (dùng fake peer đã có để bắn notification dừng / `Exited`).
- [ ] **Step 2: Chạy test — xác nhận fail.**
- [ ] **Step 3: Implement** — `Clear()` trong `OverlayState`; trong `PipelineController` thêm `private void ClearOverlayFrames() => _overlay.Clear();` và gọi ở **cả ba** chỗ chuyển sang không-chạy (`StopAsync`, core exit, notification `running == false`). Không xóa khi Start.
- [ ] **Step 4: Chạy test — pass** (cả `OverlayWindowServiceTests` để chắc overlay local không đổi hành vi).
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.App/Core/OverlayState.cs" "ReTran.App/Core/PipelineController.cs" "ReTran.Tests/OverlayStateTests.cs" "ReTran.Tests/PipelineControllerTests.cs"
  git commit -m "feat(app): OverlayState.Clear on pipeline stop so OBS never resurrects stale boxes"
  ```

---

### Task 9: `OverlayVisibility` + gate `OverlayFeedServer` + chữ trên thẻ OBS

**Files:**
- Create: `ReTran.App/Core/OverlayVisibility.cs` (interface `IOverlayVisibility` cùng file, theo pattern `IOverlayHost`)
- Modify: `ReTran.App/Services/OverlayFeed/OverlayFeedServer.cs` (ctor :142, `OnStateChanged:489`, `ServeEvents` snapshot :470, `DisposeAsync` :250)
- Modify: `ReTran.App/MauiProgram.cs` (đăng ký `IOverlayVisibility`)
- Modify: `ReTran.App/Components/Pages/Home.razor` (thẻ OBS: thêm một dòng hint) + `ReTran.App/Services/I18nStrings.cs` (key `deck.overlay.obs.hint.requiresOverlay` — đọc lại file trước)
- Test: `ReTran.Tests/Overlay/OverlayVisibilityTests.cs` (mới), `ReTran.Tests/Overlay/OverlayFeedServerTests.cs` (mở rộng)

**Interfaces:**
- Consumes: `AppSettingsService.Changed` (`:76`) + `AppSettings.OverlayOnGame`, `PipelineController.IsRunning` + `StateChanged` (`:177`) + `PipelineStateChanged` (`:167`), `OverlayState.Latest` (Task 8 có thể là `null`).
- Produces:
  ```csharp
  namespace ReTran_App.Core;
  public interface IOverlayVisibility { bool ShouldShowObs { get; } event Action? Changed; }
  public sealed class OverlayVisibility : IOverlayVisibility, IDisposable
  {
      public OverlayVisibility(AppSettingsService settings, PipelineController pipeline, ILogger<OverlayVisibility> log);
  }
  ```
  `ShouldShowObs == settings.Load().OverlayOnGame && pipeline.IsRunning`; `Changed` chỉ nổ khi **giá trị đổi** (edge-triggered — Spec 5.14). Chữ ký feed mới (tham số optional **cuối cùng**, để không vỡ call hiện có):
  ```csharp
  public OverlayFeedServer(OverlayState state, int port = 17863, TimeSpan? heartbeat = null,
      ILogger<OverlayFeedServer>? logger = null, IOverlayVisibility? visibility = null)
  ```
  `visibility == null` ⇒ coi như luôn hiện (giữ mọi test feed hiện tại không đổi).

- [ ] **Step 1: Viết test failing cho `OverlayVisibility`** — dùng `PreviewState`/`OverlayState` + fake peer pattern đã có trong `PipelineControllerTests`, và `AppSettingsService` temp path:
  - `Matrix`: 4 tổ hợp (OverlayOnGame × IsRunning) ⇒ `ShouldShowObs` đúng bảng.
  - `SettingsSaved_ToggleOverlay_RaisesChangedOnce`: `Save(with { OverlayOnGame = true })` hai lần liên tiếp cùng giá trị ⇒ `Changed` chỉ nổ khi thật sự đổi.
  - `BothStopEvents_RaiseChangedOnce`: fake pipeline raise `PipelineStateChanged(false, null)` **rồi** `StateChanged` liền nhau ⇒ `Changed` đếm = 1.
- [ ] **Step 2: Viết test failing cho `OverlayFeedServer`** (theo harness SSE hiện có trong `OverlayFeedServerTests`, dùng `FakeVisibility` tự raise `Changed`):
  - `Hidden_BroadcastsOneEmptyPayload_ThenNothing`: đang hiện + có client, chuyển ẩn ⇒ đúng **một** event `boxes: []`; sau đó publish 3 frame vào `OverlayState` ⇒ client không nhận thêm box event nào.
  - `Shown_AfterHidden_BroadcastsCurrentSnapshot`: `_state.Latest` có 1 box ⇒ bật lại ⇒ client nhận payload có box đó.
  - `ClientConnecting_WhileHidden_GetsEmptySnapshot` và `ClientConnecting_AfterClear_GetsEmptySnapshot` (`_state.Clear()` rồi connect ⇒ `boxes` rỗng).
  - `NoVisibility_GatesNothing` (regression: ctor không truyền visibility vẫn phát box như cũ).
- [ ] **Step 3: Chạy test — xác nhận fail.**
- [ ] **Step 4: Implement**
  - `OverlayVisibility`: subscribe `settings.Changed`, `pipeline.StateChanged`, `pipeline.PipelineStateChanged`; field `_current` so trước khi raise; `Dispose` unsubscribe.
  - `OverlayFeedServer`: field `private int _hidden;` (`Volatile`). Trong `OnStateChanged()`: `if (Volatile.Read(ref _hidden) == 1) return;` trước khi tăng `seq`. Khi `visibility.Changed` nổ: compute `bool hide = !visibility.ShouldShowObs;` nếu `hide != (_hidden == 1)` thì set cờ và `Broadcast(ComposePayload(Interlocked.Increment(ref _seq), hide ? null : _state.Latest))` — chính là payload xóa của Spec K9. Trong `ServeEvents`: enqueue `ComposePayload(Interlocked.Read(ref _seq), Volatile.Read(ref _hidden) == 1 ? null : _state.Latest)`. Unsubscribe trong `DisposeAsync` (cạnh `_state.Changed -= OnStateChanged` `:250`).
  - `MauiProgram`: `builder.Services.AddSingleton<IOverlayVisibility, OverlayVisibility>();` ngay cạnh `AddSingleton<OverlayFeedServer>()` (`:37`). DI sẽ bơm vào optional param cuối.
  - `Home.razor` thẻ OBS (`deck.overlay.obs.*`, xem key `deck.overlay.obs.hint.setup`): thêm một dòng `.rt-hint` dùng key mới `deck.overlay.obs.hint.requiresOverlay` — Vi `Lớp phủ phải BẬT để OBS hiện chữ.` / En `The overlay must be ON for OBS to show text.` (Review Focus #5).
- [ ] **Step 5: Chạy test — pass** (`OverlayVisibilityTests`, `OverlayFeedServerTests`, `I18nServiceTests`; `OverlayFeedServerTests` nhạy timing — fail thì chạy lại một lần trước khi kết luận).
- [ ] **Step 6: Commit**
  ```bash
  git add "ReTran.App/Core/OverlayVisibility.cs" "ReTran.App/Services/OverlayFeed/OverlayFeedServer.cs" "ReTran.App/MauiProgram.cs" "ReTran.App/Components/Pages/Home.razor" "ReTran.App/Services/I18nStrings.cs" "ReTran.Tests/Overlay/OverlayVisibilityTests.cs" "ReTran.Tests/Overlay/OverlayFeedServerTests.cs"
  git commit -m "feat(app): one overlay switch drives both desktop and OBS; clear OBS on stop and on hide"
  ```

---

### Tasks 10–15: setup wizard lần đầu + update — chạy theo plan setup, có delta

Phần setup/update đã được thiết kế và chia task trong `Docs/superpowers/plans/2026-11-04-first-run-setup-wizard.md` (spec: `Docs/superpowers/specs/2026-11-03-first-run-setup-wizard-design.md`). **Đọc plan đó và thi công nguyên văn các task được trỏ dưới đây**, với các delta sau.

Trạng thái đã xác minh 2026-11-06: **setup Tasks 1–6 ĐÃ XONG và đã commit trên `master`** — không làm lại:

| Setup Task | Nội dung đã xong | Commit |
|---|---|---|
| 1 | vendor `runtimes/win-x64/uv.exe` + `uv.version.txt`, Content items đưa source `ReTran.OCR` vào output | `46c8be7` |
| 2 | sidecar RPC `ocr.models.recommend` | `aab618d` |
| 3 | migration một lần model từ `%LOCALAPPDATA%\ReTran\models` | `7712274` |
| 4 | `PADDLE_PDX_CACHE_HOME` → `<repoRoot>\ReTran.OCR\models` + proxy RPC trong Core | `be9a7fe`, `eb15507` |
| 5 | `SetupPaths` + `SetupState` (`setup.json`, `NeedsSetup`) | `74cae90` |
| 6 | `SystemCheck` + `VideoAdapterSource` + thang wheel CUDA | `1d75819`, `84b90ff` |

Branch công tác đã **rebase lên `master` (`84b90ff`)** nên toàn bộ phần code setup đó nằm trong branch này; baseline sau rebase: **525 passed / 0 failed**, build 0 error / 34 warning.

| Task ở đây | Task trong plan setup | Delta so với plan setup |
|---|---|---|
| **Task 10** | **không có trong plan setup — task mới** | **Sửa lỗi ánh xạ phiên bản driver NVIDIA** trong `ReTran.App/Services/Setup/SystemCheck.cs:380-409` (`TryGetNvidiaProductVersion`). Hiện suy số trăm từ `segments[0]` (`30⇒400, 31⇒500, 32⇒600`) — **sai**: số trăm nằm ở **segment thứ 3** (branch): `14⇒4xx`, `15⇒5xx`, `16⇒6xx`. Đã đo/tra chứng cứ: máy dev WMI `32.0.16.2114` ↔ `nvidia-smi 621.14`; `32.0.15.6094` **là** driver 560.94 (DriversCloud + Microsoft Update Catalog + Dell KB); `32.0.15.8163` (10/2025) thật là 581.63 chứ không phải 681.63. Hệ quả thật: driver 5xx mang DDI 32 bị suy thành 6xx → vượt sàn 580 → wizard cài **cu130** cho máy chỉ chạy nổi cu126 (sai theo hướng *không* fail-safe). Kèm trong cùng task: (a) fixture WMI thật `32.0.15.6094→cu126`, `32.0.15.7697/8163→cu129/Cpu`, `32.0.16.2114→cu130`, `31.0.15.3699→Cpu`, và hàng boundary còn thiếu (base ngoài khoảng, tail 3/5 chữ số); (b) cổng arch dùng `RuntimeInformation.OSArchitecture == Architecture.X64` thay cho `Environment.Is64BitOperatingSystem` (ARM64 báo oan "Windows 32-bit"); (c) gate NVIDIA phải xét *discrete* cho đối xứng với nhánh AMD; (d) lỗi WMI trong `VideoAdapterSource.cs:40-44` phải được log (thụt qua im lặng khiến máy RTX khỏe báo "không có GPU NVIDIA"); (e) `NetworkProbeHosts` → `IReadOnlyList<string>`; (f) xếp hạng wheel bằng rank int thay `string.CompareOrdinal("cuXXX")`. |
| **Task 11** | Task 7 (`ProcessRunner` + `UvSyncRunner`) | Plan setup chưa chỉ chỗ **tiêu thụ** `SystemCheckResult.CudaWheel`: `ReTran.OCR/pyproject.toml` hard-code index `paddle-cu129` (`https://www.paddlepaddle.org.cn/packages/stable/cu129/`) + `[tool.uv.sources]`. `UvSyncRunner` phải ghi lại URL index theo wheel đã chọn (`…/stable/<cuXXX>/`) khi flavor = GPU, và bỏ pin GPU + 2 section khi flavor = CPU (đã nêu trong plan setup). Nếu index không có bản wheel tương ứng, `uv sync` exit ≠ 0 → retry ×3 → màn hình lỗi (chấp nhận, ghi vào spec §10 là mục cần đo trên máy GPU thật). |
| **Task 12** | Task 8 (`SetupCoordinator`) | không |
| **Task 13** | Task 9 (`OcrCatalogDisplay` + drift test) | drift test gọi Python/uv thật: worktree có junction `ReTran.OCR\.venv` → venv của tree chính, chạy được; nếu uv/venv absent thì skip theo pattern `ServeM4IntegrationTests`. |
| **Task 14** | Task 10 (DI + `SetupModal.razor` + gate pipeline + nút "Chạy lại setup" + i18n) | Năm file chạm chồng với task khác trong plan này — **đọc lại từng file trước khi sửa, không tin số dòng**: `MauiProgram.cs` (Task 9 → 14 → 23), `Home.razor` (Task 9 → 14 → 22), `SettingsPanel.razor` (Task 7 → 14), `I18nStrings.cs` (Task 7 → 9 → 14 → 21), `MainLayout.razor` (chỉ Task 14). Neo `MainLayout.razor:74` và `Home.razor:~473` có thể lệch vì WIP UI của user (`MainLayout +21`, `Settings −182`) được commit trước khi bắt đầu Task 14 → re-verify anchor. |
| **Task 15** | Task 11 (khoá hành vi updater + verify zip trong CI) | Plan setup ghi “Create `ReTran.Tests/UpdateStagerTests.cs`” — **file đó đã tồn tại** ở `ReTran.Tests/Update/UpdateStagerTests.cs` (10 test cũ). **Viết thêm vào file cũ**, không tạo file trùng. `InternalsVisibleTo` cho `ReTran.Updater.csproj` và comment khoá trên `UpdateStager.CopyDirectory` giữ nguyên như plan setup. |

Quyết định đo thật (user chốt 2026-11-06): Tasks 10–15 dùng **fake process runner** trong unit test; việc chạy `uv sync` thật cho cả hai flavor + đo GB/thời gian + xác nhận host probe **defer** sang smoke trên máy sạch của user, và phải được ghi là open item trong spec §10 (`Docs/superpowers/specs/2026-11-03-first-run-setup-wizard-design.md`) khi Task 15 commit.

---

### Tasks 16–23: menu nổi — chạy theo Plan 2, có delta

Toàn bộ phần menu nổi + tray đã được thiết kế và chia task trong `Docs/superpowers/plans/2026-11-02-floating-menu.md`. **Đọc plan đó và thi công nguyên văn các task được trỏ dưới đây**, với các delta sau (đây là phần duy nhất khác biệt so với plan cũ):

| Task ở đây | Task trong Plan 2 | Delta so với Plan 2 |
|---|---|---|
| Task 16 | Task 2 (`RunStats` + `IPipelineControl`) | `AppSettings` đã có 10 tham số mới ở Task 1 — **không** đổi record nữa. Plan 2 Task 2 giữ nguyên. |
| Task 17 | Task 3 (`FloatingMenuLayout`) | không |
| Task 18 | Task 4 (`FloatingMenuRenderer`) | không |
| Task 19 | Task 5 (window menu trong `Win32OverlayHost`) | không |
| Task 20 | Task 6 (`FloatingMenuService`) | menu **không** có control ngưỡng gộp (Spec §3) — chỉ switch `Gộp nhiều dòng` như plan đã nêu |
| Task 21 | Task 7 (tray `Hiện/ẩn menu nổi` + i18n menu) | đọc lại `I18nStrings.cs` — Task 7, Task 9 và Task 14 của plan này đã thêm key |
| Task 22 | Task 8 (`Home.razor` switch menu + đọc `RunStats`) | Task 9 thêm dòng hint OBS, Task 14 thêm guard Start vào `Home.razor` — đọc lại file trước khi sửa |
| Task 23 | Task 9 (DI + start-up + ghi perf) | `IOverlayVisibility` **đã** được đăng ký ở Task 9; setup DI **đã** ở Task 14 — Plan 2 Task 9 chỉ thêm `RunStats`, `IMenuWindowHost`, `IOverlayToggle`, `FloatingMenuService`. `MauiProgram.cs` bị chạm ba lần ở ba vùng khác nhau (9, 14, 23). |

Mỗi task 16–23 giữ nguyên bước, test và commit message của Plan 2. Nếu Plan 2 ghi `AppSettings` 7 tham số (Task 1), bỏ qua — đã xong ở Task 1 của plan này.

---

### Task 24: Verification cuối + smoke + ghi perf

**Files:**
- Modify: `Docs/performance.md` (mục ngắn cho ngưỡng gộp + gate OBS)
- No code.

- [ ] **Step 1: Build + test full** — `dotnet build "ReTran Project.slnx"` (0 error, 34 warning baseline); `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` ⇒ **toàn bộ PASS**, số test ≥ **525** (baseline đo sau rebase lên `master`) + test mới của Task 6–15 (≈ +30 phần ngưỡng/OBS + ≈ +40 phần setup). Chạy thêm `uv run pytest tests -v` từ `ReTran.OCR/` cho phần sidecar.
- [ ] **Step 2: Smoke có tương tác — DEFERRED TO CONTROLLER** (subagent không thấy desktop):
  1. Mở app → Settings → mục OCR: 3 hàng số hiện **đúng** layout A (label trái, ô số phải, hint dưới), disabled khi bỏ tick `Gộp nhiều dòng`.
  2. Nhập `mergeGap = 2.0` → Lưu → Stop/Start → khối nhiều dòng xa nhau giờ gộp thành 1 pill; nhập `0.1` → tách rời.
  3. Nhập `2.5` (ngoài dải) → dòng đỏ cạnh nút Lưu, giá trị không đổi trong `settings.json`.
  4. OBS Browser Source mở URL overlay: tick `Lớp phủ` OFF → chữ biến mất trên scene trong ≤1 s; ON → hiện lại; **Stop** pipeline khi overlay ON → scene sạch, và **Start** lại →scene trống cho tới frame mới.
  5. Xác nhận Review Focus #5: `Lớp phủ` OFF làm OBS trắng là **hành vi chủ đích**; dòng hint trên thẻ OBS có hiện.
  6. Chuột phải tray icon → menu 3 mục tiếng Việt (`Mở ReTran · Lớp phủ · Thoát`), checkmark theo lớp phủ; chuột trái → app hiện lại. (Bằng chứng cho Plan 1 Task 9 Step 5 — G9.)
  7. Bấm X → app ẩn xuống tray, pipeline vẫn dịch; `Thoát` → không còn `ReTran.Core`/python mồ côi. Chữ cuối **ở lại** trên scene OBS (quyết định U5 — không phải bug).
  8. Menu nổi: hiện mặc định, kéo được, không cướp focus game, box overlay vẫn dưới menu.
  9. **Setup / máy sạch**: giải nén zip (hoặc copy build output sang thư mục rỗng) → mở app → modal setup hiện, bước Check system tự chạy không hỏi gì, thông báo GPU/CPU đúng card thật của máy; chọn **한국어** → danh sách ghi đúng model rec bản 5 + tổng dung lượng trước khi tải.
  10. **Setup / driver**: trên máy NVIDIA, wizard chọn đúng flavor theo nấc duy nhất (đọc log `SystemCheck`): driver ≥ 576.02 → GPU `cu129`, cũ hơn → CPU + notice nêu "576.02" + cụm "chạy lại Setup". Ghi lại số GB đĩa + thời gian `uv sync` vào spec §10 (phần đo thật mà user đã chốt cho defer — xem mục "Quyết định đo thật" ở Tasks 10–15).
  11. **Setup / resume**: giết mạng giữa `uv sync` → retry ×3 → màn hình lỗi đúng 2 nút; `Bỏ qua` → app đóng hẳn, không còn `ReTran.Core`/python mồ côi; mở lại → modal hiện lại và resume đúng bước hỏng, không hỏi lại ngôn ngữ.
  12. **Update giữ state**: chạy update flow sau khi setup xong → `ReTran.OCR\.venv` và `models` còn nguyên, code sidecar ghi đè và pipeline chạy tiếp không cần setup lại.
  13. **Dev machine**: mở app từ repo layout → wizard không phá `.venv` của repo, không copy đè source repo; nút "Chạy lại setup" trong Settings hoàn tất gần tức thì.
- [ ] **Step 3: Ghi `Docs/performance.md`** — mục ngắn: ngưỡng là 3 giá trị value-type đi theo struct, `LineGrouper` không đổi số phép tính (đối chiếu mục đã có ở `:921`, 300 dòng < 5 ms); gate OBS giảm traffic SSE khi overlay tắt (không broadcast khi ẩn, một payload xóa mỗi transition); `OverlayVisibility` không timer.
- [ ] **Step 4: Commit**
  ```bash
  git add "Docs/performance.md"
  git commit -m "docs(perf): merge thresholds and OBS visibility gate"
  ```

---

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** G6 → Task 1/2/3/4/5/6/7. G7 → Task 8/9. G8 → Task 8 (Clear) + Task 9 (one payload). G9 → Task 24 Step 2.6–7. U1–U3 → Task 7 (layout A), Task 9 (một công tắc). U4 → Tasks 16–23. U5 → Task 8 (Stop xóa) + Task 9 (ẩn xóa) + Task 24 (thoát không xóa, ghi rõ không phải bug). K1–K15 → Task 1–9 với số thứ tự trùng tên. §5 facts → ghi lại trong Global Constraints/Review Focus. Setup/update (spec 2026-11-03) → setup Tasks 1–6 đã xong trên `master`; phần còn lại = Task 10–15 của plan này, kèm Task 10 sửa lỗi ánh xạ driver (mục Important của báo cáo verify ngày 2026-11-06) và Task 11 tiêu thụ `CudaWheel` (gap do thang wheel mới sinh ra, plan setup chưa phủ). **Không có gap.**
2. **Step scan:** mỗi step một hành động check được; body code chỉ xuất hiện ở chỗ thuật toán/chữ ký mà test không tự quyết (Clear, gate cờ `_hidden`, parse helper).
3. **Type consistency:** `MergeThresholds(GapRatio, HeightRatio, MinOverlapRatio)` + `Default` + `IsValid` dùng nhất quán ở Task 2/3/4; `AppSettings.OcrMerge{Gap,Height,Overlap}` ở Task 1/5/6/7; wire key `ocr.merge{Gap,Height,Overlap}Ratio` ở Task 3/5; `IOverlayVisibility.ShouldShowObs|Changed` ở Task 9; `OverlayState.Clear()` ở Task 8/9.
4. **Review Focus:** 7 dòng, test ghim vào Task 1,2,3,8,9,6,7,10 — mỗi dòng có test sở hữu.
5. **Proportion:** plan ngắn hơn tổng ba spec + Plan 2 + plan setup vì phần menu nổi và phần setup được tham chiếu chứ không chép lại.
