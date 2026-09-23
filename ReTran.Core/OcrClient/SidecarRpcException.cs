namespace ReTran.Core.OcrClient;

/// <summary>
/// Error reply from the sidecar process (JSON-RPC error code + message).
/// Phản hồi lỗi từ tiến trình sidecar (mã lỗi JSON-RPC + thông điệp).
/// </summary>
public sealed class SidecarRpcException(int code, string message) : Exception(message)
{
    /// <summary>JSON-RPC error code from the sidecar (-32602, -32603, ...). Mã lỗi JSON-RPC từ sidecar.</summary>
    public int Code { get; } = code;
}
