using Microsoft.Extensions.Logging;
using ReTran_App.Services;

namespace ReTran_App.Core;

/// <summary>
/// Whether the OBS overlay feed is allowed to show anything right now, plus a change signal.
/// Có feed overlay OBS được phép hiển thị gì lúc này hay không, kèm tín hiệu thay đổi.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShouldShowObs"/> answers the current value at the moment of the read — it is never a
/// stale snapshot — because subscribers must act on the value, not on the number of notifications:
/// one pipeline stop arrives as two <see cref="PipelineController"/> events (<see
/// cref="PipelineController.PipelineStateChanged"/> and <see cref="PipelineController.StateChanged"/>)
/// and a settings save can change nothing at all. <see cref="Changed"/> is therefore edge-triggered:
/// it fires only when the value actually flips.
/// </para>
/// <para>
/// <see cref="ShouldShowObs"/> trả lời giá trị hiện tại ngay tại lúc đọc — không bao giờ là snapshot
/// cũ — vì subscriber phải xử lý theo giá trị, không theo số lần thông báo: một lần dừng pipeline đến
/// thành hai event của <see cref="PipelineController"/> (<see
/// cref="PipelineController.PipelineStateChanged"/> và <see cref="PipelineController.StateChanged"/>)
/// và một lần lưu cài đặt có thể không đổi gì cả. Nên <see cref="Changed"/> là edge-triggered: nó chỉ
/// nổ khi giá trị thực sự lật.
/// </para>
/// </remarks>
public interface IOverlayVisibility
{
    /// <summary>
    /// Current visibility — a read must never see a value older than the pipeline state it is derived
    /// from. Giá trị hiển thị hiện tại — một lần đọc không bao giờ được thấy giá trị cũ hơn trạng thái
    /// pipeline suy ra từ nó.
    /// </summary>
    bool ShouldShowObs { get; }

    /// <summary>
    /// Raised when <see cref="ShouldShowObs"/> changes value (edge-triggered), on whichever thread
    /// caused the change. Nổ khi <see cref="ShouldShowObs"/> đổi giá trị (edge-triggered), trên thread
    /// nào gây ra thay đổi.
    /// </summary>
    event Action? Changed;
}

/// <summary>
/// The single switch behind both overlay surfaces: the OBS feed shows text only while the
/// <c>OverlayOnGame</c> setting is on <b>and</b> the pipeline is running (spec K8 — no separate OBS
/// toggle, no inference from focus or foreground).
/// Công tắc duy nhất đứng sau cả hai bề mặt overlay: feed OBS chỉ hiện chữ khi cài đặt
/// <c>OverlayOnGame</c> BẬT <b>và</b> pipeline đang chạy (spec K8 — không có công tắc OBS riêng, không
/// suy ra từ focus hay foreground).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShouldShowObs"/> is computed on every read: the pipeline half is
/// <see cref="PipelineController.IsRunning"/> (a field read), and only the settings half is cached —
/// <c>OverlayOnGame</c> is read from <c>settings.json</c> at construction and again on every
/// <see cref="AppSettingsService.Changed"/>, so the gate that <c>OverlayFeedServer</c> runs for each
/// translated frame costs no file I/O. Being live rather than a stored value is what makes that gate
/// correct: a stop flips <see cref="PipelineController.IsRunning"/> <em>before</em>
/// <c>PipelineController</c> clears the overlay frame and raises its events
/// (<c>PipelineController:297-300</c>, <c>:634-638</c>, <c>:839-842</c>), so the feed sees the frame
/// event as already-hidden and the visibility edge below supplies the single clear payload.
/// </para>
/// <para>
/// <see cref="ShouldShowObs"/> được tính mỗi lần đọc: nửa pipeline là
/// <see cref="PipelineController.IsRunning"/> (đọc field), chỉ nửa cài đặt được cache —
/// <c>OverlayOnGame</c> đọc từ <c>settings.json</c> khi dựng và đọc lại mỗi khi
/// <see cref="AppSettingsService.Changed"/> nổ, nên gate mà <c>OverlayFeedServer</c> chạy cho từng khung
/// hình đã dịch không tốn file I/O. Tính trực tiếp thay vì lưu giá trị chính là điều làm gate đúng: một
/// lần dừng lật <see cref="PipelineController.IsRunning"/> <em>trước khi</em>
/// <c>PipelineController</c> xóa khung overlay và nổ event của nó (<c>PipelineController:297-300</c>,
/// <c>:634-638</c>, <c>:839-842</c>), nên feed thấy event khung hình đó khi đã ẩn và edge hiển thị bên
/// dưới gửi đúng payload xóa duy nhất.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised on <see cref="AppSettingsService.Changed"/>, <see
/// cref="PipelineController.StateChanged"/> and <see cref="PipelineController.PipelineStateChanged"/>
/// only when the computed value differs from the last announced one — no timer, no polling — so one
/// user action that produces several source events (an unexpected Core stop raises two pipeline events;
/// Stop-then-core-exit raises more) still notifies exactly once.
/// </para>
/// <para>
/// <see cref="Changed"/> được nổ theo <see cref="AppSettingsService.Changed"/>, <see
/// cref="PipelineController.StateChanged"/> và <see cref="PipelineController.PipelineStateChanged"/> chỉ
/// khi giá trị tính ra khác giá trị đã báo gần nhất — không timer, không poll — nên một thao tác người
/// dùng sinh ra nhiều event nguồn (một lần Core dừng đột xuất nổ hai event pipeline; Stop rồi core exit
/// nổ còn nhiều hơn) vẫn chỉ thông báo đúng một lần.
/// </para>
/// </remarks>
public sealed class OverlayVisibility : IOverlayVisibility, IDisposable
{
    private readonly AppSettingsService _settings;
    private readonly PipelineController _pipeline;
    private readonly ILogger<OverlayVisibility> _log;

    // OverlayOnGame, read at construction and on every save — never per frame, because the feed asks
    // this gate for each translated frame it might forward (performance budget in AGENTS.md).
    // OverlayOnGame, đọc khi dựng và mỗi lần lưu — không bao giờ mỗi khung hình, vì feed hỏi gate này
    // cho từng khung hình đã dịch mà nó có thể chuyển tiếp (ngân sách hiệu năng trong AGENTS.md).
    private int _switchOn;

    // The value last announced through Changed; the edge test compares against this, not against a
    // stored copy of the live value.
    // Giá trị gần nhất báo qua Changed; phép thử edge so với giá trị này, không so với bản lưu của giá
    // trị trực tiếp.
    private int _announced;
    private int _disposed;

    /// <summary>
    /// Creates the gate over the given settings store and pipeline, reading the overlay switch
    /// immediately so a feed built later already sees the truth without waiting for an event.
    /// Tạo gate trên kho cài đặt và pipeline đã cho, đọc ngay công tắc overlay để feed dựng sau đã thấy
    /// giá trị đúng mà không phải chờ event.
    /// </summary>
    /// <param name="settings">Settings store; only its <c>Changed</c> event costs a file read. Kho cài đặt; chỉ event <c>Changed</c> của nó tốn một lần đọc file.</param>
    /// <param name="pipeline">Pipeline whose <see cref="PipelineController.IsRunning"/> feeds the gate. Pipeline có <see cref="PipelineController.IsRunning"/> nuôi gate.</param>
    /// <param name="log">Logger for each visibility edge. Logger cho mỗi edge hiển thị.</param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null. Ném khi có tham số null.</exception>
    public OverlayVisibility(AppSettingsService settings, PipelineController pipeline, ILogger<OverlayVisibility> log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Volatile.Write(ref _switchOn, _settings.Load().OverlayOnGame ? 1 : 0);
        Volatile.Write(ref _announced, ShouldShowObs ? 1 : 0);
        _settings.Changed += OnSettingsChanged;
        _pipeline.StateChanged += Reevaluate;
        _pipeline.PipelineStateChanged += OnPipelineStateChanged;
    }

    /// <inheritdoc />
    public bool ShouldShowObs => Volatile.Read(ref _switchOn) == 1 && _pipeline.IsRunning;

    /// <inheritdoc />
    public event Action? Changed;

    private void OnSettingsChanged()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        Volatile.Write(ref _switchOn, _settings.Load().OverlayOnGame ? 1 : 0);
        Reevaluate();
    }

    private void OnPipelineStateChanged(bool running, string? reason) => Reevaluate();

    private void Reevaluate()
    {
        if (Volatile.Read(ref _disposed) == 1) return;

        bool shown = ShouldShowObs;
        int flag = shown ? 1 : 0;
        if (Interlocked.Exchange(ref _announced, flag) == flag) return; // no edge ⇒ no event / không edge ⇒ không event

        // One line per edge: when OBS goes blank, this is the only trace that the switch shut the feed.
        // Một dòng mỗi edge: khi OBS trắng, đây là dấu vết duy nhất cho thấy công tắc đã tắt feed.
        _log.LogInformation("OBS overlay visibility changed to {Shown}.", shown);
        Changed?.Invoke();
    }

    /// <summary>
    /// Stops tracking: unsubscribes from every source, so <see cref="Changed"/> never fires again and
    /// the cached switch stops following saves. <see cref="ShouldShowObs"/> still answers from the
    /// pipeline's current state. Safe to call more than once.
    /// Ngừng theo dõi: gỡ đăng ký khỏi mọi nguồn, nên <see cref="Changed"/> không bao giờ nổ lại và cache
    /// công tắc ngừng bám theo các lần Save. <see cref="ShouldShowObs"/> vẫn trả lời theo trạng thái hiện
    /// tại của pipeline. Gọi nhiều lần vẫn an toàn.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _settings.Changed -= OnSettingsChanged;
        _pipeline.StateChanged -= Reevaluate;
        _pipeline.PipelineStateChanged -= OnPipelineStateChanged;
    }
}
