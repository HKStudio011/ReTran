# Thiết kế: Checklist kiểm tra LIVE cho màn Checking (setup wizard)

Ngày: 2026-11-15 · Trạng thái: chờ user duyệt
Nền: màn **1 / 1b** của loạt "tối ưu từng màn hình" (bảng visual-compare 2026-11-15). User đã chốt trong hội thoại:
hướng **A. Live** (checklist cập nhật từng dòng), và thêm 2 hạng mục: **đã tồn tại env (venv)?** + **có model sẵn không?**.

## 1. Mục tiêu

- Màn Checking hiển thị **danh sách hạng mục kiểm tra, cập nhật LIVE**: chờ `·` → đang kiểm tra `◌` → xong `✓` (hoặc `⚠` cảnh báo, `✗` lỗi).
- Danh sách **scroll khi quá dài**, chữ **tự xuống dòng**.
- Thêm 2 hạng mục **thông tin** (không phải cổng): env/venv đã tồn tại, model OCR đã có sẵn — thiếu thì không fail, chỉ ghi "sẽ cài/tải ở bước sau".
- Màn **1b gộp vào 1**: cảnh báo hạ GPU→CPU trở thành một dòng `⚠` trong list (bỏ khối paragraph riêng).

**Non-goals:** không đổi logic/kết quả check (gates, flavor, ngưỡng, host, timeout 60 s); không đổi màn Failed; chưa tách component dùng chung cho các màn sau (làm khi màn thứ 2 cần).

**Anti-goals:** checklist không được làm chậm check (thuần báo cáo tiến độ); không double-run; lỗi trong kênh báo cáo không được phá kết quả check.

## 2. Hiện trạng

`SystemCheck.Run` chạy đồng bộ trên thread nền, **chỉ trả kết quả cuối**; modal hiện 1 câu "Đang kiểm tra hệ thống…" + (đôi khi) khối notice — có thể đứng hình tới ~180 s khi 3 host mạng đều treo. Danh sách check hiện tại:

| # | Hạng mục | Cổng? | Chi tiết |
|---|----------|-------|---------|
| 1 | Tệp cài đặt (`uv.exe` có trong app dir) | **Fail cứng** nếu thiếu | `SetupPaths.UvExe` |
| 2 | Env/venv (python của venv tồn tại) | Không (chỉ cờ) | `SetupPaths.VenvPythonExe` — hiện chưa hiển thị |
| 3 | Model OCR có sẵn | Không — **MỚI thêm theo yêu cầu user** | `models/official_models` có mục nào |
| 4 | Kiến trúc OS x64 | Fail cứng | ARM64/x86 → thông báo riêng |
| 5 | Card đồ hoạ → flavor (GPU cu129 / CPU + notice) | Không (quyết định + notice) | heuristic NVIDIA rời + driver ≥ 576.02 |
| 6 | Đĩa trống (≥ 8 GB GPU / 2 GB CPU) | Fail cứng | ổ chứa thư mục app |
| 7 | Mạng ×3 host tuần tự (pypi.org → www.paddlepaddle.org.cn → github.com) | Fail cứng, dừng ngay tại host lỗi | probe HTTPS, 60 s/host |

## 3. Phương án (đã chốt trong hội thoại)

- **A. Live — CHỌN.** `SystemCheck` phát sự kiện tiến độ từng mục; coordinator giữ + publish; modal render. Tradeoff: refactor vừa (kênh progress + state mới), đổi lại feedback đúng lúc người dùng chờ lâu nhất (pha mạng).
- B. Tổng kết (hiện list sau khi xong) — loại: trong lúc chờ mạng (tới 180 s) vẫn là màn hình chết, không giải quyết vấn đề.

## 4. Thiết kế

### 4.1 SystemCheck — kênh tiến độ

- Thêm:
  ```csharp
  public enum SetupCheckState { Pending, Running, Ok, Warn, Fail }
  public sealed record SetupCheckItem(string Id, SetupCheckState State, SetupMessage Label, SetupMessage? Detail = null);
  ```
  (`SetupMessage` = key i18n + args, tái dùng hạ tầng vừa làm 2026-11-15.)
- `Run(..., Action<SetupCheckItem>? progress = null)` — tham số optional, call site cũ không đổi.
- Trình tự phát (đúng thứ tự thực thi, mỗi mục): `Pending` (phát một lượt cho toàn bộ đầu run để UI render đủ danh sách) → `Running` → `Ok|Warn|Fail`. Callback được bọc try/catch (lỗi báo cáo không phá check) và chạy trên chính thread của `Run`.
- Hai mục mới (thông tin):
  - `env`: từ cờ `venvPresent` sẵn có → detail "đã có" / "chưa có — sẽ cài ở bước sau".
  - `models`: seam mới `Func<string, int>? countModelEntries = null` (mặc định: đếm entry `Path.Combine(SetupPaths.ModelsDir, "official_models")`, 0 nếu thiếu) → detail "đã có sẵn ({0} mục)" / "chưa có — sẽ tải ở bước sau".
- IDs theo thứ tự hiển thị: `files`, `env`, `models`, `arch`, `gpu`, `disk`, `net` (3 item `net`, host nằm trong args).
- Kết quả `SystemCheckResult` **không đổi** — kênh tiến độ chỉ báo cáo.

### 4.2 Coordinator

- Thêm `public IReadOnlyList<SetupCheckItem> Checks { get; }` — reset đầu `RunCheckingStep`, cập nhật theo `Id` khi có event, `Publish()` mỗi lần.
- `Notice` giữ nguyên (còn dùng cho pause flavor-switch); màn Checking **không còn render paragraph Notice** — nội dung đã nằm ở item `gpu` (Warn) với cùng key `setup.notice.*`.

### 4.3 Modal (1 = 1b gộp)

```
 Cài đặt ReTran
 Đang kiểm tra hệ thống…
 ┌────────────────────────────────────────────┐   ← scroll khi dài (max-height ~ 260px)
 │ ✓  Tệp cài đặt (uv.exe)                    │
 │ ✓  Môi trường OCR (venv): đã có             │
 │ ✓  Model OCR: chưa có — sẽ tải ở bước sau  │
 │ ✓  Kiến trúc: Windows x64                  │
 │ ✓  Card đồ hoạ: RTX 2060 · driver 536.99   │
 │ ⚠  → driver quá cũ — sẽ dùng bản CPU       │
 │ ✓  Đĩa trống: 120 GB (cần 2 GB)            │
 │ ✓  Mạng: pypi.org                          │
 │ ◌  Mạng: www.paddlepaddle.org.cn…          │   ← đang kiểm tra
 │ ·  Mạng: github.com                        │   ← chờ
 └────────────────────────────────────────────┘
```

- Trạng thái: `·` Pending (mờ) · `◌` Running (xoay, CSS scoped) · `✓` Ok · `⚠` Warn (màu tally) · `✗` Fail (đỏ).
- **Màu chữ (sửa 2026-11-15 sau feedback mockup):** nhãn dùng `var(--surface)` — ĐÚNG cặp biến với vỏ modal (`bg-background`/`text-surface`) nên luôn tương phản trong cả 2 theme (kể cả trạng thái chưa set `data-theme` lúc wizard hiện sớm); chú thích dùng `var(--rt-dim)` (đậm hơn `--rt-faint` để dễ đọc ở 11px); mục Pending dùng `--rt-dim`, mark của Pending dùng `--rt-faint`.
- Style nằm trong `<style>` scoped của `SetupModal` — theo rationale sẵn có: modal không phụ thuộc rebuild Vite.
- Tự cuộn tới item đang chạy (nhỏ, làm nếu không vướng).
- Fail: item `✗` hiện thoáng rồi wizard chuyển màn **Failed như hiện tại** (không đổi luồng).

### 4.4 i18n (VI + EN, cân bằng key test sẵn có)

- Nhãn: `setup.check.files.label`, `.env.label`, `.models.label`, `.arch.label`, `.gpu.label`, `.disk.label`, `.net.label` ({0}=host).
- Detail: `setup.check.env.present/absent`, `.models.present` ({0}=số) `/absent`, `.arch.ok` ({0}), `.gpu.ok` ({0} tên, {1} driver, {2} GPU/CPU), `.gpu.cpu` (+ tái dùng `setup.notice.*`), `.disk.ok` ({0} trống, {1} cần), `.net.ok`, `.net.running` ({0}).
- Fail dùng lại các `setup.fail.*` sẵn có.

### 4.5 Files đụng tới

- `ReTran.App/Services/Setup/SystemCheck.cs` — enum/record + `progress` + 2 mục mới + seam `countModelEntries`.
- `ReTran.App/Services/Setup/SetupCoordinator.cs` — `Checks` + publish từng event.
- `ReTran.App/Components/Shared/SetupModal.razor` — list UI + scoped style; bỏ paragraph notice ở Checking.
- `ReTran.App/Services/I18nStrings.cs` — ~20 key × 2 ngôn ngữ.
- Tests: `SystemCheckTests.cs` (thứ tự event; env/models với seam giả; mạng dừng tại host lỗi đúng event), `SetupCoordinatorTests.cs` (`Checks` đầy đủ sau run; reset khi re-run).

### 4.6 Xác minh

- Focused + full suite như thường lệ.
- Regenerate board `visual-compare` (hạ tầng sẵn có) với 1/1b trước–sau để bạn so bằng mắt; smoke: chạy lại setup, rút mạng → `✗` đúng host; máy đã có env+model → các dòng "đã có".

## 5. Calls của controller (đề xuất — user chỉnh được khi duyệt)

1. Thứ tự mục = thứ tự thực thi: files → env → models → arch → gpu → disk → net.
2. env/models là **thông tin**, không phải cổng (máy mới chưa có gì vẫn qua bước này bình thường).
3. `models` đếm entry trong `models/official_models` (khớp định nghĩa `SetupState.NeedsSetup`).
4. Notice GPU→CPU gộp vào item, màn 1b nhập vào 1.
5. Style scoped trong modal (không cần rebuild Vite cho thay đổi này).
6. Chưa tách component list dùng chung — ghi chú tái dùng khi màn thứ 2 (vd DownloadingModels) cần.
