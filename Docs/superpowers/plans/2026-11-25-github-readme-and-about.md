# ReTran GitHub README and About Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trình bày ReTran trên GitHub bằng README song ngữ ngắn gọn, icon đã duyệt, yêu cầu chạy thật và metadata About có liên kết HKStudio011.

**Architecture:** Hai Markdown dùng cùng cấu trúc nhưng viết tự nhiên theo từng ngôn ngữ, với một SVG độc lập dựa trên geometry N đã duyệt. GitHub About được cập nhật qua `gh repo edit`, tách khỏi việc merge/push nhánh code vẫn đang tạm dừng.

**Tech Stack:** GitHub-flavored Markdown, SVG, GitHub CLI (`gh`).

**Spec:** `Docs/superpowers/specs/2026-11-25-github-readme-and-about-design.md`

## Global Constraints

- File: `README.md` (English), `README.vn.md` (Tiếng Việt), `Docs/design/logo/retran-icon.svg`.
- Nội dung chỉ gồm giới thiệu ngắn, requirements, features và About; không build/test/code/config/architecture/performance hoặc liên kết tài liệu kỹ thuật.
- Không slogan, emoji, khẳng định quảng cáo hoặc badge kỹ thuật không cần thiết.
- Requirements phải nêu: Windows 10/11 64-bit; 2 GB CPU / 8 GB GPU; Internet cho first-run setup; không cần cài riêng .NET/Python; NVIDIA tùy chọn, driver ≥576.02 cho GPU, máy khác dùng CPU.
- Không mô tả virtual camera hoặc exclusive-fullscreen game hook.
- Không merge/push nhánh icon; không sửa production code/test/timing test.
- Mọi thay đổi GitHub ngoài README được giới hạn ở repository `HKStudio011/ReTran`: description và homepage đã duyệt.

## Review Focus

- **Yêu cầu môi trường sai:** người dùng release không bị yêu cầu SDK/Python — kiểm tra bằng required/forbidden text ở Task 1.
- **Claim tính năng chưa có:** không có virtual camera/game hook — kiểm tra forbidden text ở Task 1.
- **Hai ngôn ngữ lệch cấu trúc:** cả hai phải có đủ icon, intro, requirements, features và About — validation Task 1.
- **Link tương đối hỏng:** `README.md` ↔ `README.vn.md` và logo phải resolve trên GitHub — validation Task 1.
- **Metadata sai repo/URL:** chỉ `HKStudio011/ReTran`, homepage chính xác — verify Task 2 bằng `gh repo view`.

---

### Task 1: Tạo logo README và hai README song ngữ

**Files:**
- Modify: `README.md`
- Create: `README.vn.md`
- Create: `Docs/design/logo/retran-icon.svg`
- Create outside repo: script kiểm tra Markdown dưới `%LOCALAPPDATA%\Temp\opencode\retran-readme-check\`

**Interfaces:**
- Consumes: geometry N trong `ReTran.App/Resources/AppIcon/appiconfg.svg` và nội dung spec.
- Produces: hai README cùng cấu trúc và logo SVG 128×128 được README tham chiếu bằng `Docs/design/logo/retran-icon.svg`.

- [ ] **Step 1: Tạo validation thất bại trước khi viết nội dung**

Tạo script PowerShell tạm đọc hai README và fail nếu thiếu bất kỳ điều kiện nào:
- `README.md`: liên kết `README.vn.md`, logo, `<h1 align="center">ReTran</h1>`, `## Requirements`, `## Features`, `## About`, `2 GB`, `8 GB`, `576.02`, `https://hkstudio011.github.io/`.
- `README.vn.md`: liên kết `README.md`, cùng logo/title, `## Yêu cầu tối thiểu`, `## Chức năng`, `## Giới thiệu`, các số liệu và URL tương tự.
- Cả hai không chứa `dotnet build`, `dotnet test`, `npm`, `UBIQUITOUS`, `virtual camera`, `exclusive-fullscreen`, `🚀`, `powerful`, `seamless`.
- `Docs/design/logo/retran-icon.svg` tồn tại, parse XML được và có `viewBox="0 0 128 128"`.

Run script. Expected: FAIL vì `README.vn.md` và logo chưa tồn tại.

- [ ] **Step 2: Tạo logo độc lập**

Dùng nền Ink bo góc và đúng path/màu của N đầy đủ trên viewBox 128×128, gồm ba mốc giờ; không thêm chữ vào SVG hoặc thiết kế lại hình.

- [ ] **Step 3: Viết README English và Vietnamese**

Mỗi file có:
- Language switch ở đầu, icon 128×128 và title ReTran ở giữa.
- Một câu intro tự nhiên: ReTran nhận diện chữ trên màn hình, dịch và hiển thị bản dịch trên màn hình hoặc trong OBS.
- Requirements và features đúng Global Constraints, viết ngắn bằng bullet, không giải thích cơ chế/code.
- About với `HKStudio011: https://hkstudio011.github.io/` và liên kết ngôn ngữ còn lại.

Không thêm mục Download/Build/Test/Docs ngoài thiết kế đã duyệt.

- [ ] **Step 4: Validate và xem trước bằng GitHub Markdown renderer**

Run lại script Step 1: Expected PASS toàn bộ required/forbidden/XML/link checks.

Dùng `gh api markdown -f mode=gfm` để render hai file ra HTML tạm dưới thư mục preview ngoài repo; mở và rà bố cục, heading, bullet, icon reference và language links. Không cài Markdown renderer mới.

- [ ] **Step 5: Commit**

```bash
git add README.md README.vn.md Docs/design/logo/retran-icon.svg
git commit -m "docs: add bilingual ReTran README"
```

### Task 2: Cập nhật GitHub About và hoàn tất tài liệu

**Files:**
- Modify: `Docs/superpowers/specs/2026-11-25-github-readme-and-about-design.md`
- Modify: `Docs/superpowers/plans/2026-11-25-github-readme-and-about.md`
- External: repository metadata `HKStudio011/ReTran` only.

**Interfaces:**
- Consumes: nội dung Task 1 và description/homepage đã duyệt.
- Produces: About sidebar có mô tả ngắn và website; spec ở trạng thái đã duyệt.

- [ ] **Step 1: Ghi lại trạng thái metadata trước thay đổi**

Run:
```bash
gh repo view HKStudio011/ReTran --json description,homepageUrl
```

Ghi giá trị hiện tại vào report. Không thay đổi repo khác.

- [ ] **Step 2: Cập nhật description và homepage**

Run:
```bash
gh repo edit HKStudio011/ReTran --description "Real-time translation for text on your screen, with OBS overlay support." --homepage "https://hkstudio011.github.io/"
```

Không thêm topics hoặc thay đổi visibility/settings khác.

- [ ] **Step 3: Verify metadata**

Run lại lệnh Step 1. Expected:
- `description`: đúng verbatim câu ở Step 2.
- `homepageUrl`: `https://hkstudio011.github.io/`.

- [ ] **Step 4: Đổi trạng thái spec và commit tài liệu**

Đổi spec `Trạng thái: chờ duyệt` thành `Trạng thái: Đã duyệt`. Commit:

```bash
git add Docs/superpowers/specs/2026-11-25-github-readme-and-about-design.md Docs/superpowers/plans/2026-11-25-github-readme-and-about.md
git commit -m "docs: record GitHub README design and plan"
```

- [ ] **Step 5: Review cuối và giữ nhánh**

Rà diff của hai commit theo spec, xác nhận `git status --short` sạch. Không merge/push; giữ nguyên worktree và timing-test blocker của nhánh icon cho quyết định tiếp theo của user.
