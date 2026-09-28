# Spec: Sửa UI layout tham khảo DMFT

- **Ngày:** 2026-09-26
- **Trạng thái:** Approved (user duyệt Section 1–3 qua brainstorming session)
- **Phạm vi:** làm lại `MainLayout` + `NavMenu` theo layout DMFT, restyle `Home.razor` thành header + card dọc (component `Card` tái sử dụng), dọn CSS template. **Không đổi logic, không đổi text, không đụng Overlay/NotFound.**

## 1. Bối cảnh

- CSS infra của ReTran **đã là của DMFT**: `vite-project/src/css/utility.css` + `style.css` giống hệt DMFT, `theme.css` còn có thêm color pairs (default/emerald/violet/sunset). Chỉ thiếu phần **áp dụng**: markup vẫn là template MAUI.
- `MainLayout.razor`/`NavMenu.razor` vẫn dùng scoped CSS template (sidebar gradient, `.top-row`, bootstrap classes, link About learn.microsoft).
- `Home.razor` là control page để control trần không class.
- Bug latent đã chứng minh experiment: `index.html:10` link `ReTran App.styles.css` (tên cũ trước rename project) → 404 thầm lặng.
- `app.css` còn rules template unlayered (`a { color:#006bb7 }`, `.btn-primary` xanh) — unlayered **thắng Tailwind layer** → sẽ đè màu nav/text nếu không dọn.

## 2. Quyết định đã chốt (brainstorming)

| # | Câu hỏi | Quyết định |
|---|---|---|
| 1 | Phạm vi | **Layout + Home; Overlay/NotFound giữ nguyên; gỡ Bootstrap link** |
| 2 | Bố cục Home | **Header (h1) + card dọc** — mỗi section 1 card |
| 3 | Sidebar | **Full DMFT** — brand ReTran, nav glyph, footer version (DMFT thật không có collapse → không media query) |
| 4 | Hướng thực hiện | **Phương án B** — port DMFT + component `Card` tái sử dụng |

## 3. Section 1 — Layout & Sidebar

### `MainLayout.razor` (port DMFT, bỏ `ToastContainer` — ReTran dùng status inline)

```razor
@inherits LayoutComponentBase

<div class="h-screen w-screen bg-background text-surface">
    <div class="flex h-full">
        <div class="w-64 shrink-0 bg-background border-r border-surface flex flex-col">
            <NavMenu />
        </div>
        <main class="flex-1 overflow-auto">
            <div class="p-4">
                @Body
            </div>
        </main>
    </div>
</div>
```

*(bỏ `w-25` duplicate — typo của DMFT)*

### `NavMenu.razor` (port DMFT)

```razor
@using System.Reflection

<div class="p-4">
    <a class="text-xl font-bold text-primary no-underline" href="">ReTran</a>
</div>

<nav class="flex-1 p-2">
    <NavLink class="flex items-center gap-3 px-4 py-2.5 rounded text-surface hover:bg-primary hover:text-light no-underline mb-1"
             href="" Match="NavLinkMatch.All">
        <span class="w-5 text-center">&#9654;</span>
        <span>Điều khiển / Control</span>
    </NavLink>
</nav>

<div class="p-3 text-xs text-secondary">
    <span>Windows - v@(Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown")</span>
</div>
```

### Xóa

- `Components/Layout/MainLayout.razor.css` + `Components/Layout/NavMenu.razor.css` (chỉ còn style template) → bundle `ReTran.App.styles.css` không sinh ra nữa → xóa luôn link ở `index.html:10`.
- `NotFound.razor`: giữ nguyên (kế thừa layout mới). **Overlay: không đụng.**

## 4. Section 2 — Card component + Home.razor

### `Components/Card.razor` (root `Components/` → namespace `ReTran_App.Components` đã có trong `_Imports`, không cần using thêm)

```razor
<div class="bg-background rounded-lg p-4 border flex flex-col gap-3">
    <h2 class="text-lg font-semibold text-surface m-0">@Title</h2>
    @ChildContent
</div>

@code {
    /// <summary>
    /// Card section title / Tiêu đề section trong card.
    /// </summary>
    [Parameter] public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Card body rendered inside the card / Nội dung hiển thị bên trong card.
    /// </summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }
}
```

### `Home.razor` — chỉ viết lại markup, `@code` + text giữ nguyên 100%

- `<PageTitle>ReTran - Điều khiển / Control</PageTitle>`.
- Header: `flex items-center justify-between mb-6`, h1 `text-2xl font-bold text-surface m-0` — **không nút bên phải** (không có action tự nhiên, tránh bịa).
- `<div class="space-y-4">` gồm 5 card:
  1. **Nguồn chụp / Capture source** — select Source (3 option giữ nguyên), input HWND conditional, `<p>` note giữ nguyên.
  2. **Dịch / Translation** — select Provider (Google/DeepL/Local/LLM giữ nguyên).
  3. **Demo pipeline** — button Start (`btn btn-primary`) + Stop (`btn btn-outline-secondary`), text giữ nguyên; status `<p class="text-sm text-secondary">@_status</p>`.
  4. **OBS** — `<code>` URL, button Copy (`btn btn-outline-primary`), span `_copyStatus`, warning "Feed server not running" giữ nguyên, iframe `w-full max-w-[480px] h-[270px] rounded border`.
  5. **Giao diện / Theme** — 3 button theme (`btn btn-outline-secondary`), 4 button swatch (circle `w-4 h-4 rounded-full`, giữ `data-color` attr, bỏ inline style), span `_themeStatus`.
- Form control chuẩn DMFT: label-above `flex flex-col gap-1.5 text-sm text-surface`; control `w-full px-3 py-2 border rounded bg-background text-surface focus:outline-none focus:ring-2 focus:ring-primary`.
- Bilingual text giữ nguyên **từng ký tự** (test match substring).

## 5. Section 3 — CSS/index.html dọn dẹp

1. `wwwroot/index.html:10`: **xóa** `<link rel="stylesheet" href="ReTran App.styles.css" />` — đã verify: hiện `wwwroot` không có file `*.styles.css` nào (link này vẫn 404 sẵn), và sau khi xóa 2 scoped css cũng không có gì để link tham chiếu.
2. `wwwroot/index.html:8`: **xóa** link `lib/bootstrap/dist/css/bootstrap.min.css` + xóa thư mục `wwwroot/lib/bootstrap/` (không còn class nào dùng — đã grep, chỉ NavMenu template xài và NavMenu sẽ viết lại).
3. `wwwroot/app.css`: **giữ** font, `#blazor-error-ui`, `.blazor-error-boundary`, `.status-bar-safe-area`; **xóa** `a/.btn-link` color, `.btn-primary`, `.btn:focus`, `.content`, `h1:focus`, `.valid/.invalid/.validation-message`, `.darker-border-checkbox`, `.form-floating`, block `@supports` rule `.flex-column/.navbar-brand`.
4. Xóa 2 file scoped css (Section 1).

## 6. Ràng buộc test (không được phá)

- Baseline C# **70** / Python **15** — không giảm test/assertion.
- 3 bUnit `HomeComponentTests` tìm button theo text + `Assert.Contains` markup — chuỗi phải giữ nguyên: `"Bắt đầu Demo"`, `"Dừng Demo"`, `"Sao chép / Copy"`, `"Tối / Dark"`, `"đã dừng"`, `"đang chạy"`, `"Theme: dark."`, `"Feed server not running"`, `"Đã sao chép"`, ObsUrl.
- Không có test nào bám selector layout — đã grep toàn bộ `ReTran.Tests/*.cs` (kể cả `AppiumSmokeTests`): 0 match `sidebar`/`navbar`/`top-row`/`article`/`css=`.

## 7. Verify (acceptance)

1. `npm run build` (từ `ReTran.App/vite-project`) — tailwind sinh class mới.
2. `dotnet build "ReTran Project.slnx"` — sạch.
3. `dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0` — **70/70**.
4. `uv run pytest tests -v` (từ `ReTran.OCR/`) — **15/15** (không đụng Python nhưng chạy cho chắc).
5. Check output không còn reference/404 `ReTran App.styles.css`.
6. Chạy app, chụp màn hình control page (sidebar + 5 card, theme light/dark) → user xem visual.

## 8. Non-goals (ngoài phạm vi)

- Overlay page (`/overlay`) + `OverlayFeedServer` — không đụng.
- Toast/Modal system của DMFT — ReTran giữ status inline.
- Responsive collapse (DMFT thật không có).
- Sửa bug UnicodeEncodeError sidecar — thuộc Plan 2 task 1.
- Đổi nội dung/chức năng control page — chỉ đổi hình thức.
