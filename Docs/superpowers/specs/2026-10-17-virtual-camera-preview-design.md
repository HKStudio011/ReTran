# Spec: Camera ảo + Preview — đầu ra kiểu OBS Virtual Camera, preview trong app

- **Ngày:** 2026-10-17
- **Trạng thái:** Draft — chờ user review
- **Phạm vi:** Core (render khung annotated + IPC frame mới) + App (preview + camera output + dọn HTTP feed). **Không đổi** Python sidecar, overlay local đè game, pipeline capture/OCR/translate.
- **Nguồn:** Brainstorming session 2026-10-17 (pain: đường ra OBS hiện tại là HTTP browser source mà user không dùng; muốn kiểu chuẩn của mọi app stream — camera ảo phát đi + màn hình preview thấy đang phát gì).

## 1. Bối cảnh

Hiện tại pipeline publish `OverlayState` (toạ độ box + text) cho hai consumer:

- `OverlayFeedServer` (HTTP + SSE, `127.0.0.1:17863/overlay`) → OBS Browser Source — **user không dùng tới, bỏ hẳn**.
- `OverlayWindowService` → cửa sổ overlay local đè lên game — **giữ nguyên** (vừa làm xong, phục vụ người chơi lúc chơi).

Ý tưởng camera ảo từng bị hoãn trong spec game-overlay 2026-10-15 (mục 1, mục 13) — phiên này mở lại và chốt. Lưu ý **app hiện không hề nhận pixel khung hình** (Core chỉ gửi toạ độ + text) — tính năng này kéo theo việc đưa pixel từ Core sang App.

## 2. Quyết định đã chốt (brainstorming 2026-10-17)

| # | Câu hỏi | Lựa chọn |
|---|---|---|
| 1 | Đầu ra ngoài | **Camera ảo "ReTran Camera"** — thiết bị hiện trong danh sách camera của Windows; OBS/Discord/Zoom chọn là dùng (giống OBS Virtual Camera). |
| 2 | Nội dung camera | **Khung gốc đang capture + box OCR + bản dịch vẽ đè** — video đục, không alpha. |
| 3 | Bật/tắt | **Tự chạy cùng pipeline**: Start → phát hình, Stop → ngừng (giữ khung cuối). Không nút riêng kiểu OBS. |
| 4 | Preview | Trang điều khiển **layout A**: preview lớn phía trên (thấy đúng thứ camera phát ra), card điều khiển xếp dưới. Pipeline dừng → giữ khung cuối + badge "Đang dừng". |
| 5 | HTTP feed | **Bỏ hẳn** — `OverlayFeedServer`, trang overlay HTML, card/thiết OBS liên quan, port 17863. |
| 6 | Overlay local đè game | **Giữ nguyên** — phục vụ người chơi locally, không thay bằng camera. |
| 7 | Nơi render khung annotated | **Core** — một nguồn render duy nhất cho đầu ra (preview == camera cùng một bitmap); App chỉ hiển thị + bơm ra device. (Quyết định builder, xem §3.) |
| 8 | Tần suất | Khung annotated phát theo **capture fps (≤10 fps)**, re-annotate với translation mới nhất; OCR 3 fps chỉ cập nhật nội dung text bên trong. Device khai **30 fps, frame-hold** giữa các update. |
| 9 | Demo mode | `DemoFeed` tiếp tục điều khiển overlay local demo như cũ; preview hiển thị badge "DEMO" trên nền tối (không có pixel thật). |
| 10 | Đăng ký thiết bị | **HKCU per-user, không cần admin** — app tự đăng ký idempotent mỗi lần khởi động (phân phối dạng zip, không có installer). |
| 11 | Toolchain filter camera | **C++/MSVC x64** (DirectShow reference code đầy đủ, VS C++ tools đã sẵn trên máy; Rust/cargo chưa cài — định hướng hook để sau). |
| 12 | Vòng đời device khi app đóng | **Kiểu OBS Virtual Camera** — device vẫn hiện trong danh sách, nhận **hình đen/tĩnh** khi app đóng; mở app lại là sống lại. (Filter phải tự xử lý "không có writer" = ra frame đen, không treo.) |

## 3. Kiến trúc & luồng dữ liệu

```
Core (serve mode):
  ICaptureSource.FrameArrived @ captureFps (~10)
     ├─ OcrLoop @ ocrFps (3) → TranslateCycleSink → "frame.translated"
     │        → App OverlayState → overlay local  [GIỮ NGUYÊN]
     └─ Annotator (mới, @ ≤10 fps) — IFrameAnnotator sau interface nhỏ:
            downscale frame ≤ 1280w → GDI+ draw box + translated (lấy từ
            session state mới nhất) → JPEG encode (~q70)
            → notification "frame.annotated" {seq, w, h, jpeg: base64}

App:
  PipelineController.HandleFrameAnnotated (mới)
     ├─ PreviewState (latest + Changed event) → Home.razor preview <img>
     │      throttle ≤ 15 fps, data-URL; pipeline dừng → giữ khung cuối + badge
     └─ CameraOutputService (mới) → memory-mapped file ring (Local\ReTran.Camera)
            ↑ ReTran.Camera.dll — source filter camera (user-mode, DirectShow,
              đọc shared memory) — được OBS/Discord/Zoom load như camera thường
```

- Toạ độ box vốn chuẩn hoá 0–1 → scale theo kích thước frame, không đụng DPI.
- Demo mode đi vòng cung khác: badge DEMO, không sinh frame.annotated.

## 4. File mới/sửa (cấp cao — chốt chi tiết ở plan)

**Core (mới):** `Serve/FrameAnnotator.cs` (+ interface `IFrameAnnotator`), emission từ `PipelineHost` session song song OcrLoop, notification mới qua `JsonRpcServer`.

**App (mới):** `Core/PreviewState.cs`, `Services/Camera/CameraOutputService.cs` (writer shared memory + start/stop theo pipeline), Home.razor preview pane (layout A), i18n strings mới (vi/en), DI wiring, kiểm tra `JsonRpcClient`/reader chịu được line ~150 KB.

**Camera filter (mới):** project native `ReTran.Camera/` — DirectShow source filter (user-mode COM), đăng ký thiết bị theo hướng không cần admin nếu được; format YUY2 1280×720@30.

**Tháo gỡ:** `Services/OverlayFeedServer.cs`, `Services/IOverlayFeedServer.cs`, `wwwroot/overlay/`, feed wiring trong `MainPage.xaml.cs` + `MauiProgram.cs` + `Home.razor`, OBS settings trong `settings.json`, dependency feed trong `Services/Update/UpdateStopper.cs`, test liên quan (`OverlayFeedServerTests`, phần feed của `UpdateStopperTests`). `DemoFeed` + `OverlayState` **giữ** (overlay local dùng).

## 5. Hiệu năng (budget)

| Hạng mục | Ngân sách |
|---|---|
| Render + JPEG 1280×720 @10 fps | ≤ ~10 ms/khung (gate đo thực tế; vượt → hạ 960w hoặc 7.5 fps) |
| IPC Core→App (JPEG base64) | ≤ ~1.5 MB/s local pipe; đo, vượt → giảm fps/res trước khi đổi transport |
| Camera filter | 1–2 frame buffer 720p + convert YUY2 — đo CPU |
| Preview WebView | img swap ≤ 15 fps; canvas là fallback nếu flicker |
| Tổng | Vẫn trong budget AGENTS: < 8% CPU, < 500 MB RAM khi gaming + streaming; ghi số vào `Docs/performance.md` |

## 6. Non-goals

- Không virtual display, không hook/injection, không Rust.
- Không alpha/nền trong suốt trong camera (thiết bị camera không có).
- Không đổi OCR/translate/sidecar, không đổi overlay local đè game.
- Không xử lý HDR screen-source (máy user hiện capture-error) hay Google 429 — hai việc riêng, spec/issue riêng.
- Không độ phân giải camera > 720p trong bản đầu.

## 7. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| Device camera chưa chắc hiện ở mọi app (đăng ký per-user vs admin) | Spike sớm nhất có thể: filter tối thiểu phải hiện trong OBS + Discord + Zoom trên Win10/11; fallback: cài đăng ký 1 lần cần admin, hoặc hướng Media Foundation |
| JSON line ~150 KB qua stdio (độ trễ / buffer) | JPEG ~q70 ≤ 1280w; đo throughput; fallback: memory-mapped frame ring thay JSON line |
| GDI+ render tốn CPU ở capture rate | Budget gate §5; hạ resolution/fps trước khi cân lại design |
| Preview 10 fps trong WebView flicker/chậm | Throttle 10 fps, canvas fallback |
| Format camera không được app đích chấp nhận (cần NV12/MJPG) | Bắt đầu YUY2 (phổ biến nhất); thêm format sau nếu có app từ chối |

## 8. Acceptance

1. **OBS** thêm *Video Capture Device* thấy "ReTran Camera" và nhận hình; **Discord/Zoom** thấy device. (Windows Settings → Cameras chỉ best-effort: filter DirectShow thường không hiện ở trang Settings enumerates qua MF — không làm gate.)
2. Nội dung camera = khung capture + box + bản dịch; đổi nguồn (window/screen) → camera theo kịp.
3. Start pipeline → camera tự phát; Stop → ngừng, giữ khung cuối; preview đồng bộ, badge đúng trạng thái.
4. Trang điều khiển theo layout A: preview lớn trên, card dưới; demo mode hiện badge DEMO.
5. Port 17863 không còn listener; `/overlay` biến mất khỏi code và wwwroot.
6. Overlay local đè game hoạt động không đổi.
7. Đo performance với game thật, ghi số vào `Docs/performance.md`.

## Revision (2026-10-21)

Plan [2026-10-19-pipeline-perf-fix](../plans/2026-10-19-pipeline-perf-fix.md) đã land phần transport (đo app-driven sau fix còn mở) và đổi đường truyền khung annotated so với thiết kế trên: JPEG của khung annotated giờ đi qua **shared memory** `Local\ReTran.Preview.V1` (Core ghi / App đọc, seqlock giống camera); notification `frame.annotated` chỉ còn `{seq, w, h}` (w/h là kích thước khung **annotated** ≤1280, không phải khung nguồn như bug cũ). Camera/filter không đổi (`Local\ReTran.Camera.V1` + heartbeat + `ReTran.Camera.dll` vẫn như trên). Số đo trước/sau: `Docs/performance.md` §M5-fix.
