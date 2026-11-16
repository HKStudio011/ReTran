# Thiết kế: Màn “Chọn model OCR” + “Ngôn ngữ bổ sung” (setup wizard)

Ngày: 2026-11-16 · Trạng thái: chờ user duyệt
Nền: mockup `mockup-model-extra-langs.html` (user duyệt 2026-11-16) — tiếp loạt “tối ưu từng màn” sau màn Checking (spec `2026-11-15-setup-checking-checklist-design.md`).

## 1. Mục tiêu

- Thêm **màn A “Chọn model OCR”** đứng **TRƯỚC** màn chọn ngôn ngữ: tick nhiều tier PP-OCRv6 (tiny/small/medium), **ít nhất 1**, radio **“Dùng chính”** quyết định cặp máy chạy.
- Thêm **màn B “Ngôn ngữ bổ sung”** đứng **SAU** màn chọn ngôn ngữ: các ngôn ngữ **ngoài phủ PP-OCRv6**, tick nhiều **hoặc để trống** — chỉ để **tải sẵn** rec legacy.
- **DownloadingModels** tải đúng tập model theo lựa chọn (tiers × det+rec, + rec legacy của ngôn ngữ chính nếu ngoài phủ, + extra langs) và **bỏ qua model đã cài sẵn**.
- **Applying**: cặp active = det của tier “dùng chính” + rec theo ngôn ngữ chính.
- **Rerun khi model đã tồn tại**: preselect phản ánh thực tế (đã cài + cặp đang chạy); **không tải lại**; **không đổi cặp** nếu không đổi gì; tự **tải bù** khi model bị xoá (§8).

**Non-goals:** không xoá/gỡ model trong wizard (quản lý xoá ở Settings); không đổi “engine một cặp det/rec”; **không sửa sidecar / `catalog.py`**; không đổi màn Checking / Ngôn ngữ chính / Failed / Settings.

**Anti-goals:** rerun không được âm thầm đổi cặp đang chạy; không tải lại model đã có; không chặn máy yếu (tiny là lựa chọn hợp lệ, mặc định vẫn Small).

## 2. Hiện trạng liên quan (đã kiểm chứng)

- `ocr.models.recommend(src)` (sidecar) trả cặp **cứng tier small**; rec legacy cho ngôn ngữ ngoài phủ v6 (korean/cyrillic/arabic/th/ta/te/devanagari/el/…). Không có tham số tier.
- App có mirror catalog `OcrCatalogDisplay` (drift test ghim với `catalog.py`) + `PairFor()` mirror routing — dùng cho màn ngôn ngữ hiện tại.
- Model trên đĩa: `ReTran.OCR/models/official_models/<tên-model>/` — **tên thư mục = tên model** (đo thật 2026-11-16: `PP-OCRv6_small_det`, `PP-OCRv6_tiny_rec`, …). ⇒ Phát hiện “đã cài” cho màn A/B bằng **filesystem**, không cần sidecar (A/B chạy trước khi venv tồn tại trên máy sạch).
- Wizard: `Checking` và `ChoosingLanguage` là **pre-pipeline pause**; pipeline steps có done marker; flavor đổi → `ClearPipelineSteps`; `DownloadingModels` skip từng model đã cài qua `ocr.models.list`.
- Catalog còn 2 rec legacy **routing không bao giờ chọn** (`chinese_cht`, `eslav`) → loại khỏi màn B.
- Engine giữ **một** cặp det/rec tại một thời điểm (spec setup §2) ⇒ “tick nhiều” = *tải nhiều*, cặp chạy vẫn một.

## 3. Quy trình mới

```
Checking → [A] Chọn model OCR → Chọn ngôn ngữ chính → [B] Ngôn ngữ bổ sung
        → VerifyingFiles → Syncing → VerifyingSidecar → DownloadingLlmRuntime
        → DownloadingModels → Applying → Done
```

A và B là **pre-pipeline pause như ChoosingLanguage** (không nằm trong `PipelineSteps`): `StartAsync` chạy Checking → pause A nếu `tiers` chưa trả lời → continue → pause Language nếu chưa có → continue → pause B nếu `extraLangs` chưa trả lời → continue → `RunPipelineAsync(VerifyingFiles)`.

## 4. Màn A — “Chọn model OCR”

- 3 dòng checkbox: Tiny / Small (khuyến nghị) / Medium — mỗi dòng: tên + `<tier>_det · <tier>_rec · size`; radio **“Dùng chính”** chỉ hiện ở dòng đã tick; nút **Tiếp tục khoá khi 0 tick** + dòng nhắc “Chọn ít nhất 1 bản để tiếp tục.” (mockup A1–A3).
- **Preselect (mirror thực tế):** tier **đủ det+rec đã cài** → tick sẵn **và KHOÁ checkbox (disabled — không bỏ tick được; bỏ tick dễ hiểu nhầm “không dùng/xoá”)**, kèm tag “đã cài”; máy sạch chưa có gì → chỉ **Small** (tự do tick/bỏ). **Radio “Dùng chính” vẫn chọn được trên các dòng đã tick — khoá chỉ ở checkbox.**
- **activeTier:** = tier của cặp active hiện tại (settings `OcrDet/OcrRec`) nếu tier đó đã tick; ngược lại **Small** nếu tick; ngược lại dòng tick đầu theo thứ tự tiny → small → medium.
- Trạng thái mỗi dòng: **“đã cài”** khi đủ det+rec; **“sẽ tải ~X MB”** khi còn thiếu (X = phần thiếu của riêng dòng). **Tổng tải = Σ phần thiếu** (đã cài hết → “0 MB”).
- Continue → `ContinueWithOcrTierAsync(tiers, activeTier, ct)` lưu state + marker `choosingocrtier`.

## 5. Màn B — “Ngôn ngữ bổ sung”

- 8 dòng, tên native + rec legacy + size: 한국어(ko) · Русский(ru) · العربية(ar) · ไทย(th) · தமிழ்(ta) · తెలుగు(te) · हिन्दी(hi) · Ελληνικά(el) (mockup B1–B2).
- Tick nhiều **hoặc để trống** — không ràng buộc. Preselect: rec **đã cài** → tick sẵn **+ khoá checkbox (disabled, không bỏ tick được — tránh hiểu nhầm)**, tag “đã cài”; các dòng còn lại tự do tick/bỏ. (Khoá chỉ là UI — xoá model vẫn ở Settings.)
- Dòng hint: “tải sẵn — cặp đang chạy vẫn theo ngôn ngữ chính; đổi dùng sau ở Cài đặt”.
- Continue → `ContinueWithExtraLanguagesAsync(langs, ct)` lưu `extraLangs` + marker `choosingextralangs`.

## 6. Màn ngôn ngữ chính (đổi nhẹ)

- Cặp hiển thị + tổng dung lượng = **det(activeTier) + rec(tier activeTier nếu v6 phủ, ngược lại legacy như hiện tại)**; vẫn 1 lựa chọn + “Tự động”. PairFor hiện có được param hóa theo `activeTier` (dùng chung helper với màn A/B).

## 7. Tải & áp dụng

- **Tập cần-tải (DownloadingModels)** theo answers: mỗi tier tick → `<tier>_det` + `<tier>_rec`; ngôn ngữ chính ngoài phủ → rec legacy; mỗi extra lang → rec legacy của lang đó. **Tên rec legacy lấy từ routing mirror `OcrModelChoices.LegacyRecFor` (drift-guarded) — wizard KHÔNG còn gọi `ocr.models.recommend`** *(sửa sau khi soi kỹ: mirror đã ghim bằng test với `catalog.py`, bỏ được một phụ thuộc RPC)*. Với mỗi tên: `ocr.models.list` báo chưa cài → `ocr.models.download`; poll/retry như hiện có. **Không tải lại model đã cài.**
- **KHÔNG sửa sidecar / `catalog.py`** — RPC `recommend` giữ nguyên cho nơi khác; wizard chỉ dùng mirror.
- **Applying**: `OcrDet = <activeTier>_det`; `OcrRec = <activeTier>_rec` nếu ngôn ngữ chính được v6 phủ (kể cả “Tự động”), ngược lại rec legacy của ngôn ngữ chính (mirror).

## 8. Rerun khi model đã tồn tại — RECONCILE (case user hỏi)

- **Answers đã tồn tại → A/B được SKIP như màn ngôn ngữ** (không hỏi lại). Muốn đổi lựa chọn về sau → **Settings** (đã có quản lý đầy đủ). Ngoại lệ: máy cũ (setup.json chưa có field mới) → lần “chạy lại setup” đầu tiên sau update hiện A/B **một lượt** với preselect theo thực tế; sau đó skip như thường.
- **Reconcile chạy ở 2 thời điểm** — (i) sau Checking của MỖI lượt chạy, và (ii) **ngay sau khi lưu câu trả lời ở A/B** (để lựa chọn MỚI kịp clear marker tải/apply — nếu chỉ chạy ở (i), tick thêm model ở lần rerun sẽ không bao giờ được tải vì marker `downloadingmodels` đã done trước đó). Reconcile **nhẹ bằng filesystem + settings** (không RPC, chạy được trước sidecar):
  1. Nếu bất kỳ model nào trong tập cần (theo answers) **thiếu trên đĩa** → clear marker `downloadingmodels` ⇒ bước tải chạy lại, **chỉ tải phần thiếu**.
  2. Nếu cặp active **suy từ answers ≠ cặp trong settings.json** → clear marker `applying` ⇒ áp lại cặp **theo đúng lựa chọn đã lưu** (không tự ý đổi).
  3. Không lệch gì → không clear ⇒ máy đã setup bấm “chạy lại setup” **pass-through nhanh như hiện nay**.
- Ví dụ máy dev (đo 2026-11-16: đã cài tiny+small, cặp chạy small): rerun → A preselect tick Tiny + Small (**khoá vì đã cài**), radio **Small**, tổng **0 MB**; B trống; bấm qua → reconcile thấy đủ + cặp khớp → các bước tải/apply skip → xong gần tức thì, **cặp không bị đụng**.
- Xoá model rồi rerun → reconcile phát hiện thiếu → tự tải bù. *Giới hạn đã biết:* thư mục model còn trên đĩa nhưng file con hỏng → reconcile không phát hiện (dùng Settings tải lại; bước tải khi chạy vẫn verify bằng `ocr.models.list`).

## 9. Trạng thái & resume

- `setup.json` thêm: `tiers` (list, `null` = chưa trả lời), `activeTier` (string, `null`), `extraLangs` (list; `null` = chưa hỏi — `[]` = đã hỏi “không chọn”). File cũ thiếu field → null → A/B hỏi một lần (theo §8).
- Marker: `choosingocrtier`, `choosingextralangs` (ghi khi confirm; pre-pipeline nên **không** bị `ClearPipelineSteps` xóa khi đổi flavor).

## 10. i18n & dữ liệu tên

- ~22 key mới × 2 ngôn ngữ: tiêu đề/step 2 màn, mô tả 3 tier, “Dùng chính”, “đã cài”, “sẽ tải ~{0}”, “đã cài — bỏ tick không xoá”, tổng tải, nhắc min-1, hint màn B, lỗi/empty.
- `LanguageNames` (App): thêm **தமிழ் (ta)** và **తెలుగు (te)** (ar/th/el/hi đã thêm 2026-11-15).

## 11. Files dự kiến

- `ReTran.App/Services/Setup/SetupCoordinator.cs` — enum step mới (A/B), chuỗi pre-pipeline, 2 continue methods, reconcile, tập tải, cặp applying.
- `ReTran.App/Services/Setup/SetupState.cs` — 3 field mới.
- `ReTran.App/Components/Shared/SetupModal.razor` — 2 case màn mới + CSS scoped (bảng chọn).
- `ReTran.App/Services/I18nStrings.cs` — keys + 2 tên ngôn ngữ.
- `ReTran.App/Services/Setup/OcrCatalogDisplay.cs` — helper map lang→legacy rec dùng chung (tách từ `PairFor` trong modal), param hóa tier cho pair display.
- Tests: `SetupCoordinatorTests` (order/pause/skip/reconcile/download-set/applying), `SetupStateTests`, `OcrCatalogDisplayTests` (nếu thêm helper).
- KHÔNG đụng: sidecar Python, `SystemCheck`, ReTran.Core.

## 12. Test chính (pin hành vi)

1. Order: Checking → pause A (`tiers` null) → continue → pause Language → continue → pause B → continue → pipeline; answers đủ → không pause màn nào.
2. Preselect: filesystem giả (tmp dirs) tiny+small có, medium không → A tick tiny+small, activeTier theo settings; máy sạch → chỉ small.
3. Min-1: continue với 0 tick bị chặn (UI) + coordinator validate `tiers.Count ≥ 1` (-32602-style guard nội bộ).
4. Tải: tick small+medium → tải đủ 4 model (fake bridge); main=ko → rec korean thay vì v6; extra [ko, ru] → +2 rec; model đã cài → không gọi download.
5. Applying: activeTier=medium, main=auto → `medium_det`/`medium_rec`; main=ko → `medium_det`+`korean_PP-OCRv5_mobile_rec`.
6. Reconcile: thiếu 1 model → clear `downloadingmodels` (và bước chạy lại chỉ tải model đó); cặp lệch settings → clear `applying`; khớp hết → **không** clear (máy đã setup pass-through).
7. setup.json: roundtrip 3 field; file cũ → null; `extraLangs=[]` phân biệt với `null`.
8. **Preselect/khoá helper (hàm thuần, testable):** installed map + cặp trong settings → ticks (đã cài = tick+khoá; máy sạch → Small tự do; bán phần (thiếu 1 model) = tự do + status “thiếu”) + activeTier (settings nếu tick; else Small nếu tick; else dòng tick đầu) — đúng luật §4/§5.

## 13. Calls của controller (đề xuất — user chỉnh khi duyệt)

1. Không sửa sidecar: tên model tier + rec legacy ghép từ mirror (drift-guarded); wizard **bỏ hẳn** lượt gọi `recommend` (RPC vẫn giữ nguyên cho nơi khác).
2. A/B là **pre-pipeline pause** như language (không nằm trong PipelineSteps).
3. Reconcile theo §8: chỉ clear khi lệch thật; dùng filesystem (không RPC); chạy **sau Checking và sau mỗi lần lưu câu trả lời A/B**.
4. Bỏ 2 rec “chết” (`chinese_cht`, `eslav`) khỏi màn B.
5. **Dòng đã cài ở A/B: tick sẵn + khoá checkbox** (user 16-11: bỏ tick gây hiểu nhầm “không dùng/xoá”); radio “Dùng chính” vẫn dùng được trên các dòng đã tick.

## 14. Rủi ro / giới hạn

- Filesystem-check trước sidecar có thể lệch catalog thật (dir còn, file hỏng) → đã ghi giới hạn §8; đường tải lại ở Settings.
- Màn A hiển thị size từ mirror (có thể lệch nhỏ với thực tế) — như màn ngôn ngữ hiện tại.
- Máy đã setup từ bản cũ: một lượt hỏi thêm A/B (một lần) — chấp nhận, khớp pattern “hỏi một lần như ngôn ngữ”.
