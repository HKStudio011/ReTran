# Thiết kế: Setup Wizard lần đầu chạy (First-Run Auto-Setup)

Ngày: 2026-11-03 · Trạng thái: draft chờ user duyệt
Thứ tự thực hiện: **việc 1/3** — sau này đến UI quản lý model, rồi UI profile/cache dịch (hai việc đó có spec riêng).

## 1. Bối cảnh & ý định

Release zip hiện tại chỉ chứa 3 binary self-contained (App + Core + Updater — `release.yml:65-82`). Máy chưa từng chạy ReTran giải nén zip → app mở được, UI chạy được, nhưng bấm Start thì `core.start` trả về `-32603 "sidecar not available (python/venv missing or spawn failed)"` (`ReTran.Core/Program.cs:67-84`, `ReTran.Core/Serve/PipelineHost.cs:155`). Đã xác nhận bằng thí nghiệm thật 2026-11-03: chạy `ReTran.Core.exe --serve` với CWD ở cây thư mục sạch (không có `ReTran.OCR/pyproject.toml` ở tầng cha nào) → đúng lỗi trên, cả `core.start` lẫn `ocr.spot`.

Nguyên nhân gốc: Core chỉ tìm được sidecar khi (1) `FindRepoRoot()` đi ngược từ CWD thấy `ReTran.OCR/pyproject.toml` (`OcrBootstrap.cs:16-22`) và (2) có python dùng được — hoặc `PythonExe` trong `%LOCALAPPDATA%\ReTran\core\config.json`, hoặc venv uv nằm trong repo layout (`OcrBootstrap.cs:29-37`). Máy mới không có gì trong số đó.

Ý định: **người dùng lạ giải nén zip là chạy được, không bao giờ đụng terminal hay tự cài thêm gì** — tiêu chí tự động cao nhất có thể, theo kiểu Hermes/Unsloth Desktop. Thiết bị phân phối vẫn là zip (không làm installer MSI/Inno).

## 2. Mục tiêu / Không phải mục tiêu

**Mục tiêu**

- Lần đầu chạy `ReTran.App.exe` trên máy sạch: modal setup tự động cài đặt đủ để pipeline hoạt động (uv → venv → model), chỉ hỏi user đúng những chỗ cần người chọn.
- Resume: fail giữa chừng hoặc bỏ qua rồi mở lại app → tiếp tục đúng bước hỏng, không hỏi lại câu đã trả lời.
- Chạy được trên cả máy có NVIDIA lẫn máy không có GPU rời (AMD/iGPU/none) bằng bản paddlepaddle CPU.

**Không phải mục tiêu (non-goals)**

- Không làm installer Windows; zip + self-setup là mô hình phân phối.
- Không hỗ trợ Linux/macOS.
- Không có chế độ "dùng app không OCR" — bỏ qua setup = đóng app.
- Không chạy song song nhiều model nhận chữ (engine chỉ giữ một cặp det/rec tại một thời điểm).
- App update ghi đè source `ReTran.OCR` (code sidecar mới hiệu lực tự động nhờ uv cài project kiểu editable) nhưng **không tự chạy `uv sync`** — nếu một release đổi dependencies, venv cũ thiếu package → sửa bằng nút "Chạy lại setup" (tradeoff chấp nhận theo quyết định user: ưu tiên đơn giản).
- Không nằm trong spec này: UI quản lý model đầy đủ, UI profile cửa sổ + cache dịch (import/export/sửa) — hai việc tiếp theo trong hàng đợi.

**Anti-goals**

- Không được để setup kéo dài vô hạn hay treo UI: mỗi bước có retry hữu hạn và nút thoát rõ ràng.
- Không tự ý nâng cấp/xoá venv hoặc model đã tồn tại ở chế độ dev (repo layout).

## 3. Flow do user quyết định

1. **Modal setup** hiện trên màn hình chính của app (không phải cửa sổ riêng) khi điều kiện first-run chưa đạt (mục 6). Chặn việc dùng pipeline cho tới khi xong hoặc bỏ qua.
2. **Bước Check system chạy đầu tiên, tự động hoàn toàn, không hỏi gì** — đây là cổng vào của mọi bước sau. Kết quả check quyết định các bước tiếp theo (GPU hay CPU, bước nào được skip vì đã có sẵn).
3. **Các câu hỏi cần user chọn (chọn ngôn ngữ/model) chỉ xuất hiện SAU khi check system pass.**
4. **Chọn ngôn ngữ**: chọn 1 ngôn ngữ chính của nội dung muốn nhận chữ (không đa-chọn). Ngôn ngữ thuộc phủ v6 → dùng cặp `PP-OCRv6_small`; ngoài phủ → thêm model rec bản 5 theo bộ chữ. Bước này chỉ tải những model còn thiếu.
5. **GPU/CPU**: NVIDIA **rời** + driver đủ mới → cài bản GPU (`paddlepaddle-gpu`, wheel `cu130`/`cu129`/`cu126` theo nấc driver ở mục 10). Phát hiện GPU rời AMD → **thông báo rõ "không cài được bản GPU cho card AMD trên Windows, sẽ thay bằng bản CPU"** rồi tải bản CPU. Không có GPU rời → bản CPU với thông báo nhẹ tương tự.
6. **Lỗi ở bất kỳ bước nào**: tự retry 3 lần. Vẫn lỗi → màn hình lỗi hiển thị thông điệp + đúng 2 nút: **Thử lại** (chạy lại bước đó) và **Bỏ qua** (đóng app). Lần chạy sau modal hiện lại vì điều kiện chưa đạt.
7. **Toàn bộ trạng thái OCR nằm trong thư mục app**: `.venv` và `models` đều ở `<appDir>\ReTran.OCR\` (models cùng cấp với `.venv`, KHÔNG đặt trong `%LOCALAPPDATA%`). Updater khi update **không được xoá** các thư mục này — code mới ghi đè lên, venv/models giữ nguyên.
8. **Release đổi code sidecar**: update ghi đè source `ReTran.OCR` là đủ (code mới tự hiệu lực), không cần cơ chế re-sync hay version stamp.

## 4. Các bước của wizard (trình tự thực thi)

| # | Bước | Nội dung | Progress |
|---|------|----------|----------|
| 1 | Check system | OS/arch win-x64; GPU NVIDIA + phiên bản driver; GPU rời AMD?; đĩa trống; mạng (PyPI, index paddle, host model); trạng thái hiện có (uv, venv, model) → quyết định flavor GPU/CPU + danh sách bước cần chạy | nhanh, không bar |
| 2 | Hỏi ngôn ngữ | 1 câu hỏi duy nhất: ngôn ngữ chính (mặc định gợi ý theo ngôn ngữ nguồn đang set trong settings). Hiển thị trước khi xác nhận: những model sẽ tải + tổng dung lượng | — |
| 3 | Verify file cài đặt | Source `ReTran.OCR\` + `runtimes\win-x64\uv.exe` phải có sẵn trong app dir (zip đã kèm). Thiếu → thẳng màn hình lỗi với thông điệp "hãy giải nén lại zip đầy đủ" (retry không cứu được) | nhanh |
| 4 | `uv sync` (+flavor) | Đảm bảo `pyproject.toml` khớp flavor (CPU: bỏ pin GPU + 2 section index/sources; GPU: giữ nguyên), chạy `uv sync` tại `<appDir>\ReTran.OCR\` → tạo `.venv` (CPython 3.13 managed + `retran_core`); flavor CPU thêm `uv pip install "paddlepaddle==3.4.0"` | bar + tốc độ/dung lượng (bước lâu nhất) |
| 5 | Xác minh sidecar | Khởi động lại tiến trình Core (đường kill+start có sẵn của App); ping + gọi `ocr.models.list` để chắc sidecar sống và catalog đọc được. Core tự tìm python bằng cơ chế HIỆN CÓ, không cần thay đổi: `FindRepoRoot()` từ CWD=app dir thấy `<appDir>\ReTran.OCR\pyproject.toml`, fallback venv tại `OcrBootstrap.cs:35-36` | nhanh |
| 6 | Tải model | Sau khi sidecar sống (bước 5): gọi RPC mới `ocr.models.recommend {src}` để lấy cặp det/rec chính xác cho ngôn ngữ đã chọn — routing nằm trong `recommended()` của `catalog.py`, không duplicate logic sang C#; gọi `ocr.models.download` cho từng model thiếu trong cặp; chờ hết thread nền (marker `.partial` biến mất) | bar tổng + từng model |
| 7 | Áp dụng lựa chọn | Ghi `OcrDet`/`OcrRec` vừa nhận vào `settings.json` — đường `core.start` hiện có (`PipelineController.cs:238`) sẽ verify installed + set pair cho mọi phiên sau, nên không cần persistence phía sidecar | nhanh |
| 8 | Xong | Ghi trạng thái hoàn tất vào `setup.json`; đóng modal; app dùng bình thường | — |

Retry ×3 áp dụng cho các bước 4–7 (mỗi lần retry là một lần chạy lại bước đó từ đầu). Bước 1 fail (thiếu điều kiện cứng như sai arch, hết đĩa) và bước 3 thiếu file → thẳng tới màn hình lỗi, không retry vô nghĩa.


## 5. Quyết định kỹ thuật (của controller — user chưa duyệt riêng từng mục)

Mục này là các lựa chọn "làm thế nào" do tôi quyết để hoàn tất thiết kế; user phản đối bất kỳ mục nào khi đọc tài liệu.

1. **Layout app dir = layout dev** (quyết định của user, mục 3.7): `<appDir>\ReTran.OCR\` chứa source + `.venv` + `models` — cùng hình dạng với repo dev (`repo/ReTran.OCR/.venv`). Hệ quả: Core chạy đúng nguyên code hiện có (FindRepoRoot từ CWD=app dir), không cần cơ chế OCR home riêng, không cần ghi config nào cho Core tìm python. Tradeoff chấp nhận: di chuyển/xoá thư mục app = mất venv + models → setup lại từ đầu.
2. **Updater giữ nguyên hành vi overwrite-only**: `UpdateStager.CopyDirectory` (`UpdateStager.cs:110-117`) chỉ ghi đè file có trong zip, không xoá file thừa trong app dir — `.venv`/`models` tự sống qua update (đã verify code 2026-11-03). Để bảo đảm này vĩnh viễn: thêm unit test khoá hành vi "file/thư mục lạ trong app dir sống sót sau CopyDirectory" + comment tường minh tại `UpdateStager.cs` rằng không được thêm logic xoá/clean.
3. **Output kèm thêm 2 thứ, cả hai qua csproj Content** (quyết định của user): source `ReTran.OCR` (`pyproject.toml` + `src/**` → `ReTran.OCR/`) và `uv.exe` standalone pin version (vendor sẵn trong repo tại `runtimes\win-x64\uv.exe`, kèm file `uv.version.txt` ghi phiên bản + SHA256 chính thức để audit khi bump) — đều vào output ở MỌI build/publish bằng `Content` items trong `ReTran.App.csproj`, cùng cơ chế Updater đang dùng (`ReTran.App.csproj:78-84`) và pattern `runtimes/` của user. Hệ quả: local build test được wizard y hệt máy user, CI không cần bước tải/copy gì thêm, setup chỉ phụ thuộc mạng cho Python + wheel (`uv sync`) và model — không có bước tải uv lúc runtime.
4. **Flavor GPU/CPU — cài wheel paddle tường minh, không dùng dependency-group** (sửa theo phát hiện của builder-check: `paddleocr`/`paddlex` KHÔNG khai báo dependency nào vào `paddlepaddle` — đã verify metadata PyPI — nên không flavor nào tự kéo được wheel; và đặt paddle CPU ở main deps + GPU ở group thì uv từ chối resolve lockfile vì hai group phải tương thích nhau):
   - **Flavor GPU**: `uv sync` trên `pyproject.toml` của zip, **nhưng wizard phải ghi lại URL index theo wheel đã chọn** (`SystemCheckResult.CudaWheel` → `https://www.paddlepaddle.org.cn/packages/stable/<cuXXX>/`) vì `pyproject.toml` trong repo chỉ hard-code index `paddle-cu129` + `[tool.uv.sources]` pin theo nó. Đã xác minh `…/stable/cu126/` và `…/stable/cu130/` đều liệt kê `paddlepaddle-gpu` (fetch 2026-11-06). Nếu index không có bản tương ứng, `uv sync` exit ≠ 0 → retry ×3 → màn hình lỗi (chấp nhận; cần đo trên máy GPU thật, mục 10).
   - **Flavor CPU**: wizard ghi đè `pyproject.toml` tại `<appDir>\ReTran.OCR\` (bỏ 2 dòng pin `paddlepaddle-gpu`/`nvidia-cudnn-cu12` + hai section `[tool.uv.index]`/`[tool.uv.sources]`), rồi `uv sync`, sau đó `uv pip install "paddlepaddle==3.4.0"` (CPU wheel từ PyPI). Wizard LUÔN áp lại flavor trước mỗi lần sync — nên việc update ghi đè pyproject bản GPU trở về app dir không gây lệch: chạy lại setup trên máy CPU sẽ tự sửa.
   - **Áp flavor phải là bản chuẩn (canonical), không sửa kiểu incremental** (ruling 2026-11-06, Task 11 fix): `ApplyFlavorToPyproject` dựng lại đúng hình dạng file chuẩn theo flavor/biến thể — bảo đảm dòng pin `paddlepaddle-gpu==3.4.0` (và `nvidia-cudnn-cu12==9.9.0.52` **chỉ** với cu129), section `[[tool.uv.index]]` + `[tool.uv.sources]` **tồn tại và trỏ đúng biến thể** khi flavor GPU, và **biến mất** khi flavor CPU. Lý do: nếu chỉ sửa các dòng đang tồn tại thì luồng **CPU→GPU** (user đổi card, hoặc wizard dò flavor đã cài lệch với check system rồi sync lại — mục 5.4 này) cho ra pyproject **không còn** dependency GPU lẫn index ⇒ `uv sync` cài môi trường CPU trong khi `setup.json` ghi GPU. Bất biến: áp cùng flavor/biến thể cho ra byte **giống hệt nhau** bất kể trạng thái trước đó; áp cu129 trên file đã bị bản CPU xoá sạch section phải trả lại **đúng file gốc đi kèm zip** (đường updater ghi đè pyproject).
   - **Dò flavor đã cài**: dùng exit code của `uv pip show paddlepaddle-gpu` / `uv pip show paddlepaddle` (đã đo với uv 0.13.7 vendored: installed ⇒ 0, thiếu package hoặc thiếu venv ⇒ 1), **không** parse output của `uv pip list` — bộ gom output của runner chỉ giữ đuôi ~8 KB và `pip list` xếp theo alphabet nên dòng paddle có thể bị cắt ⇒ báo oan "chưa cài".
   - Flavor ghi trong `setup.json`. Chạy lại wizard: dò flavor đang cài bằng `uv pip list` trong venv; lệch với kết quả check system (user đổi card) → chạy lại bước sync theo flavor mới.
   - Validate thật cả hai flavor khi implement, ghi số đo dung lượng/thời gian vào mục 10.
5. **Model chuyển về `<appDir>\ReTran.OCR\models`** (quyết định của user, mục 3.7): Core set `PADDLE_PDX_CACHE_HOME` trỏ tới đây thay vì `%LOCALAPPDATA%\ReTran\models` (`SidecarProcess.cs:48-50`). Python fallback trong `models.py` giữ nguyên cho chạy CLI thủ công. **Một lần migration**: model đã nằm ở `%LOCALAPPDATA%\ReTran\models` (máy dev hiện tại) được copy sang vị trí mới bằng đúng pattern marker có sẵn (`migrate_from_paddlex`, `models.py:311-351` — cơ chế đang dùng cho legacy `~/.paddlex`), chạy idempotent, source chỉ đọc.
6. **Sau setup xong, App khởi động lại tiến trình Core** (không thêm cơ chế lazy-bootstrap bên Core cho v1): Core chỉ resolve sidecar một lần lúc start; đường kill+start core đã có sẵn và được test (UpdateStopper dùng cùng đường này). Không có phiên pipeline nào đang chạy trong lúc first-run (vì không start được), nên restart an toàn.
7. **Check GPU cụ thể**:
   - NVIDIA **rời** (heuristic tên adapter — `GeForce`/`Quadro`/`Tesla`/`NVS`; card datacenter không tên theo pattern rớt về CPU, fail-safe): liệt kê display adapter (WMI `Win32_VideoController` / SetupDI) tìm vendor NVIDIA + đọc phiên bản driver (D3D version từ WMI `Win32_VideoController.DriverVersion`, map về product version của NVIDIA khi implement; dạng "file version của `nvdrv.dll`/registry `nvlddmkm`" không còn trên bản cài NVIDIA hiện đại — đo 2026-11-05), rồi chọn theo **thang wheel**: driver >= 580 → `cu130`; >= 576.02 → `cu129`; >= 560.76 → `cu126`; < 560.76 → flavor CPU + notice "driver chưa đủ mới". Wheel paddlepaddle-gpu cao nhất mà driver hỗ trợ đầy đủ; CUDA 12.7/12.8 không có wheel nên rơi về cu126. **Quy tắc map WMI → product version: số trăm lấy từ segment thứ ba (branch), không lấy DDI major** — `NN.0.14.xxxx`→4xx, `NN.0.15.xxxx`→5xx, `NN.0.16.xxxx`→6xx; branch khác ⇒ không parse được ⇒ CPU. Chi tiết + chứng cứ: mục 10. Ngưỡng chi tiết + nguồn: mục 10.
   - AMD: chỉ hiện thông báo "card AMD" khi có adapter rời (không phải iGPU); iGPU/none → thông báo chung "không có GPU NVIDIA hỗ trợ — dùng bản CPU".
   - Đĩa trống tối thiểu: **8 GB (flavor GPU) / 5 GB (flavor CPU)** — số dự phòng, hiệu chỉnh bằng đo thật dung lượng `uv sync` cả hai flavor khi implement.
   - Mạng: probe HTTPS tới PyPI, index `paddlepaddle.org.cn`, host model mà paddlex tải (xác nhận host thật khi implement), **và GitHub** — máy sạch không có Python thì `uv sync` phải tải CPython managed 3.13 từ python-build-standalone (host ở GitHub); thiếu probe này wizard sẽ fail giữa bước 4 với lỗi thô của uv thay vì thông báo pre-check rõ ràng. Đặt `UV_PYTHON_PREFERENCE=only-managed` cho determinism (trùng hành vi dev hiện tại: venv luôn dùng CPython do uv quản lý). Probe fail = bước check fail với thông điệp chỉ rõ host nào không tới được.
8. **Trạng thái setup**: `%LOCALAPPDATA%\ReTran\setup.json` — `{flavor, language, steps: {bước: done|failed}, version}`. Wizard đọc để skip bước đã xong và nhớ câu trả lời ngôn ngữ. File hỏng/missing = chạy lại từ check system (không mất dữ liệu thật vì mọi thứ đều kiểm tra được trên đĩa).
9. **Điều kiện hiện modal lúc app start**: `setup.json` chưa hoàn tất **hoặc** kiểm tra nhanh phát hiện `.venv` hoặc model đã chọn biến mất trong `<appDir>\ReTran.OCR\` (user xoá, giải nén đè). Kiểm tra này phải rẻ (chỉ test tồn tại file, không chạy python).
10. **Nút "Chạy lại setup" trong Settings** — mở lại wizard; dùng chung toàn bộ state machine. Đây cũng là đường sửa khi một release đổi dependencies mà venv chưa re-sync (non-goal mục 2).
11. **Kiến trúc UI/service**: state machine sống trong C# (`ReTran.App/Services/Setup/`), chạy các tiến trình con và đẩy progress lên WebView qua pattern JsInterop có sẵn; modal là component trong frontend Vite/Tailwind trên control page. UI không bao giờ tự gọi uv/python — mọi thứ qua service (để test được bằng fake process).
12. **Ký `uv.exe`**: thêm vào danh sách AzureSignTool của `release.yml` khi có secrets (tránh SmartScreen với binary lạ chạy lần đầu). Không chặn release nếu chưa có signing.
13. **Màn hình chọn ngôn ngữ**: danh sách ngắn các ngôn ngữ phổ biến (Tiếng Việt, English, 中文, 日本語, 한국어, Русский, العربية, ไทย, Ελληνικά, हिन्दी + nhóm "khác" theo catalog) + tuỳ chọn mặc định "Tự động" (= cặp v6 small, phủ 50 ngôn ngữ). Mỗi mục ghi chú model sẽ dùng và dung lượng; tổng dung lượng hiện trước nút xác nhận. Dữ liệu tên/dung lượng cho màn hình này là **bản sao JSON chỉ-để-hiển-thị** nằm trong frontend (`catalog.py` vẫn là source of truth); một unit test so khớp bản sao với catalog Python thật để chặn drift — khi lệch, cặp model thật vẫn lấy từ `ocr.models.recommend` (bước 6) nên hậu quả tối đa là sai số hiển thị.

## 6. Dữ liệu & file liên quan (tóm tắt)

| Đường dẫn | Vai trò |
|-----------|---------|
| `<appDir>\ReTran.OCR\` (trong zip) | Source sidecar — cũng là nơi sống của `.venv` và `models` sau setup |
| `<appDir>\ReTran.OCR\.venv\` | venv do wizard tạo — **sống qua update** (updater không xoá, mục 5.2) |
| `<appDir>\ReTran.OCR\models\` | Model cache — `PADDLE_PDX_CACHE_HOME` trỏ đây (thay `%LOCALAPPDATA%\ReTran\models`, mục 5.5) |
| `runtimes\win-x64\uv.exe` (repo → build output) | uv standalone pin version + `uv.version.txt` (phiên bản/SHA256) — vendor theo pattern `runtimes/`, vào zip qua csproj Content |
| `%LOCALAPPDATA%\ReTran\setup.json` | Trạng thái wizard (mới) |
| `%LOCALAPPDATA%\ReTran\settings.json` | Wizard ghi `OcrDet/OcrRec` ở bước 7 |

Catalog model tham khảo cho bước hỏi ngôn ngữ (`ReTran.OCR/src/retran_core/ocr/catalog.py`): v6 tiny/small/medium det+rec phủ 49–50 ngôn ngữ (gồm vi, en, zh, ja…); rec bản 5 riêng theo bộ chữ: korean, cyrillic, arabic, th, ta, te, devanagari, el, eslav, chinese_cht v3. Det luôn là tầng v6 (catalog không có det legacy).

## 7. Chiến lược test

- **Unit (C#)**: logic check system (mock WMI/disk/network), state machine wizard (fake process runner: success/fail×3/timeout/resume), điều kiện hiện modal, ghi/đọc `setup.json`.
- **Unit (Updater)**: khoá hành vi của user — file/thư mục lạ trong app dir (đại diện `.venv/`, `models/`) **sống sót nguyên vẹn** sau `CopyDirectory`; đây là test bảo vệ cho trường hợp ai đó sau này thêm logic clean/xoá.
- **Unit (Core)**: `PADDLE_PDX_CACHE_HOME` khi spawn sidecar trỏ tới `<repoRoot>\ReTran.OCR\models` (`SidecarProcess.cs`).
- **Unit (sidecar)**: RPC `ocr.models.recommend` trả đúng cặp theo routing `recommended()` — cả ngôn ngữ trong phủ v6 lẫn ngoài phủ (ko → korean, ru → cyrillic, th → th); migration một-lần từ `%LOCALAPPDATA%\ReTran\models` sang vị trí mới (idempotent, marker).
- **Integration**: chạy Core thật với app dir giả lập trong temp dir sạch (mở rộng thí nghiệm fresh-sim đã làm 2026-11-03): trước setup → `-32603`; sau khi có `ReTran.OCR\.venv` giả → ping/list OK; e2e chọn 한국어 → `settings.json` nhận `korean_PP-OCRv5_mobile_rec` và phiên sau dùng đúng model đó.
- **Thủ công (user, máy thật)**: checklist smoke — xem mục 9.

## 8. Việc cho CI (`release.yml`)

- Không cần bước mới cho `uv.exe` hay Python source — cả hai tự vào output qua csproj Content (mục 5.3); `release.yml` chỉ thêm phần verify bên dưới.
- Bước verify executables thêm kiểm tra `runtimes\win-x64\uv.exe` + `ReTran.OCR\pyproject.toml` có mặt trong zip.

## 9. Checklist smoke thủ công (chạy trên máy thật trước khi coi xong)

1. Máy sạch (hoặc VM): giải nén zip → chạy `ReTran.App.exe` → modal hiện, bước check system chạy không hỏi gì, đúng thông báo GPU/CPU theo card của máy.
2. Chọn ngôn ngữ ngoài phủ v6 (ví dụ 한국어) → wizard liệt kê đúng model rec bản 5 + dung lượng trước khi tải.
3. `uv sync` xong → sidecar sống: gọi lại pipeline thật, OCR ra chữ trên một cửa sổ/game có tiếng Hàn.
4. Giết mạng giữa `uv sync` → retry 3 lần → màn hình lỗi đúng 2 nút; Bỏ qua → app đóng hẳn (không tiến trình mồ côi); mở lại → modal hiện lại và **resume từ bước sync**, không hỏi lại ngôn ngữ.
5. Máy có NVIDIA: wizard chọn flavor GPU đúng nấc wheel theo driver thật của máy (đọc log `SystemCheck`), và `uv sync` thật sự cài wheel đó từ index mà wizard đã ghi lại trong pyproject (không phải index `cu129` hard-code trong repo).
6. Cập nhật app qua update flow sau khi đã setup → `.venv`/`models` còn nguyên (updater không xoá), code sidecar được ghi đè bằng bản mới và hiệu lực, pipeline chạy tiếp không cần setup lại.
7. Dev machine (repo layout): mở app → wizard nhận diện trạng thái dev, không phá venv repo, không copy đè source repo.
8. Nút "Chạy lại setup" trong Settings mở lại wizard và hoàn tất gần tức thì khi mọi thứ đã sẵn sàng.
9. Máy dev sau thay đổi vị trí model: chạy lần đầu → model cũ ở `%LOCALAPPDATA%\ReTran\models` được migration một lần về `ReTran.OCR\models`; chạy lần hai không migrate lại (marker).

## 10. Open items cho builder (tự quyết định được, ghi lại khi làm)

- Mức driver NVIDIA cho flavor GPU + chọn wheel paddlepaddle-gpu 3.4.x (wheel cao nhất ≤ mức driver hỗ trợ đầy đủ, Table 3 + Table 2 CUDA Toolkit Release Notes, tra cứu 2026-11-05): driver >= 580 → cu130; >= 576.02 (12.9 GA) → cu129; >= 560.76 (12.6 GA) → cu126; < 560.76 → flavor CPU. CUDA 13.0 GA cột Windows = N/A (display driver không còn bundle từ 13.0), sàn thực dụng Windows là nhánh 580 theo Table 2 (13.x Min >= 580).
- **Quy tắc map WMI `DriverVersion` → product version NVIDIA (đã sửa 2026-11-06, Task 10):** chuỗi WMI có dạng `DDI.0.BRANCH.TTTT`; **số trăm của product version nằm ở `BRANCH` (segment thứ ba)**, không ở DDI major: `14`→4xx, `15`→5xx, `16`→6xx; `TTTT` = `major%100 * 100 + minor`. Branch khác ⇒ từ chối parse ⇒ flavor CPU + notice (fail-safe). DDI major chỉ là phiên bản D3D runtime, đi kèm cả hai họ (5xx có cả `31.0.15.xxxx` lẫn `32.0.15.xxxx`). Chứng cứ (9 cặp, 4 nguồn):

  | WMI `DriverVersion` | product thật | nguồn |
  |---|---|---|
  | `32.0.16.2114` | 621.14 | đo máy dev 2026-11-06 (`Get-CimInstance Win32_VideoController` ↔ `nvidia-smi`) |
  | `32.0.16.1742` | 617.42 | đo máy dev 2026-11-05 |
  | `32.0.15.6094` | 560.94 | DriversCloud (package 560.94, mọi INF), Microsoft Update Catalog, Dell KB 000222850 |
  | `32.0.15.7666` | 576.66 | Microsoft Update Catalog |
  | `32.0.15.8163` | 581.63 | Microsoft Update Catalog (10/2025) — code cũ suy ra 681.63, một phiên bản không tồn tại |
  | `31.0.15.3699` / `31.0.15.5161` | 536.99 / 551.61 | Microsoft Update Catalog / Softpedia (branch 15 dưới DDI major 31) |
  | `30.0.14.7141` | 471.41 | Softpedia (branch 14 dưới DDI major 30) |
  | `27.21.14.5609` | 456.09 | branch 14 với DDI major 27 ⇒ major không phải nguồn số trăm |

  Hệ quả của bug cũ (`DDI major ⇒ 30⇒400/31⇒500/32⇒600`): mọi driver 5xx mang DDI 32 bị suy thành 6xx ⇒ vượt sàn 580 ⇒ wizard cài **cu130** cho máy chỉ chạy nổi cu126 — sai theo hướng **không** fail-safe. Bug này đóng được vì fixture test cũ tự sinh chuỗi `31.0.15.…`; test hiện tại ghim bằng chuỗi INF thật.
- Ghi chú chưa chốt (không chặn): `TryGetNvidiaProductVersion` còn chấp nhận tail 3 chữ số và lấy `segments[^1]` khi chuỗi >4 segment, nên về lý thuyết `32.0.16.906`→(609,6) hay `32.0.16.1742.6142`→(661,42) selectable; không chuỗi WMI thật nào tạo ra được hai dạng đó (WMI `DriverVersion` luôn 4 segment, mọi product 5xx/6xx mã hoá 4 chữ số). Siết `segments.Length != 4` nếu chạm lại file đó.
- **Đo thật `uv sync` cả hai flavor (GB + thời gian) và xác nhận host probe: user chốt defer sang smoke trên máy sạch** (unit test dùng fake process runner). Số đo phải ghi lại vào mục này ở bước smoke (Task 24), kèm hiệu chỉnh ngưỡng đĩa 8/5 GB.
- Host thật mà paddlex tải model (để probe mạng đúng host).
- URL download path thật mà uv gọi để lấy CPython managed (xác nhận host probe GitHub chính xác).
- Chọn phiên bản uv ban đầu để vendor + verify SHA256 đối chiếu release chính thức của uv (ghi vào `runtimes\win-x64\uv.version.txt`).
- Số GB đĩa + thời gian thực đo của `uv sync` cả hai flavor (kể cả kích thước wheel `paddlepaddle` CPU) → hiệu chỉnh ngưỡng check 8/5 GB.
- Danh sách cuối cùng ngôn ngữ trên màn hình hỏi (dựa vào catalog, không bịa ngôn ngữ ngoài catalog).
