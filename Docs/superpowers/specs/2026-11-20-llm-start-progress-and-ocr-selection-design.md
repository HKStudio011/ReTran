# Settings/Setup reliability: nạp LLM khi Start, tiến trình thật và sửa đồng bộ toàn bộ lỗi đã review

Ngày: 2026-11-20
Trạng thái: chờ duyệt — **đã mở rộng theo yêu cầu gộp một lần**
Phạm vi: toàn bộ lỗi người dùng báo và các lỗi có bằng chứng từ review Settings/Setup. Thiết kế ban đầu chỉ gồm LLM Start và OCR Applying; phần §11 bổ sung các nhóm còn lại vào cùng một kế hoạch triển khai.

## 1. Mục đích và bằng chứng

### Local LLM

Hiện `PipelineController.StartAsync` gọi `core.start` và báo `IsRunning=true` ngay. `Program.LlmFactory` chỉ tạo `LlmTranslator` với endpoint resolver lười; `LlmTranslator.TranslateAsync` mới gọi `LlamaServerSlot.EnsureStartedAsync` ở lần dịch đầu. Vì vậy người dùng không biết server đã khởi động hay GGUF đã nạp xong chưa.

Phép đo thật với GGUF `Hy-MT2-1.8B-UD-Q4_K_XL` và runtime `b12546`:

- Khoảng 57,7 giây từ spawn tới `/health = 200`.
- WorkingSet đã đạt khoảng 1,57 GB sau khoảng 1,4 giây nhưng model vẫn nạp tiếp.
- Vì vậy cần loading UI và không được lấy WorkingSet làm phần trăm nạp model.

### OCR khi Applying

`SetupCoordinator.RunApplyingStepAsync` ghi `OcrDet/OcrRec` vào `settings.json` nhưng không gọi `ocr.models.set`. Khi `SettingsPanel` mount lại, `LoadOcrModelsAsync(syncFromActive: true)` lấy cặp `InUse` cũ từ sidecar ghi đè pick vừa lưu. Dữ liệu thật đã xác nhận `settings.json` là medium/medium trong khi UI có thể vẫn hiển thị small.

## 2. Mục tiêu

- Khi `provider=llm` và `mode=local`, mọi đường Start qua `PipelineController` đều nạp model trước khi mở capture/OCR pipeline.
- Có popup dùng chung cho Home và floating menu, hiện phase, tiến trình thật khi đo được, tốc độ/ETA khi đủ dữ liệu và nút Hủy thật.
- Không giới hạn tổng thời gian nạp khi process vẫn làm việc; chỉ fail khi crash, bị hủy hoặc không có hoạt động trong 10 phút.
- Chỉ báo pipeline `running` sau khi model healthy và `core.start` thành công.
- Applying phải đồng bộ cặp OCR đã chọn vào sidecar trước khi wizard báo hoàn tất.
- Google và LLM API giữ nguyên hành vi Start.

## 3. Không làm trong thay đổi này

- Không preload khi mở app hoặc khi chọn model trong Settings.
- Không giữ model sau `core.stop`; lifecycle giải phóng RAM/VRAM hiện tại không đổi.
- Không đổi tham số launch `-c 4096 -ngl 99 --jinja`, prompt, sampling hay catalog model.
- Không hỗ trợ thêm projector, drafter, LoRA hoặc bộ GGUF nhiều shard ngoài catalog hiện tại.
- Không kết luận hoặc ngăn tác động ngoài ReTran làm `llama-server.exe` b12556 biến mất; phía app chỉ kiểm tra artifact, báo trạng thái thật và repair rõ ràng.
- Không mô phỏng test nạp model hàng trăm GB khi không có artifact/phần cứng tương ứng.
- Không thay đổi hành vi Base URL `/v1`, vì finding này mới là judgement call và chưa có requirement xác nhận.
- Không mở rộng sang capture, overlay, tray hoặc update app; các thay đổi logging chỉ áp dụng ở file Settings/Setup/LLM được chạm.

## 4. Các hướng đã cân nhắc

### 4.1 Thởi điểm nạp model

**Đã chọn A — nạp ngay khi nhấn Bắt đầu.** Popup xuất hiện ngay; capture/OCR và trạng thái running chỉ bắt đầu sau health. Đúng nhu cầu và spec D7.

**Loại B — nạp lười ở lần dịch đầu.** Ít thay đổi hơn nhưng app vẫn “running mà không dịch” và popup xuất hiện bất ngờ giữa chừng.

**Loại preload khi mở app/chọn model.** Tiêu tốn RAM/VRAM trước khi người dùng thực sự dịch.

### 4.2 Timeout

**Đã chọn cơ chế Unsloth — 10 phút không hoạt động, gia hạn khi vẫn đang nạp.** Model lớn có thể nạp lâu hơn 10 phút.

**Loại timeout tổng 5 phút hoặc giữ 120 giây.** Có thể giết một load vài chục/vài trăm GB vẫn đang bình thường.

### 4.3 OCR Applying

**Đã chọn A — gọi `ocr.models.set` khi Applying.** Cặp engine, Settings và wizard thống nhất ngay; lỗi RPC phải làm Applying fail/retry, không báo hoàn tất giả.

**Loại B — chỉ ưu tiên `settings.json` trong Settings.** Engine vẫn chạy cặp cũ tới Start sau và phải thay semantics của dropdown hiện tại.

## 5. Thiết kế Core: lifecycle và RPC

### 5.1 RPC khởi động bất đồng bộ

`JsonRpcServer.RunAsync` hiện await từng handler trước khi đọc request tiếp theo. Vì vậy RPC nạp model không được giữ request cho tới healthy.

Bổ sung:

- `llm.server.start` với `{ "name": "<catalog-name>" }`:
  - Validate model/runtime bằng cùng đường resolve với `Program.LlmFactory`.
  - Tạo hoặc tái sử dụng đúng host trong `LlamaServerSlot`.
  - Bắt đầu health/load task ở nền, observe exception để không có unobserved fault.
  - Trả ngay `{ accepted, attempt, model }`.
  - Start lại cùng model khi đang loading là idempotent và trả attempt hiện tại; đã ready thì không spawn thêm process.
  - Đổi model phải dừng host cũ và làm attempt cũ bị supersede, không để hai server cùng tồn tại.
- `llm.server.stop`:
  - Hủy health wait và kill đúng process tree của host hiện tại.
  - Idempotent; trả trạng thái stopped sau cleanup.
  - Attempt đang chờ phải thức dậy và kết thúc nhanh, không chờ hết timeout.
- Mở rộng `llm.server.status`:
  - Giữ `state/model/error` để tương thích.
  - Thêm `attempt`, `phase`, `bytesLoaded`, `bytesTotal`, `bytesPerSec`, `progressAvailable`.
  - Các field số liệu nullable khi không đo được; không trả số giả.

`Program.LlmFactory` và RPC start dùng chung một helper resolve/get-or-create để tránh hai đường validation lệch nhau. Sau prewarm, translator tiếp tục dùng host đã ready; `EnsureStartedAsync` idempotent.

### 5.2 Hủy và thoát process

`LlamaServerHost` có một cancellation liên kết với lifetime của chính host:

- `Dispose` cancel health wait trước khi kill process tree.
- Health loop phát hiện process exit sớm và trả lỗi kèm stderr tail thay vì poll tới timeout.
- Cancel/Dispose không được bị ghi nhận là startup error mới.
- `LlamaServerSlot` chỉ cập nhật state nếu attempt/host vẫn là hiện tại; callback của attempt cũ không được ghi đè attempt mới.
- `core.stop` và ProcessExit tiếp tục dùng cùng cleanup idempotent.

## 6. Tiến trình và stall timeout theo Unsloth

### 6.1 Số liệu hiển thị

- `bytesTotal`: kích thước file GGUF active trong catalog.
- `bytesLoaded`: IO byte đọc tích lũy của `llama-server`, giữ high-water mark theo process.
- Trên Windows đọc counter bằng Win32 (`GetProcessIoCounters` hoặc API tương đương), không thêm package.
- Percent chỉ có khi `bytesTotal > 0` và counter khả dụng; clamp trong khoảng 0–99% cho tới khi healthy.
- Khi healthy: `phase=ready`, percent 100% độc lập với RAM hoặc IO cuối.
- `bytesPerSec`: delta IO/delta thởi gian; ETA chỉ hiện khi tốc độ dương và còn byte chưa đọc. Không bù số hoặc giả tốc độ khi thiếu dữ liệu.
- Counter bao gồm IO khác của process nên là giá trị xấp xỉ, không phải byte weights tuyệt đối.

Nếu counter không khả dụng: `progressAvailable=false`, popup dùng thanh vô định và phase text. Không dùng WorkingSet làm percent.

### 6.2 Stall detector

Các hằng số theo Unsloth:

- Stall window: **600 giây**.
- Sample hoạt động: mỗi **5 giây**.
- IO read tăng tối thiểu **1 MiB**; hoặc RAM tăng ít nhất **1 MiB** so với đỉnh trước; hoặc page faults tăng ít nhất **64**; hoặc CPU trung bình ít nhất **5% một core** trong sample.
- RAM dùng high-water mark để trang bị giải phóng không bị tính sai.
- Mỗi sample có hoạt động đặt deadline mới bằng `now + 600 giây`.
- Chỉ đọc process `llama-server`; không quét toàn bộ process tree vì ReTran không cần diffusion child tree.
- Counter không đọc được: fallback một deadline cố định 600 giây từ lúc bắt đầu health wait.
- Crash và Hủy luôn thắng stall window.
- Stall khiến start fail với lỗi rõ “không có hoạt động trong 10 phút”, dọn process, không mở pipeline.

Trade-off đã chấp nhận: process liên tục có hoạt động nhưng swapping vô hạn sẽ được chờ tới khi người dùng Hủy.

Page-fault counter trên Windows dùng Win32 process-memory API phù hợp; mọi lỗi đọc counter đều chuyển về fallback, không làm crash health loop.

## 7. Thiết kế App: một Start flow và popup dùng chung

### 7.1 `PipelineController`

Khi local LLM:

1. Start Core và wire handlers nếu cần.
2. Đồng bộ `llm.models.set` từ Settings như hiện tại.
3. Gọi `llm.server.start`, nhận attempt.
4. Publish trạng thái startup và poll `llm.server.status` theo attempt (mặc định 500 ms; RPC status phải nhẹ).
5. Khi `ready`, gọi `core.start`; chỉ sau khi RPC này thành công mới đặt `IsRunning=true`, `ActiveSource` và bắn `StateChanged`.
6. Nếu attempt bị supersede, status error hoặc RPC lỗi trước `core.start`: không gọi `core.start`, không đặt running và giữ lỗi để UI hiển thị.
7. Hủy vẫn hoạt động sau ready và trong lúc chờ `core.start`:
   - Chưa gửi `core.start`: gọi `llm.server.stop`, chờ cleanup rồi kết thúc bằng `OperationCanceledException`.
   - Đã gửi `core.start`: popup giữ “Đang hủy…”; khi RPC tuần tự này kết thúc, rollback bằng `core.stop` để dọn cả session có thể vừa mở và llama-server. Không đặt `IsRunning=true` và không chuyển hủy thành lỗi Start.
8. Nếu `core.start` thất bại sau khi LLM đã healthy: rollback session bằng `core.stop` khi cần và dừng llama-server. Không giữ RAM/VRAM sau Start lỗi; lần Start sau nạp lại.

`PipelineController` giữ một startup attempt tại một thởi điểm và expose một snapshot/event chuyên cho LLM startup. Caller đầu sở hữu attempt và `SourceSelection`. Một Start thứ hai trong lúc này bị từ chối ngay bằng lỗi “pipeline đang khởi động”; không no-op, không chờ vô hạn, không đổi source và không spawn attempt thứ hai. Việc hiển thị popup không phụ thuộc Home: Start từ floating menu cũng đi qua cùng controller.

Google/API bỏ qua toàn bộ bước prewarm/poll và giữ flow cũ.

### 7.2 Popup ở `MainLayout`

Thêm component popup ngang cấp `SetupModal`/`UpdateModal`, subscribe snapshot/event của `PipelineController`:

- Hiện model, phase, percent hoặc thanh vô định, `loaded/total`, tốc độ và ETA khi có.
- `loading`: đang spawn/nạp model.
- `finalizing`: dữ liệu đọc gần đủ nhưng chưa healthy; giữ tối đa 99%.
- `ready`: 100% rồi hiển thị “đang khởi động pipeline” cho tới khi `core.start` hoàn tất và popup đóng.
- `error`: hiện lỗi và nút Đóng; người dùng có thể Start lại, không auto-retry.
- Nút **Hủy**: chuyển sang trạng thái đang hủy, chặn double-click, gọi cancel của controller; popup chỉ đóng sau cleanup.
- Home xử lý `OperationCanceledException` như hủy bình thường, không ghi `_pipelineError`; floating menu đã có semantics này.
- Popup có i18n VI/EN, `role=dialog`, `aria-modal`, nhãn tiến trình và keyboard focus phù hợp. Không đóng bằng click backdrop vì đó là hành động hủy có side effect.

Không cần thay `IPipelineControl`: concrete `PipelineController` đã được MainLayout inject; floating menu vẫn gọi interface như cũ nhưng được hưởng startup orchestration bên trong implementation.

## 8. OCR Applying: đồng bộ thật trước khi hoàn tất

Trong `SetupCoordinator.RunApplyingStepAsync`:

1. Tính cặp `ExpectedPair` như hiện tại.
2. Ghi `settings.json` với cặp đã chọn (giữ thứ tự hiện tại để lỗi ghi file không làm engine đổi trước).
3. Gọi `ocr.models.set` với chính `det/rec` đó.
4. Chỉ khi cả hai thành công mới `MarkStepDone(Applying)` và cho wizard hoàn tất.

Quy ước lỗi:

- RPC lỗi đi vào cơ chế retry hiện có; sau hết retry Applying chuyển `Failed`, không báo `Done`.
- Nếu ghi settings thành công nhưng RPC fail, settings vẫn giữ lựa chọn mới và sidecar vẫn cặp cũ; đây là trạng thái lỗi được hiển thị, không phải success. Retry gọi lại `ocr.models.set` idempotent để đồng bộ.
- `ocr.models.set` thành công nhưng thao tác sau lỗi cũng được retry idempotent.
- `llm.models.set` best-effort hiện tại nằm ngoài phạm vi và không bị đổi.

Settings giữ `syncFromActive:true` vì sau Applying thành công sidecar đã thật sự dùng medium. Deferred pick và refresh thường giữ semantics C8.

## 9. Test bắt buộc

### Core

- `llm.server.start` trả accepted trước khi health xong; trong lúc start task đang chờ, server vẫn trả lời status — test này chặn lỗi RPC tuần tự làm popup đứng.
- Ready không spawn lần hai; cùng model idempotent; đổi model supersede attempt cũ.
- Cancel/Dispose đánh thức health wait nhanh, kill đúng process, không ghi error giả; ProcessExit/core.stop vẫn cleanup.
- Process exit sớm trả lỗi kèm stderr tail, không chờ 600 giây.
- Từng tín hiệu IO/RAM/page-fault/CPU gia hạn deadline; không hoạt động mới stall; unreadable counter dùng fixed 600 giây. Test dùng fake clock/counters, không ngủ 10 phút.
- Percent monotonic, clamp 99% trước health, 100% khi ready; thiếu counter trả `progressAvailable=false`.
- Prewarm thành công thì translator dùng lại host, không tạo process thứ hai.

### App

- Local Start theo đúng thứ tự `models.set → server.start → ready → core.start → IsRunning`.
- Lỗi/cancel/supersede trước `core.start` không gọi `core.start`, không đặt running và không hiện lỗi hủy giả ở Home.
- Hủy sau khi đã gửi `core.start` rollback bằng `core.stop`; `core.start` lỗi sau healthy dừng llama-server, không giữ RAM/VRAM.
- Google/API không gọi server start và không mở popup.
- Snapshot/event xuất hiện khi Start được gọi từ interface/floating menu, không chỉ Home.
- Start thứ hai bị từ chối rõ ràng, không tạo server thứ hai và không đổi source; callback attempt cũ không đóng popup attempt mới.

### OCR

- Applying ghi đúng settings và gọi `ocr.models.set` với đúng medium det/rec trước khi Done.
- RPC fail làm Applying Failed sau retry, không complete; retry gọi lại idempotent.
- Sau completion, catalog active là medium nên lần Settings mount/sync tiếp theo giữ medium.

### Kiểm thử thật

- Build solution và full C# suite.
- Với runtime/model hợp lệ: đo lại cold/warm load bằng binary thật; kiểm tra popup từ Home và floating menu, cancel giữa load, crash/health error và Stop sau ready.
- Chạy lại Setup với medium, xác nhận `settings.json`, `ocr.models.list.InUse` và Settings sau remount đều medium.
- Không ghi nhận test model hàng trăm GB nếu chưa chạy thật; cơ chế load dài được kiểm bằng fake clock/counters và, nếu có thể, load chậm có kiểm soát.

## 10. Quyết định để builder tự chọn trong phạm vi đã chốt

- Tên/type cụ thể của snapshot App và helper Core, vị trí file private interop Windows.
- Cách tách fake clock/counter tối thiểu để test stall mà không làm thay đổi public surface ngoài RPC đã nêu.
- EMA hay trung bình trượt cho `bytesPerSec`; không được tạo ETA khi thiếu mẫu tin cậy.
- CSS cụ thể theo modal hiện có, miễn giữ accessibility và không phá layout Settings/Setup.
- Interval poll App trong khoảng 250–500 ms nếu đo overhead cho thấy cần giảm; stall sample Core giữ 5 giây.

## 11. Các task mở rộng trong cùng một đợt

Toàn bộ lỗi được gộp thành một lần triển khai theo yêu cầu đã chốt. Các task dưới đây có commit/test riêng để giới hạn blast radius nhưng cùng nằm trong một implementation plan và một lần full verification cuối.

### Task 1 — LLM Start, popup, cancel và stall detector

Thực hiện đúng §5–§7. Đây là task độc lập lớn nhất; không được đổi launch args hoặc preload ngoài Start.

### Task 2 — OCR Applying, setup-state integrity và model deletion

#### 2.1 Rerun giữ lựa chọn

`StartAsync(force:true)` từ Settings chuyển thành **re-verify/repair mà không xoá answers**:

- Giữ `Dets/Recs/Langs/LlmModels` và các active field hợp lệ.
- Xoá step markers, đặt `IsComplete=false` và chạy lại verification/download/apply.
- Không tự hỏi lại cả ba màn khi answers còn hợp lệ; màn bị thiếu dữ liệu vẫn được hỏi theo state machine.
- Cập nhật comment/doc cũ đang mô tả force là xoá toàn bộ answers.

#### 2.2 Kiểm tra hoàn tất và artifact theo lựa chọn

`NormalizeCompletion` chỉ giữ `IsComplete=true` khi:

- `Dets`, `Recs`, `Langs`, `LlmModels` đều đã được khởi tạo.
- Có ít nhất một det/rec và `ActiveDet`/`ActiveRec` thuộc danh sách tương ứng.
- `ActiveLang` chỉ được khác null khi nằm trong `Langs`; nếu `Langs` rỗng thì `ActiveLang=null`.
- LLM được phép rỗng; nếu có `ActiveLlm` thì phải nằm trong `LlmModels`.

`NeedsSetup`/đường `ArtifactsPresent` phải kiểm tra artifact của **các model đã chọn**, không chỉ “còn bất kỳ model nào”:

- Venv còn Python.
- Mọi OCR model cần theo state có thư mục hợp lệ và không có `.partial`.
- Mọi GGUF đã chọn có file hoàn chỉnh, không `.partial`.
- Nếu state chọn LLM local, runtime active phải có `llama-server.exe`.
- Đường cheap preflight vẫn chỉ đọc file/JSON, không spawn Core/Python và không probe mạng.

Thiếu artifact làm resume repair tải bù đúng phần thiếu; không tải lại artifact hợp lệ.

#### 2.3 Xoá model là một thay đổi lựa chọn

Sau khi RPC xoá model OCR/GGUF thành công từ Settings, phải đồng bộ **cả `setup.json`, `settings.json` và pick đang hiển thị**:

- Loại model khỏi mọi danh sách chọn tương ứng trong `setup.json` (`Dets`, `Recs`, `Langs`, `LlmModels`).
- Nếu `OcrDet`, `OcrRec`, `LlmActiveModel` hoặc pick trên UI trỏ vào model đã xoá, chọn model thay thế cùng slot còn **được chọn và còn installed**; ưu tiên cặp engine active còn hợp lệ, sau đó candidate đầu tiên còn hợp lệ.
- Nếu không còn candidate, clear pick/tên model đã xoá trong Settings và đặt `IsComplete=false` để Setup bắt buộc chọn lại; không giữ hoặc tự fallback sang tên model đã xoá.
- Đánh dấu Setup chưa hoàn tất trước khi persist các thay đổi để chặn Start trong lúc đồng bộ. `setup.json` và `settings.json` không thể atomic với nhau: nếu bất kỳ bước lưu nào lỗi, xử lý **fail-closed** — giữ `IsComplete=false`, hiện rõ file nào chưa cập nhật và yêu cầu repair; không cho Start/Applying tiếp tục dùng tên model đã xoá.
- Chỉ refresh catalog/UI sau khi state đã nhất quán hoặc đã chuyển fail-closed. Model đang active theo engine vẫn bị chặn xoá như hiện tại.
- Nhờ đó repair không tải lại model vừa bị loại khỏi lựa chọn, và Setup không lấy answer cũ để báo installed sai.

### Task 3 — Setup fault, catalog rỗng và fail-fast download

#### 3.1 Không treo câm khi task fault

Bọc đường `SetupCoordinator.StartAsync` bằng xử lý lỗi có quan sát:

- `OperationCanceledException` giữ semantics hủy.
- Exception khác được log, chuyển coordinator sang `Failed` với thông điệp có thể hiển thị và publish trước khi `EndRun`.
- Không để exception ra ngoài task fire-and-forget mà không có trạng thái UI.
- Đường rerun phải mở lại nút sau publish cuối; đường first-run hiện màn lỗi/retry thay vì Checking vĩnh viễn.

#### 3.2 Catalog LLM rỗng

Tách `loading`, `loaded-empty` và `loadFailed` trong `SetupModal`:

- Chỉ hiện spinner khi request thật sự đang chạy.
- RPC thành công với list rỗng hiện “Không có model LLM khả dụng” và vẫn cho Tiếp tục với lựa chọn rỗng.
- RPC lỗi giữ Retry và cũng cho bỏ qua bước optional theo spec.
- Không tự chuyển sang bước kế khi catalog chưa tải xong; chỉ cho phép bỏ qua bằng thao tác rõ ràng của user.

#### 3.3 Model download fail-fast

Trong vòng poll của `RunDownloadModelsStepAsync`:

- Với mỗi model cần tải, nếu catalog có `downloadError` và không còn `downloading`, fail attempt ngay với chi tiết lỗi.
- Không chờ 60 phút cho một background download đã kết thúc lỗi. **Mọi** `downloadError` đều kết thúc attempt hiện tại ngay, rồi dùng cơ chế retry tối đa bốn attempts hiện có; sau attempt cuối mới chuyển `Failed`.
- Không phân loại terminal/transient và không thêm error code mới ở Core/Python trong đợt này.
- Progress của model khác vẫn được giữ trong snapshot cho tới khi attempt kết thúc.

### Task 4 — Runtime version, artifact và cancellation

#### 4.1 Không báo timeout/cancel là success

`LlamaRuntimeManager` phân biệt ba kết quả của mỗi attempt: succeeded, failed và cancelled.

- Mọi đường kết thúc không commit thành công đều không được phép poll suy ra `Completed` từ binary cũ.
- `TaskCanceledException` từ `HttpClient.Timeout` được ghi nhận là attempt failed kèm lỗi, không bị nuốt như hủy chủ động.
- Hủy chủ động được nhận diện bằng token của caller và có trạng thái riêng; App không hiện toast success.
- `LlmRuntimePoll` chỉ trả `Completed` cho đúng attempt đã commit thành công; trạng thái installed nền vẫn dùng được cho refresh bình thường.

#### 4.2 Artifact và cùng phiên bản

- `CommitStagedDirectory` chỉ ghi `runtime.json` sau khi xác nhận `llama-server.exe` tồn tại trong thư mục final.
- Thiếu binary làm commit fail và rollback thư mục cũ khi có; không lưu tag của một installation không dùng được.
- Giữ quy tắc đã có test: `current == latest` và artifact hợp lệ thì Setup bỏ qua download.
- Nếu artifact thiếu, Setup gọi đây là **repair cùng tag**, không ghi nhận là update sang version mới.
- Log `flavor`, `current`, `latest`, `installed` để lần sau truy được vì sao Setup quyết định tải.

### Task 5 — Settings write race và flavor race

#### 5.1 Ghi settings nguyên tử theo nhóm read-modify-write

- `AppSettingsService` serialize Load/Save theo full path trong process và dùng temp file duy nhất cho mỗi lần ghi, dọn temp khi lỗi.
- Thêm `Update(Func<AppSettings,AppSettings>)` giữ cùng lock cho read-modify-write.
- Chuyển mọi caller Settings/Setup/LLM/OCR đang `Load() with … → Save()` sang `Update`; không để một writer ghi đè field của writer khác.
- Test đồng thởi phải chứng minh không có `IOException` do dùng chung temp và không mất field của lượt khác.

#### 5.2 Runtime flavor

- Disable flavor selector khi runtime action đang busy.
- Mỗi check/download/poll capture `flavor` cục bộ và attempt ngay từ đầu; không đọc `_llmFlavor` thay đổi sau từng await.
- Thêm revision/generation cho async status load: response của flavor cũ không được ghi đè snapshot flavor mới.
- Toast/check sau success dùng đúng flavor của attempt.

### Task 6 — Tài liệu, logging và verification tổng

- Cập nhật comment lỗi thởi về force rerun và Syncing luôn frozen-sync.
- Trong các file đã chạm, thay `Console.Error` bằng cơ chế logger phù hợp với architecture hiện có; không mở refactor DI toàn Core chỉ vì mục này.
- Cập nhật i18n VI/EN cho mọi phase/error/repair mới.
- `dotnet build "ReTran Project.slnx" -c Release`.
- `npm run build` trong `ReTran.App/vite-project`.
- Full C# suite và Python sidecar suite nếu có thay đổi Python.
- **Bắt buộc chạy UI/binary thật khi môi trường có artifact hợp lệ:** local 1.8B cold/warm load; popup từ Home và floating menu; cancel giữa load; Google/API không preload; Setup chọn medium và Settings sau remount. Runtime/model hiện hỏng phải được ghi là blocker của từng mục, không thay bằng suy đoán từ code.
- **Được dùng fixture/fake có kiểm soát:** stall 10 phút, model hàng trăm GB, IO/page-fault/CPU samples, HTTP timeout/outdated, attempt supersede, catalog rỗng, `downloadError`, process crash và concurrent writes. Dùng local fake handler/server/clock, không phụ thuộc mạng thật và không ngủ theo thởi gian thật.
- **Model deletion:** chạy integration với catalog/artifact tạm, không xoá model thật trên máy. Nếu có model phụ không active thì smoke thêm trên UI; thiếu model phụ là blocker của riêng mục manual này, không làm mất tính hợp lệ của integration test.
- Runtime outdated/timeout và Start supersede không bắt buộc tải release thật hoặc chờ timeout thật; chúng phải có automation tương ứng. Nếu một mục manual bị chặn môi trường, implementation có thể hoàn thành code/test nhưng báo cáo phải ghi **chưa kiểm chứng mục đó**, không tuyên bố toàn bộ đã được xác minh.
- Chạy lại OpenCodeReview nếu endpoint đã được cấu hình; nếu chưa, ghi rõ blocker và review diff thủ công, không tuyên bố OCR đã chạy thành công.

### Thứ tự phụ thuộc và tiêu chí hoàn thành

Có thể làm Task 1, Task 2 và Task 4 độc lập. Task 3 dùng state semantics đã chốt ở Task 2; Task 5 bảo vệ mọi writer mà các task trước tạo ra. Task 6 chạy sau cùng. Mỗi task phải có test fail trước khi sửa theo TDD, commit riêng và không thay đổi test để che hành vi sai. Không task nào được coi là xong nếu chỉ có code-reading; các hành vi load/run được hỏi ở đây phải có thực nghiệm hoặc test tương tác thật phù hợp.
