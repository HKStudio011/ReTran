using ReTran_App.Core;

namespace ReTran_App.Services.FloatingMenu;

/// <summary>
/// One entry of the floating menu's capture-source list: a monitor or a window.
/// Một mục trong danh sách nguồn capture của menu nổi: một màn hình hoặc một cửa sổ.
/// </summary>
/// <param name="IsMonitor">True for a monitor, false for a window. True với màn hình, false với cửa sổ.</param>
/// <param name="Id">Zero-based monitor index when <paramref name="IsMonitor"/> is true; unused (−1) for windows — the hwnd is their identity. Chỉ số màn hình (từ 0) khi IsMonitor là true; không dùng (−1) với cửa sổ — hwnd là định danh.</param>
/// <param name="Label">Display text: "Màn hình {Id+1}" for monitors, "{Process} — {Title}" truncated to 40 characters plus an ellipsis for windows. Chữ hiển thị: "Màn hình {Id+1}" với màn hình, "{Process} — {Title}" cắt 40 ký tự kèm dấu ba chấm với cửa sổ.</param>
/// <param name="HwndHex">Window handle as a hex string (<c>0x…</c>) for windows; null for monitors. Handle cửa sổ dạng chuỗi hex (<c>0x…</c>) với cửa sổ; null với màn hình.</param>
public sealed record SourceItem(bool IsMonitor, int Id, string Label, string? HwndHex);

/// <summary>
/// Click zone inside a menu row: Whole is a regular click, Left/Right are the two stepper halves (− / +).
/// Vùng nhấn trong một row của menu: Whole là click thường, Left/Right là hai nửa stepper (− / +).
/// </summary>
public enum MenuZone
{
    /// <summary>Regular click on the row. Click thường trên row.</summary>
    Whole,

    /// <summary>Left 40-px edge of a stepper row (decrement). Mép trái 40 px của row stepper (giảm).</summary>
    Left,

    /// <summary>Right 40-px edge of a stepper row (increment). Mép phải 40 px của row stepper (tăng).</summary>
    Right,
}

/// <summary>
/// Result of a floating-menu hit-test: which row was hit and in which zone of it.
/// Kết quả hit-test menu nổi: row nào bị nhấn và ở vùng nào của row đó.
/// </summary>
/// <param name="RowId">Pinned row id (see the constants on <see cref="FloatingMenuLayout"/>). Row id ghim (xem hằng số trên FloatingMenuLayout).</param>
/// <param name="Zone">Click zone within the row. Vùng nhấn trong row.</param>
public readonly record struct MenuHit(int RowId, MenuZone Zone);

/// <summary>
/// Immutable snapshot of everything the floating menu renders and hit-tests; the service rebuilds it on
/// every redraw so renderer and layout always agree.
/// Snapshot bất biến của mọi thứ menu nổi vẽ và hit-test; service dựng lại mỗi lần redraw để renderer và
/// layout luôn nhất quán.
/// </summary>
/// <param name="Collapsed">Whether the menu shows only the chip. Menu đang ở dạng chip (thu gọn) hay không.</param>
/// <param name="Running">Whether the pipeline is running (drives the Start/Stop label and whether the overlay control is enabled). Pipeline có đang chạy không (quyết định nhãn Start/Stop và việc bật được control lớp phủ).</param>
/// <param name="SourceLabel">Current source display text, e.g. "Màn hình 1" / "game.exe — Tít…" / "(chưa chọn cửa sổ)". Chữ nguồn hiện tại, ví dụ "Màn hình 1" / "game.exe — Tít…" / "(chưa chọn cửa sổ)".</param>
/// <param name="OverlayOn">Whether the overlay is on. Lớp phủ đang bật không.</param>
/// <param name="Dst">Destination language code. Mã ngôn ngữ đích.</param>
/// <param name="Src">Source language code ("auto" allowed). Mã ngôn ngữ nguồn (cho phép "auto").</param>
/// <param name="OcrFps">OCR cadence, clamped to 1..60. Nhịp OCR, kẹp 1..60.</param>
/// <param name="CaptureFps">Capture cadence, clamped to 1..60. Nhịp chụp, kẹp 1..60.</param>
/// <param name="MergeLines">Whether multi-line OCR merging is on. Đang bật gộp nhiều dòng OCR không.</param>
/// <param name="StatsOpen">Whether the stats number block is expanded under the Stats row. Vùng số thống kê đang mở dưới row Thống kê không.</param>
/// <param name="SourceListOpen">Whether the source-list rows are inserted after the Source row. Danh sách nguồn đang chèn sau row Nguồn không.</param>
/// <param name="Sources">The source-list items (monitors first, then windows). Các mục danh sách nguồn (màn hình trước, cửa sổ sau).</param>
/// <param name="ErrorLine">Short error status line shown at the panel bottom, or null when there is none. Dòng trạng thái lỗi ngắn ở đáy panel, hoặc null khi không có.</param>
/// <param name="Fps">Live preview frames per second from RunStats; 0 while idle. Khung preview/giây trực tiếp từ RunStats; 0 khi nghỉ.</param>
/// <param name="BoxCount">Text boxes on the latest overlay frame (live). Số hộp văn bản trên khung overlay mới nhất (trực tiếp).</param>
/// <param name="TranslatedLineCount">Boxes with a non-empty translation on the latest overlay frame (live). Số hộp có bản dịch không rỗng trên khung overlay mới nhất (trực tiếp).</param>
public sealed record MenuState(
    bool Collapsed,
    bool Running,
    string SourceLabel,
    bool OverlayOn,
    string Dst,
    string Src,
    int OcrFps,
    int CaptureFps,
    bool MergeLines,
    bool StatsOpen,
    bool SourceListOpen,
    IReadOnlyList<SourceItem> Sources,
    string? ErrorLine,
    double Fps = 0,
    int BoxCount = 0,
    int TranslatedLineCount = 0);

/// <summary>
/// Pure geometry of the floating menu: window/panel sizes per state, row presence and order, hit-testing
/// (including the shadow margin and rounded corners), fps/language clamps, position clamping into monitor
/// work areas, and the capture-source list. No Win32, no GDI+ — every number here is shared by the
/// renderer, the service and the tests.
/// Hình học thuần của menu nổi: kích thước window/panel theo trạng thái, sự hiện diện và thứ tự row,
/// hit-test (kể cả viền shadow và góc bo tròn), kẹp fps/ngôn ngữ, clamp vị trí vào work area monitor, và
/// danh sách nguồn. Không Win32, không GDI+ — mọi số ở đây được renderer, service và test dùng chung.
/// </summary>
public static class FloatingMenuLayout
{
    /// <summary>Panel width in physical pixels. Chiều rộng panel (pixel vật lý).</summary>
    public const int PanelWidth = 320;

    /// <summary>Shadow border around the panel — the window is larger and this ring is click-through. Viền shadow quanh panel — window lớn hơn và dải này click-through.</summary>
    public const int ShadowMargin = 8;

    /// <summary>Grip strip plus collapse-button band at the top of the panel. Dải grip kéo + nút thu gọn ở đầu panel.</summary>
    public const int HeaderHeight = 36;

    /// <summary>Height of every interactive row (including source-list rows). Chiều cao mỗi row tương tác (kể cả row danh sách nguồn).</summary>
    public const int RowHeight = 44;

    /// <summary>Collapsed chip size. Kích thước chip khi thu gọn.</summary>
    public const int ChipSize = 44;

    /// <summary>Height of the stats number block shown when the Stats row is open. Chiều cao vùng số thống kê khi mở row Thống kê.</summary>
    public const int StatsBlockHeight = 92;

    /// <summary>Height of the "applies on next Start" hint line above the close row. Chiều cao dòng "áp dụng ở lần Start kế tiếp" trên row đóng menu.</summary>
    public const int HintRowHeight = 18;

    /// <summary>Height of the short error status line at the panel bottom (only when an error is set). Chiều cao dòng lỗi ngắn ở đáy panel (chỉ khi có lỗi).</summary>
    public const int ErrorRowHeight = 18;

    /// <summary>Maximum number of windows in the source list. Số cửa sổ tối đa trong danh sách nguồn.</summary>
    public const int MaxSourceWindows = 8;

    /// <summary>Panel/chip corner radius (matches the UI's --r3 token); hit-testing treats points outside this arc as transparent. Bán kính bo góc panel/chip (khớp token --r3 của UI); hit-test coi điểm ngoài cung này là trong suốt.</summary>
    public const int CornerRadius = 12;

    /// <summary>Width of the Left/Right stepper zones at each row edge. Độ rộng vùng Left/Right stepper ở mỗi mép row.</summary>
    public const int StepperZoneWidth = 40;

    /// <summary>Side of the collapse button in the header's right corner, vertically centred. Cạnh nút thu gọn ở góc phải header, căn giữa theo chiều dọc.</summary>
    public const int CollapseButtonSize = 24;

    /// <summary>Distance from the work-area edge for the default first-run position. Khoảng cách mép work area cho vị trí mặc định lần đầu.</summary>
    public const int EdgeMargin = 16;

    /// <summary>Row id of the Start/Stop button row. Row id của row nút Start/Stop.</summary>
    public const int RowStartStop = 0;

    /// <summary>Row id of the capture-source row. Row id của row chọn nguồn capture.</summary>
    public const int RowSource = 1;

    /// <summary>Row id of the overlay switch row. Row id của row switch lớp phủ.</summary>
    public const int RowOverlay = 2;

    /// <summary>Row id of the destination-language cycle row. Row id của row cycle ngôn ngữ đích.</summary>
    public const int RowDst = 3;

    /// <summary>Row id of the source-language cycle row. Row id của row cycle ngôn ngữ nguồn.</summary>
    public const int RowSrc = 4;

    /// <summary>Row id of the OCR-cadence stepper row. Row id của row stepper nhịp OCR.</summary>
    public const int RowOcrFps = 5;

    /// <summary>Row id of the capture-cadence stepper row. Row id của row stepper nhịp chụp.</summary>
    public const int RowCaptureFps = 6;

    /// <summary>Row id of the merge-lines switch row. Row id của row switch gộp nhiều dòng.</summary>
    public const int RowMergeLines = 7;

    /// <summary>Row id of the stats toggle row. Row id của row mở/đóng thống kê.</summary>
    public const int RowStats = 8;

    /// <summary>Row id of the "turn off floating menu" row. Row id của row tắt menu nổi.</summary>
    public const int RowCloseMenu = 9;

    /// <summary>Hit-test id of the collapsed chip. Id hit-test của chip khi thu gọn.</summary>
    public const int Chip = 10;

    /// <summary>Hit-test id of the header collapse button. Id hit-test của nút thu gọn ở header.</summary>
    public const int CollapseButton = 11;

    /// <summary>Base of source-list row ids: item i has id SourceItemBase + i. Đích của row danh sách nguồn: mục i có id SourceItemBase + i.</summary>
    public const int SourceItemBase = 100;

    /// <summary>
    /// Fallback work area (0,0)..(1920,1080) used when no monitor list is available.
    /// Work area dự phòng (0,0)..(1920,1080) dùng khi không có danh sách monitor nào.
    /// </summary>
    public static readonly (int Left, int Top, int Right, int Bottom) FallbackWorkArea = (0, 0, 1920, 1080);

    /// <summary>
    /// Window size (panel plus the shadow margin on all sides) for a menu state.
    /// Kích thước window (panel cộng viền shadow mọi phía) theo trạng thái menu.
    /// </summary>
    /// <param name="s">The menu state snapshot. Snapshot trạng thái menu.</param>
    /// <returns>(Width, Height) in physical pixels; the collapsed chip plus margins when collapsed. (Rộng, Cao) theo pixel vật lý; chip thu gọn cộng viền khi đã thu gọn.</returns>
    public static (int Width, int Height) WindowSize(MenuState s)
    {
        if (s.Collapsed)
            return (ChipSize + 2 * ShadowMargin, ChipSize + 2 * ShadowMargin);
        return (PanelWidth + 2 * ShadowMargin, PanelHeight(s) + 2 * ShadowMargin);
    }

    /// <summary>
    /// Panel rectangle in window coordinates (offset by the shadow margin).
    /// Rect panel trong hệ toạ độ window (dịch bởi viền shadow).
    /// </summary>
    /// <param name="s">The menu state snapshot. Snapshot trạng thái menu.</param>
    /// <returns>(X, Y, W, H) of the panel (or chip when collapsed) inside the window. (X, Y, Rộng, Cao) của panel (hoặc chip khi thu gọn) trong window.</returns>
    public static (int X, int Y, int W, int H) PanelRect(MenuState s)
    {
        if (s.Collapsed)
            return (ShadowMargin, ShadowMargin, ChipSize, ChipSize);
        return (ShadowMargin, ShadowMargin, PanelWidth, PanelHeight(s));
    }

    /// <summary>
    /// Hit-tests a point over the whole window (shadow margin included). Returns null for points outside
    /// the rounded panel — the host maps those to HTTRANSPARENT — and for inside-panel zones that carry no
    /// action (the header grip, the hint line, the error line, the stats block); use <see cref="PanelRect"/>
    /// to distinguish "outside" from "inside but inert". Collapsed: every point of the chip hits (Chip,
    /// Whole). Expanded: the 24×24 button at the header's right corner hits CollapseButton; rows follow
    /// <see cref="Rows"/> in order with stepper rows exposing Left/Right zones on their 40-px edges; when
    /// the source list is open its rows sit between the Source row and the Overlay row.
    /// Hit-test một điểm trên TOÀN CẢNH window (kể cả viền shadow). Trả null cho điểm ngoài panel bo tròn
    /// — host ánh xạ chúng thành HTTRANSPARENT — và các vùng trong panel không có hành vi (dải grip header,
    /// dòng hint, dòng lỗi, vùng số thống kê); dùng PanelRect để phân biệt "ngoài" với "trong nhưng trơ".
    /// Thu gọn: mọi điểm của chip đều hit (Chip, Whole). Mở rộng: nút 24×24 góc phải header hit CollapseButton;
    /// các row theo thứ tự Rows() với row stepper có vùng Left/Right ở mép 40 px; khi danh sách nguồn mở thì
    /// các row của nó nằm giữa row Nguồn và row Lớp phủ.
    /// </summary>
    /// <param name="x">Point X in window coordinates (physical pixels). Toạ độ X điểm trong hệ window (pixel vật lý).</param>
    /// <param name="y">Point Y in window coordinates (physical pixels). Toạ độ Y điểm trong hệ window (pixel vật lý).</param>
    /// <param name="s">The menu state snapshot. Snapshot trạng thái menu.</param>
    /// <returns>The hit row and zone, or null when the point is outside the panel or on an inert zone. Row và vùng bị nhấn, hoặc null khi điểm ngoài panel hoặc ở vùng trơ.</returns>
    public static MenuHit? HitTest(int x, int y, MenuState s)
    {
        var (px, py, pw, ph) = PanelRect(s);
        if (!IsInsideRoundedRect(x, y, px, py, pw, ph)) return null;
        if (s.Collapsed) return new MenuHit(Chip, MenuZone.Whole);

        // Header: the left part is the drag grip (inert here — the host maps it to HTCAPTION), the
        // right 24×24 square is the collapse button.
        // Header: phần trái là dải grip kéo (trơ ở đây — host tự ánh xạ thành HTCAPTION), ô vuông
        // 24×24 bên phải là nút thu gọn.
        int headerBottom = py + HeaderHeight;
        if (y < headerBottom)
        {
            int btnX = px + pw - CollapseButtonSize;
            int btnY = py + (HeaderHeight - CollapseButtonSize) / 2;
            if (x >= btnX && x < btnX + CollapseButtonSize && y >= btnY && y < btnY + CollapseButtonSize)
                return new MenuHit(CollapseButton, MenuZone.Whole);
            return null;
        }

        int cursor = headerBottom;
        foreach (var (id, kind) in Rows(s))
        {
            // The hint line (and the error line when set) sit between the stats row and the close row.
            // Dòng hint (và dòng lỗi khi có) nằm giữa row thống kê và row đóng menu.
            if (id == RowCloseMenu)
            {
                cursor += HintRowHeight;
                if (s.ErrorLine is not null) cursor += ErrorRowHeight;
            }

            if (y >= cursor && y < cursor + RowHeight)
            {
                if (kind == "stepper")
                {
                    if (x < px + StepperZoneWidth) return new MenuHit(id, MenuZone.Left);
                    if (x >= px + pw - StepperZoneWidth) return new MenuHit(id, MenuZone.Right);
                }
                return new MenuHit(id, MenuZone.Whole);
            }

            cursor += RowHeight;
            if (id == RowStats && s.StatsOpen) cursor += StatsBlockHeight;
        }

        return null; // hint / error / stats-block zones: inside the panel, no action
    }

    /// <summary>
    /// Rows present in an expanded menu, in top-to-bottom order. The collapsed state has none. When the
    /// source list is open, one "sourceitem" row per source is inserted right after the Source row; the
    /// stats number block and the hint/error lines are not rows — they expand between the Stats row and
    /// the close row (see <see cref="HitTest"/>).
    /// Các row có mặt ở menu mở rộng, theo thứ tự từ trên xuống. Trạng thái thu gọn không có row nào.
    /// Khi danh sách nguồn mở, mỗi nguồn chèn một row "sourceitem" ngay sau row Nguồn; vùng số thống kê và
    /// dòng hint/lỗi không phải row — chúng nằm giữa row Thống kê và row đóng menu (xem HitTest).
    /// </summary>
    /// <param name="s">The menu state snapshot. Snapshot trạng thái menu.</param>
    /// <returns>(Id, Kind) per row; Kind is one of "startstop" | "source" | "overlay" | "cycle" | "stepper" | "switch" | "stats" | "close" | "sourceitem". (Id, Kind) mỗi row; Kind là một trong "startstop" | "source" | "overlay" | "cycle" | "stepper" | "switch" | "stats" | "close" | "sourceitem".</returns>
    public static IReadOnlyList<(int Id, string Kind)> Rows(MenuState s)
    {
        if (s.Collapsed) return Array.Empty<(int Id, string Kind)>();

        var rows = new List<(int Id, string Kind)>
        {
            (RowStartStop, "startstop"),
            (RowSource, "source"),
        };
        if (s.SourceListOpen)
            for (int i = 0; i < s.Sources.Count; i++)
                rows.Add((SourceItemBase + i, "sourceitem"));

        rows.AddRange(new[]
        {
            (RowOverlay, "overlay"),
            (RowDst, "cycle"),
            (RowSrc, "cycle"),
            (RowOcrFps, "stepper"),
            (RowCaptureFps, "stepper"),
            (RowMergeLines, "switch"),
            (RowStats, "stats"),
            (RowCloseMenu, "close"),
        });
        return rows;
    }

    /// <summary>
    /// Clamps an fps value into the 1..60 range shared by the steppers and the pipeline.
    /// Kẹp giá trị fps vào khoảng 1..60 dùng chung bởi stepper và pipeline.
    /// </summary>
    /// <param name="value">Requested fps. Giá trị fps yêu cầu.</param>
    /// <returns>The clamped value, 1..60. Giá trị đã kẹp, 1..60.</returns>
    public static int ClampFps(int value) => Math.Clamp(value, 1, 60);

    /// <summary>
    /// Index of the next language code in a cycle list: the neighbour after (forward) or before
    /// (backward) the current one, wrapping at both ends. An unknown or empty list yields index 0.
    /// Chỉ số ngôn ngữ tiếp theo trong danh sách cycle: phần tử sau (forward) hoặc trước (backward) mã
    /// hiện tại, vòng lại ở cả hai đầu. Mã không có trong danh sách hoặc danh sách rỗng cho về chỉ số 0.
    /// </summary>
    /// <param name="codes">The ordered language codes to cycle through. Danh sách mã ngôn ngữ theo thứ tự.</param>
    /// <param name="current">Current code (exact, ordinal match). Mã hiện tại (khớp đúng, ordinal).</param>
    /// <param name="forward">True for next, false for previous. True cho mã kế tiếp, false cho mã trước đó.</param>
    /// <returns>The index into <paramref name="codes"/> to select next. Chỉ số trong danh sách cần chọn tiếp.</returns>
    public static int CycleIndex(IReadOnlyList<string> codes, string current, bool forward)
    {
        if (codes.Count == 0) return 0;
        int index = -1;
        for (int i = 0; i < codes.Count; i++)
            if (string.Equals(codes[i], current, StringComparison.Ordinal)) { index = i; break; }
        if (index < 0) return 0;
        return forward ? (index + 1) % codes.Count : (index - 1 + codes.Count) % codes.Count;
    }

    /// <summary>
    /// Clamps a window position (top-left, shadow margin included) into the work area of the monitor that
    /// contains the anchor point. With no containing monitor the first work area is used; with an empty
    /// list the fallback area (0,0)..(1920,1080) is used. A window wider or taller than the area pins to
    /// its top-left corner instead of throwing.
    /// Kẹp vị trí window (góc trên trái, kể cả viền shadow) vào work area của monitor CHỨA điểm neo. Khi
    /// không có monitor nào chứa thì dùng work area đầu tiên; danh sách rỗng thì dùng vùng dự phòng
    /// (0,0)..(1920,1080). Window rộng hoặc cao hơn vùng thì ghim vào góc trên trái thay vì ném exception.
    /// </summary>
    /// <param name="x">Desired window X (anchor point). Toạ độ X mong muốn của window (điểm neo).</param>
    /// <param name="y">Desired window Y (anchor point). Toạ độ Y mong muốn của window (điểm neo).</param>
    /// <param name="windowW">Window width including the shadow margin. Rộng window kể cả viền shadow.</param>
    /// <param name="windowH">Window height including the shadow margin. Cao window kể cả viền shadow.</param>
    /// <param name="workAreas">Monitor work areas as (Left, Top, Right, Bottom). Các work area monitor dạng (Trái, Trên, Phải, Dưới).</param>
    /// <returns>The clamped top-left position. Vị trí góc trên trái đã kẹp.</returns>
    public static (int X, int Y) ClampPosition(int x, int y, int windowW, int windowH,
        IReadOnlyList<(int Left, int Top, int Right, int Bottom)> workAreas)
    {
        var area = FallbackWorkArea;
        if (workAreas.Count > 0)
        {
            area = workAreas[0];
            foreach (var a in workAreas)
                if (x >= a.Left && x < a.Right && y >= a.Top && y < a.Bottom) { area = a; break; }
        }

        int maxX = Math.Max(area.Left, area.Right - windowW);
        int maxY = Math.Max(area.Top, area.Bottom - windowH);
        return (Math.Clamp(x, area.Left, maxX), Math.Clamp(y, area.Top, maxY));
    }

    /// <summary>
    /// Builds the capture-source list: every monitor first (label "Màn hình {Id+1}"), then up to
    /// MaxSourceWindows windows in stable input order (label "{Process} — {Title}", truncated to 40
    /// characters plus an ellipsis without cutting between surrogate halves).
    /// Dựng danh sách nguồn capture: mọi màn hình trước (label "Màn hình {Id+1}"), sau đó tối đa
    /// MaxSourceWindows cửa sổ theo thứ tự đầu vào ổn định (label "{Process} — {Title}", cắt 40 ký tự kèm
    /// dấu ba chấm mà không cắt giữa surrogate pair).
    /// </summary>
    /// <param name="monitors">Available monitors, in display order. Các màn hình có sẵn, theo thứ tự hiển thị.</param>
    /// <param name="windows">Available windows, in the order reported by the capture list. Các cửa sổ có sẵn, theo thứ tự danh sách capture trả về.</param>
    /// <returns>The source items, monitors first then windows. Các mục nguồn, màn hình trước rồi cửa sổ.</returns>
    public static IReadOnlyList<SourceItem> BuildSources(IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<WindowInfo> windows)
    {
        var items = new List<SourceItem>(monitors.Count + Math.Min(windows.Count, MaxSourceWindows));
        foreach (var m in monitors)
            items.Add(new SourceItem(true, m.Id, $"Màn hình {m.Id + 1}", null));

        int windowsTaken = 0;
        foreach (var w in windows)
        {
            if (windowsTaken >= MaxSourceWindows) break;
            windowsTaken++;
            items.Add(new SourceItem(false, -1, TruncateLabel($"{w.Process} — {w.Title}"), w.Hwnd));
        }
        return items;
    }

    /// <summary>
    /// Default first-run position: top-right corner of the primary work area, 16 px from its edges.
    /// Vị trí mặc định lần đầu: góc trên phải của work area chính, cách mép 16 px.
    /// </summary>
    /// <param name="windowW">Window width including the shadow margin. Rộng window kể cả viền shadow.</param>
    /// <param name="windowH">Window height including the shadow margin. Cao window kể cả viền shadow.</param>
    /// <param name="primaryWorkArea">Primary monitor work area as (Left, Top, Right, Bottom). Work area màn hình chính dạng (Trái, Trên, Phải, Dưới).</param>
    /// <returns>The top-left position to place the window. Vị trí góc trên trái để đặt window.</returns>
    public static (int X, int Y) DefaultPosition(int windowW, int windowH,
        (int Left, int Top, int Right, int Bottom) primaryWorkArea)
        => (primaryWorkArea.Right - EdgeMargin - windowW, primaryWorkArea.Top + EdgeMargin);

    /// <summary>Panel height for an expanded state: header + rows + hint line, plus the stats block and error line when present. Chiều cao panel ở trạng thái mở rộng: header + rows + dòng hint, cộng vùng số thống kê và dòng lỗi khi có.</summary>
    private static int PanelHeight(MenuState s) =>
        HeaderHeight + Rows(s).Count * RowHeight + HintRowHeight
        + (s.StatsOpen ? StatsBlockHeight : 0)
        + (s.ErrorLine is not null ? ErrorRowHeight : 0);

    /// <summary>
    /// Whether a point lies inside a rectangle with CornerRadius-rounded corners: the four corner squares
    /// exclude the area beyond each arc. Điểm có nằm trong hình chữ nhật bo góc CornerRadius không: bốn ô
    /// vuông góc loại bỏ phần ngoài mỗi cung tròn.
    /// </summary>
    private static bool IsInsideRoundedRect(int x, int y, int px, int py, int pw, int ph)
    {
        if (x < px || x >= px + pw || y < py || y >= py + ph) return false;
        int r = Math.Min(CornerRadius, Math.Min(pw, ph) / 2);
        if (r <= 0) return true;

        // The corner square the point falls in; none means straight edges → inside.
        // Ô vuông góc mà điểm nằm trong; không có nghĩa là cạnh thẳng → bên trong.
        int dx = -1, dy = -1;
        if (x < px + r && y < py + r) { dx = px + r - x; dy = py + r - y; }
        else if (x >= px + pw - r && y < py + r) { dx = x - (px + pw - r); dy = py + r - y; }
        else if (x < px + r && y >= py + ph - r) { dx = px + r - x; dy = y - (py + ph - r); }
        else if (x >= px + pw - r && y >= py + ph - r) { dx = x - (px + pw - r); dy = y - (py + ph - r); }

        return dx < 0 || dx * dx + dy * dy <= r * r;
    }

    /// <summary>Maximum source-list label length including the ellipsis. Độ dài tối đa label danh sách nguồn kể cả dấu ba chấm.</summary>
    private const int MaxLabelLength = 40;

    /// <summary>Truncates a window label to MaxLabelLength characters, appending an ellipsis when cut. Cắt label cửa sổ về MaxLabelLength ký tự, thêm dấu ba chấm khi bị cắt.</summary>
    private static string TruncateLabel(string label) =>
        label.Length <= MaxLabelLength ? label : Prefix(label, MaxLabelLength - 1) + "…";

    /// <summary>
    /// Same prefix pattern as BoxAnnotationStyle.Prefix: backs off one character when the cut would land
    /// between a surrogate pair, then trims trailing spaces.
    /// Cùng pattern prefix như BoxAnnotationStyle.Prefix: lùi một ký tự khi điểm cắt rơi giữa surrogate
    /// pair, rồi bỏ khoảng trắng cuối.
    /// </summary>
    private static string Prefix(string text, int length)
    {
        int cut = Math.Min(length, text.Length);
        if (cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd();
    }
}
