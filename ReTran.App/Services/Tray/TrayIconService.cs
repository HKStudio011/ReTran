using Microsoft.Extensions.Logging;
using ReTran_App.Services;

namespace ReTran_App.Services.Tray;

/// <summary>
/// The app-side operations a tray menu entry can trigger, in both directions: the tray reads the
/// current overlay/pipeline state to render its check marks and calls back to change it.
/// Các thao tác phía app mà một mục menu khay có thể kích hoạt, chạy cả hai chiều: khay đọc trạng
/// thái overlay/pipeline hiện tại để vẽ dấu tick và gọi ngược lại để thay đổi chúng.
/// </summary>
public interface ITrayActions
{
    /// <summary>
    /// Whether the overlay is currently enabled; drives the checked state of the toggle entry.
    /// Overlay đang bật hay không; quyết định trạng thái tick của mục toggle.
    /// </summary>
    bool OverlayIsOn { get; }

    /// <summary>
    /// Whether the capture/OCR pipeline is running; an overlay set while it is stopped only takes
    /// effect on the next start.
    /// Pipeline capture/OCR đang chạy hay không; overlay bật lúc pipeline dừng chỉ có hiệu lực ở
    /// lần Start kế tiếp.
    /// </summary>
    bool PipelineRunning { get; }

    /// <summary>
    /// Turns the overlay on or off. Throwing is allowed — the tray catches it, logs the detail and
    /// shows the user one localized error message (spec §Báo lỗi, D12).
    /// Bật/tắt overlay. Được phép ném lỗi — khay bắt lỗi, log chi tiết và cho người dùng thấy một
    /// thông điệp lỗi đã dịch (spec §Báo lỗi, D12).
    /// </summary>
    /// <param name="on">Target state. Trạng thái đích.</param>
    void SetOverlay(bool on);

    /// <summary>
    /// Shows the primary app window again (the close-to-tray counterpart of pressing X).
    /// Hiện lại cửa sổ chính của app (chiều ngược lại của close-to-tray khi bấm X).
    /// </summary>
    void OpenAppWindow();

    /// <summary>
    /// Stops the app's services and quits the application.
    /// Dừng các service của app rồi thoát ứng dụng.
    /// </summary>
    /// <returns>
    /// A task that completes once the quit has been requested. Task hoàn tất khi lệnh thoát được gửi đi.
    /// </returns>
    Task ExitApp();
}

/// <summary>
/// Owns the tray icon for the app's lifetime: draws it (<see cref="TrayIconSource"/>), registers it
/// with the <see cref="ITrayHost"/>, builds its context menu from the current app state and routes
/// the chosen entry to an <see cref="ITrayActions"/> implementation. It touches no window, no overlay
/// and no pipeline directly — every state it reports or changes goes through those seams, so it stays
/// unit-testable without a real notification area. A command that fails is reported to the user, not
/// just logged (spec §Báo lỗi, D12). Not thread-safe: <see cref="Start"/>,
/// <see cref="Stop"/> and the command handler all run on the UI thread, where the host's message-only
/// window lives. Nothing here may throw back into that window procedure — <see cref="Start"/>, the
/// command handler and <see cref="BuildMenu"/> are entry points the host or the app lifecycle calls
/// bare, and every log write goes through <see cref="TryLog"/> for the same reason.
/// Sở hữu icon khay trong suốt đời app: vẽ icon (<see cref="TrayIconSource"/>), đăng ký với
/// <see cref="ITrayHost"/>, dựng menu ngữ cảnh từ trạng thái app hiện tại và chuyển mục được chọn
/// tới một cài đặt <see cref="ITrayActions"/>. Bản thân service không đụng trực tiếp cửa sổ, overlay
/// hay pipeline — mọi trạng thái nó báo cáo hoặc thay đổi đều đi qua các seam đó, nên kiểm thử được
/// mà không cần khu vực thông báo thật. Lệnh thất bại được báo cho người dùng, không chỉ log
/// (spec §Báo lỗi, D12). Không thread-safe: <see cref="Start"/>, <see cref="Stop"/> và handler lệnh
/// đều chạy trên UI thread, nơi cửa sổ message-only của host tồn tại. Không gì ở đây được ném lỗi
/// ngược lại window procedure đó — <see cref="Start"/>, handler lệnh và <see cref="BuildMenu"/> là
/// các entry điểm mà host hoặc lifecycle của app gọi trần, và mọi lần ghi log đi qua
/// <see cref="TryLog"/> vì cùng lý do.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ITrayHost _host;
    private readonly ITrayActions _actions;
    private readonly I18nService _L;
    private readonly AppNotifier _notifier;
    private readonly ILogger<TrayIconService> _log;

    /// <summary>The live icon, or <see langword="null"/> while the tray is stopped. Icon đang sống, hoặc <see langword="null"/> khi khay đã dừng.</summary>
    private TrayIconSource? _icon;

    /// <summary>
    /// Creates the service; nothing is placed in the notification area until <see cref="Start"/>.
    /// Tạo service; chưa có gì được đặt ở khu vực thông báo cho tới khi <see cref="Start"/>.
    /// </summary>
    /// <param name="host">Tray host that registers the icon and raises menu commands. Tray host đăng ký icon và phát lệnh menu.</param>
    /// <param name="actions">App-side operations behind the menu entries. Các thao tác phía app đứng sau các mục menu.</param>
    /// <param name="L">Localized labels, read at menu-build time so a language switch is picked up. Nhãn đã dịch, đọc lúc dựng menu để đổi ngôn ngữ có hiệu lực ngay.</param>
    /// <param name="notifier">Notifies the user when a menu choice needs explaining. Thông báo cho người dùng khi một lựa chọn trong menu cần được nói rõ.</param>
    /// <param name="log">Logger for command failures. Logger cho lỗi khi chạy lệnh.</param>
    public TrayIconService(ITrayHost host, ITrayActions actions, I18nService L,
        AppNotifier notifier, ILogger<TrayIconService> log)
    {
        _host = host;
        _actions = actions;
        _L = L;
        _notifier = notifier;
        _log = log;
    }

    /// <summary>
    /// Registers the icon, drawing a fresh <see cref="TrayIconSource"/> after every <see cref="Stop"/>;
    /// a second call while already registered does nothing (idempotent). This method NEVER throws: it
    /// runs inside the window-lifecycle hook, where a throw over a cosmetic notification-area icon
    /// would take the app down. If registration fails the attempt is rolled back completely — the HICON
    /// is destroyed and the command handler detached — so the service is back in the not-started state
    /// and a later <see cref="Start"/> can retry.
    /// Đăng ký icon, vẽ <see cref="TrayIconSource"/> mới sau mỗi lần <see cref="Stop"/>; gọi lần hai khi
    /// đã đăng ký thì không làm gì (idempotent). Hàm này KHÔNG BAO GIỜ ném lỗi: nó chạy trong hook
    /// lifecycle của cửa sổ, nơi một exception chỉ vì icon ở khu vực thông báo sẽ giết app. Nếu việc
    /// đăng ký thất bại, lần thử được hoàn tác toàn bộ — HICON bị hủy và handler lệnh bị gỡ — để service
    /// trở về trạng thái chưa-start và lần <see cref="Start"/> sau thử lại được.
    /// </summary>
    public void Start()
    {
        if (_icon is not null)
        {
            TryLog(() => _log.LogDebug("Tray icon already registered; Start ignored."));
            return;
        }

        TrayIconSource? icon = null;
        try
        {
            // Attach the handler BEFORE registering: between NIM_ADD and the first right-click nothing
            // may be unhandled, and one handler per registration keeps Start/Stop symmetric.
            // Gắn handler TRƯỚC khi đăng ký: giữa NIM_ADD và cú click đầu tiên không được có lệnh nào
            // không được xử lý, và mỗi lần đăng ký đúng một handler giữ Start/Stop đối xứng.
            _host.CommandSelected += OnCommandSelected;
            icon = new TrayIconSource();
            _host.Create(icon.Handle, _L["tray.tip"], BuildMenu);
        }
        catch (Exception ex)
        {
            // Roll the attempt back: an HICON left behind here leaks one per retry, and an _icon left
            // set would make the idempotence guard skip every later Start — the tray would never appear.
            // Detaching a handler that was never attached is a no-op.
            // Hoàn tác lần thử: HICON bị bỏ lại sẽ rò rỉ một handle mỗi lần retry, còn _icon đã gán sẽ
            // khiến guard idempotent bỏ qua mọi lần Start sau — tray không bao giờ hiện. Gỡ handler chưa
            // gắn là no-op.
            icon?.Dispose();
            _host.CommandSelected -= OnCommandSelected;
            TryLog(() => _log.LogError(ex, "Failed to register the tray icon; a later Start will retry."));
            return;
        }

        // Assigned only once Create returned normally — the guard above must protect real
        // registrations only, never a failed attempt. Chỉ gán khi Create trả về bình thường — guard ở
        // trên phải bảo vệ ĐĂNG KÝ THẬT, không phải một lần thử hỏng.
        _icon = icon;
        // IsCreated keeps the success log honest: a host that is already disposed no-ops Create without
        // throwing, and claiming "registered" would send the next reader hunting for a phantom icon.
        // IsCreated giữ log thành công trung thực: host đã dispose thì Create no-op mà không ném lỗi, và
        // báo "registered" sẽ khiến người đọc sau đi tìm icon ma.
        TryLog(() => _log.LogInformation(_host.IsCreated
            ? "Tray icon registered."
            : "Tray host did not take the icon (host disposed or its Win32 call failed); icon and menu will not appear."));
    }

    /// <summary>
    /// Removes the icon from the notification area and destroys its handle (idempotent — a call while
    /// already stopped does nothing). The host is NOT disposed: it is a shared singleton owned by DI.
    /// Gỡ icon khỏi khu vực thông báo và hủy handle của nó (idempotent — gọi khi đã dừng thì không làm
    /// gì). Host KHÔNG bị dispose: nó là singleton dùng chung do DI sở hữu.
    /// </summary>
    public void Stop()
    {
        var icon = _icon;
        if (icon is null) return;
        _icon = null;

        _host.CommandSelected -= OnCommandSelected;
        // Remove before DestroyIcon: an icon that is still registered must not have its handle
        // destroyed underneath it. Gỡ khỏi tray trước khi DestroyIcon: icon còn đăng ký mà đã bị hủy
        // handle thì tray sẽ vẽ lại handle đã chết.
        _host.Remove();
        icon.Dispose();
        TryLog(() => _log.LogInformation("Tray icon removed."));
    }

    /// <summary>
    /// Builds the context menu from the CURRENT app state: the host calls this on every right-click,
    /// so <see cref="ITrayActions.OverlayIsOn"/> is read live here and must never be cached.
    /// Dựng menu ngữ cảnh từ trạng thái app HIỆN TẠI: host gọi hàm này mỗi lần click chuột phải, nên
    /// <see cref="ITrayActions.OverlayIsOn"/> phải đọc trực tiếp ở đây, không được cache.
    /// </summary>
    /// <remarks>
    /// This is a SECOND entry point the tray window procedure calls bare (the host invokes the provider
    /// without any guard), so it never throws either: a throw out of <see cref="ITrayActions.OverlayIsOn"/>
    /// or the label lookup would kill the app on a plain right-click. On failure it returns an EMPTY
    /// list, which the host reads as "no menu" — the menu simply does not open for that click — and
    /// the reason is logged.
    /// Đây là entry điểm THỨ HAI mà window procedure của khay gọi trần (host gọi provider không một lớp
    /// guard), nên nó cũng không ném lỗi: một exception từ <see cref="ITrayActions.OverlayIsOn"/> hoặc
    /// từ việc tra nhãn sẽ giết app chỉ bằng một cú click chuột phải. Khi lỗi, hàm trả danh sách RỖNG,
    /// host hiểu là "không menu" — menu đơn giản không mở cho cú click đó — và lý do được log.
    /// </remarks>
    /// <returns>
    /// The ordered entries OpenApp / ToggleOverlay (checked per overlay state) / Exit, or an empty list
    /// when the app state could not be read (the host then shows no menu).
    /// Các mục theo thứ tự OpenApp / ToggleOverlay (tick theo trạng thái overlay) / Exit, hoặc danh sách
    /// rỗng khi không đọc được trạng thái app (host khi đó không hiện menu).
    /// </returns>
    public IReadOnlyList<TrayCommand> BuildMenu()
    {
        try
        {
            return TrayMenuModel.Build(
                _L["tray.menu.open"], _L["tray.menu.overlay"], _L["tray.menu.exit"], _actions.OverlayIsOn);
        }
        catch (Exception ex)
        {
            TryLog(() => _log.LogWarning(ex, "Failed to read the app state for the tray menu; skipping the menu for this click."));
            return Array.Empty<TrayCommand>();
        }
    }

    /// <summary>
    /// Runs a tray command and reports its failure twice — to the log (detail) and to the user (one
    /// localized message): every failure initiated from the tray must reach the user's eyes (spec
    /// §Báo lỗi, D12). No exception ever escapes this handler.
    /// Chạy một lệnh khay và báo lỗi của nó ở hai nơi — log (chi tiết) và người dùng (một thông điệp
    /// đã dịch): mọi lỗi khởi phát từ khay phải tới mắt người dùng (spec §Báo lỗi, D12). Không exception
    /// nào được thoát ra khỏi handler này.
    /// </summary>
    /// <remarks>
    /// The command's effect must happen SYNCHRONOUSLY inside this handler, up to the first real
    /// await: the host raises <see cref="ITrayHost.CommandSelected"/> straight from the tray window
    /// procedure, and deferring it (<c>Task.Run</c>) would reorder menu actions against the UI thread
    /// and make the effect invisible to callers. Only the tail of <see cref="ITrayActions.ExitApp"/>
    /// is left asynchronous, and its continuation is observed by <see cref="ObserveExit"/> so a late
    /// fault is reported the same way instead of becoming an unhandled exception on the message pump —
    /// which would kill the process the tray exists to protect.
    /// A failure is reported exactly once: a command that throws synchronously is handled here, a
    /// command that returns a faulted task is handled there — never both. The notice that explains a
    /// SUCCESSFUL toggle is raised after the catch, deliberately not inside it: if delivering that
    /// notice failed, reporting the command as failed would tell the user the opposite of what
    /// happened (the overlay really was turned on).
    /// Tác động của lệnh PHẢI chạy ĐỒNG BỘ ngay trong handler này, tới await thật đầu tiên: host phát
    /// <see cref="ITrayHost.CommandSelected"/> thẳng từ window procedure của khay, và hoãn tác động
    /// (<c>Task.Run</c>) sẽ làm đảo thứ tự các lệnh menu so với UI thread và khiến tác động vô hình
    /// với người gọi. Chỉ phần đuôi của <see cref="ITrayActions.ExitApp"/> là bất đồng bộ, và phần đó
    /// được <see cref="ObserveExit"/> theo dõi để lỗi muộn cũng được báo y như vậy thay vì thành
    /// exception không được xử lý trên message pump — thứ sẽ giết chính process mà tray sinh ra để
    /// bảo vệ.
    /// Mỗi lỗi chỉ được báo ĐÚNG MỘT lần: lệnh ném lỗi đồng bộ thì xử lý ở đây, lệnh trả task lỗi thì
    /// xử lý ở kia — không bao giờ cả hai. Thông báo giải thích cho một lệnh toggle THÀNH CÔNG được đặt
    /// SAU catch, cố ý không nằm trong catch: nếu việc gửi thông báo đó hỏng mà báo lệnh thất bại thì
    /// ta nói với người dùng điều ngược lại với những gì đã xảy ra (overlay thật sự đã được bật).
    /// </remarks>
    /// <param name="id">The command the user picked. Lệnh người dùng chọn.</param>
    private void OnCommandSelected(TrayCommandId id)
    {
        bool announceNextStart = false;
        try
        {
            switch (id)
            {
                case TrayCommandId.OpenApp:
                    _actions.OpenAppWindow();
                    break;
                case TrayCommandId.ToggleOverlay:
                    announceNextStart = ToggleOverlay();
                    break;
                case TrayCommandId.Exit:
                    // ExitApp() is CALLED here (not deferred); its task is observed fire-and-forget.
                    // ExitApp() được GỌI ngay tại đây (không hoãn); task của nó được theo dõi kiểu
                    // fire-and-forget.
                    _ = ObserveExit(_actions.ExitApp());
                    break;
                default:
                    // A TrayCommandId added without a case here would otherwise do nothing silently.
                    // Một TrayCommandId thêm mà không có case ở đây sẽ im lặng không làm gì.
                    TryLog(() => _log.LogWarning("No handler for tray command {Command}.", id));
                    break;
            }
        }
        catch (Exception ex)
        {
            // An exception escaping an event handler of the tray window procedure takes the process
            // down with it, so every synchronous command failure ends here — reported, not swallowed.
            // Lỗi thoát khỏi handler của window procedure khay sẽ kéo theo process chết, nên mọi lỗi
            // lệnh đồng bộ đều dừng ở đây — có báo cáo, không bị nuốt.
            ReportFailure(id, ex);
            return;
        }

        // After the catch on purpose: this notice belongs to a command that SUCCEEDED, and NotifyUser
        // cannot throw, so a delivery problem here can never be mislabeled as a command failure.
        // Đặt SAU catch có chủ đích: thông báo này thuộc về một lệnh đã thành công, và NotifyUser không
        // ném lỗi, nên lỗi gửi thông báo không thể bị gán nhầm thành lỗi lệnh.
        if (announceNextStart) NotifyUser("tray.overlay.appliesAtStart", ToastSeverity.Info);
    }

    /// <summary>
    /// Reports one command failure in the two directions that matter: the user gets one localized
    /// sentence through <see cref="AppNotifier"/> — a tray balloon while the window is hidden or
    /// unfocused, a toast when they are looking at the app (spec §Báo lỗi, D12) — and the exception
    /// detail goes to the log. A command that fails is otherwise invisible: the menu entry just looks
    /// like it did nothing.
    /// Báo một lỗi lệnh theo hai chiều quan trọng: người dùng nhận một câu đã dịch qua
    /// <see cref="AppNotifier"/> — bubble tray khi cửa sổ ẩn hoặc không focus, toast khi họ đang nhìn
    /// vào app (spec §Báo lỗi, D12) — còn chi tiết exception vào log. Nếu không, một lệnh thất bại vô
    /// hình: mục menu chỉ trông như không có tác dụng.
    /// </summary>
    /// <remarks>
    /// The message is deliberately generic (<c>tray.error.command</c>, no exception text): an exception
    /// message is developer detail, sometimes with paths or tokens, and the log already carries it.
    /// Both directions are guarded (<see cref="NotifyUser"/>, <see cref="TryLog"/>) because a report of
    /// a failure must not itself become a crash. The user is told BEFORE logging so a broken log channel
    /// cannot hide a failure from them.
    /// Thông điệp cố ý chung chung (<c>tray.error.command</c>, không kèm text exception): message của
    /// exception là chi tiết dành cho developer, đôi khi chứa đường dẫn hoặc token, và log đã giữ nó. Cả
    /// hai chiều đều có guard (<see cref="NotifyUser"/>, <see cref="TryLog"/>) vì việc báo cáo một lỗi
    /// không được biến thành crash. Báo người dùng TRƯỚC khi log để một kênh log hỏng không che được lỗi.
    /// </remarks>
    /// <param name="id">The command that failed. Lệnh đã thất bại.</param>
    /// <param name="ex">The failure to record. Lỗi cần ghi nhận.</param>
    private void ReportFailure(TrayCommandId id, Exception ex)
    {
        NotifyUser("tray.error.command", ToastSeverity.Error);
        TryLog(() => _log.LogError(ex, "Tray command {Command} failed.", id));
    }

    /// <summary>
    /// Sends one localized message through <see cref="AppNotifier"/>: a tray balloon while the app window
    /// is hidden or unfocused, a toast when the user is looking at the app. It never throws —
    /// <see cref="AppNotifier.Notify"/> is NOT self-isolating (<c>ToastService.Show</c> runs its
    /// subscribers synchronously and a throwing UI subscriber propagates), and every caller of this
    /// helper sits on the tray's native message loop, where an escaping exception kills the app. A
    /// delivery that fails is logged as a warning, never rethrown.
    /// Gửi một thông điệp đã dịch qua <see cref="AppNotifier"/>: bubble tray khi cửa sổ app ẩn hoặc không
    /// focus, toast khi người dùng đang nhìn vào app. Hàm không ném lỗi — <see cref="AppNotifier.Notify"/>
    /// KHÔNG tự cách ly (<c>ToastService.Show</c> gọi các subscriber đồng bộ và một subscriber UI ném lỗi
    /// sẽ lan ra), còn mọi nơi gọi helper này đều nằm trên message loop native của khay, nơi một exception
    /// thoát ra sẽ giết app. Việc gửi hỏng được log warning, không bao giờ ném lại.
    /// </summary>
    /// <param name="messageKey">i18n key of the message to show. Key i18n của thông điệp cần hiện.</param>
    /// <param name="severity">Severity for the toast and the balloon icon. Mức độ cho toast và icon balloon.</param>
    private void NotifyUser(string messageKey, ToastSeverity severity)
    {
        try
        {
            _notifier.Notify(_L[messageKey], severity);
        }
        catch (Exception notifyError)
        {
            TryLog(() => _log.LogWarning(notifyError,
                "Could not deliver the tray message {Key} at {Severity}.", messageKey, severity));
        }
    }

    /// <summary>
    /// Attempts one log write and drops it when the logging channel itself fails. Every caller here sits
    /// on the tray's native message loop (or in a rollback path beside it) and there is no second channel
    /// to fall back on, so swallowing a broken write is deliberate: the alternative is rethrowing out of
    /// a catch and killing the process the tray exists to protect.
    /// Thử ghi một dòng log và bỏ nếu chính kênh log hỏng. Mọi nơi gọi ở đây nằm trên message loop native
    /// của khay (hoặc trong đường hoàn tác cạnh nó) và không có kênh thứ hai để lùi về, nên việc nuốt một
    /// lần ghi hỏng là có chủ đích: phương án còn lại là ném lại từ trong catch và giết chính process mà
    /// tray sinh ra để bảo vệ.
    /// </summary>
    /// <param name="write">The log call to attempt. Lời gọi log cần thử.</param>
    private void TryLog(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Deliberately empty: the logger is the failing component here, and nothing else can report
            // it. Cố ý rỗng: logger mới là component hỏng ở đây, và không gì khác báo lại được nó.
        }
    }

    /// <summary>
    /// Toggles the overlay and decides whether the user is owed a word about what they cannot see for
    /// themselves: turning the overlay on while the pipeline is stopped changes nothing on screen until
    /// the next start. The choice is still applied (spec §7.2) — saying so beats refusing the click — and
    /// the state is read BEFORE <see cref="ITrayActions.SetOverlay"/> so an implementation that starts
    /// something itself cannot talk us out of the notice. The notice is NOT sent here: the caller sends
    /// it after the failure catch, where a delivery failure cannot be mistaken for a command failure.
    /// Bật/tắt overlay và quyết định có nợ người dùng một câu về điều họ không tự thấy được: bật overlay
    /// khi pipeline đang dừng thì màn hình không đổi gì cho tới lần Start kế tiếp. Lựa chọn vẫn được áp
    /// dụng (spec §7.2) — nói rõ vẫn hơn từ chối click — và trạng thái được đọc TRƯỚC
    /// <see cref="ITrayActions.SetOverlay"/> để một cài đặt tự khởi động gì đó không khiến ta bỏ qua thông
    /// báo. Thông báo KHÔNG được gửi ở đây: nơi gọi gửi nó sau catch lỗi, nơi một lỗi gửi thông báo không
    /// thể bị nhầm thành lỗi lệnh.
    /// </summary>
    /// <returns>
    /// True when the overlay was just turned on while the pipeline is stopped — the caller owes the user
    /// the "next start" notice. True khi overlay vừa được bật lúc pipeline đang dừng — nơi gọi nợ người
    /// dùng thông báo "lần Start kế tiếp".
    /// </returns>
    private bool ToggleOverlay()
    {
        bool wasRunning = _actions.PipelineRunning;
        bool turnOn = !_actions.OverlayIsOn;
        _actions.SetOverlay(turnOn);

        return turnOn && !wasRunning;
    }

    /// <summary>
    /// Awaits the tail of an exit that has already begun and reports its failure the same way a
    /// synchronous command failure is reported (log + one user-visible message, spec §Báo lỗi D12).
    /// Nothing can escape it: the await is inside the <c>try</c> and <see cref="ReportFailure"/> cannot
    /// throw — which is what lets the caller discard the returned task without risking an unhandled
    /// exception on the message pump.
    /// Chờ phần đuôi của một lệnh thoát đã bắt đầu và báo lỗi của nó đúng như cách báo một lệnh thất bại
    /// đồng bộ (log + một thông điệp tới người dùng, spec §Báo lỗi D12). Không gì thoát ra được: await
    /// nằm trong <c>try</c> và <see cref="ReportFailure"/> không ném lỗi — nên nơi gọi được phép bỏ qua
    /// task trả về mà không sợ exception không được xử lý trên message pump.
    /// </summary>
    /// <param name="pending">The task returned by <see cref="ITrayActions.ExitApp"/>. Task do <see cref="ITrayActions.ExitApp"/> trả về.</param>
    private async Task ObserveExit(Task pending)
    {
        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            ReportFailure(TrayCommandId.Exit, ex);
        }
    }

    /// <summary>
    /// Stops the tray (same as <see cref="Stop"/>, idempotent) so DI teardown destroys the icon.
    /// Dừng khay (giống <see cref="Stop"/>, idempotent) để DI teardown hủy icon.
    /// </summary>
    public void Dispose() => Stop();
}
