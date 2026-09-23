# ReTran — M2 OCR (Core-side) Design

**Status:** Approved (user đã duyệt từng phần §1–§3 trong brainstorming)
**Date:** 2026-09-23
**Scope:** Milestone M2 theo spec §9 (`2026-09-09-retran-core-app-architecture-design.md`): engine PP-OCR trong sidecar Python, method `ocr.spot`, diff logic trong Core. **Chỉ Core-side** — không nối App UI, không notification, không dịch thuật (M3).

---

## 1. Quyết định đã chốt

| Câu hỏi | Quyết định |
|---|---|
| Scope "xong" M2 | Core-side trước: `ocr.spot` + `OcrEngine` + diff + CLI one-shot + test. Pipeline capture-runtime → App UI để milestone sau. |
| Backend OCR | **PaddleOCR + paddlepaddle** (đúng hướng AGENTS.md; ủy quyền model download). |
| Ngôn ngữ model | **English** (`lang="en"`) làm mặc định; multi-lang là sau (YAGNI). |
| Transport ảnh Core→sidecar | **PNG base64 trong JSON params** — dùng lại `PngWriter` đã có; text-frame nén tốt (~100–500KB/1080p thay vì ~11MB raw). |
| Diff semantics | **Session-seen set**: text chưa từng thấy trong phiên = new; overlay boxes (sau này) lấy trực tiếp từ OCR result mỗi frame, không qua diff. |

## 2. Kiến trúc & data flow (§1)

```
[CLI one-shot]  ReTran.Core.exe ocr <png> ──┐
                                            ├─► OcrPipeline (C#) ─► SidecarProcess.CallAsync("ocr.spot", {image: b64png})
[serve --serve] rpc method ocr.spot ────────┘         │
                                                      ▼
                                     Sidecar Python: OcrEngine (interface)
                                                      └─ PaddleOcrEngine (paddlepaddle + PaddleOCR, lazy init, lang=en)
                                                      │
                                                      ▼ trả spots + elapsed_ms + new_texts
                                     Core OcrDiff: session-seen set → new_texts (log info; chưa dịch)
```

- **App không tham gia** trong M2: không `frame.ocr_result`, không UI, không EF/SQLite.
- **Spawn sidecar:** CLI = mỗi lần chạy 1 process rồi thoát; serve = spawn-once tại serve start (giữ ấm model — rule "long-lived, không re-spawn per frame" trong spec §2).
- **Frame→PNG:** `PngWriter` (đã có trong `ReTran.Core/Capture/`) → base64 → JSON line (framing stdio hiện hữu, 1 message/line).

## 3. Contracts (§2)

### 3.1 `ocr.spot` (JSON-RPC, sidecar)

- **params:** `{ image: string }` — base64 PNG, **không** prefix `data:`.
- **result:** `{ spots: [{ box: [[x,y],[x,y],[x,y],[x,y]], text: string, score: number }], elapsed_ms: number, new_texts: string[] }`
  - `box`: 4 điểm tứ giác theo thứ tự PaddleOCR trả về.
  - `new_texts`: từ Core `OcrDiff.Feed` (xem §3.4) — đi kèm kết quả để caller (CLI log / caller serve) không phải tự diff.
- **errors:**
  - `-32602` — thiếu/invalid `image` (base64 hỏng, PNG hỏng).
  - `-32603` — engine chưa init được / import paddle fail / model init fail; message rõ nguyên nhân (`"paddleocr not installed: ..."`, `"model init failed: ..."`).

### 3.2 `OcrEngine` (Python — `retran_core/ocr/engine.py`)

```python
class OcrEngine(Protocol):
    def spot(self, image_bgr: npt.NDArray) -> list[Spot]: ...
```

- `Spot`: box tứ giác 4 điểm + text + score (dataclass hoặc NamedTuple).
- `PaddleOcrEngine(lang="en")`:
  - **Lazy init** — load PaddleOCR pipeline ở **first `spot` call**, không ở import/`__main__` (giữ sidecar startup stdlib-nhanh, M0 behavior không đổi).
  - Nhận `image_bgr` (numpy H×W×3, BGR — chuẩn OpenCV/Paddle); decode PNG→RGB→BGR thực hiện ở tầng handler (`PIL`).
- Deps thêm vào `pyproject.toml`: `paddlepaddle`, `paddleocr`, `pillow`.
- Handler `ocr.spot` trong `__main__._handlers()` decode base64 → `PIL.Image` → numpy BGR → `engine.spot` → result dict (qJsonrpc handler style hiện hữu).

### 3.3 CLI `ocr`

```
ReTran.Core.exe ocr <input.png>
```

- stdout: JSON result đầy đủ như `ocr.spot` (+ `new_texts`), exit 0.
- File thiếu/không đọc được → stderr message, exit 1.
- Sidecar spawn fail / call timeout (**30s** — bao gồm cả lần init model đầu) → stderr message, exit 1.
- Subcommand đăng ký qua `CommandDispatcher` (cùng pattern `CaptureCommands.Register`) — handler dùng chung `OcrPipeline.SpotAsync` với serve mode (spec §4: one-shot reuse handler).

### 3.4 Diff — `OcrDiff` (C#, Core)

- `IEnumerable<string> Feed(spots)`:
  - So text mới với session set theo `StringComparison.OrdinalIgnoreCase`.
  - Text chưa có trong set → thêm vào set, trả về như "new".
  - Set giữ nguyên suốt phiên (CLI = 1 lần chạy; serve = cả đời process). Không persist ra file (Core scratch JSON chưa cần trong M2).
- New lines: log mức `info` qua `ILogger` (chưa gửi đi đâu — translation round-trip là M3).
- Cả serve handler `ocr.spot` và CLI đều đi qua **một** handler `OcrPipeline.SpotAsync` → `OcrDiff.Feed` → result (đã thống nhất §2).

## 4. Errors (§3)

| Tình huống | Hành vi |
|---|---|
| paddle/paddleocr chưa cài hoặc import fail (lúc first call) | sidecar trả `-32603` kèm message; sidecar **không crash** |
| Model init/download fail | `-32603` `"model init failed: ..."` |
| PNG/base64 hỏng | `-32602` |
| Sidecar chết giữa chừng (serve) | `CallAsync` waiter fail → method trả `-32603`; Core serve loop không crash |
| CLI spawn fail / timeout 30s | exit 1 + stderr |

Lỗi môi trường (thiếu paddle, thiếu model, thiếu sidecar) trong **test** phải **skip êm** (early return, style `SidecarProcessTests`) — không bao giờ fail vì môi trường (hard constraint test plan đã dùng lại ở đây).

## 5. Testing

### Python (`uv run pytest tests -v` từ `ReTran.OCR/`)

- Unit: decode PNG base64→numpy BGR (fixture PNG nhỏ); handler `ocr.spot` với **FakeEngine** (trả spots cố định — không paddle); error paths (invalid base64 → -32602, engine raise → -32603).
- Integration thật PaddleOCR: **1 test**, mark/`pytest.mark.skipif` style — skip êm khi paddle chưa cài hoặc model chưa download được (mạng). Chạy thật được thì assert spots không rỗng trên fixture ảnh có chữ.

### C# (`ReTran.Tests`, xUnit)

- `OcrDiff` unit: text mới / đã thấy / hoa thường / trộn nhiều box.
- `OcrPipeline` với **mock transport/`SidecarProcess` abstraction** (không env-hook trong product code): map params→result, new_texts đi kèm, error map đúng.
- CLI dispatch `ocr`: đăng ký đúng CommandDispatcher; missing file → exit 1.
- Integration `ocr` CLI với sidecar thật + model thật → skip êm nếu thiếu hạ tầng (giống style hiện hữu).

Fixture: 1 ảnh PNG có sẵn chữ (đặt trong `ReTran.Tests/Fixtures/` hoặc `ReTran.OCR/tests/fixtures/` — plan sẽ chốt vị trí cụ thể).

### Performance — `Docs/performance.md` (tạo mới, ghi số thật trên máy này)

- Latency OCR 1080p: **cold** (first call, gồm model load/download) vs **warm**.
- RAM/VRAM sidecar sau khi load model.
- Ghi chú so với budget: ~3fps throttle, VRAM < 1GB (spec §7) — M2 chưa có frame loop, chỉ ghi số 1-call làm baseline.
- Note: model det+rec `en` ~15–20MB tải lần đầu.

## 6. Ngoài scope M2 (milestone sau / đã ghi nhận)

- Capture runtime RPC (`capture.*`) + notification `frame.*` → App UI (cổng Task 7 của UI plan).
- `frame.ocr_result` / `frame.translated` notification.
- Translation (`ITranslator`, EF/SQLite cache round-trip) — M3.
- OBS export — M4; Rust hook — M5.
- Đổi ngôn ngữ OCR qua settings (YAGNI với `en` mặc định).
- Box-tracking/IoU diff, frame throttle loop 3fps (chưa có pipeline loop trong M2).

## 7. Rủi ro / lưu ý

- **PaddleOCR + paddlepaddle nặng deps** (~vài GB disk, RAM load model vài trăm MB): nằm trong budget VRAM < 1GB nhưng cần verify số thật → performance.md.
- **PaddleOCR API đổi giữa bản 2.x/3.x** (`PaddleOCR(...)` constructor, `.predict` vs `.ocr`): plan sẽ pin version trong pyproject và viết theo API của version đã pin.
- Stdio line ~500KB max hợp lý (không có limit cứng của pipe ở mức này) — theo dõi nếu frame phức tạp (nhiều màu) nén kém hơn.
