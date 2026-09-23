namespace ReTran.Core.JsonRpc;

/// <summary>
/// Handler exception that maps to a specific JSON-RPC error code instead of -32603.
/// Exception của handler được map sang mã lỗi JSON-RPC cụ thể thay vì -32603.
/// </summary>
public sealed class RpcMethodException(int code, string message) : Exception(message)
{
    /// <summary>JSON-RPC error code to return (-32602, -32603, ...). Mã lỗi JSON-RPC sẽ trả về.</summary>
    public int Code { get; } = code;
}
