# Thiết kế: LineGrouper — gộp text nhiều dòng thành một khối trước khi dịch

- Ngày: 2026-11-01
- Trạng thái: **đề xuất — chờ user duyệt** (chưa viết code)
- Ngữ cảnh: sau nhánh OCR engine settings (spec `2026-10-26-...`), PP-OCR det phát hiện theo dòng nên mỗi dòng text là một spot riêng. Subtitle 2 dòng, đoạn văn, list item bị wrap → dịch từng dòng rời (mất ngữ cảnh) và preview/overlay rối nhiều pill nhỏ.

## 1. Vấn đề

- Không có tham số engine nào trong 27 param hiện tại gộp được nhiều dòng: det là detector theo dòng; `text_det_unclip_ratio` chỉ nới box quanh **từng** dòng, `limit_side_len` hạ quá thấp có thể dính 2 dòng nhưng không kiểm soát được.
- Cần một tầng gộp ở phía Core, sau OCR, trước diff + dịch.

## 2. Mục tiêu / Non-goals

**Mục tiêu**
- Gộp các dòng cùng khối thành **một đơn vị**: 1 text (nối bằng space), 1 box union, 1 pill — cho cả preview annotate lẫn overlay trên game (user chốt: "một box/pill cho cả khối").
- Bật/tắt được: App mặc định **bật**, CLI mặc định **tắt** (giữ nguyên contract M3 của `ocr spot`/`ocr loop`).
- Không làm chậm pipeline: gộp chạy sau `ocr.spot`, O(n log n) trên ~60 box.

**Non-goals**
- Không phơi ngưỡng gộp thành UI param — ngưỡng cố định trong code (1 checkbox duy nhất).
- Không thay đổi detector, không dùng unclip/limit_side_len để "tình cờ" gộp.
- Không live-reload giữa phiên: áp dụng từ lần `core.start` kế tiếp (giống `ocrFps`/`captureFps`).
- Không dọn các entry cache dòng cũ.

## 3. Quyết định chính

| # | Quyết định | Ghi chú |
|---|-----------|---------|
| D1 | Hook tại `OcrPipeline.SpotAsync`: sau khi nhận `spots` từ sidecar, **trước** `_diff.Feed`. Thay `obj["spots"]` bằng bản gộp. | Điểm duy nhất mà diff + mọi sink (`TranslateCycleSink`, `JsonLinesSink`) cùng đi qua → các sink không cần biết có gộp hay không. *(my call)* |
| D2 | Gộp trên AABB pixel của quad; spot kết quả giữ shape `{box, text, score}`; box union biểu diễn thành quad chữ nhật 4 điểm. Spot gộp (≥ 2 dòng) thêm field `paragraph: true`. | `SpotGeometry.NormalizeQuad` đã xử lý quad bất kỳ → downstream không đổi. Field `paragraph` là cách sink biết cờ để render pill cao hơn. *(my call)* |
| D3 | Nối text các dòng bằng **space** (không `\n`). | Dịch tự nhiên hơn cho subtitle; cache key phẳng, không phụ thuộc line-ending. *(my call)* |
| D4 | Heuristic 3 điều kiện trên cặp **dòng liên tiếp** theo reading order: (a) gap dọc `gapY ≤ 0.6 × min(hA,hB)` (cho phép overlap âm); (b) tỷ lệ chiều cao `max/min ≤ 1.35`; (c) overlap ngang `≥ 0.5 × min(wA,wB)`. | Không yêu cầu canh lề trái → đỡ được text **căn giữa** (subtitle game). Hai cột song song cùng hàng bị loại bởi điều kiện (c). *(my call — ngưỡng)* |
| D5 | Mỗi box so với **dòng cuối** của từng group đang mở; gộp vào group đầu tiên nhận, không nhận thì tự mở group mới (không anchor toàn group). | Đỡ được layout 2 cột xen dòng. *(my call; chỉnh 2026-11-01 theo ruling SDD Task 1)* |
| D6 | Config: `AppSettings.OcrMergeLines` (default `true`) → `core.start` param `ocr.mergeLines?: bool` (absent = `false`) → constructor `OcrPipeline`. CLI: flag `--merge-lines`, default off. | Đặt trong object `ocr` của `core.start` vì đây là hành vi pipeline phía Core, cùng họ với det/rec/params. *(my call — vị trí)* |
| D7 | Render: 1 pill cho box union; `maxLines` pill tăng **2 → 4** chỉ cho box gộp (cờ `IsParagraph`), font vẫn shrink-to-fit + truncate như hiện tại. | Box đơn giữ nguyên maxLines 2 → không đổi visual hiện có. *(my call — số 4)* |
| D8 | Diff/session/cache chạy theo **text đoạn**: `new_texts` chứa text khối; cache key `(context, textĐoạn, src, dst)`. Entry dòng cũ thành mồ côi, không dọn. | Hệ quả tất yếu của D1 + D3. |
| D9 | `score` của block = `min(score)` các dòng thành viên. | Conservative; score không được downstream dùng để quyết định gì. *(my call)* |

## 4. Thuật toán `LineGrouper`

Input: `JsonArray spots`, mỗi spot `{box: [[x,y]×4] pixel, text, score}`.

1. Mỗi spot → AABB `(x, y, w, h)` theo pixel. Spot thiếu `text`/`box` hoặc box suy biến (`w < 1 || h < 1`) **giữ nguyên, không gộp**.
2. Sort theo `(y_top, x_left)` — reading order trên→dưới, trái→phải.
3. Quét tuần tự theo reading order với **nhiều group đang mở**: box mới `B` gộp vào group mở đầu tiên mà dòng cuối `A` của nó đủ cả 3 điều kiện D4 (union AABB + nối text bằng space); không group nào nhận → mở group mới với `B`. Spot cột khác xen giữa các dòng cùng cột không phá chuỗi. *(chỉnh 2026-11-01 theo ruling SDD Task 1 — single-group scan bất khả thi với test case 3-dòng-xen-cột-khác)*
4. Output: array spots mới cùng shape, theo reading order. Group 1 dòng = giữ spot gốc (không thêm field `paragraph`).

Chi phí: sort O(n log n) + quét O(n). Với ~60 box/khung: không đáng kể so với thời gian OCR.

## 5. Thay đổi code theo file

### Core (`ReTran.Core`)
- **Mới** `Ocr/LineGrouper.cs`: class thuần (không phụ thuộc RPC/UI), `static JsonArray Group(JsonArray spots)`; hằng số ngưỡng public const (`MaxGapRatio = 0.6`, `MaxHeightRatio = 1.35`, `MinOverlapRatio = 0.5`) — dễ test, dễ chỉnh sau.
- `Ocr/OcrPipeline.cs`: constructor thêm `bool mergeLines = false`; trong `SpotAsync` (dòng ~101–112) thay `spotsArr` bằng bản gộp **trước** khi lấy texts và `_diff.Feed`.
- `Serve/PipelineHost.cs`: `ParseStart` (:560) đọc `ocr.mergeLines` — absent = `false`, sai kiểu → `-32602`; `StartSpec` (:1026) thêm `bool MergeLines`; truyền vào `OcrPipeline` khi tạo session (:213).
- `Cli/OcrCommands.cs`: 2 lệnh dùng `OcrPipeline` (`:58` spot, `:117` loop) thêm flag `--merge-lines` (default off — giữ contract M3).
- `Program.cs:84`: handler JSON-RPC `ocr.spot` standalone giữ default off (không có flag ở đây).
- `Serve/AnnotationSnapshot.cs`: `AnnotBox` thêm positional param cuối `bool IsParagraph = false` (call site cũ compile nguyên).
- `Serve/TranslateCycleSink.cs`: đọc `paragraph` trên spot → gắn `IsParagraph` vào `AnnotBox` + field `"paragraph"` trong wire boxes (:143–153).
- `Serve/FrameAnnotator.cs`: `DrawBoxes` (:100–142) dùng `maxLines: box.IsParagraph ? 4 : 2`.

### App (`ReTran.App`)
- `Services/AppSettingsService.cs`: `AppSettings` thêm `bool OcrMergeLines = true` (doc comment song ngữ). File settings cũ thiếu key → deserialize ra default `true`.
- `Components/Shared/SettingsPanel.razor`: checkbox trong mục **OCR (model & tham số)**: "Gộp nhiều dòng thành một khối trước khi dịch" + hint "áp dụng từ lần Start kế tiếp". Lưu qua đường `SaveOcrParamsAsync` (ghi `settings.json` cùng khối OCR — nhất quán với `ocrFps`/`captureFps`).
- `Core/PipelineController.cs`: `StartAsync` thêm `mergeLines` vào object `ocr` của `core.start`; `HandleFrameTranslated` (:786–807) đọc field `"paragraph"` (default `false` nếu thiếu — tương thích Core cũ).
- `Core/OverlayState.cs`: `OverlayBox` thêm `bool IsParagraph = false`.
- `Services/OverlayWindow/Win32OverlayHost.cs` (+ `BoxAnnotationStyle` phía App): pill `maxLines` theo cờ (2 → 4 cho block gộp).
- `Services/I18nStrings.cs`: key mới vi/en cho checkbox + hint.

## 6. Wire / API thay đổi

| Mặt | Thay đổi | Tương thích |
|---|---|---|
| `core.start` | `ocr.mergeLines?: bool` (absent = false) | App cũ không gửi → Core coi như tắt — đúng contract CLI hiện tại |
| `frame.translated` boxes[] | thêm `"paragraph": bool` | App cũ bỏ qua field → render như box thường (degrade graceful) |
| Reply `ocr.spot` nội bộ Core | spot gộp mang `paragraph: true` | Chỉ khi bật; CLI off mặc định nên output M3 không đổi |

## 7. Hệ quả cache & diff (D8)

- Một dòng thay đổi trong block → text khối đổi → cả block là "new" → dịch lại **cả block**. Tradeoff chấp nhận: đổi lấy chất lượng dịch theo ngữ cảnh tốt hơn và ít request hơn khi màn hình ổn định.
- Cache SQLite không cần migration: key mới tự sinh; entry dòng cũ nằm im, không hại.

## 8. Kế hoạch test

1. **`LineGrouperTests`** (mới, thuần unit trên `JsonArray`):
   - 2 dòng canh trái → 1 block: text nối space, box = union quad, `paragraph: true`, score = min.
   - 2 dòng căn giữa → gộp (overlap ≥ 50%).
   - 2 cột song song cùng hàng → **không** gộp.
   - Gap dọc > 0.6×h → không gộp; ngay ngưỡng → gộp.
   - Tỷ lệ cao > 1.35 → không gộp.
   - Chuỗi 3 dòng → 1 block, giữ reading order.
   - Empty array / 1 spot / spot hỏng (thiếu box/text) → không đổi, không throw.
2. **`OcrPipelineTests`**: merge on → `spots` + `new_texts` theo đoạn; merge off → output byte-giống hiện tại (regression M3).
3. **`PipelineHostTests`**: `ocr.mergeLines` true / false / absent / sai kiểu (-32602).
4. **`TranslateCycleSinkTests`**: block gộp → đúng 1 box, dịch 1 lần, wire có `"paragraph": true`.
5. **`AppSettingsServiceTests`**: round-trip `OcrMergeLines`; file thiếu key → `true`.
6. Render: test cờ propagates tới `AnnotBox`/`OverlayBox` (visual pixel không assert).

## 9. Hiệu năng

- Gộp: < 0.1 ms/60 box — nằm gọn trong ngân sách annotate ≤ 40 ms/khung; nếu nghi ngờ, chạy A/B bằng `Tools/Perf/annotbench`.
- Translation: số request **giảm** (N dòng → 1 block) khi màn hình ổn định; không có đường nóng mới.

## 10. Rủi ro / giới hạn đã biết

- Block biến động một dòng → dịch lại cả block (chi phí dịch tăng trong lúc text đổi nhanh).
- List có bullet/indent cùng cột sẽ bị gộp thành 1 khối — chấp nhận cho v1.
- Font pill trong block cao: shrink-to-fit tự co; nếu thực tế chữ nhỏ hơn mong muốn → cải tiến sau (dùng `h / số dòng` làm base font).
- OCR jitter giữa các khung có thể đổi cách nhóm → box nhấp nháy nhẹ; không tệ hơn hiện trạng (box dòng cũng jitter).

## 11. Ngoài phạm vi

Ngưỡng chỉnh qua UI, gộp theo ngữ cảnh (cột/menu), live-reload giữa phiên, dọn cache dòng cũ, Rust hook cho exclusive fullscreen.
