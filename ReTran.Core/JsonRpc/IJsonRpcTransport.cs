namespace ReTran.Core.JsonRpc;

/// <summary>
/// Abstraction over the byte/line channel carrying JSON-RPC messages.
/// Trừu tượng cho kênh dòng mang tin nhắn JSON-RPC.
/// </summary>
public interface IJsonRpcTransport
{
    /// <summary>Returns the next line, or null when the stream is closed.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>Writes one framed line to the peer.</summary>
    Task WriteLineAsync(string line, CancellationToken ct);
}
