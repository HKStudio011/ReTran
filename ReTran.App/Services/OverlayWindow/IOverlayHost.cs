namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Window that hosts the overlay: layered, always-on-top and click-through, so it can sit
/// over a game window without stealing input. The real implementation is Win32-based (Task 5);
/// tests substitute a fake.
/// Cửa sổ chứa overlay: layered, luôn trên cùng và click-through, để đè lên cửa sổ game
/// mà không chiếm input. Implementation thật dùng Win32 (Task 5); test dùng fake.
/// </summary>
public interface IOverlayHost : IDisposable
{
    /// <summary>
    /// Whether the native window has been created yet. Native window đã được tạo hay chưa.
    /// </summary>
    bool IsCreated { get; }

    /// <summary>
    /// Creates the native window (idempotent). Tạo native window (idempotent).
    /// </summary>
    void Create();

    /// <summary>
    /// Moves/resizes the window to the given physical-pixel rectangle and shows it.
    /// Di chuyển/đổi cỡ cửa sổ về hình chữ nhật pixel vật lý cho trước và hiện nó.
    /// </summary>
    /// <param name="x">Left edge in physical pixels. Mép trái theo pixel vật lý.</param>
    /// <param name="y">Top edge in physical pixels. Mép trên theo pixel vật lý.</param>
    /// <param name="width">Width in physical pixels. Chiều rộng theo pixel vật lý.</param>
    /// <param name="height">Height in physical pixels. Chiều cao theo pixel vật lý.</param>
    void Show(int x, int y, int width, int height);

    /// <summary>
    /// Hides the window without destroying it. Ẩn cửa sổ mà không hủy nó.
    /// </summary>
    void Hide();

    /// <summary>
    /// Draws the current frame; an empty list clears the window.
    /// Vẽ frame hiện tại; danh sách rỗng làm trống cửa sổ.
    /// </summary>
    /// <param name="boxes">Boxes in viewport pixel coordinates. Các hộp theo tọa độ pixel viewport.</param>
    void Draw(IReadOnlyList<DrawBox> boxes);
}
