# Skip-confirmation popup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Nút "Bỏ qua" trên màn hình lỗi setup mở popup 3 lựa chọn — **[Thoát ReTran] · [Bỏ qua thiết lập] · [Quay lại]** — thay vì đóng app ngay.

**Architecture:** Cờ `SkippedForSession` trong-memory trên `SetupCoordinator` + `PipelineGate.CanStartPipeline` nhận thêm tham số; popup là overlay trong-wizard ở `SetupModal.razor`; `SettingsPanel` mở điều kiện "Chạy lại setup" làm đường quay lại cho phiên đã skip.

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid (Razor), xUnit v3 (MTP runner), i18n vi/en trong `I18nStrings.cs`.

**Spec:** `Docs/superpowers/specs/2026-11-13-skip-confirmation-popup-design.md`

## Global Constraints

- **Một commit duy nhất**, stage theo path; KHÔNG `git add -A`, KHÔNG push, KHÔNG stage `ReTran.App/wwwroot/**`.
- Doc comment XML **song ngữ** (Anh trước, Việt ngay sau) cho mọi API public thay đổi.
- i18n: 4 key mới phải có ĐỦ ở cả 2 bảng vi + en (guard `I18nServiceTests` sẽ bắt thiếu).
- KHÔNG persist cờ skip; KHÔNG đổi hành vi `Skip()` hiện có (đóng app qua `closeApp`).
- Suite hiện tại **748/748** — không được regress; số test tăng (bảng gate 4→8 dòng, +4 test coordinator).
- Implementer KHÔNG chạy app/không live-verify — controller + user làm (chuột thật).
- Report tiếng Việt vào `.superpowers/sdd/2026-11-04-merge-thresholds/task-28-report.md`.

## Review Focus

1. **Bấm "Bỏ qua thiết lập" rồi Start khi cờ skip vẫn đang chạy một lượt setup** (cửa sổ Checking đồng bộ ~15 s) — gate phải vẫn chặn vì `running` thắng `skipped` (gate row `(false,true,true)→false`).
2. **Vòng lặp rerun→fail→"Bỏ qua thiết lập"→rerun lần nữa** — nút "Chạy lại setup" phải bấm được lại (`_rerunStarted` reset khi `!IsRunning`); smoke #4 (không có bUnit → manual).
3. **Bấm liên tiếp trên popup** (double-click) — guard `_busy` + `TryBeginRun`; không được đóng app hai lần / mở hai luồng.
4. **Đổi ngôn ngữ khi popup đang mở** — text đổi theo (key resolve mỗi render); smoke #1.
5. **Phiên đã "Bỏ qua thiết lập" rồi đóng app, mở lại** — wizard phải quay lại (không persist cờ); smoke #6.

---

### Task 1: Skip-for-session flow + popup xác nhận

**Files:**
- Modify: `ReTran.App/Services/Setup/SetupCoordinator.cs` (~:250 khu `IsComplete`; ~:303 `StartAsync`; ~:505-511 `Skip`)
- Modify: `ReTran.App/Services/Setup/PipelineGate.cs` (~:24-33)
- Modify: `ReTran.App/Components/Shared/SetupModal.razor` (~:11 điều kiện render; ~:76-88 Failed + nút Skip; ~:271-288 handler)
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor` (~:261-271 section; ~:971-975 `OnSetupChanged`)
- Modify: `ReTran.App/Components/Pages/Home.razor` (~:480 call site gate)
- Modify: `ReTran.App/Services/I18nStrings.cs` (vi ~:347; en ~:706)
- Test: `ReTran.Tests/Setup/SetupWiringTests.cs` (~:52-60 theory), `ReTran.Tests/Setup/SetupCoordinatorTests.cs` (~:575, ~:647 call sites; harness `NewCoordinator` ~:78)

**Interfaces:**
- Produces:
  - `SetupCoordinator.SkippedForSession` — `{ get; private set; }` : `bool` (in-memory, không persist)
  - `SetupCoordinator.SkipForSession()` : `void` — set cờ + `Publish()`
  - `PipelineGate.CanStartPipeline(bool setupComplete, bool setupRunning, bool skippedForSession)` : `bool` = `(setupComplete || skippedForSession) && !setupRunning`
- Consumes: `SetupCoordinator.Publish()` (private, dùng trong class), `Changed` event, `TryBeginRun`, harness test có sẵn (`_closeAppCalled`, `_changedCount`, `WriteSetupJson`).

- [ ] **Step 1: Viết test fail trước**

**1a.** Trong `ReTran.Tests/Setup/SetupWiringTests.cs` — thay theory cũ (4 dòng, ~:52-60) bằng:

```csharp
    [Theory]
    // (complete, running, skipped): "Bỏ qua thiết lập" THAY THẾ completion cho phiên này, nhưng
    // "đang chạy một lượt" luôn khóa (kể cả cửa sổ rerun cũ).
    [InlineData(true, false, false, true)]   // complete + idle → cho start (cũ)
    [InlineData(true, false, true, true)]    // complete + skipped → cho start
    [InlineData(false, false, true, true)]   // ⭐ core: chưa xong nhưng đã "Bỏ qua thiết lập" → cho start
    [InlineData(false, false, false, false)] // chưa xong, chưa skip → chặn (cũ)
    [InlineData(true, true, false, false)]   // cửa sổ rerun (cũ) → chặn
    [InlineData(false, true, false, false)]  // first run đang chạy (cũ) → chặn
    [InlineData(true, true, true, false)]    // skip không mở được cửa sổ rerun
    [InlineData(false, true, true, false)]   // skip + đang chạy → chặn
    public void CanStartPipeline_RequiresIdleAndCompleteOrSkipped(
        bool setupComplete, bool setupRunning, bool skippedForSession, bool expected) =>
        Assert.Equal(expected, PipelineGate.CanStartPipeline(
            setupComplete: setupComplete, setupRunning: setupRunning, skippedForSession: skippedForSession));
```

Cập nhật luôn doc comment của class/method test cho khớp (thêm vế "skipped-for-session mở gate").

**1b.** Trong `ReTran.Tests/Setup/SetupCoordinatorTests.cs`:

- Sửa 2 call-site hiện có của `CanStartPipeline` (~:575 và ~:647) thành 3 tham số — thêm `coordinator.SkippedForSession`:
  `Assert.True(PipelineGate.CanStartPipeline(coordinator.IsComplete, coordinator.IsRunning, coordinator.SkippedForSession));`
- Thêm 4 test mới (đặt sau khu `IsRunning` ~:657 hoặc cuối file, kèm tiêu đề nhóm `── "Bỏ qua thiết lập" cho phiên này (popup xác nhận — spec 2026-11-13) ──`):

```csharp
    [Fact]
    public void SkipForSession_SetsFlagAndPublishes_WithoutClosingApp()
    {
        var coordinator = NewCoordinator();
        int before = _changedCount;

        coordinator.SkipForSession();

        Assert.True(coordinator.SkippedForSession);
        Assert.True(_changedCount > before);
        Assert.False(_closeAppCalled);
        Assert.False(coordinator.IsComplete); // không ghi state: lần mở sau wizard nhắc lại
    }

    [Fact]
    public void Skip_ClosesApp()
    {
        var coordinator = NewCoordinator();

        coordinator.Skip();

        Assert.True(_closeAppCalled);
        Assert.False(coordinator.SkippedForSession);
    }

    [Fact]
    public async Task StartAsync_ClearsSessionSkip()
    {
        var coordinator = NewCoordinator();
        coordinator.SkipForSession();
        Assert.True(coordinator.SkippedForSession);

        // State trống → lượt chạy dừng ở ChoosingLanguage; chỉ cần chứng minh cờ bị thu hồi khi lượt chạy bắt đầu.
        await coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.False(coordinator.SkippedForSession);
    }

    [Fact]
    public async Task SecondStartAsync_WhileRunInFlight_IsANoOp()
    {
        // Chống double-click (popup + nút rerun): lời gọi thứ hai trong lúc một lượt đang chạy phải
        // no-op — TryBeginRun giữ _running, không có lượt chồng. (Cùng kỹ thuật latch probe mạng như
        // test ForcedRerun ở trên.)
        var blockProbe = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, bool> probe = _ =>
        {
            if (!blockProbe) return true;
            entered.TrySetResult();
            release.Task.Wait(TestContext.Current.CancellationToken);
            return true;
        };
        var coordinator = NewCoordinator(isHostReachable: probe);
        blockProbe = true;
        Task first = Task.Run(
            () => coordinator.StartAsync(ct: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Task second = coordinator.StartAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(second.IsCompleted); // TryBeginRun false → trả về ngay, không chồng lượt
        Assert.True(coordinator.IsRunning);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(coordinator.IsRunning);
    }
```

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SetupWiringTests"`
Expected: **build FAIL** — `CS1501: No overload for method 'CanStartPipeline' takes 3 arguments` + `CS1061: 'SetupCoordinator' does not contain 'SkippedForSession'/'SkipForSession'`. (Đây là "red" của thay đổi chữ ký.)

- [ ] **Step 3: Implement `SetupCoordinator`**

**3a.** Thêm property ngay sau khối `IsRunning` (~:266):

```csharp
    /// <summary>
    /// True once the user chose "skip setup for this session" on the failure popup: the wizard stays
    /// hidden and the pipeline gate opens WITHOUT the setup being complete — nothing is persisted, so
    /// the next launch shows the wizard again. Cleared when an explicit run starts ("re-run setup").
    /// Đúng khi user chọn "bỏ qua thiết lập cho phiên này" trên popup màn lỗi: wizard ẩn và cổng pipeline
    /// mở dù setup chưa hoàn tất — không ghi state nào, nên lần mở app sau wizard hiện lại. Cờ được xóa
    /// khi một lượt chạy tường minh bắt đầu (đường "chạy lại setup").
    /// </summary>
    public bool SkippedForSession { get; private set; }
```

**3b.** Thêm method cạnh `Skip()` (~:511) và mở rộng doc của `Skip()`:

```csharp
    /// <summary>
    /// The popup's "skip setup" choice: hides the wizard for the rest of the session and lets the
    /// pipeline start, without closing the app and without writing any state.
    /// Lựa chọn "bỏ qua thiết lập" của popup: ẩn wizard tới hết phiên và cho pipeline start, không đóng
    /// app và không ghi state nào.
    /// </summary>
    public void SkipForSession()
    {
        SkippedForSession = true;
        Publish();
    }
```

`Skip()` giữ nguyên thân; doc thêm một câu: từ 2026-11-13 đây là lựa chọn **"Thoát ReTran"** của popup (nút "Bỏ qua" trên màn lỗi mở popup thay vì gọi thẳng).

**3c.** Trong `StartAsync`, ngay sau `if (!TryBeginRun()) return;` (trước `_state = _stateFactory();`):

```csharp
            // An explicit run (the modal's first-run trigger or the Settings re-run) revokes the
            // session skip: after "skip setup", the way back IS this run, and the wizard must be
            // visible again while it runs.
            // Một lượt chạy tường minh (trigger first-run của modal hoặc rerun từ Settings) thu hồi cờ
            // skip phiên: sau "bỏ qua thiết lập", đường quay lại CHÍNH LÀ lượt chạy này, và wizard phải
            // hiện lại trong lúc nó chạy.
            if (SkippedForSession)
            {
                SkippedForSession = false;
                Publish();
            }
```

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SetupCoordinatorTests"` — Expected: PASS (3 test mới + các test cũ).

- [ ] **Step 4: Implement `PipelineGate` + call site**

**4a.** `PipelineGate.cs`: chữ ký mới + doc song ngữ cập nhật (nêu: skip thay completion cho phiên, pipeline start vẫn có thể fail ở Start như thường; `running` luôn khóa):

```csharp
    internal static bool CanStartPipeline(bool setupComplete, bool setupRunning, bool skippedForSession) =>
        (setupComplete || skippedForSession) && !setupRunning;
```

Thêm `<param name="skippedForSession">` vào doc.

**4b.** `Home.razor` ~:480:

```csharp
        if (!ReTran_App.Services.Setup.PipelineGate.CanStartPipeline(Setup.IsComplete, Setup.IsRunning, Setup.SkippedForSession)) return;
```

(cập nhật comment phía trên cho khớp — thêm vế "hoặc user đã bỏ qua thiết lập cho phiên này").

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SetupWiringTests"` — Expected: PASS (8 dòng).

- [ ] **Step 5: UI — `SetupModal.razor`**

**5a.** Điều kiện render (~:11): `@if (_wizardActive && !Coordinator.IsComplete && !Coordinator.SkippedForSession)` — cập nhật comment đầu file (render rỗng khi hoàn tất **hoặc đã bỏ qua cho phiên này**).

**5b.** Nút "Bỏ qua" ở Failed (~:86): `@onclick="Skip"` → `@onclick="ShowSkipConfirm"` (nhãn key giữ nguyên). Xóa method `Skip()` cũ (~:271-288).

**5c.** Thêm field `private bool _showSkipConfirm;` cạnh `_busy`.

**5d.** Thêm popup ngay SAU khối `<div class="fixed inset-0 ... style="z-index: 60;"> ... </div>` (vẫn trong `@if` ngoài):

```razor
    @if (_showSkipConfirm)
    {
        @* Popup xác nhận của nút "Bỏ qua" (spec 2026-11-13): lớp phủ TRÊN modal (z 61 > 60, dưới
           toasts 70). [Thoát ReTran] = hành vi Skip cũ; [Bỏ qua thiết lập] = ẩn wizard + mở gate cho
           phiên này; [Quay lại] = đóng popup. *@
        <div class="fixed inset-0 flex items-center justify-center bg-black/50 p-4" style="z-index: 61;">
            <div role="dialog" aria-modal="true" aria-label="@(L["setup.skipConfirm.message"])"
                 class="rt-setup-modal w-full max-w-sm rounded-lg border border-surface bg-background p-4 shadow-xl">
                <p class="mb-3 text-sm text-surface">@(L["setup.skipConfirm.message"])</p>
                <div class="flex gap-2" style="flex-wrap: wrap;">
                    <button type="button" class="rt-btn rt-mini" disabled="@_busy" @onclick="ConfirmExit">@(L["setup.skipConfirm.exit"])</button>
                    <button type="button" class="rt-btn rt-btn-primary rt-mini" disabled="@_busy" @onclick="ConfirmSkipForSession">@(L["setup.skipConfirm.skip"])</button>
                    <button type="button" class="rt-btn rt-mini" disabled="@_busy" @onclick="BackFromSkipConfirm">@(L["setup.skipConfirm.back"])</button>
                </div>
            </div>
        </div>
    }
```

**5e.** Handlers (cùng pattern `_busy` như nút sibling; "Quay lại" là hành động phụ):

```csharp
    // Popup "Bỏ qua" (spec 2026-11-13): mở popup thay vì đóng app ngay.
    private void ShowSkipConfirm()
    {
        if (_busy) return;
        _showSkipConfirm = true;
    }

    // [Thoát ReTran] — đúng hành vi Skip cũ: đóng app sạch qua closeApp, không ghi state.
    private void ConfirmExit()
    {
        if (_busy) return;
        _busy = true;
        try { Coordinator.Skip(); }
        finally { _busy = false; StateHasChanged(); }
    }

    // [Bỏ qua thiết lập] — ẩn wizard tới hết phiên + mở gate; modal tự ẩn qua điều kiện render.
    private void ConfirmSkipForSession()
    {
        if (_busy) return;
        _busy = true;
        try { Coordinator.SkipForSession(); }
        finally { _busy = false; StateHasChanged(); }
    }

    // [Quay lại] — đóng popup, về màn lỗi như cũ.
    private void BackFromSkipConfirm()
    {
        if (_busy) return;
        _showSkipConfirm = false;
        StateHasChanged();
    }
```

- [ ] **Step 6: UI — `SettingsPanel.razor` (đường quay lại)**

**6a.** Section (~:261): `@if (Setup.IsComplete)` → `@if (Setup.IsComplete || Setup.SkippedForSession)`; cập nhật comment (hiện khi wizard xong **hoặc user đã "Bỏ qua thiết lập" phiên này — đây là đường quay lại**).

**6b.** `OnSetupChanged` (~:971-975): `if (Setup.IsComplete) _rerunStarted = false;` → `if (Setup.IsComplete || !Setup.IsRunning) _rerunStarted = false;` + comment (một lượt rerun kết thúc ở Failed vẫn phải để nút bấm lại được — vòng lặp rerun→fail→skip→rerun; publish lúc này có thể vẫn `IsRunning` nên điều kiện `IsComplete` đơn lẻ không đủ, phải reset ở lần publish kế tiếp khi đã rảnh).

- [ ] **Step 7: i18n — `I18nStrings.cs` (2 bảng)**

Bảng **vi** (sau dòng `setup.rerun` ~:347):

```csharp
        // Popup xác nhận "Bỏ qua" (spec 2026-11-13)
        ["setup.skipConfirm.message"] = "Bạn muốn thoát ReTran hay bỏ qua phần thiết lập?",
        ["setup.skipConfirm.exit"] = "Thoát ReTran",
        ["setup.skipConfirm.skip"] = "Bỏ qua thiết lập",
        ["setup.skipConfirm.back"] = "Quay lại",
```

Bảng **en** (sau `setup.rerun` ~:706):

```csharp
        // "Skip" confirmation popup (spec 2026-11-13)
        ["setup.skipConfirm.message"] = "Do you want to exit ReTran or skip the setup?",
        ["setup.skipConfirm.exit"] = "Exit ReTran",
        ["setup.skipConfirm.skip"] = "Skip setup",
        ["setup.skipConfirm.back"] = "Back",
```

- [ ] **Step 8: Build + full suite**

Run: `dotnet build "ReTran Project.slnx"` → Expected: **0 error / ≤34 warning**.
Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` → Expected: **≥ 748 total, 0 failed** (dự kiến ~756: +4 dòng gate, +4 coordinator). Ghi số thật.
Filters: `SetupCoordinatorTests`, `SetupWiringTests`, `I18nServiceTests` (guard 2 ngôn ngữ) — ghi counts.

- [ ] **Step 9: Commit**

```bash
git add "ReTran.App/Services/Setup/SetupCoordinator.cs" "ReTran.App/Services/Setup/PipelineGate.cs" "ReTran.App/Components/Shared/SetupModal.razor" "ReTran.App/Components/Shared/SettingsPanel.razor" "ReTran.App/Components/Pages/Home.razor" "ReTran.App/Services/I18nStrings.cs" "ReTran.Tests/Setup/SetupWiringTests.cs" "ReTran.Tests/Setup/SetupCoordinatorTests.cs"
git commit -m "feat(setup): offer exit-or-skip popup on the failed wizard screen"
```

- [ ] **Step 10 (controller — KHÔNG phải implementer): verify chuột thật + merge round 4**

Live verify trên build merged (user bấm thật): theo checklist §6 của spec — popup 3 nút, [Quay lại], [Bỏ qua thiết lập] + Start + "Chạy lại setup", [Thoát ReTran], mở lại app → wizard hiện lại.
