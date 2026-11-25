using System.Net;
using ReTran_App.Services.Update;

namespace ReTran.Tests.Update;

public class GitHubAppUpdateServiceTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.3",
          "html_url": "https://github.com/HKStudio011/router-balancing/releases/tag/v1.2.3",
          "body": "Release notes",
          "assets": [
            {
              "name": "router-balancing-1.2.3-win-x64.zip",
              "browser_download_url": "https://github.com/HKStudio011/router-balancing/releases/download/v1.2.3/router-balancing-1.2.3-win-x64.zip"
            }
          ]
        }
        """;

    private const string ZipAssetName = "router-balancing-test-payload.zip";

    private static GitHubAppUpdateService ServiceWith(HttpMessageHandler handler, string currentVersion = "1.0.0.0",
        string? publicKeyBase64 = null, string? appDir = null,
        Func<string, string, string, bool>? packageVerifier = null) =>
        new(new HttpClient(handler, disposeHandler: false), currentVersion, publicKeyBase64, appDir, packageVerifier);

    private static ReleaseInfo Release(string tag, params ReleaseAsset[] assets) =>
        new(tag, $"https://github.com/HKStudio011/router-balancing/releases/tag/{tag}", null, [.. assets]);

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsRelease_WhenSuccess()
    {
        var service = ServiceWith(new JsonHandler(ReleaseJson));

        var release = await service.CheckForUpdatesAsync();

        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("https://github.com/HKStudio011/router-balancing/releases/tag/v1.2.3", release.HtmlUrl);
        Assert.Equal("Release notes", release.Body);
        var asset = Assert.Single(release.Assets);
        Assert.Equal("router-balancing-1.2.3-win-x64.zip", asset.Name);
        Assert.Equal(
            "https://github.com/HKStudio011/router-balancing/releases/download/v1.2.3/router-balancing-1.2.3-win-x64.zip",
            asset.BrowserDownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsNull_WhenNonSuccess()
    {
        var service = ServiceWith(new StatusHandler(HttpStatusCode.InternalServerError));

        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsNull_WhenNonJsonResponse()
    {
        // Spec §12: API trả HTML/gián đoạn (rate limit page, proxy error...) —
        // deserialize ném JsonException, service phải trả null chứ không ném ra caller.
        var service = ServiceWith(new JsonHandler("<html>gateway timeout</html>"));

        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Fact]
    public void IsUpdateAvailable_True_WhenTagNewer()
    {
        var service = ServiceWith(new JsonHandler("{}"), "1.0.0.0");

        Assert.True(service.IsUpdateAvailable(Release("v1.0.1")));
    }

    [Fact]
    public void IsUpdateAvailable_False_WhenEqualOrOlder()
    {
        var service = ServiceWith(new JsonHandler("{}"), "1.0.0.0");

        Assert.False(service.IsUpdateAvailable(Release("1.0.0.0")));
        // Ít thành phần hơn nhưng bằng sau khi pad 0 — 1.0 ≡ 1.0.0.0
        Assert.False(service.IsUpdateAvailable(Release("v1.0")));
        Assert.False(service.IsUpdateAvailable(Release("v0.9.9")));
    }

    [Fact]
    public void IsUpdateAvailable_True_WhenTagShorter_Padded()
    {
        var service = ServiceWith(new JsonHandler("{}"), "1.0.9.0");

        Assert.True(service.IsUpdateAvailable(Release("v1.1")));
    }

    [Fact]
    public async Task DownloadUpdateAsync_WritesZipAndSignature_AndReportsProgress()
    {
        // 30 bytes, chunk 10 → đúng 3 lần đọc, progress tăng dần 33/66/100
        var payload = "0123456789ABCDEFGHIJ0123456789"u8.ToArray();
        using var key = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64, chunkSize: 10),
            publicKeyBase64: key.PublicKeyBase64);
        var progress = new RecordingProgress();

        var path = await service.DownloadUpdateAsync(release, progress, TestContext.Current.CancellationToken);

        try
        {
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(path, service.DownloadedZipPath);
            Assert.NotNull(service.DownloadedSignaturePath);
            Assert.True(File.Exists(service.DownloadedSignaturePath));
            Assert.Equal(signatureBase64,
                await File.ReadAllTextAsync(service.DownloadedSignaturePath, TestContext.Current.CancellationToken));
            Assert.NotEmpty(progress.Reports);
            Assert.Equal(100, progress.Reports[^1]);
        }
        finally
        {
            TryDeleteAttemptDirectory(Path.GetFileName(Path.GetDirectoryName(path)));
        }
    }

    [Fact]
    public async Task Download_OlderAttemptCannotOverwriteNewerAttempt()
    {
        var olderPayload = "older zip"u8.ToArray();
        var newerPayload = "newer zip"u8.ToArray();
        var handler = new StaleRunHandler(olderPayload, newerPayload);
        var service = ServiceWith(handler);
        var release = Release("v1.2.3",
            new ReleaseAsset(ZipAssetName, "https://example.com/payload.zip"),
            new ReleaseAsset(ZipAssetName + ".sig", "https://example.com/payload.zip.sig"));
        using var olderCts = new CancellationTokenSource();
        var olderDownload = DownloadWithToken(service, release, olderCts.Token);
        string? newerPath = null;

        try
        {
            await handler.FirstSignatureStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            olderCts.Cancel();

            newerPath = await DownloadWithToken(service, release, CancellationToken.None);

            Assert.NotNull(newerPath);
            var updateRoot = Path.Combine(Path.GetTempPath(), "ReTran_Update");
            var olderPath = Path.Combine(updateRoot, "1", ZipAssetName);
            Assert.Equal(Path.Combine(updateRoot, "2", ZipAssetName), newerPath);
            Assert.NotEqual(olderPath, newerPath);
            Assert.Equal(newerPath, service.DownloadedZipPath);
            Assert.Equal(newerPath + ".sig", service.DownloadedSignaturePath);
            Assert.Equal(newerPayload, await File.ReadAllBytesAsync(
                newerPath, TestContext.Current.CancellationToken));
            Assert.Equal("newer signature", await File.ReadAllTextAsync(
                newerPath + ".sig", TestContext.Current.CancellationToken));

            // The HTTP double deliberately releases A only after B has completed, exposing stale publication.
            handler.ReleaseOlderSignature();
            Assert.Null(await olderDownload);

            Assert.True(handler.OlderSignatureToken.IsCancellationRequested);
            Assert.Equal(newerPath, service.DownloadedZipPath);
            Assert.Equal(newerPath + ".sig", service.DownloadedSignaturePath);
            Assert.Equal(newerPayload, await File.ReadAllBytesAsync(
                newerPath, TestContext.Current.CancellationToken));
            Assert.Equal("newer signature", await File.ReadAllTextAsync(
                newerPath + ".sig", TestContext.Current.CancellationToken));
            Assert.False(File.Exists(olderPath));
            Assert.False(File.Exists(olderPath + ".sig"));
            Assert.False(Directory.Exists(Path.GetDirectoryName(olderPath)));
        }
        finally
        {
            olderCts.Cancel();
            handler.ReleaseOlderSignature();
            try { await olderDownload; } catch { }
            TryDeleteAttemptDirectory("1");
            if (newerPath is not null)
            {
                TryDeleteAttemptDirectory(Path.GetFileName(Path.GetDirectoryName(newerPath)));
            }
        }
    }

    [Fact]
    public async Task Download_UncancelledOlderAttemptCannotReplaceNewerAttempt()
    {
        var olderPayload = "older zip"u8.ToArray();
        var newerPayload = "newer zip"u8.ToArray();
        var handler = new StaleRunHandler(olderPayload, newerPayload);
        var service = ServiceWith(handler);
        var release = Release("v1.2.3",
            new ReleaseAsset(ZipAssetName, "https://example.com/payload.zip"),
            new ReleaseAsset(ZipAssetName + ".sig", "https://example.com/payload.zip.sig"));
        var olderDownload = DownloadWithToken(service, release, CancellationToken.None);
        string? newerPath = null;

        try
        {
            await handler.FirstSignatureStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // A remains uncancelled: latest-only publication, not cancellation, must reject its late completion.
            newerPath = await DownloadWithToken(service, release, CancellationToken.None);

            Assert.NotNull(newerPath);
            handler.ReleaseOlderSignature();
            Assert.Null(await olderDownload);

            var updateRoot = Path.Combine(Path.GetTempPath(), "ReTran_Update");
            var olderPath = Path.Combine(updateRoot, "1", ZipAssetName);
            Assert.Equal(Path.Combine(updateRoot, "2", ZipAssetName), newerPath);
            Assert.Equal(newerPath, service.DownloadedZipPath);
            Assert.Equal(newerPath + ".sig", service.DownloadedSignaturePath);
            Assert.Equal(newerPayload, await File.ReadAllBytesAsync(
                newerPath, TestContext.Current.CancellationToken));
            Assert.Equal("newer signature", await File.ReadAllTextAsync(
                newerPath + ".sig", TestContext.Current.CancellationToken));
            Assert.False(File.Exists(olderPath));
            Assert.False(File.Exists(olderPath + ".sig"));
        }
        finally
        {
            handler.ReleaseOlderSignature();
            try { await olderDownload; } catch { }
            TryDeleteAttemptDirectory("1");
            if (newerPath is not null)
            {
                TryDeleteAttemptDirectory(Path.GetFileName(Path.GetDirectoryName(newerPath)));
            }
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_ReturnsNull_WhenSignatureAssetMissing()
    {
        // Fail-closed: release không có file .sig → không tin được zip, từ chối ngay lúc tải
        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        var release = Release("v1.2.3", new ReleaseAsset(ZipAssetName, "https://example.com/payload.zip"));
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64: null), publicKeyBase64: key.PublicKeyBase64);

        Assert.Null(await service.DownloadUpdateAsync(release, null, TestContext.Current.CancellationToken));
        Assert.Null(service.DownloadedZipPath);
    }

    [Fact]
    public async Task InstallUpdateAsync_FalseAndDeletesZip_WhenSignatureInvalid()
    {
        // Threat model: zip bị tamper hoặc ký sai key — verify fail → không khởi động updater, xóa zip rác
        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        using var otherKey = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64),
            publicKeyBase64: otherKey.PublicKeyBase64);
        var downloaded = await service.DownloadUpdateAsync(release, null, TestContext.Current.CancellationToken);

        try
        {
            Assert.NotNull(downloaded);

            Assert.False(await service.InstallUpdateAsync());
            Assert.Null(service.DownloadedZipPath);
            Assert.False(File.Exists(downloaded));
        }
        finally
        {
            TryDeleteAttemptDirectory(Path.GetFileName(Path.GetDirectoryName(downloaded)));
        }
    }

    [Fact]
    public async Task InstallUpdateAsync_FalseAndDeletesZip_WhenVerifyFails()
    {
        // Mới so với router-balancing (IAppUpdateService.VerifyDownloadedPackage): ký bằng
        // keypair khác key inject → verify fail-closed, xóa cả zip lẫn .sig, null cả hai
        // property; InstallUpdateAsync defense-in-depth cũng trả false.
        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        using var otherKey = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64),
            publicKeyBase64: otherKey.PublicKeyBase64);
        var downloaded = await service.DownloadUpdateAsync(release, null, TestContext.Current.CancellationToken);

        try
        {
            Assert.NotNull(downloaded);
            Assert.NotNull(service.DownloadedSignaturePath);

            Assert.False(service.VerifyDownloadedPackage());
            Assert.Null(service.DownloadedZipPath);
            Assert.Null(service.DownloadedSignaturePath);
            Assert.False(File.Exists(downloaded));

            Assert.False(await service.InstallUpdateAsync());
        }
        finally
        {
            TryDeleteAttemptDirectory(Path.GetFileName(Path.GetDirectoryName(downloaded)));
        }
    }

    [Fact]
    public async Task Verify_OlderAttemptFailureCannotDeleteOrClearNewerAttempt()
    {
        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var verifierStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifierRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64),
            publicKeyBase64: key.PublicKeyBase64,
            packageVerifier: (_, _, _) =>
            {
                verifierStarted.TrySetResult();
                verifierRelease.Task.GetAwaiter().GetResult();
                return false;
            });
        string? olderPath = null;
        string? newerPath = null;
        Task<bool>? olderVerify = null;

        try
        {
            olderPath = await service.DownloadUpdateAsync(
                release, null, TestContext.Current.CancellationToken);
            Assert.NotNull(olderPath);

            olderVerify = Task.Run(() => service.VerifyDownloadedPackage(), TestContext.Current.CancellationToken);
            await verifierStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // B clears A's publication and publishes its own files while A is held inside verification.
            newerPath = await service.DownloadUpdateAsync(
                release, null, TestContext.Current.CancellationToken);
            Assert.NotNull(newerPath);
            Assert.Equal(newerPath, service.DownloadedZipPath);

            verifierRelease.TrySetResult();
            Assert.False(await olderVerify);

            Assert.Equal(newerPath, service.DownloadedZipPath);
            Assert.Equal(newerPath + ".sig", service.DownloadedSignaturePath);
            Assert.True(File.Exists(newerPath));
            Assert.True(File.Exists(newerPath + ".sig"));
            Assert.False(File.Exists(olderPath));
            Assert.False(File.Exists(olderPath + ".sig"));
        }
        finally
        {
            verifierRelease.TrySetResult();
            if (olderVerify is not null)
            {
                try { await olderVerify; } catch { }
            }
            TryDeleteAttemptDirectory("1");
            TryDeleteAttemptDirectory("2");
        }
    }

    [Fact]
    public async Task InstallUpdateAsync_False_WhenUpdaterMissing()
    {
        // Task 6 wiring copy ReTran.Updater.exe vào output qua ProjectReference Content — test bin
        // có thể chứa updater, nên nhánh "missing" được exercise bằng appDir trỏ vào temp rỗng.
        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        string appDir = Path.Combine(Path.GetTempPath(), $"retran-no-updater-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDir);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64),
            publicKeyBase64: key.PublicKeyBase64, appDir: appDir);
        var downloaded = await service.DownloadUpdateAsync(release, null, TestContext.Current.CancellationToken);

        try
        {
            Assert.NotNull(downloaded);
            Assert.False(File.Exists(Path.Combine(appDir, "ReTran.Updater.exe")));

            // Signature hợp lệ → vượt qua verify, fail ở bước thiếu updater → false
            Assert.False(await service.InstallUpdateAsync());
        }
        finally
        {
            TryDeleteAttemptDirectory(Path.GetFileName(Path.GetDirectoryName(downloaded)));
            try { Directory.Delete(appDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void BuildUpdaterStartInfo_UsesArgumentList_AndKeepsTrailingBackslash()
    {
        // Simulate AppContext.BaseDirectory — luôn kết thúc bằng '\'
        const string zipPath = @"C:\Temp\ReTran_Update\app.zip";
        const string appDir = @"C:\Program Files\ReTran\net10.0\";

        var psi = GitHubAppUpdateService.BuildUpdaterStartInfo(zipPath, 1234, appDir);

        // Sequence đúng contract parser của updater: [--zip, zip, --pid, pid, --app-dir, dir]
        Assert.Equal(
            new[] { "--zip", zipPath, "--pid", "1234", "--app-dir", appDir },
            psi.ArgumentList);

        // Regression: '\' cuối phải giữ nguyên verbatim — chuỗi Arguments thủ công
        // sẽ bị Windows parse là escape dấu quote và updater nhận arg có ký tự '"'.
        Assert.EndsWith(@"\", psi.ArgumentList[^1]);
        Assert.Equal(string.Empty, psi.Arguments);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void PrepareUpdaterShadowCopy_CopiesRuntimeFiles_SkipsPdbAndAppFiles()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "retran-updater-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.exe"), "exe");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.dll"), "dll");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.deps.json"), "{}");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.pdb"), "pdb");
            File.WriteAllText(Path.Combine(appDir, "ReTran.App.exe"), "app");

            var shadow = GitHubAppUpdateService.PrepareUpdaterShadowCopy(appDir);

            try
            {
                Assert.NotNull(shadow);
                // Shadow dir nằm dưới %TEMP%\ReTran_Updater (không đụng appDir — updater tự khóa file)
                Assert.StartsWith(Path.Combine(Path.GetTempPath(), "ReTran_Updater"), shadow,
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(Path.Combine(shadow, "ReTran.Updater.exe")));
                Assert.True(File.Exists(Path.Combine(shadow, "ReTran.Updater.dll")));
                Assert.True(File.Exists(Path.Combine(shadow, "ReTran.Updater.deps.json")));
                Assert.True(File.Exists(Path.Combine(shadow, "ReTran.Updater.runtimeconfig.json")));
                Assert.False(File.Exists(Path.Combine(shadow, "ReTran.Updater.pdb")));
                Assert.False(File.Exists(Path.Combine(shadow, "ReTran.App.exe")));
            }
            finally
            {
                if (shadow is not null)
                {
                    Directory.Delete(shadow, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public void PrepareUpdaterShadowCopy_ReturnsNull_WhenUpdaterFilesMissing()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "retran-updater-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            File.WriteAllText(Path.Combine(appDir, "ReTran.App.exe"), "app");

            Assert.Null(GitHubAppUpdateService.PrepareUpdaterShadowCopy(appDir));
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public void PrepareUpdaterShadowCopy_CopiesSelfContainedRuntime_Files()
    {
        // Regression hostpolicy.dll (Event 1023): updater là self-contained — chạy từ
        // shadow dir phải có đủ runtime files (liệt kê trong deps.json) + host binaries,
        // nếu không host chết trước khi managed code chạy (console flash, không lỗi).
        var appDir = Path.Combine(Path.GetTempPath(), "retran-updater-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.exe"), "exe");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.dll"), "dll");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Updater.deps.json"), """
                {
                  "targets": {
                    ".NETCoreApp,Version=v10.0": { },
                    ".NETCoreApp,Version=v10.0/win-x64": {
                      "ReTran.Updater/1.0.0": {
                        "runtime": { "ReTran.Updater.dll": { } }
                      },
                      "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12": {
                        "runtime": { "System.Fake.Runtime.dll": { } },
                        "native": { "coreclr.dll": { } }
                      }
                    }
                  }
                }
                """);

            // Runtime + host files mà self-contained host cần cạnh exe
            File.WriteAllText(Path.Combine(appDir, "System.Fake.Runtime.dll"), "rt");
            File.WriteAllText(Path.Combine(appDir, "coreclr.dll"), "native");
            File.WriteAllText(Path.Combine(appDir, "hostfxr.dll"), "host");
            File.WriteAllText(Path.Combine(appDir, "hostpolicy.dll"), "host");

            // Payload app chính — KHÔNG được copy vào shadow
            File.WriteAllText(Path.Combine(appDir, "ReTran.App.exe"), "app");
            File.WriteAllText(Path.Combine(appDir, "ReTran.Core.dll"), "core");

            var shadow = GitHubAppUpdateService.PrepareUpdaterShadowCopy(appDir);

            try
            {
                Assert.NotNull(shadow);
                Assert.True(File.Exists(Path.Combine(shadow, "ReTran.Updater.exe")));
                Assert.True(File.Exists(Path.Combine(shadow, "System.Fake.Runtime.dll")));
                Assert.True(File.Exists(Path.Combine(shadow, "coreclr.dll")));
                Assert.True(File.Exists(Path.Combine(shadow, "hostfxr.dll")));
                Assert.True(File.Exists(Path.Combine(shadow, "hostpolicy.dll")));
                Assert.False(File.Exists(Path.Combine(shadow, "ReTran.App.exe")));
                Assert.False(File.Exists(Path.Combine(shadow, "ReTran.Core.dll")));
            }
            finally
            {
                if (shadow is not null)
                {
                    Directory.Delete(shadow, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_FailureAfterSuccess_ClearsPathAndPreservesEarlierAttemptFiles()
    {
        // 30 bytes / chunk 10: attempt 1 tải trọn zip + sig; attempt 2 ghi zip rồi ném IOException giữa chừng.
        var payload = "0123456789ABCDEFGHIJ0123456789"u8.ToArray();
        using var key = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new GoodThenFailMidstreamHandler(payload, signatureBase64, chunkSize: 10, succeedBeforeFail: 2),
            publicKeyBase64: key.PublicKeyBase64);

        var firstPath = await service.DownloadUpdateAsync(release, null, TestContext.Current.CancellationToken);

        Assert.NotNull(firstPath);
        Assert.True(File.Exists(firstPath));
        Assert.Equal(firstPath, service.DownloadedZipPath);
        var firstSignaturePath = service.DownloadedSignaturePath;
        Assert.NotNull(firstSignaturePath);

        var secondResult = await service.DownloadUpdateAsync(release, null, TestContext.Current.CancellationToken);

        try
        {
            // Attempt 2 chỉ dọn file dở của chính nó; file hoàn chỉnh của attempt 1 không bị xóa nhầm.
            var updateRoot = Path.Combine(Path.GetTempPath(), "ReTran_Update");
            var secondPath = Path.Combine(updateRoot, "2", ZipAssetName);
            Assert.Null(secondResult);
            Assert.Null(service.DownloadedZipPath);
            Assert.Null(service.DownloadedSignaturePath);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(firstSignaturePath));
            Assert.False(File.Exists(secondPath));
            Assert.False(File.Exists(secondPath + ".sig"));
        }
        finally
        {
            TryDeleteAttemptDirectory("1");
            TryDeleteAttemptDirectory("2");
        }
    }

    /// <summary>
    /// Release có cả zip lẫn file .sig cạnh nhau (đúng cấu trúc release thật);
    /// chữ ký được tạo bởi <paramref name="key"/> — test nào cần sai key tự ký bằng keypair khác.
    /// </summary>
    private static ReleaseInfo SignedRelease(byte[] payload, TestSigningKey key, out string signatureBase64)
    {
        signatureBase64 = key.SignToBase64(payload);
        return Release("v1.2.3",
            new ReleaseAsset(ZipAssetName, "https://example.com/payload.zip"),
            new ReleaseAsset(ZipAssetName + ".sig", "https://example.com/payload.zip.sig"));
    }

    private static Task<string?> DownloadWithToken(
        GitHubAppUpdateService service, ReleaseInfo release, CancellationToken cancellationToken) =>
        service.DownloadUpdateAsync(release, null, cancellationToken);

    private static void TryDeleteAttemptDirectory(string? attemptId)
    {
        if (string.IsNullOrEmpty(attemptId)) return;

        var path = Path.Combine(Path.GetTempPath(), "ReTran_Update", attemptId);
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ===== HTTP doubles (pattern FreeModelSyncServiceTests) =====

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    /// <summary>
    /// Route theo URL: request .sig trả chữ ký Base64, còn lại trả payload zip theo chunk
    /// (để test progress nhiều lần thay vì 1 lần đọc trọn vẹn).
    /// <paramref name="signatureBase64"/> null mô phỏng release không có file chữ ký (404).
    /// </summary>
    private sealed class SignedZipHandler(byte[] payload, string? signatureBase64, int chunkSize = 81920)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsSignatureRequest(request))
            {
                return Task.FromResult(signatureBase64 is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signatureBase64) });
            }

            var content = new StreamContent(new ChunkedStream(payload, chunkSize));
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>
    /// Blocks the first signature request until the second attempt has completed. The gate intentionally
    /// ignores cancellation to model a non-cooperative HTTP double; the request token is captured separately.
    /// </summary>
    private sealed class StaleRunHandler(byte[] olderPayload, byte[] newerPayload) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _olderSignatureRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _zipCalls;
        private int _signatureCalls;

        public TaskCompletionSource FirstSignatureStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken OlderSignatureToken { get; private set; }

        public void ReleaseOlderSignature() => _olderSignatureRelease.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (IsSignatureRequest(request))
            {
                if (Interlocked.Increment(ref _signatureCalls) == 1)
                {
                    OlderSignatureToken = ct;
                    FirstSignatureStarted.TrySetResult();
                    await _olderSignatureRelease.Task;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("older signature"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("newer signature"),
                };
            }

            var payload = Interlocked.Increment(ref _zipCalls) == 1 ? olderPayload : newerPayload;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        }
    }

    private static bool IsSignatureRequest(HttpRequestMessage request) =>
        request.RequestUri is { AbsolutePath: var path } &&
        path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lần tải zip đầu trả payload bình thường; các lần tải zip sau trả stream ném lỗi giữa chừng
    /// (mất mạng). Chữ ký .sig luôn trả bình thường.
    /// </summary>
    private sealed class GoodThenFailMidstreamHandler(byte[] payload, string? signatureBase64, int chunkSize,
        int succeedBeforeFail) : HttpMessageHandler
    {
        private int _zipCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsSignatureRequest(request))
            {
                return Task.FromResult(signatureBase64 is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signatureBase64) });
            }

            _zipCalls++;
            Stream stream = _zipCalls == 1
                ? new ChunkedStream(payload, chunkSize)
                : new FailAfterChunksStream(payload, chunkSize, succeedBeforeFail);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Trả <paramref name="succeedBeforeFail"/> chunk đầu rồi ném IOException — mô phỏng mất mạng giữa chừng.</summary>
    private sealed class FailAfterChunksStream(byte[] data, int chunkSize, int succeedBeforeFail) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            MaybeFail();
            return _inner.Read(buffer, offset, Math.Min(count, chunkSize));
        }

        public override int Read(Span<byte> buffer)
        {
            MaybeFail();
            return _inner.Read(buffer[..Math.Min(buffer.Length, chunkSize)]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            MaybeFail();
            return _inner.ReadAsync(buffer, offset, Math.Min(count, chunkSize), ct);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            MaybeFail();
            return _inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], ct);
        }

        private void MaybeFail()
        {
            if (_reads++ >= succeedBeforeFail)
            {
                throw new IOException("Simulated connection reset mid-download.");
            }
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, chunkSize));

        public override int Read(Span<byte> buffer) =>
            _inner.Read(buffer[..Math.Min(buffer.Length, chunkSize)]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.ReadAsync(buffer, offset, Math.Min(count, chunkSize), ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], ct);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Reports { get; } = [];

        public void Report(int value) => Reports.Add(value);
    }
}
