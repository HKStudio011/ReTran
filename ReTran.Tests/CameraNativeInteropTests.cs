using System.Runtime.InteropServices;

/// <summary>
/// Interop test for the native BGRA → YUY2 conversion the camera filter runs per frame. Skipped
/// silently when the native camera DLL was not built for this configuration.
/// Test interop cho phép chuyển BGRA → YUY2 native mà filter camera chạy mỗi khung. Bỏ qua im
/// lặng khi DLL camera native chưa được build cho cấu hình này.
/// </summary>
public class CameraNativeInteropTests
{
    [Fact]
    public void ConvertBgraToYuy2_WhiteAndBlackHitBt601LimitedLimits()
    {
        string dllPath = Path.Combine(AppContext.BaseDirectory, "ReTran.Camera.dll");
        if (!File.Exists(dllPath))
        {
            return;
        }

        byte[] bgra = [255, 255, 255, 255, 0, 0, 0, 255];
        byte[] yuy2 = new byte[4];

        ReTranCamera_ConvertBgraToYuy2(bgra, yuy2, 2);

        Assert.Equal(235, yuy2[0]);
        Assert.Equal(16, yuy2[2]);
        Assert.Equal(128, yuy2[1]);
        Assert.Equal(128, yuy2[3]);
    }

    [DllImport("ReTran.Camera.dll", ExactSpelling = true)]
    private static extern void ReTranCamera_ConvertBgraToYuy2(
        [In] byte[] bgra, [In, Out] byte[] yuy2, uint pixelCount);
}
