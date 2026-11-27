using System.Runtime.ExceptionServices;
using ReTran_App.Core;

namespace ReTran_App.Services.Update;

/// <summary>
/// Stops the pipeline, core process, and overlay feed in order during install or app teardown.
/// Every step has its own timeout covering both synchronous invocation and asynchronous completion;
/// cleanup remains best-effort, but every failure is propagated after all three steps have been
/// attempted so an update cannot continue with a hidden stop error.
/// Dừng pipeline, tiến trình core và overlay feed theo thứ tự khi cài đặt hoặc thoát app.
/// Mỗi bước có timeout riêng cho cả lời gọi đồng bộ lẫn phần hoàn tất bất đồng bộ; cleanup vẫn
/// best-effort, nhưng mọi lỗi đều được ném sau khi đã thử
/// cả ba bước để update không thể tiếp tục khi có lỗi dừng service bị che giấu.
/// </summary>
public sealed class UpdateStopper : IUpdateStopper
{
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(10);

    private readonly PipelineController _pipeline;
    private readonly CoreProcessClient _core;
    private readonly IAsyncDisposable _overlayFeed;
    private readonly TimeSpan _stepTimeout;

    /// <summary>
    /// Creates a stopper over the live app services.
    /// Tạo stopper trên các service đang sống của app.
    /// </summary>
    /// <param name="pipeline">Pipeline whose session is stopped first. Phiên pipeline được dừng trước.</param>
    /// <param name="core">Core process client disposed after the pipeline step. Client tiến trình core được dispose sau bước pipeline.</param>
    /// <param name="overlayFeed">Overlay feed disposed last to release its listener. Overlay feed được dispose cuối để giải phóng listener.</param>
    /// <param name="stepTimeout">Optional per-step timeout; defaults to 10 seconds. Timeout tùy chọn cho từng bước; mặc định 10 giây.</param>
    /// <exception cref="ArgumentNullException">Thrown when any service reference is null. Ném khi một tham chiếu service là null.</exception>
    public UpdateStopper(
        PipelineController pipeline,
        CoreProcessClient core,
        IAsyncDisposable overlayFeed,
        TimeSpan? stepTimeout = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _overlayFeed = overlayFeed ?? throw new ArgumentNullException(nameof(overlayFeed));
        _stepTimeout = stepTimeout ?? DefaultStepTimeout;
    }

    /// <inheritdoc />
    public async Task StopServicesAsync(CancellationToken cancellationToken)
    {
        List<Exception> failures = [];

        if (_pipeline.IsRunning)
        {
            try
            {
                await RunStepAsync(
                    ct => _pipeline.StopAsync(ct),
                    "pipeline",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        try
        {
            await RunStepAsync(
                _ => _core.DisposeAsync().AsTask(),
                "core process",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await RunStepAsync(
                _ => _overlayFeed.DisposeAsync().AsTask(),
                "overlay feed",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException("One or more service stop steps failed.", failures);
        }
    }

    private async Task RunStepAsync(
        Func<CancellationToken, Task> step,
        string name,
        CancellationToken cancellationToken)
    {
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stepCts.CancelAfter(_stepTimeout);
        try
        {
            // Invoke on the thread pool so a synchronous block before Task/ValueTask creation is bounded too.
            // WaitAsync also bounds asynchronous disposals that cannot observe a cancellation token themselves.
            var stepTask = Task.Run(() => step(stepCts.Token), CancellationToken.None);
            await stepTask.WaitAsync(stepCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (
            stepCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Stopping the {name} timed out after {_stepTimeout}.", ex);
        }
    }
}
