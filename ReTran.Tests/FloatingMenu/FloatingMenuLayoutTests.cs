using ReTran_App.Core;
using ReTran_App.Services.FloatingMenu;

public class FloatingMenuLayoutTests
{
    private static MenuState State(bool collapsed = false, bool running = true,
        bool statsOpen = false, bool sourceListOpen = false,
        IReadOnlyList<SourceItem>? sources = null) => new(
        Collapsed: collapsed, Running: running, SourceLabel: "Màn hình 1",
        OverlayOn: true, Dst: "vi", Src: "auto", OcrFps: 3, CaptureFps: 10,
        MergeLines: true, StatsOpen: statsOpen, SourceListOpen: sourceListOpen,
        Sources: sources ?? (IReadOnlyList<SourceItem>)Array.Empty<SourceItem>(), ErrorLine: null);

    [Fact]
    public void WindowSize_Collapsed_IsChipPlusMargins()
    {
        var s = State(collapsed: true);
        Assert.Equal(
            (FloatingMenuLayout.ChipSize + 2 * FloatingMenuLayout.ShadowMargin,
             FloatingMenuLayout.ChipSize + 2 * FloatingMenuLayout.ShadowMargin),
            FloatingMenuLayout.WindowSize(s));

        // Panel (chip) rect sits at the shadow-margin offset.
        Assert.Equal(
            (FloatingMenuLayout.ShadowMargin, FloatingMenuLayout.ShadowMargin,
             FloatingMenuLayout.ChipSize, FloatingMenuLayout.ChipSize),
            FloatingMenuLayout.PanelRect(s));
    }

    [Fact]
    public void WindowSize_Expanded_HeaderRowsHintClose()
    {
        var s = State(); // no source list, no stats block, no error line
        int rows = FloatingMenuLayout.Rows(s).Count;
        Assert.Equal(10, rows); // 9 settings rows + close

        int expectedPanelH = FloatingMenuLayout.HeaderHeight + rows * FloatingMenuLayout.RowHeight
                             + FloatingMenuLayout.HintRowHeight;
        var (w, h) = FloatingMenuLayout.WindowSize(s);
        Assert.Equal(FloatingMenuLayout.PanelWidth + 2 * FloatingMenuLayout.ShadowMargin, w);
        Assert.Equal(expectedPanelH + 2 * FloatingMenuLayout.ShadowMargin, h);

        // Panel rect = window inset by the shadow margin.
        var (px, py, pw, ph) = FloatingMenuLayout.PanelRect(s);
        Assert.Equal(
            (FloatingMenuLayout.ShadowMargin, FloatingMenuLayout.ShadowMargin,
             w - 2 * FloatingMenuLayout.ShadowMargin, h - 2 * FloatingMenuLayout.ShadowMargin),
            (px, py, pw, ph));
    }

    [Fact]
    public void WindowSize_StatsOpen_AddsStatsBlock()
    {
        var closed = FloatingMenuLayout.WindowSize(State());
        var open = FloatingMenuLayout.WindowSize(State(statsOpen: true));
        Assert.Equal(closed.Item2 + FloatingMenuLayout.StatsBlockHeight, open.Item2);
        Assert.Equal(closed.Item1, open.Item1); // width unchanged
    }

    [Fact]
    public void HitTest_OutsidePanel_ReturnsNull()
    {
        var s = State();
        var (w, h) = FloatingMenuLayout.WindowSize(s);

        // Shadow margin on all four sides → HTTRANSPARENT.
        Assert.Null(FloatingMenuLayout.HitTest(0, 250, s));         // left margin
        Assert.Null(FloatingMenuLayout.HitTest(w - 1, 250, s));     // right margin
        Assert.Null(FloatingMenuLayout.HitTest(w / 2, 0, s));       // top margin
        Assert.Null(FloatingMenuLayout.HitTest(w / 2, h - 1, s));   // bottom margin

        // Rounded corners (panel spans x [8..328), y [8..502), radius 12): points in a corner
        // square but beyond the arc are outside the panel.
        Assert.Null(FloatingMenuLayout.HitTest(8, 8, s));           // top-left, outside the arc
        Assert.Null(FloatingMenuLayout.HitTest(w - 9, 9, s));       // top-right, outside the arc
        Assert.Null(FloatingMenuLayout.HitTest(8, h - 9, s));       // bottom-left, outside the arc

        // Just inside the same bottom-left arc the panel still answers (close row).
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowCloseMenu, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(12, h - 13, s));
    }

    [Fact]
    public void HitTest_Collapsed_ChipZone()
    {
        var s = State(collapsed: true);
        Assert.Equal(new MenuHit(FloatingMenuLayout.Chip, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(30, 30, s)); // chip centre
        Assert.Equal(new MenuHit(FloatingMenuLayout.Chip, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(12, 12, s)); // inside the rounded chip corner
        Assert.Null(FloatingMenuLayout.HitTest(8, 8, s));    // outside the chip's arc
        Assert.Null(FloatingMenuLayout.HitTest(0, 30, s));   // margin
    }

    [Fact]
    public void HitTest_Expanded_RowOrderAndIds()
    {
        var s = State();
        // Panel top at y=8; header 8..44; rows in Rows() order from 44 (each 44 px), then the
        // 18-px hint line, then the close row. Midpoint x of the 320-px-wide panel: 8+160=168.
        int x = FloatingMenuLayout.ShadowMargin + FloatingMenuLayout.PanelWidth / 2;
        var expected = new (int Id, int Y)[]
        {
            (FloatingMenuLayout.RowStartStop, 44 + 22),    // 66
            (FloatingMenuLayout.RowSource, 88 + 22),       // 110
            (FloatingMenuLayout.RowOverlay, 132 + 22),     // 154
            (FloatingMenuLayout.RowDst, 176 + 22),         // 198
            (FloatingMenuLayout.RowSrc, 220 + 22),         // 242
            (FloatingMenuLayout.RowOcrFps, 264 + 22),      // 286
            (FloatingMenuLayout.RowCaptureFps, 308 + 22),  // 330
            (FloatingMenuLayout.RowMergeLines, 352 + 22),  // 374
            (FloatingMenuLayout.RowStats, 396 + 22),       // 418
            (FloatingMenuLayout.RowCloseMenu, 458 + 22),   // 480 — after the hint line at 440..458
        };
        foreach (var (id, y) in expected)
            Assert.Equal(new MenuHit(id, MenuZone.Whole), FloatingMenuLayout.HitTest(x, y, s));

        // The hint line itself is inside the panel but not a row → no hit.
        Assert.Null(FloatingMenuLayout.HitTest(x, 449, s));
    }

    [Fact]
    public void HitTest_Stepper_LeftRightZones()
    {
        var s = State();
        // OCR fps row centre y=286 and capture fps row centre y=330; zones are the 40-px edges.
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowOcrFps, MenuZone.Left),
                     FloatingMenuLayout.HitTest(28, 286, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowOcrFps, MenuZone.Right),
                     FloatingMenuLayout.HitTest(308, 286, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowOcrFps, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(168, 286, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowCaptureFps, MenuZone.Left),
                     FloatingMenuLayout.HitTest(28, 330, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowCaptureFps, MenuZone.Right),
                     FloatingMenuLayout.HitTest(308, 330, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowCaptureFps, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(168, 330, s));
    }

    [Fact]
    public void HitTest_SourceList_InsertedAfterSourceRow()
    {
        var sources = new[]
        {
            new SourceItem(true, 0, "Màn hình 1", null),
            new SourceItem(false, -1, "game.exe — Tít", "0x1"),
        };
        var s = State(sourceListOpen: true, sources: sources);

        // Rows(): the two source rows sit right after RowSource.
        Assert.Equal(
            new[] { 0, 1, FloatingMenuLayout.SourceItemBase, FloatingMenuLayout.SourceItemBase + 1,
                    2, 3, 4, 5, 6, 7, 8, 9 },
            FloatingMenuLayout.Rows(s).Select(r => r.Id));

        // Source row centre y=110; items at 154 and 198; overlay pushed down to 242.
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowSource, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(168, 110, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.SourceItemBase, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(168, 154, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.SourceItemBase + 1, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(168, 198, s));
        Assert.Equal(new MenuHit(FloatingMenuLayout.RowOverlay, MenuZone.Whole),
                     FloatingMenuLayout.HitTest(168, 242, s));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(61, 60)]
    [InlineData(30, 30)]
    [InlineData(-5, 1)]
    public void ClampFps_Bounds(int input, int expected) =>
        Assert.Equal(expected, FloatingMenuLayout.ClampFps(input));

    [Fact]
    public void CycleIndex_WrapsBothWays_AndUnknownCurrent()
    {
        var codes = new[] { "vi", "en", "ja" };
        Assert.Equal(1, FloatingMenuLayout.CycleIndex(codes, "vi", forward: true));
        Assert.Equal(0, FloatingMenuLayout.CycleIndex(codes, "ja", forward: true));   // wraps to first
        Assert.Equal(2, FloatingMenuLayout.CycleIndex(codes, "vi", forward: false));  // wraps to last
        Assert.Equal(0, FloatingMenuLayout.CycleIndex(codes, "xx", forward: true));   // unknown → 0
        Assert.Equal(0, FloatingMenuLayout.CycleIndex(codes, "xx", forward: false));
    }

    [Fact]
    public void Rows_OverlayAlwaysPresent_StartStopLabelSwitchesWithRunning()
    {
        // Presence is state-independent: the Start/Stop row and the overlay row exist in both
        // pipeline states (the label text is resolved from Running by the renderer/service).
        var running = FloatingMenuLayout.Rows(State(running: true));
        var stopped = FloatingMenuLayout.Rows(State(running: false));

        Assert.Equal(10, running.Count);
        Assert.Equal(running.Select(r => r.Id), stopped.Select(r => r.Id));
        Assert.Contains((FloatingMenuLayout.RowStartStop, "startstop"), running);
        Assert.Contains((FloatingMenuLayout.RowOverlay, "overlay"), running);
        Assert.Contains((FloatingMenuLayout.RowCloseMenu, "close"), running);

        // Collapsed state has no rows at all (only the chip).
        Assert.Empty(FloatingMenuLayout.Rows(State(collapsed: true)));
    }

    [Fact]
    public void BuildSources_MonitorsFirst_TruncatesLongTitles_StableOrder_LimitsWindows()
    {
        var monitors = new[] { new MonitorInfo(0, "Màn hình chính"), new MonitorInfo(1, "Màn hình phụ") };
        var windows = new List<WindowInfo>();
        for (int i = 0; i < 10; i++)
            windows.Add(new WindowInfo($"0x{i + 1}", $"title {i}", $"proc{i}.exe"));

        // Long title on the first window: "game.exe — " + 60 chars ⇒ cut to 39 + ellipsis.
        windows[0] = new WindowInfo("0x1", new string('t', 60), "game.exe");
        // Surrogate-pair title placed so the naive 39-char cut lands between the halves.
        windows[1] = new WindowInfo("0x2", new string('x', 34) + "\U0001F3AE" + new string('y', 5), "g");

        var items = FloatingMenuLayout.BuildSources(monitors, windows);

        // 2 monitors first, then at most MaxSourceWindows (8) of the 10 windows.
        Assert.Equal(2 + FloatingMenuLayout.MaxSourceWindows, items.Count);
        Assert.All(items.Take(2), i => Assert.True(i.IsMonitor));
        Assert.Equal("Màn hình 1", items[0].Label);
        Assert.Equal(0, items[0].Id);
        Assert.Null(items[0].HwndHex);
        Assert.Equal("Màn hình 2", items[1].Label);

        // Stable input order; windows carry their hwnd, Id unused (-1).
        for (int i = 0; i < FloatingMenuLayout.MaxSourceWindows; i++)
        {
            var item = items[2 + i];
            Assert.False(item.IsMonitor);
            Assert.Equal(-1, item.Id);
            Assert.Equal($"0x{i + 1}", item.HwndHex);
        }

        // Long label truncated to exactly 40 chars with an ellipsis.
        string longLabel = items[2].Label;
        Assert.Equal(40, longLabel.Length);
        Assert.EndsWith("…", longLabel);
        Assert.Equal("game.exe — " + new string('t', 28) + "…", longLabel);

        // Surrogate-safe: the cut backs off one char so no lone high surrogate remains.
        string surrLabel = items[3].Label;
        Assert.EndsWith("…", surrLabel);
        for (int i = 0; i < surrLabel.Length; i++)
        {
            if (char.IsHighSurrogate(surrLabel[i]))
                Assert.True(i + 1 < surrLabel.Length && char.IsLowSurrogate(surrLabel[i + 1]),
                    $"lone high surrogate at index {i}");
        }
        // Cut at 39 lands on the high surrogate (index 38) → backs off to 38, keeping all 34 x's.
        Assert.Equal("g — " + new string('x', 34) + "…", surrLabel);

        // Short labels are untouched.
        Assert.Equal("proc2.exe — title 2", items[4].Label);
    }

    [Fact]
    public void BuildSources_CustomMonitorLabelFactory_UsedForMonitorsOnly()
    {
        var monitors = new[] { new MonitorInfo(0, "M1"), new MonitorInfo(1, "M2") };
        var windows = new[] { new WindowInfo("0x1", "Game", "game.exe") };

        // The factory receives the zero-based monitor id; window labels must stay untouched.
        // Factory nhận chỉ số màn hình từ 0; label cửa sổ phải giữ nguyên.
        var items = FloatingMenuLayout.BuildSources(monitors, windows, i => $"Monitor {i + 1}");

        Assert.Equal("Monitor 1", items[0].Label);
        Assert.Equal("Monitor 2", items[1].Label);
        Assert.True(items[0].IsMonitor && items[1].IsMonitor);
        Assert.Equal("game.exe — Game", items[2].Label);
    }

    [Fact]
    public void ClampPosition_ClampsIntoContainingMonitor_EdgeAndOffscreen()
    {
        var monitors = new[]
        {
            (Left: 0, Top: 0, Right: 1920, Bottom: 1080),
            (Left: 1920, Top: 0, Right: 3840, Bottom: 1080),
        };

        // Anchor near the right edge of monitor 0: clamped inside monitor 0, not pushed into 1.
        Assert.Equal((1584, 100), FloatingMenuLayout.ClampPosition(1900, 100, 336, 510, monitors));

        // Off-screen anchor: falls back to the first work area.
        Assert.Equal((1584, 570), FloatingMenuLayout.ClampPosition(5000, 2000, 336, 510, monitors));

        // Empty monitor list: clamps into the (0,0)..(1920,1080) fallback rect.
        var none = new List<(int Left, int Top, int Right, int Bottom)>();
        Assert.Equal((1860, 1020), FloatingMenuLayout.ClampPosition(9999, 9999, 60, 60, none));

        // Off-screen with a single non-origin area: clamps into that (first) area, not the fallback.
        var one = new[] { (Left: 100, Top: 100, Right: 800, Bottom: 600) };
        Assert.Equal((464, 100), FloatingMenuLayout.ClampPosition(9999, 9999, 336, 510, one));
    }

    [Fact]
    public void DefaultPosition_TopRightMinus16()
    {
        Assert.Equal((1568, 16),
            FloatingMenuLayout.DefaultPosition(336, 510, (Left: 0, Top: 0, Right: 1920, Bottom: 1080)));
        Assert.Equal((3488, 16),
            FloatingMenuLayout.DefaultPosition(336, 510, (Left: 1920, Top: 0, Right: 3840, Bottom: 1080)));
    }
}
