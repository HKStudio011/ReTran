# Version & Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port subsystem version/auto-update từ router-balancing sang ReTran: check GitHub Releases, download zip + verify ECDSA fail-closed, stop services, updater exe self-contained, CI release workflow ký + upload.

**Architecture:** `ReTran.App/Services/Update/` chứa models + check/download/verify/coordinator (namespace `ReTran_App.Services.Update`); project mới `ReTran.Updater` (console exe, self-contained khi publish); stop-services thay cho drain/snapshot của router-balancing (PipelineController → CoreProcessClient kill tree → OverlayFeedServer); version single-source ở `ReTran.App.csproj`; release qua GitHub Releases `HKStudio011/ReTran` với zip ECDSA-P256-signed.

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid, xUnit v3, System.Security.Cryptography ECDsa, GitHub Releases API, GitHub Actions.

**Spec:** `Docs/superpowers/specs/2026-10-14-version-update-design.md` (đã user-approved). Nguồn port: `D:\Code\router-balancing\src\RouterBalancing.Core\Update\*`, `D:\Code\router-balancing\RouterBalancing.Updater\*`, `D:\Code\router-balancing\router balancing test\Update\*`, `D:\Code\router-balancing\.github\workflows\release.yml`.

## Global Constraints

- Namespace App-side: `ReTran_App.Services.Update`; Updater: `ReTran.Updater`; tests: `ReTran.Tests.Update`.
- Repo constants: `RepoOwner="HKStudio011"`, `RepoName="ReTran"`, `UpdaterFileName="ReTran.Updater.exe"`, `UpdateFolderName="ReTran_Update"`, shadow dir `%TEMP%\ReTran_Updater`, User-Agent `Retran/{version}`.
- Fail-closed ECDSA P-256/SHA-256 everywhere: zip thiếu `.sig` → refuse; public key nhúng const `PackageSignatureVerifier.ReleasePublicKeyBase64`; private key chỉ nằm trong GH secret `UPDATE_SIGNING_KEY`.
- Mọi I/O method guard `OperatingSystem.IsWindows()` trả sentinel (null/false), không ném.
- Version: csproj `<Version>0.1.0.0</Version>` (4-part), `<ApplicationDisplayVersion>0.1.0</ApplicationDisplayVersion>`; tag release phải khớp display version (workflow enforce).
- Package zip = C# בלבד (App + Core + Updater self-contained `win-x64`); không sidecar/models.
- DEBUG build: update feature tắt hoàn toàn (`UpdateFeature.IsEnabled == false`) — không check nền, không hiện UI update.
- Log qua `Microsoft.Extensions.Logging.ILogger<T>`, không Console.WriteLine (ngoại trừ Updater exe — console app, stderr là đúng).
- Doc comments song ngữ Anh/Việt theo AGENTS.md; TDD: test fail trước khi implement.
- Commit mỗi task xong (commit message theo style `feat:`/`test:`/`chore:`).

## Review Focus

1. **Zip thiếu `.sig` hoặc sai chữ ký phải bị từ chối tuyệt đối** (fail-closed) — test: `DownloadUpdateAsync_ReturnsNull_WhenSignatureAssetMissing`, `InstallUpdateAsync_FalseAndDeletesZip_WhenSignatureInvalid`, `Verify_ReturnsFalse_*` (Task 1–2).
2. **DEBUG guard** — chạy từ repo không được phép "update" ghi đè `bin\Debug` — test: `Start_WhenNotSupported_IsNoOp` với `isSupported=false` (Task 3); UI chỉ render card update khi `UpdateFeature.IsEnabled` (Task 7).
3. **Updater phải chạy từ shadow copy kèm runtime self-contained** (bug #4/#12) — test: `PrepareUpdaterShadowCopy_CopiesSelfContainedRuntime_Files`, `VerifySelfContained_WhenFrameworkDependentRuntimeConfig_ReturnsFalse` (Task 2, 4).
4. **Core child process không được giữ lock file khi copy update** — test coordinator happy-path asserts `IUpdateStopper.StopServicesAsync` được gọi TRƯỚC `InstallUpdateAsync` (Task 5); updater `WaitUntilWritable` timeout tests (Task 4).
5. **Stale-run race** (Fail/Cancel của run cũ không được phá run mới) — port test `FailedHandler_NestedDisposeThenStart_RunTwoGateSurvivesTrailingRelease` còn hiệu lực với `_runId` guard (Task 5).

---

### Task 1: Update models + AppVersionFormatter + PackageSignatureVerifier + keypair

**Files:**
- Create: `ReTran.App/Services/Update/AppUpdateModels.cs`
- Create: `ReTran.App/Services/Update/AppVersionFormatter.cs`
- Create: `ReTran.App/Services/Update/PackageSignatureVerifier.cs`
- Create: `scripts/generate-update-signing-key.ps1`
- Modify: `ReTran.App/ReTran.App.csproj` (thêm `InternalsVisibleTo`)
- Test: `ReTran.Tests/Update/AppVersionFormatterTests.cs`, `ReTran.Tests/Update/PackageSignatureVerifierTests.cs`, `ReTran.Tests/Update/TestSigningKey.cs`

**Interfaces:**
- Consumes: không có (leaf).
- Produces (namespace `ReTran_App.Services.Update`):
  - `record ReleaseInfo([JsonPropertyName("tag_name")] string TagName, [JsonPropertyName("html_url")] string HtmlUrl, [JsonPropertyName("body")] string? Body, [JsonPropertyName("assets")] List<ReleaseAsset> Assets)`
  - `record ReleaseAsset([JsonPropertyName("name")] string Name, [JsonPropertyName("browser_download_url")] string BrowserDownloadUrl)`
  - `interface IAppUpdateService { string CurrentVersion { get; } Task<ReleaseInfo?> CheckForUpdatesAsync(); bool IsUpdateAvailable(ReleaseInfo release); Task<string?> DownloadUpdateAsync(ReleaseInfo release, IProgress<int>? progress); string? DownloadedZipPath { get; } bool VerifyDownloadedPackage(); Task<bool> InstallUpdateAsync(); }`
    *(khác router-balancing: thêm `VerifyDownloadedPackage()` — verify .sig của zip đã tải, trả false + xóa zip khi fail; Task 5 dùng để fail BEFORE stop-services.)*
  - `static class PackageSignatureVerifier { public const string ReleasePublicKeyBase64; public static bool Verify(string packagePath, string signaturePath, string publicKeyBase64); }`
  - `static class AppVersionFormatter { public static string Format(string version); public static string Format(Version version); }`

- [ ] **Step 1: Port test — AppVersionFormatterTests**

Copy `D:\Code\router-balancing\router balancing test\Update\AppVersionFormatterTests.cs`, đổi namespace `ReTran.Tests.Update`, `using ReTran_App.Services.Update;`. 7 test method giữ nguyên tên: `Format_ReturnsShortForm` (Theory: `1.0.0.0→"1.0"`, `1.0.3.0→"1.0.3"`, `1.0.0.4→"1.0.0.4"`), `Format_WhenBuildUndefined_ReturnsMajorMinor`, `Format_WhenRevisionUndefined_ReturnsShortForm`, `Format_WhenVersionNull_Throws`, `Format_ParsesStringThenFormats`, `Format_WhenStringUnparsable_Throws`, `Format_WhenStringNull_Throws`.

- [ ] **Step 2: Port test — TestSigningKey + PackageSignatureVerifierTests**

Copy `TestSigningKey.cs` (class `TestSigningKey : IDisposable`, ephemeral nistP256, `string SignToBase64(byte[] data)` xuất P1363 base64) đổi namespace/giống trên. Copy `PackageSignatureVerifierTests.cs` — 6 test giữ tên: `Verify_ReturnsTrue_WhenSignatureValid`, `Verify_ReturnsFalse_WhenPackageTampered`, `Verify_ReturnsFalse_WhenSignedByDifferentKey`, `Verify_ReturnsFalse_WhenSignatureNotBase64`, `Verify_ReturnsFalse_WhenFileMissing`, `Verify_ReturnsFalse_WhenPublicKeyMalformed`.

- [ ] **Step 3: Chạy test — mong đợi FAIL (compile error: class chưa tồn tại)**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` — expected: build fail do thiếu types.

- [ ] **Step 4: Implement 3 file + InternalsVisibleTo**

Port nguyên body từ `D:\Code\router-balancing\src\RouterBalancing.Core\Update\{AppUpdateModels,AppVersionFormatter,PackageSignatureVerifier}.cs`, chỉ đổi namespace +:
- `AppUpdateModels.cs`: interface `IAppUpdateService` thêm method `bool VerifyDownloadedPackage();` (doc comment song ngữ giải thích: verify sig của zip đã tải — fail-closed, xóa zip rác).
- `PackageSignatureVerifier.ReleasePublicKeyBase64`:暂时 để string rỗng `""` — Step 5 điền key thật; giữ const (test không dùng giá trị production).
- Thêm vào `ReTran.App.csproj`: `<ItemGroup><InternalsVisibleTo Include="ReTran.Tests" /></ItemGroup>` (dùng cho internal overload ở Task 2).

- [ ] **Step 5: Sinh production keypair (user-blocking)**

Copy script từ `D:\Code\router-balancing\scripts\generate-update-signing-key.ps1` (giữ nguyên nội dung). Chạy `pwsh scripts/generate-update-signing-key.ps1`:
- **PRIVATE key** — đưa cho user để họ thêm vào GitHub secret `UPDATE_SIGNING_KEY` (Settings → Environments → release). **TUYỆT ĐỐI không ghi private key vào bất kỳ file nào.**
- **PUBLIC key** — paste vào `PackageSignatureVerifier.ReleasePublicKeyBase64`.

- [ ] **Step 6: Chạy test — PASS**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "*AppVersionFormatterTests,*PackageSignatureVerifierTests"` — expected: PASS tất cả.

- [ ] **Step 7: Commit**

```bash
git add ReTran.App/Services/Update ReTran.Tests/Update scripts ReTran.App/ReTran.App.csproj
git commit -m "feat(update): models, version formatter, ECDSA package verifier + keygen script"
```

---

### Task 2: GitHubAppUpdateService

**Files:**
- Create: `ReTran.App/Services/Update/GitHubAppUpdateService.cs`
- Test: `ReTran.Tests/Update/GitHubAppUpdateServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 — `IAppUpdateService`, `ReleaseInfo`, `ReleaseAsset`, `PackageSignatureVerifier.Verify`.
- Produces: `sealed class GitHubAppUpdateService : IAppUpdateService`; public ctor `(HttpClient http)`; internal ctor `(HttpClient http, string currentVersion, string? signingPublicKeyBase64 = null)`; internal members `string? DownloadedSignaturePath { get; }`, `static string? PrepareUpdaterShadowCopy(string appDir)`, `static ProcessStartInfo BuildUpdaterStartInfo(string zipPath, int parentPid, string appDir)`.

- [ ] **Step 1: Port test**

Copy `D:\Code\router-balancing\router balancing test\Update\GitHubAppUpdateServiceTests.cs` → `ReTran.Tests/Update/`, đổi namespace + usings (`ReTran_App.Services.Update`), giữ nguyên sample manifest JSON + fake `HttpMessageHandler` helpers. Giữ 12 test (bỏ test nào chỉ涉及 snapshot — không có). Đổi expectations theo constants ReTran:
- `BuildUpdaterStartInfo_UsesArgumentList_AndKeepsTrailingBackslash` — assert `ArgumentList` sequence `["--zip", zip, "--pid", pid, "--app-dir", dir]`.
- `PrepareUpdaterShadowCopy_*` — pattern file `ReTran.Updater.*`, shadow dir dưới `%TEMP%\ReTran_Updater`.
- Thêm test mới `InstallUpdateAsync_FalseAndDeletesZip_WhenVerifyFails` dùng fake service path thật + keypair `TestSigningKey` khác key inject.

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "*GitHubAppUpdateServiceTests"` — expected: compile fail.

- [ ] **Step 3: Implement GitHubAppUpdateService**

Port `D:\Code\router-balancing\src\RouterBalancing.Core\Update\GitHubAppUpdateService.cs`, адапt:
- Constants: `RepoOwner="HKStudio011"`, `RepoName="ReTran"`, `UpdaterFileName="ReTran.Updater.exe"`, `UpdateFolderName="ReTran_Update"`; shadow root `"ReTran_Updater"`; mọi `"RouterBalancing…"` trong comment/glob pattern → `"ReTran…"`.
- User-Agent: `Retran/{CurrentVersion}`.
- Default version fallback ctor: `"0.0.0.0"`.
- Implement `VerifyDownloadedPackage()`:guard Windows + `DownloadedZipPath`/`DownloadedSignaturePath` null-check + `PackageSignatureVerifier.Verify(zip, sig, _signingPublicKeyBase64)`; fail → `DeletePartialFile` cả hai + null cả hai property + return false.
- `InstallUpdateAsync()` GIỮ verify lại lần nữa (defense-in-depth như router-balancing) rồi mới shadow-copy + `Process.Start`.
- Giữ nguyên: `DownloadedZipPath = null` ngay đầu `DownloadUpdateAsync`, `CompareVersions` zero-pad, chọn asset `.zip` không `symbols`, bắt buộc `.sig`, progress chỉ báo khi % tăng, mọi catch nuốt → sentinel.

- [ ] **Step 4: Chạy test — PASS**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "*GitHubAppUpdateServiceTests"` — expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Update/GitHubAppUpdateService.cs ReTran.Tests/Update/GitHubAppUpdateServiceTests.cs
git commit -m "feat(update): GitHub Releases update service (check/download/verify/shadow-copy)"
```

---

### Task 3: UpdateCheckService + UpdateFeature

**Files:**
- Create: `ReTran.App/Services/Update/UpdateCheckService.cs`
- Create: `ReTran.App/Services/Update/UpdateFeature.cs`
- Test: `ReTran.Tests/Update/UpdateCheckServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 — `IAppUpdateService`, `ReleaseInfo`.
- Produces:
  - `enum UpdateCheckResult { UpdateAvailable, AlreadyLatest, Failed }`
  - `static class UpdateFeature { public static bool IsEnabled { get; } }` — `#if DEBUG` → false, else `OperatingSystem.IsWindows()`.
  - `sealed class UpdateCheckService : IAsyncDisposable`; public ctor `(IAppUpdateService updates, ILogger<UpdateCheckService> log)` dùng `UpdateFeature.IsEnabled` làm `isSupported`; internal ctor `(IAppUpdateService updates, ILogger<UpdateCheckService> log, bool isSupported, TimeSpan? initialDelay = null)`; props `ReleaseInfo? AvailableRelease`, `bool IsUpdateAvailable`; events `Action? StateChanged`, `Action<ReleaseInfo>? UpdateFound`; `void Start()`; `Task<UpdateCheckResult> CheckOnceAsync()`; `ValueTask DisposeAsync()`.

- [ ] **Step 1: Port test**

Copy `UpdateCheckServiceTests.cs` — 7 test giữ tên: `CheckOnceAsync_UpdateAvailable_SetsStateAndRaisesUpdateFound`, `CheckOnceAsync_SecondTimeWithSameRelease_DoesNotRaiseUpdateFoundAgain`, `CheckOnceAsync_AlreadyLatest_ClearsPreviousState`, `CheckOnceAsync_WhenCheckReturnsNull_ReturnsFailed_StateUnchanged`, `Start_WhenNotSupported_IsNoOp` (sửa tên từ `Start_OnNonWindows_IsNoOp` — test `isSupported=false`, không phụ thuộc OS test host), `CheckOnceAsync_WhileAnotherCheckInFlight_ReturnsCurrentStateWithoutSecondHttpCall`, `CheckOnceAsync_WhileAnotherCheckInFlight_ReturnsUpdateAvailableWhenBadgeKnown`. Đổi `ILogService` → `ILogger<UpdateCheckService>` (dùng `NullLogger<UpdateCheckService>.Instance` hoặc Moq/xUnit v3 mock pattern sẵn có trong repo).

- [ ] **Step 2: Chạy test — FAIL** · Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "*UpdateCheckServiceTests"`

- [ ] **Step 3: Implement**

Port `UpdateCheckService.cs` với адапt: `_log.Warn(msg)` → `_log.LogWarning(msg)`, `_log.Info` → `_log.LogInformation`; bỏ tham chiếu `LogRetentionWorker`; guard `Start()` theo `_isSupported` (đã gồm DEBUG qua `UpdateFeature.IsEnabled`). Implement `UpdateFeature.cs` (file riêng, doc comment song ngữ giải thích lý do DEBUG-off).

- [ ] **Step 4: Chạy test — PASS** · Run: cùng lệnh Step 1.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Update/UpdateCheckService.cs ReTran.App/Services/Update/UpdateFeature.cs ReTran.Tests/Update/UpdateCheckServiceTests.cs
git commit -m "feat(update): background check service (5s + 24h loop, UpdateFound once per discovery)"
```

---

### Task 4: ReTran.Updater project + UpdateStager

**Files:**
- Create: `ReTran.Updater/ReTran.Updater.csproj`
- Create: `ReTran.Updater/UpdateStager.cs`
- Create: `ReTran.Updater/Program.cs`
- Modify: `ReTran Project.slnx` (thêm project)
- Modify: `ReTran.Tests/ReTran.Tests.csproj` (linked compile UpdateStager.cs)
- Test: `ReTran.Tests/Update/UpdateStagerTests.cs`

**Interfaces:**
- Consumes: zip layout do release.yml tạo (App publish output).
- Produces (namespace `ReTran.Updater`): `internal static class UpdateStager` với `string ExtractToStaging(string zipPath)`, `bool VerifySelfContained(string stagingDir)`, `bool WaitUntilWritable(string stagingDir, string appDir, TimeSpan timeout)`, `void CopyDirectory(string stagingDir, string appDir)`; `Program.cs` top-level args contract `["--zip", _, "--pid", _, "--app-dir", _]`.

- [ ] **Step 1: Scaffold project + link test**

`ReTran.Updater.csproj` copy từ `D:\Code\router-balancing\RouterBalancing.Updater\RouterBalancing.Updater.csproj` (10 dòng: OutputType Exe, TFM net10.0, AssemblyName `ReTran.Updater`, RootNamespace `ReTran.Updater`). Thêm vào `ReTran Project.slnx`. Thêm vào `ReTran.Tests.csproj`:
```xml
<Compile Include="..\ReTran.Updater\UpdateStager.cs" Link="Update\UpdateStager.cs" />
```
(ghi chú comment: compile trực tiếp vì updater là exe — theo pattern router-balancing; KHÔNG link Program.cs).

- [ ] **Step 2: Port test UpdateStagerTests**

Copy `UpdateStagerTests.cs` — 13 test giữ tên (`ExtractToStaging_*` 2, `WaitUntilWritable_*` 4, `CopyDirectory_*` 1, `VerifySelfContained_*` 5 + fixture), `using ReTran.Updater;`. Đổi `router-balancing.runtimeconfig.json` → `ReTran.App.runtimeconfig.json`, thư mục staging test → `%TEMP%\ReTran_Update\staging`.

- [ ] **Step 3: Chạy test — FAIL** · Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "*UpdateStagerTests"`

- [ ] **Step 4: Implement UpdateStager + Program.cs**

Port `UpdateStager.cs`: staging `Path.Combine(Path.GetTempPath(), "ReTran_Update", "staging")`; `VerifySelfContained` check `ReTran.App.runtimeconfig.json` + `hostfxr.dll`. Port `Program.cs` (top-level): giữ nguyên control flow + comment điều chỉnh; target exe backup/relaunch `"ReTran.App.exe"`; usage string `ReTran.Updater`; 60s parent wait; `CleanupSelfTempDir` giữ nguyên.

- [ ] **Step 5: Chạy test — PASS** · Run: cùng Step 2.

- [ ] **Step 6: Build toàn solution**

Run: `dotnet build "ReTran Project.slnx"` — expected: clean (Updater exe build được, App chưa reference).

- [ ] **Step 7: Commit**

```bash
git add "ReTran Project.slnx" ReTran.Updater ReTran.Tests/Update/UpdateStagerTests.cs ReTran.Tests/ReTran.Tests.csproj
git commit -m "feat(update): ReTran.Updater console project + staging/writable/self-contained checks"
```

---

### Task 5: UpdateCoordinator + stop-services + shutdown

**Files:**
- Create: `ReTran.App/Services/Update/UpdateCoordinator.cs` (chứa `UpdateError`, `IAppShutdown`, `IUpdateStopper`, `UpdateCoordinator`)
- Test: `ReTran.Tests/Update/UpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 1 — `IAppUpdateService.VerifyDownloadedPackage/InstallUpdateAsync/DownloadUpdateAsync`.
- Produces (namespace `ReTran_App.Services.Update`):
  - `enum UpdateError { DownloadFailed, VerifyFailed, StopServicesFailed, InstallFailed }`
  - `interface IAppShutdown { void Quit(); }`
  - `interface IUpdateStopper { Task StopServicesAsync(CancellationToken ct); }`
  - `sealed class UpdateCoordinator : IDisposable`; ctor `(IAppUpdateService updates, IUpdateStopper stopper, IAppShutdown shutdown, ILogger<UpdateCoordinator> log)`; props `UpdateInProgress`, `IsFinalizing`, `DownloadPercent`; events `Changed`, `Failed(Action<UpdateError>)`, `Cancelled`; `Task StartUpdateAsync(ReleaseInfo release)`, `void CancelUpdate()`, `void Dispose()`.

- [ ] **Step 1: Viết test (doubles + cases)**

Tạo `UpdateCoordinatorTests.cs` với fakes:
- `FakeUpdateService : IAppUpdateService` — `TaskCompletionSource<string?> DownloadTcs` cho `DownloadUpdateAsync`, `bool VerifyResult`/`bool InstallResult` settable, record `List<string> Calls` (ghi `download`/`verify`/`install`).
- `FakeStopper : IUpdateStopper` — record `StopCalled`, optional `Exception? ThrowOnStop`.
- `FakeShutdown : IAppShutdown` — record `QuitCalled`, optional throw.

Test cases (tên + assertion chính):
1. `StartUpdateAsync_HappyPath_StopsServicesThenInstallsThenQuits` — Calls order `["download","verify","stop","install","quit"]` (ghi thêm "stop" từ FakeStopper).
2. `StartUpdateAsync_WhenDownloadFails_RaisesFailedDownload_ServicesNotStopped` — DownloadTcs.SetResult(null) → `Failed(DownloadFailed)`, StopCalled=false.
3. `StartUpdateAsync_WhenVerifyFails_RaisesFailedVerify_ServicesNotStopped` — VerifyResult=false → `Failed(VerifyFailed)`, StopCalled=false, InstallResult không được gọi.
4. `StartUpdateAsync_WhenStopServicesFails_RaisesFailedStop` — ThrowOnStop → `Failed(StopServicesFailed)`, Install không gọi.
5. `StartUpdateAsync_WhenInstallFails_RaisesFailedInstall` — InstallResult=false → `Failed(InstallFailed)`.
6. `CancelUpdate_BeforeFinalize_CancelsAndRaisesCancelled` — cancel khi download còn pending → `Cancelled`, UpdateInProgress=false, stopper không gọi.
7. `CancelUpdate_DuringFinalize_IsNoOp` — sau khi download+verify xong (finalizing) → CancelUpdate không bắn Cancelled, pipeline chạy tiếp.
8. `StartUpdateAsync_WhenAlreadyInProgress_Throws` — `InvalidOperationException`.
9. `Dispose_CancelsPendingUpdate` — Dispose khi download pending → không exception, state reset.
10. `StartUpdateAsync_WhenQuitThrows_ResetsStateWithoutFailed` — Quit throw → log warn, UpdateInProgress=false, KHÔNG `Failed`.
11. `StartUpdateAsync_HandlerReentrantStartDuringFailed_UsesRunIdGuard` — port `FailedHandler_NestedDisposeThenStart...` rút gọn: từ handler `Failed` gọi `Dispose()` rồi `StartUpdateAsync` run 2; run 1 trailing không phá run 2 (run 2 install+quit chạy trọn).

- [ ] **Step 2: Chạy test — FAIL** · Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "*UpdateCoordinatorTests"`

- [ ] **Step 3: Implement UpdateCoordinator**

So với `D:\Code\router-balancing\...\UpdateCoordinator.cs` (511 dòng) — GIỮ: pattern `_sync` lock, `_runId`, `Changed/Failed/Cancelled` bắn ngoài lock, `PercentProgress : IProgress<int>` (không dùng `Progress<T>`), guard Windows + reentrancy, `IsFinalizing` check-set atomic, Fail cancel CTS trước khi bắn event, Reset sau khi release. BỎ: `DrainGate`, `IProxyHost`, `InFlightCount`, `PollUntilReadyAsync`, queue snapshot (`DeleteSnapshot`, `SnapshotFailed`, `DrainFailed`). THÊM luồng:

```
StartUpdateAsync(release):
  Windows guard → lock: reentrancy throw → new CTS + runId + percent 0 → Changed
  zipPath = await _updates.DownloadUpdateAsync(release, PercentProgress)  (cancel-aware qua cts.Token — dùng cts.IsCancellationRequested check sau await như router-balancing)
  null/exception → Fail(DownloadFailed)
  !_updates.VerifyDownloadedPackage() → Fail(VerifyFailed)          // TRƯỚC stop
  lock: !updateInProgress/cts cancelled → return; _isFinalizing = true → Changed
  await _stopper.StopServicesAsync(cts.Token) với overall timeout ~30s (spec §6.2 ~10s/bước; `CancellationTokenSource(TimeSpan.FromSeconds(30))` kết hợp token) — exception/timeout → Fail(StopServicesFailed)
  await _updates.InstallUpdateAsync() — false/exception → Fail(InstallFailed)
  _shutdown.Quit() — exception → log warn + reset (KHÔNG Failed)
```

`CancelUpdate`/`Dispose`/`Fail`/`Reset` giữ nguyên semantics `_runId` như nguồn. Doc comments song ngữ cho public API.

- [ ] **Step 4: Chạy test — PASS** · Run: cùng Step 1.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Update/UpdateCoordinator.cs ReTran.Tests/Update/UpdateCoordinatorTests.cs
git commit -m "feat(update): coordinator — download → verify → stop services → install → quit"
```

---

### Task 6: Wiring — version, CoreLocator, DI, startup

**Files:**
- Modify: `ReTran.App/ReTran.App.csproj` (Version props + ProjectReference Updater)
- Modify: `ReTran.App/Core/CoreLocator.cs` (sibling check)
- Modify: `ReTran.App/MauiProgram.cs` (DI registrations)
- Modify: `ReTran.App/App.xaml.cs` (Start update check)
- Create: `ReTran.App/Services/Update/UpdateStopper.cs`
- Create: `ReTran.App/Services/Update/MauiAppShutdown.cs`
- Test: `ReTran.Tests/Update/UpdateStopperTests.cs`, `ReTran.Tests/CoreLocatorTests.cs` (mới hoặc extend nếu đã có)

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces: `sealed class UpdateStopper : IUpdateStopper` ctor `(PipelineController pipeline, CoreProcessClient core, OverlayFeedServer feed, ILogger<UpdateStopper> log)`; `sealed class MauiAppShutdown : IAppShutdown`; DI: `GitHubAppUpdateService` (+ `HttpClient` singleton), `UpdateStopper`, `UpdateCoordinator`, `UpdateCheckService`, `IAppShutdown`.

- [ ] **Step 1: Test trước — CoreLocator sibling**

Viết test `CoreLocatorTests` (hoặc extend file có sẵn nếu repo đã có): tạo temp dir có file `ReTran.Core.exe` giả, set `RETRAN_CORE_EXE` env rỗng/xóa, không test được `AppContext.BaseDirectory` thật → refactor nhẹ `CoreLocator` cho testable: thêm internal overload `FindCoreExe(string? envValue, string baseDirectory)` (public `FindCoreExe()` gọi với giá trị thật). Test: env có file → env wins; env null + sibling tồn tại → sibling; cả hai không → fallback (null hoặc repo-walk — assert không throw).

- [ ] **Step 2: Chạy fail → implement**

CoreLocator thứ tự mới: env → sibling `Path.Combine(baseDirectory, "ReTran.Core.exe")` → repo-walk Debug. Cập nhật doc comment song ngữ.

- [ ] **Step 3: Test UpdateStopper**

`UpdateStopperTests` với fakes `ICorePeer`/mock: happy path gọi đủ 3 bước; `pipeline.IsRunning=false` → bỏ qua `StopAsync`; mỗi bước throw → nuốt + log warn, bước sau vẫn chạy, method không ném. (Dùng FakeCorePeer pattern từ `PipelineControllerTests` + fake OverlayFeedServer — nếu `OverlayFeedServer` concrete khó fake, bọc interface `IOverlayFeedServer { ValueTask DisposeAsync(); }` — chỉ thêm nếu cần, ưu tiên test trên concrete với instance chưa Start.)

- [ ] **Step 4: Chạy fail → implement UpdateStopper + MauiAppShutdown**

`UpdateStopper.StopServicesAsync`: (1) `if (pipeline.IsRunning) await pipeline.StopAsync(ct)` try/catch warn; (2) `await core.DisposeAsync()` try/catch warn (kill tree); (3) `await feed.DisposeAsync()` try/catch warn. `MauiAppShutdown.Quit()` → `Application.Current?.Quit();`.

- [ ] **Step 5: csproj version + ProjectReference**

In `ReTran.App.csproj`:
```xml
<Version>0.1.0.0</Version>
<ApplicationDisplayVersion>0.1.0</ApplicationDisplayVersion>
```
(thay `1.0`; giữ `<ApplicationVersion>1</ApplicationVersion>`). Thêm:
```xml
<ProjectReference Include="..\ReTran.Updater\ReTran.Updater.csproj">
  <OutputItemType>Content</OutputItemType>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</ProjectReference>
```

- [ ] **Step 6: DI + startup**

`MauiProgram.cs` — thêm sau registrations hiện có:
```csharp
builder.Services.AddSingleton(_ => new HttpClient());
builder.Services.AddSingleton<GitHubAppUpdateService>();
builder.Services.AddSingleton<IAppUpdateService>(sp => sp.GetRequiredService<GitHubAppUpdateService>());
builder.Services.AddSingleton<UpdateStopper>();
builder.Services.AddSingleton<IUpdateStopper>(sp => sp.GetRequiredService<UpdateStopper>());
builder.Services.AddSingleton<UpdateCoordinator>();
builder.Services.AddSingleton<UpdateCheckService>();
builder.Services.AddSingleton<IAppShutdown, MauiAppShutdown>();
```
`App.xaml.cs` ctor, sau `InitializeComponent()`:
```csharp
UpdateCheckService? check = IPlatformApplication.Current?.Services?.GetService<UpdateCheckService>();
check?.Start();
```
(`Start()` đã noop ở DEBUG/off-Windows — guard duy nhất nằm ở `UpdateFeature`/`isSupported`.)

- [ ] **Step 7: Build + test toàn bộ**

Run: `dotnet build "ReTran Project.slnx"` rồi `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` — expected: clean, PASS. Kiểm chứng publish sớm (optional smoke): `dotnet publish ReTran.App/ReTran.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained -c Release -o <temp>` thấy `ReTran.Updater.exe` trong output.

- [ ] **Step 8: Commit**

```bash
git add ReTran.App "ReTran.Tests/Update" ReTran.Tests/ReTran.Tests.csproj
git commit -m "feat(update): wire DI/startup, version 0.1.0.0, CoreLocator sibling, stop-services impl"
```

---

### Task 7: UI — ToastService, Settings card, UpdateModal, MainLayout, NavMenu, i18n

**Files:**
- Create: `ReTran.App/Services/ToastService.cs`
- Create: `ReTran.App/Components/Shared/UpdateModal.razor`
- Modify: `ReTran.App/Services/I18nStrings.cs` (thêm `update.*` VI + EN)
- Modify: `ReTran.App/Components/Pages/Settings.razor` (Card cập nhật)
- Modify: `ReTran.App/Components/Layout/MainLayout.razor` (toast stack + UpdateModal)
- Modify: `ReTran.App/Components/Layout/NavMenu.razor` (footer format + badge)
- Modify: `ReTran.App/MauiProgram.cs` (ToastService singleton)

**Interfaces:**
- Consumes: Tasks 3–5 (`UpdateCheckService`, `UpdateCoordinator`, `UpdateFeature`, `AppVersionFormatter`, `IAppUpdateService`, `ReleaseInfo`, `UpdateError`, `UpdateCheckResult`).
- Produces: `enum ToastSeverity { Info, Error }`; `record ToastItem(string Message, ToastSeverity Severity, Guid Id)`; `sealed class ToastService { public event Action<ToastItem>? ToastAdded; public void Show(string message, ToastSeverity severity = ToastSeverity.Info); }`.

- [ ] **Step 1: i18n keys**

Thêm vào cả `I18nStrings.Vi` và `.En` (bỏ `update.draining`, `update.error.snapshot`, `update.error.drain` của router-balancing; thêm `error.verify`, `error.stop`):
`update.title` (Cập nhật ứng dụng / Application update), `update.version` (Phiên bản / Version), `update.badge` (Có bản cập nhật / Update available), `update.check` (Kiểm tra cập nhật / Check for updates), `update.checkFailed` (Kiểm tra cập nhật thất bại - thử lại sau. / Update check failed. Please try again.), `update.now` (Cập nhật ngay / Update now), `update.latest` (Bạn đang dùng bản mới nhất. / You are on the latest version.), `update.available` (Có bản cập nhật mới: {0} / New update available: {0}), `update.downloading` (Đang tải bản cập nhật. {0} / Downloading update. {0}), `update.preparing` (Đang chuẩn bị cài đặt. / Preparing to install.), `update.cancel` (Hủy / Cancel), `update.cancelled` (Đã hủy cập nhật. / Update cancelled.), `update.failed` (Cập nhật thất bại: {0} / Update failed: {0}), `update.error.download`, `update.error.verify`, `update.error.stop`, `update.error.install`.

- [ ] **Step 2: ToastService + DI**

Implement theo Interfaces. Đăng ký `builder.Services.AddSingleton<ToastService>();` trong MauiProgram.

- [ ] **Step 3: UpdateModal.razor**

Port `D:\Code\router-balancing\router-balancing\Components\Shared\UpdateModal.razor`: bỏ dòng draining; giữ `Coordinator.UpdateInProgress` render guard, `IsFinalizing` → ẩn Cancel + `update.preparing`, progress bar style inline height 8px; `@inject UpdateCoordinator Coordinator`, `@inject I18nService L`; `OnInitialized` subscribe `Changed` → `InvokeAsync(StateHasChanged)`; `@implements IDisposable`. Tailwind classes theo theme hiện có của ReTran (đối chiếu class có sẵn trong Settings.razor/Card.razor — dùng `border-surface`, `bg-background`, `btn btn-primary`...).

- [ ] **Step 4: MainLayout.razor — toast stack + UpdateModal**

Thêm `@inject ToastService Toast`, `@inject UpdateCheckService UpdateCheck`, `@inject UpdateCoordinator Coordinator`, `@inject I18nService L`, `@implements IDisposable`; mount `<UpdateModal />`; toast stack fixed bottom-right (port pattern `_toasts` + 4s auto-remove từ router-balancing MainLayout); handlers: `UpdateFound` → toast `update.available` (format tag); `Failed(error)` → switch `UpdateError` map 4 key lỗi (fallback `update.error.install`) → toast error `update.failed`; `Cancelled` → toast `update.cancelled`. Subscribe/unsubscribe đúng IDisposable.

- [ ] **Step 5: NavMenu.razor — footer + badge**

Footer: thay raw `Assembly...ToString()` bằng `AppVersionFormatter.Format(...)` (inject `IAppUpdateService`); thêm badge span (ẩn khi `!UpdateFeature.IsEnabled` hoặc không có update): text `update.badge`, style `rounded-full bg-warning px-2 py-0.5 text-xs`; subscribe `UpdateCheckService.StateChanged` + `I18nService.Changed` → re-render. Bỏ `@using System.Reflection` nếu không còn dùng.

- [ ] **Step 6: Settings.razor — Card cập nhật**

Thêm sau Card ngôn ngữ, chỉ render khi `UpdateFeature.IsEnabled`:
```razor
<Card Title="@(L["update.title"])">
    <div class="text-sm">@(L["update.version"]): @_appVersion
        @if (Check.IsUpdateAvailable) { <span class="...badge...">@(L["update.badge"])</span> }
    </div>
    <button class="btn btn-primary" disabled="@_checking" @onclick="CheckAsync">@(L["update.check"])</button>
    @if (Check.IsUpdateAvailable) { <button class="btn btn-primary" @onclick="UpdateNowAsync">@(L["update.now"])</button> }
</Card>
```
Code-behind: inject `UpdateCheckService Check`, `UpdateCoordinator Coordinator`, `ToastService Toast`, `IAppUpdateService Updates`; `_appVersion = AppVersionFormatter.Format(Updates.CurrentVersion)`. `CheckAsync()` port pattern `wasAvailable` (toast chỉ khi user check lại update đã biết; `AlreadyLatest` → toast latest; `Failed` → toast checkFailed error). `UpdateNowAsync()`: guard `AvailableRelease is not { } release` + `Coordinator.UpdateInProgress`; fire-and-forget `_ = Coordinator.StartUpdateAsync(release);`.

- [ ] **Step 7: Build + test**

Run: `dotnet build "ReTran Project.slnx"` && `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` — expected: clean + PASS. (Không có UI test tự động — kiểm tra render bằng cách chạy app ở bước Task 9.)

- [ ] **Step 8: Commit**

```bash
git add ReTran.App
git commit -m "feat(update): settings card, update modal, toasts, formatted version footer + i18n"
```

---

### Task 8: CI workflows

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: layout publish (Task 6 ProjectReference), keypair (Task 1 secret `UPDATE_SIGNING_KEY` đã có trên repo).
- Produces: tag `vX.Y.Z` → release zip signed uploaded.

- [ ] **Step 1: ci.yml**

Port `D:\Code\router-balancing\.github\workflows\ci.yml`, адапt: `PROJECT_PATH: "ReTran Project/ReTran.App/ReTran.App.csproj"` (lưu ý space trong path — quote), build: `dotnet build "ReTran Project.slnx" -f net10.0-windows10.0.19041.0 --nologo` (hoặc build từng project nếu `-f` trên slnx không hỗ trợ — dùng `dotnet build "ReTran Project/ReTran.App/ReTran.App.csproj" -f net10.0-windows10.0.19041.0` cho chắc), test: `dotnet test "ReTran Project/ReTran.Tests/ReTran.Tests.csproj" --no-banner`. Giữ `dotnet workload install maui` + setup-dotnet 10.0.x.

- [ ] **Step 2: release.yml**

Port `D:\Code\router-balancing\.github\workflows\release.yml` với các sửa đổi:
- `PROJECT_PATH: ReTran Project/ReTran.App/ReTran.App.csproj`.
- **Step mới đầu tiên — verify tag == version:**
```yaml
- name: Verify tag matches ApplicationDisplayVersion
  if: github.event_name == 'release'
  shell: pwsh
  run: |
    [xml]$csproj = Get-Content "${{ env.PROJECT_PATH }}"
    $display = $csproj.Project.PropertyGroup.ApplicationDisplayVersion | Select-Object -First 1
    $tag = "${{ github.ref_name }}".TrimStart('v')
    if ($display -ne $tag) { throw "Tag '${{ github.ref_name }}' does not match ApplicationDisplayVersion '$display' — bump the csproj before tagging." }
```
- Publish giữ nguyên (self-contained flow lan sang Updater qua ProjectReference).
- **Publish Core + copy vào output** (Core không phải ProjectReference của App — zip sẽ thiếu `ReTran.Core.exe` nếu bỏ qua):
```pwsh
dotnet publish "ReTran Project/ReTran.Core/ReTran.Core.csproj" -c Release -r win-x64 --self-contained -o publish-core
Copy-Item publish-core/* "${{ env.OUTPUT_DIR }}/" -Exclude "ReTran.Core.pdb"
Remove-Item -Recurse publish-core
```
(Core phải self-contained như App — zip chạy trên máy không có .NET runtime; file trùng tên với App được overwrite là bình thường.)
(Xác minh khi smoke Task 9: `ReTran.Core.exe` + runtimeconfig/deps của Core nằm cạnh `ReTran.App.exe`.)
- Verify output: `ReTran.App.exe` + `ReTran.Updater.exe` (đổi tên file check).
- Azure Sign bước (optional, giữ nguyên pattern) sign `ReTran.App.exe` + `ReTran.Updater.exe`.
- Zip: `Compress-Archive ... "ReTran-$version-win-x64.zip"`; sign bước giữ nguyên logic, glob `ReTran-*.zip`; SHA256SUMS + attest glob `ReTran-*`; upload `gh release upload <tag> ReTran-*.zip ReTran-*.zip.sig SHA256SUMS.txt --clobber`.

- [ ] **Step 3: Validate yaml**

Run locally: `pwsh -Command "Get-Content .github/workflows/ci.yml | ConvertFrom-Json"` không được với YAML — thay bằng build solution lần nữa để chắc không gì vỡ, và review tay 2 file khớp structure nguồn.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows
git commit -m "ci: build/test workflow + release workflow (tag check, self-contained publish, ECDSA sign, upload)"
```

---

### Task 9: Final verification + smoke checklist

**Files:**
- Không tạo mới (chỉ verify + fix fallout).

- [ ] **Step 1: Full build + test**

Run: `dotnet build "ReTran Project.slnx"` && `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` — expected: 0 lỗi, mọi test PASS (bao gồm test cũ không bị regressed).

- [ ] **Step 2: Publish smoke**

`dotnet publish ReTran.App/ReTran.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained -c Release -o <temp>` — verify: `ReTran.App.exe`, `ReTran.Core.exe`? (KHÔNG — Core không phải ProjectReference của App; zip sẽ THIẾU Core exe!).

**⚠️ Phát hiện cần xử lý tại đây (đã biết từ spec §3 layout):** `ReTran.Core.exe` chưa nằm trong publish output vì App chỉ *tìm* Core qua `CoreLocator`, không reference project. Thêm vào release.yml (hoặc Task 6 csproj) bước copy sau publish:
```pwsh
dotnet publish "ReTran Project/ReTran.Core/ReTran.Core.csproj" -c Release -o ${{ env.OUTPUT_DIR }}-core
Copy-Item "${{ env.OUTPUT_DIR }}-core/ReTran.Core.exe" "${{ env.OUTPUT_DIR }}/"
# + các dll/runtimeconfig/deps của Core nếu publish ra folder (xác minh khi chạy)
```
Cập nhật plan Task 8 step 2 để include bước publish Core + copy; verify khi smoke ở trên thấy đủ `ReTran.App.exe`, `ReTran.Core.exe`, `ReTran.Updater.exe`.

- [ ] **Step 3: Chạy app DEBUG smoke (tay)**

Chạy app (Debug) — verify: app khởi động không lỗi DI mới; Settings **không** hiện Card cập nhật (DEBUG guard); footer hiện `v0.1` (AppVersionFormatter).

- [ ] **Step 4: Manual update E2E checklist (không chạy được local — ghi vào Docs/performance.md hoặc release notes)**

1. User: tạo GH environment `release` + secret `UPDATE_SIGNING_KEY` (từ keygen Task 1).
2. Bump `<Version>`/`<ApplicationDisplayVersion>`, tag `v<version>`, publish release → workflow chạy, assets zip+.sig.
3. Cài bản zip cũ hơn → app check nền 5s → toast "Có bản cập nhật mới" → bấm "Cập nhật ngay" → modal progress → app tắt → updater cài → app mở lại báo version mới.
4. Negative: xóa `.sig` trên release → client refuse; zip tampered → `VerifyFailed`.

- [ ] **Step 5: Commit cuối (nếu có fix fallout)**

```bash
git add -A
git commit -m "chore(update): publish Core into release zip + final wiring fixes"
```

---

## Ghi chú cho executor

- Nguồn port sống tại `D:\Code\router-balancing` — đọc file nguồn khi cần full body; plan chỉ ghi delta.
- Không bao giờ ghi private key vào file/commit; chỉ public key vào const.
- Nếu 1 test port không khớp behavior đã адапt (drain/snapshot bị bỏ) — sửa test theo Task 5 contract, không bịa back drain.
- ReTran chưa có Moq; kiểm tra pattern mock hiện có trong `ReTran.Tests` (FakeCorePeer…) và tái dùng style đó.
