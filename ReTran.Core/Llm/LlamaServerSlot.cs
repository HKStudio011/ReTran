namespace ReTran.Core.Llm;

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

    internal LlamaServerSlot(Func<string, string, ILlamaServerHost>? factory = null) =>
        _factory = factory ?? ((serverExe, modelPath) => new LlamaServerHost(serverExe, modelPath));

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
            return _server;
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
    }
}
