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
    internal const int NiifNone = 0, NiifInfo = 1, NiifWarning = 2, NiifError = 3;
    internal const uint WmLbuttonUp = 0x0202, WmLbuttonDblClk = 0x0203, WmRbuttonUp = 0x0205, WmContextMenu = 0x007B;

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
    /// <param name="lParamMouseMsg">Mouse code in the low word of lParam (v4 shell). Mã chuột trong từ thấp của lParam (shell v4).</param>
    internal static TrayDispatch Dispatch(uint wParamMsg, uint taskbarCreatedId, uint callbackMessage, uint lParamMouseMsg)
    {
        if (wParamMsg == taskbarCreatedId) return TrayDispatch.ReAddIcon;
        if (wParamMsg != callbackMessage) return TrayDispatch.Ignore;

        return lParamMouseMsg switch
        {
            WmLbuttonUp or WmLbuttonDblClk => TrayDispatch.LeftClick,
            WmRbuttonUp or WmContextMenu => TrayDispatch.ShowMenu,
            _ => TrayDispatch.Ignore,
        };
    }
}
