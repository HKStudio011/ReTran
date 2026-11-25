# ReTran Brand Icon Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đưa monogram N — A dải gấp mềm vào MAUI app icon, tray, topbar và chip menu nổi thu gọn với hình dạng nhất quán theo từng kích thước.

**Architecture:** SVG giữ bản đầy đủ cho MAUI và bản rút gọn cho topbar. Một renderer GDI+ dùng chung vẽ monogram rút gọn trên hệ tọa độ 128×128 cho tray và chip; các vòng đời `HICON`, layout và hit-test hiện có không thay đổi.

**Tech Stack:** .NET 10, C#, GDI+ (`System.Drawing`), xUnit v3/MTP, Razor, SVG, CSS, Vite/TypeScript build hiện có.

**Spec:** `Docs/superpowers/specs/2026-11-24-retran-folded-a-brand-icon-design.md`

## Global Constraints

- Màu brand cố định: Ink `#07201E`, Ivory `#EFEAD9`, Amber `#FFB020`, mặt gấp `#C7CEC4`.
- Geometry dùng chung trên viewBox 128×128:
  - Vòng cung: `M95.112 95.112A44 44 0 1 1 95.112 32.888`, stroke 6, đầu tròn.
  - Thân A: `M39 94V41Q39 37 42 34L48 28Q51 25 54 29L94 86Q98 92 90 92H78L56 61V78H71Q75 78 77 82L82 90Q85 94 79 94Z`.
  - Mặt gấp: `M51 27L94 86Q98 92 90 92H78L56 61L61 47Z`.
  - Nhấn gấp: `M56 78H71Q75 78 77 82L82 90Q85 94 79 94H69L64 86H56Z`.
  - Bản đầy đủ thêm ba mốc: `M64 17v7M18 64h7M64 103v7`, stroke 4, đầu tròn. Bản native/topbar rút gọn không có mốc này.
- Không thêm package hoặc SVG decoder; không dùng font glyph cho icon.
- Không đổi splash screen, overlay OBS, màu `BoxAnnotationStyle.TickColor`, layout/hit-test hoặc cơ chế sở hữu/hủy `HICON`.
- API C# mới hoặc thay đổi phải có XML doc song ngữ theo `AGENTS.md`; hành vi cũ được bảo toàn.
- Không chỉnh hoặc commit hai tài liệu untracked không liên quan: `2026-11-19-settings-workflow-and-llm-status*` và plan cũ cùng tên.

## Review Focus

- **Khung hình nhỏ 32/44 px:** phải còn cả mực Amber và Ivory sau khử răng cưa — test ở Task 1, 2 và 3.
- **Graphics đã có transform/clip:** renderer không được làm rò trạng thái sang phần vẽ tiếp theo — test `Draw_PreservesGraphicsState` ở Task 1.
- **Pixel ngoài nền bo góc:** phải trong suốt, không thành khối đen che nền desktop — test ở Task 2 và Task 3.
- **Restart/dispose tray:** handle phải ổn định khi đọc và chỉ bị hủy qua lifecycle hiện có — chạy lại toàn bộ `TrayIconSourceTests` ở Task 2 và Task 6.
- **Trạng thái topbar:** thay dấu chấm brand nhưng không được xóa chip Live/Stopped — test markup ở Task 5 và kiểm tra build ở Task 6.

---

### Task 1: Renderer GDI+ dùng chung cho monogram rút gọn

**Files:**
- Create: `ReTran.App/Services/Branding/BrandMarkRenderer.cs`
- Test: `ReTran.Tests/Branding/BrandMarkRendererTests.cs`

**Interfaces:**
- Consumes: `System.Drawing.Graphics`, `RectangleF`.
- Produces: `internal static void BrandMarkRenderer.Draw(SD.Graphics graphics, SD.RectangleF bounds)`; vẽ glyph không nền, không mốc giờ, không font và khôi phục trạng thái graphics trước khi trả về.

- [ ] **Step 1: Viết test thất bại**

Tạo hai test:
- `Draw_PaintsBrandColors_WithoutCoveringTransparentCorners`: bitmap 64×64 transparent, gọi `Draw(g, new RectangleF(0, 0, 64, 64))`; đếm pixel gần Amber (`R>200, G>120, G<210, B<80`) và Ivory (`R>200, G>200, B>180`) đều >20; bốn góc bitmap phải alpha 0.
- `Draw_PreservesGraphicsState`: đặt translate/scale và clip khác mặc định, lưu `Transform.Elements` và clip bounds trước khi gọi; sau `Draw`, so sánh lại các giá trị đó.

- [ ] **Step 2: Chạy test và xác nhận RED**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "BrandMarkRendererTests"`

Expected: FAIL do `ReTran_App.Services.Branding.BrandMarkRenderer` chưa tồn tại.

- [ ] **Step 3: Cài renderer tối thiểu**

Cài `Draw` trên hệ tọa độ 128×128: scale đều theo cạnh nhỏ của `bounds`, căn giữa, `Save`/`Restore` trong `try/finally`. Vẽ vòng cung Amber bằng `AddArc(20, 20, 88, 88, 45, 270)`, pen 6 và đầu tròn; dải A mềm dùng đúng path data ở Global Constraints, chuyển mỗi đoạn quadratic `Q` thành cubic Bezier tương đương khi dựng `GraphicsPath`. Không vẽ nền, tick hoặc text.

- [ ] **Step 4: Chạy test và xác nhận GREEN**

Run lại lệnh Step 2.

Expected: cả hai test PASS; các góc vẫn transparent và transform/clip được khôi phục.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Branding/BrandMarkRenderer.cs ReTran.Tests/Branding/BrandMarkRendererTests.cs
git commit -m "feat: add shared ReTran brand mark renderer"
```

### Task 2: Thay tray icon R bằng monogram rút gọn

**Files:**
- Modify: `ReTran.App/Services/Tray/TrayIconSource.cs`
- Test: `ReTran.Tests/Tray/TrayIconSourceTests.cs`

**Interfaces:**
- Consumes: `BrandMarkRenderer.Draw(graphics, bounds)` từ Task 1.
- Produces: `internal static SD.Bitmap TrayIconSource.RenderBitmap()` trả bitmap 32×32 do caller sở hữu; constructor dùng cùng hàm này rồi giữ nguyên `GetHicon`/`DestroyIcon`.

- [ ] **Step 1: Viết test thất bại**

Thêm `RenderBitmap_ContainsSimplifiedBrandMark`: gọi `TrayIconSource.RenderBitmap()`, assert kích thước 32×32, có >8 pixel Amber và >8 pixel Ivory theo ngưỡng Task 1, đồng thời bốn góc alpha 0. Giữ nguyên hai test handle/dispose hiện có.

- [ ] **Step 2: Chạy test và xác nhận RED**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "TrayIconSourceTests"`

Expected: FAIL do `RenderBitmap` chưa tồn tại.

- [ ] **Step 3: Tách bitmap rendering và thay glyph R**

Cài `RenderBitmap`: nền Ink bo góc radius 6 bằng `BuildRoundedRect`, sau đó `BrandMarkRenderer.Draw(g, new RectangleF(4, 4, 24, 24))`. Constructor chỉ còn lấy bitmap từ hàm này và tạo handle; bỏ `DrawString`, `StringFormat` và font khỏi tray. Cập nhật XML doc mô tả icon mới và quyền sở hữu bitmap trả về.

- [ ] **Step 4: Chạy test tray**

Run lại lệnh Step 2.

Expected: test pixel mới và toàn bộ test lifecycle cũ PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Tray/TrayIconSource.cs ReTran.Tests/Tray/TrayIconSourceTests.cs
git commit -m "feat: use folded A mark for tray icon"
```

### Task 3: Thay R trên chip menu nổi thu gọn

**Files:**
- Modify: `ReTran.App/Services/FloatingMenu/FloatingMenuRenderer.cs`
- Test: `ReTran.Tests/FloatingMenu/FloatingMenuRendererTests.cs`

**Interfaces:**
- Consumes: `BrandMarkRenderer.Draw(graphics, bounds)` từ Task 1.
- Produces: chip 44×44 giữ nguyên nền/viền/hit-test; monogram nằm trong `RectangleF(px + 7, py + 7, pw - 14, ph - 14)`.

- [ ] **Step 1: Đổi test cũ thành kỳ vọng brand và thêm test margin**

Đổi `Collapsed_ChipDrawn_WithAccentGlyph` thành `Collapsed_ChipDrawn_WithBrandMark`: trong vùng trung tâm 28×28 phải có >20 pixel Amber và >20 pixel Ivory; không còn assert teal `R`. Thêm `Collapsed_MarginIsTransparent` kiểm tra góc ngoài shadow margin alpha 0.

- [ ] **Step 2: Chạy test và xác nhận RED**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "FloatingMenuRendererTests"`

Expected: `Collapsed_ChipDrawn_WithBrandMark` FAIL trên glyph R cũ.

- [ ] **Step 3: Gọi shared renderer trong DrawChip**

Thay đoạn đo và vẽ font `R` bằng `BrandMarkRenderer.Draw` với bounds ở Interfaces. Xóa `ChipGlyphSize`, bỏ tham số `format`/`tickBrush` không còn dùng khỏi `DrawChip` và call site; không sửa `FloatingMenuLayout`, panel background hoặc hit-test. Cập nhật doc comment song ngữ của `DrawChip`.

- [ ] **Step 4: Chạy test renderer**

Run lại lệnh Step 2.

Expected: toàn bộ test PASS, gồm brand colors, transparent margin và các test expanded menu.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/FloatingMenu/FloatingMenuRenderer.cs ReTran.Tests/FloatingMenu/FloatingMenuRendererTests.cs
git commit -m "feat: use folded A mark for collapsed menu"
```

### Task 4: Tạo MAUI app icon từ bản N đầy đủ

**Files:**
- Modify: `ReTran.App/Resources/AppIcon/appicon.svg`
- Modify: `ReTran.App/Resources/AppIcon/appiconfg.svg`
- Modify: `ReTran.App/ReTran.App.csproj:42`
- Test: `ReTran.Tests/Branding/BrandAssetTests.cs`

**Interfaces:**
- Consumes: hình N đã duyệt trong spec.
- Produces: icon nền/foreground 456×456 và `MauiIcon Color="#07201E"`; foreground chỉ gồm vector, không text/font.

- [ ] **Step 1: Viết asset test thất bại**

Tạo `BrandAssetTests` với helper đi từ `AppContext.BaseDirectory` lên tới thư mục chứa `ReTran Project.slnx`. Test `MauiIcon_UsesFoldedABrandAssets` đọc hai SVG và csproj, assert: cả SVG có `viewBox="0 0 456 456"`; nền và `MauiIcon` dùng `#07201E`; foreground có `#EFEAD9` và `#FFB020`; foreground không có phần tử `<text>` và không còn `#512BD4`.

- [ ] **Step 2: Chạy test và xác nhận RED**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "BrandAssetTests"`

Expected: FAIL trên icon `.NET`/màu tím hiện tại.

- [ ] **Step 3: Thay SVG và màu MauiIcon**

`appicon.svg` chỉ giữ nền Ink 456×456. `appiconfg.svg` dùng đúng geometry 128×128 ở Global Constraints, gồm cả ba mốc giờ, đặt trong `<g transform="translate(31.6 24.8) scale(3.2)">` để toàn bộ nội dung nằm trong vùng an toàn trung tâm của adaptive icon. Đổi `MauiIcon Color` sang `#07201E`; không sửa `MauiSplashScreen`.

- [ ] **Step 4: Chạy asset test**

Run lại lệnh Step 2.

Expected: PASS và cả hai SVG parse được bằng `XDocument`.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Resources/AppIcon/appicon.svg ReTran.App/Resources/AppIcon/appiconfg.svg ReTran.App/ReTran.App.csproj ReTran.Tests/Branding/BrandAssetTests.cs
git commit -m "feat: replace default MAUI icon with ReTran mark"
```

### Task 5: Đưa monogram vào topbar trước chữ ReTran

**Files:**
- Modify: `ReTran.App/Components/Layout/MainLayout.razor:15-20`
- Modify: `ReTran.App/vite-project/src/css/deck.css:99-130`
- Modify: `ReTran.App/wwwroot/build/` (output do `npm run build` tạo lại)
- Test: `ReTran.Tests/Branding/BrandAssetTests.cs`

**Interfaces:**
- Consumes: bản N rút gọn cùng màu và geometry với `BrandMarkRenderer`.
- Produces: `<svg class="rt-brand-mark" viewBox="0 0 128 128" aria-hidden="true">` kích thước 22×22 trước `.rt-brand-name`; chip Live/Stopped vẫn giữ nguyên.

- [ ] **Step 1: Viết markup test thất bại**

Thêm `Topbar_UsesSvgBrandMark_AndKeepsRunStatus` vào `BrandAssetTests`: đọc `MainLayout.razor` và `deck.css`; assert có SVG brand viewBox 128×128, CSS width/height 22px, không còn `.rt-brand-mark i` hoặc `.is-live .rt-brand-mark`, và markup vẫn chứa `deck.chip.live` cùng `deck.chip.stopped`.

- [ ] **Step 2: Chạy test và xác nhận RED**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "BrandAssetTests"`

Expected: test topbar FAIL trên dấu chấm 16×16 hiện tại.

- [ ] **Step 3: Thay markup và CSS**

Thay `<span class="rt-brand-mark"><i></i></span>` bằng SVG inline có nền Ink bo góc, vòng cung Amber và dải A mềm rút gọn không tick. CSS chỉ giữ kích thước 22×22, `display:block`, `flex:0 0 22px`; xóa rule dấu chấm và animation brand cũ. Không thay đổi markup/chip trạng thái bên phải.

- [ ] **Step 4: Chạy test và build frontend**

Run: lệnh Step 2, sau đó `npm run build` trong `ReTran.App/vite-project`.

Expected: asset tests PASS; `tsc && vite build` thành công và output `wwwroot/build` được cập nhật.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Components/Layout/MainLayout.razor ReTran.App/vite-project/src/css/deck.css ReTran.App/wwwroot/build ReTran.Tests/Branding/BrandAssetTests.cs
git commit -m "feat: add ReTran brand mark to topbar"
```

### Task 6: Preview thực tế và kiểm chứng toàn bộ

**Files:**
- Modify: `Docs/superpowers/specs/2026-11-24-retran-folded-a-brand-icon-design.md` (đổi trạng thái thành đã duyệt)
- Create outside repo: preview PNG dưới `%LOCALAPPDATA%\Temp\opencode\retran-brand-preview\`

**Interfaces:**
- Consumes: toàn bộ output Task 1–5.
- Produces: kết quả test/build và preview 32/44 px để rà trực quan; không thêm dependency hoặc preview generator vào sản phẩm.

- [ ] **Step 1: Chạy toàn bộ test và build**

Run:
```bash
dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"
dotnet build "ReTran Project.slnx"
```

Expected: cả hai lệnh exit 0, không test failed hoặc build error.

- [ ] **Step 2: Tạo preview native từ renderer thật**

Sau build, dùng script PowerShell tạm dưới `%LOCALAPPDATA%\Temp\opencode` để nạp assembly App, gọi `TrayIconSource.RenderBitmap()` và `FloatingMenuRenderer.DrawMenu` cho trạng thái collapsed; lưu PNG tray 32×32 và chip 44×44 ở thư mục preview. Không tạo file script trong repo.

Expected: hai PNG tồn tại, đúng kích thước và có alpha.

- [ ] **Step 3: Rà preview và bản SVG**

Đọc hai PNG bằng công cụ xem ảnh; rà monogram ở kích thước thật, nền trong suốt và màu. Đưa bản N đầy đủ/rút gọn lên một màn hình Visual Companion cuối nếu cần đối chiếu với lựa chọn đã duyệt. Nếu có lỗi hình, quay lại task sở hữu renderer tương ứng và thêm regression test trước khi sửa.

- [ ] **Step 4: Hoàn tất tài liệu và review**

Đổi trạng thái spec thành `Đã duyệt`, ghi lại lệnh kiểm chứng trong báo cáo cuối. Với native execution, nhờ một reviewer mới rà toàn bộ diff theo spec; với subagent execution, thực hiện whole-branch review theo workflow đã chọn.

- [ ] **Step 5: Commit tài liệu nếu user cho phép các commit trong plan**

```bash
git add Docs/superpowers/specs/2026-11-24-retran-folded-a-brand-icon-design.md Docs/superpowers/plans/2026-11-24-retran-folded-a-brand-icon.md
git commit -m "docs: record ReTran brand icon design and plan"
```
