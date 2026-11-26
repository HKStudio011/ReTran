using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReTran_App.Services.Update;

/// <summary>
/// <see cref="IAppUpdateService"/> implementation over the GitHub Releases of HKStudio011/ReTran.
/// Triển khai <see cref="IAppUpdateService"/> trên GitHub Releases của HKStudio011/ReTran.
/// Windows-only (the updater is a Windows process); every method returns a sentinel (null/false) instead of throwing.
/// Chỉ hoạt động trên Windows (updater là process Windows); các method trả sentinel (null/false) thay vì ném lỗi.
/// </summary>
public sealed class GitHubAppUpdateService : IAppUpdateService
{
    private const string RepoOwner = "HKStudio011";
    private const string RepoName = "ReTran";
    private const string UpdaterFileName = "ReTran.Updater.exe";
    private const string UpdateFolderName = "ReTran_Update";

    private readonly HttpClient _http;
    private readonly string _downloadOwnerId = Guid.NewGuid().ToString("N");

    // Attempt creation, path publication, and verification snapshots share this lock so a stale download
    // cannot clear, delete, or publish over a newer attempt. The per-service GUID keeps attempt directories
    // isolated across service instances and processes; each attempt writes only below its own directory.
    private readonly object _downloadSync = new();
    private long _downloadAttemptId;
    private long _publishedDownloadAttemptId;

    private readonly record struct DownloadSnapshot(long AttemptId, string? ZipPath, string? SignaturePath);

    /// <summary>
    /// Public key (SPKI Base64) verify chữ ký zip update — production dùng
    /// <see cref="PackageSignatureVerifier.ReleasePublicKeyBase64"/>; test inject keypair khác.
    /// </summary>
    private readonly string _signingPublicKeyBase64;
    private readonly Func<string, string, string, bool> _packageVerifier;

    /// <summary>
    /// Install directory the updater is looked up in — <c>AppContext.BaseDirectory</c> in
    /// production; tests inject an empty temp dir to exercise the missing-updater branch even
    /// though the test bin ships ReTran.Updater.exe (ProjectReference Content from Task 6 wiring).
    /// Thư mục cài đặt mà updater được tìm trong đó — <c>AppContext.BaseDirectory</c> ở production;
    /// test inject thư mục tạm rỗng để test nhánh thiếu updater dù test bin có sẵn
    /// ReTran.Updater.exe (ProjectReference Content của wiring Task 6).
    /// </summary>
    private readonly string _appDir;

    /// <inheritdoc />
    public string CurrentVersion { get; }

    /// <inheritdoc />
    public string? DownloadedZipPath { get; private set; }

    /// <summary>
    /// Path of the downloaded signature file (.sig) alongside the zip, if any.
    /// Đường dẫn file chữ ký (.sig) đã tải kèm zip (nếu có).
    /// </summary>
    internal string? DownloadedSignaturePath { get; private set; }

    /// <summary>
    /// Creates the service with the version taken from the entry assembly of the app.
    /// Tạo service với phiên bản lấy từ entry assembly của app.
    /// </summary>
    /// <param name="http">HttpClient used for every request to GitHub (GitHub requires a User-Agent header).
    /// HttpClient dùng cho mọi request tới GitHub (GitHub yêu cầu header User-Agent).</param>
    public GitHubAppUpdateService(HttpClient http)
        : this(http, Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0")
    {
    }

    // Overload nội bộ cho test: Assembly.GetEntryAssembly() trong test host trả về testhost
    // chứ không phải app — không ép được thì mọi assertion về version sẽ phụ thuộc runner.
    // appDir cho test: test bin giờ chứa sẵn ReTran.Updater.exe (Task 6 wiring) nên nhánh
    // "thiếu updater" phải chỉ vào thư mục trống thay vì AppContext.BaseDirectory.
    internal GitHubAppUpdateService(
        HttpClient http,
        string currentVersion,
        string? signingPublicKeyBase64 = null,
        string? appDir = null,
        Func<string, string, string, bool>? packageVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _http = http;
        CurrentVersion = currentVersion;
        _signingPublicKeyBase64 = signingPublicKeyBase64 ?? PackageSignatureVerifier.ReleasePublicKeyBase64;
        _packageVerifier = packageVerifier ?? PackageSignatureVerifier.Verify;
        _appDir = appDir ?? AppContext.BaseDirectory;
    }

    /// <inheritdoc />
    public async Task<ReleaseInfo?> CheckForUpdatesAsync()
    {
        // Update chỉ dành cho app Windows thật — trả null để caller báo "không kiểm tra được".
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"Retran/{CurrentVersion}");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ReleaseInfo>(json);
        }
        catch (Exception)
        {
            // Mất mạng/JSON hỏng → null để UpdateCoordinator bỏ qua lần kiểm tra này thay vì crash app.
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsUpdateAvailable(ReleaseInfo release)
    {
        // So sánh thuần (không I/O) nên không cần Windows guard — UI hiển thị trạng thái update ở mọi nền tảng.
        try
        {
            var tag = release.TagName.TrimStart('v');
            return CompareVersions(tag, CurrentVersion) > 0;
        }
        catch (Exception)
        {
            // Release rỗng/khỏng hợp lệ → coi như không có bản mới, không để exception lọt ra caller.
            return false;
        }
    }

    /// <summary>
    /// Compares two versions component by component, zero-padding missing parts (v1.1 ≡ 1.1.0.0).
    /// A component that does not parse as a number counts as 0.
    /// So sánh hai version từng bậc, pad số 0 khi thiếu thành phần (v1.1 ≡ 1.1.0.0).
    /// Thành phần không parse được thành số coi như 0.
    /// </summary>
    private static int CompareVersions(string candidate, string current)
    {
        var candidateParts = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var currentParts = current.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var maxParts = Math.Max(candidateParts.Length, currentParts.Length);

        for (var i = 0; i < maxParts; i++)
        {
            var left = i < candidateParts.Length && int.TryParse(candidateParts[i], out var p1) ? p1 : 0;
            var right = i < currentParts.Length && int.TryParse(currentParts[i], out var p2) ? p2 : 0;
            if (left != right) return left.CompareTo(right);
        }

        return 0;
    }

    /// <inheritdoc />
    public async Task<string?> DownloadUpdateAsync(
        ReleaseInfo release, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        // Tải file + ghi đĩa là thao tác Windows-only theo contract của updater.
        if (!OperatingSystem.IsWindows()) return null;

        long attemptId;
        lock (_downloadSync)
        {
            attemptId = ++_downloadAttemptId;

            // Invalidate paths when this attempt becomes latest; no partial or superseded zip is installable.
            _publishedDownloadAttemptId = 0;
            DownloadedZipPath = null;
            DownloadedSignaturePath = null;
        }

        var attemptDir = Path.Combine(
            Path.GetTempPath(),
            UpdateFolderName,
            _downloadOwnerId,
            attemptId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string? zipPath = null;
        string? signaturePath = null;
        var published = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(attemptDir);

            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase));

            if (asset is null) return null;

            // Fail-closed: release không có file .sig → không tin được zip, từ chối update.
            // (Các release trước khi triển khai ký sẽ không cập nhật được — intended.)
            var signatureAsset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, asset.Name + ".sig", StringComparison.OrdinalIgnoreCase));
            if (signatureAsset is null) return null;

            // Every attempt has distinct paths, so an old writer cannot truncate or delete a newer download.
            zipPath = Path.Combine(attemptDir, asset.Name);
            signaturePath = zipPath + ".sig";

            using var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
            request.Headers.UserAgent.ParseAdd($"Retran/{CurrentVersion}");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long readBytes = 0;
                var lastReported = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    readBytes += bytesRead;

                    // Chỉ báo khi % tăng — tránh spam progress với 81920 byte mỗi lần đọc.
                    if (totalBytes > 0 && progress is not null)
                    {
                        var pct = (int)(readBytes * 100 / totalBytes);
                        if (pct > lastReported)
                        {
                            lastReported = pct;
                            progress.Report(pct);
                        }
                    }
                }

                await fileStream.FlushAsync(cancellationToken);
            }

            // Tải chữ ký (file nhỏ, đọc trọn 1 lần — không cần progress)
            using var signatureRequest = new HttpRequestMessage(HttpMethod.Get, signatureAsset.BrowserDownloadUrl);
            signatureRequest.Headers.UserAgent.ParseAdd($"Retran/{CurrentVersion}");

            using var signatureResponse = await _http.SendAsync(signatureRequest, cancellationToken);
            if (!signatureResponse.IsSuccessStatusCode) return null;

            var signatureBytes = await signatureResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(signaturePath, signatureBytes, cancellationToken);

            lock (_downloadSync)
            {
                if (attemptId == _downloadAttemptId && !cancellationToken.IsCancellationRequested)
                {
                    _publishedDownloadAttemptId = attemptId;
                    DownloadedSignaturePath = signaturePath;
                    DownloadedZipPath = zipPath;
                    published = true;
                }
            }

            return published ? zipPath : null;
        }
        catch (Exception)
        {
            // Network, disk, and cancellation failures all invalidate this attempt; finally cleans only its files.
            return null;
        }
        finally
        {
            if (!published)
            {
                DeletePartialFile(zipPath);
                DeletePartialFile(signaturePath);
                DeleteAttemptDirectory(attemptDir);
            }
        }
    }

    // File.Delete không ném khi file không tồn tại; có thể fail nếu file bị khóa — khi đó giữ nguyên,
    // vô hại vì DownloadedZipPath đã null nên updater không thể dùng nhầm file rác.
    private static void DeletePartialFile(string? zipPath)
    {
        if (zipPath is null) return;

        try
        {
            File.Delete(zipPath);
        }
        catch (Exception)
        {
            // Không xóa được file rác trong temp (đĩa đầy/file khóa) — đã xử lý bằng cách không trỏ path tới nó.
        }
    }

    // Non-recursive by design: an attempt may remove only its own empty directory, never a sibling attempt.
    private static void DeleteAttemptDirectory(string attemptDir)
    {
        try
        {
            Directory.Delete(attemptDir, recursive: false);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup; a locked file or unexpected extra file must not affect another attempt.
        }
    }

    /// <inheritdoc />
    public bool VerifyDownloadedPackage()
    {
        // Verify đọc file + ghi đĩa (xóa rác) — Windows-only theo contract của updater.
        if (!OperatingSystem.IsWindows()) return false;

        DownloadSnapshot snapshot;
        lock (_downloadSync)
        {
            snapshot = new DownloadSnapshot(
                _publishedDownloadAttemptId, DownloadedZipPath, DownloadedSignaturePath);
        }

        try
        {
            if (snapshot.AttemptId > 0 && snapshot.ZipPath is not null && snapshot.SignaturePath is not null &&
                _packageVerifier(snapshot.ZipPath, snapshot.SignaturePath, _signingPublicKeyBase64))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or CryptographicException)
        {
            // Unreadable/locked files and verifier failures are verification failures, never coordinator faults.
            // Fall through to the same snapshot-scoped cleanup as an invalid signature.
        }

        // Fail-closed cleanup is scoped to the consistent snapshot, never to paths a newer attempt may publish.
        DeletePartialFile(snapshot.ZipPath);
        DeletePartialFile(snapshot.SignaturePath);
        lock (_downloadSync)
        {
            if (_publishedDownloadAttemptId == snapshot.AttemptId &&
                string.Equals(DownloadedZipPath, snapshot.ZipPath, StringComparison.Ordinal) &&
                string.Equals(DownloadedSignaturePath, snapshot.SignaturePath, StringComparison.Ordinal))
            {
                _publishedDownloadAttemptId = 0;
                DownloadedZipPath = null;
                DownloadedSignaturePath = null;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public Task<bool> InstallUpdateAsync()
    {
        // Updater là process Windows — ngoài Windows luôn trả false.
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);

        if (DownloadedZipPath is null || !File.Exists(DownloadedZipPath)) return Task.FromResult(false);

        // Verify chữ ký TRƯỚC khi khởi động updater (threat model: zip bị tamper trên đĩa
        // sau khi tải hoặc release bị giả mạo) — fail-closed: không xác thực được thì xóa zip.
        // Defense-in-depth: coordinator đã gọi VerifyDownloadedPackage trước đó, verify lại lần nữa ở đây.
        if (!VerifyDownloadedPackage()) return Task.FromResult(false);

        try
        {
            var appDir = _appDir;
            if (FindUpdaterPath(appDir) is null) return Task.FromResult(false);

            // Updater phải chạy từ shadow copy ngoài appDir: chạy từ chính appDir thì
            // process giữ khóa ReTran.Updater.dll → zip extract (overwrite)
            // không ghi đè được chính dll đó → "file is being used by another process".
            var shadowDir = PrepareUpdaterShadowCopy(appDir);
            if (shadowDir is null) return Task.FromResult(false);

            var psi = BuildUpdaterStartInfo(DownloadedZipPath, Environment.ProcessId, appDir);
            psi.FileName = Path.Combine(shadowDir, UpdaterFileName);

            Process.Start(psi);
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            // Không khởi động được updater (file hỏng, thiếu quyền...) → false để UI báo lỗi cài đặt.
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Copies the updater runtime files to <c>%TEMP%\ReTran_Updater</c> so the new process does not
    /// keep files in appDir locked. The updater is self-contained (SelfContained flow from the main
    /// app's publish) so runtime files + host binaries must be copied too — copying only the 4 app
    /// files makes the host die with "hostpolicy.dll not found" before managed code runs
    /// (see <see cref="CopySelfContainedRuntime"/> for the deps.json enumeration).
    /// Copy file runtime của updater sang <c>%TEMP%\ReTran_Updater</c> để process
    /// mới không giữ khóa file trong appDir. Updater là self-contained (SelfContained flow
    /// từ publish của app chính) nên phải copy kèm runtime files + host binaries — chỉ copy
    /// 4 file app thì host chết với "hostpolicy.dll not found" trước khi managed code chạy
    /// (xem <see cref="CopySelfContainedRuntime"/> cho cách liệt kê từ deps.json).
    /// </summary>
    /// <returns>The directory containing the copy; <see langword="null"/> when the copy fails (disk full/no permission).
    /// Thư mục chứa bản copy; null khi copy thất bại (đĩa đầy/thiếu quyền).</returns>
    internal static string? PrepareUpdaterShadowCopy(string appDir)
    {
        // Shadow copy là thao tác file Windows-only (contract updater).
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var shadowDir = Path.Combine(
                Path.GetTempPath(), "ReTran_Updater", $"v{Environment.ProcessId}-{Environment.TickCount64}");
            Directory.CreateDirectory(shadowDir);

            foreach (var file in Directory.EnumerateFiles(appDir, "ReTran.Updater.*"))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(shadowDir, name), overwrite: true);
            }

            // Không có exe trong bản copy → updater không chạy được, coi như prepare fail
            if (!File.Exists(Path.Combine(shadowDir, UpdaterFileName)))
                return null;

            CopySelfContainedRuntime(appDir, shadowDir);
            return shadowDir;
        }
        catch (Exception)
        {
            // Copy fail → caller trả false; thư mục dở dang best-effort để System temp dọn.
            return null;
        }
    }

    /// <summary>
    /// Copies the runtime files a self-contained updater needs next to its exe: implicit host binaries
    /// (hostfxr/hostpolicy are not in deps.json) + every managed/native file listed in the deps.json
    /// targets. Files missing from appDir are skipped (best-effort) — the updater already ran from
    /// appDir before, so if a truly-needed file is missing the update was broken from install time.
    /// Copy runtime files mà updater self-contained cần cạnh exe: host binaries implicit
    /// (hostfxr/hostpolicy không nằm trong deps.json) + mọi managed/native file liệt kê
    /// trong targets của deps.json. File thiếu trong appDir được bỏ qua (best-effort) —
    /// updater đã chạy từ appDir trước đó nên nếu file thật sự cần mà thiếu thì update
    /// đã hỏng từ lúc cài.
    /// </summary>
    private static void CopySelfContainedRuntime(string appDir, string shadowDir)
    {
        foreach (var host in new[] { "hostfxr.dll", "hostpolicy.dll" })
        {
            CopyIfExists(appDir, shadowDir, host);
        }

        var depsPath = Path.Combine(appDir, Path.GetFileNameWithoutExtension(UpdaterFileName) + ".deps.json");
        if (!File.Exists(depsPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
            foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
            {
                foreach (var package in target.Value.EnumerateObject())
                {
                    // Key của runtime/native chính là tên file flattened trong appDir publish
                    if (package.Value.TryGetProperty("runtime", out var runtime))
                    {
                        foreach (var file in runtime.EnumerateObject())
                        {
                            CopyIfExists(appDir, shadowDir, file.Name);
                        }
                    }

                    if (package.Value.TryGetProperty("native", out var native))
                    {
                        foreach (var file in native.EnumerateObject())
                        {
                            CopyIfExists(appDir, shadowDir, file.Name);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // deps.json hỏng/không đọc được — giữ 4 file app; updater sẽ fail rõ ràng
            // (Event 1023) thay vì nuốt lỗi im lặng.
        }
    }

    private static void CopyIfExists(string appDir, string shadowDir, string fileName)
    {
        var src = Path.Combine(appDir, fileName);
        if (File.Exists(src))
        {
            File.Copy(src, Path.Combine(shadowDir, fileName), overwrite: true);
        }
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> that launches the updater with exactly the sequence
    /// <c>--zip &lt;zip&gt; --pid &lt;pid&gt; --app-dir &lt;dir&gt;</c> the updater's parser requires.
    /// Xây <see cref="ProcessStartInfo"/> khởi chạy updater với đúng sequence
    /// <c>--zip &lt;zip&gt; --pid &lt;pid&gt; --app-dir &lt;dir&gt;</c> mà parser của updater yêu cầu.
    /// </summary>
    /// <param name="zipPath">Path of the downloaded update zip. Đường dẫn zip update đã tải.</param>
    /// <param name="parentPid">PID of the app so the updater waits for exit before overwriting.
    /// PID của app để updater chờ process thoát trước khi ghi đè.</param>
    /// <param name="appDir">Install directory of the app (usually <c>AppContext.BaseDirectory</c>).
    /// Thư mục cài đặt của app (thường là <c>AppContext.BaseDirectory</c>).</param>
    internal static ProcessStartInfo BuildUpdaterStartInfo(string zipPath, int parentPid, string appDir)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };

        // Dùng ArgumentList thay vì nối chuỗi `Arguments` thủ công: appDir luôn kết thúc bằng '\',
        // nên `--app-dir "C:\...\net10.0\"` bị Windows parse theo odd-backslash rule — '\' cuối
        // escape dấu quote đóng, updater nhận arg thừa ký tự '"' → backup bị skip và extract
        // tạo thư mục lỗi. ArgumentList để .NET tự quote từng phần tử đúng chuẩn CommandLineToArgvW.
        psi.ArgumentList.Add("--zip");
        psi.ArgumentList.Add(zipPath);

        // --pid để updater chờ app chết hẳn trước khi ghi đè file đang chạy.
        psi.ArgumentList.Add("--pid");
        psi.ArgumentList.Add(parentPid.ToString());

        psi.ArgumentList.Add("--app-dir");
        psi.ArgumentList.Add(appDir);

        return psi;
    }

    private static string? FindUpdaterPath(string appDir)
    {
        var candidates = new[]
        {
            Path.Combine(appDir, UpdaterFileName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
