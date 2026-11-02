# LineGrouper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gộp các dòng text OCR gần nhau thành một khối (1 box union, 1 text nối space) trước khi diff + dịch, hiển thị 1 pill cho cả khối — bật/tắt qua `AppSettings.OcrMergeLines` (App default bật) và `core.start ocr.mergeLines` (CLI default tắt).

**Architecture:** Class thuần `LineGrouper` chạy tại `OcrPipeline.SpotAsync` sau khi nhận `spots`, trước `_diff.Feed` — điểm duy nhất mà diff + mọi sink cùng đi qua. Spot gộp mang cờ `paragraph: true`; cờ truyền xuyên wire `frame.translated` → `AnnotBox`/`OverlayBox` → pill `maxLines 2→4`. Config đi đường `settings.json` → `core.start` như `ocrFps`/`captureFps`.

**Tech Stack:** C# .NET 10 (ReTran.Core + ReTran.App MAUI Blazor), xUnit v3, System.Text.Json.Nodes. Không thêm dependency.

**Spec:** `Docs/superpowers/specs/2026-11-01-ocr-line-grouping-design.md` — đọc spec trước khi implement; plan này là chi tiết thi công của spec (D1–D9).

## Global Constraints

- **Không `git add -A`.** Repo có ~97 file WIP của user. Mỗi commit chỉ `git add` đúng các file của task.
- Working tree đã có UI-fix **chưa commit** ở `ReTran.App/Components/Shared/SettingsPanel.razor` + `ReTran.App/Services/I18nStrings.cs` (sửa nhãn nút lưu, spec trước đó). Chúng thuộc cùng nhánh việc và sẽ đi kèm commit của Task 6 — không xoá, không tách riêng.
- Không push. Branch `master`.
- Doc comment C# public API: song ngữ Anh → Việt (câu đầu tiếng Anh cho IDE tooltip), theo AGENTS.md.
- Ngưỡng gộp cố định trong code (spec D4): `MaxGapRatio = 0.6`, `MaxHeightRatio = 1.35`, `MinOverlapRatio = 0.5`. Không phơi UI param.
- CLI giữ contract M3: không flag → output không đổi (không có field `paragraph`).
- Build/test lệnh chuẩn:
  - `dotnet build "ReTran Project.slnx"` — expected: 0 error (34 warning `xUnit1051` có sẵn).
  - `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<TênĐầyĐủ>"` — MTP runner chỉ match **tên fully-qualified chính xác** (verify Task 1 + Task 5): class trong namespace dùng `RetranCore.Tests.<ClassName>`; class ở global namespace (không có dòng `namespace`, ví dụ `AppSettingsServiceTests`, `PipelineControllerTests`) dùng tên trần. Khi nghi ngờ: `--list-tests`.
- Không kill process ComfyUI của user; không đụng `wwwroot/build` (output WIP).

## Review Focus

Các input/failure mode spec gợi ý mà test task phải ghim:

1. **Dòng text rỗng/whitespace-only** trong spots → không được gộp, text nối không có space đầu/cuối thừa. → test tại Task 1 (`LineGrouperTests`).
2. **Quad suy biến** (4 điểm trùng nhau / diện tích 0) hoặc spot thiếu `box`/`text` → giữ nguyên spot trong output, không throw, không gộp. → test tại Task 1.
3. **Khung lớn ~300 dòng, 2 cột** → group đúng theo cột, chạy < 5 ms (chống O(n²) vô tình). → test tại Task 1.
4. **Block đổi 1 dòng giữa 2 khung** → `new_texts` chứa text block mới hoàn chỉnh, không chứa các dòng cũ riêng lẻ. → test tại Task 2 (`OcrPipelineTests`).
5. **`ocr.mergeLines` sai kiểu** (string/number) trong `core.start` → `-32602`; JSON `null` tường minh = absent (giữ cấu hình hiện tại, nhất quán với `ocr.params: null`). → test tại Task 3 (`PipelineHostTests`).

---

### Task 1: `LineGrouper` — class thuần gộp dòng

**Files:**
- Create: `ReTran.Core/Ocr/LineGrouper.cs`
- Test: `ReTran.Tests/LineGrouperTests.cs` (mới)

**Interfaces:**
- Consumes: không (chỉ `System.Text.Json.Nodes`).
- Produces: `static class LineGrouper` với `public static JsonArray Group(JsonArray spots)` và 3 const `public const double MaxGapRatio = 0.6; MaxHeightRatio = 1.35; MinOverlapRatio = 0.5;`. Output cùng shape input: mỗi spot `{box: [[x,y]×4] pixel, text, score}`; spot gộp (≥ 2 dòng) thêm `"paragraph": true`. Task 2 gọi `LineGrouper.Group`.

- [ ] **Step 1: Viết test failing** — tạo `ReTran.Tests/LineGrouperTests.cs` (namespace `RetranCore.Tests`, xUnit v3 `[Fact]`). Helper dựng spot theo pixel:

```csharp
private static JsonObject Spot(double x, double y, double w, double h, string text, double score = 0.9) =>
    new()
    {
        ["box"] = new JsonArray(
            new JsonArray(x, y), new JsonArray(x + w, y),
            new JsonArray(x + w, y + h), new JsonArray(x, y + h)),
        ["text"] = text,
        ["score"] = score,
    };
```

Cases (tên test → assertion chính):
- `TwoLeftAlignedLines_MergeIntoOneBlock`: 2 spot `(100,100,300,24)` "HELLO" + `(100,136,300,24)` "WORLD" (gap = 12 ≤ 0.6×24) → output 1 spot: `text == "HELLO WORLD"`, `paragraph == true`, `score == min`, box union = quad của AABB `(100,100,300,60)`; không còn 2 spot riêng.
- `CenteredLines_MergeByOverlap`: dòng trên `(150,100,300,24)`, dòng dưới `(200,136,300,24)` (lệch trái 50px nhưng overlap = 250 ≥ 0.5×300) → gộp.
- `SideBySideColumns_DoNotMerge`: 2 spot cùng hàng `(100,100,200,24)` + `(600,100,200,24)` → output 2 spot, không có `paragraph`.
- `LargeVerticalGap_DoNotMerge`: gap = 30 (> 0.6×24 = 14.4) → 2 spot riêng.
- `HeightRatioAbove1p35_DoNotMerge`: cao 24 và 40 (ratio 1.67) → không gộp.
- `ThreeLineChain_MergesIntoOneBlock_ReadingOrder`: 3 dòng liên tiếp cùng cột, xen 1 spot ở cột khác giữa 2 dòng (cùng hàng với dòng 2) → output: block 3 dòng + 1 spot riêng; thứ tự output = reading order (y trước, x sau).
- `BlankTextLines_NotMerged_NoStraySpaces`: spot text `"   "` kề spot "HELLO" đủ điều kiện hình học → không gộp; output text "HELLO" không có space thừa.
- `DegenerateQuad_PassesThrough_Unchanged`: spot 4 điểm trùng `(50,50)` + 1 spot bình thường đủ điều kiện hình học với nó → cả 2 giữ nguyên, không throw, không `paragraph`.
- `MissingBoxOrText_PassesThrough`: spot thiếu key `box`, spot thiếu key `text` → giữ nguyên vị trí tương đối trong output, không throw.
- `EmptyArray_ReturnsEmpty`: `Group(new JsonArray())` → array rỗng.
- `SingleSpot_Unchanged_NoParagraphFlag`: 1 spot hợp lệ → output đúng 1 spot, **không** có key `paragraph`.
- `ManyLinesTwoColumns_GroupsPerColumn_Fast`: 300 dòng (2 cột × 150), gap đều 8px, cao 24 → đúng 2 block; đo `Stopwatch` < 5 ms.

- [ ] **Step 2: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.LineGrouperTests"`
Expected: compile error / FAIL (class `LineGrouper` chưa tồn tại).

- [ ] **Step 3: Implement `ReTran.Core/Ocr/LineGrouper.cs`**

Thuật toán (spec §4) — ghi chú triển khai:
1. Mỗi spot → AABB `(x, y, w, h)` = min/max 4 điểm quad. Spot thiếu `box` (không phải array 4 điểm số), thiếu `text`, text trim xong rỗng, hoặc box suy biến (`w < 1 || h < 1`) → **unmergeable**: giữ nguyên spot trong output nhưng không tham gia gộp.
2. Sort các spot mergeable theo `(y_top, x_left)`.
3. Quét: box mới `B` so với dòng cuối `A` của group hiện tại — đủ cả 3 điều kiện thì gộp:
   - `gapY = B.Y - (A.Y + A.H)`; OK nếu `gapY <= MaxGapRatio * Math.Min(A.H, B.H)` (không chặn dưới — overlap là OK).
   - `Math.Max(A.H, B.H) / Math.Min(A.H, B.H) <= MaxHeightRatio`.
   - `overlapX = Math.Min(A.X + A.W, B.X + B.W) - Math.Max(A.X, B.X)`; OK nếu `overlapX >= MinOverlapRatio * Math.Min(A.W, B.W)`.
4. Spot gộp: `box` = quad chữ nhật của union AABB `[[x0,y0],[x1,y0],[x1,y1],[x0,y1]]`; `text` = các dòng đã `Trim()` nối bằng `" "`; `score` = min; thêm `["paragraph"] = true`. Group 1 dòng = giữ spot gốc y nguyên (không thêm key).
5. Output: array mới, mergeable theo reading order, xen các spot unmergeable theo vị trí y của chúng (giữ ổn định — không cần đúng tuyệt đối, test chỉ yêu cầu "giữ nguyên trong output").

Doc comment class + method song ngữ; const có `<summary>` nêu nguồn ngưỡng (spec D4).

- [ ] **Step 4: Chạy test — xác nhận pass**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.LineGrouperTests"`
Expected: PASS toàn bộ.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.Core/Ocr/LineGrouper.cs" "ReTran.Tests/LineGrouperTests.cs"
git commit -m "feat(core): LineGrouper — merge nearby OCR lines into one block"
```

---

### Task 2: `OcrPipeline` — hook gộp trước diff

**Files:**
- Modify: `ReTran.Core/Ocr/OcrPipeline.cs` (constructor :23, `SpotAsync` body :101–112)
- Test: `ReTran.Tests/OcrPipelineTests.cs` (mở rộng)

**Interfaces:**
- Consumes: `LineGrouper.Group(JsonArray)` (Task 1).
- Produces: `OcrPipeline(ISidecarRpc rpc, OcrDiff diff, bool mergeLines = false)` — Task 3 truyền `spec.MergeLines`. Khi bật, reply của `SpotAsync` có `spots` đã gộp (spot gộp mang `paragraph`) và `new_texts` theo text block.

- [ ] **Step 1: Viết test failing** — thêm vào `OcrPipelineTests` (dùng `FakeSidecarRpc` + helper `SpotResult` có sẵn; helper mới cho spots có toạ độ):

```csharp
private static JsonObject SpotAt(double x, double y, double w, double h, string text) => /* như LineGrouperTests.Spot */;
private static JsonObject TwoLineResult() => new()
{
    ["spots"] = new JsonArray(
        SpotAt(100, 100, 300, 24, "HELLO"),
        SpotAt(100, 136, 300, 24, "WORLD")),
    ["elapsed_ms"] = 5,
};
```

- `SpotAsync_MergeOn_SpotsMerged_AndNewTextsAreBlocks`: `new OcrPipeline(fake, new OcrDiff(), mergeLines: true)`, result của `TwoLineResult()` → `result["spots"]` có 1 spot với `text == "HELLO WORLD"` và `paragraph == true`; `new_texts == ["HELLO WORLD"]`.
- `SpotAsync_MergeOn_SecondFrameChangedLine_RetranslatesWholeBlock`: khung 2 đổi dòng 2 thành "EARTH" → `new_texts` chứa đúng `"HELLO EARTH"`, **không** chứa `"HELLO"` hay `"EARTH"` riêng (Review Focus #4).
- `SpotAsync_MergeOff_Default_OutputUnchanged`: không truyền `mergeLines` → `spots` giữ 2 spot gốc, không có key `paragraph`, `new_texts == ["HELLO", "WORLD"]` (regression M3).

- [ ] **Step 2: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.OcrPipelineTests"`
Expected: FAIL (constructor chưa có tham số `mergeLines`).

- [ ] **Step 3: Implement trong `OcrPipeline`**

- Constructor: thêm field `private readonly bool _mergeLines;` + tham số thứ 3 `bool mergeLines = false` (default giữ mọi call site cũ compile).
- Trong `SpotAsync(JsonObject, CancellationToken)` sau khi validate `spotsArr` (:101–102):

```csharp
JsonArray effectiveSpots = _mergeLines ? LineGrouper.Group(spotsArr) : spotsArr;
obj["spots"] = effectiveSpots;
```

  rồi lấy `texts` từ `effectiveSpots` thay vì `spotsArr`. Cập nhật doc comment constructor + `<param>` song ngữ (ghi rõ: khi bật, `spots`/`new_texts` theo block gộp).

- [ ] **Step 4: Chạy test — xác nhận pass** (+ toàn class cũ)

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.OcrPipelineTests"`
Expected: PASS (cũ + mới).

- [ ] **Step 5: Commit**

```bash
git add "ReTran.Core/Ocr/OcrPipeline.cs" "ReTran.Tests/OcrPipelineTests.cs"
git commit -m "feat(core): OcrPipeline mergeLines — group spots before diff"
```

---

### Task 3: `core.start ocr.mergeLines` + CLI `--merge-lines`

**Files:**
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (`ParseStart` :560–673, `StartSpec` :1026, tạo pipeline :213)
- Modify: `ReTran.Core/Cli/OcrLoop.cs` (`OcrLoopOptions` :24, `Usage` :36, `TryParse` :47–119)
- Modify: `ReTran.Core/Cli/OcrCommands.cs` (one-shot `RunAsync` :27–69, loop :117)
- Test: `ReTran.Tests/PipelineHostTests.cs`, `ReTran.Tests/OcrLoopCliTests.cs` (mở rộng)

**Interfaces:**
- Consumes: `OcrPipeline(rpc, diff, mergeLines)` (Task 2).
- Produces: wire `core.start`: `ocr.mergeLines?: bool` (absent/null = `false`, sai kiểu → `-32602`). CLI: `--merge-lines` (flag không giá trị) cho cả `ocr <png>` và `ocr loop`; default off.

- [ ] **Step 1: Viết test failing**

`PipelineHostTests` (dùng helper `StartParams()` + `host.StartAsync(parameters, ct)` như các test `Start_*` hiện có):
- `Start_MergeLinesTrue_ParsesOk`: `parameters["ocr"] = new JsonObject { ["det"]="detA", ["rec"]="recA", ["mergeLines"]=true }` → start không ném (kiểm tra parse; hiệu ứng runtime kiểm tra ở Task 4/5).
- `Start_MergeLinesAbsent_DefaultsFalse`: `StartParams()` thường → không ném.
- `Start_MergeLinesNull_TreatedAsAbsent`: `["mergeLines"] = null` → không ném (Review Focus #5).
- `Start_MergeLinesNonBool_InvalidParams`: `["mergeLines"] = "yes"` và `= 1` → `RpcMethodException`, code `RpcErrorCodes.InvalidParams`, message chứa `"mergeLines"`.

`OcrLoopCliTests`:
- `TryParse_MergeLinesFlag_SetsOption`: `--monitor 0 --merge-lines` → `options.MergeLines == true`; không có flag → `false`.
- One-shot: test cho method parse mới (bên dưới) — args `["a.png", "--merge-lines"]` OK; `[]`, `["a.png","b.png"]`, `["--bogus"]` → error.

- [ ] **Step 2: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.PipelineHostTests"` và tương tự `OcrLoopCliTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

- `PipelineHost.ParseStart`: trong khối `if (p.TryGetPropertyValue("ocr", ...))` (:652–669), sau phần `params`: đọc `mergeLines` — absent hoặc JSON `null` → `false`; `True`/`False` → giá trị; khác → ném `-32602` `"ocr.mergeLines' must be a boolean."`. `StartSpec` thêm positional param cuối `bool MergeLines`; cập nhật record doc.
- `PipelineHost` :213: `new OcrPipeline(rpc, new OcrDiff(), spec.MergeLines)`.
- `OcrLoopOptions`: thêm `bool MergeLines = false` (param cuối, default).
- `OcrLoop.Usage`: nối `[--merge-lines]` vào cuối.
- `OcrLoop.TryParse`: thêm `case "--merge-lines": mergeLines = true; break;`; truyền vào record ở :118.
- `OcrCommands.RunAsync` (one-shot): tách parse args thành `internal static bool TryParseOneShot(string[] args, out string? input, out bool mergeLines, out string? error)` — duyệt args: `"--merge-lines"` → flag; positional đầu tiên → input; positional thứ 2 hoặc flag lạ → error. Usage line mới: `"usage: ReTran.Core ocr <input.png> [--merge-lines]"`. `RunLoopAsync` :117: `new OcrPipeline(sp, new OcrDiff(), options.MergeLines)`.

- [ ] **Step 4: Chạy test — xác nhận pass**

Run: cả 2 filter class trên.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.Core/Serve/PipelineHost.cs" "ReTran.Core/Cli/OcrLoop.cs" "ReTran.Core/Cli/OcrCommands.cs" "ReTran.Tests/PipelineHostTests.cs" "ReTran.Tests/OcrLoopCliTests.cs"
git commit -m "feat(core): core.start ocr.mergeLines + CLI --merge-lines flag"
```

---

### Task 4: Cờ `paragraph` xuyên annotate (Core)

**Files:**
- Modify: `ReTran.Core/Serve/AnnotationSnapshot.cs:15` (`AnnotBox`)
- Modify: `ReTran.Core/Serve/TranslateCycleSink.cs` (khối dựng boxes :133–155)
- Modify: `ReTran.Core/Serve/FrameAnnotator.cs` (`DrawBoxes` :100–142)
- Test: `ReTran.Tests/TranslateCycleSinkTests.cs`, `ReTran.Tests/FrameAnnotatorTests.cs` (mở rộng)

**Interfaces:**
- Consumes: spot gộp có key `"paragraph": true` (Task 1).
- Produces: `AnnotBox(double X, double Y, double W, double H, string Text, string? Translated, bool IsParagraph = false)`; wire `frame.translated` boxes[] thêm `"paragraph": bool`. Task 5 đọc field này phía App.

- [ ] **Step 1: Viết test failing**

`TranslateCycleSinkTests` (helper `Result()` có sẵn — thêm biến thể spot gộp):
```csharp
private static JsonObject MergedBlockResult() // 1 spot: box [[100,50],[400,50],[400,250],[100,250]], text "HELLO WORLD", paragraph true, new_texts ["HELLO WORLD"]
```
- `Cycle_MergedSpot_SingleBoxWithParagraphFlag`: sau `OnCycleAsync` → boxes trong `frame.translated` có 1 box với `"paragraph" == true`; `AnnotationSnapshot.TryGet()` (truyền snapshot vào `Create`) trả box `IsParagraph == true`.
- `Cycle_PlainSpot_BoxHasParagraphFalse`: spot thường → wire `"paragraph" == false`, `AnnotBox.IsParagraph == false`.

`FrameAnnotatorTests`: thêm 1 test — `Annotate` với snapshot chứa 1 paragraph box (text dài ~60 ký tự, box cao 3 dòng) chạy không throw và trả JPEG hợp lệ (`Jpeg.Length > 0`, Width/Height đúng tỉ lệ). (Không assert pixel.)

- [ ] **Step 2: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "RetranCore.Tests.TranslateCycleSinkTests"`
Expected: FAIL (`AnnotBox` chưa có param).

- [ ] **Step 3: Implement**

- `AnnotBox`: thêm positional param cuối `bool IsParagraph = false` + `<param>` song ngữ.
- `TranslateCycleSink.OnCycleAsync` trong foreach spots: đọc cờ `spot["paragraph"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.True`; gắn vào wire box (`["paragraph"] = paragraph`) và `new AnnotBox(..., paragraph)`. Cập nhật `<remarks>` class (box có thể là block gộp nhiều dòng — spec 2026-11-01).
- `FrameAnnotator.DrawBoxes`: `int maxLines = box.IsParagraph ? 4 : 2;` dùng cho cả 2 call `BoxAnnotationStyle.Fit` (:117) và `TruncateToFit` (:122).

- [ ] **Step 4: Chạy test — xác nhận pass** (+ `AnnotationSnapshotTests`, `FrameAnnotatorTests`)

Run: các filter class trên.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.Core/Serve/AnnotationSnapshot.cs" "ReTran.Core/Serve/TranslateCycleSink.cs" "ReTran.Core/Serve/FrameAnnotator.cs" "ReTran.Tests/TranslateCycleSinkTests.cs" "ReTran.Tests/FrameAnnotatorTests.cs"
git commit -m "feat(core): paragraph flag through snapshot, wire and annotator pill"
```

---

### Task 5: App — `OcrMergeLines` + wire (Core → App)

**Files:**
- Modify: `ReTran.App/Services/AppSettingsService.cs` (`AppSettings` :31–45)
- Modify: `ReTran.App/Core/PipelineController.cs` (`StartAsync` :238, `HandleFrameTranslated` :794–800)
- Modify: `ReTran.App/Core/OverlayState.cs:13` (`OverlayBox`)
- Test: `ReTran.Tests/AppSettingsServiceTests.cs`, `ReTran.Tests/PipelineControllerTests.cs` (mở rộng)

**Interfaces:**
- Consumes: wire `"paragraph"` (Task 4).
- Produces: `AppSettings.OcrMergeLines` (bool, default `true`); `core.start` params luôn mang `ocr.mergeLines`; `OverlayBox(..., bool IsParagraph = false)`. Task 6 bind checkbox + dùng cờ khi render overlay.

- [ ] **Step 1: Viết test failing**

`AppSettingsServiceTests` (pattern round-trip có sẵn :113–150):
- `OcrMergeLines_RoundTrips`: save `with { OcrMergeLines = false }` → load ra `false`; save lại `true` → load ra `true`.
- `OcrMergeLines_Absent_DefaultsTrue`: file settings không có key (hoặc `AppSettings.Default`) → `true`.

`PipelineControllerTests` (pattern fake peer bắt `peer.Calls` — xem assertion `ocrFps` :96):
- `Start_SendsMergeLinesFromSettings`: settings default → `call.Params["ocr"]!["mergeLines"]!.GetValue<bool>() == true`; với `OcrMergeLines = false` → `false`.
- `HandleFrameTranslated_ParagraphBox_FlagPropagates`: feed notification `frame.translated` có box `"paragraph": true` → `OverlayState` frame cuối có box `IsParagraph == true`; box không có key → `false`. (Dùng đường inject notification hiện có trong file test — theo pattern các test `frame.translated` đã tồn tại; nếu chưa có helper, thêm theo style của chúng.)

- [ ] **Step 2: Chạy test — xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "AppSettingsServiceTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- `AppSettings`: thêm positional param cuối `bool OcrMergeLines = true` + `<param>` song ngữ ("Merge nearby OCR lines into one block before translation; sent on core.start as ocr.mergeLines. Gộp các dòng OCR gần nhau thành một khối trước khi dịch; gửi trong core.start dưới dạng ocr.mergeLines.").
- `PipelineController.StartAsync` :238: sau `["det"]/["rec"]` thêm `ocr["mergeLines"] = settings.OcrMergeLines;` (luôn gửi — Core cũ sẽ bỏ qua key lạ).
- `OverlayBox`: thêm param cuối `bool IsParagraph = false` + doc.
- `HandleFrameTranslated` :794: đọc `bool paragraph = box["paragraph"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.True;` → truyền vào `new OverlayBox(..., paragraph)`.

- [ ] **Step 4: Chạy test — xác nhận pass** (+ cả 2 class)

Run: 2 filter class trên.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "ReTran.App/Services/AppSettingsService.cs" "ReTran.App/Core/PipelineController.cs" "ReTran.App/Core/OverlayState.cs" "ReTran.Tests/AppSettingsServiceTests.cs" "ReTran.Tests/PipelineControllerTests.cs"
git commit -m "feat(app): OcrMergeLines setting + mergeLines on core.start + paragraph box flag"
```

---

### Task 6: UI Settings + render overlay (App)

**Files:**
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor` (OCR section, `OnInitialized` :339, `ApplySettingsToOcrState` :587, `SaveOcrParamsAsync` :619–676, `RestoreOcrParamsAsync` :679)
- Modify: `ReTran.App/Services/I18nStrings.cs` (vi + en)
- Modify: `ReTran.App/Services/OverlayWindow/OverlayLayout.cs` (`DrawBox`, `RenderSubtitle` :37–54)
- Modify: `ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs` (`DrawBoxes` :232–290)

**Interfaces:**
- Consumes: `AppSettings.OcrMergeLines` (Task 5), `OverlayBox.IsParagraph` (Task 5).
- Produces: checkbox "Gộp nhiều dòng" trong mục OCR, lưu qua đường `Lưu tham số OCR`; pill overlay maxLines 4 cho block gộp. Không có interface mới cho task khác (task cuối trước verification).

- [ ] **Step 1: i18n — thêm key vi + en vào `I18nStrings.cs`** (đặt cạnh các key `deck.ocr.rhythm.*` / `deck.ocr.params.*` hiện có, cả 2 block ngôn ngữ):
  - vi: `["deck.ocr.mergeLines.label"] = "Gộp nhiều dòng thành một khối"`, `["deck.ocr.mergeLines.hint"] = "Các dòng gần nhau cùng cột được gộp thành 1 box trước khi dịch — áp dụng từ lần Start kế tiếp."`
  - en: `["deck.ocr.mergeLines.label"] = "Merge multi-line text into one block"`, `["deck.ocr.mergeLines.hint"] = "Nearby lines in the same column are merged into a single box before translation — applies from the next start."`

- [ ] **Step 2: SettingsPanel — checkbox + save path**
  - Field mới cạnh `_ocrFpsText` (:325–326): `private bool _mergeLines = true;`
  - `OnInitialized` :341–347: `_mergeLines = current.OcrMergeLines;`
  - `ApplySettingsToOcrState` :589–594: `_mergeLines = settings.OcrMergeLines;`
  - UI: trong nhánh `else` (schema đã load), **sau** khối `<details>` "Nhịp OCR" (:217) và **trước** sticky button row (:218):
    ```razor
    <div class="rt-srow">
        <span>@(L["deck.ocr.mergeLines.label"])</span>
        <input type="checkbox" @bind="_mergeLines" aria-label="@(...)" />
        <p class="rt-hint">@(L["deck.ocr.mergeLines.hint"])</p>
    </div>
    ```
  - `SaveOcrParamsAsync` :654–659: thêm `OcrMergeLines = _mergeLines,` vào `with { ... }`.
  - `RestoreOcrParamsAsync` :679–684: trước `await SaveOcrParamsAsync()` thêm `_mergeLines = AppSettings.Default.OcrMergeLines;`.

- [ ] **Step 3: Overlay render — maxLines theo cờ**
  - `OverlayLayout.cs`: `DrawBox` record thêm param cuối `bool IsParagraph = false`; `RenderSubtitle` :51 truyền `b.IsParagraph`.
  - `Win32OverlayHost.DrawBoxes` :263–270: thay 2 chỗ hard-code `maxLines: 2` bằng `int maxLines = box.IsParagraph ? 4 : 2;` (dùng cho cả `Fit` và `TruncateToFit`).

- [ ] **Step 4: Build + test toàn bộ**

Run: `dotnet build "ReTran Project.slnx"` → 0 error.
Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` (toàn suite) → PASS (chú ý: `OverlayFeedServerTests` có thể flaky dưới tải — nếu chỉ class này fail do timing, chạy lại lần hai trước khi kết luận).

- [ ] **Step 5: Commit** (kèm UI-fix nhãn nút lưu chưa commit từ trước — cùng nhánh việc, xem Global Constraints)

```bash
git add "ReTran.App/Components/Shared/SettingsPanel.razor" "ReTran.App/Services/I18nStrings.cs" "ReTran.App/Services/OverlayWindow/OverlayLayout.cs" "ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs"
git commit -m "feat(app): merge-lines checkbox in OCR settings + paragraph pill in overlay; relabel save buttons"
```

---

### Task 7: Verification cuối + ghi nhận docs

**Files:**
- Modify: `Docs/performance.md` (thêm mục ngắn)
- No new code.

**Interfaces:**
- Consumes: toàn bộ Task 1–6.
- Produces: bằng chứng build/test sạch + ghi chú perf trong repo.

- [ ] **Step 1: Build + test full**

Run: `dotnet build "ReTran Project.slnx"` → expected 0 error, 34 warning có sẵn.
Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` → expected toàn bộ PASS (số test mới: +~18 so với baseline 388).

- [ ] **Step 2: Smoke E2E (nếu app/core đang chạy được trên máy dev)**

Khởi động App, Start nguồn screen, kiểm tra preview: text nhiều dòng liền kề hiển thị 1 pill/block thay vì N pill; tắt `Gộp nhiều dòng` trong Settings → Lưu tham số OCR → Stop/Start → về N box. Ghi nhận hành vi thực tế (không bắt buộc số đo — gộp < 0.1 ms, spec §9).

- [ ] **Step 3: Ghi chú perf**

Thêm vào `Docs/performance.md` (cuối mục "OCR engine settings v2" hoặc mục mới ngắn): LineGrouper O(n log n) trên ~60 box, đo unit test 300 dòng < 5 ms; không thêm đường nóng — translation requests giảm khi màn hình ổn định. Không cần chạy annotbench lại trừ khi nghi ngờ.

- [ ] **Step 4: Commit docs**

```bash
git add "Docs/performance.md"
git commit -m "docs(perf): LineGrouper cost note"
```

---

## Self-review (đã chạy theo skill writing-plans)

1. **Spec coverage:** D1→Task 2; D2/D9→Task 1; D3→Task 1 (nối space, trim); D4→Task 1 (const + điều kiện); D5→Task 1 (so dòng cuối group); D6→Task 3 + Task 5 + Task 6; D7→Task 4 (Core) + Task 6 (overlay App); D8→Task 2 (new_texts theo block, test #4 Review Focus). §8 test → Tasks 1–5. §9 perf → Task 7. Không có gap.
2. **Step scan:** mỗi step một hành động có kết quả kiểm tra được; thuật toán gộp chỉ xuất hiện 1 lần (Task 3 của Task 1) vì signature + test không tự quyết định chain logic.
3. **Type consistency:** `LineGrouper.Group(JsonArray)→JsonArray`, `OcrPipeline(rpc, diff, mergeLines=false)`, `AnnotBox(..., IsParagraph=false)`, `OverlayBox(..., IsParagraph=false)`, `DrawBox(..., IsParagraph=false)`, `AppSettings.OcrMergeLines=true`, wire key `"paragraph"`, CLI flag `--merge-lines` — dùng nhất quán ở mọi task.
4. **Review Focus:** 5 dòng, mỗi dòng đã ghim test vào task sở hữu (1,1,1,2,3).
5. **Proportion:** plan ~ cùng cỡ spec; code block chỉ là signature/helper test + đoạn wire bắt buộc chính xác.
