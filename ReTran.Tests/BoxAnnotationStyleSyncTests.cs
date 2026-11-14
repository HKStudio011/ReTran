using CoreStyle = ReTran.Core.Serve.BoxAnnotationStyle;
using AppStyle = ReTran_App.Services.OverlayWindow.BoxAnnotationStyle;

namespace ReTran.Tests;

/// <summary>
/// Pins the shared constants of the duplicated box annotation styles. Core and App cannot share
/// the file (the App spawns Core as a separate process), so the copy is kept identical in logic;
/// drift in these constants would silently desync preview, overlay and menu metrics.
/// Ghim các hằng số dùng chung của hai bản BoxAnnotationStyle trùng lặp. Core và App không thể
/// dùng chung file (App spawn Core như process riêng), nên bản sao được giữ y hệt về logic; lệch
/// hằng số sẽ âm thầm lệch số đo giữa preview, overlay và menu.
/// </summary>
public class BoxAnnotationStyleSyncTests
{
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
}
