using System.Text.Json.Nodes;

namespace ReTran.Core.OcrClient;

/// <summary>
/// JSON-RPC request surface over the sidecar stdio channel, mocked in pipeline tests.
/// Mặt phẳng yêu cầu JSON-RPC qua kênh stdio sidecar, được mock trong test pipeline.
/// </summary>
public interface ISidecarRpc
{
    /// <summary>
    /// Sends one request and awaits its response (matched by id).
    /// Gửi một yêu cầu và đợi phản hồi (ghép theo id).
    /// </summary>
    /// <exception cref="SidecarRpcException">Thrown when the sidecar replies with an error object. Khi sidecar trả về object lỗi.</exception>
    Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct);
}
