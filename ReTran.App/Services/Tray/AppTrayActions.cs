using ReTran_App.Core;
using ReTran_App.Services.OverlayWindow;
using ReTran_App.Services.Window;

namespace ReTran_App.Services.Tray;

/// <summary>
/// The <see cref="ITrayActions"/> the real tray drives: it maps the menu operations onto the app's
/// own singletons — the game-overlay service, the capture pipeline, the primary-window bridge and
/// the clean-exit path. Pure glue: every decision (what a toggle means, when a close is allowed,
/// what stopping the app involves) lives in those services, so this class has no branches and no
/// state of its own.
/// Cài đặt <see cref="ITrayActions"/> mà khay thật điều khiển: nó ánh xạ các thao tác menu lên
/// chính các singleton của app — service overlay game, pipeline capture, bridge cửa sổ chính và
/// đường thoát sạch. Thuần là glue: mọi quyết định (một toggle nghĩa là gì, khi nào cho phép đóng,
/// dừng app gồm những gì) đều nằm trong các service đó, nên class này không có nhánh rẽ và không
/// giữ trạng thái.
/// </summary>
/// <remarks>
/// Runs on the UI thread: the tray's message-only window is created there, so its commands are
/// delivered there (see <see cref="TrayIconService"/>). It is built when <see cref="TrayIconService"/>
/// is FIRST RESOLVED — it is a constructor dependency of the service, not something Start looks up —
/// and the only thing that ever resolves <see cref="TrayIconService"/> is the primary-window hook, so
/// an app whose window hook never runs never touches it.
/// Chạy trên UI thread: cửa sổ message-only của tray được tạo ở đó nên lệnh của nó được giao ở đó
/// (xem <see cref="TrayIconService"/>). Nó được tạo khi <see cref="TrayIconService"/> được RESOLVE
/// lần ĐẦU TIÊN — nó là dependency của constructor service đó, không phải thứ Start tra cứu — và thứ
/// duy nhất resolve <see cref="TrayIconService"/> là hook cửa sổ chính, nên app mà hook cửa sổ không
/// chạy sẽ không đụng tới nó.
/// </remarks>
public sealed class AppTrayActions : ITrayActions
{
    private readonly OverlayWindowService _overlay;
    private readonly PipelineController _pipeline;
    private readonly AppWindowBridge _window;
    private readonly ITrayExit _exit;

    /// <summary>
    /// Wires the tray menu onto the four app services it operates on.
    /// Nối menu khay với bốn service của app mà nó thao tác lên.
    /// </summary>
    /// <param name="overlay">Game overlay whose toggle entry flips. Overlay game mà mục toggle sẽ bật/tắt.</param>
    /// <param name="pipeline">Capture/OCR pipeline whose run state gates the "next start" notice. Pipeline capture/OCR mà trạng thái chạy quyết định thông báo "lần Start kế tiếp".</param>
    /// <param name="window">Primary window bridge: reopen the window, and flag a real exit. Bridge cửa sổ chính: mở lại cửa sổ, và đánh dấu thoát thật.</param>
    /// <param name="exit">Stops the services and quits the app. Dừng các service rồi thoát app.</param>
    public AppTrayActions(OverlayWindowService overlay, PipelineController pipeline,
        AppWindowBridge window, ITrayExit exit)
    {
        _overlay = overlay;
        _pipeline = pipeline;
        _window = window;
        _exit = exit;
    }

    /// <summary>
    /// Reads the overlay's live state — the tray menu asks on every right-click and never caches it.
    /// Đọc trạng thái sống của overlay — menu khay hỏi mỗi lần click chuột phải, không bao giờ cache.
    /// </summary>
    public bool OverlayIsOn => _overlay.IsEnabled;

    /// <summary>
    /// Whether the capture/OCR pipeline is running; the tray uses it to explain that an overlay
    /// switched on while the pipeline is stopped only appears at the next start.
    /// Pipeline capture/OCR có đang chạy; khay dùng giá trị này để giải thích rằng overlay bật lúc
    /// pipeline đang dừng chỉ hiện ở lần Start kế tiếp.
    /// </summary>
    public bool PipelineRunning => _pipeline.IsRunning;

    /// <summary>
    /// Turns the game overlay on or off through the overlay service, which also persists the choice
    /// (<c>OverlayOnGame</c>). Allowed to throw — the tray catches it, logs the detail and shows the
    /// user one localized error (spec §Báo lỗi, D12).
    /// Bật/tắt overlay game qua service overlay; service này đồng thời lưu lựa chọn
    /// (<c>OverlayOnGame</c>). Được phép ném lỗi — khay bắt lỗi, log chi tiết và cho người dùng một
    /// thông điệp lỗi đã dịch (spec §Báo lỗi, D12).
    /// </summary>
    /// <param name="on">Target state. Trạng thái đích.</param>
    public void SetOverlay(bool on) => _overlay.SetEnabled(on);

    /// <summary>
    /// Shows the primary window again — the reverse leg of close-to-tray (left click or "Mở ReTran").
    /// Hiện lại cửa sổ chính — chiều ngược của close-to-tray (click chuột trái hoặc "Mở ReTran").
    /// </summary>
    public void OpenAppWindow() => _window.Restore();

    /// <summary>
    /// Quits the app from the tray: flag the real exit on the window bridge first, then stop the
    /// services and shut the app down.
    /// Thoát app từ tray: đánh dấu thoát thật trên bridge cửa sổ TRƯỚC, rồi dừng các service và đóng app.
    /// </summary>
    /// <remarks>
    /// The order is the requirement, not an implementation detail. <c>IAppShutdown.Quit()</c> closes
    /// the window, and the bridge's Closing handler cancels a close unless the app has already begun
    /// a real exit — so without <see cref="AppWindowBridge.BeginExit"/> first, that quit would be
    /// turned back into hide-to-tray and the app would never exit.
    /// Thứ tự là YÊU CẦU, không phải chi tiết cài đặt. <c>IAppShutdown.Quit()</c> đóng cửa sổ, còn
    /// handler Closing của bridge chỉ cho đóng khi app đã bắt đầu thoát thật — nên nếu không gọi
    /// <see cref="AppWindowBridge.BeginExit"/> trước, lệnh quit sẽ bị biến ngược thành ẩn xuống khay
    /// và app không bao giờ thoát.
    /// A failure of the stop/quit phase is not swallowed here: <c>TrayExitService</c> logs a failed
    /// stop and quits anyway, and the caller (<see cref="TrayIconService"/>) observes this task and
    /// reports anything else to the user.
    /// Lỗi của pha dừng/quit không bị nuốt ở đây: <c>TrayExitService</c> log lỗi dừng và vẫn quit,
    /// còn nơi gọi (<see cref="TrayIconService"/>) theo dõi task này và báo mọi lỗi khác cho người
    /// dùng.
    /// </remarks>
    /// <returns>
    /// A task that completes once the quit has been requested. Task hoàn tất khi lệnh thoát được gửi đi.
    /// </returns>
    public async Task ExitApp()
    {
        _window.BeginExit();
        await _exit.ExitAsync(CancellationToken.None);
    }
}
