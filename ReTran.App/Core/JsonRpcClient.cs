using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReTran_App.Core;

/// <summary>
/// Newline-delimited bidirectional JSON-RPC 2.0 client over an injectable reader/writer pair:
/// sends requests via <see cref="CallAsync"/>, answers requests arriving from the peer, and
/// raises notifications as events.
/// Client JSON-RPC 2.0 phân tách bằng xuống dòng, hai chiều trên cặp reader/writer có thể tiêm:
/// gửi yêu cầu qua <see cref="CallAsync"/>, trả lời yêu cầu từ đối tác và phát thông báo dạng event.
/// </summary>
public sealed class JsonRpcClient : IAsyncDisposable
{
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly object _pendingGate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextId = 0;
    private volatile Func<string, JsonNode?, Task<JsonNode?>>? _requestHandler;

    public JsonRpcClient(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
        _ = ReadLoopAsync();
    }

    /// <summary>
    /// Raised for each notification (a message without an id) received from the peer; handlers
    /// run sequentially inside the read loop, each one isolated from the others.
    /// Ném lên mỗi thông báo (tin không có id) nhận từ đối tác; các handler chạy tuần tự trong
    /// vòng lặp đọc, mỗi handler cách ly với các handler còn lại.
    /// </summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>
    /// Raised when a request or notification handler throws; the read loop keeps reading afterwards.
    /// Ném lên khi handler của yêu cầu hoặc thông báo ném exception; vòng lặp đọc vẫn đọc tiếp sau đó.
    /// </summary>
    public event Action<Exception>? HandlerError;

    /// <summary>
    /// Registers the single handler that answers requests sent by the peer (Core → App).
    /// Đăng ký handler duy nhất trả lời yêu cầu do đối tác (Core → App) gửi tới.
    /// </summary>
    /// <param name="handler">
    /// Receives the method name and params and returns the result node; a thrown exception is
    /// answered as a -32603 error. When no handler is registered, every request is answered
    /// with -32601 Method not found.
    /// Nhận tên phương thức và params rồi trả về node kết quả; exception được ném ra sẽ được trả
    /// lời bằng lỗi -32603. Khi chưa đăng ký handler, mọi yêu cầu được trả lời -32601 Method not found.
    /// </param>
    public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler)
        => _requestHandler = handler;

    /// <summary>
    /// Sends a request and awaits its response (matched by id).
    /// Gửi yêu cầu và chờ phản hồi tương ứng (khớp theo id).
    /// </summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        string id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate) _pending[id] = tcs;
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
        try
        {
            await WriteLineAsync(line, linked.Token);
            return await tcs.Task.WaitAsync(ct);
        }
        catch
        {
            lock (_pendingGate) _pending.Remove(id);
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _input.ReadLineAsync(_cts.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? node;
                try
                {
                    node = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (node is null) continue;

                if (node["method"] is { } methodNode && methodNode.GetValueKind() == JsonValueKind.String)
                {
                    string method = methodNode.GetValue<string>()!;
                    if (ExtractId(node["id"]) is { } id)
                        await DispatchRequestAsync(id, method, node["params"]);
                    else
                        DispatchNotification(method, node["params"]);
                    continue;
                }

                if (ExtractId(node["id"]) is { } responseId && TryTakePending(responseId, out var tcs))
                {
                    if (node["error"] is { } error)
                    {
                        int code = error["code"]?.GetValue<int>() ?? 0;
                        string message = error["message"]?.GetValue<string>() ?? "Unknown RPC error";
                        tcs.TrySetException(new RpcException(code, message));
                    }
                    else
                    {
                        tcs.TrySetResult(node["result"]);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception) { /* stream closed/disposed — peer went away */ }
        finally
        {
            // Every exit path (EOF, cancel on dispose, error) must complete outstanding calls instead of hanging.
            FailAllPending();
        }
    }

    private async Task DispatchRequestAsync(string id, string method, JsonNode? @params)
    {
        Func<string, JsonNode?, Task<JsonNode?>>? handler = _requestHandler;
        if (handler is null)
        {
            await WriteLineAsync(ErrorLine(id, MethodNotFound, $"Method not found: {method}"), _cts.Token);
            return;
        }

        try
        {
            JsonNode? result = await handler(method, @params);
            string reply = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result ?? JsonValue.Create<object?>(null),
            }.ToJsonString();
            await WriteLineAsync(reply, _cts.Token);
        }
        catch (Exception ex)
        {
            RaiseHandlerError(ex);
            await WriteLineAsync(ErrorLine(id, InternalError, ex.Message), _cts.Token);
        }
    }

    private void DispatchNotification(string method, JsonNode? @params)
    {
        Action<string, JsonNode?>? handlers = NotificationReceived;
        if (handlers is null) return;
        foreach (Action<string, JsonNode?> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(method, @params);
            }
            catch (Exception ex)
            {
                RaiseHandlerError(ex);
            }
        }
    }

    private void RaiseHandlerError(Exception ex)
    {
        try
        {
            HandlerError?.Invoke(ex);
        }
        catch { /* faulty subscriber must not kill the read loop */ }
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync((line + "\n").AsMemory(), ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private bool TryTakePending(string id, out TaskCompletionSource<JsonNode?> tcs)
    {
        lock (_pendingGate)
        {
            if (_pending.TryGetValue(id, out var found))
            {
                _pending.Remove(id);
                tcs = found;
                return true;
            }
        }
        tcs = null!;
        return false;
    }

    private void FailAllPending()
    {
        lock (_pendingGate)
        {
            foreach (TaskCompletionSource<JsonNode?> tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }

    private static string? ExtractId(JsonNode? idNode)
    {
        if (idNode is null) return null;
        return idNode.GetValueKind() switch
        {
            JsonValueKind.String => idNode.GetValue<string>(),
            JsonValueKind.Number => idNode.ToJsonString(),
            _ => null,
        };
    }

    private static string ErrorLine(string id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    }.ToJsonString();

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
