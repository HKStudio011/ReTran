using System.Text.Json.Nodes;

namespace ReTran.Core.JsonRpc;

/// <summary>
/// A JSON-RPC 2.0 request: a method call with an id and optional params.
/// Yêu cầu JSON-RPC 2.0: lời gọi phương thức kèm id và tham số tùy chọn.
/// </summary>
public sealed record JsonRpcRequest(string Id, string Method, JsonNode? Params);

/// <summary>
/// A JSON-RPC 2.0 error object (code + message + optional data).
/// Đối tượng lỗi JSON-RPC 2.0 (mã + thông điệp + dữ liệu tùy chọn).
/// </summary>
public sealed record JsonRpcError(int Code, string Message, JsonNode? Data = null);

/// <summary>
/// A JSON-RPC 2.0 response carrying either a result or an error.
/// Phản hồi JSON-RPC 2.0 mang kết quả hoặc lỗi.
/// </summary>
public sealed record JsonRpcResponse(string Id, JsonNode? Result, JsonRpcError? Error);

/// <summary>
/// A JSON-RPC 2.0 notification (no id) used for streaming results.
/// Thông báo JSON-RPC 2.0 (không id) dùng cho kết quả dạng luồng.
/// </summary>
public sealed record JsonRpcNotification(string Method, JsonNode? Params);

/// <summary>
/// Standard JSON-RPC error codes.
/// Mã lỗi chuẩn JSON-RPC.
/// </summary>
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>
/// Converts records to/from JSON-RPC 2.0 nodes and frames single-line messages.
/// Chuyển đổi record sang/ra node JSON-RPC 2.0 và đóng gói tin một dòng.
/// </summary>
public static class JsonRpc
{
    /// <summary>Serializes a request into one line of JSON (no trailing newline).</summary>
    public static string SerializeRequest(JsonRpcRequest r) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = r.Id,
        ["method"] = r.Method,
        ["params"] = r.Params ?? new JsonObject(),
    }.ToJsonString();

    /// <summary>Serializes a response into one line of JSON.</summary>
    public static string SerializeResponse(JsonRpcResponse resp)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = resp.Id };
        if (resp.Error is not null)
            o["error"] = new JsonObject { ["code"] = resp.Error.Code, ["message"] = resp.Error.Message, ["data"] = resp.Error.Data ?? new JsonObject() };
        else
            o["result"] = resp.Result ?? JsonValue.Create<object?>(null);
        return o.ToJsonString();
    }

    /// <summary>Serializes a notification into one line of JSON (no id).</summary>
    public static string SerializeNotification(JsonRpcNotification n) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = n.Method,
        ["params"] = n.Params ?? new JsonObject(),
    }.ToJsonString();

    /// <summary>Parses one line into a JsonNode; returns null for blank lines.</summary>
    public static JsonNode? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonNode.Parse(line); }
        catch { return null; }
    }

    /// <summary>Wraps a request as its wire JsonNode (used by tests and the CLI).</summary>
    public static JsonNode RequestToNode(JsonRpcRequest r) => JsonNode.Parse(SerializeRequest(r))!;
}
