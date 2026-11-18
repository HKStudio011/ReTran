namespace ReTran.Core.Llm;

/// <summary>Snapshot exposed through <c>llm.server.status</c>. Snapshot phơi qua <c>llm.server.status</c>.</summary>
internal sealed record LlamaServerStatus(string State, string? Model, string? Error);

/// <summary>
/// Owns the current local-model server and provides one idempotent cleanup path for core.stop,
/// provider switches, model switches and process shutdown. Sở hữu server của model local hiện tại
/// và gom một đường cleanup idempotent cho core.stop, đổi provider/model và thoát tiến trình.
/// </summary>
internal sealed class LlamaServerSlot
{
    private readonly Func<string, string, ILlamaServerHost> _factory;
    private readonly object _gate = new();
    private ILlamaServerHost? _server;
    private string? _modelName;
    private string _state = "stopped";
    private string? _error;

    internal LlamaServerSlot(Func<string, string, ILlamaServerHost>? factory = null) =>
        _factory = factory ?? ((serverExe, modelPath) => new LlamaServerHost(serverExe, modelPath));

    /// <summary>Current loading/ready/error/stopped state. Trạng thái loading/ready/error/stopped hiện tại.</summary>
    internal LlamaServerStatus Status
    {
        get { lock (_gate) return new LlamaServerStatus(_state, _modelName, _error); }
    }

    /// <summary>
    /// Returns the current model's host, disposing a different model's host first.
    /// Trả host của model hiện tại, dispose host của model khác trước khi tạo mới.
    /// </summary>
    internal ILlamaServerHost GetOrCreate(string modelName, string serverExe, string modelPath)
    {
        lock (_gate)
        {
            if (_server is not null && _modelName == modelName) return _server;
            StopLocked();
            _server = _factory(serverExe, modelPath);
            _modelName = modelName;
            _state = "loading";
            _error = null;
            return _server;
        }
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
            {
                throw new InvalidOperationException("llama-server is no longer active for this session.");
            }
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
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _server?.Dispose();
        _server = null;
        _modelName = null;
        _state = "stopped";
        _error = null;
    }
}
