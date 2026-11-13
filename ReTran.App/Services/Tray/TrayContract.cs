namespace ReTran_App.Services.Tray;

/// <summary>
/// Pure Win32 decision table for the tray host: flag combinations, message ids and callback
/// dispatch — every decision testable without a real notification area.
/// Bảng quyết định Win32 thuần cho tray host: tổ hợp cờ, mã tin nhắn và dispatch callback —
/// mọi quyết định đều test được không cần tray thật.
/// </summary>
internal static class TrayContract
{
    internal const uint NifMessage = 0x0001, NifIcon = 0x0002, NifTip = 0x0004, NifInfo = 0x0010;
    internal const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    // NIM_SETVERSION (0x00000004) with NOTIFYICON_VERSION_4 (4): MSDN — the shell switches the
    // callback payload to the v4 layout only when this message is sent; uVersion inside NIM_ADD
    // alone is not a guaranteed contract.
    // NIM_SETVERSION (0x00000004) với NOTIFYICON_VERSION_4 (4): MSDN — shell chỉ chuyển payload
    // callback sang bố cục v4 khi tin nhắn này được gửi; chỉ đặt uVersion trong NIM_ADD không phải
    // hợp đồng được bảo đảm.
    internal const uint NimSetVersion = 0x00000004;
    internal const uint NotifyIconVersion4 = 4;
    internal const int NiifNone = 0, NiifInfo = 1, NiifWarning = 2, NiifError = 3;
    internal const uint WmLbuttonUp = 0x0202, WmLbuttonDblClk = 0x0203, WmRbuttonUp = 0x0205, WmContextMenu = 0x007B;
    // NOTIFYICON_VERSION_4: the shell reports a left click as NIN_SELECT (WM_USER+0) / NIN_KEYSELECT
    // (WM_USER+1) instead of WM_LBUTTONUP. NIN_KEYSELECT (0x0401) numerically equals the
    // CallbackMessage used by the host, but they live in DIFFERENT fields — CallbackMessage is the
    // window message id, NIN_* is the mouse code inside LOWORD(lParam) — so the duplication is
    // intentional and must never be "unified" later.
    // NOTIFYICON_VERSION_4: shell báo left-click dưới dạng NIN_SELECT (WM_USER+0) / NIN_KEYSELECT
    // (WM_USER+1) thay vì WM_LBUTTONUP. NIN_KEYSELECT (0x0401) trùng số với CallbackMessage của
    // host, nhưng chúng nằm ở HAI field khác nhau — CallbackMessage là mã tin nhắn cửa sổ, NIN_* là
    // mã chuột trong LOWORD(lParam) — nên sự trùng số này là cố ý, tuyệt đối không "gộp" lại sau này.
    internal const uint NinSelect = 0x0400, NinKeySelect = 0x0401;

    /// <summary>
    /// uFlags for NIM_ADD: message callbacks plus icon and tooltip.
    /// uFlags cho NIM_ADD: callback tin nhắn kèm icon và tooltip.
    /// </summary>
    internal static uint AddUFlags() => NifMessage | NifIcon | NifTip;

    /// <summary>
    /// uFlags for a NIM_MODIFY that updates the balloon info: NIF_INFO plus the tooltip when requested.
    /// uFlags cho NIM_MODIFY cập nhật balloon: NIF_INFO kèm tooltip khi được yêu cầu.
    /// </summary>
    internal static uint ModifyUFlags(bool withTip) => NifInfo | (withTip ? NifTip : 0u);

    /// <summary>
    /// Maps a balloon level to the NIIF_* dwInfoFlags value.
    /// Ánh xạ mức balloon sang giá trị dwInfoFlags NIIF_*.
    /// </summary>
    internal static uint InfoFlags(BalloonIcon icon) => icon switch
    {
        BalloonIcon.Info => (uint)NiifInfo,
        BalloonIcon.Warning => (uint)NiifWarning,
        BalloonIcon.Error => (uint)NiifError,
        _ => (uint)NiifNone,
    };

    internal enum TrayDispatch { Ignore, ReAddIcon, ShowMenu, LeftClick }

    /// <summary>
    /// Decides how the host reacts to one window message for the tray icon.
    /// Quyết định host phản hồi thế nào với một tin nhắn cửa sổ của icon tray.
    /// </summary>
    /// <param name="wParamMsg">The WM received: the window message id, or the registered "TaskbarCreated" broadcast id. WM nhận được: mã tin nhắn cửa sổ, hoặc mã broadcast "TaskbarCreated" đã đăng ký.</param>
    /// <param name="taskbarCreatedId">Id returned by RegisterWindowMessage("TaskbarCreated"). Id trả về bởi RegisterWindowMessage("TaskbarCreated").</param>
    /// <param name="callbackMessage">The WM_USER+1 message registered for the icon. Tin nhắn WM_USER+1 đã đăng ký cho icon.</param>
    /// <param name="lParamMouseMsg">Mouse code in the low word of lParam (v4 layout, pinned explicitly by NIM_SETVERSION at every add/re-add). Mã chuột trong từ thấp của lParam (bố cục v4, được ghim tường minh bằng NIM_SETVERSION ở mọi lần add/re-add).</param>
    internal static TrayDispatch Dispatch(uint wParamMsg, uint taskbarCreatedId, uint callbackMessage, uint lParamMouseMsg)
    {
        if (wParamMsg == taskbarCreatedId) return TrayDispatch.ReAddIcon;
        if (wParamMsg != callbackMessage) return TrayDispatch.Ignore;

        return lParamMouseMsg switch
        {
            // Under v4 a plain left click / keyboard select arrives as NIN_SELECT / NIN_KEYSELECT;
            // WM_LBUTTONUP and WM_LBUTTONDBLCLK stay mapped for the legacy v0 payload.
            // Dưới v4 left-click thuần / chọn bằng bàn phím đến dưới dạng NIN_SELECT / NIN_KEYSELECT;
            // WM_LBUTTONUP và WM_LBUTTONDBLCLK giữ mapping cho payload v0 cũ.
            WmLbuttonUp or WmLbuttonDblClk or NinSelect or NinKeySelect => TrayDispatch.LeftClick,
            // v4 right-click = WM_CONTEXTMENU (already the primary path); WM_RBUTTONUP stays for v0.
            // Chuột phải v4 = WM_CONTEXTMENU (đường chính, đã có); WM_RBUTTONUP giữ cho v0.
            WmRbuttonUp or WmContextMenu => TrayDispatch.ShowMenu,
            _ => TrayDispatch.Ignore,
        };
    }
}
