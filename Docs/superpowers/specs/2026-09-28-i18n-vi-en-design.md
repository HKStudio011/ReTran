# Spec: i18n tiếng Việt / Anh cho app điều khiển ReTran

- **Ngày:** 2026-09-28
- **Trạng thái:** approved (brainstorming với user, 4 câu hỏi + 3 phần design)
- **Tiền đề:** UI Layout DMFT hoàn tất (`64800e7..ad96321`); UI hiện dùng chuỗi kép gộp `VI / EN` cứng trong Razor/XAML.

## 1. Bối cảnh & mục tiêu

App điều khiển (Home, NavMenu, NotFound, header MainPage) đang hiển thị chuỗi kép gộp
`"Bắt đầu Demo / Start Demo"` ngay trong markup. Mục tiêu: cho phép user chọn **một** ngôn ngữ
(tiếng Việt hoặc tiếng Anh), mọi chữ hiển thị đi theo lựa chọn đó, lưu lại giữa các lần mở app.

## 2. Quyết định của user (đã chốt)

| # | Câu hỏi | Quyết định |
|---|---------|-----------|
| 1 | Hành vi | **Switcher 1 ngôn ngữ** — mỗi chuỗi chỉ hiển thị 1 thứ tiếng; bỏ chuỗi kép gộp; lưu `localStorage` (cùng cơ chế theme). |
| 2 | Phạm vi | **UI app điều khiển**: `Home.razor`, `NavMenu.razor`, `NotFound.razor`, `MainPage.xaml` (header native). Overlay OBS, log, JSON-RPC message → **không** trong scope. |
| 3 | Mặc định | **Auto theo OS**: `CultureInfo.CurrentUICulture` bắt đầu bằng `vi` → `vi`, ngược lại `en`; switcher đổi tay ghi đè `localStorage["retran.lang"]`. |
| 4 | Vị trí switcher | **Trong card "Giao diện / Theme"** hiện có. |
| 5 | Cơ chế | **A: `I18nService` custom** (dictionary C# trong service DI, 0 package mới) — đã so sánh với B (.resx + `IStringLocalizer`) và C (JSON nhúng). |

## 3. Kiến trúc — `I18nService`

File mới `ReTran.App/Services/I18nService.cs`, singleton đăng ký trong `MauiProgram`:

- Hai dictionary `vi` / `en` (string→string), định nghĩa static (cùng file hoặc
  `I18nStrings.cs` bên cạnh). Key dạng dotted: `home.card.capture`, `home.btn.startDemo`,
  `nav.control`, `notfound.title`, `core.startCore`, `core.status.stopped`…
- API:
  - `this[string key]` → chuỗi theo `Lang` hiện tại.
  - `string Fmt(string key, params object?[] args)` → `string.Format` với culture invariant
    cho chuỗi nội suy (vd `"Demo: đang chạy — nguồn: {0}."`).
  - `string Lang { get; }` — `"vi"` hoặc `"en"`.
  - `void Set(string lang)` — đổi + phát event `Changed`; ghi `localStorage["retran.lang"]`.
  - `event Action? Changed`.
- Fallback: key không tồn tại → trả **chính key** (dễ nhìn thấy khi dev) + log warning 1 lần;
  `Lang` không có trong dictionary → dùng `en`.
- Detect lúc khởi động: đọc `localStorage["retran.lang"]` qua `JsInteropService` (đã có pattern
  từ theme); đọc lỗi / chưa có → `CultureInfo.CurrentUICulture.Name.StartsWith("vi",
  OrdinalIgnoreCase)` ? `vi` : `en`.
- Re-render: component Razor `@inject I18nService L` + `@implements IDisposable` subscribe
  `Changed` → `StateHasChanged` (giống pattern theme hiện tại). `MainPage.xaml.cs` (native,
  nằm ngoài BlazorWebView) cũng subscribe để set lại `Text` của 3 nút + status label.

**Không đổi:** doc-comment song ngữ trong code (quy ước `AGENTS.md`) — chỉ chữ **hiển thị cho
user** đi qua `I18nService`.

## 4. Phạm vi chuỗi

| Bề mặt | Dịch (có key) | Giữ nguyên (không key) |
|---|---|---|
| `NavMenu` | "Điều khiển / Control" → vi `Điều khiển`, en `Control` | brand `ReTran`, footer `Windows - v{ver}`, `"Unknown"` |
| `Home` card titles | `Nguồn chụp`/`Capture source`, `Dịch`/`Translation`, `Giao diện`/`Appearance` | `Demo pipeline`, `OBS` |
| `Home` labels | `Nguồn / Source:`, `Nhà cung cấp / Provider:`, `URL cho OBS / OBS URL:` (tách vi/en) | — |
| `Home` select options | `Màn hình 0/1`, `Cửa sổ (HWND)` | nhà cung cấp: `Google`, `DeepL`, `Local`, `LLM`; swatch: `Emerald`, `Violet`, `Sunset` |
| `Home` buttons | `Bắt đầu Demo (Core live ở milestone sau)`/`Start Demo`, `Dừng Demo`/`Stop`, `Sao chép`/`Copy`, `Sáng`/`Tối`/`Hệ thống` ↔ `Light`/`Dark`/`System`, `Mặc định`/`Default` | nhãn nút switcher `VI`/`EN` (mã ngôn ngữ, không dịch) |
| `Home` status (nội suy qua `Fmt`) | demo `đã dừng`/`running` + nguồn; `Đã sao chép`/`Copied`; `Theme: {theme}.`; `Màu / Color: {colorId}.`; 2 message lỗi theme/color; warning `Máy chủ feed chưa chạy`/`Feed server not running` | giá trị `{theme}`, `{colorId}` (`light/dark/system`, `default/emerald/violet/sunset`) |
| `Home` misc | note paragraph (Lựa chọn chỉ được lưu tạm…), iframe title `Xem trước overlay`/`Overlay preview` | URL `ObsUrl` |
| `NotFound` | title + body (hiện English-only → có bản vi) | — |
| `MainPage.xaml` | `Start Core` → (en giữ nguyên; vi: `Khởi động Core`), `Core: stopped/started` (code-behind set) | `Ping`, `Stop` (technical) |

Chuỗi hiện tại dạng gộp `VI / EN` bị **tách thành 2 chuỗi riêng**; không còn tùy chọn hiển thị
kép.

## 5. Switcher UI (card Theme)

Dòng mới trong card `Giao diện / Theme`, đặt **dưới 3 nút theme, trên hàng swatch**:

- Label: `Ngôn ngữ / Language:` (label này cũng có key theo lang hiện tại).
- 2 nút: `VI` | `EN` — cùng class `btn btn-outline-secondary` với 3 nút theme; nút đang active
  có kiểu highlight như `Tối / Dark` active hiện tại.
- Bấm → `L.Set("en"|"vi")` → event → Home + NavMenu + header MainPage đổi ngay, không reload;
  `localStorage["retran.lang"]` ghi đè.

## 6. Test & gates

- **`I18nServiceTests` (mới):** key tồn tại ở cả 2 lang (duyệt toàn bộ dictionary — bắt key
  thiếu sót giữa vi/en); key lạ trả đúng key; `Fmt` nội suy đúng; `Set("en")` phát `Changed`;
  lang thiếu fallback `en`.
- **`HomeComponentTests` (cập nhật chữ, GIỮ nguyên số assertion):** inject `I18nService` +
  `Set("vi")` tường minh (không phụ thuộc culture máy) rồi đổi assertion theo chuỗi vi đã tách:
  - `"Sao chép / Copy"` → `"Sao chép"`
  - `"Bắt đầu Demo"` — giữ (vi vẫn bắt đầu bằng chuỗi con này)
  - `"Tối / Dark"` → `"Tối"`
  - `"đã dừng"`, `"đang chạy"`, `"Theme: dark."`, `"Đã sao chép"` — giữ (vi)
  - `"Feed server not running"` → `"Máy chủ feed chưa chạy"`
- **Test mới:** bấm nút `EN` → markup có chuỗi en (`Start Demo`, `Feed server not running`),
  bấm lại `VI` OK. Tổng số test phải **≥ 70** (không giảm test/assertion).
- **Gates:** `dotnet build "ReTran Project.slnx"` 0 err/0 warn;
  `dotnet test ReTran.Tests -f net10.0-windows10.0.19041.0` ≥70 pass;
  `uv run pytest tests` 15 pass; `npm run build` OK (frontend TS không đổi chữ).

## 7. Ngoài phạm vi

- Doc-comment song ngữ trong code (quy ước `AGENTS.md` — giữ nguyên).
- Trang `/overlay` (OBS Browser Source) — chữ do game sinh, không hardcode app.
- Log Core / sidecar Python / message JSON-RPC (không phải UI).
- Plan 2 (OCR perf) — không liên quan, làm sau i18n.
- Ngân sách perf: chỉ dictionary lookup + event → không ảnh hưởng ngân sách `<8% CPU`.

## 8. Tiêu chí hoàn thành

1. Đổi VI/EN thấy ngay trên Home + NavMenu + header MainPage, không reload; reload app giữ
   lựa chọn (localStorage); lần đầu mở auto theo locale Windows.
2. Không còn chuỗi kép `VI / EN` nào trong Razor/XAML hiển thị user (verify bằng grep).
3. Gates pass (số ở mục 6), test ≥70.
4. `I18nServiceTests` + test switcher mới pass.
