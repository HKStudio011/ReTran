# ReTran — Checklist E2E cập nhật bản release (manual)

Checklist chạy tay trước/at mỗi lần phát hành bản cập nhật. Không chạy được local tự động (cần GitHub release thật + app đã cài) → ghi lại kết quả từng mục tại đây hoặc vào release notes.

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
   - Verify assets: `ReTran-<version>-win-x64.zip`, `.zip.sig`, `SHA256SUMS.txt`; zip chứa đủ `ReTran.App.exe`, `ReTran.Core.exe`, `ReTran.Updater.exe` + `ReTran.Core.runtimeconfig.json` / `ReTran.Core.deps.json`.
3. **Happy path cập nhật**
   - Cài bản zip cũ hơn (bản trước) → mở app → app check nền (định kỳ ~5 s/lần poll theo cấu hình) → toast "Có bản cập nhật mới" → bấm "Cập nhật ngay" → modal progress → app tự tắt → updater cài → app mở lại báo version mới (`AppVersionFormatter`).
4. **Negative cases (fail-closed)**
   - Xóa file `.sig` trên release → client **từ chối** update (không có chữ ký = không cài).
   - Zip bị tampered (đổi 1 byte) → client báo `VerifyFailed` (sai chữ ký ECDSA), không cài.

## 2. Kết quả chạy (điền khi chạy)

| Ngày | Phiên bản | Mục 1 | Mục 2 | Mục 3 | Mục 4 | Ghi chú |
|---|---|---|---|---|---|---|
| — | — | chưa chạy | chưa chạy | chưa chạy | chưa chạy | local không chạy được E2E này (cần release thật) |

## 3. Ghi chú

- Local đã verify thay được (Task 9): build 0 lỗi, 223/223 test PASS, publish App + Core + copy vào cùng output (đủ 3 exe + runtimeconfig/deps Core, pdb loại), app Debug khởi động không lỗi DI. Các mục trên **bắt buộc** chạy trên release thật + máy có app đã cài.
