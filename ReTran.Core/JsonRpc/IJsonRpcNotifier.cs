using System.Text.Json.Nodes;

namespace ReTran.Core.JsonRpc;

/// <summary>
/// One-way JSON-RPC sender that delivers notifications (lines without id) to a peer.
/// Bộ gửi JSON-RPC một chiều, chuyển thông báo (dòng không id) tới peer.
/// </summary>
public interface IJsonRpcNotifier
{
    /// <summary>
    /// Sends a notification (a JSON-RPC line without id) to the peer.
    /// Gửi một thông báo (dòng JSON-RPC không id) tới peer.
    /// </summary>
    /// <param name="method">Method name to invoke. Tên phương thức cần gọi.</param>
    /// <param name="@params">Optional parameters for the method; serialized as an empty object when null.
    /// Tham số tùy chọn cho phương thức; khi null sẽ được đóng gói thành object rỗng.</param>
    /// <param name="ct">Token used to cancel the send. Token dùng để hủy việc gửi.</param>
    /// <returns>A task that completes once the line has been written.
    /// Task hoàn tất ngay khi dòng đã được ghi.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the server's read loop has not been started. Khi vòng lặp đọc của máy chủ chưa được khởi động.
    /// </exception>
    Task NotifyAsync(string method, JsonNode? @params, CancellationToken ct = default);
}
