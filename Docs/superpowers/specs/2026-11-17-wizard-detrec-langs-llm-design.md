# Thiết kế: Setup wizard vòng 2 — gộp det/rec, màn Ngôn ngữ, chọn LLM, dữ liệu từ nguồn

Ngày: 2026-11-17 · Trạng thái: chờ user duyệt (đã qua builder-check nội bộ)
Nền: mockup `mockup-detrec-langs-llm.html` (user duyệt 17-11, ghi chú "bỏ chữ Phần trên/Phần dưới" đã áp) + hội thoại 16–17/11.
Thay thế phần liên quan của spec `2026-11-16-ocr-tier-and-extra-langs-design.md`: màn A (tick theo tier) → màn det/rec 2 khối; màn "Chọn ngôn ngữ chính" **bỏ**; màn B → "Ngôn ngữ" (+ "Đọc chính").

## 1. Mục tiêu

- **Bước 2 "Chọn model OCR"**: một màn, HAI khối — det (nhận diện khung chữ) trên, rec (nhận dạng chữ) dưới; tick nhiều, tối thiểu 1 mỗi khối; mỗi khối một chấm "Dùng chính". Hai slot độc lập (trộn cỡ được: vd small_det + medium_rec).
- **Bước 3 "Ngôn ngữ"**: thay màn chọn ngôn ngữ chính cũ (bỏ hẳn — rec v6 đã phủ 50 ngôn ngữ). Tick nhiều/trống các ngôn ngữ ngoài phủ PP-OCRv6; chấm **"Đọc chính"** trên dòng tick để đổi rec đang chạy. Trống ⇒ không chấm, cặp giữ theo bước 2.
- **Bước 4 "Chọn model LLM"** (mới): tick nhiều/trống; nhóm theo lớp kích thước; mỗi model kèm bản lượng tử hoá. **Không chọn ⇒ bỏ qua bước 8**.
- **Bước 8 "Tải llama.cpp"**: chỉ chạy khi bước 4 có chọn; chưa có → tải; **cũ → cập nhật**; mới nhất → bỏ qua.
- **Tải model hiện % + ETA** (bước 9).
- **Dữ liệu từ nguồn**: mọi danh sách (cỡ det/rec, ngôn ngữ legacy, quant LLM…) đọc/suy từ nguồn catalog — thêm mục mới (cỡ `big`, quant mới…) không phải sửa UI/logic.
- **Settings**: nhóm chọn model rõ ràng (OCR det/rec · Ngôn ngữ · LLM).

## 2. Non-goals / anti-goals

- **Non-goals**: không đổi engine "một cặp det+rec chạy mỗi phiên" (các chấm chỉ định cặp chạy; tick còn lại là tải sẵn); không xoá model trong wizard; không đổi màn Checking / Failed / FlavorSwitch / popup bỏ qua; không đổi capture/OCR runtime/overlay.
- **Anti-goals**: rerun máy đã setup **không được hỏi lại** (trừ đúng 1 lượt cho field mới chưa có); không tải lại thứ đã có (ngoại lệ: runtime llama **cũ** thì cập nhật); không tự đổi cặp khi lựa chọn không đổi (giữ luật reconcile 16-11).

## 3. Quy trình mới (11 bước)

```
1  Checking
2  ChoosingOcrModels        (det + rec)     [MỚI — gộp bước 2+3 cũ]
3  ChoosingLanguages        (Ngôn ngữ)      [ĐỔI từ màn B cũ; thêm "Đọc chính"]
4  ChoosingLlmModels        (MỚI)
5  VerifyingFiles → 6 Syncing → 7 VerifyingSidecar
8  DownloadingLlmRuntime    [chỉ khi bước 4 có chọn; thêm "cũ → cập nhật"]
9  DownloadingModels        [tải OCR + GGUF đã chọn; % + ETA]
10 Applying → 11 Done
```

Ba màn 2/3/4 là **pre-pipeline pause** như A/B cũ (không nằm trong `PipelineSteps`). Bỏ step `ChoosingLanguage` + pause của nó.

## 4. Bước 2 — Chọn model OCR (det + rec)

- Khối **det** (trên): các model det trong catalog; khối **rec** (dưới): các rec họ PP-OCRv6. Mỗi dòng: tên cỡ + mô tả ("Tiny — nhẹ nhất…"), dòng mono `tên model · dung lượng`, dòng "sẽ tải ~X" khi thiếu (dòng đang tick), tick + khoá + tag **"đã cài"** khi đã có, chấm **"Dùng chính"** trên dòng tick của từng khối.
- **≥ 1 tick mỗi khối**; thiếu khối nào khoá nút + dòng nhắc "Chọn ít nhất 1 model ở mỗi khối để tiếp tục." (mockup A3).
- Preselect/preserve (thừa kế luật 16-11): đủ det∧rec đã cài ⇒ tick + khoá (theo từng khối); bán phần ⇒ tick tự do; máy sạch ⇒ Small mỗi khối; chấm chạy chính theo cặp settings (ActiveDet/ActiveRec nếu tick, else small, else dòng tick đầu).
- Continue → lưu `dets`, `activeDet`, `recs`, `activeRec` + marker `choosingocrmodels`.

## 5. Bước 3 — Ngôn ngữ

- Danh sách = các rec legacy **"routing-first"** (định nghĩa ở §8; hôm nay = 한국어/Русский/العربية/ไทย/தமிழ்/తెలుగు/हिन्दी/Ελληνικά). Tick nhiều **hoặc trống**; đã cài ⇒ tick + khoá + tag "đã cài".
- Chấm **"Đọc chính"** chỉ hiện trên dòng tick; chọn ⇒ rec đang chạy = rec legacy của ngôn ngữ đó (thắng rec v6 của bước 2); trống / chưa chấm ⇒ không đổi cặp. Bỏ tick dòng đang giữ chấm ⇒ chấm dời sang dòng tick kế; bỏ hết ⇒ không chấm (đúng ca B1 mockup).
- **Bản (mobile/server/…)**: gom rec legacy theo *ngôn ngữ chính*; trong nhóm bỏ entry có `langs` là **tập con thật** của entry khác (redundant — vd `eslav ⊂ cyrillic`); nhóm còn **≥ 2 entry** ⇒ hiện chọn "Bản:" trên dòng đó (vd `korean_…_server` sau này, cùng tập `langs` với mobile). Hôm nay cả 8 nhóm còn đúng 1 entry ⇒ không render (khớp mockup). *(Định nghĩa "cùng ngôn ngữ chính" trần sẽ nổ nhầm cyrillic/eslav — đã sửa sau builder-check.)*
- Continue → lưu `langs`, `activeLang` (nullable) + marker `choosinglanguages`.

## 6. Bước 4 — Chọn model LLM

- Danh sách = `llm.models.list` (nguồn chuyển theo §8). **Nhóm theo SizeClass động** (không hardcode "1.8B/7B"); mỗi dòng: tên + quant + dung lượng; tick nhiều **hoặc trống**; đã cài ⇒ tick + khoá + tag; chấm **"Dùng chính"** trên dòng tick.
- **Chấm "Dùng chính" có hiệu lực runtime** *(chốt sau builder-check — spec v1 không nói)*: có tick ⇒ chấm luôn tồn tại (radio, tự gán dòng tick đầu khi chưa có; preselect = `AppSettings.LlmActiveModel` nếu nằm trong dòng tick); không tick ⇒ `activeLlm = null`.
- **Applying ghi `activeLlm`**: `activeLlm ≠ null` ⇒ ghi `AppSettings.LlmActiveModel` + gọi `llm.models.set` (best-effort — RPC từ chối thì log warn, settings vẫn ghi; bình thường model đã cài xong ở bước 9). **Không tự đổi provider/mode sang LLM** — provider đổi ở Settings như hiện nay (call của mình — muốn khác thì nói khi duyệt).
- **Reconcile bổ sung**: `activeLlm ≠ null` mà `AppSettings.LlmActiveModel ≠ activeLlm` ⇒ clear marker `applying`.
- Hint: "Không chọn gì → wizard bỏ qua tải runtime llama.cpp (bước 8)."
- Continue → lưu `llmModels`, `activeLlm` + marker `choosingllmmodels`.

## 7. Bước 8 — DownloadingLlmRuntime (có điều kiện)

- `llmModels == []` ⇒ **MarkStepDone ngay, không gọi RPC nào** (bỏ qua thật).
- Có chọn: `llm.runtime.status` →
  - chưa cài ⇒ tải (như hiện tại);
  - đã cài & tag == bản mới nhất mà Core biết (theo cơ chế sẵn của `LlamaRuntimeManager`: bản Windows mới nhất có asset trên GitHub releases) ⇒ skip;
  - **tag cũ ⇒ tải/cập nhật lên bản mới**;
  - không tra được bản mới (mất mạng) ⇒ coi bản đang cài là ok, không chặn wizard.
- Retry mềm ×4 như hiện tại.

## 8. Dữ liệu từ nguồn (tự động)

**Cách tiếp cận đã cân nhắc**: (i) giữ danh sách cứng trong UI — loại (đúng vấn đề user nêu); (ii) hỏi sidecar khi chạy — không được, 3 màn chạy TRƯỚC khi sidecar sống (máy sạch chưa venv); (iii) **giữ một bản dữ liệu trong App, mọi thứ suy/đọc 100% từ bản đó, bản đó ghim với nguồn** — **chọn (iii)**.

- **OCR** (nguồn = `ReTran.OCR/src/retran_core/ocr/catalog.py`): App đọc `OcrCatalogDisplay` (mirror, đã có drift-test ghim `DisplayCatalog_MatchesPythonCatalog`). Suy tự động:
  - *cỡ* = token tách từ tên model `PP-OCRv6_<cỡ>_<kind>` (không danh sách cứng — thêm `big` là tự hiện); nhãn hiển thị i18n có **fallback về token**.
  - *khối det / rec* = entries theo `kind` (+ họ v6 cho khối rec); thứ tự = thứ tự catalog.
  - *ngôn ngữ bước 3* = gom rec legacy (không-v6) theo "ngôn ngữ chính" (lang đầu khác `en`; entry chỉ có `en` bị bỏ), chỉ giữ nhóm có ngôn ngữ chính **ngoài phủ v6**; trong nhóm, bỏ entry có `langs` là **tập con thật** của entry khác (redundant). Hôm nay = 8 nhóm (ko/ru/ar/th/ta/te/hi/el): `eslav` bị nuốt bởi `cyrillic`, `chinese_cht` bị v6 phủ; upstream thêm rec legacy mới là tự hiện.
  - *bản (mobile/server/…)*: nhóm còn **≥ 2 entry** sau lọc ⇒ hiện chọn "Bản:" (vd `korean_…_server` sau này cùng tập `langs` với bản mobile).
- **LLM** (nguồn = catalog trong Core): chuyển `LlmModelCatalog` từ **list C# cứng** → **file dữ liệu JSON** đóng gói cùng Core (đủ 6 trường như `LlmModelSpec`: Name/Repo/File/SizeBytes/Quant/SizeClass), nạp một lần lúc khởi động; UI nhóm theo SizeClass động. Thêm quant/cỡ mới = sửa JSON, **không sửa code, không rebuild App**.
- **Kiểm tra thực tế 17-11 (nguồn PaddleOCR)**: bản "server" chỉ có cho ch/TC/en/ja (`PP-OCRv5_server_rec`) + `en` server riêng — **8 ngôn ngữ của bước 3 hiện chỉ có mobile**; không vẽ "cỡ server" cho chúng cho tới khi nguồn có (mặc định: không bổ sung bản ch/TC/en/ja trong đợt này).

## 9. Tiến độ tải — % + ETA

- Cơ chế **chọn: poll-based** (đang poll sẵn) + mở rộng dữ liệu trả về:
  - OCR: `ocr.models.list` mỗi entry thêm `progress: { receivedBytes, totalBytes, bytesPerSec }` (optional — chỉ khi đang tải). Cần sidecar đếm bytes trong lúc tải.
  - LLM GGUF: `llm.models.list` tương tự (Core).
  - Runtime llama: `llm.runtime.status` thêm `progress`.
- UI: % = received/total; ETA = (total − received) / bytesPerSec (EMA làm mượt); chưa biết `totalBytes` ⇒ giữ thanh vô định như cũ; tốc độ ≈ 0 ⇒ hiện "% · đang kết nối lại…".
- Bước 9 hiển thị từng model đang tải + "model 2/3".
- *(Open cho plan: tên field + cách sidecar đếm — chốt khi code; nếu vướng có thể tách %+ETA thành đợt sau, nhưng mục tiêu là làm trong đợt này.)*

## 10. setup.json v2 + migration

- Field mới: `dets`, `activeDet`, `recs`, `activeRec`, `langs`, `activeLang`, `llmModels`, `activeLlm` (null = chưa hỏi; `[]` = đã hỏi "không chọn").
- **`dets`/`recs`/`activeDet`/`activeRec` lưu TÊN MODEL** (vd `PP-OCRv6_small_det`) — không lưu token cỡ: để biểu diễn được entry ngoài pattern `<cỡ>_<kind>` sau này, và preselect so trực tiếp với `settings.OcrDet/OcrRec`. *(Chốt sau builder-check.)*
- **`langs`/`activeLang` cũng lưu TÊN MODEL rec legacy** (vd `korean_PP-OCRv5_mobile_rec`): hôm nay 1:1 với ngôn ngữ, nhưng khi một ngôn ngữ có nhiều "Bản" thì hai field này giữ **bản đã chọn / bản đang đọc** — không cần field phụ.
- Bỏ pause ngôn ngữ; field `language` cũ **không ghi mới nữa**. Migration một lần khi gặp file 16-11 (có `tiers`, chưa có `dets`):
  - `dets = [PP-OCRv6_<t>_det | t ∈ tiers]`, `recs = [PP-OCRv6_<t>_rec | t ∈ tiers]`; `activeDet = PP-OCRv6_<activeTier>_det`, `activeRec = PP-OCRv6_<activeTier>_rec`;
  - `langs = extraLangs ∪ {language nếu là ngôn ngữ legacy}`; `activeLang = language nếu legacy` (giữ đúng cặp đang chạy cũ);
  - `llmModels = null` ⇒ máy cũ thấy bước 4 hỏi một lượt (pattern chấp nhận như đợt 16-11).
- Marker mới: `choosingocrmodels`, `choosinglanguages`, `choosingllmmodels` (pre-pipeline — không bị `ClearPipelineSteps` xoá).
- **Reconcile bổ sung**: `llmModels ≠ ∅` mà runtime thiếu/cũ ⇒ clear marker bước 8; GGUF trong `llmModels` chưa có ⇒ clear marker bước 9 (chi tiết phép kiểm tra — filesystem hay RPC list — chốt ở plan).

## 11. Settings — nhóm rõ (nhỏ)

- Mục OCR đã có 2 select det/rec → giữ, đổi tiêu đề nhóm khớp wizard ("Model nhận diện khung chữ (det)" / "Model nhận dạng chữ (rec)"); bảng model (tải/xoá) giữ nguyên.
- Tách tiêu đề nhóm **"Ngôn ngữ"** cho các rec legacy trong bảng; mục **LLM** nhóm theo SizeClass động (đồng bộ cơ chế bước 4).
- (Vấn đề user: "không biết đang chọn OCR hay ngôn ngữ hay LLM" — soi lại toàn mục khi làm.)

## 12. i18n & tên

- ~25 key mới/đổi ×2 ngôn ngữ: tiêu đề 3 màn, nhãn 2 khối, nhắc min-1 khối, "Đọc chính", hint trống từng màn, nhóm LLM (template `{0}` = SizeClass), nhắc bỏ qua bước 8, chuỗi %, ETA, "đang kết nối lại…", nhóm Settings.
- **Fallback bắt buộc**: cỡ/quant/ngôn ngữ lạ → hiện raw token/mã (không vỡ UI, không hiện key i18n).

## 13. Files dự kiến

- `ReTran.App/Services/Setup/SetupCoordinator.cs` — 3 step mới, 3 continue, tải theo dets/recs/langs/llmModels, applying (activeDet + activeRec hoặc activeLang), reconcile mở rộng, bước 8 có điều kiện + so tag.
- `ReTran.App/Services/Setup/SetupState.cs` — 8 field mới + migration.
- `ReTran.App/Services/Setup/OcrModelChoices.cs` — chuyển thành derive-từ-catalog (bỏ hardcode cỡ + 8 ngôn ngữ + routing-first).
- `ReTran.App/Components/Shared/SetupModal.razor` — 3 màn mới thay 2 màn cũ.
- `ReTran.App/Services/I18nStrings.cs` — keys mới.
- `ReTran.Core/Llm/LlmModelCatalog.cs` (+ JSON) — chuyển nguồn dữ liệu; `LlamaRuntimeManager` — so tag cũ/mới.
- `ReTran.OCR/src/retran_core/` — đếm bytes khi tải (progress) cho `ocr.models.list`.
- `ReTran.App/Core/PipelineController.cs` — parse progress; `SettingsPanel.razor` — nhóm.
- Tests: `SetupCoordinatorTests`, `SetupStateTests`, `OcrModelChoicesTests` (derive), `I18nServiceTests`, Core LLM tests, sidecar tests.
- KHÔNG đụng: capture, OCR engine runtime, overlay server, màn Checking/Failed.

## 14. Test chính (pin hành vi)

1. **Flow**: Start → ChoosingOcrModels → ChoosingLanguages → ChoosingLlmModels → pipeline; không còn pause ngôn ngữ; answers đủ ⇒ pass-through.
2. **Min-1 mỗi khối**: continue với 0 det hoặc 0 rec → ArgumentException; UI khoá (helper thuần test).
3. **Đọc chính**: có tick ⇒ chấm luôn tồn tại (tự gán dòng tick đầu; bỏ tick dòng giữ ⇒ dời dòng kế); `activeLang ∈ langs` ⇒ Applying rec = rec legacy của lang; không tick ⇒ `activeLang = null` ⇒ rec = `activeRec` (helper).
4. **LLM ∅** ⇒ bước 8 mark done KHÔNG gọi `llm.runtime.*`; LLM ≠ ∅: runtime thiếu ⇒ tải; tag cũ ⇒ gọi tải lại; mới nhất ⇒ skip; mất mạng khi check ⇒ skip như bản ok.
4b. **LLM active**: `activeLlm ≠ null` ⇒ Applying ghi `AppSettings.LlmActiveModel` + gọi `llm.models.set` (RPC từ chối ⇒ vẫn ghi settings); reconcile: settings lệch `activeLlm` ⇒ clear `applying`.
5. **Reconcile mới**: rerun thêm LLM tick ⇒ clear marker 8 + 9 để tải bù; máy khớp ⇒ không clear (không hồi quy).
6. **Migration**: file 16-11 → map đúng từng field dạng TÊN MODEL (`dets`/`recs` mở từ `tiers`; `activeDet`/`activeRec` từ `activeTier`; `activeLang` từ `language` legacy; `llmModels = null`).
7. **Derive**: catalog giả thêm `PP-OCRv6_big_det/rec` ⇒ danh sách cỡ tự có `big`; `eslav` KHÔNG tạo "Bản" (tập con thật của `cyrillic`); fake `korean_…_server` cùng `langs` với mobile ⇒ dòng 한국어 hiện 2 "Bản"; xoá hết legacy ⇒ màn Ngôn ngữ trống nhưng không vỡ.
8. **% ETA (helper)**: có `progress` đủ ⇒ %/ETA đúng; thiếu total ⇒ indeterminate; speed 0 ⇒ chuỗi "đang kết nối lại".

## 15. Rủi ro / open items

- **Sidecar + Core phải thêm progress** — chạm 2 process; nếu trượt, %+ETA tách đợt sau (ưu tiên vẫn làm trong đợt này).
- **LLM catalog JSON**: format chốt khi code; file thiếu/hỏng ⇒ list rỗng + log, UI hiện "chưa có model" (không crash).
- **Migration một lượt hỏi bước 4** cho máy cũ — chấp nhận.
- **Kiểm tra bản runtime mới** phụ thuộc GitHub API — mất mạng ⇒ giữ bản đang cài, không chặn.
- Mockup đã duyệt bố cục; chữ nghĩa chỉnh nhẹ khi code.

## 16. Điểm cần user chốt

- Đã chốt toàn bộ qua hội thoại 16–17/11. **Call sau builder-check (17-11)**: (i) chấm LLM có hiệu lực — Applying ghi `LlmActiveModel` + `llm.models.set`, KHÔNG tự đổi provider; (ii) `dets`/`recs` lưu **tên model**; (iii) "Bản" theo nhóm-ngôn-ngữ-chính đã lọc tập-con-thật (`eslav`/`chinese_cht` không tạo bản).
- Open duy nhất: **có thêm bản server ch/TC/en/ja vào catalog ngay không?** — mặc định: **không**, để sau (cơ chế "chọn bản" có sẵn).
