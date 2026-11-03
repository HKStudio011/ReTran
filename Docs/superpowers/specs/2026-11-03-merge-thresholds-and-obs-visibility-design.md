# Thiết kế: Ngưỡng gộp dòng OCR chỉnh được + lớp phủ OBS theo cùng một công tắc

Ngày: 2026-11-03, chốt 2026-11-04 · Trạng thái: **user đã duyệt 2026-11-04** (kèm U5) · Liên quan: `Docs/superpowers/specs/2026-11-01-floating-menu-tray-design.md` (menu nổi + tray — vẫn là nguồn chốt cho phần đó), `Docs/superpowers/plans/2026-11-02-floating-menu.md` (Plan 2, sẽ được mở rộng bằng các task của tài liệu này)

## 1. Ý định và vì sao

Hai việc đang chặn trực tiếp việc chơi game + stream của user, và cả hai đều là khoảng trống phát hiện khi đối chiếu plan 1–2/11 với code:

1. **Gộp dòng OCR đang đúng một phần nhưng không chỉnh được.** Thuật toán gộp (`LineGrouper`) quyết định bằng ba ngưỡng hardcode. Màn hình game rất khác màn hình desktop: chữ nhỏ sát nhau, nhiều cột, label HUD lệch cột. Người dùng nhìn thấy khối bị gộp sai / tách sai nhưng không có cách nào siết hay nới — phải sửa code và build lại.
2. **Tắt "Lớp phủ" không tắt lớp phủ trong OBS.** App phục vụ overlay cho OBS qua SSE (`OverlayFeedServer`), nhưng server này không biết overlay đang bật hay tắt. Tắt công tắc chỉ ẩn cửa sổ desktop; OBS vẫn hiện chữ. Cùng gốc: khi Stop pipeline cũng không có ai gửi frame rỗng, nên **chữ cuối cùng đóng băng trên canvas OBS**.

User cần: nhìn thấy 3 con số và sửa được trong sheet Settings; một công tắc `Lớp phủ` điều khiển cả desktop lẫn OBS.

## 2. Goals

- **G6 — Ngưỡng gộp dòng trong Settings**: ba ô số thô, hiện open ngay dưới checkbox `Gộp nhiều dòng thành một khối` (layout đã duyệt: phương án A), có hint giải thích và dải hợp lệ; lưu vào `settings.json`; áp dụng ở lần `Start` kế tiếp.
- **G7 — OBS theo cùng một công tắc**: `Lớp phủ` tắt → OBS không còn box nào; bật → OBS hiện lại. Không có công tắc thứ hai.
- **G8 — Hết cảnh chữ đóng băng trên OBS**: Stop pipeline / Core chết / pipeline dừng đột xuất → OBS được xóa sạch sân khấu, kể cả khi `Lớp phủ` đang bật. Kết nối SSE lại không được dựng lại frame cũ.
- **G9 — Xác nhận chạy thật cho những gì Plan 1 đã merge**: các mục smoke chưa có bằng chứng (menu chuột phải trên icon, Bấm X xuống tray, bubble lỗi, `Thoát` không để mồ côi process) phải được chạy và ghi kết quả.

## 3. Non-goals

- **Không preset, không nút nhanh** cho ngưỡng (user đã chốt: đưa số).
- Không làm UI ngưỡng trong menu nổi: menu là window `WS_EX_NOACTIVATE`, không nhập liệu bàn phím (spec 2026-11-01 §3). Menu nổi chỉ giữ switch `Gộp nhiều dòng` như plan đã có.
- Không tách hai công tắc (desktop / OBS) — user đã chốt một công tắc.
- Không gate OBS theo focus/foreground: desktop overlay tự ẩn khi nguồn không foreground (`Win32AnchorTracker.IsSourceForeground`), OBS **không** làm theo điều đó.
- Không rename các const trong `LineGrouper`, không đổi thuật toán gộp, không đổi chi phí gộp.
- Không sửa `overlay.js` / `overlay.html` (đã kiểm chứng: payload `boxes: []` là đủ để trang xóa sân khấu — `overlay.js:65` `clearStage()` chạy trước khi render).
- Không thêm dependency, không thêm process/thread, không thêm RPC mới ngoài mở rộng payload `core.start` hiện có.

## 4. Anti-goals

- **Hành vi gộp mặc định thay đổi** với file `settings.json` cũ hoặc với CLI không có flag ⇒ mọi default phải đúng bằng ba const hiện tại (0.6 / 1.35 / 0.5).
- OBS bị **trắng khi đang bật** overlay (chặn nhầm) hoặc bị **nhấp nháy** vì gửi frame rỗng lặp đi lặp lại.
- Gửi frame rỗng định kỳ khi đã ẩn (traffic loopback vô ích) hoặc để hidden-state chỉ áp dụng cho client kết nối sau mà bỏ quên client đang kết nối.
- Vỡ 454 test đang pass, hoặc làm `OverlayFeedServerTests` (vốn nhạy timing) fail không do logic.
- Ba ô số cho phép nhập giá trị làm gộp mọi thứ thành một khối (ví dụ `MaxGapRatio` = 100) rồi âm thầm lưu sang `settings.json`.

## 5. Sự thật kỹ thuật đã kiểm chứng (không phải phỏng đoán)

1. `ReTran.Core/Ocr/LineGrouper.cs:15,21,27` — `MaxGapRatio = 0.6`, `MaxHeightRatio = 1.35`, `MinOverlapRatio = 0.5` là `public const double`; được dùng tại `:149`, `:152`, `:155` trong `CanMerge(a, b)`. `Group(JsonArray spots)` là static, không tham số cấu hình.
2. `OcrPipeline` gọi `LineGrouper.Group(spotsArr)` khi `_mergeLines` (constructor 3 tham số `bool mergeLines = false`), rồi lấy `texts` từ spots đã gộp. Đây là **điểm duy nhất** cần truyền ngưỡng.
3. Chuỗi cấu hình hiện có cho một tham số OCR kiểu số: `AppSettings` (record 11 tham số, `AppSettingsService.cs:33-41`) → `PipelineController.StartAsync` (`PipelineController.cs:239-240` gửi `ocr.mergeLines`) → `PipelineHost.ParseStart` parse + validate → `StartSpec` → dựng pipeline. CLI one-shot/loop: `OcrCommands` / `OcrLoop.TryParse`.
4. `AppSettings` record **11 tham số** tại thời điểm viết tài liệu này; Plan 2 Task 1 thêm 7 tham số nữa (`FloatingMenuOn` … `SourceHwnd`) ⇒ 18. Thêm 3 ngưỡng ⇒ 21.
5. `OverlayFeedServer` chỉ biết `OverlayState` (constructor `OverlayFeedServer.cs:142`), đăng ký `_state.Changed` và phát snapshot từ `_state.Latest` (`:470`). Không có bất kỳ khái niệm bật/tắt/focus nào.
6. `OverlayState.Publish` là **chỗ duy nhất** ghi `_latest`, và nó **bỏ khung hình khi khóa đang bận** (`OverlayState.cs:58` `if (!Monitor.TryEnter(_gate)) return;`). ⇒ dựa vào việc publish một frame rỗng để "xóa OBS" là không đáng tin: lệnh xóa có thể bị bỏ.
7. `OverlayState.Publish` chỉ được gọi từ `PipelineController.HandleFrameTranslated` (`PipelineController.cs:808`). Không có đường nào publish frame rỗng khi Stop ⇒ OBS giữ nguyên frame cuối (và reconnect còn nhận lại frame đó qua snapshot ở mục 5.5).
8. Trang OBS (`ReTran.App/wwwroot/overlay-feed/overlay.js:56-73`): mỗi event `data:` là clear stage rồi render lại toàn bộ `payload.boxes || []`. Nên `boxes: []` **đã là** "xóa sạch", không cần đổi JS.
9. `AppSettingsService.Save` bắn event `Changed`; `OverlayWindowService.SetEnabled` luôn persist `OverlayOnGame` (`OverlayWindowService.cs:120`), kể cả khi bật/tắt từ tray hay từ menu nổi ⇒ đọc cờ từ settings là đủ để mọi surface toggle đều được tính.
10. `PipelineController` có hai đường báo dừng: `StateChanged` (Stop do người dùng bấm — chỉ nổ đường này) và `PipelineStateChanged(false, …)` (dừng đột xuất). Một gate trạng thái chạy/dừng phải nghe **cả hai** (ruling đã có trong Plan 2 Task 2 cho `RunStats`).
11. `OverlayFeedServer` được đăng ký singleton trong `MauiProgram.cs:37` và inject vào `Home.razor:8`; nó chạy suốt vòng đời app, không phụ thuộc pipeline chạy hay dừng.
12. **Không có test component/bUnit** nào trong `ReTran.Tests` (grep `SettingsPanel|bUnit` trong `ReTran.Tests/` → 0 kết quả; component `ReTran.App/Components/Shared/SettingsPanel.razor` có thật và đang giữ `SaveOcrParamsAsync`/`RestoreOcrParamsAsync`/`TryParseOcrFps:781`). Toàn bộ test App là test service thuần. ⇒ logic parse/chuẩn hóa 3 ô số phải nằm ở một helper thuần để test được, không nằm trong `.razor`.
13. **`OverlayState` không có API xóa `_latest`**, và không ai xóa khi Stop ⇒ frame cuối của phiên trước vẫn còn trong state sau khi dừng pipeline.
14. **Hai đường dừng không loại trừ nhau**: Stop do người dùng chỉ nổ `StateChanged` (`PipelineController.cs:275,291`); Core chết / dừng đột xuất nổ **liền nhau cả** `PipelineStateChanged(false, …)` (`:627-628`) và `StateChanged` (`:824-825`). ⇒ listener phải re-evaluate theo **edge của `ShouldShowObs`**, không theo số lần event, nếu không sẽ phát hai payload xóa cho một lần dừng.

## 6. Hình thái và các quyết định

### 6.1 User đã chốt

- **U1.** Đưa **3 con số thô** ra Settings, không preset.
- **U2.** Layout **A**: 3 hàng số để open ngay dưới checkbox (không gập trong `<details>`).
- **U3.** **Một công tắc**: tắt `Lớp phủ` là OBS cũng ẩn theo.
- **U4.** Phạm vi đợt làm: **toàn bộ Plan 2 (menu nổi) + hai việc trên**, gộp thành một plan chạy một mạch.
- **U5.** Stop pipeline ⇒ **OBS xóa sạch** (không giữ lại chữ của phiên cũ). App thoát / update restart ⇒ **không xóa gì**, chữ đóng băng trên scene tới lần bật app sau. Nói cách khác: việc xóa tự động chỉ gắn với vòng đời pipeline và với công tắc `Lớp phủ`.

### 6.2 Quyết định của tôi (user gạch nếu không đồng ý)

- **K1. Tên trên wire, phẳng, cùng họ với `ocr.mergeLines`:** `ocr.mergeGapRatio`, `ocr.mergeHeightRatio`, `ocr.mergeOverlapRatio`. Thiếu hoặc `null` ⇒ mặc định bằng const hiện tại ⇒ hành vi cũ không đổi. Sai kiểu hoặc ngoài dải ⇒ `-32602` với message nêu tên key (theo khuôn `ocr.mergeLines` đã có).
- **K2. Dải hợp lệ** (chặn ở cả App lẫn Core, cùng một dải): `mergeGapRatio 0.1–2.0`, `mergeHeightRatio 1.0–3.0`, `mergeOverlapRatio 0.0–1.0`. App chặn trước để báo lỗi tại chỗ (dòng trạng thái cạnh nút Lưu), Core chặn lại lần nữa để `core.start` thủ công/CLI không gửi được giá trị vô nghĩa.
- **K3. `AppSettings` thêm 3 tham số cuối**: `double OcrMergeGap = 0.6`, `double OcrMergeHeight = 1.35`, `double OcrMergeOverlap = 0.5`. Thêm **một lần** cùng lúc với 7 tham số của Plan 2 Task 1 (một commit đổi chữ ký record một lần, không hai).
- **K4. Core nhận ngưỡng qua một struct bất biến**: `readonly record struct MergeThresholds(double GapRatio, double HeightRatio, double MinOverlapRatio)` với `static readonly MergeThresholds Default` = (0.6, 1.35, 0.5). `LineGrouper.Group(JsonArray spots, MergeThresholds t)` (giữ overload 1 tham số = dùng `Default`, để không phải sửa mọi call site/test cũ); `CanMerge` đọc từ tham số thay vì const. `OcrPipeline` thêm tham số `MergeThresholds? merge = null`.
- **K5. CLI**: `--merge-gap <x>`, `--merge-height <x>`, `--merge-overlap <x>` cho cả `ocr <png>` và `ocr loop`; không có flag ⇒ default. Lý do có flag: đo nhanh trên ảnh tĩnh mà không phải mở app.
- **K6. App không tính lại ngưỡng nào** — nó chỉ là vận chuyển. Toàn bộ thuật toán vẫn ở Core (theo AGENTS.md: không layer nào khác sở hữu engine).
- **K7. Parse + chuẩn hóa 3 ô số nằm trong một helper thuần test được**: `ReTran.App/Services/OcrMergeThresholds.cs` — chứa constant dải, `TryParse(string, out double)` (chấp nhận dấu chấm **và** dấu phẩy thập phân vì sheet có thể hiển thị theo culture), `IsInRange`, và `Restore` trả bộ ba mặc định. `SettingsPanel.razor` gọi helper này; vi phạm dải ⇒ `_ocrStatusText` + `_ocrStatusError = true`, **không** gọi `Save`.
- **K8. Gate hiển thị OBS là một singleton mới `OverlayVisibility` (`ReTran.App/Core/`)**: `bool ShouldShowObs => settings.Load().OverlayOnGame && pipeline.IsRunning`, expose `event Action? Changed`, tự re-evaluate khi `AppSettingsService.Changed`, `PipelineController.StateChanged` hoặc `PipelineController.PipelineStateChanged` nổ (cả hai đường — mục 5.10). Không suy ra từ focus/foreground.
- **K9. Đường xóa OBS đi thẳng qua `OverlayFeedServer`, không qua `OverlayState.Publish`** (lý do: mục 5.6 — publish có thể bị bỏ khi khóa bận). `OverlayFeedServer` subscribe `OverlayVisibility.Changed`: khi transition → ẩn, enqueue **một** payload `{"seq":n,"boxes":[]}` cho mọi client đang kết nối; khi transition → hiện, enqueue snapshot theo K10. Trong lúc ẩn, các event `OverlayState.Changed` **không** được broadcast (chặn ở gate, không tốn work). `OverlayVisibility.Changed` là **edge-triggered theo giá trị `ShouldShowObs`** (mục 5.14) ⇒ một lần dừng chỉ đúng một payload xóa.
- **K10. Snapshot chỉ được dựng từ frame còn "tươi": `OverlayState.Clear()`**. Thêm `void Clear()` vào `OverlayState` (lấy lock **chặn**, không `TryEnter` — Clear là hành động một lần do người dùng/phái dừng, không phải khung hình đến dồn; khác lý do drop-frame ở 5.6) đặt `_latest = null` và nổ `Changed`. `Clear()` được gọi khi pipeline dừng. Quy tắc snapshot ở mọi chỗ (`ServeEvents` và transition → hiện): `_latest is null` ⇒ `boxes: []`. Hệ quả đã chủ đích:
  - **Start lại sau Stop ⇒ OBS trống cho tới frame mới đầu tiên**, không dựng lại đám chữ của phiên trước (builder-check 2026-11-03 chỉ ra K9 bản đầu tự mâu thuẫn với G8; chốt lại ở đây).
  - Client kết nối lúc đang ẩn hoặc sau khi dừng ⇒ snapshot rỗng (bịt mục 5.5).
  - Toggle `Lớp phủ` tắt→bật **trong lúc pipeline đang chạy** ⇒ snapshot frame hiện tại gửi ngay (chữ vẫn còn trên màn hình game, đây là hành vi đúng).
- **K11. Không sửa `overlay.js`** (mục 5.8) và không thêm field `visible` vào payload — ít bề mặt wire hơn, và trang cũ trong OBS đang chạy vẫn hiểu `boxes: []`.
- **K12. Dừng pipeline là một transition ẩn** (vì `IsRunning` đổi) ⇒ dùng chung cơ chế K9 + `Clear()` của K10, không cần code riêng cho G8.
- **K13. Hint/nhãn tiếng Việt nói bằng ngôn ngữ đời thường**, không phải tên biến: `Khoảng cách dòng tối đa` / `Chênh chiều cao chữ` / `Chồng ngang tối thiểu`, hint kèm ví dụ quy ra pixel ("chữ cao 24 px → cách quá 14 px là không cùng khối"). Tên `MaxGapRatio`… chỉ sống trong code/wire.
- **K14. Thứ tự làm trong plan**: ngưỡng gộp (App→Core→UI) **trước**, rồi gate OBS, rồi mới đến các task menu nổi của Plan 2, smoke cuối cùng gộp một lần. Lý do: hai việc user cần ngay xong sớm, và menu nổi là phần dễ vỡ nhất, đặt cuối để nếu phải dừng giữa chừng thì hai việc kia đã xong và đã commit.
- **K15. Xung đột file**: `AppSettingsService.cs`, `I18nStrings.cs`, `MauiProgram.cs`, `SettingsPanel.razor` đều bị cả Plan 2 lẫn phần này chạm ⇒ mỗi file sửa **một lần** trong plan, ở task của phần này nếu Plan 2 cũng cần (xem §8).

## 7. Hành vi chi tiết

### 7.1 Sheet Settings (mục OCR)

- Checkbox `Gộp nhiều dòng thành một khối` giữ nguyên vị trí.
- Ngay dưới nó, 3 hàng `rt-srow` (nhãn trái + `input type=number` phải, `rt-mono`), mỗi hàng có `.rt-hint`:
  - `Khoảng cách dòng tối đa` = `0.6` — "Hai dòng cách nhau quá 0,6 × chiều cao chữ thì không cùng khối. 0,1–2,0. Chữ cao 24 px → quá ~14 px là tách."
  - `Chênh chiều cao chữ` = `1.35` — "Hai dòng lệch chiều cao quá 1,35 lần thì không gộp. 1,0–3,0. 1,0 = chỉ chữ cùng cỡ."
  - `Chồng ngang tối thiểu` = `0.5` — "Hai dòng phải chồng lên nhau ít nhất 0,5 chiều rộng dòng nhỏ hơn. 0–1. Giảm = cho phép lệch cột (text căn giữa)."
- Ba ô **disabled khi checkbox tắt** (không có nghĩa gì khi gộp tắt), và bật lại khi tick.
- Nút `Lưu tham số OCR` lưu cả 3 số cùng `_mergeLines` (một lần Save, không nhiều lần); `Về mặc định` đưa cả ba về 0.6 / 1.35 / 0.5.
- Giá trị ngoài dải hoặc không parse được ⇒ dòng trạng thái đỏ cạnh nút Lưu, không Save, không gửi `core.start`.
- Hiệu lực: ghi `settings.json`, gửi trong `core.start` **kế tiếp** ⇒ hint chung "áp dụng từ lần Start kế tiếp" (dùng lại mẫu chữ đã có trong sheet).

### 7.2 Chuỗi cấu hình

```
SettingsPanel (3 ô số) → AppSettings.OcrMerge{Gap,Height,Overlap}
  → PipelineController.StartAsync: ocr["mergeGapRatio"|"mergeHeightRatio"|"mergeOverlapRatio"]
  → PipelineHost.ParseStart: validate kiểu + dải → StartSpec
  → new OcrPipeline(rpc, new OcrDiff(), spec.MergeLines, new MergeThresholds(...))
  → LineGrouper.Group(spots, thresholds)
```

CLI: `ReTran.Core ocr frame.png --merge-gap 0.4 --merge-overlap 0.3` và `ocr loop … --merge-height 2`.

### 7.3 Lớp phủ OBS (một công tắc)

| Bối cảnh | Desktop overlay | OBS |
|---|---|---|
| `Lớp phủ` bật, pipeline chạy | hiện theo anchor/foreground (như hiện tại) | hiện box theo frame mới |
| `Lớp phủ` tắt | ẩn (`SetEnabled` như hiện tại) | **một** payload `boxes: []` rồi im lặng |
| `Lớp phủ` bật lại (pipeline vẫn chạy) | hiện lại | snapshot frame hiện tại gửi ngay cho mọi client |
| Stop / Core chết, `Lớp phủ` vẫn bật | ẩn (như hiện tại) | **một** payload `boxes: []` + `OverlayState.Clear()` (hết đóng băng — G8) |
| **Start lại sau Stop** | hiện lại | **trống cho tới frame mới đầu tiên** — không dựng lại chữ của phiên trước (K10) |
| OBS mở Browser Source lúc đang ẩn / sau khi dừng | — | snapshot rỗng, không dựng lại frame cũ |
| Game mất focus, `Lớp phủ` bật | desktop tự ẩn | **vẫn hiện** (K8) |
| App thoát (`Thoát` ở tray, hoặc update restart) | — | **⚠ để ngỏ, xem §7.4** |

### 7.4 App thoát khi OBS đang hiện chữ — ✅ user chốt (2026-11-04): **chấp nhận đóng băng**

`OverlayFeedServer.DisposeAsync` (`:243-290`) chủ đích hủy writer và abort connection, **không flush hàng đợi** ⇒ khi app tắt thì server tắt, OBS không bao giờ nhận `boxes: []`, và EventSource của Browser Source reconnect vào một server không còn tồn tại. **Không làm gì thêm**: chữ cuối cùng của buổi phát nằm lại trên scene OBS cho tới lần bật app sau. User chỉ muốn việc xóa xảy ra khi **chính họ** tắt lớp phủ. Không có task flush-trước-thoát.

### 7.4 Bằng chứng chạy thật (G9)

Chạy app thật và ghi kết quả vào `Docs/performance.md` hoặc mục verification của plan: menu chuột phải trên tray icon (3 mục, checkmark theo lớp phủ), chuột trái mở lại app, Bấm X xuống tray mà pipeline vẫn dịch, bubble lỗi khi app ẩn, `Thoát` không để `ReTran.Core`/python mồ côi, và OBS hiện/ẩn đúng theo công tắc.

## 8. Thứ tự và xung đột với Plan 2

Plan 2 (`2026-11-02-floating-menu.md`) Task 1 sửa `AppSettingsService.cs` (+7 tham số), Task 7 sửa `I18nStrings.cs`, Task 9 sửa `MauiProgram.cs`. Plan này cần cùng các file đó. Chốt:

- **Task A (mở đầu Plan 2)**: thêm **10** tham số vào `AppSettings` một lần (7 của Plan 2 + 3 ngưỡng) — thay thế Task 1 cũ.
- Phần ngưỡng + gate OBS chèn thành Task trước các task menu nổi (K14).
- `I18nStrings.cs`: mỗi lần chạm phải đọc lại file trước (kế hoạch cũ đã ghi rule này).
- `MauiProgram.cs`: Plan 2 Task 9 đăng ký DI menu + `RunStats`; phần này đăng ký `OverlayVisibility` + wire gate vào `OverlayFeedServer` — gộp thành **một** task wiring DI để không sửa file hai lần.
- `OverlayFeedServer` không nằm trong danh sách file Plan 2 sửa ⇒ không xung đột, nhưng `OverlayFeedServerTests` (nhạy timing) phải chạy lại hai lần nếu fail.

## 9. Để ngỏ cho builder (quyết ở bước plan, không cần user)

- Tên chính xác file/class helper parse (K7 nêu yêu cầu, không ép tên).
- Có tách `MergeThresholds` ra file riêng trong `ReTran.Core/Ocr/` hay đặt cùng `LineGrouper.cs`.
- Cách `OverlayFeedServer` nhận gate: constructor `Func<bool>` + event, hay nhận thẳng interface `IOverlayVisibility` (K8/K9 chốt hành vi, không chốt chữ ký).
- Có thêm `--merge-lines` kèm dải vào `Usage` text hay không (có, nếu không vỡ test usage string hiện có).
- Số đo perf cụ thể cho §11 (không bắt buộc chạy annotbench lại).

## 10. Test strategy

**Thuần, không pixel, không cần desktop:**
- `LineGrouperTests` (mở rộng): ngưỡng truyền vào thay đổi ranh giới gộp — gap đúng bằng `gap × minH` thì gộp, lớn hơn thì không; `height = 3.0` cho gộp chữ to với chữ nhỏ; `overlap = 0` cho gộp hai dòng không chồng nhau; default overload ≡ hành vi cũ (ghim 0.6/1.35/0.5).
- `PipelineHostTests`: ba key parse đúng; `null`/thiếu ⇒ default; sai kiểu ⇒ `-32602`; ngoài dải ⇒ `-32602` message nêu tên key.
- `OcrLoopCliTests`: ba flag parse, giá trị, và lỗi khi thiếu giá trị.
- `AppSettingsServiceTests`: round-trip 3 số; file cũ thiếu key ⇒ đúng default.
- `PipelineControllerTests`: `core.start` mang 3 số từ settings; settings default ⇒ đúng 0.6/1.35/0.5.
- `OcrMergeThresholdsTests` (mới): parse dấu chấm/dấu phẩy, ngoài dải bị chặn, `Restore` trả default.
- `OverlayVisibilityTests` (mới): 4 tổ hợp (overlay on/off × running/stopped); toggle overlay ⇒ `Changed` nổ một lần; Stop do người dùng (`StateChanged`) và dừng đột xuất (`PipelineStateChanged`) đều làm `ShouldShowObs = false`.
- `OverlayFeedServerTests` (mở rộng): đang ẩn ⇒ client mới nhận snapshot rỗng; đang hiện rồi chuyển ẩn ⇒ đúng **một** event `boxes: []` rồi không còn event box; bật lại khi pipeline **đang chạy** ⇒ snapshot frame hiện tại; dừng do Core chết (nổ cả hai event — mục 5.14) ⇒ vẫn đúng **một** payload xóa; đảm bảo không regress các test hiện có.
- `OverlayStateTests` (mở rộng): `Clear()` đặt `Latest = null` và nổ `Changed` một lần; feed dựng snapshot sau `Clear()` ⇒ `boxes: []` (ghim hành vi "Start lại sau Stop ⇒ OBS trống").

**Chạy thật (ghim vào task verification, không phải subagent tự kiểm):** checklist §7.4 + hai mục: bật/tắt `Lớp phủ` khi OBS đang mở Browser Source; Stop pipeline khi OBS đang hiện chữ.

## 11. Performance

- Ngưỡng: `LineGrouper` chỉ đổi nguồn giá trị (const → struct bất biến), **không đổi số phép tính**; không thêm allocation trên đường OCR (struct đi theo value, một instance mỗi chu kỳ). Chi phí gộp giữ nguyên con số đã ghi ở `Docs/performance.md:921` (300 dòng < 5 ms).
- Gate OBS: một phép đọc bool mỗi lần broadcast; khi ẩn thì **bỏ hẳn** broadcast ⇒ traffic SSE loopback giảm khi overlay tắt. Payload xóa: một lần mỗi transition.
- `OverlayVisibility`: không timer, không poll; chỉ event-driven.
- Ngân sách `AGENTS.md` (UI/overlay ≤ 10–15 fps, total < 8% CPU) không đổi; ghi chú lại vào `Docs/performance.md` khi xong.

## 12. Doc comment

Theo `AGENTS.md`: XML doc song ngữ Anh → Việt cho mọi public API mới (`MergeThresholds`, overload `LineGrouper.Group`, `OcrMergeThresholds`, `OverlayVisibility`, tham số mới của `AppSettings`/`OcrPipeline`/`OverlayFeedServer`), `<param>` cho từng tham số mới, và cập nhật doc của `OverlayFeedServer` (remarks về wire `boxes: []` khi ẩn) + `TranslateCycleSink`/`LineGrouper` nếu câu "ngưỡng cố định" trở nên sai.
