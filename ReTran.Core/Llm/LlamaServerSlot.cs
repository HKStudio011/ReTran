namespace ReTran.Core.Llm;

/// <summary>
/// Snapshot exposed through <c>llm.server.status</c>, including attempt and nullable load metrics.
/// Snapshot phơi qua <c>llm.server.status</c>, gồm attempt và các số liệu nạp có thể null.
/// </summary>
internal sealed record LlamaServerStatus(
    string State, string? Model, string? Error, long Attempt, string Phase,
    long? BytesLoaded, long? BytesTotal, double? BytesPerSec, bool ProgressAvailable);

/// <summary>
/// Owns the current local-model server, its health task and monotonic attempt identity, with one
/// idempotent cleanup path for core.stop, provider/model switches and process shutdown.
/// Sở hữu server model local hiện tại, health task và định danh attempt đơn điệu, với một đường
/// cleanup idempotent cho core.stop, đổi provider/model và thoát tiến trình.
/// </summary>
internal sealed class LlamaServerSlot
{
    private readonly Func<string, string, ILlamaServerHost> _factory;
    private readonly object _gate = new();
    private ILlamaServerHost? _server;
    private ILlamaServerHost? _healthHost;
    private string? _modelName;
    private string _state = "stopped";
    private string? _error;
    private long _attempt;

    internal LlamaServerSlot(Func<string, string, ILlamaServerHost>? factory = null) =>
        _factory = factory ?? ((serverExe, modelPath) => new LlamaServerHost(serverExe, modelPath));

    /// <summary>Current loading/ready/error/stopped state and progress. Trạng thái loading/ready/error/stopped và tiến trình hiện tại.</summary>
    internal LlamaServerStatus Status
    {
        get
        {
            lock (_gate)
            {
                LlmLoadProgressSnapshot? progress = _server?.LoadProgress;
                string phase = _state switch
                {
                    "stopped" => "stopped",
                    "error" => "error",
                    "ready" => "ready",
                    _ when progress?.Phase is "loading" or "finalizing" => progress.Phase,
                    _ => "loading",
                };
                return new LlamaServerStatus(
                    _state, _modelName, _error, _attempt, phase,
                    progress?.BytesLoaded, progress?.BytesTotal,
                    progress?.BytesPerSec, progress?.ProgressAvailable ?? false);
            }
        }
    }

    /// <summary>
    /// Returns the current model's host, disposing a different model's host first and assigning a new attempt.
    /// Trả host của model hiện tại, dispose host của model khác trước và gán attempt mới khi tạo host.
    /// </summary>
    internal ILlamaServerHost GetOrCreate(string modelName, string serverExe, string modelPath)
    {
        lock (_gate)
        {
            if (_server is not null && _modelName == modelName) return _server;
            StopLocked();
            return CreateLocked(modelName, serverExe, modelPath);
        }
    }

    /// <summary>
    /// Starts health loading in the background and returns the current attempt immediately.
    /// Bắt đầu health/load ở nền và trả attempt hiện tại ngay lập tức.
    /// </summary>
    internal long Start(string modelName, string serverExe, string modelPath)
    {
        ILlamaServerHost host;
        bool runHealth = false;
        long attempt;
        lock (_gate)
        {
            if (_server is not null && _modelName == modelName && _state is "loading" or "ready")
            {
                host = _server;
                if (_state == "loading" && !ReferenceEquals(_healthHost, host))
                {
                    _healthHost = host;
                    runHealth = true;
                }
            }
            else
            {
                StopLocked();
                host = CreateLocked(modelName, serverExe, modelPath);
                _healthHost = host;
                runHealth = true;
            }
            attempt = _attempt;
        }

        if (runHealth) _ = RunHealthObservedAsync(host);
        return attempt;
    }

    /// <summary>
    /// Tracks the lazy health wait so the UI can distinguish loading, ready and startup errors.
    /// Theo dõi health wait lười để UI phân biệt loading, ready và lỗi khởi động.
    /// </summary>
    internal async Task EnsureStartedAsync(ILlamaServerHost host, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_server, host))
                throw new InvalidOperationException("llama-server is no longer active for this session.");
            _state = "loading";
            _error = null;
        }
        try
        {
            await host.EnsureStartedAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_server, host)) _state = "ready";
            }
        }
        catch (OperationCanceledException)
        {
            bool dispose = false;
            lock (_gate)
            {
                if (ReferenceEquals(_server, host))
                {
                    ClearLocked();
                    dispose = true;
                }
            }
            if (dispose) host.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_server, host))
                {
                    _state = "error";
                    _error = ex.Message;
                }
            }
            throw;
        }
    }

    /// <summary>Disposes and forgets the current host; safe to repeat. Dispose và quên host hiện tại; gọi lặp vẫn an toàn.</summary>
    internal void Stop()
    {
        lock (_gate) StopLocked();
    }

    private ILlamaServerHost CreateLocked(string modelName, string serverExe, string modelPath)
    {
        _server = _factory(serverExe, modelPath);
        _modelName = modelName;
        _state = "loading";
        _error = null;
        _attempt++;
        return _server;
    }

    private async Task RunHealthObservedAsync(ILlamaServerHost host)
    {
        try
        {
            await EnsureStartedAsync(host, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop/model switch owns this cancellation; it is not a new startup error.
        }
        catch (Exception)
        {
            // EnsureStartedAsync has already published the current attempt's error.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_healthHost, host)) _healthHost = null;
            }
        }
    }

    private void StopLocked()
    {
        _healthHost = null;
        _server?.Dispose();
        ClearLocked();
    }

    private void ClearLocked()
    {
        _server = null;
        _healthHost = null;
        _modelName = null;
        _state = "stopped";
        _error = null;
    }
}
