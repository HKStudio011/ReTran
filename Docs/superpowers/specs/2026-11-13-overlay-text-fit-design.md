# Thiết kế: Font scale & wrap cho pill overlay — fit theo lòng box

- Ngày: 2026-11-13
- Trạng thái: **đề xuất — chờ user duyệt** (chưa viết code)
- Ngữ cảnh: nối tiếp spec LineGrouper (`2026-11-01-ocr-line-grouping-design.md` — D7 chốt `maxLines` 2→4 cho box gộp) và ghi chú §10 của spec đó: *"shrink-to-fit tự co; nếu thực tế chữ nhỏ hơn mong muốn → cải tiến sau"*. User báo thực tế: **chữ bị thu quá nhỏ trong khi box còn rất dài/rộng** (box gộp cao bị thừa nửa dưới, box thấp-rộng bị ép chữ nhỏ/cắt "…"). User chốt hướng: fit dùng hết không gian box; pill được phép nở tràn khi bí.

## 1. Đo thật (2026-11-12, bản sao trung thành `BoxAnnotationStyle.Fit`, box 1080p)

| Box, bản dịch ~146 ký tự | Hiện tại (trần 2/4 dòng) | Bỏ trần dòng (fit theo box) |
|---|---|---|
| Gộp hẹp-cao 360×210 | 19px / 4 dòng — dùng 103/202px bề dọc | 24px / 6 dòng — dùng 195/202px |
| Gộp cao 500×330 | 27px / 4 dòng — 147/322px | 38px / 6 dòng — 308/322px |
| Rộng 900×120 | 25px / 2 dòng | không đổi |
| Thấp-dài 1100×40 (1 dòng) | 15px / 1 dòng (vừa) | không đổi |
| Box nhỏ 240×30 | 9px, vẫn không vừa → cắt "…" | (cần cơ chế nở — D2) |

Nguyên nhân: `Fit` ép cứng `lines ≤ maxLines` (2 cho box đơn, 4 cho box gộp) **cùng lúc** với điều kiện cao — bản dịch cần nhiều dòng hơn trần thì bị thu cỡ chữ để nhét đủ vào 4 dòng, dù bề dọc box còn trống. Cùng cực (kể cả 9px không vừa) → `TruncateToFit` cắt "…" tại cỡ 9px.

Bề mặt thứ ba — trang OBS (`wwwroot/overlay-feed/overlay.js`) — có 2 khác biệt nữa:
- **Không có logic fit nào**: font = `clamp(10, 0.5 × boxH_px, 40)`; tràn thì pill bị `overflow:hidden` cắt.
- **Trần 40px cứng, không scale theo canvas** — native scale: canvas 1080 → 34, 1920 → 60, 2160 → 67 (formula `40 × max(0.5, width/1280)`), nên ở canvas lớn chữ OBS nhỏ hơn hẳn.

## 2. Mục tiêu / Non-goals

**Mục tiêu**
- Cỡ chữ = **lớn nhất mà khối chữ (wrap tự nhiên) nằm gọn trong lòng box** (rộng × cao) — dùng hết không gian, không còn trần dòng cứng.
- Khi ngay cả cỡ sàn không vừa: **pill nở tràn xuống dưới** khỏi box (giữ chữ đọc được) thay vì ép nhỏ/cắt; cắt "…" chỉ còn là chốt an toàn cuối khi vượt viewport.
- Đồng bộ hành vi 3 bề mặt: preview (Core), overlay Win32 (App), trang OBS (JS).

**Non-goals**
- Không đổi vị trí/kích thước box (letterbox, clamp toạ độ giữ nguyên).
- Không đổi màu/viền/tick/mockup; không thêm settings UI.
- Không đồng bộ căn lề native (top-left) với web (center) — khác biệt có sẵn, ngoài phạm vi.
- Không đổi cơ chế `paragraph` (vẫn do LineGrouper phát cờ; cờ giờ chỉ mang tính gợi ý — công thức fit mới không cần phân biệt).

## 3. Quyết định chính

| # | Quyết định | Ghi chú |
|---|-----------|---------|
| D1 | Bỏ điều kiện `lines ≤ maxLines` khỏi `Measure`/`Fit`; fit = `block.Height + 2×PadY(size) ≤ innerHeight + 0.5` (đo với bề ngang inner như hiện tại). Chọn cỡ lớn nhất trong `[MinFontSize..BaseFontSize]` (binary search giữ nguyên). Gỡ tham số `maxLines`/`shrink` khỏi `Fit` ở cả 2 bản Core/App. | *(my call — supersede D7 spec 2026-11-01)* |
| D2 | Khi `!fit.Fits` (kể cả sàn 9px): **giữ cỡ sàn**, để pill nở (`pillH = block.Height + 2×PadY`, cơ chế `pillH = max(innerH, …)` đã có sẵn ở Core + Win32); chỉ gọi `TruncateToFit` khi block vượt chiều cao khả dụng của viewport (tính số dòng tối đa mà viewport chứa được). | *(user chốt: "pill nở tràn khi bí"; viewport-guard là my call)* |
| D3 | `MinFontSize` giữ 9px; `MaxFontSize` 40px và công thức scale theo viewport giữ nguyên (`× max(0.5, width/1280)`); `BaseFontSize = clamp(0.5×boxHeight, 12, cap)` giữ nguyên (giờ chỉ là trần ban đầu). | *(my call)* |
| D4 | Trang OBS: port cùng thuật toán vào JS (binary search + đo bằng element ẩn cùng font/width, đọc `scrollHeight`), cap chữ theo canvas `40 × max(0.5, width/1280)`, sàn 10px; pill đổi sang `height:auto; min-height:100%` neo trên để nở xuống. | *(my call)* |
| D5 | Giữ 3 bản sao (Core `Serve/BoxAnnotationStyle.cs`, App `Services/OverlayWindow/BoxAnnotationStyle.cs`, JS) — lý do bất biến: App không reference Core; web là JS. Cập nhật ghi chú "kept in sync" + cùng bộ hằng số. | *(my call)* |
| D6 | Cờ `IsParagraph` không còn ảnh hưởng cỡ chữ/maxLines; giữ trên wire + `OverlayBox`/`AnnotBox` (không phá vỡ contract, dùng cho thống kê/debug sau). | *(my call)* |

## 4. Thuật toán fit mới (Core/App)

```
Fit(g, text, innerWidth, innerHeight, boxHeight, format, maxFontSize):
  high = BaseFontSize(boxHeight, maxFontSize)          // trần ban đầu
  atHigh = Measure(high);  nếu fits → trả về
  low = MinFontSize (9); best = Measure(low)           // sàn, kể cả khi không fits
  binary search (low..high): candidate.Fits → best = candidate, đi lên; ngược lại đi xuống
  trả best

Measure(size):  block = MeasureBlock(size, innerWidth)
                fits = block.Height + 2×PadY(size) ≤ innerHeight + 0.5
```

Renderer (Core `FrameAnnotator.DrawBoxes`, App `Win32OverlayHost.DrawBoxes`, JS tương ứng):
1. `fit` → nếu `!fit.Fits`: giữ `fit.Size`, `block = MeasureBlock(...)`, **không truncate vội**.
2. `pillH = max(innerH, block.Height + 2×PadY)`; `pillY = clamp(inner.Y, 0, viewportH − pillH)` (đã có) — pill nở xuống, đỉnh neo trên.
3. Nếu `block.Height + 2×PadY > viewportH − margin` (vượt cả viewport): `TruncateToFit` với `maxLines = max(1, floor((viewportH − 2×PadY) / lineHeight))`.

## 5. Thay đổi code theo file

### Core (`ReTran.Core`)
- `Serve/BoxAnnotationStyle.cs`: `Fit` bỏ `maxLines`/`shrink`; `Measure` bỏ điều kiện dòng; `TruncateToFit` giữ (chỉ còn dùng ở viewport-guard).
- `Serve/FrameAnnotator.cs` `DrawBoxes` (:100–147): bỏ `maxLines = IsParagraph ? 4 : 2`; thêm guard viewport (D2).

### App (`ReTran.App`)
- `Services/OverlayWindow/BoxAnnotationStyle.cs`: đồng bộ y hệt Core.
- `Services/OverlayWindow/Win32OverlayHost.cs` `DrawBoxes` (:232–292): bỏ maxLines, `maxFontSize: 40×ui` giữ nguyên; guard viewport.
- `wwwroot/overlay-feed/overlay.js`: fit mới + cap theo canvas + pill nở (`overlay.css`: `.pill { height:auto; min-height:100%; }` — vẫn `overflow:hidden` phía ngoài stage để không tràn canvas).

### Tests (`ReTran.Tests`)
- `BoxAnnotationStyleTests` (App) + test Core tương ứng: bảng ca từ §1 — box gộp cao chọn cỡ lớn hơn & nhiều dòng hơn (không còn phụ thuộc 2/4); box thấp-rộng không đổi; box nhỏ → `Fits=false`, size = sàn (caller nở pill).
- `FrameAnnotatorTests` / `Win32OverlayHostRenderTests`: cập nhật test "font scale theo chiều cao box" nếu assert cỡ cũ; thêm ca pill cao hơn box.
- Trang OBS: không có harness JS — kiểm tra tay theo checklist (canvas 1080/1920/2160, box gộp cao, box siêu nhỏ) ghi vào plan.

## 6. Wire / API

Không đổi. `paragraph` vẫn trên wire nhưng không còn điều khiển layout.

## 7. Hiệu năng

- Core/App: cùng loại binary search (số bước tương đương), không thêm cấp phát đáng kể → annotate ≤ 40 ms/khung giữ nguyên; chạy lại `Tools/Perf/annotbench` sau khi xong.
- OBS JS: thêm 1 phép đo layout/box (≤ ~60 box × 10–15 fps) — dùng 1 element đo tái sử dụng, ghi tối thiểu; nằm trong ngân sách browser source.

## 8. Rủi ro / giới hạn đã biết

- Pill nở che nội dung game phía dưới (user đã chấp nhận đánh đổi — ưu tiên đọc được).
- Box cực nhỏ: chữ ở sàn 9–10px + pill nở; không còn cắt "…" trừ khi vượt viewport.
- Khác biệt căn lề native vs web tồn tại như trước (ngoài phạm vi).
- OCR jitter làm box đổi cỡ liên tục → pill nhảy nhẹ như hiện tại (không tệ hơn).

## 9. Ngoài phạm vi

- Đồng bộ thẩm mỹ 3 bề mặt (căn lề, inset, bán kính bo góc).
- Hiệu ứng chuyển động/transition; theme pill.
- Fit cho text dọc/quad nghiêng (box vẫn là AABB chuẩn hoá).
