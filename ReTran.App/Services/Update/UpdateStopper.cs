using Microsoft.Extensions.Logging;
using ReTran_App.Core;

namespace ReTran_App.Services.Update;

/// <summary>
/// Stops the app services that would hold file locks during install: pipeline session first,
/// then the core process (kill tree). Each step is best-effort — a failing step is logged as a
/// warning and never blocks the next one, and the method itself never throws.
/// Dừng các service của app đang giữ khóa file trong lúc cài: phiên pipeline trước, rồi tiến trình
/// core (kill tree). Mỗi bước best-effort — bước fail được ghi Warning và không bao giờ chặn bước
/// sau, chính method không ném.
/// </summary>
public sealed class UpdateStopper : IUpdateStopper
{
    private readonly PipelineController _pipeline;
    private readonly CoreProcessClient _core;
    private readonly ILogger<UpdateStopper> _log;

    /// <summary>
    /// Creates a stopper over the live app services.
    /// Tạo stopper trên các service đang sống của app.
    /// </summary>
    /// <param name="pipeline">Pipeline whose session is stopped first. Phiên pipeline được dừng trước.</param>
    /// <param name="core">Core process client disposed (kill tree) after the pipeline step. Client tiến trình core được dispose (kill tree) sau bước pipeline.</param>
    /// <param name="log">Warns when a step fails. Ghi Warn khi một bước fail.</param>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    public UpdateStopper(
        PipelineController pipeline,
        CoreProcessClient core,
        ILogger<UpdateStopper> log)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public async Task StopServicesAsync(CancellationToken cancellationToken)
    {
        // (1) Chỉ dừng pipeline khi nó đang chạy — core.stop trên pipeline đã tắt là không cần thiết.
        if (_pipeline.IsRunning)
        {
            try
            {
                await _pipeline.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Stopping the pipeline failed; continuing with core disposal.");
            }
        }

        // (2) Kill tree tiến trình core — bước này không nhận token để việc dispose không bị cắt giữa chừng.
        try
        {
            await _core.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Disposing the core process failed.");
        }
    }
}
