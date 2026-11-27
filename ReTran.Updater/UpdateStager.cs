using System.IO.Compression;
using System.Text.Json;

namespace ReTran.Updater;

/// <summary>
/// Safe update steps: extract the zip to staging, wait until target files are released,
/// and only then write into the app folder — extracting straight into a half-updated
/// appDir leaves a mix of self-contained + framework-dependent runtimes (causes the
/// host ".NET missing" error because a self-contained hostfxr next to the exe makes
/// the apphost resolve the framework from the wrong place).
/// Các bước update an toàn: trích zip sang staging, chờ file đích hết lock,
/// chỉ sau đó mới ghi vào thư mục app — extract trực tiếp vào appDir dở dang
/// sẽ để lại folder lẫn lộn runtime self-contained + framework-dependent (gây
/// lỗi host ".NET missing" vì hostfxr self-contained cạnh exe làm apphost
/// chọn sai chỗ phân giải framework).
/// </summary>
internal static class UpdateStager
{
    /// <summary>
    /// Creates an extraction-owned staging folder under %TEMP% so concurrent updaters cannot wipe or
    /// overwrite each other's files. The caller removes only the returned directory after the update;
    /// extraction failure removes only this invocation's partial directory and rethrows the original error.
    /// Tạo thư mục staging riêng cho từng lần trích dưới %TEMP% để các updater đồng thời không xóa hoặc
    /// ghi đè file của nhau. Caller chỉ xóa đúng thư mục được trả về sau khi cập nhật; nếu trích lỗi,
    /// chỉ thư mục dở của lần gọi này bị xóa và exception gốc được ném lại.
    /// </summary>
    internal static string ExtractToStaging(string zipPath)
    {
        var staging = Path.Combine(
            Path.GetTempPath(), "ReTran_Update", $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            return staging;
        }
        catch
        {
            // The caller never receives this path on failure, so clean only this invocation's owned staging.
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// The zip must be a self-contained build: runtimeconfig has <c>includedFrameworks</c>
    /// and <c>hostfxr.dll</c> next to the exe. Overwriting appDir with a FDD zip switches
    /// the install to framework-dependent mid-flight — a self-contained hostfxr next to the
    /// exe mixed with system framework resolution → ".NET missing"/hostfxr error on machines
    /// without the runtime (bug seen before — see class comment).
    /// Zip phải là bản self-contained: runtimeconfig có <c>includedFrameworks</c>
    /// và <c>hostfxr.dll</c> cạnh exe. Zip framework-dependent ghi đè lên appDir sẽ
    /// chuyển cài đặt sang FDD giữa chừng — hostfxr self-contained cạnh exe lẫn lộn
    /// với phân giải framework hệ thống → lỗi ".NET missing"/hostfxr ở máy không
    /// cài runtime (bug đã từng xảy ra — xem comment class).
    /// </summary>
    internal static bool VerifySelfContained(string stagingDir)
    {
        var runtimeConfigPath = Path.Combine(stagingDir, "ReTran.App.runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath) || !File.Exists(Path.Combine(stagingDir, "hostfxr.dll")))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
            return doc.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
                && runtimeOptions.TryGetProperty("includedFrameworks", out var frameworks)
                && frameworks.ValueKind == JsonValueKind.Array
                && frameworks.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until every file in staging can be opened for writing (exclusive) at
    /// <paramref name="appDir"/> within <paramref name="timeout"/>.
    /// Chờ mọi file trong staging đã tồn tại ở <paramref name="appDir"/> mở ghi
    /// exclusive được (hết lock) trong <paramref name="timeout"/>.
    /// </summary>
    /// <returns><see langword="true"/> when all target files are writable; false if the timeout elapses while still locked.</returns>
    /// <exception cref="UnauthorizedAccessException">Access denied (not a lock) — retrying cannot help.</exception>
    internal static bool WaitUntilWritable(string stagingDir, string appDir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var allWritable = true;
            foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(appDir, Path.GetRelativePath(stagingDir, src));
                if (!File.Exists(dst)) continue;

                try
                {
                    // FileShare.None: file đang được process khác load/mở sẽ ném IOException
                    using var _ = File.Open(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    allWritable = false;
                    break;
                }
            }

            if (allWritable) return true;
            if (DateTime.UtcNow >= deadline) return false;

            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// Copies every file in staging (subdirectories included) into appDir, overwriting duplicates.
    /// Overwrite-only by design — never delete files in appDir: user state (.venv, models) must survive updates (spec 2026-11-03-first-run-setup-wizard §5.2).
    /// Copy toàn bộ file staging (kể cả thư mục con) vào appDir, ghi đè file trùng.
    /// Chỉ ghi đè theo thiết kế — không bao giờ xoá file trong appDir.
    /// </summary>
    internal static void CopyDirectory(string stagingDir, string appDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(stagingDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(appDir, Path.GetRelativePath(stagingDir, dir)));
        }

        foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(appDir, Path.GetRelativePath(stagingDir, src));
            File.Copy(src, dst, overwrite: true);
        }
    }
}
