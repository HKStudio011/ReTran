# Thiết kế: OCR model manager + ảnh OCR qua shared memory + HTTP overlay (bỏ camera ảo)

Ngày: 2026-10-22
Trạng thái: chờ user duyệt (chưa build gì)

## 1. Vì sao (intent)

Sau khi đo và rà lại đường ống, ba vấn đề độc lập nhau lộ ra:

1. **Model OCR bị hard-code.** `paddle_engine.py` pin `PP-OCRv5_mobile_det` +
   `en_PP-OCRv5_mobile_rec`, không có chỗ cho user đổi. Project đang ở
   `paddleocr 3.4.1` / `paddlepaddle-gpu 3.3.1`, trong khi upstream đã có
   **PaddleOCR 3.7.0 + PP-OCRv6** (tiny/small/medium, một model phủ 50 ngôn
   ngữ CN/TC/EN/JP + 46 Latin — **không có tiếng Hàn**). Model v6 nhỏ (det
   1,9/9,6/59,4 MB; rec 4,4/20,4/73,3 MB) nên việc tải/đổi/xoá rất hợp lý.
2. **Ảnh OCR đi qua JSON-RPC stdio dạng base64 JPEG.** Đo trên khung
   Arknights 1920×1080: 368 KB JPEG q85 → 490 KB base64 → **1,44 MB/s @3 fps**,
   sát trần budget IPC 1,5 MB/s; cộng thêm ~5–10 ms encode ở Core và ~10–20 ms
   decode ở sidecar mỗi chu kỳ, và JPEG 4:2:0 làm hại chữ nhỏ/chữ màu — đúng
   chỗ game hay đau. Ảnh không đi đâu xa (cùng máy), nên shared memory là
   đường đúng: raw BGRA 8,3 MB × 3 fps = 25 MB/s qua shm, không đáng kể.
3. **Camera ảo là đường đắt và thừa cho mục tiêu hiện tại.** OBS phải tự
   capture cửa sổ để có **âm thanh**; một khi OBS đã có video + audio thì nguồn
   hình của ReTran là thừa. Camera còn kéo theo annotate + JPEG + App decode +
   DShow filter mỗi khung chỉ để đạt ~8,5–10 fps nội dung. HTTP overlay (chỉ
   gửi box JSON ~2,5 KB/khung) rẻ hơn nhiều và giữ nguyên chất lượng hình do
   OBS capture. User đã chốt: **bỏ camera, dùng HTTP overlay**.

## 2. Mục tiêu

- User **chọn / tải / xoá / đổi** model OCR được, có UI và có thông báo chọn
  model khi khởi động mà chưa có model nào.
- Ảnh OCR đi qua **shared memory** (raw), bỏ base64 + JPEG khỏi đường OCR.
- Nâng OCR lên **PaddleOCR 3.7.0 + PP-OCRv6**, đo lại và cập nhật budget.
- **Bỏ camera ảo** khỏi pipeline; OBS lấy overlay qua **HTTP overlay**
  (Browser Source); OBS tự lo video + audio.
- Giữ nguyên overlay cửa sổ trên game (Win32 GDI) và preview trong app.
- **Phủ ngôn ngữ**: v6 cho nhóm phổ biến, v5 per-language cho phần còn lại
  (ko, ru, …) — user chọn/tải trong model manager.

## 3. Không mục tiêu (out of scope)

- **Preview trong app**: giữ nguyên như hiện tại (annotated JPEG ~10 fps qua
  `Local\ReTran.Preview.V1`). Mọi phương án preview full-fps/MJPEG/GPU surface
  để việc riêng.
- **Hook Rust cho exclusive fullscreen**, HDR, Windows.Graphics.Capture (WGC):
  không làm trong đợt này. Window capture giữ PrintWindow + DWM thumbnail như
  hiện tại.
- **Xuất hình cho Zoom/Discord**: chấp nhận mất khi bỏ camera (user đã chốt).
- Không đổi định dạng/kiến trúc `frame.translated`, `frame.annotated`, preview
  MMF hay overlay window.

## 4. Quyết định đã chốt (user)

| # | Quyết định |
| - | ---------- |
| D1 | Bỏ camera ảo; OBS nhận overlay qua HTTP overlay (Browser Source); OBS lo video + audio. |
| D2 | Ảnh OCR qua shared memory, không base64/không JPEG. |
| D3 | Nâng OCR lên PaddleOCR 3.7 + PP-OCRv6; user tự chọn model (manager), không pin cứng. |
| D4 | Có UI nhắc chọn model khi khởi động và chưa có model nào. |
| D5 | Preview để riêng, không gộp vào đợt này. |
| D6 | `ReTran.Camera` **xoá hẳn** (code, solution, copy target, registrar, services, tests) và gỡ đăng ký filter DShow đã cài trên máy. |
| D7 | Không chỉ tiếng Hàn: catalog có thêm **model v5 per-language** cho mọi ngôn ngữ ngoài phạm vi v6, user tự tải khi cần. |
| D8 | Không bundle model trong installer; model cũ trong `~/.paddlex` **được migrate một lần** (đó đúng là thư mục PaddleX tải model về). |

## 5. Quyết định của người thiết kế (có thể override)

| # | Quyết định | Lý do |
| - | ---------- | ----- |
| C1 | Model mặc định gợi ý: `PP-OCRv6_small_det` + `PP-OCRv6_small_rec` (first-run vẫn bắt user bấm chọn/tải). | Cân bằng accuracy/size; tiny yếu hơn hẳn ở cột chữ khó. |
| C2 | Thư mục model do app sở hữu: `%LOCALAPPDATA%\ReTran\models`, set qua `PADDLE_PDX_CACHE_HOME` khi spawn sidecar; lần đầu chạy **copy một lần** các model thuộc catalog từ `~/.paddlex/official_models` (nếu có) sang, kèm marker để không lặp lại. | Đã verify `paddlex/cache.py` đọc env này và `~/.paddlex/official_models` là nơi PaddleX tải model về; migrate để user hiện hữu không phải tải lại. |
| C3 | Đổi model → **dựng lại engine trong sidecar**; nếu VRAM không nhả thì restart sidecar. | Paddle khó đoán chuyện nhả VRAM; restart là đường chắc. |
| C4 | Catalog có nhóm **"ngôn ngữ v6 chưa phủ"** dùng model v5 per-language đã có trong registry: `korean_PP-OCRv5_mobile_rec` (ko), `cyrillic_PP-OCRv5_mobile_rec` (ru), `arabic_`, `th_`, `ta_`, `te_`, `devanagari_`, `el_`, `eslav_`, `chinese_cht_PP-OCRv3_mobile_rec`… (danh sách chốt khi làm). | v6 hợp nhất phủ zh/en/ja + 46 Latin; ReTran có src/dst `ko`, `ru` nằm ngoài phạm vi đó. |
| C5 | Trang `/overlay` phục dựng theo thiết kế cũ đã xoá (transparent page + SSE), không bê nguyên code cũ. | Code cũ đã bị xoá có chủ đích; làm lại gọn theo style hiện tại. |
| C6 | Xoá hẳn `ReTran.Camera` khỏi repo (D6). Việc gỡ đăng ký DShow làm **một lần, thủ công, có ghi chú** (unregister trước khi xoá DLL); không tự động gỡ trong app. | Tránh app làm thao tác một chiều không mong muốn; dev/user chủ động chạy 1 lệnh. |
| C7 | Shm OCR giữ một slot + seqlock (không ring buffer). | OcrLoop hiện gọi tuần tự, await từng chu kỳ; ring buffer chưa cần. |
| C8 | Đổi model khi pipeline đang chạy: **hoãn tới phiên kế tiếp** (UI ghi "áp dụng sau khi Dừng"); khi không có phiên thì dựng lại engine tại chỗ. Không thêm cơ chế respawn/auto-resume sidecar. | Tránh nhánh vòng đời phiên + supervision mới; Core biết `_session` còn sidecar thì không. |
| C9 | `ocr.models.download` chạy ở luồng nền trong sidecar; `ocr.spot` vẫn tuần tự, không bị chặn. | Sidecar 1 luồng handler đồng bộ; tải model không được làm rớt frame. |
| C10 | `core.start` với cặp det/rec chưa cài → lỗi **-32603 `ocr-model-missing`**; App mở hộp thoại chọn model. **Không tải ngầm** khi start. Warmup chỉ chạy khi đã có cặp model được cấu hình. | Giữ đúng D4 (user chủ động chọn/tải); warmup hiện tại (dummy spot lúc `--serve`) sẽ tự tải model khi chưa chọn. |
| C11 | Cặp det/rec đã chọn lưu trong `settings.json` của App (`OcrDet`, `OcrRec`), App gửi kèm **mọi** `core.start`; `active` trong `ocr.models.list` = cặp engine sidecar đang giữ. | Không có chỗ lưu nào khác; restart App/Core không được mất lựa chọn. |
| C12 | Bỏ field `translatedLang` khỏi payload `/events`; trang `/overlay` dùng font stack phủ CJK. | `OverlayBox`/`frame.translated` không có lang; field per-box sẽ là dữ liệu chết. |

## 6. Thiết kế

### 6.1 Model manager

**Nơi lưu:** `%LOCALAPPDATA%\ReTran\models\official_models\<model_name>\...`
(PaddleX tự tạo layout). Sidecar được spawn với `PADDLE_PDX_CACHE_HOME` trỏ
vào đây. Có thể set `PADDLE_PDX_MODEL_SOURCE` (mặc định huggingface) làm
setting nâng cao.

**RPC (Core proxy → sidecar):**
- `ocr.models.list` → `{catalog:[{name, kind:"det"|"rec", langs:[...], sizeMb,
  installed, inUse}], active:{det, rec}}`
- `ocr.models.download {name}` → tải **ở luồng nền**, không chặn `ocr.spot`
  (tiến trình báo qua event `ocr.models.progress` hoặc poll `list`) (C9)
- `ocr.models.delete {name}` → lỗi nếu đang dùng
- `ocr.models.set {det, rec}` → dựng lại engine ngay khi không có phiên chạy;
  đang chạy thì App hoãn tới phiên kế tiếp (C8)
- `core.start` nhận thêm `ocr: {det, rec}`; cặp chưa cài → -32603
  `ocr-model-missing` và App mở hộp thoại chọn model, không tải ngầm (C10)
- Cặp đã chọn lưu ở `settings.json` (`OcrDet`/`OcrRec`) và gửi kèm mọi
  `core.start` (C11)

**Catalog:** tĩnh trong code sidecar, hai nhóm:
- **v6 (khuyến nghị):** tiny/small/medium det + rec, phủ zh/TC/en/ja + 46 Latin.
- **Ngôn ngữ v6 chưa phủ (v5 per-language):** `korean_PP-OCRv5_mobile_rec`,
  `cyrillic_PP-OCRv5_mobile_rec`, `arabic_`, `th_`, `ta_`, `te_`, `devanagari_`,
  `el_`, `eslav_`, `chinese_cht_PP-OCRv3_mobile_rec`… (registry đã có trong
  paddleocr 3.4.x; chốt danh sách cuối khi làm).
Mỗi mục kèm metadata ngôn ngữ để UI gợi ý theo src/dst đang chọn; trạng thái
`installed` suy ra từ thư mục model.

**UI:**
- Cài đặt → mục **OCR model**: bảng model (tên, ngôn ngữ, dung lượng, trạng
  thái), nút Tải/Xoá/Chọn, hiển thị model đang dùng.
- **First-run**: khi mở app (hoặc lúc bấm Bắt đầu) mà `ocr.models.list` không có
  model nào `installed` → hiện hộp thoại bắt chọn & tải model mới cho Start.
- Cảnh báo rõ model không phủ tiếng Hàn nếu user chọn src/dst `ko` mà model
  không hỗ trợ.

### 6.2 OCR qua shared memory

- MMF: `Local\ReTran.Ocr.V1`.
- Header cố định: magic, version, `seq` (seqlock), `width`, `height`, `stride`,
  `format = BGRA32`, `payloadOffset`, `heartbeatUtc`.
- Payload: đúng một khung, **full-res** (không downscale phía Core; Paddle tự
  chuẩn hoá det, còn rec crop lấy từ ảnh full-res nên nét hơn).
- Luồng: Core ghi khung (seq lẻ → ghi → seq chẵn) rồi gọi `ocr.spot {seq, w, h}`
  (không còn `image`). Sidecar map MMF một lần, chờ `seq`, đọc có retry seqlock,
  cắt alpha (`arr[:, :, :3]` → BGR view, không copy) rồi chạy Paddle.
- Lỗi mở MMF ở sidecar (rủi ro Windows `Local\` + Python `mmap` — xem §8-R1):
  log rõ và **fallback** về đường base64 JPEG hiện tại trong cùng handler; chỉ
  giữ fallback cho tình huống mở shm thất bại, không phải đường chính.
- Kỳ vọng: `rpc_MBps` của `ocr.spot` ≈ 0 (chỉ metadata); Core bỏ encode; sidecar
  bỏ decode; OCR sạch artifact JPEG.

### 6.3 HTTP overlay (thay camera)

- Server local trong App, bind `127.0.0.1`, port mặc định **17863**, tự +1 khi
  bận (giống thiết kế cũ đã xoá).
- `GET /overlay` → trang trong suốt (nền transparent), vẽ tick + pill + chữ
  theo dữ liệu SSE; kích thước hiển thị bám canvas OBS.
- `GET /events` → SSE `{"seq":n,"boxes":[{x,y,w,h,text,translated}]}`
  toạ độ chuẩn hoá 0–1, lấy từ `OverlayState` (cùng nguồn với overlay cửa sổ);
  không có `translatedLang` (C12).
- App có nút **Copy URL** + hướng dẫn ngắn(OBS → Browser Source → dán URL,
  đặt 1920×1080, tick "Shutdown source when not visible" tuỳ chọn).
- Bảo mật: chỉ loopback, không auth (như thiết kế cũ).
- Không cần thêm gì cho audio — OBS capture cửa sổ như bình thường.

### 6.4 Xoá camera ảo (D6)

Xoá hẳn khỏi repo:
- `ReTran.Camera/` (project native) + mục `<Build Project="false">` trong
  `ReTran Project.slnx`.
- Copy target `ReTran.Camera.dll` trong `ReTran.App.csproj`.
- Gọi `new CameraRegistrar().EnsureRegistered()` trong `MainPage.xaml.cs`.
- `ReTran.App/Services/Camera/*`; card Camera + nút copy tên thiết bị trong UI.
- 4 file test camera (`CameraRegistrarTests`, `CameraProtocolTests`,
  `CameraOutputServiceTests`, `CameraNativeInteropTests`) + mọi test liên quan
  trong `PipelineController`/`OverlayWindowService`.
- App thôi publish `Local\ReTran.Camera.V1`.

Giữ nguyên: Core annotate loop cho **preview** (`frame.annotated` + preview MMF).

Dọn máy đã cài filter (thủ công, một lần, ghi trong release note): chạy
`regsvr32 /u <đường dẫn cũ>\ReTran.Camera.dll` **trước khi** xoá DLL, rồi xoá
file build cũ. Không tự động gỡ trong app (C6).

### 6.5 Nâng OCR lên 3.7 / PP-OCRv6

- `pyproject.toml`: `paddleocr>=3.7.0`, `paddlepaddle-gpu==3.4.0` (index cu129
  đã có wheel Windows cp313 — đã kiểm tra listing), giữ index/source hiện tại.
- `paddle_engine.py`: nhận `det_model`/`rec_model` từ config thay vì pin cứng;
  thử **bỏ** workaround `enable_mkldnn=False` trên paddle 3.4 (lỗi oneDNN/PIR
  của 3.3.x có thể đã fix) — nếu crash thì giữ lại, ghi chú.
- Warmup: bỏ dummy spot lúc `--serve`; chỉ warm cặp model khi đã cấu hình
  (sau `core.start` đầu tiên hoặc sau `ocr.models.set`) (C10).
- Ngôn ngữ: v6 unified phủ CN/TC/EN/JP + 46 Latin; các ngôn ngữ ngoài phạm vi
  (ko, ru, ar, th, …) dùng model v5 per-language trong catalog (C4). Bỏ cơ chế
  `f"{lang}_PP-OCRv5_mobile_rec"` cứng; model rec chọn theo catalog.
- Sau khi nâng: chạy lại pytest + A/B v5 vs v6 trên ≥20 khung game thật (cùng
  bộ ảnh), đo số box/chữ đúng, latency/chu kỳ, VRAM/RAM; ghi vào
  `Docs/performance.md` (kèm verdict budget OCR).

## 7. Nghiệm thu

| # | Tiêu chí | Cách đo |
| - | -------- | ------- |
| A1 | Model manager: fresh profile (không model) → hộp thoại chọn model; tải/xoá/đổi chạy được; xoá model đang dùng bị chặn; đổi model không cần restart app | checklist tay + unit test sidecar |
| A2 | `ocr.spot` không còn base64/JPEG: stdio `rpc_MBps` ≈ 0 cho ocr.spot; stdio tổng ≤ 1,5 MB/s | probe như `Tools/Perf/probe_after.ps1` |
| A3 | OCR v6: A/B trên cùng bộ khung, ghi số + verdict vào `Docs/performance.md` | script benchmark |
| A4 | HTTP overlay: OBS Browser Source hiện box khớp game (sai số ≲2% cạnh canvas), không cần nguồn hình ReTran, audio OBS nguyên vẹn | quan sát + screenshot |
| A5 | Bỏ camera: không còn ghi `Local\ReTran.Camera.V1`, không còn UI camera, test cũ pass | grep + test suite |
| A6 | Không regression: `ReTran.Tests` (full suite) + `uv run pytest tests -v` xanh | CI/local run |
| A7 | Migrate model: profile có sẵn `~/.paddlex` (v5 mobile det + en rec) → lần chạy đầu tự copy sang `%LOCALAPPDATA%\ReTran\models`, không hiện first-run, không tải lại | checklist + so dung lượng thư mục |
| A8 | Xoá camera: `grep -r "ReTran.Camera\|CameraRegistrar\|ReTran.Camera.V1" ReTran.App ReTran.Tests ReTran.Core` → 0 kết quả; solution không còn project; không còn publish shm camera | grep + build |

## 8. Rủi ro

| # | Rủi ro | Giảm thiểu |
| - | ------ | ---------- |
| R1 | Python `mmap` mở named mapping `Local\...` trên Windows hành xử khác `MemoryMappedFile` của .NET (namespace, quyền) | **Spike đầu tiên**: C# tạo MMF + Python mmap đọc thử, phải chạy được trước khi làm tiếp; fallback base64 giữ trong lúc chuyển |
| R2 | Paddle 3.4 + v6 đổi hành vi (oneDNN, warmup, VRAM) | Thử ở nhánh riêng, đo trước–sau; giữ đường pin cũ trong `pyproject` nếu fail |
| R3 | VRAM/RAM sidecar tăng khi đổi model (đang ~2 GB, ~362 MB VRAM) | Đo từng tier; restart sidecar khi đổi; cảnh báo trong UI |
| R4 | v6 không phủ một số ngôn ngữ (ko, ru, ar, th, …) | C4: catalog v5 per-language + UI cảnh báo khi src/dst chưa có model phù hợp |
| R5 | Xoá camera làm regression cho user đang dùng OBS/Zoom | Xác nhận user không cần Zoom/Discord (đã chốt D1); ghi rõ trong release note |
| R6 | Port 17863 bị chiếm | fallback +1 như thiết kế cũ |
| R7 | Migrate `~/.paddlex` lỗi (đĩa đầy, file đang bị giữ) | Copy best-effort từng model, log; model nào fail thì hiện như "chưa cài" và cho tải lại; marker ghi sau khi xong |

## 9. Câu hỏi mở

Không còn câu hỏi mở — Q1/Q2/Q3 đã được user trả lời ngày 2026-10-22 (xem D6,
D7, D8). Câu hỏi mới (nếu có) sẽ bổ sung khi review plan.

## 10. Việc để builder quyết

- Cấu trúc thư mục header/shm chi tiết, tên field, versioning.
- Cách sidecar báo tiến trình tải model (SSE/poll) — chọn cách đơn giản nhất đủ
  dùng trong UI.
- Có tái dùng CSS/tick style hiện tại cho trang `/overlay` hay viết lại tối
  giản (chỉ cần khớp hình dạng box, màu teal, pill tối).
- Thứ tự triển khai và cách chia commit (sẽ nằm ở plan, không phải spec này).
