# Thiết kế: phơi toàn bộ tham số PaddleOCR + bỏ panel mode + bỏ cỡ chữ thủ công

Ngày: 2026-10-26
Trạng thái: đã thực hiện (2026-10-26 → 2026-11-01; kiểm chứng đầu-cuối xong 2026-11-01)

## 1. Vì sao (intent)

Ba việc độc lập, user chốt trong cùng một đợt:

1. **Máy OCR chỉ chỉnh được cặp model.** `PaddleOcrEngine` pin 3 cờ pipeline
   (`use_doc_orientation_classify=False`, `use_doc_unwarping=False`,
   `use_textline_orientation=False`) và không có đường nào cho user chỉnh
   ngưỡng detect/rec, giới hạn cạnh, batch, device, precision… Muốn thử tiny
   model cho máy yếu, hay hạ `box_thresh` cho chữ mờ, đều phải sửa code.
2. **Panel mode (bảng dịch ở góc) là mode thừa.** Overlay subtitle đã đè đúng
   vị trí chữ gốc; bảng góc chỉ lặp lại nội dung, thêm một nhánh layout + cờ
   `IsPanelLine` + test riêng, và làm UI overlay card dài thêm (chọn mode, chọn
   góc).
3. **Cỡ chữ thủ công không còn ý nghĩa.** Cả overlay cục bộ, preview và trang
   OBS đều đã scale cỡ chữ theo chiều cao box. Hệ số nhân 0.85/1.0/1.25 chỉ
   làm lệch khỏi công thức chung, thêm state (`OverlayFontScale`) và một RPC
   (`core.set_font_scale`) để đồng bộ.

## 2. Mục tiêu

- Phơi **toàn bộ tham số engine PaddleOCR 3.7** mà ReTran dùng được, có UI
  chỉnh, có nhãn + ghi chú + tooltip giải thích (mặc định, khoảng hợp lệ, ảnh
  hưởng tốc độ/độ chính xác).
- Tham số áp dụng đúng thời điểm: nhóm `predict()` đổi **ngay** không nạp lại
  model; nhóm constructor **nạp lại model** (~27 s) và bị hoãn tới phiên sau
  khi pipeline đang chạy.
- Bỏ hẳn overlay **panel mode**: chỉ còn subtitle đè lên box gốc.
- Bỏ hẳn control **Cỡ chữ** và mọi state/RPC liên quan; cỡ chữ = công thức
  tự động duy nhất (0.5 × cao box, kẹp 12–40 px, shrink ≥ 9 px).
- Sửa luôn lỗi `-32603 "The node already has a parent."` đang làm toàn bộ
  `ocr.models.*` (và sẽ làm cả `ocr.config.*`) không dùng được.

## 3. Không mục tiêu

- Không đổi giao thức ảnh OCR (shared memory `Local\ReTran.Ocr.V1`), preview
  MMF, overlay HTTP/SSE, hay đường translate.
- Không thêm profile/nhiều bộ cấu hình OCR; một bộ global trong `settings.json`.
- Không tự tải model orientation/unwarping mới ngoài cơ chế PaddleX sẵn có;
  model manager det/rec giữ nguyên.
- Không đổi ngưỡng mặc định hiện tại (xem §6): hành vi mặc định phải trùng
  khớp bản đang chạy.
- Không làm lại UI Settings ngoài mục OCR.

## 4. Quyết định đã chốt (user)

| # | Quyết định |
| - | ---------- |
| D1 | Bỏ hoàn toàn mode panel (bảng ở góc); chỉ giữ subtitle. |
| D2 | Bỏ control cỡ chữ thủ công; cỡ chữ overlay scale động theo box. |
| D3 | Phơi **toàn bộ** tham số engine PaddleOCR, có UI chỉnh + tiêu đề + ghi chú/tooltip. |
| D4 | Ẩn 4 tham số vô nghĩa/phá kiến trúc: `lang`, `ocr_version`, `text_detection_model_dir`, `text_recognition_model_dir`. |

## 5. Quyết định của người thiết kế (có thể override)

| # | Quyết định | Lý do |
| - | ---------- | ----- |
| C1 | Danh sách tham số do **sidecar** làm nguồn sự thật duy nhất, trả qua RPC `ocr.config.schema`; UI render động từ schema, không hard-code 27 dòng. | Tránh 3 nơi (App/Core/Python) lệch nhau; test đối chiếu `PaddleOCR.__init__` + `parse_common_args` bảo đảm "phơi đủ". |
| C2 | Ẩn thêm 3 `*_model_dir` còn lại (doc orientation, doc unwarping, textline orientation) vì cùng lý do D4: đường dẫn thủ công lách catalog/download manager. | Nhất quán với tiêu chí "phá kiến trúc". Tên model của 3 module này vẫn phơi (không phải đường dẫn). |
| C3 | Nhóm `predict()` (7 tham số) áp dụng ngay; nhóm constructor (20 tham số) hoãn khi pipeline chạy, qua cờ `deferReload` do Core set = `IsRunning`. | Đổi ngưỡng cần thấy ngay trên preview; nạp lại model giữa phiên tốn ~27 s và vi phạm tinh thần C8 của spec 2026-10-22. |
| C4 | Gộp `ocrFps` + `captureFps` (tham số pipeline, không phải PaddleOCR) vào cùng mục UI; chỉ áp dụng ở `core.start` kế tiếp. | Đây là 2 tham số OCR user cần chỉnh nhất mà hiện phải sửa `%LOCALAPPDATA%\ReTran\core\config.json`. |
| C5 | Giá trị `null` trong `settings.json` = "dùng mặc định engine"; UI hiển thị mặc định bằng chữ mờ. Nút "Khôi phục mặc định" xoá `ocrEngine` về `{}`; 3 cờ orientation vẫn là `false` vì đó là default schema của ReTran. | PaddleX đổi default giữa các bản thì app không âm thầm ghim giá trị cũ. |
| C6 | Nút Lưu gọi `ocr.config.set` **một lần** với toàn bộ params; sidecar trả `{live, deferred}` để UI nói rõ cái nào áp ngay, cái nào chờ phiên sau. | Một round-trip, không cần App tự phân nhóm. |
| C7 | Xoá hẳn `core.set_font_scale`, `OverlayFontScale`, `OverlayMode`, `OverlayCorner`; key cũ trong `settings.json` bị bỏ qua (System.Text.Json mặc định). | Không để code chết; file cũ vẫn đọc được. |
| C8 | Sửa lỗi node-parent bằng `DeepClone()` tại biên wire (3 chỗ), không sửa từng handler. | Lỗi thuộc lớp transport, mọi handler trả node con đều dính. |
| C9 | Không thêm `ocr.config.get`: UI đọc giá trị từ `settings.json`, schema từ sidecar. | Một nguồn cho giá trị đang dùng; sidecar nhận lại đủ bộ mỗi `core.start`. |
| C10 | `return_word_box` vẫn phơi dù ReTran chưa dùng word box; tooltip ghi rõ "chưa dùng, chỉ tốn thời gian". | Yêu cầu D3 là phơi đủ; ẩn sẽ thành quyết định kỹ thuật không ai thấy. |
| C11 | Ẩn thêm `paddlex_config` (đường dẫn YAML thay toàn bộ pipeline config) theo đúng tiêu chí D4: nó lách catalog model, default orientation và model manager. `text_detection_model_name` / `text_recognition_model_name` cũng nằm trong danh sách ẩn của schema vì đã có control model manager riêng (không phơi dạng text tự do). | Giữ nguyên tắc "một nguồn cho model"; tránh 2 đường chỉnh cùng một thứ. |
| C12 | `ocr.config.set` là **replace toàn bộ map override**: key vắng hoặc `null` = trả về default schema; `{}` = toàn bộ mặc định. App chỉ gửi các key có giá trị non-null trong `settings.json`, nên "Khôi phục mặc định" = gửi `{}`. | Nếu merge, xoá một key/"Khôi phục mặc định" sẽ không có tác dụng với sidecar đang sống — lỗi rất khó thấy. |
| C13 | `core.start` lọc `ocr.params` theo schema (bỏ key lạ/ẩn, ghi warn) trước khi forward; `ocr.config.set` trực tiếp vẫn strict -32602. | File `settings.json` bị sửa tay với key lạ không được làm hỏng cả lần Start; UI vẫn bắt lỗi sớm. |
| C14 | Nút Lưu chạy **validate → `ocr.config.set` → ghi `settings.json`** (không ghi file trước). | Giá trị bị sidecar từ chối không bao giờ nằm lại trong file để phá lần Start sau. |

## 6. Bề mặt tham số

Ký hiệu áp dụng: **L** = live (truyền vào `predict()`, không nạp lại model);
**R** = reload (constructor, nạp lại model); **S** = chỉ áp dụng ở `core.start`
kế tiếp.

### 6.1 Nhóm "Thiết bị & engine" (R)

| Key | Kiểu | Mặc định | Nhãn VI | Ghi chú / tooltip |
| --- | ---- | -------- | ------- | ----------------- |
| `device` | string | `null` = auto (GPU 0 nếu có, else CPU) | Thiết bị | `auto \| cpu \| gpu:0 \| gpu:0,1`. Auto = Paddle tự chọn. |
| `engine` | enum | `null` = `paddle` | Inference engine | `paddle`, `paddle_static`, `paddle_dynamic`, `transformers`, `onnxruntime`. Đổi engine có thể cần cài thêm gói. |
| `precision` | enum | `fp32` | Precision | `fp32`/`fp16`; chỉ có tác dụng khi bật TensorRT. |
| `use_tensorrt` | bool | `false` | TensorRT | Chỉ dùng được nếu model + máy hỗ trợ; không hỗ trợ thì Paddle tự bỏ qua. |
| `enable_hpi` | bool | `null` (tắt) | High-performance inference | Bật plugin HPI của PaddleX (nếu đã cài). |
| `enable_cinn` | bool | `false` | CINN compiler | Chỉ đường CPU/một số model; bật sai cấu hình dễ lỗi nạp. |
| `enable_mkldnn` | bool | `false` | oneDNN (MKL-DNN) | Tăng tốc CPU; không ảnh hưởng khi chạy GPU. Tắt mặc định vì oneDNN làm Paddle 3.4 crash trên CPU (`ConvertPirAttribute2RuntimeAttribute`, Paddle#77340); bật lại nếu tự kiểm tra được. |
| `mkldnn_cache_capacity` | int 0–512 | `10` | oneDNN cache | Số entry cache của oneDNN; chỉ dùng khi bật oneDNN. |
| `cpu_threads` | int 1–64 | `10` | CPU threads | Số luồng suy luận CPU. |
| `engine_config` | JSON object | `null` | Engine config | Ghi đè cấu hình engine (JSON). Để trống = mặc định. |

`paddlex_config` bị ẩn (C11) vì thay toàn bộ pipeline config, lách catalog và
default của ReTran.

### 6.2 Nhóm "Nạp model" (R)

| Key | Kiểu | Mặc định | Nhãn VI | Ghi chú / tooltip |
| --- | ---- | -------- | ------- | ----------------- |
| `text_recognition_batch_size` | int 1–64 | `6` | Batch nhận dạng | Số dòng chữ mỗi lượt rec; tăng = nhanh hơn nhưng tốn VRAM. |
| `textline_orientation_batch_size` | int 1–64 | `6` | Batch orientation | Chỉ dùng khi bật `use_textline_orientation`. |
| `text_det_input_shape` | shape `N,C,H,W` | `null` | Input shape detect | Ép kích thước đầu vào det; trống = theo ảnh. |
| `text_rec_input_shape` | shape `N,C,H,W` | `null` | Input shape rec | Ép kích thước đầu vào rec; trống = mặc định model. |
| `doc_orientation_classify_model_name` | string | `PP-LCNet_x1_0_doc_ori` | Model orientation | Chỉ dùng khi bật `use_doc_orientation_classify`; model do PaddleX tải, **không** nằm trong model manager của ReTran. |
| `doc_unwarping_model_name` | string | `UVDoc` | Model unwarping | Chỉ dùng khi bật `use_doc_unwarping`; tải ngoài catalog ReTran. |
| `textline_orientation_model_name` | string | `PP-LCNet_x1_0_textline_ori` | Model textline orientation | Chỉ dùng khi bật `use_textline_orientation`; tải ngoài catalog ReTran. |

### 6.3 Nhóm "Tiền xử lý" (R)

| Key | Kiểu | Mặc định | Nhãn VI | Ghi chú / tooltip |
| --- | ---- | -------- | ------- | ----------------- |
| `use_doc_orientation_classify` | bool | `false` | Xoay trang tự động | Model ngoài catalog; lần đầu bật sẽ tải, preview có thể đứng vài phút. Game/UI text không cần. |
| `use_doc_unwarping` | bool | `false` | Làm phẳng trang | Model UVDoc ngoài catalog; lần đầu bật sẽ tải, preview có thể đứng vài phút. Chỉ hữu ích với ảnh chụp giấy cong. |
| `use_textline_orientation` | bool | `false` | Xoay dòng chữ | Model ngoài catalog; lần đầu bật sẽ tải, preview có thể đứng vài phút. Cần cho chữ dọc/xoay. |

### 6.4 Nhóm "Phát hiện" (L)

| Key | Kiểu | Mặc định | Nhãn VI | Ghi chú / tooltip |
| --- | ---- | -------- | ------- | ----------------- |
| `text_det_limit_side_len` | int 32–4096 | `64` | Giới hạn cạnh | Ngưỡng resize cạnh ảnh trước khi detect (theo `limit_type`). |
| `text_det_limit_type` | enum `min`/`max` | `min` | Kiểu giới hạn | `min`: bảo đảm cạnh ngắn ≥ ngưỡng; `max`: cạnh dài ≤ ngưỡng. |
| `text_det_thresh` | float 0–1 | `0.3` | Ngưỡng pixel | Xác suất tối thiểu để một pixel là chữ; giảm = bắt nhiều chữ mờ hơn, dễ nhiễu. |
| `text_det_box_thresh` | float 0–1 | `0.6` | Ngưỡng box | Điểm trung bình tối thiểu để giữ một box chữ; giảm = giữ box mờ. |
| `text_det_unclip_ratio` | float 0.1–10 | `1.5` | Hệ số nới box | Nới box quanh vùng chữ; tăng = box rộng hơn (dễ dính chữ bên cạnh). |

### 6.5 Nhóm "Nhận dạng" (L)

| Key | Kiểu | Mặc định | Nhãn VI | Ghi chú / tooltip |
| --- | ---- | -------- | ------- | ----------------- |
| `text_rec_score_thresh` | float 0–1 | `0.0` | Ngưỡng điểm rec | Bỏ dòng có confidence thấp hơn ngưỡng; 0 = giữ tất cả. |
| `return_word_box` | bool | `false` | Trả word box | ReTran chưa dùng word box; bật chỉ tốn thời gian. |

### 6.6 Nhóm "Nhịp OCR" (S)

| Key | Kiểu | Mặc định | Nhãn VI | Ghi chú / tooltip |
| --- | ---- | -------- | ------- | ----------------- |
| `ocrFps` | int 1–60 | `3` | Tốc độ OCR | Số lượt OCR/giây; bỏ tick nếu không có khung mới. |
| `captureFps` | int 1–60 | `10` | Tốc độ capture | Số khung chụp/giây của nguồn capture. **Không** đổi nhịp annotate preview — nhịp đó cố định theo config Core lúc khởi động (`_annotatorFps`, mặc định 10 fps) để `core.start` không thể đẩy vòng annotate vượt budget. |

### 6.7 Ẩn khỏi UI (D4 + C2 + C11)

Tổng: schema phơi **27** tham số, ẩn **10**; đúng bằng 37 tham số của
`PaddleOCR.__init__` (26 tham số có tên + 11 kwarg chung), không sót không thừa.

- Vô nghĩa: `lang`, `ocr_version` — ReTran luôn set tên model det/rec nên
  PaddleOCR bỏ qua hai tham số này.
- Lách catalog/model manager: `text_detection_model_dir`,
  `text_recognition_model_dir`, `doc_orientation_classify_model_dir`,
  `doc_unwarping_model_dir`, `textline_orientation_model_dir`,
  `paddlex_config` (YAML thay toàn bộ pipeline config).
- Đã có control riêng: `text_detection_model_name`,
  `text_recognition_model_name` — do select + catalog ở mục model manager
  quản lý, không phơi dạng text tự do.

Sidecar từ chối key lạ/ẩn bằng -32602 (`unsupported param: <key>`), để không
ai lách qua RPC. `core.start` là ngoại lệ duy nhất: Core lọc bỏ key lạ trước
khi gửi (C13), vì file `settings.json` có thể bị sửa tay.

## 7. Hợp đồng RPC

### 7.1 `ocr.config.schema` (mới, sidecar)

Không tham số. Trả:

```json
{"params": [
  {"key": "device", "group": "device", "type": "string",
   "default": null, "reloadRequired": true,
   "values": null, "min": null, "max": null, "step": null,
   "placeholder": "auto | cpu | gpu:0"}
]}
```

- `group`: `device` | `models` | `preprocess` | `det` | `rec` (thứ tự UI).
- `type`: `bool` | `int` | `float` | `string` | `enum` | `json` | `shape`.
- `default`: giá trị sidecar dùng khi key vắng mặt **hoặc `null`** trong
  `ocr.config.set`; đây là default của ReTran, không phải default PaddleX khi
  ReTran đã override (ví dụ 3 cờ orientation = `false`).
- `reloadRequired`: `true` cho nhóm constructor.
- Test đối chiếu: hợp của `inspect.signature(PaddleOCR.__init__)` (trừ `self`,
  `**kwargs`) và 11 kwarg chung của `parse_common_args` phải nằm trọn trong
  schema ∪ danh sách ẩn §6.7 (27 + 10 = 37).

### 7.2 `ocr.config.set` (mới, sidecar)

```json
{"params": {"text_det_thresh": 0.35, "device": "gpu:0"}, "deferReload": true}
```

Trả:

```json
{"live": ["text_det_thresh"], "deferred": ["device"], "reloadRequired": true}
```

Ngữ nghĩa:

- `params` là **toàn bộ map override mong muốn**, không phải patch. Sidecar
  thay map cũ bằng map mới; key vắng hoặc `null` = trả về default schema
  (§7.1); `{}` = xoá hết override.
- Validate **toàn bộ** trước khi áp: một key sai làm cả request bị từ chối và
  map cũ giữ nguyên (atomic), không có trạng thái nửa cũ nửa mới.
- Key không thuộc schema (kể cả key ẩn) → -32602 `unsupported param: <key>`.
- Sai kiểu/khoảng → -32602 `invalid <key>: <lý do>`.
- Key live: dùng ở `spot()` kế tiếp (không nạp lại model).
- Key reload + `deferReload=false`: reset predictor (`_ocr = None`), lần
  `spot()` sau dựng lại (nằm trong timeout 30 s hiện có; warmup của
  `core.start` che chi phí).
- Key reload + `deferReload=true`: **không** reset predictor; phiên hiện tại
  giữ nguyên model/engine, phiên sau nhận đủ bộ từ `core.start`.
- Sidecar so map cũ/mới và chỉ báo các key thực sự đổi:
  `live` = đã dùng ngay; `deferred` = reload nhưng bị hoãn; `reloadRequired` =
  có ít nhất một key reload đổi. UI dựa vào đây để nói rõ "áp dụng ngay" hay
  "áp dụng sau khi Dừng".

### 7.3 `core.start` (mở rộng)

```json
{"ocr": {"det": "...", "rec": "...", "params": { ...các key user đã đặt... }},
 "ocrFps": 3, "captureFps": 10, ...}
```

`PipelineHost.StartAsync` gọi `ocr.config.schema` (cache trong phiên) để lọc
`ocr.params` — bỏ key lạ/ẩn kèm `warn:` (C13) — rồi gọi
`ocr.config.set {params, deferReload:false}` trước `ocr.models.set`, cuối cùng
mới chạy loop + warmup. `ocr.params` vắng mặt = giữ cấu hình sidecar hiện tại
(tương thích test/CLI cũ); nếu có mặt thì theo ngữ nghĩa replace của §7.2
(key vắng = default, không phải "giữ nguyên"). `ocrFps`/`captureFps` đã được
`StartAsync` hỗ trợ sẵn; App bắt đầu gửi. Sidecar không sẵn sàng thì
`StartAsync` đã ném -32603 như hiện tại (không đổi hành vi); lỗi schema/set
giữa chừng trả nguyên văn cho App hiển thị trong toast.

### 7.4 Proxy Core

Đăng ký thêm trong `Program.cs` (serve):
`ocr.config.schema` → `PipelineHost.OcrConfigSchemaAsync` (forward, cache theo
phiên sidecar);
`ocr.config.set` → `PipelineHost.SetOcrConfigAsync`, tự set
`deferReload = IsRunning` (bỏ qua giá trị caller gửi) rồi forward — **không**
lọc key, để lỗi từ UI hiện nguyên văn.
`StartAsync` dùng schema đã cache để lọc `ocr.params` trước khi gọi set (C13).

### 7.5 Sửa lỗi node-parent (bắt buộc trước khi làm UI)

- `ReTran.Core/OcrClient/SidecarProcess.cs:82` → `Deliver(id, r.DeepClone())`.
- `ReTran.Core/JsonRpc/JsonRpcServer.cs:183` → `waiter.TrySetResult(response["result"]?.DeepClone())`.
- `ReTran.App/Core/JsonRpcClient.cs:144` → `tcs.TrySetResult(node["result"]?.DeepClone())`.
- Regression test: handler trả node có parent (node con của envelope) → response
  vẫn serialize được; test riêng cho `SidecarProcess` read-loop.

## 8. UI

Vị trí: **Settings → mục "OCR"** (đang có). Giữ nguyên phần model manager
(det/rec select + bảng catalog + Tải/Xoá/Làm mới) ở trên; thêm dưới nó khối
**"Tham số máy OCR"**.

- Render động từ `ocr.config.schema`; nhóm theo `group`, mỗi nhóm là
  `<details>` mở sẵn (trừ "Nạp model" và "Thiết bị & engine" thu gọn mặc định).
- Mỗi dòng: nhãn (i18n `ocr.param.<key>.label`), control theo `type`,
  ghi chú 1 dòng (`ocr.param.<key>.hint`), tooltip `title` đầy đủ
  (`ocr.param.<key>.tip`: mặc định, khoảng, ảnh hưởng).
- `int`/`float`: input number với `min`/`max`/`step`, lỗi inline khi ngoài
  khoảng. `enum`: select. `bool`: switch. `json`: textarea 3 dòng, báo
  "JSON không hợp lệ". `shape`: text `N,C,H,W`, trống = mặc định.
- Nút: **Lưu tham số** (validate → `ocr.config.set` → chỉ ghi
  `settings.json` khi set thành công; lỗi thì giữ nguyên file — C14),
  **Khôi phục mặc định** (xoá `ocrEngine` → gửi `{}`). Dòng trạng thái đọc
  `{live, deferred}`: "Áp dụng ngay" / "Đã lưu — áp dụng sau khi Dừng".
- Tham số nhịp (`ocrFps`, `captureFps`): 2 dòng hard-code (không thuộc schema
  sidecar), lưu vào `settings.json`, gửi ở `core.start` kế tiếp.
- Danh sách key nằm trong `settings.json` dưới `ocrEngine` (object, chỉ chứa
  key user đã đặt; key vắng mặt hoặc `null` = mặc định), cộng `ocrFps`,
  `captureFps`.
- Cảnh báo riêng cho 3 cờ orientation/unwarping: lần đầu bật, PaddleX tải
  model ngoài catalog (có thể vài phút) nên preview đứng cho tới khi tải xong
  — hint ghi rõ "nên bật khi đang Dừng".
- i18n: thêm khối `ocr.param.*` cho `vi` + `en`; nhãn nhóm
  `ocr.group.device/models/preprocess/det/rec/rhythm`.

## 9. Bỏ panel mode + bỏ cỡ chữ thủ công

### 9.1 Panel mode

Xoá: `OverlayMode`, `OverlayCorner`, `OverlayViewOptions`, `RenderPanel`,
`PanelMaxLines/PanelMargin/PanelLineGap/PanelWidthRatio/PanelLineHeight`,
`DrawBox.IsPanelLine`, nhánh panel trong `Win32OverlayHost.DrawBoxes`,
`TryParseMode/TryParseCorner/ModeToString/CornerToString` +
`ApplyOptions(mode|corner)` trong `OverlayWindowService`,
`AppSettings.OverlayMode/OverlayCorner`, dropdown Chế độ + Góc trong
`Home.razor`, i18n `overlay.mode.*`, `overlay.corner.*`, `deck.overlay.mode`,
`deck.overlay.corner`, test `OverlayLayoutTests` panel + render tests panel.

`OverlayLayout.Render(frame, vw, vh)` chỉ còn nhánh subtitle.

### 9.2 Cỡ chữ thủ công

Xoá: `AppSettings.OverlayFontScale`, `fontScale` khỏi `core.start` params,
RPC `core.set_font_scale` + `PipelineHost.SetFontScaleAsync` +
`_annotationFontScale`, `PipelineController.SetAnnotationFontScaleAsync`,
segmented Nhỏ/Trung bình/Lớn + `SetOverlayFont` trong `Home.razor`,
`ApplyOptions(fontScale)` + `OverlayViewOptions.FontScale`, tham số
`fontScale` của `IOverlayHost.Draw`, `Win32OverlayHost.Draw/DrawBoxes`,
`BoxAnnotationStyle.BaseFontSize/Fit/ResolveFontSize` (bỏ hệ số, giữ nguyên
công thức 0.5 × cao box, kẹp 12–40, shrink ≥ 9), `FrameAnnotator` (bỏ tham
số fontScale), i18n `overlay.font.*`, `deck.overlay.font`, test liên quan.

Preview, overlay cục bộ và OBS dùng chung một công thức; OBS không đổi gì.

### 9.3 Ghi chú tài liệu

Spec 2026-10-15 (game overlay) và 2026-10-06 (M4) có nhắc panel/font scale:
không viết lại; thêm ghi chú "superseded bởi spec này" vào đầu file liên quan
khi triển khai.

## 10. Ảnh hưởng hiệu năng

- Mặc định mới **trùng** giá trị PaddleX đang chạy (64/min, 0.3/0.6/1.5, 0.0,
  batch 6, threads 10, tf32 mặc định; riêng oneDNN **tắt** — xem mục dưới), nên
  không đổi kết quả hay tốc độ trên GPU. Phải đo lại 1 lần bằng `Tools/Perf`
  trước/sau trên cùng khung để chứng minh (annotation ≤ 40 ms/khung, OCR ≤ 3 fps,
  VRAM < 1 GB).
- **oneDNN mặc định tắt (2026-11-01, user duyệt):** đo thật cho thấy
  `device=cpu` + `enable_mkldnn=true` làm sidecar chết với lỗi Paddle 3.4
  `ConvertPirAttribute2RuntimeAttribute` (Paddle#77340); `enable_mkldnn=false`
  chạy được. GPU không bị ảnh hưởng (PaddleX tự tắt mkldnn cho GPU), nên đổi
  default chỉ ảnh hưởng đường CPU.
- Tham số user đổi có thể phá budget (model medium, batch lớn, TensorRT/CINN).
  Tooltip cảnh báo; không chặn cứng — đây là lựa chọn của user.
- Đổi tham số reload khi idle làm lần `spot()` kế tiếp chậm ~27 s; warmup của
  `core.start` đã che. Ghi vào `Docs/performance.md` như một mục riêng.
- Bật 3 cờ orientation/unwarping lần đầu kéo theo tải model ngoài catalog;
  trong lúc tải, các `spot()` có thể timeout 30 s liên tiếp và preview đứng.
  Đây là ngoại lệ tạm thời có chủ đích (không phải regression budget); ghi
  rõ trong `Docs/performance.md` và trong tooltip UI.

## 11. Test plan

Python (`ReTran.OCR/tests`):
- Schema phủ đủ 37 tham số (27 phơi + 10 ẩn), đúng `reloadRequired`, đúng
  default của ReTran.
- `set` replace: gửi subset → các key cũ không có trong payload trở về
  default; gửi `{}` → toàn bộ default; một key sai làm cả request -32602 và
  map cũ không đổi (atomic); key lạ/ẩn → -32602.
- `set` live → dùng ở `predict()` kế tiếp (engine giả); reload +
  `deferReload=false` → reset predictor; `deferReload=true` → không reset,
  params mới có hiệu lực sau khi reset; response `{live, deferred}` chỉ chứa
  key thực sự đổi.
- Engine truyền đúng kwargs vào `PaddleOCR(...)` và `predict(...)`
  (monkeypatch class giả).

C# (`ReTran.Tests`):
- `SidecarProcess` trả node con → `JsonRpcServer` serialize được (regression
  node-parent).
- Proxy `ocr.config.schema/set`: forward đúng params; `deferReload` bị ép theo
  `IsRunning`; `core.start` gửi `ocr.params` + `ocrFps/captureFps`; `core.start`
  lọc key lạ bằng schema (sidecar giả trả schema) và **không** fail vì key lạ;
  `ocr.params` vắng mặt = không gọi set.
- `AppSettings` round-trip `ocrEngine` (key lạ được giữ nguyên ở tầng settings,
  không làm hỏng load; Core lọc khi start); file cũ có
  `overlayFontScale`/`overlayMode` vẫn đọc được.
- Validation UI (helper tách khỏi Razor): số ngoài khoảng, JSON hỏng, shape
  sai; nút Lưu không ghi file khi `ocr.config.set` lỗi.
- Xoá test panel/font; `OverlayLayoutTests` chỉ còn subtitle; render test
  không còn `IsPanelLine`.

Kiểm tay:
- Settings → OCR: sửa `text_det_box_thresh` khi pipeline đang chạy → preview
  đổi ngay, toast "Áp dụng ngay"; sửa `device` → toast "sau khi Dừng"; Dừng →
  Start → sidecar nhận `device` mới (log + `ocr.config.schema` không cần).
- OBS source không đổi; overlay cục bộ chỉ còn subtitle; không còn control cỡ
  chữ ở đâu.

## 12. Rủi ro / câu mở

- `engine_config` do user nhập có thể làm hỏng lần Start kế tiếp. Chấp nhận:
  lỗi hiện trong toast + log Core, sửa bằng "Khôi phục mặc định". Không thêm
  cơ chế rollback; `paddlex_config` đã bị ẩn (C11) nên không còn đường YAML
  thay cả pipeline.
- File `settings.json` sửa tay với key lạ không làm hỏng Start (Core lọc —
  C13); key đúng tên nhưng sai giá trị vẫn làm Start fail với thông điệp
  -32602, user sửa trong UI (control hiện lỗi inline).
- `device=gpu:1` trên máy 1 GPU: lỗi nạp ở lần Start kế tiếp, thông điệp từ
  Paddle khá khó đọc; UI chỉ cảnh báo trong tooltip.
- 3 model orientation/unwarping tải ngoài model manager: user không thấy
  dung lượng trong bảng catalog. Ghi rõ trong ghi chú của nhóm.
- Sidecar là nguồn schema, nên UI phụ thuộc Core/sidecar sống. Nếu sidecar
  chết: mục tham số hiện "Không lấy được schema — bấm Làm mới" (giống model
  manager hiện tại), không crash.
