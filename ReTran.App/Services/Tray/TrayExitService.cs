using Microsoft.Extensions.Logging;
using ReTran_App.Services.Update;

namespace ReTran_App.Services.Tray;

/// <summary>
/// Clean exit path for the tray "Thoát" command: stops the app services before quitting the app.
/// Đường thoát sạch cho lệnh "Thoát" trên tray: dừng các service của app trước khi thoát ứng dụng.
/// </summary>
public interface ITrayExit
{
    /// <summary>
    /// Stops the pipeline and core process tree, then quits the application.
    /// Dừng pipeline và toàn bộ cây process core, rồi thoát ứng dụng.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancelled by the host to bound the service-stop phase. Host huỷ để giới hạn pha dừng service.
    /// </param>
    /// <returns>
    /// A task that completes once the quit has been requested. Task hoàn tất khi lệnh thoát được gửi đi.
    /// </returns>
    Task ExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Exits the app from the tray without orphaning the capture pipeline. Call order is a hard rule:
/// <see cref="IUpdateStopper.StopServicesAsync"/> (stops the pipeline and kills the ReTran.Core process
/// tree, including its multi-gigabyte Python OCR sidecar) MUST run BEFORE
/// <see cref="IAppShutdown.Quit"/> — quitting first would leave Core and the sidecar running after the
/// app dies. If the stop throws, the failure is logged and the app quits anyway: the user asked to
/// leave, and an app that refuses to close is worse than one that leaks a process tree.
/// Thoát app từ tray mà không mồ côi pipeline capture. Thứ tự gọi là quy tắc cứng:
/// <see cref="IUpdateStopper.StopServicesAsync"/> (dừng pipeline và kill toàn bộ cây process ReTran.Core,
/// gồm sidecar Python OCR nhiều GB của nó) PHẢI chạy TRƯỚC <see cref="IAppShutdown.Quit"/> — Quit trước sẽ
/// để Core và sidecar sống tiếp sau khi app chết. Nếu bước dừng ném lỗi, lỗi được log và app vẫn quit:
/// người dùng đã bấm thoát, và app không chịu đóng còn tệ hơn app để lọt process tree.
/// </summary>
public sealed class TrayExitService : ITrayExit
{
    private readonly IUpdateStopper _stopper;
    private readonly IAppShutdown _shutdown;
    private readonly ILogger<TrayExitService> _log;

    /// <summary>
    /// Creates the exit service over the shared update stop/quit seams.
    /// Tạo service thoát trên chính các seam stop/quit dùng cho luồng cập nhật.
    /// </summary>
    /// <param name="stopper">Stops the pipeline and core tree. Dừng pipeline và cây process core.</param>
    /// <param name="shutdown">Quits the application. Đóng ứng dụng.</param>
    /// <param name="log">Logger for stop failures. Logger cho lỗi khi dừng service.</param>
    public TrayExitService(IUpdateStopper stopper, IAppShutdown shutdown, ILogger<TrayExitService> log)
    {
        _stopper = stopper;
        _shutdown = shutdown;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task ExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stopper.StopServicesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Log nhưng vẫn Quit: người dùng đã bấm thoát. Core/sidecar có thể mồ côi — người dùng
            // có thể kết thúc tiến trình còn sót trong Task Manager.
            _log.LogError(ex, "Failed to stop services during tray exit; the core process tree may survive the quit.");
        }

        _shutdown.Quit();
    }
}
