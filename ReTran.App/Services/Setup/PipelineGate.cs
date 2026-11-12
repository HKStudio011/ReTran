using System.Text.Json.Nodes;
using ReTran_App.Core;

namespace ReTran_App.Services.Setup;

/// <summary>
/// Pipeline-start guard for the first-run setup gate (spec §3.1): while the wizard is not complete
/// its full-screen modal covers the whole UI, so starting capture/OCR underneath would burn CPU/GPU
/// for a screen nobody can see and race the wizard's own sidecar restarts. The guard also blocks a
/// completed machine that is RE-RUNNING setup: during the forced re-run's synchronous Checking window
/// the step still reads Done (so completion alone passes), but the re-run will kill+start Core at its
/// VerifyingSidecar step and any pipeline started in that window dies with it. Extracted from
/// Home.razor as a pure function so the decision is unit-testable (SetupWiringTests).
/// Cổng chặn start pipeline khi setup chạy lần đầu chưa xong (spec §3.1): trong khi wizard chưa hoàn
/// tất, modal toàn màn hình của nó che kín UI — start capture/OCR bên dưới vừa tốn CPU/GPU cho màn
/// hình không ai thấy, vừa đua với các lần restart sidecar của chính wizard. Cổng cũng chặn máy ĐÃ XONG
/// nhưng đang "chạy lại setup": trong cửa sổ Checking đồng bộ của lượt chạy lại, bước vẫn đọc là Done
/// (nên chỉ xét completion thì pass), nhưng lượt chạy đó sẽ kill+start Core ở bước VerifyingSidecar và
/// pipeline nào start trong cửa sổ đó sẽ chết theo. Tách ra khỏi Home.razor thành hàm thuần để quyết
/// định test được (SetupWiringTests).
/// </summary>
public static class PipelineGate
{
    /// <summary>
    /// Whether the capture/OCR pipeline may start given the setup completion state and whether a wizard
    /// run is in flight.
    /// Pipeline capture/OCR có được start không khi biết trạng thái hoàn tất của setup và wizard có đang
    /// chạy một lượt không.
    /// </summary>
    /// <param name="setupComplete">True once the setup wizard reached its Done step. Đúng khi wizard setup đã tới bước Done.</param>
    /// <param name="setupRunning">True while any coordinator operation is in flight (e.g. a forced re-run). Đúng khi một thao tác của coordinator đang chạy (vd "chạy lại setup").</param>
    /// <returns><see langword="true"/> only when setup is complete AND no run is in flight; <see langword="false"/> otherwise. <see langword="true"/> chỉ khi setup đã xong VÀ không có lượt chạy nào đang diễn ra; <see langword="false"/> trong mọi trường hợp khác.</returns>
    internal static bool CanStartPipeline(bool setupComplete, bool setupRunning) => setupComplete && !setupRunning;
}

/// <summary>
/// <see cref="ISetupCoreBridge"/> adapter over the app's existing core-process client: JSON-RPC calls
/// are forwarded verbatim, and a restart is the existing kill+start path (dispose releases the stdio
/// connection and kills the process tree, then the core is started again). The wizard is the only
/// caller that may restart Core mid-session (spec §5.11) — the pipeline is gated off while setup is
/// incomplete, so no other component talks to a half-restarted peer. Task 14 lives in this file
/// because the task's file list provides no separate home for the adapter.
/// Adapter <see cref="ISetupCoreBridge"/> trên core-process client có sẵn của app: các lời gọi
/// JSON-RPC được forward nguyên vẹn, và restart chính là đường kill+start có sẵn (dispose giải phóng
/// kết nối stdio và kill cây tiến trình, rồi start core lại). Wizard là caller duy nhất được phép
/// restart Core giữa phiên (spec §5.11) — pipeline bị chặn trong khi setup chưa xong, nên không
/// component nào khác nói chuyện với peer đang restart dở. Task 14 đặt class này trong file vì danh
/// sách file của task không có chỗ riêng cho adapter.
/// </summary>
public sealed class SetupCoreBridge : ISetupCoreBridge
{
    private readonly ICorePeer _core;

    /// <summary>
    /// Creates the bridge over the shared core peer (the production <c>CoreProcessClient</c>).
    /// Tạo bridge trên core peer dùng chung (<c>CoreProcessClient</c> production).
    /// </summary>
    /// <param name="core">The app's core-process connection. Kết nối tiến trình core của app.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is null. <paramref name="core"/> là null.</exception>
    public SetupCoreBridge(ICorePeer core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
    }

    /// <inheritdoc />
    public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct) =>
        _core.CallAsync(method, @params, ct);

    /// <summary>
    /// Restarts the whole core process: dispose first (kill + release stdio), then start again. The
    /// production peer implements <see cref="IAsyncDisposable"/>; a peer without it (a minimal test
    /// double) is simply started fresh. Order matters — starting before disposing would leave two
    /// core processes alive at once.
    /// Khởi động lại toàn bộ tiến trình core: dispose trước (kill + giải phóng stdio), rồi start
    /// lại. Peer production implement <see cref="IAsyncDisposable"/>; peer không có nó (test double
    /// tối giản) chỉ việc start mới. Thứ tự quan trọng — start trước dispose để lại hai tiến trình
    /// core sống cùng lúc.
    /// </summary>
    /// <param name="ct">Cancellation token. Token hủy.</param>
    public async Task RestartAsync(CancellationToken ct)
    {
        if (_core is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await _core.StartCoreAsync(ct);
    }
}
