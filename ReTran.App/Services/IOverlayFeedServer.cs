namespace ReTran_App.Services;

/// <summary>
/// Disposable overlay feed endpoint — the minimal contract <see cref="Update.UpdateStopper"/> needs
/// to release the overlay port before install, typed to an interface so tests can substitute a
/// feed that fails on disposal.
/// Điểm cuối feed overlay có thể dispose — hợp đồng tối thiểu mà <see cref="Update.UpdateStopper"/>
/// cần để nhả cổng overlay trước khi cài, đặt trên interface để test thay bằng feed giả ném lỗi lúc dispose.
/// </summary>
public interface IOverlayFeedServer
{
    /// <summary>
    /// Stops the listener and completes open streams.
    /// Dừng listener và kết thúc các luồng đang mở.
    /// </summary>
    ValueTask DisposeAsync();
}
