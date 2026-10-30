> **Ghi chú (superseded):** Phần panel mode (bảng dịch ở góc) và cỡ chữ thủ công của spec này đã bị thay thế bởi spec `2026-10-26-ocr-engine-settings-overlay-simplify-design.md` §9 — overlay chỉ còn subtitle, cỡ chữ tự scale theo box.

# Spec: M4 — Translation (cache lookup + dịch + UI cài đặt)

- **Ngày:** 2026-10-06
- **Trạng thái:** Draft — chờ user review
- **Phạm vi:** Core (loop serve + `ITranslator` + Google provider), App (EF cache, IPC 2 chiều, nối overlay), UI (Start/Stop ở trang điều khiển + trang Cài đặt mới).
- **Nguồn:** pipeline bước 4–5 (cache lookup + translate) — spec M0 chốt diff/cache split, M3 chốt "translation → M4".

## 1. Bối cảnh

M0–M3 đã làm: IPC backbone (App spawn Core, JSON-RPC stdio), capture screen/window, sidecar OCR (`ocr.spot`), `OcrDiff` + `OcrLoop` (CLI `ocr loop`, JSON-lines), App UI + overlay SSE (`OverlayState`/`OverlayFeedServer`) nhưng hiện chỉ có `DemoFeed` publish. Serve mode mới có `core.ping`/`core.version`/`ocr.spot`; `Program.cs:30` còn comment *"translate handler lands in M4"*. Chưa có EF DbContext, `JsonRpcClient` (App) chưa nhận request/notification, `JsonRpcServer` (Core) chưa notify được, chưa có `ITranslator`.

## 2. Quyết định đã chốt (brainstorming 2026-10-06)

| # | Câu hỏi | Lựa chọn |
|---|---|---|
| 1 | Phạm vi M4 | **C** — end-to-end (serve loop + notification → overlay/OBS) + UI settings đầy đủ |
| 2 | Provider đầu tiên | **A** — Google web endpoint (không cần key, không chính thức) + `ITranslator` sẵn cho provider sau |
| 3 | Khi nào pipeline chạy | **A** — user bấm Start/Stop trên trang điều khiển; không tự start khi mở app |
| 4 | Ai điều phối translate | **Đường A — Core điều phối** theo đúng diff/cache split của M0: Core diff + lookup + dịch, App giữ cache SQLite. Local model sau này nằm trong sidecar (Core) nên không phải chuyển lại |

## 3. Hợp đồng IPC (JSON-RPC stdio, bổ sung cho M0)

### 3.1 App → Core (request)

**`core.start`**
```json
{
  "source":    { "kind": "screen", "monitor": 0 },
  "translate": { "provider": "google", "src": "auto", "dst": "vi" },
  "ocrFps": 3, "captureFps": 10
}
```
- `source`: `"screen"` + `monitor` (int) **hoặc** `"window"` + `hwnd` (string `"0x…"`, hex).
- `translate.src` cho phép `"auto"`; `provider` phải có trong registry否则 `-32602`.
- `ocrFps`/`captureFps` optional — default từ `CoreConfig` (3 / 10).
- **Context key Core tự tính**, App không truyền: window → tên process của hwnd (viết thường, vd `notepad.exe`), screen → `screen:<monitor>`, không xác định được → `unknown`.
- Result `{"running": true}`. Lỗi: `-32602` params sai / provider lạ; `-32600` `"pipeline already running"` (App phải `core.stop` trước); `-32603` sidecar không sẵn sàng / mở capture source thất bại.

**`core.stop`** → `{"running": false}` — idempotent (chưa chạy cũng trả `false`, không lỗi).

**`core.list_sources`** → `{ "monitors": [{"id":0,"name":"…"}], "windows": [{"hwnd":"0x…","title":"…","process":"…"}] }`
*(bổ sung cần cho dropdown nguồn trên UI — tái dùng logic `CaptureCommands.ListMonitors/ListWindows`, extract thành nơi dùng chung.)*

### 3.2 Core → App (request — chiều ngược, App phải reply)

**`translate.request_cache`** — batch, tối đa 1 lần mỗi chu kỳ OCR:
```json
req : { "context": "screen:0", "src": "auto", "dst": "vi", "texts": ["HELLO", "HP: 99"] }
resp: { "hits": { "HELLO": "Xin chào" }, "misses": ["HP: 99"] }
```

### 3.3 Core → App (notification, không id)

- **`translate.put`** — `{context, src, dst, entries:[{text, translated}]}` — App upsert best-effort.
- **`frame.translated`** — `{seq, w, h, boxes:[{x,y,w,h,text,translated}]}`:
  - Emit **full box list mỗi chu kỳ** (~3 fps), kể cả `boxes:[]` — để overlay tự clear khi text biến mất; `seq` tăng không reset (như M3).
  - Toạ độ **normalized 0–1** (khớp `OverlayBox`). Nếu sidecar trả pixel thì Core chia cho `w`/`h` (plan xác minh contract thật của `Spot.to_dict` — bước 0).
  - `translated: null` = chưa dịch xong / lỗi → UI hiển thị text gốc.
- **`pipeline.state`** — `{running: bool, reason?: string}` — Core push khi loop **dừng đột xuất** phía Core (sidecar chết, capture lỗi, lỗi nội bộ); `reason` là code ngắn (`sidecar-exited`, `capture-error`, `internal`). Start/Stop bình thường đã có kết quả request nên không cần notification này.

## 4. Core: loop + ITranslator + điều phối

### 4.1 Tái dùng loop M3 — không viết vòng lặp thứ hai

Refactor nhẹ `OcrLoop` (hiện nhận `TextWriter` emitter) thành **`ICycleSink`**:
- CLI `ocr loop` → sink JSON-lines stdout cũ — contract + test giữ nguyên.
- Serve mode → **`TranslateCycleSink`**.

### 4.2 Chu kỳ trong serve (`TranslateCycleSink`)

```
frame mới → BGRA→PNG → OcrPipeline.SpotAsync → spots (box + text)
  → emit frame.translated (full boxes, annotate từ session translation dict)
  → OcrDiff.Feed → new_texts; rỗng → xong chu kỳ
  → task nền (KHÔNG block tick):
      translate.request_cache (batch 1 RPC, timeout 2s → fail = coi hết là miss)
      → hits: điền thẳng vào dict
      → misses: ITranslator.TranslateAsync song song, tối đa 4 request
                (SemaphoreSlim), dedupe text đang dịch dở
      → translate.put (App ghi DB) + cập nhật session dict
```
- Chu kỳ sau emit lại với `translated` đã có. Tick **không bao giờ** chờ HTTP/RPC.
- Text dịch lỗi → `translated:null`, log stderr, **không retry trong phiên** (`OcrDiff` đã seen; chỉ start lại mới thử lại — khi đó cache cũng sẽ miss).

### 4.3 `ITranslator` + Google

```csharp
public interface ITranslator
{
    string Name { get; }
    Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct);
}
```
- **`GoogleWebTranslator`**: GET `https://translate.googleapis.com/translate_a/single?client=gtx&sl={src}&tl={dst}&dt=t&q={urlencoded}` (`src="auto"` → `sl=auto`), parse `result[0][i][0]` rồi nối; `HttpClient` timeout **5 s**; không cần key.
- **Registry** theo tên: `"google"` đăng ký lúc serve start; provider lạ → `-32602` khi `core.start`. Tương lai thêm DeepL/LLM/local chỉ là đăng ký thêm.

### 4.4 Vòng đời & phiên

- Sidecar spawn **1 lần** khi serve start (đã có sẵn trong `RunServeAsync`).
- Mỗi `core.start` = **phiên mới**: `OcrDiff` mới + translation dict mới (stop→start thì text cũ hiện lại và dịch lại nếu cache miss). `core.stop` → hủy token loop, dispose capture source, **giữ** sidecar.
- Handler `core.start` trả về ngay; loop chạy background task, không block read-dispatch của `JsonRpcServer` (dispatch hiện tuần tự).
- `JsonRpcServer` thêm **`NotifyAsync(method, params)`** ghi notification qua write-lock chung với response.

### 4.5 CLI one-shot (đóng comment `Program.cs:30`)

`ReTran.Core translate "text" --dst vi [--src auto] [--provider google]` — in bản dịch ra stdout; test E2E không cần App. Exit code theo convention existing: 0 sạch / 1 runtime / 2 usage.

## 5. App: EF cache + JsonRpcClient + nối overlay

### 5.1 Persistence

- `Microsoft.EntityFrameworkCore.Sqlite`; **migration từ đầu** (không raw schema); `db.Database.Migrate()` lúc app start.
- Bảng `TranslationEntry`: `Id`, `Context`, `Text`, `Src`, `Dst`, `Translated`, `CreatedAtUtc` — **unique index (Context, Text, Src, Dst)** = cache key đã chốt.
- DB: `%LOCALAPPDATA%\ReTran\retran.db` — **chỉ App mở**; Core không đụng file.
- `settings.json` (cạnh DB): `{ "provider", "src", "dst", "apiKey"? }` — `apiKey` dành cho provider sau (Google web không cần). Settings page đọc/ghi file này.

### 5.2 Mở rộng `JsonRpcClient` — ReadLoop 3 nhánh

1. có `id` + `result/error` → response cho `CallAsync` (giữ nguyên);
2. có `id` + `method` → **request từ Core** → handler App → ghi response;
3. không `id` + `method` → **notification** → dispatch handler.

Toàn bộ outbound đi qua **1 `SemaphoreSlim` write-lock** (response của `CallAsync` và reply request không đan xen ghi chồng). Handler exception bắt riêng — không được kill read loop.

### 5.3 Handler App-side (đăng ký qua `CoreProcessClient`)

| Method | Việc làm |
|---|---|
| `translate.request_cache` | Query batch `WHERE Context+Src+Dst AND Text IN (…)` → `{hits, misses}` |
| `translate.put` | Upsert theo unique index (conflict → update `Translated`), best-effort catch + log |
| `frame.translated` | Parse → `OverlayState.Publish(new OverlayFrame(w, h, now, boxes))` → trang Điều khiển + SSE/OBS tự cập nhật |
| `pipeline.state` | Đổi badge trạng thái UI + hiện `reason` |

### 5.4 Start/Stop

Start → (nếu `DemoFeed` đang chạy thì stop, tránh 2 nguồn publish cùng `OverlayState`) → đọc `settings.json` + lựa chọn nguồn → `core.start`. Core chết/ngoại lệ → `CoreProcessClient` exit event → badge lỗi.

## 6. UI

### 6.1 Trang Điều khiển (`/`, hiện có) — thêm khối Start/Stop

- Badge trạng thái: `■ Đang dừng` ↔ `● Đang chạy` (đi từ kết quả `core.start`/`core.stop` + `pipeline.state`).
- Chọn nguồn: `Màn hình — Monitor N` / `Cửa sổ — <title> (process)` từ **`core.list_sources`** + nút *Làm mới danh sách*.
- Nút **Bắt đầu/Dừng**; lỗi hiện ngay dưới nút.
- Chọn nhanh ngôn ngữ đích (ghi thẳng `settings.json.dst`, áp dụng cho lần start kế tiếp).

### 6.2 Trang Cài đặt mới

- **Provider**: `Google (web, không cần key)` enabled; `DeepL` / `LLM (OpenAI-compatible)` disabled + nhãn "sắp có".
- **Ngôn ngữ gốc**: `Tự động (auto)` + danh sách; **Ngôn ngữ đích**: `Tiếng Việt` + danh sách.
- **API key**: disabled khi provider không cần key.
- Nút **Lưu cài đặt** → ghi `settings.json` + xác nhận. `src`/`dst` áp dụng cho lần start kế tiếp (không dịch lại giữa phiên đang chạy).
- Mọi chuỗi mới vào bảng `I18nService` vi/en (quy ước i18n hiện có).

## 7. Error handling (nguyên tắc: pipeline không đứng vì dịch)

| Tình huống | Xử lý |
|---|---|
| Google HTTP fail/timeout (5 s) | `translated:null`, log stderr, không retry trong phiên |
| `translate.request_cache` timeout (2 s) | Coi hết là miss → dịch hết, bỏ qua cache |
| `translate.put` / App ghi DB fail | Log best-effort, display không ảnh hưởng |
| Sidecar chết / capture lỗi / lỗi nội bộ trong loop | `pipeline.state {running:false, reason}` + dừng loop có trật tự (giữ sidecar nếu vẫn sống — sidecar chết thì tất nhiên dừng hẳn) |
| Core process chết | `CoreProcessClient` exit event → badge lỗi trên UI |
| Provider/src/dst lạ ở `core.start` | `-32602` |

## 8. Perf gate (mục tiêu M4)

- Translate **nếuêm** đối với tick: không awaited trong chu kỳ; tối đa 4 song song.
- `frame.translated` @3 fps, payload vài KB ≪ budget UI 10–15 fps.
- Cache hit = 0 network call.
- Đo thật, ghi `Docs/performance.md` section **M4** (kỷ luật M2/M3: số thật, method + confounder disclosed; không tuyên bố đạt khi chưa đo).

## 9. Testing

Baseline hiện tại: **C# 55, Python 11**. Style: skip-êm bằng early return, không `Console.WriteLine` trong test.

- **Core:** `TranslateCycleSink` (fake `ISidecarRpc` + fake App transport: tick không block khi App chậm/dịch lỗi; emit đủ 2 nhánh before/after dịch; batch miss→put; `seq` tăng; empty boxes vẫn emit); `GoogleWebTranslator` parse fixture JSON qua fake `HttpMessageHandler` + timeout; `core.start/stop/list_sources` params validation (-32602/-32600); `NotifyAsync` không đan xen ghi; CLI `translate` one-shot (usage exit 2, output shape); regression `ocr loop` giữ nguyên.
- **App:** `JsonRpcClient` 3 nhánh (StringReader/Writer); `request_cache` query batch trên SQLite tempfile; upsert `translate.put`; `frame.translated` → `OverlayState`; `pipeline.state` → event.
- **UI:** bUnit — Settings page render (provider disabled đúng), Lưu ghi `settings.json`; Start/Stop gọi đúng `core.start/stop`.
- **Integration (skip-êm khi thiếu môi trường/offline):** serve qua stdio scripted — `core.start` → nhận `frame.translated` + `translate.request_cache` + reply; `translate` CLI gọi Google thật.
- **E2E manual:** App Start với cửa sổ thật → overlay/OBS hiện bản dịch.

## 10. Ngoài phạm vi M4

- Provider DeepL/LLM/local (chỉ mới để sẵn interface + slot UI disabled).
- Game hook (Rust), obs-websocket client (M5+).
- Dịch lại khi đổi ngôn ngữ giữa phiên đang chạy (chỉ áp dụng start kế tiếp).
- Retry/backoff cho Google; monitor FPS/quality UI; multi-monitor capture mới.

## 11. Rủi ro đã nhận thức

| Rủi ro | Xử lý |
|---|---|
| Google endpoint **không chính thức** — có thể đổi/đóng bất cứ lúc nào | Đúng cam kết đã chốt: chạy được ngay, khóa sau `ITranslator`; fail → hiển thị text gốc, không crash |
| Contract box của sidecar (pixel hay normalized) chưa verify | **Bước 0 của plan:** đọc `Spot.to_dict`/handler thật; pixel → Core normalize theo `w`/`h` |
| IPC 2 chiều (Core request App) — deadlock/đan xen ghi | 1 write-lock mỗi phía; test 3 nhánh ReadLoop; timeout ở mọi request |
| EF migration lần đầu + MAUI startup | `Migrate()` lúc start, test cover path DB chưa tồn tại |
