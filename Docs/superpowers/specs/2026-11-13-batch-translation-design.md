# Thiết kế: Gộp nhiều text thành một request dịch (batch provider)

- Ngày: 2026-11-13
- Trạng thái: **đề xuất — chờ user duyệt** (chưa viết code)
- Ngữ cảnh: nối tiếp spec M4 (`2026-10-06-m4-translation-design.md` §4.2–4.3) và LineGrouper (`2026-11-01-ocr-line-grouping-design.md`). Hiện mỗi text mới = 1 GET `translate_a/single` → màn hình nhiều box sinh nhiều request → dễ chạm rate limit của endpoint `gtx` (không chính thức). User chốt hướng: **gộp nhiều box vào một request**.

## 1. Đo thật (2026-11-12, curl trực tiếp)

- Lặp nhiều tham số `q` (`…&q=A&q=B`): **không hoạt động** — endpoint chỉ dịch `q` đầu tiên (thử 2 lần, cùng kết quả).
- Nối nhiều text bằng `\n` trong **một** `q`: trả về danh sách segment, mỗi dòng một segment `[translated, original, …]`; bản gốc được echo lại đầy đủ, kể cả `\n` phân cách.
  - 3 dòng → đủ 3 segment; 12 dòng (~1.050 ký tự thô, URL 1.474 ký tự) → đủ 12/12 segment, thứ tự đúng.
- ⇒ Batch khả thi bằng newline-join; ánh xạ ngược theo bản gốc echo.

## 2. Mục tiêu / Non-goals

**Mục tiêu**
- Giảm số request HTTP tỉ lệ ~1/K (K = số text mỗi lô), không đổi bất kỳ hành vi nào khác: cache lookup/put giữ nguyên, gate 4, "lỗi không retry trong phiên" (M4 §4.2).
- Fallback an toàn về hành vi cũ khi cấu trúc trả về lạ (mapping lỗi → dịch lẻ từng text).

**Non-goals**
- Không đổi wire Core↔App (batch nằm hoàn toàn nội bộ Core).
- Không thêm policy retry/backoff mới (hành vi lỗi giữ nguyên M4).
- Không thêm settings/UI.
- Chưa batch cho provider khác — interface mở sẵn để DeepL/LLM tận dụng sau.

## 3. Quyết định chính

| # | Quyết định | Ghi chú |
|---|-----------|---------|
| D1 | `ITranslator` thêm `Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string src, string dst, CancellationToken ct)`, **default interface implementation** = `Task.WhenAll` từng `TranslateAsync` (giữ nguyên hành vi mọi provider hiện có). Hợp đồng: kết quả đúng thứ tự input; thất bại = **ném exception** (không partial). | *(my call — giữ `TranslateAsync` cho đường lẻ/fallback)* |
| D2 | Chia lô nằm **bên trong** `GoogleWebTranslator` (biên lô là chi tiết protocol của Google — độ dài URL). `TranslateCycleSink` gọi **một** `TranslateBatchAsync(misses)` cho cả lô pending, không tự chia. | *(my call)* |
| D3 | Chia lô greedy theo **độ dài URL đã escape ≤ 1.800 ký tự** (đo được 1.474 ký tự OK; hằng số public để chỉnh). Text đơn dài hơn hạn mức → lô 1 text (vẫn 1 request, không tệ hơn hiện tại). | *(my call — mốc an toàn)* |
| D4 | Trước khi join: bỏ `\n`/`\r` trong từng text (thay bằng space) — contract "text là một dòng" (LineGrouper đã nối bằng space). Join bằng `\n` rồi `Uri.EscapeDataString` cả chuỗi. | *(my call — phòng hờ)* |
| D5 | Parse: đọc `root[0]` (danh sách segment), **cộng dồn** `original` (`seg[1]`) và `translated` (`seg[0]`); đóng một text khi phần original cộng dồn khớp text hiện tại (bỏ khác biệt trailing `\n` do phân cách); bỏ `\n` đuôi của bản dịch khi đóng. Không khớp → `FormatException`. | *(my call — thuật toán đã đo)* |
| D6 | Fallback **trong `TranslateCycleSink`**: `FormatException` từ batch → chạy lại đường cũ `TranslateOneAsync` cho từng text của lô (qua gate, log như cũ). Lỗi HTTP/timeout/429 → coi cả lô thất bại, **không fallback** (tránh dội thêm request khi đang bị rate limit); texts giữ nguyên trạng thái "chưa dịch" như hành vi M4. | *(my call — đặt fallback phía sink để đi qua gate)* |
| D7 | Gate 4 slot giữ nguyên: lô chính chiếm 1 slot, chạy các sub-chunk **tuần tự** bên trong; đường fallback per-text dùng gate như cũ. | *(my call — tổng tải không tăng)* |
| D8 | `sl=auto` giữ nguyên — Google detect ngôn ngữ trên toàn request; frame trộn ngôn ngữ có thể detect sai một phía (hiếm) → ghi vào rủi ro, không xử lý ở v1. | *(my call)* |

## 4. Thuật toán tách kết quả (đo được ở §1)

Input: `texts[0..n-1]`, joined = `t0 + "\n" + t1 + …`, response segments `S[0..m-1]`, mỗi `S[i] = [translated_i, original_i, …]`.

1. Duyệt `S` theo thứ tự; cộng dồn `accOrig += S[i].original`, `accTrans += S[i].translated`.
2. Khi `TrimEnd(accOrig, '\n') == texts[k]` → đóng text k: kết quả `k` = `TrimEnd(accTrans, '\n')`; reset accumulator; `k++`.
3. Kết thúc khi `k == n` và `S` duyệt hết; nếu còn lệch (thiếu/thừa segment, original không khớp) → `FormatException`.

Chú ý: so khớp dùng nguyên văn bản gốc (Google echo trung thực theo đo thật); nếu Google chuẩn hoá khoảng trắng trong tương lai → mismatch → fallback D6.

## 5. Thay đổi code theo file

### Core (`ReTran.Core`)
- `Translate/ITranslator.cs`: thêm `TranslateBatchAsync` (default impl) + doc comment song ngữ.
- `Translate/GoogleWebTranslator.cs`: override `TranslateBatchAsync`: chuẩn hoá text (D4) → chia lô (D3) → 1 GET/lô → parse segment + ánh xạ (D5). Hằng số lô `MaxBatchUrlLength = 1800` (public). `TranslateAsync` lẻ giữ nguyên (đường fallback).
- `Serve/TranslateCycleSink.cs`: `BackgroundAsync` — thay `Task.WhenAll(pending.Select(TranslateOneAsync))` bằng: batch 1 lần (qua gate) → kết quả cặp `(text, translated)` → `translate.put` như cũ; `catch (FormatException)` → fallback per-text (D6). `TranslateOneAsync` giữ nguyên cho fallback.

### Tests (`ReTran.Tests`)
- `GoogleWebTranslatorTests`: fake handler assert **1 HTTP call** cho nhiều text; kết quả đúng thứ tự; mismatch original → `FormatException`; vượt hạn mức URL → nhiều call, thứ tự đúng; `\n` trong text bị chuẩn hoá.
- `TranslateCycleSinkTests`: N miss → translator batch được gọi đúng 1 lần; batch ném `FormatException` → có fallback per-text (đếm số lần gọi `TranslateAsync`); batch ném HTTP error → không fallback, session không có text.

## 6. Wire / API

Không đổi. Toàn bộ nằm trong Core; `core.start`/`translate.request_cache`/`translate.put`/`frame.translated` giữ nguyên.

## 7. Hiệu năng

- HTTP: N request → ⌈N/K⌉ (màn hình ~15–40 box mới thường còn 1–3 request).
- Latency/request tăng nhẹ (payload lớn hơn) nhưng tổng thời gian lô giảm (bớt round-trip).
- Annotate/diff/cache/SHM không đụng → budget M2–M4 không đổi; đo lại khi nhánh xong, ghi `Docs/performance.md` nếu số thay đổi đáng kể.

## 8. Rủi ro / giới hạn đã biết

- Endpoint không chính thức, shape có thể đổi bất kỳ lúc nào → `FormatException` → fallback per-text (degrade về hành vi cũ, không crash).
- Google có thể gộp/tách dòng không theo input → mapping lỗi → fallback (mất lợi ích batch, không mất chức năng).
- `sl=auto` detect theo toàn request (D8).
- Text cực dài (> hạn mức lô) vẫn 1 request/text như hiện tại.

## 9. Ngoài phạm vi

- Retry/backoff theo 429 (giữ nguyên "không retry trong phiên" của M4).
- Batch cho DeepL/LLM (spec LLM sẽ tận dụng chính `TranslateBatchAsync` này).
- Dọn cache entry cũ; thay đổi gate/song song.
