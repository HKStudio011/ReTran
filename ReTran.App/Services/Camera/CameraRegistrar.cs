using System.Runtime.InteropServices;

namespace ReTran_App.Services.Camera;

/// <summary>
/// Registers the ReTran virtual camera (per-user) by invoking the ReTranCamera_Register export of ReTran.Camera.dll.
/// Đăng ký camera ảo ReTran (per-user) bằng cách gọi export ReTranCamera_Register của ReTran.Camera.dll.
/// </summary>
public sealed class CameraRegistrar
{
    /// <summary>
    /// Signature of the native ReTranCamera_Register export (HRESULT __stdcall).
    /// Chữ ký của export native ReTranCamera_Register (HRESULT __stdcall).
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeRegister();

    private readonly Action<string> _registerAction;

    /// <summary>
    /// Initializes a registrar that loads the native DLL and calls its registration export.
    /// Khởi tạo registrar nạp DLL native và gọi export đăng ký của nó.
    /// </summary>
    public CameraRegistrar()
        : this(RegisterNative)
    {
    }

    /// <summary>
    /// Initializes a registrar with a custom registration action (test seam).
    /// Khởi tạo registrar với hành động đăng ký tùy chỉnh (seam cho test).
    /// </summary>
    /// <param name="registerAction">Receives the full path of the camera DLL; must not be null.</param>
    public CameraRegistrar(Action<string> registerAction)
    {
        ArgumentNullException.ThrowIfNull(registerAction);
        _registerAction = registerAction;
    }

    /// <summary>
    /// Default camera DLL path: ReTran.Camera.dll beside the running app.
    /// Đường dẫn mặc định: ReTran.Camera.dll cạnh app đang chạy.
    /// </summary>
    public static string DefaultDllPath => Path.Combine(AppContext.BaseDirectory, "ReTran.Camera.dll");

    /// <summary>
    /// Friendly name the native filter registers; capture apps list it under this name.
    /// Tên hiển thị mà filter native đăng ký; các app capture liệt kê nó dưới tên này.
    /// </summary>
    /// <remarks>
    /// Must stay identical to <c>kCameraFriendlyName</c> in ReTran.Camera/src/guids.cpp.
    /// Phải trùng với <c>kCameraFriendlyName</c> trong ReTran.Camera/src/guids.cpp.
    /// </remarks>
    public const string DeviceName = "ReTran Camera";

    /// <summary>
    /// Ensures the virtual camera is registered using <see cref="DefaultDllPath"/>; never throws.
    /// Đảm bảo camera ảo đã đăng ký bằng <see cref="DefaultDllPath"/>; không bao giờ ném lỗi.
    /// </summary>
    public void EnsureRegistered() => EnsureRegistered(DefaultDllPath);

    /// <summary>
    /// Ensures the virtual camera is registered; a missing DLL or export is ignored and no exception escapes.
    /// Đảm bảo camera ảo đã đăng ký; thiếu DLL hoặc export thì bỏ qua, không để lỗi thoát ra ngoài.
    /// </summary>
    /// <param name="cameraDllPath">Full path to ReTran.Camera.dll.</param>
    public void EnsureRegistered(string cameraDllPath)
    {
        try
        {
            _registerAction(cameraDllPath);
        }
        catch (Exception)
        {
            // Never throws — like CoreLocator: an absent DLL/export is a normal state.
            // Không bao giờ ném — giống CoreLocator: thiếu DLL/export là trạng thái bình thường.
        }
    }

    private static void RegisterNative(string cameraDllPath)
    {
        if (!File.Exists(cameraDllPath))
        {
            return;
        }

        IntPtr lib = NativeLibrary.Load(cameraDllPath);
        try
        {
            IntPtr proc = NativeLibrary.GetExport(lib, "ReTranCamera_Register");
            var register = Marshal.GetDelegateForFunctionPointer<NativeRegister>(proc);
            _ = register();
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }
}
