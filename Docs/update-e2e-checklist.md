# ReTran — Checklist E2E cập nhật bản release (manual)

Checklist chạy tay trước mỗi lần phát hành bản cập nhật. Không chạy được local tự động (cần GitHub release thật + app đã cài) → ghi lại kết quả từng mục tại đây hoặc vào release notes.

Nguồn: spec `Docs/superpowers/specs/2026-10-14-version-update-design.md` (§ layout zip, fail-closed verify) + plan `Docs/superpowers/plans/2026-10-14-version-update.md`.

## 0. Hành động của user còn thiếu (outstanding)

- [ ] **(USER)** Tạo GitHub environment `release` (Settings → Environments → `release`) và thêm secret **`UPDATE_SIGNING_KEY`** = ECDSA P-256 PKCS#8 Base64 (private key sinh ở Task 1, giữ trong tay user — KHÔNG commit vào repo).
  - Workflow `release.yml` **fail-closed**: thiếu secret này thì bước "Sign zip" throw → không upload zip/.sig. Bắt buộc làm trước lần phát hành đầu tiên.
  - Khuyến nghị: bật required reviewers cho environment `release` để gate tay trước khi build.

## 1. Checklist E2E manual

1. **(USER — làm một lần)** Tạo environment `release` + secret `UPDATE_SIGNING_KEY` (xem mục 0).
2. **Bump version + tag + publish release**
   - Bump `<Version>` / `<ApplicationDisplayVersion>` trong `ReTran.App/ReTran.App.csproj`.
   - Tag `v<version>` (khớp `ApplicationDisplayVersion` — workflow có bước verify, sai là fail sớm).
   - Publish release trên GitHub → workflow chạy: publish App (self-contained win-x64) → publish Core + copy vào output → verify 3 exe → zip → ký `.sig` (ECDSA) → SHA256SUMS → attest → upload assets.
   - Verify assets: `ReTran-v<version>-win-x64.zip` (workflow zip theo `github.ref_name` — tên asset gồm `v`, ví dụ `ReTran-v0.2-win-x64.zip`), `.zip.sig`, `SHA256SUMS.txt`; zip chứa đủ `ReTran.App.exe`, `ReTran.Core.exe`, `ReTran.Updater.exe` + `ReTran.Core.runtimeconfig.json` / `ReTran.Core.deps.json`.
   - Verify payload OCR/setup trong zip: đủ source sidecar `ReTran.OCR/src/**`, `ReTran.OCR/pyproject.toml`, `ReTran.OCR/uv.lock` và `runtimes/win-x64/uv.exe`. Giải nén ở thư mục sạch phải giữ đúng các đường dẫn tương đối này để first-run setup dùng `uv sync --frozen` được.
   - **Lần phát hành đầu tiên bắt buộc hai build/release:** build A làm bản cũ để cài đặt, sau đó bump version và build B làm bản mới; chạy happy path cập nhật thật từ A lên B. Chỉ build một zip rồi cài trực tiếp **không** đủ điều kiện đánh dấu E2E update đạt.
3. **Happy path cập nhật**
   - Cài bản zip cũ hơn (bản trước) → mở app → app check nền sau ~5 s (lần đầu) → lặp mỗi 24 h → toast "Có bản cập nhật mới" → bấm "Cập nhật ngay" → modal progress → app tự tắt → updater cài → app mở lại báo version mới (`AppVersionFormatter`).
4. **Negative cases (fail-closed)**
   - Xóa file `.sig` trên release → client **từ chối** update (không có chữ ký = không cài).
   - Zip bị tampered (đổi 1 byte) → client báo `VerifyFailed` (sai chữ ký ECDSA), không cài.
   - Download/verify fail → services chưa bị dừng, app không restart. Dispose trong stop-services → teardown im lặng, không restart.
   - Stop-services fail/timeout hoặc updater launch trả false/throw **sau khi services đã dừng** → app bắn lỗi UI tương ứng, khởi chạy lại **ReTran.App hiện tại** (không phải updater), rồi Quit process cũ. Nếu restart fail, log Error và vẫn Quit; nếu Quit fail, process cũ giữ trạng thái terminal thay vì quay lại UI healthy với services đã dispose.
   - Happy path vẫn updater → Quit; Quit-after-updater không khởi chạy thêm ReTran.App từ recovery (chỉ updater mở app sau khi cài).

## 2. Kết quả chạy (điền khi chạy)

| Ngày | Phiên bản | Mục 1 | Mục 2 | Mục 3 | Mục 4 | Ghi chú |
|---|---|---|---|---|---|---|
| 2026-11-26 | 0.1.0 / local `b72ec20` | chưa chạy — USER | chưa chạy live release; local clean-package PASS | chưa chạy A→B | chưa chạy trên live release | Không dùng secret/tag/push; chi tiết local ở §2.1, automated fail-closed tests PASS nhưng không thay negative E2E. |

### 2.1 Verification local Task 5 — 2026-11-26

- **Automated gate:** `dotnet test` exit 0: 1147 total, **1143 succeeded, 0 failed, 4 skipped**. `dotnet build "ReTran Project.slnx" -c Release` exit 0, 0 error / 34 warning `xUnit1051` trong test files. `npm run build` exit 0 sau `npm ci` từ lockfile hiện có; lần chạy đầu fail vì fresh worktree chưa có `node_modules`/`tsc`.
- **Clean package:** clean self-contained publish App + Core đều exit 0. `Tools/Release/Test-ReleasePackage.ps1 -ExpectedTag v0.1.0` chạy hai lần trên cùng output đều exit 0; mỗi lần giữ nguyên **692 App payload hashes**, không file App nào bị thêm/xoá/đổi hash và `Llm` merge idempotent. `ReTran.Core.pdb` không được merge.
- **Dependency/payload:** tám hash kiểm riêng (`System.Drawing.Common`, Logging.Abstractions, sáu Vortice/SharpGen) giống nhau trước/sau merge. Đủ và khớp hash với repo cho **12 file** `ReTran.OCR/src/**`, `pyproject.toml`, `uv.lock`; `runtimes/win-x64/uv.exe` tồn tại, 31.803.184 bytes, SHA-256 `1791D7A9ED5B6DF13CCF80F102E025C333010D96326B4FF0262AA3B578835C5A`.
- **Native smoke thật:** `ReTran.Core.exe capture list-monitors` exit 0, liệt kê `DISPLAY1`/`DISPLAY2`; `ReTran.Updater.exe` no-args in usage và exit 1 đúng contract.
- **OCR delegate + host review:** review toàn diff `d1d11b7..b72ec20` theo update/release background; không tìm thấy Critical/Important. Preview bỏ qua script vì `.gitignore` `[Rr]elease/`, nên script được truyền explicit vào `ocr delegate rule` và host review cùng 15 file còn lại.
- **Bốn environment skips, không tính là pass:** `Ocr_RealSidecar_EndToEnd_PrintsSpotsJson`, `OcrLoop_RealSidecar_FakeSource_EmitsTwoJsonLines`, `DisplayCatalog_MatchesPythonCatalog`, `Sidecar_Device_ReturnsGpuDeviceAndCapability`; worktree thiếu `ReTran.OCR/.venv/Scripts/python.exe`.

### 2.2 Phần chưa verify / blocker trước release thật

- Chưa tạo/xác minh GitHub environment `release` và `UPDATE_SIGNING_KEY`; không ký bằng secret thật, không chạy GitHub Actions release, upload hoặc attestation.
- Chưa chạy hai build/release A→B cho happy path, cũng chưa xoá `.sig`/tamper zip trên release thật cho negative E2E. Các kịch bản terminal recovery ở mục 1 cũng mới chỉ có automated regression, chưa chạy live E2E. Các mục này vẫn **chưa chạy**, không được suy ra PASS từ local package smoke hoặc unit tests.
- `npm ci` báo 2 high severity audit warnings trong dependency tree hiện có; nhánh không thay frontend dependency và verification không chạy `npm audit fix`.

## 3. Ghi chú

- Kết quả local trên chỉ xác nhận automated gates, clean package và native smoke. Các mục live E2E tại mục 1 **bắt buộc** chạy trên release thật + máy có app đã cài trước khi đánh dấu E2E hoàn tất.
