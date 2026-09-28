# UI Layout DMFT Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Làm lại layout ReTran (sidebar + control page) theo phong cách DMFT — header + card dọc, component `Card` tái sử dụng, dọn CSS template — không đổi logic/text.

**Architecture:** Port markup Tailwind từ DMFT (`MainLayout`, `NavMenu`) thay cho template MAUI + scoped CSS; `Home.razor` viết lại markup quanh component `Card` mới, `@code` giữ nguyên 100%; gỡ Bootstrap/app.css template/index.html link lỗi.

**Tech Stack:** .NET 10 MAUI Blazor Hybrid, Razor components, Tailwind v4 (vite build từ `ReTran.App/vite-project`), bUnit/xUnit.

## Global Constraints

- Spec: `Docs/superpowers/specs/2026-09-26-ui-layout-dmft-design.md` — mọi requirement ở đây map về spec.
- Test baselines: C# **70** / Python **15** — không giảm test/assertion; mỗi task chạy `dotnet test` phải **70/70 PASS**.
- Text bilingual trong `Home.razor` giữ nguyên **từng ký tự** (bUnit match substring: `"Bắt đầu Demo"`, `"Dừng Demo"`, `"Sao chép / Copy"`, `"Tối / Dark"`, `"đã dừng"`, `"đang chạy"`, `"Theme: dark."`, `"Feed server not running"`, `"Đã sao chép"`, ObsUrl).
- `@code` block của `Home.razor` giữ nguyên 100% (copy verbatim, không sửa 1 dòng).
- Không đụng: Overlay page, `OverlayFeedServer`, `NotFound.razor`, Python sidecar.
- Không commit: `AGENTS.md` (dirty từ trước — để nguyên cho user quyết định), `.codegraph/`.
- Doc comment C#: bilingual (EN câu đầu, VI ngay sau) — áp cho `Card.razor`.
- Build/test commands:
  - Frontend: `npm run build` (từ `ReTran.App/vite-project`) — output `../wwwroot/build` (**git-tracked 2 file**: `assets/main.js`, `assets/styles.css` → stage cùng commit).
  - C#: `dotnet build "ReTran Project.slnx"` (từ repo root).
  - Test: `dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0` → Expected: `Failed: 0, Passed: 70`.
  - Python: `uv run pytest tests -v` (từ `ReTran.OCR/`) → Expected: `15 passed`.
- Shell: lệnh chạy qua tool bash (PowerShell 7 host); script nhiều dòng dùng `;` hoặc `&&`.

---

### Task 1: Layout + NavMenu (port DMFT) + xóa scoped CSS

**Files:**
- Modify: `ReTran.App/Components/Layout/MainLayout.razor`
- Modify: `ReTran.App/Components/Layout/NavMenu.razor`
- Delete: `ReTran.App/Components/Layout/MainLayout.razor.css`
- Delete: `ReTran.App/Components/Layout/NavMenu.razor.css`
- Modify: `ReTran.App/wwwroot/index.html` (bỏ dòng 10 — link `ReTran App.styles.css`)

**Interfaces:**
- Consumes: `_Imports.razor` đã có `@using ReTran_App.Components.Layout` (NavMenu/Home resolve sẵn).
- Produces: layout classes mới (`h-screen`, `w-64 shrink-0`, `flex-1 overflow-auto`) — Task 4 verify; `NavMenu` brand text `ReTran`, nav text `Điều khiển / Control`, footer `Windows - v{version}`.

- [ ] **Step 1: Commit docs (spec + plan)**

```powershell
git add "Docs/superpowers/specs/2026-09-26-ui-layout-dmft-design.md" "Docs/superpowers/plans/2026-09-26-ui-layout-dmft.md"
git commit -m "docs: spec + plan cho UI layout theo DMFT"
```

Expected: 1 commit, working tree còn `AGENTS.md` (M) + `.codegraph/` (??) — **không stage**.

- [ ] **Step 2: Viết lại `MainLayout.razor`**

Thay toàn bộ nội dung file thành:

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

- [ ] **Step 3: Viết lại `NavMenu.razor`**

Thay toàn bộ nội dung file thành:

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

- [ ] **Step 4: Xóa 2 file scoped CSS**

```powershell
Remove-Item "ReTran.App\Components\Layout\MainLayout.razor.css", "ReTran.App\Components\Layout\NavMenu.razor.css"
```

- [ ] **Step 5: Bỏ link CSS lỗi trong `index.html`**

Xóa dòng 10 của `ReTran.App/wwwroot/index.html`:

```html
    <link rel="stylesheet" href="ReTran App.styles.css" />
```

(Kết quả: sau `app.css` là dòng `build/assets/styles.css`.)

- [ ] **Step 6: Build frontend (tailwind sinh class mới)**

Run: `npm run build` (workdir `ReTran.App\vite-project`)
Expected: `tsc && vite build` thành công, không lỗi.

- [ ] **Step 7: Build solution**

Run: `dotnet build "ReTran Project.slnx"`
Expected: `0 Error(s)`.

- [ ] **Step 8: Chạy test — 70/70**

Run: `dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`
Expected: `Failed: 0, Passed: 70` — nếu fail → sửa trước khi commit.

- [ ] **Step 9: Verify không còn bundle scoped css tham chiếu**

```powershell
Select-String -Path "ReTran.App\wwwroot\index.html" -Pattern 'styles.css'
Get-ChildItem "ReTran.App\wwwroot" -Recurse -Filter "*.styles.css"
```

Expected: chỉ còn 1 match `build/assets/styles.css`; output thứ hai rỗng.

- [ ] **Step 10: Commit**

```powershell
git add "ReTran.App/Components/Layout/MainLayout.razor" "ReTran.App/Components/Layout/NavMenu.razor" "ReTran.App/Components/Layout/MainLayout.razor.css" "ReTran.App/Components/Layout/NavMenu.razor.css" "ReTran.App/wwwroot/index.html" "ReTran.App/wwwroot/build"
git commit -m "feat(app): port MainLayout + NavMenu to DMFT-style sidebar, drop scoped template CSS"
```

---

### Task 2: Component `Card` + restyle `Home.razor`

**Files:**
- Create: `ReTran.App/Components/Card.razor`
- Modify: `ReTran.App/Components/Pages/Home.razor`
- Build artifacts: `ReTran.App/wwwroot/build/assets/{main.js,styles.css}` (git-tracked — stage cùng commit)

**Interfaces:**
- Consumes: namespace `ReTran_App.Components` đã có trong `_Imports.razor` (không cần using thêm); utilities `btn`, `btn-primary`, `btn-outline-secondary`, `btn-outline-primary`, `text-surface`, `text-secondary`, `text-danger`, `bg-background`, `border`, `focus:ring-primary` từ `vite-project/src/css/{utility,theme}.css` (đã có sẵn).
- Produces: `<Card Title="...">...</Card>` (Parameter `Title: string`, `ChildContent: RenderFragment?`) — chỉ Task 2 dùng, Task 4 verify visual.

- [ ] **Step 1: Tạo `ReTran.App/Components/Card.razor`**

```razor
<div class="bg-background rounded-lg p-4 border flex flex-col gap-3">
    <h2 class="text-lg font-semibold text-surface m-0">@Title</h2>
    @ChildContent
</div>

@code {
    /// <summary>
    /// Card section title / Tiêu đề section trong card.
    /// </summary>
    [Parameter]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Card body rendered inside the card / Nội dung hiển thị bên trong card.
    /// </summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
```

- [ ] **Step 2: Viết lại `Home.razor` — markup mới, `@code` copy verbatim**

Thay toàn bộ nội dung `ReTran.App/Components/Pages/Home.razor` thành:

```razor
@page "/"
@inject ReTran_App.Core.OverlayState State
@inject ReTran_App.Services.OverlayFeedServer FeedServer
@inject ReTran_App.Services.JsInteropService Js
@inject IJSRuntime JS
@implements IDisposable

<PageTitle>ReTran - Điều khiển / Control</PageTitle>

<div class="flex items-center justify-between mb-6">
    <h1 class="text-2xl font-bold text-surface m-0">Điều khiển / Control</h1>
</div>

<div class="space-y-4">
    <Card Title="Nguồn chụp / Capture source">
        <label class="flex flex-col gap-1.5 text-sm text-surface">
            Nguồn / Source:
            <select @bind="_source" class="w-full px-3 py-2 border rounded bg-background text-surface focus:outline-none focus:ring-2 focus:ring-primary">
                <option value="screen:0">Màn hình 0 / Screen 0</option>
                <option value="screen:1">Màn hình 1 / Screen 1</option>
                <option value="window">Cửa sổ / Window (HWND)</option>
            </select>
        </label>
        @if (_source == "window")
        {
            <label class="flex flex-col gap-1.5 text-sm text-surface">
                HWND cửa sổ / Window HWND:
                <input @bind="_hwnd" placeholder="VD / e.g. 0x1A2B3C" class="w-full px-3 py-2 border rounded bg-background text-surface focus:outline-none focus:ring-2 focus:ring-primary" />
            </label>
        }
        <p class="text-sm text-secondary">Lựa chọn chỉ được lưu tạm — RPC capture chưa có, Core live ở milestone sau. / Stored selection only — capture RPC not yet available, Core live comes in a later milestone.</p>
    </Card>

    <Card Title="Dịch / Translation">
        <label class="flex flex-col gap-1.5 text-sm text-surface">
            Nhà cung cấp / Provider:
            <select @bind="_provider" class="w-full px-3 py-2 border rounded bg-background text-surface focus:outline-none focus:ring-2 focus:ring-primary">
                <option>Google</option>
                <option>DeepL</option>
                <option>Local</option>
                <option>LLM</option>
            </select>
        </label>
    </Card>

    <Card Title="Demo pipeline">
        <div class="flex gap-2 flex-wrap">
            <button class="btn btn-primary" @onclick="StartDemo" disabled="@_running">Bắt đầu Demo (Core live ở milestone sau) / Start Demo</button>
            <button class="btn btn-outline-secondary" @onclick="StopDemo" disabled="@(!_running)">Dừng Demo / Stop</button>
        </div>
        <p class="text-sm text-secondary">@_status</p>
    </Card>

    <Card Title="OBS">
        <div class="flex items-center gap-2 flex-wrap">
            <span class="text-sm text-surface">URL cho OBS / OBS URL:</span>
            <code class="px-2 py-1 rounded bg-background border text-sm text-surface break-all">@ObsUrl</code>
            <button class="btn btn-outline-primary" @onclick="CopyUrlAsync">Sao chép / Copy</button>
            @if (!string.IsNullOrEmpty(_copyStatus))
            {
                <span class="text-sm text-secondary">@_copyStatus</span>
            }
        </div>
        @if (FeedServer.Port == 0)
        {
            <p class="text-sm text-danger">Máy chủ feed chưa chạy / Feed server not running.</p>
        }
        <iframe src="@ObsUrl" title="Xem trước overlay / Overlay preview" class="w-full max-w-[480px] h-[270px] rounded border"></iframe>
    </Card>

    <Card Title="Giao diện / Theme">
        <div class="flex gap-2 flex-wrap">
            <button class="btn btn-outline-secondary" @onclick='() => SetThemeAsync("light")'>Sáng / Light</button>
            <button class="btn btn-outline-secondary" @onclick='() => SetThemeAsync("dark")'>Tối / Dark</button>
            <button class="btn btn-outline-secondary" @onclick='() => SetThemeAsync("system")'>Hệ thống / System</button>
        </div>
        <div class="flex gap-2 flex-wrap">
            <button class="btn btn-outline-secondary" @onclick='() => SetColorAsync("default")' title="Mặc định / Default"><span class="inline-block w-4 h-4 rounded-full border border-current" style="background:var(--primary);"></span> Mặc định / Default</button>
            <button class="btn btn-outline-secondary" data-color="emerald" @onclick='() => SetColorAsync("emerald")' title="Emerald"><span class="inline-block w-4 h-4 rounded-full border border-current" style="background:var(--primary);"></span> Emerald</button>
            <button class="btn btn-outline-secondary" data-color="violet" @onclick='() => SetColorAsync("violet")' title="Violet"><span class="inline-block w-4 h-4 rounded-full border border-current" style="background:var(--primary);"></span> Violet</button>
            <button class="btn btn-outline-secondary" data-color="sunset" @onclick='() => SetColorAsync("sunset")' title="Sunset"><span class="inline-block w-4 h-4 rounded-full border border-current" style="background:var(--primary);"></span> Sunset</button>
        </div>
        @if (!string.IsNullOrEmpty(_themeStatus))
        {
            <span class="text-sm text-secondary">@_themeStatus</span>
        }
    </Card>
</div>

@code {
    private string _source = "screen:0";
    private string _hwnd = string.Empty;
    private string _provider = "Google";
    private ReTran_App.Core.DemoFeed? _feed;
    private bool _running;
    private string _status = "Demo: đã dừng / stopped.";
    private string _copyStatus = string.Empty;
    private string _themeStatus = string.Empty;

    private string ObsUrl => $"http://127.0.0.1:{FeedServer.Port}/overlay";

    protected override void OnAfterRender(bool firstRender)
    {
        // Nothing else initializes the JS bridge (verified: no other caller of
        // Initialize/NotifyWebViewReady), so the control page does it on first
        // render — otherwise copyText would silently no-op.
        if (firstRender)
        {
            Js.Initialize(JS);
            Js.NotifyWebViewReady();
        }
    }

    private void StartDemo()
    {
        if (_feed is not null) return;
        _feed = new ReTran_App.Core.DemoFeed(State);
        _feed.Start();
        _running = true;
        _status = $"Demo: đang chạy / running — nguồn / source: {SourceLabel()}.";
    }

    private void StopDemo()
    {
        if (_feed is null) return;
        _feed.Stop();
        _feed.Dispose();
        _feed = null;
        _running = false;
        _status = "Demo: đã dừng / stopped.";
    }

    private string SourceLabel() => _source == "window" && !string.IsNullOrWhiteSpace(_hwnd)
        ? $"Window ({_hwnd})"
        : _source;

    private async Task CopyUrlAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("copyText", ObsUrl);
            _copyStatus = "Đã sao chép / Copied.";
        }
        catch (Exception)
        {
            _copyStatus = "Sao chép thất bại / Copy failed.";
        }
    }

    public void Dispose()
    {
        if (_feed is not null)
        {
            _feed.Stop();
            _feed.Dispose();
            _feed = null;
        }
    }

    private async Task SetThemeAsync(string theme)
    {
        try
        {
            await Js.SetThemeAsync(theme);
            _themeStatus = $"Theme: {theme}.";
        }
        catch (Exception)
        {
            _themeStatus = "Đổi theme thất bại / Theme change failed.";
        }
    }

    private async Task SetColorAsync(string colorId)
    {
        try
        {
            await Js.SetColorAsync(colorId);
            _themeStatus = $"Màu / Color: {colorId}.";
        }
        catch (Exception)
        {
            _themeStatus = "Đổi màu thất bại / Color change failed.";
        }
    }
}
```

**Lưu ý bắt buộc khi copy:**
- `@code` block phải **verbatim** với bản cũ (dựng từ git: `git show HEAD:ReTran.App/Components/Pages/Home.razor` nếu nghi ngờ).
- Giữ nguyên `data-color` attr trên 3 button swatch (cơ chế preview swatch qua `[data-color=...]` selector trong `theme.css`), button "Mặc định" **không** có `data-color`.
- Giữ nguyên text bilingual từng ký tự.

- [ ] **Step 3: Build frontend**

Run: `npm run build` (workdir `ReTran.App\vite-project`)
Expected: thành công — tailwind scan thấy class mới trong `Components/*.razor`.

- [ ] **Step 4: Build solution**

Run: `dotnet build "ReTran Project.slnx"`
Expected: `0 Error(s)`.

- [ ] **Step 5: Chạy test — 70/70, HomeComponentTests 3/3**

Run: `dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0`
Expected: `Failed: 0, Passed: 70`.
Nếu `HomeComponentTests` fail → thường do đổi text; khôi phục text cũ, KHÔNG sửa test.

- [ ] **Step 6: Commit**

```powershell
git add "ReTran.App/Components/Card.razor" "ReTran.App/Components/Pages/Home.razor" "ReTran.App/wwwroot/build"
git commit -m "feat(app): restyle control page as header + cards with shared Card component"
```

---

### Task 3: Dọn CSS template (app.css + Bootstrap)

**Files:**
- Modify: `ReTran.App/wwwroot/app.css`
- Modify: `ReTran.App/wwwroot/index.html` (bỏ dòng 8 — link bootstrap)
- Delete: `ReTran.App/wwwroot/lib/bootstrap/` (thư mục)

**Interfaces:**
- Consumes: Task 1 đã bỏ bootstrap classes khỏi NavMenu/MainLayout; Task 2 dùng Tailwind utilities (cần app.css không đè màu).
- Produces: app.css chỉ còn font + error UI + status-bar (Task 4 verify).

- [ ] **Step 1: Viết lại `app.css` — chỉ giữ khối cần thiết**

Thay toàn bộ nội dung `ReTran.App/wwwroot/app.css` thành:

```css
html, body {
    font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;
}

#blazor-error-ui {
    color-scheme: light only;
    background: lightyellow;
    bottom: 0;
    box-shadow: 0 -1px 2px rgba(0, 0, 0, 0.2);
    box-sizing: border-box;
    display: none;
    left: 0;
    padding: 0.6rem 1.25rem 0.7rem 1.25rem;
    position: fixed;
    width: 100%;
    z-index: 1000;
}

    #blazor-error-ui .dismiss {
        cursor: pointer;
        position: absolute;
        right: 0.75rem;
        top: 0.5rem;
    }

.blazor-error-boundary {
    background: url(data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNTYiIGhlaWdodD0iNDkiIHhtbG5zPSJodHRwOi8vd3d3LnczLm9yZy8yMDAwL3N2ZyIgeG1sbnM6eGxpbms9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkveGxpbmsiIG92ZXJmbG93PSJoaWRkZW4iPjxkZWZzPjxjbGlwUGF0aCBpZD0iY2xpcDAiPjxyZWN0IHg9IjIzNSIgeT0iNTEiIHdpZHRoPSI1NiIgaGVpZ2h0PSI0OSIvPjwvY2xpcFBhdGg+PC9kZWZzPjxnIGNsaXAtcGF0aD0idXJsKCNjbGlwMCkiIHRyYW5zZm9ybT0idHJhbnNsYXRlKC0yMzUgLTUxKSI+PHBhdGggZD0iTTI2My41MDYgNTFDMjY0LjcxNyA1MSAyNjUuODEzIDUxLjQ4MzcgMjY2LjYwNiA1Mi4yNjU4TDI2Ny4wNTIgNTIuNzk4NyAyNjcuNTM5IDUzLjYyODMgMjkwLjE4NSA5Mi4xODMxIDI5MC41NDUgOTIuOTk2QzI5MC44NzcgOTMuNTEzIDI5MSA5NC4wODE1IDI5MSA5NC42NzgyIDI5MSA5Ny4wNjUxIDI4OS4wMzggOTkgMjg2LjYxNyA5OUwyNDAuMzgzIDk5QzIzNy45NjMgOTkgMjM2IDk3LjA2NTEgMjM2IDk0LjY3ODIgMjM2IDk0LjM3OTkgMjM2LjAzMSA5NC4wODg2IDIzNi4wODkgOTMuODA3MkwyMzYuMzM4IDkzLjAxNjIgMjM2Ljg1OCA5Mi4xMzE0IDI1OS40NzMgNTMuNjI5NCAyNTkuOTYxIDUyLjc5ODUgMjYwLjQwNyA1Mi4yNjU4QzI2MS4yIDUxLjQ4MzcgMjYyLjI5NiA1MSAyNjMuNTA2IDUxWk0yNjMuNTg2IDY2LjAxODNDMjYwLjczNyA2Ni4wMTgzIDI1OS4zMTMgNjcuMTI0NSAyNTkuMzEzIDY5LjMzNyAyNTkuMzEzIDY5LjYxMDIgMjU5LjMzMiA2OS44NjA4IDI1OS4zNzEgNzAuMDg4N0wyNjEuNzk1IDg0LjAxNjEgMjY1LjM4IDg0LjAxNjEgMjY3LjgyMSA2OS43NDc1QzI2Ny44NiA2OS43MzA5IDI2Ny44NzkgNjkuNTg3NyAyNjcuODc5IDY5LjMxNzkgMjY3Ljg3OSA2Ny4xMTgyIDI2Ni40NDggNjYuMDE4MyAyNjMuNTg2IDY2LjAxODNaTTI2My41NzYgODYuMDU0N0MyNjEuMDQ5IDg2LjA1NDcgMjU5Ljc4NiA4Ny4zMDA1IDI1OS43ODYgODkuNzkyMSAyNTkuNzg2IDkyLjI4MzcgMjYxLjA0OSA5My41Mjk1IDI2My41NzYgOTMuNTI5NSAyNjYuMTE2IDkzLjUyOTUgMjY3LjM4NyA5Mi4yODM3IDI2Ny4zODcgODkuNzkyMSAyNjcuMzg3IDg3LjMwMDUgMjY2LjExNiA4Ni4wNTQ3IDI2My41NzYgODYuMDU0N1oiIGZpbGw9IiNGRkU1MDAiIGZpbGwtcnVsZT0iZXZlbm9kZCIvPjwvZz48L3N2Zz4=) no-repeat 1rem/1.8rem, #b32121;
    padding: 1rem 1rem 1rem 3.7rem;
    color: white;
}

    .blazor-error-boundary::after {
        content: "An error has occurred."
    }

.status-bar-safe-area {
    display: none;
}

@supports (-webkit-touch-callout: none) {
    .status-bar-safe-area {
        display: flex;
        position: sticky;
        top: 0;
        height: env(safe-area-inset-top);
        background-color: #f7f7f7;
        width: 100%;
        z-index: 1;
    }
}
```

*(Đã xóa: `a/.btn-link` color, `.btn-primary`, `.btn:focus`, `.content`, `h1:focus`, `.valid/.invalid/.validation-message`, `.darker-border-checkbox`, `.form-floating` — các rule unlayered đè Tailwind utilities.)*

- [ ] **Step 2: Bỏ link bootstrap trong `index.html`**

Xóa dòng 8 của `ReTran.App/wwwroot/index.html`:

```html
    <link rel="stylesheet" href="lib/bootstrap/dist/css/bootstrap.min.css" />
```

- [ ] **Step 3: Xóa thư mục bootstrap**

```powershell
Remove-Item -Recurse -Force "ReTran.App\wwwroot\lib"
```

(giảm ~0,8 MB template — verify bằng `Test-Path "ReTran.App\wwwroot\lib"` → `False`).

- [ ] **Step 4: Build + test**

```powershell
npm run build   # workdir: ReTran.App\vite-project
dotnet build "ReTran Project.slnx"
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0
```

Expected: build `0 Error(s)`; test `Failed: 0, Passed: 70`.

- [ ] **Step 5: Verify không còn reference bootstrap/CSS lỗi**

```powershell
Select-String -Path "ReTran.App\wwwroot\index.html" -Pattern 'bootstrap|ReTran App.styles'
Select-String -Path "ReTran.App\wwwroot\app.css" -Pattern 'btn-primary|form-floating|validation'
```

Expected: cả 2 rỗng (0 match).

- [ ] **Step 6: Commit**

```powershell
git add "ReTran.App/wwwroot/app.css" "ReTran.App/wwwroot/index.html" "ReTran.App/wwwroot/lib" "ReTran.App/wwwroot/build"
git commit -m "chore(app): drop Bootstrap and template CSS leftovers"
```

---

### Task 4: Verify toàn bộ + screenshot cho user

**Files:** không thay đổi code (task verify — không commit, trừ khi phát hiện bug → fix + commit riêng).

**Interfaces:**
- Consumes: toàn bộ Task 1–3.
- Produces: báo cáo kết quả + 2 screenshot (light/dark) cho user review.

- [ ] **Step 1: Full gates**

```powershell
npm run build   # workdir: ReTran.App\vite-project → OK
dotnet build "ReTran Project.slnx"   # → 0 Error(s)
dotnet test "ReTran.Tests\ReTran.Tests.csproj" -f net10.0-windows10.0.19041.0   # → Failed: 0, Passed: 70
```

Run từ `ReTran.OCR/`: `uv run pytest tests -v` → Expected: `15 passed`.

- [ ] **Step 2: Verify dọn sạch template**

```powershell
Select-String -Path "ReTran.App\wwwroot\index.html" -Pattern 'bootstrap|ReTran App.styles'
Get-ChildItem "ReTran.App\wwwroot" -Recurse -Filter "*.styles.css"
Get-ChildItem "ReTran.App\Components" -Recurse -Include *.razor.css
Select-String -Path "ReTran.App\Components\Pages\Home.razor" -Pattern '<section>|class="page"'
```

Expected: tất cả rỗng / 0 match (riêng index.html chỉ còn `build/assets/styles.css`).

- [ ] **Step 3: Chạy app + screenshot light theme**

```powershell
Start-Process "D:\Code\ReTran Project\ReTran.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\ReTran App.exe"
Start-Sleep -Seconds 8
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("$env:TEMP\retran-ui-light.png")
$g.Dispose(); $bmp.Dispose()
```

Đọc `$env:TEMP\retran-ui-light.png` (dùng read tool) để xem + gửi user.

- [ ] **Step 4: Screenshot dark theme**

Nhắc user bấm nút **Tối / Dark** trong app (kéo app ra trước cửa sổ), chờ 2s, chụp lại:

```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("$env:TEMP\retran-ui-dark.png")
$g.Dispose(); $bmp.Dispose()
```

Đọc + gửi user xem. Kiểm tra: sidebar 256px có brand/nav/footer, 5 card xếp dọc, controls có border/focus ring, không còn link xanh template.

- [ ] **Step 5: Đóng app**

```powershell
Stop-Process -Name "ReTran App" -ErrorAction SilentlyContinue
```

- [ ] **Step 6: Báo cáo**

Tổng hợp: số test (70/15), kết quả grep verify, 2 screenshot, ghi rõ nếu có phát hiện bug (fix ở task riêng, có commit riêng).
