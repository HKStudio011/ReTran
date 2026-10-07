using System.Text.Json.Nodes;

namespace ReTran_App.Core;

/// <summary>
/// Connection surface the pipeline controller needs from the Core process: liveness, request/reply
/// calls, the Core→App request handler, and Core→App notifications.
/// Bề mặt kết nối mà bộ điều phối pipeline cần từ tiến trình Core: trạng thái sống, gọi yêu cầu/
/// phản hồi, handler yêu cầu Core→App và thông báo Core→App.
/// </summary>
public interface ICorePeer
{
    /// <summary>
    /// Whether the core process connection is currently up. True after a successful start and
    /// false again after disposal.
    /// Kết nối tiến trình core hiện có đang mở hay không. True sau khi start thành công và trở lại
    /// false sau khi dispose.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Starts the core process when it is not running yet, locating the executable via
    /// <see cref="CoreLocator.FindCoreExe"/>.
    /// Khởi chạy tiến trình core khi chưa chạy, tự tìm tệp thực thi qua <see cref="CoreLocator.FindCoreExe"/>.
    /// </summary>
    /// <param name="ct">Token used to cancel the start. Token dùng để hủy việc start.</param>
    /// <returns>A task that completes once the process is up. Task hoàn tất khi tiến trình đã lên.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the core executable cannot be found. Ném khi không tìm thấy tệp core.</exception>
    Task StartCoreAsync(CancellationToken ct);

    /// <summary>
    /// Sends a JSON-RPC request to the core and awaits its result.
    /// Gửi yêu cầu JSON-RPC tới core và chờ kết quả.
    /// </summary>
    /// <param name="method">Method name, e.g. <c>core.start</c>. Tên phương thức, ví dụ <c>core.start</c>.</param>
    /// <param name="@params">Params object, or <see langword="null"/> for none. Object tham số, hoặc <see langword="null"/> nếu không có.</param>
    /// <param name="ct">Token used to cancel the call. Token dùng để hủy lời gọi.</param>
    /// <returns>The result node of the response. Node kết quả của phản hồi.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the core is not started. Ném khi core chưa được start.</exception>
    Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct);

    /// <summary>
    /// Registers the single handler that answers requests sent by the core (Core → App). Kept
    /// across core restarts.
    /// Đăng ký handler duy nhất trả lời yêu cầu do core gửi tới (Core → App). Được giữ qua các
    /// lần khởi động lại core.
    /// </summary>
    /// <param name="handler">
    /// Receives the method name and params and returns the result node; a thrown exception is
    /// answered as a -32603 error.
    /// Nhận tên phương thức và params rồi trả về node kết quả; exception được ném ra sẽ được trả
    /// lời bằng lỗi -32603.
    /// </param>
    void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler);

    /// <summary>
    /// Raised for each notification (a message without an id) received from the core.
    /// Ném lên mỗi thông báo (tin không có id) nhận từ core.
    /// </summary>
    event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>
    /// Raised when a request or notification handler throws; the read loop keeps running.
    /// Ném lên khi handler của yêu cầu hoặc thông báo ném exception; vòng lặp đọc vẫn chạy tiếp.
    /// </summary>
    event Action<Exception>? HandlerError;
}
