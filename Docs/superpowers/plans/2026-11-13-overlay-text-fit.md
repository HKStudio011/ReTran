# Plan: Font scale & wrap cho pill overlay — fit theo lòng box (3 bề mặt)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cỡ chữ pill = lớn nhất mà khối chữ wrap vừa lòng box (bỏ trần cứng 2/4 dòng); khi bí thì pill nở tràn thay vì ép chữ nhỏ/cắt "…" — áp dụng đồng bộ preview (Core), overlay Win32 (App) và trang OBS (JS).

**Architecture:** `BoxAnnotationStyle.Fit` (2 bản sao Core/App, sửa y hệt nhau) bỏ tham số `maxLines`/`shrink`; điều kiện fit chỉ còn `block.Height + 2×PadY ≤ innerHeight`. Hai renderer thêm guard viewport: chỉ `TruncateToFit` khi pill vượt hẳn chiều cao viewport. Trang OBS port cùng thuật toán bằng element đo + cap chữ scale theo canvas như native.

**Tech Stack:** C# .NET 10 + GDI+ (`ReTran.Core`, `ReTran.App`), xUnit v3 (MTP runner), JavaScript thuần cho `wwwroot/overlay-feed` (không build) — không dependency mới.

**Spec:** `Docs/superpowers/specs/2026-11-13-overlay-text-fit-design.md` (user duyệt 2026-11-13). Spec nền: `2026-11-01-ocr-line-grouping-design.md` (D7 bị thay thế).

## Global Constraints

- **Không `git add -A`.** Repo có nhiều file WIP của user. Mỗi commit chỉ `git add` đúng các file của task. Không push. Branch `master`.
- Build chuẩn: `dotnet build "ReTran Project.slnx"` → 0 error (warning `xUnit1051` có sẵn).
- Test chuẩn: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<TênĐầyĐủ>"`. Test Core mang prefix `RetranCore.Tests.`; test App/root ở global namespace. **Baseline đã đo lại 2026-11-13 sau merge floating-menu (9934b51): 742 passed / 0 failed.** Mọi task phải giữ nguyên con số đó trở lên.
- **Hai bản `BoxAnnotationStyle` (Core + App) phải giống nhau từng dòng logic** — sửa bản nào thì chép sang bản kia ngay trong cùng task; có test `BoxAnnotationStyleSyncTests` ghim hằng số (Task 2).
- `Fit` signature mới (bỏ `maxLines`, `shrink`): cập nhật **mọi** call site — `FrameAnnotator.cs`, `Win32OverlayHost.cs`, `BoxAnnotationStyleTests.cs` (helper). `TruncateToFit` **giữ nguyên chữ ký**, chỉ còn dùng ở guard viewport.
- Bản App `BoxAnnotationStyle` đang được **menu nổi + tray dùng chung** (`FloatingMenuRenderer`, `TrayIconSource`: màu, `GetFont`, `CreateTextFormat`, `BuildRoundedRect`, `TruncateToFit`, `PadX/PadY`, `DecorationScale` — vừa merge 9934b51). Plan này **chỉ đổi chữ ký `Fit`** (+ logic `Measure`); không được đổi các API còn lại, không đụng phần menu trong `Win32OverlayHost`.
- Không đổi màu/mockup/viền/bo góc/vị trí box; không đổi wire; không thêm settings.
- Ngân sách `AGENTS.md`: annotate ≤ 40 ms/khung @ ~60 box — chạy lại `dotnet run -c Debug --project Tools/Perf/annotbench` ở Task 4.
- Trang OBS không có harness JS ⇒ Task 3 verify bằng checklist tay (mục "DEFERRED TO CONTROLLER"); không đụng `ReTran.App/wwwroot/build/`.

## Review Focus

Năm failure mode spec ngụ ý nhưng không task nào tự phủ — mỗi dòng đã ghim test vào task sở hữu:

1. **Box gộp cao còn thừa nửa dưới** ⇒ cỡ chữ phải tăng và dùng gần hết lòng box (bảng §1 spec: 27px→38px). → Task 1 `Fit_TallBox_UsesVerticalRoom`.
2. **Box nhỏ + bản dịch dài** ⇒ `Fits=false`, `Size=MinFontSize`, và renderer phải NỞ pill xuống dưới box — không còn cắt "…" mặc định. → Task 1 `Fit_TinyBox_KeepsFloorAndReportsNotFitting`, Task 1 `Annotate_TinyBox_LongText_PillGrowsBelowBox`, Task 2 `DrawBoxes_TinyBox_LongText_PillGrowsBelowBox`.
3. **Cắt "…" cũ quay lại ở đường thường** (regression do gọi `TruncateToFit` sai chỗ) ⇒ chỉ vượt **viewport** mới cắt. → Task 1 `Fit_LongTranslation_FitsInnerHeight` (không truncate) + guard chỉ theo `outH`.
4. **Canvas lớn (1920/2160) chữ OBS nhỏ hơn native** ⇒ cap chữ scale theo `width/1280` như native. → Task 3 checklist mục 3 (1920 → cap ~60px).
5. **Box thấp-rộng 1 dòng bị phình/đổi hành vi ngoài dự kiến** ⇒ giữ 1 dòng, không tăng cỡ. → Task 1 `Fit_WideShortBox_StaysSingleLine`.

---

### Task 1: Core — `BoxAnnotationStyle.Fit` mới + `FrameAnnotator` guard viewport

**Files:**
- Modify: `ReTran.Core/Serve/BoxAnnotationStyle.cs` (`Fit` :137-163, `Measure` :255-264, doc `:120-136`)
- Modify: `ReTran.Core/Serve/FrameAnnotator.cs` (`DrawBoxes` :118-129)
- Modify: `ReTran.Tests/BoxAnnotationStyleTests.cs` (helper + 2 test thay, 3 test mới)
- Modify: `ReTran.Tests/FrameAnnotatorTests.cs` (+1 test, sửa comment `:205`)

**Interfaces:**
- Consumes: không (nội bộ Core).
- Produces (Task 2 chép sang bản App y hệt):
  ```csharp
  public static TextFit Fit(Graphics g, string text, float innerWidth, float innerHeight,
      float boxHeight, StringFormat format, float maxFontSize = MaxFontSize);
  ```
  `Measure(size)` mới: `fits = block.Height + 2f * PadY(size) <= innerHeight + 0.5f` (bỏ điều kiện `lines <= maxLines`); `Lines` vẫn được tính và trả về trong `TextFit` (cho test/telemetry). Guard trong renderer (khối này là chủ đích, không suy ra từ chữ ký):
  ```csharp
  float need = block.Height + 2f * padY;
  if (need > outH)   // pill không thể nằm trọn trong viewport -> cắt theo ngân sách viewport
  {
      int maxLines = Math.Max(1, (int)MathF.Floor((outH - 2f * padY) / BoxAnnotationStyle.GetFont(fit.Size).GetHeight(g)));
      label = BoxAnnotationStyle.TruncateToFit(g, label, fit.Size, innerW, outH, format, maxLines);
      block = BoxAnnotationStyle.MeasureBlock(g, label, fit.Size, innerW, format);
  }
  ```
  (Khối `pillH = MathF.Max(innerH, block.Height + 2f * padY)` + clamp `pillY` giữ nguyên — pill nở tự nhiên khi `need ≤ outH`.)

- [ ] **Step 1: Viết test failing** — `BoxAnnotationStyleTests`:
  - Sửa helper `Fit(...)` còn `(text, innerWidth, innerHeight, boxHeight)` (bỏ `maxLines`/`shrink`).
  - Thay `Fit_LongTranslation_WrapsToTwoLines` → `Fit_LongTranslation_FitsInnerHeight`: cùng box 188×49,6 / boxHeight 57,6 → `Assert.True(fit.Fits)`; `Assert.True(fit.Block.Height + 2f * PadY(fit.Size) <= 49.6f + 0.5f)`; bỏ assert `Lines == 2`.
  - Thêm `Fit_TallBox_UsesVerticalRoom`: text dài 146 ký tự (chép nguyên văn chuỗi trong spec §1), box 360×210 → `Fit(text, 356f, 202f, 210f)`; assert `Fits`, `Size >= 22`, `Lines >= 5`, `Block.Height >= 0.8f * 202f` (đo 2026-11-12: 24px / 6 dòng / 195px).
  - Thêm `Fit_TinyBox_KeepsFloorAndReportsNotFitting`: box 240×30, text 65 ký tự → `Fit(text, 236f, 22f, 30f)`; assert `!Fits` && `Size == (int)BoxAnnotationStyle.MinFontSize`.
  - Thêm `Fit_WideShortBox_StaysSingleLine`: box 1100×40, text 146 ký tự → `Fit(text, 1096f, 32f, 40f)`; assert `Fits` && `Lines <= 2`.
  - Giữ `Fit_FontSizeGrowsWithBoxHeight`, `BaseFontSize_ScalesWithBoxHeightAndClamps`; thay `Fit_WithoutShrink_KeepsBaseSizeAndTruncates` → `TruncateToFit_CutsToLineBudget` (gọi `TruncateToFit` trực tiếp, size 12, maxLines 1 → `EndsWith("…")`, ngắn hơn input).
  - `FrameAnnotatorTests`: thêm `Annotate_TinyBox_LongText_PillGrowsBelowBox` — frame 640×360 trắng, `AnnotBox(0.1, 0.1, 0.35, 0.06, longText, longText)` (box px: 64..288 × 36..57,6); scan `y 36..140, x 60..300` ghi `maxDarkY` của `IsDark`; assert `maxDarkY > 60` (pill nở qua đáy box; đo kỳ vọng ~69); sửa comment "pill maxLines 4" ở `:205`.
- [ ] **Step 2: Chạy test — xác nhận fail** (`BoxAnnotationStyleTests`, `FrameAnnotatorTests`).
- [ ] **Step 3: Implement** — `BoxAnnotationStyle.cs`: sửa `Fit`/`Measure` theo Interfaces + cập nhật doc comment song ngữ (bỏ `<param name="maxLines">`/`shrink`, mô tả "lớn nhất vừa lòng box; sàn MinFontSize; pill nở khi bất khả"). `FrameAnnotator.cs` `DrawBoxes`: xoá `int maxLines = box.IsParagraph ? 4 : 2;` và tham số tương ứng; chèn guard viewport ngay sau `if (!fit.Fits)` cũ — thay khối `if (!fit.Fits) { label = TruncateToFit(..., maxLines); ... }` bằng `if (need > outH) { ... }`.
- [ ] **Step 4: Chạy test — pass** + `dotnet build "ReTran Project.slnx"` 0 error (bản App chưa đổi sẽ **vẫn compile** vì Fit bản App còn chữ ký cũ — xem Task 2).
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.Core/Serve/BoxAnnotationStyle.cs" "ReTran.Core/Serve/FrameAnnotator.cs" "ReTran.Tests/BoxAnnotationStyleTests.cs" "ReTran.Tests/FrameAnnotatorTests.cs"
  git commit -m "feat(core): fit translated text by the full box interior and grow the pill"
  ```

---

### Task 2: App — chép nguyên bản + `Win32OverlayHost` + test đồng bộ/render

**Files:**
- Modify: `ReTran.App/Services/OverlayWindow/BoxAnnotationStyle.cs` (chép y hệt Task 1, giữ `internal`)
- Modify: `ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs` (`DrawBoxes` :509-569 — chỉ khối này + doc của nó; file cũng chứa host menu nổi, không đụng phần menu)
- Modify: `ReTran.Tests/Overlay/Win32OverlayHostRenderTests.cs` (+1 test)
- Create: `ReTran.Tests/BoxAnnotationStyleSyncTests.cs`

**Interfaces:**
- Consumes: bản Core đã đổi (Task 1) làm nguồn chép.
- Produces: bản App của `Fit(Graphics, string, float, float, float, StringFormat, float)`; guard viewport trong `DrawBoxes` dùng `viewportHeight` thay `outH`:
  ```csharp
  float need = block.Height + 2f * padY;
  if (need > viewportHeight) { int maxLines = Math.Max(1, (int)MathF.Floor((viewportHeight - 2f * padY) / BoxAnnotationStyle.GetFont(fit.Size).GetHeight(g))); text = BoxAnnotationStyle.TruncateToFit(...); block = ...; }
  ```
  `maxFontSize: BoxAnnotationStyle.MaxFontSize * ui` giữ nguyên.

- [ ] **Step 1: Viết test failing** — `Win32OverlayHostRenderTests`:
  - `DrawBoxes_TinyBox_LongText_PillGrowsBelowBox`: `Render(new DrawBox(100, 40, 240, 24, longText))` (longText = chuỗi 146 ký tự của spec §1); scan `y 36..170, x 100..340` ghi `maxDarkY`; assert `maxDarkY > 66` (đáy box = 64). Giữ `DrawBoxes_FontScalesWithBoxHeight` + `DrawBoxes_DrawsTealTicksAndPillInsideBox` nguyên assert (đã kiểm: pill vẫn ≥ innerH nên vẫn xanh).
  - `BoxAnnotationStyleSyncTests` (file mới, namespace `ReTran.Tests`, alias `Core` = `ReTran.Core.Serve.BoxAnnotationStyle`, `App` = `ReTran_App.Services.OverlayWindow.BoxAnnotationStyle` — cả hai internal, ReTran.Tests đã có InternalsVisibleTo hai bên):
    - `ConstantsMatch`: `Assert.Equal` cho `Inset`, `MinFontSize`, `MaxFontSize`, `FontHeightRatio`, `PadX(20)`, `PadY(20)`.
- [ ] **Step 2: Chạy test — xác nhận fail** (`Win32OverlayHostRenderTests`, `BoxAnnotationStyleSyncTests`).
- [ ] **Step 3: Implement** — chép `Fit`/`Measure`/doc từ bản Core sang bản App (giữ nguyên namespace/`internal`); sửa `DrawBoxes` (xoá `maxLines`, guard viewport). `Fit_FontSizeGrowsWithBoxHeight`... helper trong test không đổi. Cập nhật comment "tối đa hai dòng wrap — bốn dòng..." ở doc `DrawBoxes` :496-503 cho khớp hành vi mới.
- [ ] **Step 4: Chạy test — pass** + full suite ≥ 742 + ~9 test mới/task nhóm này.
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.App/Services/OverlayWindow/BoxAnnotationStyle.cs" "ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs" "ReTran.Tests/Overlay/Win32OverlayHostRenderTests.cs" "ReTran.Tests/BoxAnnotationStyleSyncTests.cs"
  git commit -m "feat(app): mirror box-interior text fit in the Win32 overlay host"
  ```

---

### Task 3: Trang OBS — `overlay.js` fit mới + cap theo canvas + pill nở

**Files:**
- Modify: `ReTran.App/wwwroot/overlay-feed/overlay.js` (:10-54)
- Modify: `ReTran.App/wwwroot/overlay-feed/overlay.css` (:24-39)

**Interfaces:**
- Consumes: hằng số của 2 bản C# (9/40/0.5/0.35·px/0.12·px) — giữ đúng giá trị.
- Produces (file tĩnh, không build — chép đúng khối này):
  ```js
  var MIN_FONT_PX = 9, MAX_FONT_PX = 40, FONT_HEIGHT_RATIO = 0.5;
  function padX(px) { return Math.max(4, px * 0.35); }
  function padY(px) { return Math.max(2, px * 0.12); }
  // 1 element đo tạo 1 lần: position absolute, visibility hidden, left -10000px, white-space pre-wrap,
  // word-wrap break-word, font-family "Segoe UI"; đo offsetHeight với width = innerW - 2*padX, fontSize = px, line-height 1.15.
  function fits(text, px, innerW, innerH) { return measureHeight(text, px, innerW) + 2 * padY(px) <= innerH + 0.5; }
  function fitFont(text, innerW, innerH, boxHpx) {
    var cap = Math.max(20, MAX_FONT_PX * Math.max(0.5, window.innerWidth / 1280));
    var high = Math.round(Math.max(12, Math.min(cap, FONT_HEIGHT_RATIO * boxHpx)));
    if (fits(text, high, innerW, innerH)) return high;
    var low = MIN_FONT_PX, best = low, from = low + 1, to = high - 1;
    while (from <= to) { var mid = (from + to) >> 1; if (fits(text, mid, innerW, innerH)) { best = mid; from = mid + 1; } else { to = mid - 1; } }
    return best;
  }
  ```
  `renderBox`: `pxWidth = box.w * window.innerWidth`, `pxHeight = box.h * viewHeight`; `fontPx = fitFont(text, pxWidth, pxHeight, pxHeight)`; pill: `style.fontSize = fontPx + "px"`; `style.padding = padY(fontPx) + "px " + padX(fontPx) + "px"`.
  CSS `.box .pill`: bỏ `inset: 0` → `left: 0; top: 0; right: 0; bottom: auto; min-height: 100%;` (nở xuống khi chữ dài; `#stage` vẫn `overflow: hidden` theo canvas).

- [ ] **Step 1: Sửa `overlay.css` + `overlay.js`** theo Interfaces (giữ IIFE/ES5 style hiện có, không external resource).
- [ ] **Step 2: Kiểm tra cú pháp** — `node --check "ReTran.App/wwwroot/overlay-feed/overlay.js"` → exit 0.
- [ ] **Step 3: Checklist tay — DEFERRED TO CONTROLLER** (subagent không thấy desktop; mở app, Start pipeline, **bật công tắc `Lớp phủ`** — mới từ merge 9934b51: tắt thì feed gửi `boxes:[]` — rồi mở `http://127.0.0.1:17863/overlay` (URL/port thật trên thẻ OBS trang Home) bằng Chrome):
  1. Box gộp nhiều dòng: chữ to hơn rõ và dùng gần hết bề dọc box (không còn nửa dưới trống như trước).
  2. Box nhỏ + bản dịch dài: pill nở xuống dưới đáy box, chữ đọc được, **không bị cắt** trên/dưới (khác hẳn trước).
  3. Resize cửa sổ Chrome 1920×1080: box cao nhận font tới ~60px (cap scale theo canvas); 1280×720: cap 40px.
  4. Box 1 dòng thấp: không nhảy cỡ so với trước ngoài dung sai vài px.
  5. `boxes: []` (Stop pipeline hoặc text biến mất) → stage sạch, pill nở cũ không sót.
- [ ] **Step 4: Commit**
  ```bash
  git add "ReTran.App/wwwroot/overlay-feed/overlay.js" "ReTran.App/wwwroot/overlay-feed/overlay.css"
  git commit -m "feat(overlay): fit OBS browser-source text to the box and scale the font cap by canvas"
  ```

---

### Task 4: Verification cuối + annotbench + ghi perf

**Files:**
- Modify: `Docs/performance.md` (mục ngắn "overlay text fit")
- No code.

- [ ] **Step 1: Build + test full** — `dotnet build "ReTran Project.slnx"` (0 error); `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` ⇒ toàn bộ PASS, ≥ 742 + ~10 test mới.
- [ ] **Step 2: Chạy annotbench** — `dotnet run -c Debug --project Tools/Perf/annotbench` ⇒ annotate ≤ 40 ms/khung (benchmark code thật, frame 1920×1080); đối chiếu số cũ trong `Docs/performance.md` §annotate.
- [ ] **Step 3: Smoke có tương tác — DEFERRED TO CONTROLLER** (đối chiếu bảng §1 spec): preview trong app + overlay trên game + OBS — box gộp cao chữ to hơn/không thừa chỗ; box nhỏ chữ không bị cắt "…" nữa (pill nở); không có hồi quy màu/viền/tick.
- [ ] **Step 4: Ghi `Docs/performance.md`** — mục ngắn: công thức fit mới (vẫn binary search, số bước tương đương); số annotbench đo được; trang OBS thêm 1 phép đo layout/box (element đo tái sử dụng).
- [ ] **Step 5: Commit**
  ```bash
  git add "Docs/performance.md"
  git commit -m "docs(perf): overlay text fit measurements"
  ```

---

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** D1 → Task 1/2 (Fit mới); D2 → Task 1/2 guard + Task 3 pill nở; D3 → giữ nguyên BaseFontSize/cap (Task 1) + JS cap (Task 3); D4 → Task 3; D5 → Task 2 `BoxAnnotationStyleSyncTests` + chép nguyên bản; D6 → không task nào cần sửa cờ `paragraph` (đúng D6: giữ trên wire, không còn điều khiển layout). §4 thuật toán → Task 1 Step 3 + Task 3 Interfaces. Không có gap.
2. **Step scan:** mỗi step một hành động check được; body code chỉ ở chỗ không suy ra được (guard viewport, JS fitFont/measure, khối handler/test infra).
3. **Type consistency:** `Fit(Graphics, string, float, float, float, StringFormat, float)` nhất quán Task 1/2 + helper tests; `fit.FontSize`… `maxFontSize` chỉ còn 1 ý nghĩa (trần ban đầu); JS `fitFont(text, innerW, innerH, boxHpx)` khớp biến trong `renderBox`.
4. **Review Focus:** 5 dòng, test ghim Task 1 (`Fit_TallBox…`, `Fit_TinyBox…`, `Annotate_TinyBox…`, `Fit_LongTranslation…`, `Fit_WideShortBox…`), Task 2 (`DrawBoxes_TinyBox…`, `ConstantsMatch`), Task 3 checklist (canvas cap).
5. **Proportion:** plan gọn hơn spec; hành vi được mô tả bằng công thức + assert chính xác, không transcript renderer.
