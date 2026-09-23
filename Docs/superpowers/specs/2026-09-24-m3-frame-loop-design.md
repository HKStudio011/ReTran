# Spec: M3 — Frame loop + Perf gate

- **Ngày:** 2026-09-24
- **Trạng thái:** Approved (user review qua brainstorming session)
- **Supersede:** đánh số "M3 = translation" trong spec kiến trúc cũ — translation dời sang **M4**.
- **Phạm vi:** Core-side. Không App UI, không translation, không serve-mode loop.

## 1. Bối cảnh

M2 (đã push, `0a94f2f`) dựng xong OCR một-lần: sidecar `ocr.spot` (PaddleOCR lazy, CPU), `OcrPipeline` (30s timeout, `new_texts` qua `OcrDiff` session-seen), CLI `ocr <png>` (exit 0/1/2), serve mode (error-code matrix). `Docs/performance.md` ghi baseline trung thực: warm median 1080p **35.467 s** (BREACH ~106× budget 333 ms), 640×96 **3.003 s**, nguyên nhân chính = det dùng **bản server** + 3 model phụ doc/textline + input full-res + máy đo đang load 60–96%.

M1 capture (merged) đã có: `ICaptureSource` event `FrameArrived` với loop riêng @10 fps (`ScreenCaptureSource`/`WindowCaptureSource`), `LatestFrameBuffer` (drop-if-busy, có drop counter), `PngWriter` (chỉ ghi file), CLI `capture screen|window`. `CoreConfig.OcrFps = 3` tồn tại nhưng chưa runtime nào đọc.

M3 = biến OCR thành **pipeline chạy liên tục** ở ~3 fps, đồng thời **gỡ perf gate** để con số đó có ý nghĩa.

## 2. Quyết định đã chốt (brainstorming)

| # | Quyết định | Lựa chọn |
|---|---|---|
| 1 | Loop host + output đi đâu | **CLI `ocr loop`** — serve mode không đổi; App push tính sau |
| 2 | Perf gate ngưỡng | **Đo rồi chốt cùng user** — không hardcode 3fps vào spec; đạt → PASS, chưa → user approve tradeoff / yêu cầu tối ưu thêm |
| 3 | nguồn khung hình | **Screen (`--monitor`) + Window (`--hwnd`)** — game hook ngoài scope |
| 4 | Output mỗi chu kỳ | **JSON-lines: chỉ `new_texts`** (diff-based) |
| 5 | Cấu trúc loop | **A: Event → LatestFrameBuffer → PeriodicTimer tick** — tái dùng tối đa M1 |

## 3. Kiến trúc & data flow

Subcommand mới trong `OcrCommands`:

```
ocr loop (--monitor N | --hwnd 0xHEX) [--fps N] [--max-frames N]
```

```
capture source (loop 10fps sẵn có, FrameArrived)
  → LatestFrameBuffer (drop-if-busy — sẵn có từ M1)
  → PeriodicTimer @ OcrFps (CoreConfig.OcrFps, default 3; --fps override)
  → lấy frame hiện tại (nếu chưa có frame mới từ tick trước → bỏ qua tick,
     KHÔNG OCR lại frame cũ; frame cũ chỉ OCR khi thực sự mới)
  → BGRA → PNG in-memory (encoder mới trong Core; PngWriter hiện chỉ ghi file)
  → base64 → OcrPipeline.SpotAsync (M2, giữ nguyên: 30s timeout, OcrDiff,
      new_texts, error wrapping)
  → emit 1 JSON line stdout
```

- **Cadence:** tick 333 ms; OCR bận → frame đến tiếp bị buffer thay thế (drop), tick sau lấy frame mới nhất. Không queue, không backpressure — đúng budget "throttle ~3fps + drop frames if busy".
- **OcrDiff session:** 1 instance / 1 process / 1 nguồn — `OcrDiff` không cần reset API (nguồn không đổi giữa chừng). HashSet text đã thấy growth = vài K string cho phiên dài → chấp nhận (negligible); ghi rõ trong doc.
- **Log/warning:** mọi thứ ngoài JSON output đều ra **stderr** — stdout chỉ có JSON lines.

## 4. Perf gate (điều kiện chốt milestone)

Áp dụng cho path OCR của loop (sidecar, không đổi contract):

1. **Det → mobile:** pin detection model `PP-OCRv5_mobile_det`; **tắt** doc-orientation classify, doc unwarping, textline orientation (3 model phụ đang bị default server pipeline kéo theo — không cần cho game/UI text). Rec giữ `en_PP-OCRv5_mobile_rec` (đang là mobile rồi). API thật của `PaddleOCR(...)` do plan chốt với evidence từ installed package (PaddleOCR 3.4.1).
2. **Downscale trước detect:** nếu long-side > **1280 px** → resize tỉ lệ về 1280 (Python-side, PIL, trong path decode trước khi gọi engine). Lý do: số đo M2 cho thấy latency phụ thuộc mạnh input size. Tham số default là hằng số Python (module-level), plan được phép nâng thành param `ocr.spot` nếu cần test.
3. **Re-measure máy idle:** user đóng ComfyUI/Hermes/WSL (hoặc xác nhận máy trống) lúc đo. Đo lại: cold, warm N=5 (giới hạn median/min/mean), CPU/RAM, với fixture 1080p + fixture nhỏ — ghi `Docs/performance.md` section **M3** (giữ section M2 làm baseline so sánh).
4. **Acceptance:** số đo xong → present cho user chốt tại final review:
   - warm ≤ 333 ms (đủ 3 fps) → **PASS**;
   - vượt → user quyết: approve tradeoff (ghi rõ) hay yêu cầu tối ưu thêm trước khi done.
   Không được tuyên bố PASS/BREACH mà không có số đo thật (constraint xuyên suốt từ M2).
5. **Ngoài gate này (giữ nguyên, đã ghi nhận từ M2):** mkldnn vẫn tắt (upstream Paddle #77340), GPU paddle = milestone sau, peak-RAM 11.3 GiB cần theo dõi khi đo lại nhưng không đặt ngưỡng mới trong M3.

## 5. Output contract

Mỗi chu kỳ loop emit đúng 1 dòng JSON compact (UTF-8) ra stdout:

```json
{"seq":1,"at":"2026-09-24T12:00:00.000Z","new_texts":["HELLO"],"spots_count":3,"elapsed_ms":412}
```

| Field | Ý nghĩa |
|---|---|
| `seq` | Số thứ tự chu kỳ, tăng từ 1, **không reset** |
| `at` | ISO-8601 UTC thời điểm emit |
| `new_texts` | Các dòng text **mới** so với session (`OcrDiff.Feed`) — array, có thể rỗng |
| `spots_count` | Tổng số box OCR được trong frame này (kể cả đã seen) |
| `elapsed_ms` | Thời gian `SpotAsync` trả về (từ sidecar `elapsed_ms`) |

- Chu kỳ không có text mới vẫn emit (`"new_texts":[]`) — consumer phân biệt được loop sống.
- `--max-frames N`: dừng sau N chu kỳ (default `0` = vô hạn) — hook cho test/demo.
- Exit codes theo convention `OcrCommands`: **0** = kết thúc sạch (Ctrl+C hoặc `--max-frames` xong); **1** = infra/runtime (thiếu file/nguồn, first-frame timeout, sidecar chết, spawn fail); **2** = usage (sai args, `--monitor` + `--hwnd` cùng lúc, cả hai đều thiếu).

## 6. Error handling & shutdown

- **First-frame timeout 5 s** (reuse pattern `CaptureCommands.FirstFrameTimeoutSeconds`) → stderr + exit 1.
- **Ctrl+C** (`Console.CancelKeyPress`): stop capture source → dispose sidecar → flush stdout → exit 0. Không kill ngang; event handler đặt `e.Cancel = true` rồi loop thoát có trật tự.
- **Sidecar chết giữa loop:** M2 đã có `_exited` guard → `SidecarRpcException` → in message ra stderr, **exit 1** (không im lặng spin, không restart tự động — restart вне scope).
- **Timeout từng chu kỳ:** `OcrPipeline` giữ 30 s (bao gồm cold init). Nếu timeout xảy ra: warning stderr, **bỏ chu kỳ đó** (không emit JSON line thiếu), tiếp tục vòng sau — trừ khi sidecar đã dead (case trên).
- Frame encode fail (memory, stride lạ): warning stderr + bỏ chu kỳ, tiếp tục.

## 7. Testing

Mục tiêu test-count do plan chốt (baseline hiện tại: **C# 55, Python 11**). Style: skip-êm bằng early return (không `Assert.Skip`), không `Console.WriteLine` trong test (dùng redirect harness của `OcrCliTests`).

- **Unit — encoder:** `BgraToPng` roundtrip (PNG giải lại đúng W/H/đúng pixel mẫu); stride có padding.
- **Unit — loop tick:** fake `ICaptureSource` + fake `ISidecarRpc` (kỹ thuật inject như `OcrPipelineTests`/`ServeOcrTests`): drop-if-busy (buffer 1 slot → frame bị thay không bị OCR), tick không OCR lại frame cũ, seq tăng, JSON shape đúng, `--max-frames` dừng, stop có trật tự.
- **Unit — CLI args:** usage exit 2 (thiếu nguồn / cả hai nguồn / fps không hợp lệ).
- **Integration (skip-êm khi thiếu môi trường):** `ocr loop --monitor 0 --max-frames 2` sidecar thật → đúng 2 JSON lines parse được; sidecar chết giữa loop (giết process) → exit 1 + stderr.
- **Perf:** đo theo §4, ghi `Docs/performance.md` — số thật, method + confounder disclosed (kỷ luật M2).

## 8. Ngoài phạm vi M3

- **Translation** → M4 (spec riêng; `ITranslator` + provider + cache).
- App push/UI (`JsonRpcClient` chưa xử lý notification), serve-mode loop (dispatch tuần tự của `JsonRpcServer` chưa sửa), OBS export, game hook (Rust, cargo chưa cài).
- D-items của M2 (relay `SidecarRpcException.Code` → -32602 ở serve, `CoreConfig.LoadFrom` vào try, `decode_png_b64` PNG magic bytes, `PythonExe`/repoRoot layout cho bản cài đặt, A-item docs typos) — **không** thuộc M3; giữ nguyên ledger.
- mkldnn re-enable khi upstream fix; GPU paddle.

## 9. Rủi ro đã nhận thức

| Rủi ro | Xử lý |
|---|---|
| CPU-only + mobile det vẫn có thể chưa chạm 333 ms | Acceptance "đo rồi chốt cùng user" (§4.4) — không hứa hẹn trong spec |
| BGRA→PNG mỗi frame là CPU cost riêng | Encoder mới + đo; nếu đáng kể, downscale trước encode là lever dự phòng (plan được phép xem xét) |
| Measured numbers mùa này bẩn (máy load) | Re-measure khi idle là điều kiện bắt buộc (§4.3) |
| `PeriodicTimer` drift khi OCR > tick period | Design chấp nhận: tick đến sớm nhưng buffer đã drop → OCR frame mới nhất, không backlog |
