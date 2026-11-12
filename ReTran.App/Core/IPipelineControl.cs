namespace ReTran_App.Core;

/// <summary>
/// The pipeline surface the floating menu and stats consume: start/stop, source listing, run state.
/// Implemented by <see cref="PipelineController"/> so both can be faked in tests.
/// Bề mặt pipeline mà menu nổi và thống kê dùng: start/dừng, liệt kê nguồn, trạng thái chạy.
/// Do <see cref="PipelineController"/> implement để test fake được.
/// </summary>
public interface IPipelineControl
{
    /// <summary>Whether the pipeline is currently running. Pipeline có đang chạy hay không.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// The capture source of the current session, or <see langword="null"/> when no session is active.
    /// Nguồn capture của phiên hiện tại, hoặc <see langword="null"/> khi không có phiên đang chạy.
    /// </summary>
    SourceSelection? ActiveSource { get; }

    /// <summary>Starts a pipeline session for the given source. Khởi động phiên pipeline cho nguồn đã cho.</summary>
    Task StartAsync(SourceSelection source, CancellationToken ct);

    /// <summary>Stops the current session (Core treats the stop as idempotent). Dừng phiên hiện tại (Core coi lệnh dừng là idempotent).</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Lists the capturable monitors and windows. Liệt kê các màn hình và cửa sổ có thể capture.</summary>
    Task<(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows)> ListSourcesAsync(CancellationToken ct);

    /// <summary>
    /// Raised whenever the run state or active source changes — a user start/stop and an abrupt
    /// pipeline stop alike. Được phát mỗi khi trạng thái chạy hoặc nguồn thay đổi — cả start/dừng do
    /// người dùng lẫn dừng đột xuất.
    /// </summary>
    event Action? StateChanged;

    /// <summary>
    /// Raised when Core reports the loop stopped unexpectedly (sidecar died, capture error). A
    /// normal user stop does not raise it. Được phát khi Core báo loop dừng đột xuất (sidecar chết,
    /// lỗi capture). Dừng bình thường do người dùng không phát event này.
    /// </summary>
    event Action<bool, string?>? PipelineStateChanged;
}
