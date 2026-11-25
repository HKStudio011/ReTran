using System.Text.Json.Serialization;

namespace ReTran_App.Services.Update;

/// <summary>
/// Information about a GitHub Release — maps 1:1 from the GitHub API JSON (releases/latest).
/// Thông tin một GitHub Release — map trực tiếp từ JSON của GitHub API (releases/latest).
/// </summary>
/// <param name="TagName">The release tag, e.g. "v1.2.3". Tag của release, ví dụ "v1.2.3".</param>
/// <param name="HtmlUrl">URL of the release page on GitHub. URL trang release trên GitHub.</param>
/// <param name="Body">Release notes body; may be null. Nội dung release notes; có thể null.</param>
/// <param name="Assets">Attached files (the install zip). Danh sách file đính kèm (zip cài đặt).</param>
public record ReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] List<ReleaseAsset> Assets);

/// <summary>
/// One attached file of a release — keeps only the two fields needed to download the update.
/// Một file đính kèm của release — chỉ giữ đúng 2 trường cần để tải bản update.
/// </summary>
/// <param name="Name">File name, e.g. "retran-1.2.3-win-x64.zip". Tên file, ví dụ "retran-1.2.3-win-x64.zip".</param>
/// <param name="BrowserDownloadUrl">Direct download URL from GitHub. URL tải trực tiếp từ GitHub.</param>
public record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

/// <summary>
/// Service that checks for, downloads, and installs application updates.
/// Dịch vụ kiểm tra, tải và cài bản update của ứng dụng.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>
    /// The current app version (assembly version).
    /// Phiên bản hiện tại của app (assembly version).
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Returns the latest release from GitHub; <see langword="null"/> on error or when not on Windows.
    /// Trả release mới nhất từ GitHub; <see langword="null"/> khi lỗi hoặc không phải Windows.
    /// </summary>
    Task<ReleaseInfo?> CheckForUpdatesAsync();

    /// <summary>
    /// Whether a release newer than <see cref="CurrentVersion"/> exists — compares tags with 'v' stripped and numbers zero-padded.
    /// Có bản mới hơn <see cref="CurrentVersion"/> không — so tag đã strip 'v' và pad số 0.
    /// </summary>
    bool IsUpdateAvailable(ReleaseInfo release);

    /// <summary>
    /// Downloads the zip of <paramref name="release"/> to the temp directory, reporting progress via <paramref name="progress"/>;
    /// returns the file path or <see langword="null"/> on error.
    /// Tải zip của <paramref name="release"/> về thư mục tạm, báo tiến độ qua <paramref name="progress"/>;
    /// trả về đường dẫn file hoặc <see langword="null"/> khi lỗi.
    /// </summary>
    /// <param name="release">The release whose zip should be downloaded. Release cần tải zip.</param>
    /// <param name="progress">Progress reporter, may be null. Trình báo tiến độ, có thể null.</param>
    /// <param name="cancellationToken">Cancels HTTP and file operations for this attempt. Huỷ các thao tác HTTP và file của attempt này.</param>
    /// <returns>The downloaded file path, or <see langword="null"/> on error, cancellation, or when superseded by a newer attempt. Đường dẫn file đã tải, hoặc <see langword="null"/> khi lỗi, bị huỷ hoặc bị attempt mới thay thế.</returns>
    Task<string?> DownloadUpdateAsync(
        ReleaseInfo release, IProgress<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Path of the downloaded zip, if any.
    /// Đường dẫn zip đã tải (nếu có).
    /// </summary>
    string? DownloadedZipPath { get; }

    /// <summary>
    /// Verifies the signature (.sig) of the downloaded zip; fail-closed — on failure deletes the bad zip and returns <see langword="false"/>.
    /// Xác thực chữ ký (.sig) của zip đã tải — fail-closed: khi fail xóa zip rác và trả <see langword="false"/>.
    /// </summary>
    bool VerifyDownloadedPackage();

    /// <summary>
    /// Launches ReTran.Updater.exe to install the update; <see langword="false"/> on error or when not on Windows.
    /// Khởi chạy ReTran.Updater.exe để cài bản update; <see langword="false"/> khi lỗi hoặc không phải Windows.
    /// </summary>
    Task<bool> InstallUpdateAsync();
}
