using System.Globalization;
using SD = System.Drawing;
using ReTran_App.Services;
using ReTran_App.Services.OverlayWindow;

namespace ReTran_App.Services.FloatingMenu;

/// <summary>
/// GDI+ drawing of the floating menu: the expanded panel (offset shadow, rounded dark fill with a
/// border, header with grip dots and collapse button, one row per <see cref="FloatingMenuLayout.Rows"/>
/// entry, optional stats block, hint/error lines) or the collapsed chip.
/// Vẽ GDI+ của menu nổi: panel mở rộng (shadow lệch, nền tối bo góc có viền, header với chấm grip và
/// nút thu gọn, một row cho mỗi mục của Rows(), vùng thống kê tuỳ chọn, dòng hint/lỗi) hoặc chip khi
/// thu gọn.
/// </summary>
/// <remarks>
/// All structural geometry comes from <see cref="FloatingMenuLayout"/> (panel rect, header/row/hint/
/// error/stats heights, collapse button and stepper zones, corner radius) and all typography from
/// <see cref="BoxAnnotationStyle"/> (colors, cached fonts, text format, rounded-rect path builder);
/// this type only adds drawing-only paddings and control sizes.
/// Mọi hình học cấu trúc lấy từ FloatingMenuLayout (rect panel, chiều cao header/row/hint/lỗi/thống kê,
/// nút thu gọn và vùng stepper, bán kính góc) và mọi kiểu chữ từ BoxAnnotationStyle (màu, font cache,
/// format chữ, builder path bo góc); class này chỉ thêm đệm vẽ và kích thước control.
/// </remarks>
public static class FloatingMenuRenderer
{
    /// <summary>Shadow offset behind the panel (pixels). Độ lệch shadow phía sau panel (pixel).</summary>
    private const int ShadowOffset = 3;

    /// <summary>Horizontal padding of row labels/values from the panel edge (pixels). Đệm ngang nhãn/giá trị cách mép panel (pixel).</summary>
    private const int RowPadX = 16;

    /// <summary>Row label/value font size (pixels). Cỡ chữ nhãn/giá trị row (pixel).</summary>
    private const int LabelSize = 14;

    /// <summary>Secondary text font size: source items, stepper glyphs, stats lines (pixels). Cỡ chữ phụ: mục nguồn, glyph stepper, dòng thống kê (pixel).</summary>
    private const int SmallSize = 13;

    /// <summary>Hint/error line font size (pixels). Cỡ chữ dòng hint/lỗi (pixel).</summary>
    private const int TinySize = 11;

    /// <summary>Collapsed chip glyph font size (pixels). Cỡ chữ glyph chip thu gọn (pixel).</summary>
    private const int ChipGlyphSize = 18;

    /// <summary>Start/Stop button width (pixels). Rộng nút Start/Stop (pixel).</summary>
    private const float ButtonWidth = 64f;

    /// <summary>Start/Stop button height (pixels). Cao nút Start/Stop (pixel).</summary>
    private const float ButtonHeight = 28f;

    /// <summary>Switch track width (pixels). Rộng track switch (pixel).</summary>
    private const float SwitchWidth = 40f;

    /// <summary>Switch track height (pixels). Cao track switch (pixel).</summary>
    private const float SwitchHeight = 22f;

    /// <summary>Stepper zone button size, inside the layout's press zones (pixels). Kích thước nút vùng stepper, nằm trong vùng nhấn của layout (pixel).</summary>
    private const float ZoneButtonSize = 28f;

    /// <summary>Grip dot diameter in the header (pixels). Đường kính chấm grip trong header (pixel).</summary>
    private const float GripDotSize = 4f;

    /// <summary>Edge-to-edge pitch of the grip dots (pixels); the visible gap is the pitch minus the dot size. Pitch cạnh-đến-cạnh của các chấm grip (pixel); khe nhìn thấy bằng pitch trừ cỡ chấm.</summary>
    private const float GripDotPitch = 8f;

    /// <summary>Dimmed text: TextColor at ~50% alpha. Chữ mờ: TextColor alpha ~50%.</summary>
    private static readonly SD.Color DimColor = SD.Color.FromArgb(130, BoxAnnotationStyle.TextColor.R, BoxAnnotationStyle.TextColor.G, BoxAnnotationStyle.TextColor.B);

    /// <summary>Error line color rgba(240,90,90,1). Màu dòng lỗi.</summary>
    private static readonly SD.Color ErrorColor = SD.Color.FromArgb(255, 240, 90, 90);

    /// <summary>Shadow color rgba(0,0,0,.35). Màu shadow.</summary>
    private static readonly SD.Color ShadowColor = SD.Color.FromArgb(90, 0, 0, 0);

    /// <summary>Fill of the stepper zone buttons and the off switch track. Nền nút vùng stepper và track switch tắt.</summary>
    private static readonly SD.Color ZoneFill = SD.Color.FromArgb(255, 28, 39, 44);

    /// <summary>Dark knob color on the "on" switch track. Màu núm tối trên track switch đang bật.</summary>
    private static readonly SD.Color KnobOnColor = SD.Color.FromArgb(255, 8, 16, 18);

    /// <summary>
    /// Draws the menu (expanded panel or collapsed chip) onto an existing surface in WINDOW
    /// coordinates — that is, including the shadow margin around the panel.
    /// Vẽ menu (panel mở rộng hoặc chip thu gọn) lên surface có sẵn theo toạ độ WINDOW — tức là kể cả
    /// viền shadow quanh panel.
    /// </summary>
    /// <param name="g">Target graphics surface; antialiasing and the text hint are set by the caller. Surface đồ hoạ đích; khử răng cưa và kiểu chữ do người gọi đặt.</param>
    /// <param name="s">Menu state snapshot to draw. Snapshot trạng thái menu cần vẽ.</param>
    /// <param name="windowW">Window width in pixels (geometry is owned by the layout, not used here). Rộng window theo pixel (hình học do layout sở hữu, không dùng ở đây).</param>
    /// <param name="windowH">Window height in pixels (geometry is owned by the layout, not used here). Cao window theo pixel (hình học do layout sở hữu, không dùng ở đây).</param>
    /// <param name="l">
    /// Optional localized string table for the row/stats/hint labels; null keeps the original
    /// hardcoded values so legacy call sites render exactly as before.
    /// Bảng chuỗi đã dịch tuỳ chọn cho nhãn row/thống kê/hint; null giữ nguyên giá trị hardcode gốc để
    /// các caller cũ vẽ y như trước.
    /// </param>
    internal static void DrawMenu(SD.Graphics g, MenuState s, int windowW, int windowH, I18nService? l = null)
    {
        MenuStrings t = Resolve(l);
        using var format = BoxAnnotationStyle.CreateTextFormat();
        using var panelBrush = new SD.SolidBrush(BoxAnnotationStyle.PillFill);
        using var borderPen = new SD.Pen(BoxAnnotationStyle.PillBorder, 1f);
        using var textBrush = new SD.SolidBrush(BoxAnnotationStyle.TextColor);
        using var dimBrush = new SD.SolidBrush(DimColor);
        using var tickBrush = new SD.SolidBrush(BoxAnnotationStyle.TickColor);
        using var errorBrush = new SD.SolidBrush(ErrorColor);
        using var shadowBrush = new SD.SolidBrush(ShadowColor);
        using var path = new SD.Drawing2D.GraphicsPath();

        if (s.Collapsed)
        {
            DrawChip(g, s, format, panelBrush, borderPen, tickBrush, path);
            return;
        }

        var (px, py, pw, ph) = FloatingMenuLayout.PanelRect(s);

        // Offset shadow behind the panel, then the panel itself (same rounded rect, no offset).
        // Shadow lệch phía sau panel, rồi chính panel (cùng rect bo góc, không lệch).
        BoxAnnotationStyle.BuildRoundedRect(path, new SD.RectangleF(px + ShadowOffset, py + ShadowOffset, pw, ph), FloatingMenuLayout.CornerRadius);
        g.FillPath(shadowBrush, path);

        BoxAnnotationStyle.BuildRoundedRect(path, new SD.RectangleF(px, py, pw, ph), FloatingMenuLayout.CornerRadius);
        g.FillPath(panelBrush, path);
        g.DrawPath(borderPen, path);

        DrawHeader(g, px, py, pw, borderPen, textBrush, dimBrush, path);

        int cursor = py + FloatingMenuLayout.HeaderHeight;
        foreach (var (id, kind) in FloatingMenuLayout.Rows(s))
        {
            // The hint line (and the error line when set) sit between the stats row and the close row —
            // same cursor walk as FloatingMenuLayout.HitTest.
            // Dòng hint (và dòng lỗi khi có) nằm giữa row thống kê và row đóng menu — cùng bước đi con trỏ
            // với HitTest của layout.
            if (id == FloatingMenuLayout.RowCloseMenu)
            {
                DrawLine(g, t.NextStartHint, TinySize, px, pw, cursor, FloatingMenuLayout.HintRowHeight, dimBrush, format);
                cursor += FloatingMenuLayout.HintRowHeight;

                if (s.ErrorLine is not null)
                {
                    DrawLine(g, s.ErrorLine, TinySize, px, pw, cursor, FloatingMenuLayout.ErrorRowHeight, errorBrush, format);
                    cursor += FloatingMenuLayout.ErrorRowHeight;
                }
            }

            DrawRow(g, id, kind, px, pw, cursor, s, t, format, textBrush, dimBrush, tickBrush, borderPen, path);
            cursor += FloatingMenuLayout.RowHeight;

            if (id == FloatingMenuLayout.RowStats && s.StatsOpen)
            {
                DrawStatsBlock(g, px, pw, cursor, s, t, format, textBrush, dimBrush);
                cursor += FloatingMenuLayout.StatsBlockHeight;
            }
        }
    }

    /// <summary>
    /// Every user-facing string the floating menu draws, resolved once per draw: the eleven row labels
    /// (menu.row.* — Start/Stop feed the button), the three dedicated stats-block labels (menu.stats.*)
    /// whose "source"/"dst" lines reuse the Source/Dst row labels, and the hint line
    /// (menu.note.nextStart).
    /// Mọi chuỗi hướng người dùng mà menu nổi vẽ, phân giải một lần mỗi lần vẽ: mười một nhãn row
    /// (menu.row.* — Start/Stop nuôi nút), ba nhãn riêng của vùng thống kê (menu.stats.*) trong đó hai
    /// dòng "nguồn"/"dịch sang" tái dùng nhãn row Nguồn/Dịch sang, và dòng hint (menu.note.nextStart).
    /// </summary>
    internal sealed record MenuStrings(
        string Start,
        string Stop,
        string Source,
        string Overlay,
        string Dst,
        string Src,
        string OcrFps,
        string CaptureFps,
        string MergeLines,
        string Stats,
        string CloseMenu,
        string NextStartHint,
        string StatFps,
        string StatBoxes,
        string StatLines);

    /// <summary>
    /// Resolves the menu strings for one draw: with a table every label comes from its i18n key; with
    /// null the exact values the renderer hardcoded before the i18n wiring are returned verbatim, so
    /// legacy four-argument call sites render unchanged (the Start/Stop button keeps its "Start"/"Stop"
    /// words).
    /// Phân giải chuỗi menu cho một lần vẽ: có bảng thì mọi nhãn lấy từ key i18n; null trả về nguyên giá
    /// trị renderer hardcode trước khi nối i18n, để các caller 4 tham số cũ vẽ không đổi (nút Start/Stop
    /// giữ nguyên chữ "Start"/"Stop").
    /// </summary>
    /// <param name="l">Localized string table in the active language, or null for the original hardcoded values. Bảng chuỗi đã dịch theo ngôn ngữ hiện tại, hoặc null để dùng giá trị hardcode gốc.</param>
    /// <returns>The resolved strings for every label the renderer draws. Chuỗi đã phân giải cho mọi nhãn mà renderer vẽ.</returns>
    internal static MenuStrings Resolve(I18nService? l)
    {
        if (l is null)
            return new MenuStrings(
                Start: "Start", Stop: "Stop",
                Source: "Nguồn", Overlay: "Lớp phủ", Dst: "Dịch sang", Src: "Ngôn ngữ nguồn",
                OcrFps: "Nhịp OCR", CaptureFps: "Nhịp chụp", MergeLines: "Gộp nhiều dòng",
                Stats: "Thống kê", CloseMenu: "Tắt menu nổi",
                NextStartHint: "Áp dụng ở lần Start kế tiếp",
                StatFps: "FPS", StatBoxes: "Box", StatLines: "Dòng dịch");

        return new MenuStrings(
            Start: l["menu.row.start"], Stop: l["menu.row.stop"],
            Source: l["menu.row.source"], Overlay: l["menu.row.overlay"], Dst: l["menu.row.dst"], Src: l["menu.row.src"],
            OcrFps: l["menu.row.ocrFps"], CaptureFps: l["menu.row.captureFps"], MergeLines: l["menu.row.mergeLines"],
            Stats: l["menu.row.stats"], CloseMenu: l["menu.row.closeMenu"],
            NextStartHint: l["menu.note.nextStart"],
            StatFps: l["menu.stats.fps"], StatBoxes: l["menu.stats.boxes"], StatLines: l["menu.stats.lines"]);
    }

    /// <summary>Collapsed chip: rounded background plus the "R" glyph centred in it. Chip thu gọn: nền bo góc kèm glyph "R" ở giữa.</summary>
    private static void DrawChip(SD.Graphics g, MenuState s, SD.StringFormat format, SD.Brush panelBrush,
        SD.Pen borderPen, SD.Brush tickBrush, SD.Drawing2D.GraphicsPath path)
    {
        var (px, py, pw, ph) = FloatingMenuLayout.PanelRect(s);

        BoxAnnotationStyle.BuildRoundedRect(path, new SD.RectangleF(px, py, pw, ph), FloatingMenuLayout.CornerRadius);
        g.FillPath(panelBrush, path);
        g.DrawPath(borderPen, path);

        SD.Font font = BoxAnnotationStyle.GetFont(ChipGlyphSize);
        SD.SizeF size = g.MeasureString("R", font, new SD.PointF(0f, 0f), format);
        g.DrawString("R", font, tickBrush, px + (pw - size.Width) / 2f, py + (ph - size.Height) / 2f, format);
    }

    /// <summary>Header: three grip dots on the left, the collapse button ("—" icon) at the right corner. Header: ba chấm grip bên trái, nút thu gọn (icon "—") ở góc phải.</summary>
    private static void DrawHeader(SD.Graphics g, int px, int py, int pw, SD.Pen borderPen,
        SD.Brush textBrush, SD.Brush dimBrush, SD.Drawing2D.GraphicsPath path)
    {
        float midY = py + FloatingMenuLayout.HeaderHeight / 2f;

        // The left strip is the drag handle (inert in hit-test — the host maps it to HTCAPTION).
        // Dải bên trái là tay kéo (trơ trong hit-test — host tự ánh xạ thành HTCAPTION).
        for (int i = 0; i < 3; i++)
            g.FillEllipse(dimBrush, px + RowPadX + i * GripDotPitch, midY - GripDotSize / 2f, GripDotSize, GripDotSize);

        int btnX = px + pw - FloatingMenuLayout.CollapseButtonSize;
        int btnY = py + (FloatingMenuLayout.HeaderHeight - FloatingMenuLayout.CollapseButtonSize) / 2;

        BoxAnnotationStyle.BuildRoundedRect(path, new SD.RectangleF(btnX + 1f, btnY + 1f,
            FloatingMenuLayout.CollapseButtonSize - 2f, FloatingMenuLayout.CollapseButtonSize - 2f), 6f);
        g.DrawPath(borderPen, path);

        using var dashPen = new SD.Pen(BoxAnnotationStyle.TextColor, 2f)
        {
            StartCap = SD.Drawing2D.LineCap.Square,
            EndCap = SD.Drawing2D.LineCap.Square,
        };
        float dashY = btnY + FloatingMenuLayout.CollapseButtonSize / 2f;
        g.DrawLine(dashPen, btnX + 7f, dashY, btnX + FloatingMenuLayout.CollapseButtonSize - 7f, dashY);
    }

    /// <summary>One expanded-menu row: label left, value right, per the row kind. Một row menu mở rộng: nhãn trái, giá trị phải, theo loại row.</summary>
    private static void DrawRow(SD.Graphics g, int id, string kind, int px, int pw, int y, MenuState s,
        MenuStrings t, SD.StringFormat format, SD.Brush textBrush, SD.Brush dimBrush, SD.Brush tickBrush,
        SD.Pen borderPen, SD.Drawing2D.GraphicsPath path)
    {
        float midY = y + FloatingMenuLayout.RowHeight / 2f;

        switch (kind)
        {
            case "startstop":
                // "Pipeline" has no i18n key in the pinned table — stays hardcoded.
                // "Pipeline" không có key i18n trong bảng ghim — giữ hardcode.
                DrawLabel(g, "Pipeline", px, midY, format, textBrush);
                DrawStartStopButton(g, s.Running, s.Running ? t.Stop : t.Start, px + pw - RowPadX, midY, format, path);
                break;

            case "source":
            {
                string label = t.Source;
                float labelW = MeasureWidth(g, label, LabelSize, format);
                DrawLabel(g, label, px, midY, format, textBrush);
                float maxW = pw - 2f * RowPadX - labelW - 8f;
                string value = BoxAnnotationStyle.TruncateToFit(g, s.SourceLabel, SmallSize,
                    MathF.Max(1f, maxW), FloatingMenuLayout.RowHeight, format, 1);
                DrawRightAligned(g, value, SmallSize, px + pw - RowPadX, midY, format, textBrush);
                break;
            }

            case "sourceitem":
                DrawSourceItem(g, id, px, pw, midY, s, format, textBrush, dimBrush, tickBrush);
                break;

            case "overlay":
                // Disabled while the pipeline is not running (40% alpha in DrawSwitch).
                // Disabled khi pipeline chưa chạy (alpha 40% trong DrawSwitch).
                DrawLabel(g, t.Overlay, px, midY, format, textBrush);
                DrawSwitch(g, px + pw - RowPadX, midY, s.OverlayOn, s.Running);
                break;

            case "cycle":
            {
                bool dst = id == FloatingMenuLayout.RowDst;
                DrawLabel(g, dst ? t.Dst : t.Src, px, midY, format, textBrush);
                string code = (dst ? s.Dst : s.Src).ToUpperInvariant();
                DrawRightAligned(g, code, LabelSize, px + pw - RowPadX, midY, format, textBrush);
                break;
            }

            case "stepper":
            {
                bool ocr = id == FloatingMenuLayout.RowOcrFps;
                DrawStepper(g, ocr ? t.OcrFps : t.CaptureFps, ocr ? s.OcrFps : s.CaptureFps,
                    px, pw, midY, format, textBrush);
                break;
            }

            case "switch":
                DrawLabel(g, t.MergeLines, px, midY, format, textBrush);
                DrawSwitch(g, px + pw - RowPadX, midY, s.MergeLines, true);
                break;

            case "stats":
                DrawLabel(g, t.Stats, px, midY, format, textBrush);
                DrawRightAligned(g, s.StatsOpen ? "▾" : "▸", SmallSize, px + pw - RowPadX, midY, format, dimBrush);
                break;

            case "close":
                DrawLabel(g, t.CloseMenu, px, midY, format, textBrush);
                break;
        }
    }

    /// <summary>Source-list row: check on the selected item, then the label. Row danh sách nguồn: dấu check ở mục đang chọn, rồi label.</summary>
    private static void DrawSourceItem(SD.Graphics g, int id, int px, int pw, float midY, MenuState s,
        SD.StringFormat format, SD.Brush textBrush, SD.Brush dimBrush, SD.Brush tickBrush)
    {
        int index = id - FloatingMenuLayout.SourceItemBase;
        if (index < 0 || index >= s.Sources.Count) return;
        SourceItem item = s.Sources[index];

        bool selected = string.Equals(item.Label, s.SourceLabel, StringComparison.Ordinal);
        float x = px + RowPadX;
        if (selected)
        {
            DrawTextAt(g, "✓", SmallSize, x, midY, format, tickBrush);
            x += 18f; // room for the check glyph. Chỗ cho glyph check.
        }

        float maxW = px + pw - RowPadX - x;
        string value = BoxAnnotationStyle.TruncateToFit(g, item.Label, SmallSize,
            MathF.Max(1f, maxW), FloatingMenuLayout.RowHeight, format, 1);
        DrawTextAt(g, value, SmallSize, x, midY, format, selected ? textBrush : dimBrush);
    }

    /// <summary>
    /// Start/Stop button at the row's right edge: accent outline per pipeline state and the resolved
    /// label centred in it.
    /// Nút Start/Stop ở mép phải row: viền màu nhấn theo trạng thái pipeline và nhãn đã phân giải căn
    /// giữa trong nút.
    /// </summary>
    private static void DrawStartStopButton(SD.Graphics g, bool running, string text, float rightX, float midY,
        SD.StringFormat format, SD.Drawing2D.GraphicsPath path)
    {
        SD.Color accent = running ? ErrorColor : BoxAnnotationStyle.TickColor;
        using var brush = new SD.SolidBrush(accent);
        using var pen = new SD.Pen(accent, 1f);

        BoxAnnotationStyle.BuildRoundedRect(path, new SD.RectangleF(rightX - ButtonWidth, midY - ButtonHeight / 2f,
            ButtonWidth, ButtonHeight), 8f);
        g.DrawPath(pen, path);

        SD.Font font = BoxAnnotationStyle.GetFont(SmallSize);
        SD.SizeF size = g.MeasureString(text, font, new SD.PointF(0f, 0f), format);
        g.DrawString(text, font, brush, rightX - ButtonWidth + (ButtonWidth - size.Width) / 2f, midY - size.Height / 2f, format);
    }

    /// <summary>
    /// Switch pill at the row's right edge: on = TickColor track with a dark knob, off = dim track
    /// with a border and a light knob; disabled draws everything at 40% alpha.
    /// Pill switch ở mép phải row: bật = track màu TickColor kèm núm tối, tắt = track mờ có viền kèm
    /// núm sáng; disabled vẽ mọi thứ với alpha 40%.
    /// </summary>
    private static void DrawSwitch(SD.Graphics g, float rightX, float midY, bool on, bool enabled)
    {
        float a = enabled ? 1f : 0.4f;
        float x = rightX - SwitchWidth, y = midY - SwitchHeight / 2f;

        using var trackPath = new SD.Drawing2D.GraphicsPath();
        BoxAnnotationStyle.BuildRoundedRect(trackPath, new SD.RectangleF(x, y, SwitchWidth, SwitchHeight), SwitchHeight / 2f);

        if (on)
        {
            using var fill = new SD.SolidBrush(Alpha(BoxAnnotationStyle.TickColor, a));
            g.FillPath(fill, trackPath);
        }
        else
        {
            using var fill = new SD.SolidBrush(Alpha(ZoneFill, a));
            using var pen = new SD.Pen(Alpha(BoxAnnotationStyle.PillBorder, a), 1f);
            g.FillPath(fill, trackPath);
            g.DrawPath(pen, trackPath);
        }

        float knob = SwitchHeight - 8f;
        float kx = on ? x + SwitchWidth - 4f - knob : x + 4f;
        using var knobBrush = new SD.SolidBrush(on ? Alpha(KnobOnColor, a) : Alpha(BoxAnnotationStyle.TextColor, 0.6f * a));
        g.FillEllipse(knobBrush, kx, y + 4f, knob, knob);
    }

    /// <summary>
    /// Stepper row: "−" and "+" buttons centred in the layout's left/right press zones (visually
    /// distinct filled squares), label right of the left zone, value just before the right zone.
    /// Row stepper: nút "−" và "+" ở giữa hai vùng nhấn trái/phải của layout (ô vuông có nền, phân biệt
    /// được rõ), nhãn sau vùng trái, giá trị sát trước vùng phải.
    /// </summary>
    private static void DrawStepper(SD.Graphics g, string label, int value, int px, int pw, float midY,
        SD.StringFormat format, SD.Brush textBrush)
    {
        DrawZoneButton(g, "−", px + FloatingMenuLayout.StepperZoneWidth / 2f, midY, format, textBrush);
        DrawZoneButton(g, "+", px + pw - FloatingMenuLayout.StepperZoneWidth / 2f, midY, format, textBrush);

        DrawTextAt(g, label, LabelSize, px + FloatingMenuLayout.StepperZoneWidth + 8f, midY, format, textBrush);
        DrawRightAligned(g, value.ToString(), LabelSize, px + pw - FloatingMenuLayout.StepperZoneWidth - 12f, midY, format, textBrush);
    }

    /// <summary>Filled rounded square with a centred glyph — one stepper press zone. Ô vuông bo góc có nền với glyph ở giữa — một vùng nhấn stepper.</summary>
    private static void DrawZoneButton(SD.Graphics g, string glyph, float centerX, float midY,
        SD.StringFormat format, SD.Brush brush)
    {
        using var path = new SD.Drawing2D.GraphicsPath();
        BoxAnnotationStyle.BuildRoundedRect(path, new SD.RectangleF(centerX - ZoneButtonSize / 2f, midY - ZoneButtonSize / 2f,
            ZoneButtonSize, ZoneButtonSize), 7f);
        using var fill = new SD.SolidBrush(ZoneFill);
        g.FillPath(fill, path);

        DrawTextAt(g, glyph, SmallSize, centerX - MeasureWidth(g, glyph, SmallSize, format) / 2f, midY, format, brush);
    }

    /// <summary>
    /// Stats block: five 13-px LIVE lines per spec §7.3 — fps, current box count, translated-line
    /// count, the source label and the language pair (SRC → DST uppercased). Labels come from the
    /// resolved <see cref="MenuStrings"/> (menu.stats.* plus the Source/Dst row labels).
    /// Vùng thống kê: năm dòng TRỰC TIẾP 13 px theo spec §7.3 — fps, số box hiện tại, số dòng đã dịch,
    /// nhãn nguồn và cặp ngôn ngữ (SRC → DST hoa). Nhãn lấy từ MenuStrings đã phân giải (menu.stats.*
    /// cộng nhãn row Nguồn/Dịch sang).
    /// </summary>
    private static void DrawStatsBlock(SD.Graphics g, int px, int pw, int y, MenuState s, MenuStrings t,
        SD.StringFormat format, SD.Brush textBrush, SD.Brush dimBrush)
    {
        string[] labels = [t.StatFps, t.StatBoxes, t.StatLines, t.Source, t.Dst];
        string[] values =
        [
            s.Fps.ToString("0.#", CultureInfo.InvariantCulture),
            s.BoxCount.ToString(CultureInfo.InvariantCulture),
            s.TranslatedLineCount.ToString(CultureInfo.InvariantCulture),
            s.SourceLabel,
            $"{s.Src.ToUpperInvariant()} → {s.Dst.ToUpperInvariant()}",
        ];

        // Pitch derived from the block height (≈18.4 px for 5 lines): a 13-px line measures ≈18.9 px tall,
        // so a fixed 16-px pitch made adjacent lines overlap; this fills the block exactly without overlap.
        // Pitch suy ra từ chiều cao vùng (≈18,4 px cho 5 dòng): dòng 13 px đo cao ≈18,9 px nên pitch cố định
        // 16 px làm các dòng đè nhau; cách này lấp đúng vùng mà không trùng lặp.
        float lineH = FloatingMenuLayout.StatsBlockHeight / labels.Length;
        for (int i = 0; i < labels.Length; i++)
        {
            float lineY = y + (i + 0.5f) * lineH;
            DrawTextAt(g, labels[i], SmallSize, px + RowPadX, lineY, format, dimBrush);

            // Values are right-aligned and truncated to the column left of them so a long source label
            // never collides with the label column (numbers and the pair always fit).
            // Giá trị căn phải và cắt vừa cột bên trái để nhãn nguồn dài không bao giờ đụng cột nhãn
            // (số và cặp ngôn ngữ luôn vừa).
            float labelW = MeasureWidth(g, labels[i], SmallSize, format);
            string value = BoxAnnotationStyle.TruncateToFit(g, values[i], SmallSize,
                MathF.Max(1f, pw - 2f * RowPadX - labelW - 8f), FloatingMenuLayout.RowHeight, format, 1);
            DrawRightAligned(g, value, SmallSize, px + pw - RowPadX, lineY, format, textBrush);
        }
    }

    /// <summary>
    /// Dimmed or error status line (hint/error row), truncated to the panel width. The row is a single
    /// text line, so the fit height is compensated by TruncateToFit's vertical padding — otherwise the
    /// height rule (line + 2·PadY ≤ innerHeight) rejects every candidate and the row renders as a lone "…".
    /// Dòng trạng thái mờ hoặc lỗi (row hint/lỗi), cắt vừa rộng panel. Row là một dòng chữ duy nhất nên chiều
    /// cao fit được bù bằng đệm dọc của TruncateToFit — nếu không, luật chiều cao (dòng + 2·PadY ≤ innerHeight)
    /// từ chối mọi ứng viên và row chỉ còn dấu "…" đơn lẻ.
    /// </summary>
    private static void DrawLine(SD.Graphics g, string text, int size, int px, int pw, int y, int h,
        SD.Brush brush, SD.StringFormat format)
    {
        float innerH = h + 2f * BoxAnnotationStyle.PadY(size);
        string value = BoxAnnotationStyle.TruncateToFit(g, text, size, MathF.Max(1f, pw - 2f * RowPadX), innerH, format, 1);
        DrawTextAt(g, value, size, px + RowPadX, y + h / 2f, format, brush);
    }

    /// <summary>Row label at the left edge, vertically centred. Nhãn row ở mép trái, căn giữa dọc.</summary>
    private static void DrawLabel(SD.Graphics g, string text, int px, float midY, SD.StringFormat format, SD.Brush brush) =>
        DrawTextAt(g, text, LabelSize, px + RowPadX, midY, format, brush);

    /// <summary>Text left-anchored at x, vertically centred on midY. Chữ neo trái tại x, căn giữa dọc theo midY.</summary>
    private static void DrawTextAt(SD.Graphics g, string text, int size, float x, float midY, SD.StringFormat format, SD.Brush brush)
    {
        SD.Font font = BoxAnnotationStyle.GetFont(size);
        SD.SizeF s = g.MeasureString(text, font, new SD.PointF(0f, 0f), format);
        g.DrawString(text, font, brush, x, midY - s.Height / 2f, format);
    }

    /// <summary>Text right-aligned at rightX, vertically centred on midY. Chữ căn phải tại rightX, căn giữa dọc theo midY.</summary>
    private static void DrawRightAligned(SD.Graphics g, string text, int size, float rightX, float midY, SD.StringFormat format, SD.Brush brush)
    {
        SD.Font font = BoxAnnotationStyle.GetFont(size);
        SD.SizeF s = g.MeasureString(text, font, new SD.PointF(0f, 0f), format);
        g.DrawString(text, font, brush, rightX - s.Width, midY - s.Height / 2f, format);
    }

    /// <summary>Measured width of a string at an integer size. Chiều rộng đo của chuỗi ở cỡ nguyên.</summary>
    private static float MeasureWidth(SD.Graphics g, string text, int size, SD.StringFormat format) =>
        g.MeasureString(text, BoxAnnotationStyle.GetFont(size), new SD.PointF(0f, 0f), format).Width;

    /// <summary>Scales a color's alpha by a factor (colors here are opaque, so this dims them). Giảm alpha của màu theo hệ số (màu ở đây không trong suốt, nên đây là làm mờ).</summary>
    private static SD.Color Alpha(SD.Color c, float factor) => SD.Color.FromArgb((byte)(c.A * factor), c.R, c.G, c.B);
}
