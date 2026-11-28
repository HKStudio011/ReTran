using System.Runtime.InteropServices;
using SD = System.Drawing;
using ReTran_App.Services.Branding;
using ReTran_App.Services.OverlayWindow;

namespace ReTran_App.Services.Tray;

/// <summary>
/// GDI+-drawn 32×32 tray icon (rounded Ink background with the folded-A monogram); owns the
/// HICON and destroys it on dispose.
/// Icon tray 32×32 vẽ bằng GDI+ (nền Ink bo góc với monogram A dải gấp); sở hữu HICON và hủy nó
/// khi dispose.
/// </summary>
public sealed class TrayIconSource : IDisposable
{
    private const int Size = 32;
    private const float CornerRadius = 6f;

    private bool _disposed;

    /// <summary>Creates the icon and keeps its HICON alive until <see cref="Dispose"/>.</summary>
    /// <remarks>
    /// The raw handle from <c>Bitmap.GetHicon</c> is kept on purpose — an
    /// <see cref="SD.Icon.FromHandle"/> wrapper would destroy it when the wrapper is disposed.
    /// Tạo icon và giữ HICON sống tới khi <see cref="Dispose"/>. Giữ handle trần từ
    /// <c>Bitmap.GetHicon</c> cố ý — wrapper <see cref="SD.Icon.FromHandle"/> sẽ destroy nó khi
    /// wrapper bị dispose.
    /// </remarks>
    public TrayIconSource()
    {
        using var bitmap = RenderBitmap();
        Handle = bitmap.GetHicon();
    }

    /// <summary>
    /// Renders the 32×32 folded-A tray bitmap; the caller owns and must dispose the returned bitmap.
    /// Vẽ bitmap tray A dải gấp 32×32; caller sở hữu và phải dispose bitmap được trả về.
    /// </summary>
    /// <returns>
    /// A new transparent-cornered bitmap with an Ink rounded background and the shared brand mark.
    /// Bitmap mới có các góc trong suốt, nền Ink bo góc và brand mark dùng chung.
    /// </returns>
    internal static SD.Bitmap RenderBitmap()
    {
        var bitmap = new SD.Bitmap(Size, Size, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using (SD.Graphics g = SD.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);

                using var pillBrush = new SD.SolidBrush(SD.Color.FromArgb(255, 7, 32, 30));
                using var path = new SD.Drawing2D.GraphicsPath();
                BoxAnnotationStyle.BuildRoundedRect(path, new SD.RectangleF(0f, 0f, Size, Size), CornerRadius);
                g.FillPath(pillBrush, path);

                BrandMarkRenderer.Draw(g, new SD.RectangleF(4f, 4f, 24f, 24f));
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The native icon handle; non-zero, stable across every read and valid until Dispose.
    /// Handle icon native; khác 0, ổn định qua mọi lần đọc và hợp lệ tới khi Dispose.
    /// </summary>
    public nint Handle { get; }

    /// <summary>Whether the handle has already been destroyed. Handle đã bị hủy hay chưa.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Destroys the icon exactly once (idempotent). Hủy icon đúng một lần (idempotent).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != 0) NativeMethods.DestroyIcon(Handle);
    }

    /// <summary>Win32 entry points used by the source (internal only). Các hàm Win32 mà source dùng (chỉ nội bộ).</summary>
    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(nint hIcon);
    }
}
