using System.Drawing;
using CoreStyle = ReTran.Core.Serve.BoxAnnotationStyle;
using AppStyle = ReTran_App.Services.OverlayWindow.BoxAnnotationStyle;

namespace ReTran.Tests;

/// <summary>
/// Pins the shared constants and the duplicated fit logic of the two box annotation styles. Core
/// and App cannot share the file (the App spawns Core as a separate process), so the copy is kept
/// identical in logic; drift would silently desync preview, overlay and menu metrics.
/// Ghim các hằng số dùng chung và phần logic fit trùng lặp của hai bản BoxAnnotationStyle. Core và
/// App không thể dùng chung file (App spawn Core như process riêng), nên bản sao được giữ y hệt về
/// logic; lệch sẽ âm thầm lệch số đo giữa preview, overlay và menu.
/// </summary>
public class BoxAnnotationStyleSyncTests
{
    /// <summary>146-char Vietnamese translation shared by the parity cases. Bản dịch 146 ký tự dùng chung cho các ca đối chiếu.</summary>
    private const string LongText =
        "Đây là bản dịch tiếng Việt khá dài cho một đoạn hội thoại trong game, cần xuống dòng nhiều lần để hiển thị đầy đủ nội dung bên trong chiếc hộp này";

    /// <summary>Every geometry/typography constant must match between the two copies. Mọi hằng số hình học/kiểu chữ phải khớp giữa hai bản.</summary>
    [Fact]
    public void ConstantsMatch()
    {
        Assert.Equal(CoreStyle.Inset, AppStyle.Inset);
        Assert.Equal(CoreStyle.MinFontSize, AppStyle.MinFontSize);
        Assert.Equal(CoreStyle.MaxFontSize, AppStyle.MaxFontSize);
        Assert.Equal(CoreStyle.FontHeightRatio, AppStyle.FontHeightRatio);
        Assert.Equal(CoreStyle.PadX(20), AppStyle.PadX(20));
        Assert.Equal(CoreStyle.PadY(20), AppStyle.PadY(20));
    }

    /// <summary>
    /// The duplicated Fit/Measure logic must choose the same size, lines and fit verdict on both
    /// copies — constants alone cannot catch an algorithm drift (e.g. one side still capping
    /// lines). Logic Fit/Measure trùng lặp phải chọn cùng cỡ, số dòng và kết quả fits trên cả hai
    /// bản — chỉ hằng số không bắt được lệch thuật toán (ví dụ một bên vẫn trần dòng).
    /// </summary>
    [Fact]
    public void FitLogic_MatchesCore()
    {
        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        using StringFormat coreFormat = CoreStyle.CreateTextFormat();
        using StringFormat appFormat = AppStyle.CreateTextFormat();

        (string Text, float InnerW, float InnerH, float BoxH)[] cases =
        [
            (LongText, 356f, 202f, 210f),                    // tall paragraph — uses vertical room
            (LongText, 1096f, 32f, 40f),                     // wide short — single line
            ("Đây là bản dịch tiếng Việt của một dòng hội thoại ngắn trong game", 236f, 22f, 30f), // tiny box — floor, not fitting
            ("Chào", 188f, 82f, 90f),                        // short text in a tall box
        ];

        foreach ((string text, float innerW, float innerH, float boxH) in cases)
        {
            CoreStyle.TextFit core = CoreStyle.Fit(g, text, innerW, innerH, boxH, coreFormat);
            AppStyle.TextFit app = AppStyle.Fit(g, text, innerW, innerH, boxH, appFormat);

            Assert.Equal(core.Size, app.Size);
            Assert.Equal(core.Lines, app.Lines);
            Assert.Equal(core.Fits, app.Fits);
            Assert.Equal(core.Block.Height, app.Block.Height, 1); // 0.1 px precision — cùng font/máy
        }
    }
}
