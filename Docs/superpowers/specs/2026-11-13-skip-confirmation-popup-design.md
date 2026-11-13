# Thiết kế: Popup xác nhận khi bấm "Bỏ qua" trên màn hình lỗi setup

Ngày: 2026-11-13 · Trạng thái: đã duyệt (user chốt 2026-11-13)
Quan hệ: **sửa đổi (amendment)** spec `2026-11-03-first-run-setup-wizard-design.md` — mục 2 (non-goal
"bỏ qua setup = đóng app") và mục 3.6 (nút "Bỏ qua" chỉ có một hành vi duy nhất).

## 1. Bối cảnh & ý định

User test thật E2E (2026-11-13): bấm "Bỏ qua" trên màn hình lỗi setup → app đóng sạch (đúng spec
§3.6 hiện hành) — nhưng **gây bất ngờ**: user muốn có thêm lựa chọn vẫn vào dùng app thay vì chỉ một
kết cục đóng app. Giữ nguyên 2 kết cục cũ, thêm lựa chọn thứ ba qua một popup hỏi rõ.

## 2. Hành vi đã chốt với user

1. Trên màn hình lỗi (Failed), bấm **"Bỏ qua"** → hiện **popup trong wizard** (KHÔNG dùng MessageBox
   hệ điều hành — giữ ngôn ngữ thiết kế của app):
   - Câu hỏi: **"Bạn muốn thoát ReTran hay bỏ qua phần thiết lập?"**
   - 3 nút theo thứ tự: **[Thoát ReTran]** · **[Bỏ qua thiết lập]** · **[Quay lại]**.
2. **[Thoát ReTran]** = hành vi Skip hiện nay: `Skip()` → `closeApp()` (đóng sạch, đã verify E2E).
   Không ghi state — lần sau mở app wizard hiện lại, resume từ bước đã lưu.
3. **[Bỏ qua thiết lập]** = đóng wizard cho phiên này + **mở khóa nút Start cho phiên này** (nếu chỉ
   đóng wizard mà vẫn khóa gate thì app không dùng được gì — trái ý "vào dùng app"):
   - Cờ **`SkippedForSession`** trong `SetupCoordinator` — chỉ trong bộ nhớ, KHÔNG ghi `setup.json`.
   - Setup vẫn `isComplete=false` ⇒ lần mở app sau wizard **hiện lại** (user chốt (b): nhắc lại, ít
     trạng thái mới).
   - Thiếu sidecar/model (nếu có) hiện ra ở lúc Start như mọi lỗi Start khác — đúng như user chốt (a):
     "skin in" là user tự chịu lỗi Start.
4. **[Quay lại]** = đóng popup, về màn hình lỗi như cũ.
5. Đường quay lại setup trong cùng phiên: **Cài đặt → "Chạy lại setup"** (mở rộng điều kiện hiển thị —
   mục 3.4).
6. Popup full i18n Vi/En.

## 3. Thay đổi kỹ thuật

### 3.1 `SetupCoordinator` (`ReTran.App/Services/Setup/SetupCoordinator.cs`)

- Thêm `public bool SkippedForSession { get; private set; }` — chỉ đọc, không persist.
- Thêm `public void SkipForSession()` — set cờ + `Publish()`; không ghi state, không đóng app.
- `Skip()` **giữ nguyên** hành vi (closeApp) — [Thoát ReTran] dùng nó; cập nhật doc comment (không còn
  là hành vi mặc định của nút "Bỏ qua" mà là lựa chọn "Thoát ReTran" của popup).
- `StartAsync(...)` xóa cờ khi bắt đầu — đường "Chạy lại setup" (`force: true`) phải hiện lại modal;
  vòng đời cờ = phiên hiện tại, hoặc tới khi user chủ động chạy lại setup.

### 3.2 `PipelineGate` (`ReTran.App/Services/Setup/PipelineGate.cs`)

- Chữ ký mới: `CanStartPipeline(bool setupComplete, bool setupRunning, bool skippedForSession)` =
  `(setupComplete || skippedForSession) && !setupRunning` — cờ skip thay thế completion cho phiên này,
  nhưng "đang chạy một lượt" vẫn khóa (cửa sổ race của rerun giữ nguyên).
- Call site duy nhất trong app: `Home.razor` (~:480) truyền thêm `Setup.SkippedForSession`.

### 3.3 `SetupModal.razor`

- Điều kiện render modal: `_wizardActive && !Coordinator.IsComplete && !Coordinator.SkippedForSession`.
- Khai báo `_showSkipConfirm`; nút "Bỏ qua" ở Failed **mở popup** thay vì gọi Skip ngay.
- Popup: overlay **trong wizard** phủ trên card modal (cùng ngôn ngữ visual: nền `bg-background`,
  viền, shadow, `role="dialog"`); câu hỏi + 3 nút đúng thứ tự, classes `rt-btn` sẵn có.
- Handlers (giữ pattern `_busy` như các nút sibling):
  - [Thoát ReTran] → `Coordinator.Skip()`;
  - [Bỏ qua thiết lập] → `Coordinator.SkipForSession()` (modal tự ẩn qua điều kiện render);
  - [Quay lại] → `_showSkipConfirm = false`.
- Không cần xử lý ESC/click-nền (các modal hiện có cũng không — ngoài phạm vi).

### 3.4 `SettingsPanel.razor` — đường quay lại

- Section "Chạy lại setup" hiển thị khi `Setup.IsComplete || Setup.SkippedForSession` (hiện chỉ
  `IsComplete`) — thiếu vế sau thì user "Bỏ qua thiết lập" không còn đường quay lại setup trong phiên.
- `OnSetupChanged`: reset `_rerunStarted` khi **`!Setup.IsRunning`** (thay vì chỉ khi `IsComplete`) —
  vòng lặp rerun→fail→"Bỏ qua thiết lập"→rerun phải bấm được nút lần nữa; guard chỉ có ý nghĩa khi
  một lượt đang chạy thật.

### 3.5 i18n keys (`I18nStrings.cs`, cả vi + en)

| key                        | vi                                                | en                                             |
| -------------------------- | ------------------------------------------------- | ---------------------------------------------- |
| `setup.skipConfirm.message` | Bạn muốn thoát ReTran hay bỏ qua phần thiết lập? | Do you want to exit ReTran or skip the setup?  |
| `setup.skipConfirm.exit`    | Thoát ReTran                                      | Exit ReTran                                    |
| `setup.skipConfirm.skip`    | Bỏ qua thiết lập                                  | Skip setup                                     |
| `setup.skipConfirm.back`    | Quay lại                                          | Back                                           |

## 4. Chiến lược test

- `SetupWiringTests.CanStartPipeline_*`: bảng tổ hợp mới (complete, running, skipped) — tối thiểu 6 dòng:
  `(false,false,true)→true` **[điểm cốt lõi của feature]**, `(true,false,false)→true` [cũ],
  `(false,false,false)→false` [cũ], `(true,true,false)→false` / `(false,true,true)→false` [running luôn khóa],
  `(false,true,false)→false` [cũ].
- `SetupCoordinatorTests` (dùng hạ tầng fake có sẵn trong file):
  - `SkipForSession` set cờ + publish + **KHÔNG** gọi closeApp;
  - `Skip` vẫn gọi closeApp (pin hành vi Thoát ReTran);
  - `StartAsync` (kể cả `force: false`) xóa cờ.
- `I18nServiceTests` guard "mọi key có đủ 2 ngôn ngữ" tự bắt 4 key mới (không cần test riêng).
- Repo không có bUnit → luồng 3 nút verify bằng smoke thật (checklist §6) + code review.

## 5. Không thuộc phạm vi

- Không persist cờ skip (chủ ý — nhắc lại lần sau; đúng lựa chọn (b) của user).
- Không thêm title/hint khác trong popup (đúng text user đã chốt).
- Không đổi hành vi Skip ở các đường khác (chỉ màn Failed mới có nút này).

## 6. Checklist smoke (máy thật, sau merge)

1. Dựng wizard fail → bấm "Bỏ qua" → popup hiện đúng câu hỏi + 3 nút (vi); đổi ngôn ngữ En → đúng bản En.
2. [Quay lại] → về màn lỗi; bấm "Bỏ qua" lại → popup hiện lại.
3. [Bỏ qua thiết lập] → wizard biến mất; bấm Start → gate cho qua (nếu thiếu sidecar thì lỗi Start hiện
   như thường).
4. Cài đặt → thấy "Chạy lại setup" → bấm → wizard hiện lại và chạy.
5. [Thoát ReTran] → app đóng sạch (như đã verify trước đây).
6. Mở lại app sau khi "Bỏ qua thiết lập" → wizard hiện lại (state dở).
