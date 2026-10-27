using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReTran.Core.JsonRpc;

/// <summary>
/// Minimal bidirectional JSON-RPC 2.0 server: reads lines, dispatches to registered handlers,
/// writes responses, and can also send notifications (<see cref="NotifyAsync"/>) and requests
/// (<see cref="CallAsync"/>) to the peer.
/// Máy chủ JSON-RPC 2.0 tối giản hai chiều: đọc dòng, điều phối tới handler đã đăng ký,
/// ghi phản hồi, đồng thời có thể gửi thông báo (<see cref="NotifyAsync"/>) và yêu cầu
/// (<see cref="CallAsync"/>) về phía peer.
/// </summary>
public sealed class JsonRpcServer : IJsonRpcNotifier
{
    private readonly Dictionary<string, Func<JsonNode?, Task<JsonNode?>>> _handlers = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly object _pendingGate = new();
    private int _nextId;
    private volatile IJsonRpcTransport? _transport;

    /// <summary>Registers a handler for a method name (e.g. "core.ping").</summary>
    public void Register(string method, Func<JsonNode?, Task<JsonNode?>> handler) => _handlers[method] = handler;

    /// <summary>Runs the read/dispatch/write loop until the transport is closed.</summary>
    public async Task RunAsync(IJsonRpcTransport transport, CancellationToken ct)
    {
        _transport = transport;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await transport.ReadLineAsync(ct);
                if (line is null) break; // EOF

                JsonNode? node = JsonRpc.ParseLine(line);
                JsonNode? version = node?["jsonrpc"];
                if (node is null || version is null
                    || version.GetValueKind() != JsonValueKind.String || version.GetValue<string>() != "2.0")
                {
                    await WriteAsync(transport, ParseErrorLine(), ct);
                    continue;
                }

                JsonNode? methodNode = node["method"];
                if (methodNode is not null && methodNode.GetValueKind() == JsonValueKind.String)
                {
                    await DispatchRequestAsync(transport, node, methodNode.GetValue<string>()!, ct);
                    continue;
                }

                JsonNode? idNode = node["id"];
                string? responseId = idNode is null ? null : ExtractId(idNode);
                if (responseId is null)
                {
                    await WriteAsync(transport, ParseErrorLine(), ct);
                    continue;
                }

                // Response: route to the waiting CallAsync, or ignore when the id is unknown.
                if (TryTakePending(responseId, out var waiter))
                    RouteResponse(waiter, node);
            }
        }
        finally
        {
            FailAllPending(ct);
        }
    }

    /// <summary>
    /// Sends a JSON-RPC request and awaits the response carrying the same id.
    /// Gửi yêu cầu JSON-RPC và chờ phản hồi mang cùng id.
    /// </summary>
    /// <param name="method">Method name to invoke. Tên phương thức cần gọi.</param>
    /// <param name="@params">Optional request parameters; serialized as an empty object when null.
    /// Tham số tùy chọn; khi null sẽ được đóng gói thành object rỗng.</param>
    /// <param name="ct">Token used to cancel the wait for the response. Token dùng để hủy lời chờ phản hồi.</param>
    /// <returns>The <c>result</c> of the matching response as a detached clone (no parent); a null result yields null.
    /// Kết quả <c>result</c> của phản hồi tương ứng dưới dạng bản sao tách rời (không có parent); nếu result là null thì trả về null.</returns>
    /// <exception cref="RpcMethodException">
    /// Thrown when the peer answers with an error object. Khi peer trả về đối tượng lỗi.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the connection closes before a response arrives. Khi kết nối đóng trước khi có phản hồi.
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled while waiting. Khi <paramref name="ct"/> bị hủy khi đang chờ.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="RunAsync"/> has not been started yet. Khi <see cref="RunAsync"/> chưa được khởi động.
    /// </exception>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        IJsonRpcTransport transport = _transport
            ?? throw new InvalidOperationException("RunAsync must be started before calling CallAsync.");
        string id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate) _pending[id] = waiter;

        try
        {
            await WriteAsync(transport, JsonRpc.SerializeRequest(new JsonRpcRequest(id, method, @params)), ct);
            return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_pendingGate) _pending.Remove(id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task NotifyAsync(string method, JsonNode? @params, CancellationToken ct = default)
    {
        IJsonRpcTransport transport = _transport
            ?? throw new InvalidOperationException("RunAsync must be started before sending notifications.");
        await WriteAsync(transport, JsonRpc.SerializeNotification(new JsonRpcNotification(method, @params)), ct);
    }

    /// <summary>
    /// Writes one line while holding the shared write lock so concurrent writers cannot interleave.
    /// Ghi một dòng khi đang giữ lock ghi chung để các luồng ghi đồng thời không bị xen kẽ.
    /// </summary>
    private async Task WriteAsync(IJsonRpcTransport transport, string line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await transport.WriteLineAsync(line, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Handles an incoming request line exactly as before: lookup, dispatch, respond.
    /// Xử lý dòng yêu cầu đến đúng như trước: tìm handler, điều phối, phản hồi.
    /// </summary>
    private async Task DispatchRequestAsync(IJsonRpcTransport transport, JsonNode node, string method, CancellationToken ct)
    {
        JsonNode? idNode = node["id"];
        string id = (idNode is null ? null : ExtractId(idNode)) ?? "null";
        JsonNode? @params = node["params"];

        if (!_handlers.TryGetValue(method, out var handler))
        {
            await WriteAsync(transport, JsonRpc.SerializeResponse(
                new JsonRpcResponse(id, null, new JsonRpcError(RpcErrorCodes.MethodNotFound, $"Method not found: {method}"))), ct);
            return;
        }

        try
        {
            JsonNode? result = await handler(@params);
            await WriteAsync(transport, JsonRpc.SerializeResponse(new JsonRpcResponse(id, result, null)), ct);
        }
        catch (RpcMethodException ex)
        {
            await WriteAsync(transport, JsonRpc.SerializeResponse(
                new JsonRpcResponse(id, null, new JsonRpcError(ex.Code, ex.Message))), ct);
        }
        catch (Exception ex)
        {
            await WriteAsync(transport, JsonRpc.SerializeResponse(
                new JsonRpcResponse(id, null, new JsonRpcError(RpcErrorCodes.InternalError, ex.Message))), ct);
        }
    }

    /// <summary>
    /// Completes the CallAsync waiter registered for a response line (result or error); the result is a detached clone.
    /// Hoàn tất waiter của CallAsync đã đăng ký cho dòng phản hồi (result hoặc error); kết quả là bản sao tách rời.
    /// </summary>
    internal static void RouteResponse(TaskCompletionSource<JsonNode?> waiter, JsonNode response)
    {
        JsonNode? errorNode = response["error"];
        if (errorNode is null)
        {
            // DeepClone: the result must not stay a child of the parsed envelope, or re-parenting it
            // into a serialized response throws InvalidOperationException "The node already has a parent."
            waiter.TrySetResult(response["result"]?.DeepClone());
            return;
        }

        int code = RpcErrorCodes.InternalError;
        string message = "Unknown error";
        if (errorNode.GetValueKind() == JsonValueKind.Object)
        {
            if (errorNode["code"] is { } codeNode && codeNode.GetValueKind() == JsonValueKind.Number)
                code = codeNode.GetValue<int>();
            if (errorNode["message"] is { } messageNode && messageNode.GetValueKind() == JsonValueKind.String)
                message = messageNode.GetValue<string>()!;
        }
        waiter.TrySetException(new RpcMethodException(code, message));
    }

    /// <summary>
    /// Extracts a wire id as a string key; returns null when the id node is missing or not a scalar.
    /// Lấy id trên wire thành khóa chuỗi; trả null khi node id thiếu hoặc không phải giá trị đơn.
    /// </summary>
    private static string? ExtractId(JsonNode idNode) => idNode.GetValueKind() switch
    {
        JsonValueKind.String => idNode.GetValue<string>(),
        JsonValueKind.Number => idNode.ToJsonString(),
        _ => null,
    };

    /// <summary>
    /// Removes and returns the waiter registered for an id, if any.
    /// Gỡ và trả waiter đã đăng ký cho id (nếu có).
    /// </summary>
    private bool TryTakePending(string id, out TaskCompletionSource<JsonNode?> waiter)
    {
        lock (_pendingGate)
        {
            if (_pending.TryGetValue(id, out var found))
            {
                _pending.Remove(id);
                waiter = found;
                return true;
            }
        }
        waiter = null!;
        return false;
    }

    /// <summary>
    /// Fails every outstanding CallAsync when the read loop ends (EOF or cancellation).
    /// Làm hỏng mọi CallAsync còn treo khi vòng lặp đọc kết thúc (EOF hoặc hủy).
    /// </summary>
    private void FailAllPending(CancellationToken ct)
    {
        List<TaskCompletionSource<JsonNode?>>? leftovers = null;
        lock (_pendingGate)
        {
            if (_pending.Count > 0)
            {
                leftovers = [.. _pending.Values];
                _pending.Clear();
            }
        }
        if (leftovers is null) return;

        foreach (TaskCompletionSource<JsonNode?> waiter in leftovers)
        {
            waiter.TrySetException(ct.IsCancellationRequested
                ? new TaskCanceledException("The JSON-RPC read loop was cancelled before a response arrived.")
                : new TimeoutException("The connection closed before a response arrived."));
        }
    }

    /// <summary>The parse-error response line kept from the M0 contract.</summary>
    private static string ParseErrorLine() => JsonRpc.SerializeResponse(
        new JsonRpcResponse("null", null, new JsonRpcError(RpcErrorCodes.ParseError, "Parse error")));
}
