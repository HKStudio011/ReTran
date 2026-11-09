namespace ReTran_App.Services.FloatingMenu;

/// <summary>
/// Window hosting the floating menu: layered, topmost, NOT click-through and non-activating.
/// Same worker thread/pump as the overlay box window. Lifetime is tied to the host's
/// IOverlayHost disposal (do not dispose twice).
/// Cửa sổ chứa menu nổi: layered, topmost, KHÔNG click-through và không kích hoạt.
/// Cùng worker thread/pump với cửa sổ box overlay. Tuổi thọ gắn với Dispose của IOverlayHost.
/// </summary>
public interface IMenuWindowHost
{
    bool MenuCreated { get; }               // window menu đã tạo chưa (KHÔNG phải IsCreated của IOverlayHost — host implement cả hai interface, ngữ nghĩa khác nhau)
    nint MenuHwnd { get; }                  // 0 khi chưa tạo — dùng cho z-order box
    void CreateMenu();                      // idempotent; tự khởi worker thread nếu chưa chạy
    void ShowMenu(int x, int y, int width, int height); // pixel vật lý, SWP_NOACTIVATE
    void HideMenu();
    void DrawMenu(System.Drawing.Bitmap bitmap); // 32bpp premultiplied, đúng cỡ window
    event Action<int, int>? PanelClicked;   // (x,y) TOÁN HỆ PANEL (gốc = góc trái trên panel), chỉ khi click không kéo
    event Action<int, int>? DragFinished;   // (X,Y) desktop vật lý mới sau khi kéo xong
}
