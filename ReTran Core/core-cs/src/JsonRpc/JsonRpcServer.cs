using System.Text.Json.Nodes;

namespace ReTran.Core.JsonRpc;

/// <summary>
/// Minimal JSON-RPC 2.0 server: reads lines, dispatches to registered handlers, writes responses.
/// Máy chủ JSON-RPC 2.0 tối giản: đọc dòng, điều phối tới handler đã đăng ký, ghi phản hồi.
/// </summary>
public sealed class JsonRpcServer
{
    private readonly Dictionary<string, Func<JsonNode?, Task<JsonNode?>>> _handlers = new();

    /// <summary>Registers a handler for a method name (e.g. "core.ping").</summary>
    public void Register(string method, Func<JsonNode?, Task<JsonNode?>> handler) => _handlers[method] = handler;

    /// <summary>Runs the read/dispatch/write loop until the transport is closed.</summary>
    public async Task RunAsync(IJsonRpcTransport transport, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await transport.ReadLineAsync(ct);
            if (line is null) break; // EOF

            JsonNode? node = JsonRpc.ParseLine(line);
            if (node is null || node["jsonrpc"]?.GetValue<string>() != "2.0" || node["method"] is null)
            {
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(
                    new JsonRpcResponse("null", null, new JsonRpcError(RpcErrorCodes.ParseError, "Parse error"))), ct);
                continue;
            }

            string id = node["id"]?.GetValue<string>() ?? "null";
            string method = node["method"]!.GetValue<string>()!;
            JsonNode? @params = node["params"];

            if (!_handlers.TryGetValue(method, out var handler))
            {
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(
                    new JsonRpcResponse(id, null, new JsonRpcError(RpcErrorCodes.MethodNotFound, $"Method not found: {method}"))), ct);
                continue;
            }

            try
            {
                JsonNode? result = await handler(@params);
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(new JsonRpcResponse(id, result, null)), ct);
            }
            catch (Exception ex)
            {
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(
                    new JsonRpcResponse(id, null, new JsonRpcError(RpcErrorCodes.InternalError, ex.Message))), ct);
            }
        }
    }
}
