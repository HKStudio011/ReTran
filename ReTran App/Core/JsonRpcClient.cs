using System.Text;
using System.Text.Json.Nodes;

namespace ReTran_App.Core;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 client over an injectable reader/writer pair.
/// Client JSON-RPC 2.0 phân tách bằng xuống dòng trên cặp reader/writer có thể tiêm.
/// </summary>
public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId = 0;

    public JsonRpcClient(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
        _ = ReadLoopAsync();
    }

    /// <summary>
    /// Sends a request and awaits its response (matched by id).
    /// Gửi yêu cầu và chờ phản hồi tương ứng (khớp theo id).
    /// </summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        string id = (++_nextId).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        // JsonObject serialization keeps key order and escapes method/params correctly.
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params ?? new JsonObject(),
        };
        string line = request.ToJsonString();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _output.WriteAsync((line + "\n").AsMemory(), linked.Token);
        await _output.FlushAsync(linked.Token);
        return await tcs.Task.WaitAsync(ct);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _input.ReadLineAsync(_cts.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (JsonNode.Parse(line) is { } n && n["id"]?.GetValue<string>() is { } id
                    && _pending.TryGetValue(id, out var tcs))
                {
                    _pending.Remove(id);
                    if (n["error"] is { } error)
                    {
                        int code = error["code"]?.GetValue<int>() ?? 0;
                        string message = error["message"]?.GetValue<string>() ?? "Unknown RPC error";
                        tcs.TrySetException(new RpcException(code, message));
                    }
                    else
                    {
                        tcs.TrySetResult(n["result"]);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception)
        {
            // Stream closed/disposed — peer went away; fail any outstanding calls instead of hanging.
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _output.FlushAsync(); } catch { /* best effort */ }
    }
}

/// <summary>
/// JSON-RPC error response received from the peer; carries the standard error code and message.
/// Phản hồi lỗi JSON-RPC nhận từ đối tác; mang mã lỗi và thông báo chuẩn.
/// </summary>
public sealed class RpcException : Exception
{
    /// <summary>
    /// The JSON-RPC error code (e.g. -32601 Method not found, -32603 Internal error).
    /// Mã lỗi JSON-RPC (ví dụ -32601 Method not found, -32603 Internal error).
    /// </summary>
    public int Code { get; }

    public RpcException(int code, string message) : base(message)
    {
        Code = code;
    }
}
