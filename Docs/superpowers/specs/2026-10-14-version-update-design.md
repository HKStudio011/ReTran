# Spec: Version & Auto-Update (port cơ chế từ router-balancing)

- **Ngày:** 2026-10-14
- **Trạng thái:** Draft — chờ user review
- **Phạm vi:** App (subsystem `Services/Update/` + UI Settings/Modal/toast/footer), project mới `ReTran.Updater`, version source-of-truth ở csproj, CI (`ci.yml` + `release.yml`) + ký ECDSA, test port.
- **Nguồn:** port subsystem version/update của `D:\Code\router-balancing` (spec `2026-10-11-version-update-design.md` bên đó) + khảo sát ReTran 2026-10-14 (brainstorming session).

## 1. Bối cảnh

ReTran hiện **chưa có bất kỳ infrastructure nào** cho version/release/update:

- Version phân mảnh 4 nguồn, không đồng bộ: `ReTran.App.csproj` (`ApplicationDisplayVersion=1.0`, không khai `<Version>` → assembly auto-generate `1.0.0.0`), `ReTran.Core/Program.cs:20` (`const "0.1.0"`), `vite-project/package.json` (`0.0.0`), `ReTran.OCR/pyproject.toml` (`0.1.0`).
- Chưa từng publish (chỉ `dotnet build`), không có `.github/`, không có git tag, không có release.
- `NavMenu.razor:23` hiển thị assembly version thô.
- App unpackaged (`WindowsPackageType=None`), chạy từ repo; `CoreLocator` nhặt Core từ `bin\Debug` bằng cách walk lên repo root.
- Không có HttpClient outbound nào ở App (pattern test sẵn: fake `HttpMessageHandler` như `GoogleWebTranslatorTests`).

router-balancing đã có hệ thống hoàn chỉnh: check GitHub Releases (5s + 24h) → download zip → verify ECDSA fail-closed → updater exe self-contained từ shadow-copy → drain + snapshot trước khi restart; 10 file test; `release.yml` sign + upload. Port cơ chế đó sang ReTran với các адапt được nêu tại §6, §7.

## 2. Quyết định đã chốt (brainstorming 2026-10-14)

| # | Câu hỏi | Lựa chọn |
|---|---|---|
| 1 | Phạm vi zip update | **Chỉ phần C#** — `ReTran.App.exe` + `ReTran.Core.exe` self-contained. Python sidecar/models không nằm trong package (user cài riêng, trỏ `CoreConfig.PythonExe`). |
| 2 | Kênh phát hành | **GitHub Releases công khai** — repo `HKStudio011/ReTran` (đã xác nhận từ git remote), client check `/releases/latest`. |
| 3 | Chữ ký | **Giữ fail-closed ECDSA P-256** — zip thiếu `.sig`/sai key → refuse update. Keypair **mới riêng cho ReTran** (tách khỏi key router-balancing). |
| 4 | Approach | **A — port trực tiếp mirror cấu trúc**: `Services/Update/` + project `ReTran.Updater` + port test + 2 workflow. Không dùng framework bên thứ ba (Velopack/AutoUpdater.NET). |
| 5 | Drain/queue snapshot | **Bỏ thay bằng stop-services** (§6.2) — ReTran không có request đang bay cần snapshot; translation cache đã bền trong SQLite. |
| 6 | Layout publish | **Self-contained directory, `win-x64`** — giống router-balancing; `VerifySelfContained` của updater port nguyên. |

## 3. Component architecture

```
ReTran.App/Services/Update/              (mới — port từ router-balancing/src/RouterBalancing.Core/Update/)
├── AppUpdateModels.cs                   # DTO GitHub release (tag_name/html_url/body/assets[].name/browser_download_url)
│                                        #   + UpdateState + UpdateError
├── GitHubAppUpdateService.cs            # check/download/verify/launch-updater. Constants:
│                                        #   RepoOwner="HKStudio011", RepoName="ReTran",
│                                        #   UpdaterFileName="ReTran.Updater.exe", UpdateFolderName="ReTran_Update"
├── UpdateCheckService.cs                # background loop (5s delay + PeriodicTimer 24h), overlap-guard,
│                                        #   events StateChanged + UpdateFound (chỉ transition no→yes)
├── UpdateCoordinator.cs                 # StartUpdateAsync — điều phối download → verify → stop-services → launch → quit (§6)
├── PackageSignatureVerifier.cs          # ECDSA P-256/SHA-256, public key ReTran nhúng (ReleasePublicKeyBase64)
└── AppVersionFormatter.cs               # port nguyên: 1.0.0.0→"1.0", tối thiểu 2 phần

ReTran.Updater/                          (mới — console project net10.0, plain FDD csproj như router-balancing;
│                                        #   self-contained nhờ `--self-contained` truyền global khi publish App)
├── ReTran.Updater.csproj                # OutputType=Exe, TFM net10.0, AssemblyName ReTran.Updater
├── Program.cs                           # port: parse args → WaitForExit(pid, 60s) → staging → verify → copy → relaunch → cleanup
└── UpdateStager.cs                      # port: ExtractToStaging / VerifySelfContained / WaitUntilWritable / CopyDirectory

ReTran.App/ReTran.App.csproj             # ProjectReference ReTran.Updater, OutputItemType=Content,
                                         #   CopyToOutputDirectory → Updater.exe nằm cạnh App.exe
ReTran.Tests/Update/                     (mới — port 6 nhóm test, §10)
.github/workflows/ci.yml                 (mới — build + test)
.github/workflows/release.yml            (mới — verify tag → publish → sign → upload)
scripts/generate-update-signing-key.ps1  (mới — keygen: private → GH secret, public → verifier)
```

**DI wiring** (`MauiProgram.cs`, theo style singleton hiện có):

```csharp
builder.Services.AddSingleton<GitHubAppUpdateService>();
builder.Services.AddSingleton<UpdateCoordinator>();
builder.Services.AddSingleton<UpdateCheckService>();
```

`UpdateCheckService.Start()` gọi từ `App.xaml.cs` ctor (giống `App.xaml.cs:84` của router-balancing) — best-effort, nuốt exception.

**CoreLocator** (`ReTran.App/Core/CoreLocator.cs`) — đổi thứ tự tìm Core exe:

1. env `RETRAN_CORE_EXE` (giữ nguyên, test/dev override),
2. **mới:** sibling `ReTran.Core.exe` cạnh `AppContext.BaseDirectory` — chỉ tồn tại trong layout publish (bin của App không chứa Core exe),
3. repo-walk `bin\Debug` (fallback dev như hiện nay).

## 4. Version source of truth

- `ReTran.App.csproj` — thêm/chuẩn hóa:
  ```xml
  <Version>0.1.0.0</Version>                          <!-- 4-part, assembly version — nguồn duy nhất App đọc runtime -->
  <ApplicationDisplayVersion>0.1.0</ApplicationDisplayVersion>  <!-- semver 3 phần, phải khớp tag release -->
  <ApplicationVersion>1</ApplicationVersion>           <!-- Android int, không liên quan update -->
  ```
- Runtime read giữ pattern router-balancing: `Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0"`.
- `NavMenu.razor:23`: đổi sang `AppVersionFormatter.Format(...)` (bỏ raw `ToString()`).
- `ReTran.Core/Program.cs` `Version` const và `pyproject.toml`/`package.json` **giữ nguyên** — không đồng bộ cứng; App + Core ship cùng zip nên version App là master cho update.
- Quy trình release: **bump csproj → tag `v<ApplicationDisplayVersion>` → publish release**. `release.yml` fail nếu tag ≠ `ApplicationDisplayVersion` (chống bug `v1.3.0` của router-balancing — tag bị thiếu bump, binary vẫn tự report 1.2.2 → lặp update vô hạn).

## 5. Update check (giữ nguyên contract router-balancing)

- **Nguồn:** `GET https://api.github.com/repos/HKStudio011/ReTran/releases/latest`, header `User-Agent: Retran/<version>`. Không manifest custom, không channel, không custom feed.
- **Guard:** `OperatingSystem.IsWindows()` — off-Windows mọi thứ là noop.
- **Manifest tối thiểu:** `tag_name`, `html_url`, `body`, `assets[].name`, `assets[].browser_download_url`. Asset chọn: zip đầu tiên không chứa `symbols`; **bắt buộc** asset kèm `<zip>.sig` — thiếu → refuse (fail-closed).
- **So sánh version:** numeric per-component, zero-pad, không phải full semver (`v1.1` ≡ `1.1.0.0`); chỉ `> 0` (không downgrade).
- **Loop:** start → delay 5s → `PeriodicTimer(24h)`; `SemaphoreSlim(1,1)` + `Wait(0)` chặn overlap giữa manual check và background loop.
- **Events:** `StateChanged` (badge re-render), `UpdateFound` — chỉ fire transition no→yes (mỗi lần phát hiện 1 toast, không lặp mỗi 24h).
- **Lỗi:** background check fail → giữ nguyên state cũ, im lặng. Manual check fail → toast `update.checkFailed`.
- **Dev guard:** `#if DEBUG` → `Start()` noop + UI ẩn badge/nút "Cập nhật ngay" — tránh "update" ghi đè `bin\Debug` khi chạy từ repo. (Release build chạy từ repo vẫn cho phép — chấp nhận được, document trong README release notes.)

## 6. Install flow (phần адапт nhiều nhất)

### 6.1 Trigger

"Cập nhật ngay" (Settings card hoặc toast) → `UpdateCoordinator.StartUpdateAsync(progress, ct)`.

### 6.2 Sequence

```
1. Download    %TEMP%\ReTran_Update\<owner-guid>\<attempt-id>\<asset-name>.zip — owner GUID tạo một lần
               cho mỗi service/process, attempt-id tăng đơn điệu trong service; zip/signature/cleanup của
               hai instance không bao giờ dùng chung path; buffer 80 KB; progress % chỉ báo khi số nguyên %
               tăng; DownloadedZipPath = null ngay khi bắt đầu attempt (zip dở dang không bao giờ được cài)
2. Verify      ECDSA P-256/SHA-256 qua PackageSignatureVerifier — fail → xóa zip, UpdateError.VerifyFailed
3. Stop services (best-effort, timeout ~10s/bước bao phủ cả invocation đồng bộ trên thread pool và
   asynchronous wait, lỗi → UpdateError.StopServicesFailed):
   a. PipelineController.StopAsync()    — core.stop nếu pipeline đang chạy (idempotent)
   b. CoreProcessClient.DisposeAsync()  — Kill(entireProcessTree: true) → Core + Python sidecar con đều chết
   c. OverlayFeedServer.DisposeAsync()  — giải phóng 127.0.0.1:17863
4. Launch updater   shadow-copy ReTran.Updater.exe → %TEMP%\ReTran_Updater\v{pid}-{tick} (kèm runtime
                    từ deps.json — port CopySelfContainedRuntime) → Process.Start với ArgumentList:
                    --zip <zip> --pid <pid> --app-dir <dir>  (KHÔNG nối string — bug quote backslash cuối)
5. Quit        Application.Current.Quit()  (ReTran không có tray-close → không cần MarkReallyExiting)
```

- **Không** DrainGate, **không** queue snapshot: translation cache đã bền trong SQLite; request đang bay bị drop là chấp nhận được.
- Nhánh cancel/fail trước stop-services gọi `_runId`-guarded cleanup (giữ pattern chống stale-run);
  `IsFinalizing = true` trước bước 3 (ẩn Cancel). Dispose trong stop-services vẫn teardown im lặng,
  không restart; download/verify fail chưa dừng service nên cũng không restart.
- **Terminal recovery sau post-stop failure:** nếu bước 3 throw/timeout hoặc bước 4 trả false/throw, bắn
  `Failed` để UI báo lỗi rồi khởi chạy **ReTran.App hiện tại** bằng `Environment.ProcessPath`,
  `ProcessStartInfo` (`WorkingDirectory = AppContext.BaseDirectory`, không shell command string), sau đó
  `Application.Current.Quit()` cho process cũ. Không khởi chạy updater ở nhánh recovery và không cố hồi
  sinh DI singleton đã dispose. Nếu restart fail, log Error và vẫn Quit process đã teardown; nếu Quit cũ
  fail, giữ trạng thái finalizing thay vì trả UI về trạng thái healthy giả. Happy path updater→Quit và
  Quit-after-updater không bao giờ đi qua current-app recovery.

### 6.3 UpdateError

```csharp
public enum UpdateError { DownloadFailed, VerifyFailed, StopServicesFailed, InstallFailed }
```

(Bỏ `DrainFailed`/`SnapshotFailed` của router-balancing, thêm `VerifyFailed`.)

## 7. Updater behavior (port, giữ nguyên các fix bug)

`ReTran.Updater.exe` chạy từ **shadow copy** (self-copy sang `%TEMP%` + copy runtime từ `deps.json`) — bằng không nó tự khóa file mình và extract-over-appDir thất bại (bug #4) hoặc `hostpolicy.dll not found` (bug #12).

```
argv phải đúng ["--zip", _, "--pid", _, "--app-dir", _]   — sai → stderr + exit 1
WaitForExit(parent, 60_000)                                 — MAUI Hybrid thoát chậm
1. ExtractToStaging(zip)      → %TEMP%\ReTran_Update\staging-<guid> (directory riêng cho từng extraction;
                                 không xóa staging của updater khác)
2. VerifySelfContained()      → runtimeconfig includedFrameworks ≠ rỗng + hostfxr.dll có mặt
                                (reject FDD zip — chống bug #5/#9: extract trộn file → app chết)
3. WaitUntilWritable(staging, appDir, 60s)  — probe File.Open(FileShare.None) từng file
4. best-effort backup ReTran.App.exe → .bak
5. CopyDirectory(staging, appDir, overwrite)
6. Process.Start(ReTran.App.exe, UseShellExecute, WorkingDirectory=appDir)
7. cleanup: .bak, zip, đúng staging-<guid> của extraction; CleanupSelfTempDir() → chuyển chính mình sang .old
error → stderr + "Press Enter" + exit 1
```

**Không rollback** nếu copy dở chừng (như router-balancing — đã chấp nhận trong spec nguồn); `.bak` chỉ để phát hiện hỏng, không tự phục hồi.

## 8. UI + i18n

| Vị trí | Nội dung |
|---|---|
| `Settings.razor` — Card "Cập nhật" mới | version hiện tại (`update.version`), badge "có bản mới", nút "Kiểm tra cập nhật" (giữ `wasAvailable` trước check để không double-toast), nút "Cập nhật ngay" → mở modal |
| `Components/Shared/UpdateModal.razor` (mới) | progress bar %, state "Đang chuẩn bị cài đặt…" ẩn Cancel, re-render qua `Coordinator.Changed` + `InvokeAsync`; không click-outside close |
| `MainLayout.razor` — toast minimal (ReTran chưa có toast system — port pattern local-state + fixed-position div) | `UpdateFound` → "Có bản cập nhật mới: {tag}"; `Failed(err)` → message theo nguyên nhân; `Cancelled` → "Đã hủy cập nhật." |
| `NavMenu.razor` footer | version qua `AppVersionFormatter` + badge nhỏ khi `StateChanged` có update |

Với post-stop failure, `Failed` được bắn trước current-app restart để toast nhận đúng `UpdateError`.
Modal của process cũ giữ `UpdateInProgress/IsFinalizing` cho tới khi Quit (không hiện lại nút Update/Cancel
như thể app vẫn healthy); instance mới khởi tạo lại DI services bình thường. Nếu restart/Quit fail, log
Error và trạng thái terminal vẫn được giữ thay vì cho phép tiếp tục dùng services đã dispose.

i18n: thêm key `update.*` (`update.title/version/badge/check/now/latest/available/downloading/preparing/cancel/cancelled/failed/error.*`) vào `I18nStrings.cs` — VI + EN, theo pattern dictionary key dạng chấm hiện có. Component subscribe `L.Changed` + `@implements IDisposable`.

DI: `Settings.razor`/`UpdateModal`/`MainLayout` `@inject` các service update; events marshal qua `InvokeAsync`.

## 9. Release workflow

### 9.1 `ci.yml`

`setup-dotnet` (net 10) → `dotnet workload install maui` → `dotnet build "ReTran Project.slnx"` → `dotnet test --project ReTran.Tests/ReTran.Tests.csproj`. (Chưa thêm npm/uv build — giữ gọn, thêm sau khi cần.)

### 9.2 `release.yml`

Trigger: `release: types [published]` (+ `workflow_dispatch` dry-run); môi trường `release` bảo vệ secrets. Steps:

1. **Verify tag == `ApplicationDisplayVersion`** (parse csproj) — mismatch → fail (mới, §4).
2. `dotnet publish ReTran.App -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained -c Release -o publish` — properties truyền CLI lan sang project reference nên `ReTran.Updater.exe` cũng tự self-contained vào output.
3. Hard-fail nếu thiếu `ReTran.App.exe` hoặc `ReTran.Updater.exe` trong output.
4. Optional Azure Trusted Signing (OIDC, nếu secret `AZURE_KEY_VAULT_URI` có) — port nguyên pattern, sign 2 exe.
5. Zip `ReTran-<tag>-win-x64.zip`.
6. **Sign zip ECDSA** — secret `UPDATE_SIGNING_KEY` (PKCS#8 Base64) bắt buộc; workflow throw nếu thiếu; tạo key 1 lần bằng `scripts/generate-update-signing-key.ps1` (private → secret ReTran repo, public → `PackageSignatureVerifier.ReleasePublicKeyBase64`).
7. `SHA256SUMS.txt` + `actions/attest-build-provenance` (client không dùng — chỉ verify tay).
8. `gh release upload <tag> ReTran-*.zip ReTran-*.zip.sig SHA256SUMS.txt --clobber` (chỉ khi trigger bởi release published).

## 10. Testing (port 6 nhóm test → `ReTran.Tests/Update/`)

| File test | Nội dung |
|---|---|
| `GitHubAppUpdateServiceTests` | check (manifest sample JSON), asset selection (bắt buộc `.sig`, loại `symbols`), so sánh version, download progress, refuse khi thiếu sig |
| `UpdateCoordinatorTests` | pipeline thứ tự stop-services → launch; mọi nhánh fail/cancel; `UpdateError` đúng loại; fake `HttpMessageHandler` (pattern `GoogleWebTranslatorTests`) |
| `UpdateCheckServiceTests` | 5s delay, overlap-guard, `UpdateFound` chỉ 1 lần/discovery, fail giữ state |
| `PackageSignatureVerifierTests` + `TestSigningKey` | sign/verify roundtrip, tampered zip → fail |
| `UpdateStagerTests` | staging extract, `VerifySelfContained`, `WaitUntilWritable`, `CopyDirectory` |
| `AppVersionFormatterTests` | trim zero, min 2 phần |
| `QueueSnapshot*Tests` | **KHÔNG port** (bỏ snapshot — §6.2) |

Verify: `dotnet build "ReTran Project.slnx"` + `dotnet test --project ReTran.Tests/ReTran.Tests.csproj` clean.

## 11. Non-goals

- Không update Python sidecar / models PaddleOCR (chốt ở §2-1) — user cài `uv sync` riêng, trỏ `CoreConfig.PythonExe`.
- Không downgrade, không beta/stable channel, không custom feed URL.
- Không MSIX/store; app vẫn unpackaged.
- Không rollback tự động sau copy dở dang.
- Không đụng `package.json`/`pyproject.toml` version.
- Chưa có step publish npm/uv trong CI.

## 12. Bẫy đã port (không được regressed — bug history router-balancing)

1. **Fail-closed signing** — mọi đường: thiếu `.sig`, sai key, zip tampered → refuse; không "warning rồi cho qua".
2. **Updater phải shadow-copy** kèm runtime từ `deps.json` (bug #4 self-DLL lock, bug #12 hostpolicy.dll).
3. **Staging + wait-writable**, không extract thẳng vào appDir (bug #5/#9 FDD trộn self-contained).
4. **`ArgumentList`** cho argv updater — không nối string (bug quote `C:\...\` cuối).
5. **`DownloadedZipPath` null tại đầu attempt** — zip dở không bao giờ được cài.
6. **`UpdateFound` chỉ transition** — không toast lặp mỗi 24h.
7. **Tag == csproj version** — enforced bằng workflow step (bug `v1.3.0`).
8. **Windows-only guards** ở mọi I/O method; `Start()` noop off-Windows.
