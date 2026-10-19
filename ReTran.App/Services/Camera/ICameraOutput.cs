namespace ReTran_App.Services.Camera;

/// <summary>
/// Output surface of the virtual camera: the app pushes every annotated frame Core sends, and
/// marks the last one as held when the pipeline stops.
/// Bề mặt đầu ra của camera ảo: app đẩy mọi khung đã ghi chú mà Core gửi, và đánh dấu giữ khung
/// cuối khi pipeline dừng.
/// </summary>
public interface ICameraOutput : IDisposable
{
    /// <summary>
    /// Publishes one annotated frame to the camera shared memory, replacing the previous frame.
    /// Xuất bản một khung đã ghi chú vào shared memory của camera, thay thế khung trước đó.
    /// </summary>
    /// <param name="seq">Frame number reported by Core; carried for diagnostics only. Số khung do Core báo; chỉ dùng để chẩn đoán.</param>
    /// <param name="w">Width of the provided JPEG in pixels. Chiều rộng của JPEG được cung cấp theo pixel.</param>
    /// <param name="h">Height of the provided JPEG in pixels. Chiều cao của JPEG được cung cấp theo pixel.</param>
    /// <param name="jpeg">Encoded JPEG bytes; the service letterboxes them into the fixed camera frame. Bytes JPEG đã mã hoá; service fit thành khung camera cố định.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the output was already disposed. Ném khi đầu ra đã bị dispose.</exception>
    void Submit(long seq, int w, int h, byte[] jpeg);

    /// <summary>
    /// Keeps the current frame on screen while the pipeline is stopped: the pixels stay as they are
    /// and the frame is flagged as held. Giữ khung hiện tại trên màn hình khi pipeline dừng: pixel
    /// giữ nguyên và khung được đánh dấu là đang giữ.
    /// </summary>
    void Hold();
}
