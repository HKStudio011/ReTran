using System.ComponentModel;
using System.Diagnostics;
using ReTran.Core.Ocr;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="UvSyncRunner"/>: the one uv command a flavor selects (Amendment A2 — a
/// dependency-group sync against the shipped lock, with <c>pyproject.toml</c> never read or written), the
/// working dir, env, first non-zero exit code, the output tail kept for the error screen, the
/// installed-flavor probe, and the packaging invariants of the sidecar tree the app ships.
/// Test cho <see cref="UvSyncRunner"/>: đúng một lệnh uv mà flavor chọn (Amendment A2 — sync theo
/// dependency-group trên lock đi kèm, <c>pyproject.toml</c> không được đọc hay ghi), working dir, env,
/// exit code khác 0 đầu tiên, đuôi output giữ cho màn hình lỗi, probe flavor đã cài, và các bất biến
/// packaging của cây sidecar mà app ship.
/// </summary>
/// <remarks>
/// The command tests drive a FAKE process runner over a throwaway temp tree: this suite never invokes
/// real <c>uv</c> and never writes the repo's own <c>pyproject.toml</c> or <c>.venv</c> — real
/// <c>uv sync</c> / <c>uv run pytest</c> evidence belongs to the task report (measured 2026-11-07), not
/// here. The packaging tests are the one exception: they READ (never write) the repo's real
/// <c>pyproject.toml</c> and <c>uv.lock</c>, and skip politely outside the repo layout.
/// Các test lệnh chạy qua FAKE process runner trên cây temp dùng một lần: suite không gọi <c>uv</c> thật
/// và không ghi <c>pyproject.toml</c> hay <c>.venv</c> của repo — bằng chứng <c>uv sync</c> /
/// <c>uv run pytest</c> thật thuộc báo cáo task (đo 2026-11-07), không ở đây. Test packaging là ngoại lệ
/// duy nhất: chúng ĐỌC (không ghi) <c>pyproject.toml</c> và <c>uv.lock</c> thật của repo, và bỏ qua lịch
/// sự khi ngoài layout repo.
/// </remarks>
public class UvSyncRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _ocrDir;
    private readonly string _pyproject;

    /// <summary>Path of a uv.exe that does not exist: the fake runner never launches it.</summary>
    private const string UvExe = @"C:\retran-test-fake\runtimes\win-x64\uv.exe";

    /// <summary>
    /// A <c>pyproject.toml> in the shape the app shipped BEFORE Amendment A2 — the GPU pins inside
    /// <c>[project].dependencies</c>, one paddle index, and no gpu/cpu dependency groups. That is exactly
    /// the file the retired rewrite machine used to edit, so it is the fixture for the new contract: a
    /// stale, foreign, or already-CPU pyproject must survive a sync byte for byte.
    /// <c>pyproject.toml</c> theo hình dạng app ship TRƯỚC Amendment A2 — pin GPU nằm trong
    /// <c>[project].dependencies</c>, một index paddle, không có group gpu/cpu. Đó chính là file mà máy
    /// chế rewrite đã bị bỏ đi từng sửa, nên nó là fixture cho hợp đồng mới: một pyproject cũ, lạ, hay đã
    /// ở CPU phải sống sót qua sync mà không đổi một byte nào.
    /// </summary>
    private const string PyprojectPreA2Shape = """
        [project]
        name = "retran-core"
        version = "0.1.0"
        description = ""
        requires-python = ">=3.13"
        dependencies = [
            "numpy>=1.24,<2.4",
            "paddleocr>=3.7.0",
            "paddlepaddle-gpu==3.4.0",
            "nvidia-cudnn-cu12==9.9.0.52",
            "pillow>=12.3.0",
        ]

        [dependency-groups]
        dev = [
            "pytest>=9.1.1",
        ]

        [[tool.uv.index]]
        name = "paddle-cu129"
        url = "https://www.paddlepaddle.org.cn/packages/stable/cu129/"
        explicit = true

        [tool.uv.sources]
        paddlepaddle-gpu = { index = "paddle-cu129" }
        """;

    /// <summary>
    /// Creates a temp sidecar tree whose <c>pyproject.toml</c> holds the pre-A2 shape (CRLF, no BOM — the
    /// repo file's format), so every command test starts from a file that a rewriting implementation would
    /// have changed.
    /// Tạo cây sidecar temp có <c>pyproject.toml</c> ở hình dạng pre-A2 (CRLF, không BOM — đúng định dạng
    /// file repo), để mọi test lệnh bắt đầu từ một file mà implementation kiểu rewrite sẽ đã sửa.
    /// </summary>
    public UvSyncRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"retran-uvsync-{Guid.NewGuid():N}");
        _ocrDir = Path.Combine(_dir, "ReTran.OCR");
        _pyproject = Path.Combine(_ocrDir, "pyproject.toml");
        Directory.CreateDirectory(_ocrDir);
        File.WriteAllText(_pyproject, PyprojectPreA2Shape.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── SyncAsync: flavor = dependency-group, không sửa pyproject (Amendment A2) ───────────────────

    [Fact]
    public async Task SyncAsync_Gpu_RunsGroupSyncWithoutTouchingPyproject()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);
        byte[] before = File.ReadAllBytes(_pyproject);

        int exit = await runner.SyncAsync(OcrFlavor.Gpu, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        // ĐÚNG một lệnh uv, và argv là đúng chuỗi A2: chọn group gpu trên lock đi kèm (--frozen = không
        // re-resolve), bỏ default-groups để dev không bị cài kèm trên máy user.
        // EXACTLY one uv call, with exactly the A2 argv: pick the gpu group out of the shipped lock
        // (--frozen = no re-resolve) and drop default-groups so dev tools never ride along on a user box.
        Call only = Assert.Single(fake.Calls);
        Assert.Equal(UvExe, only.FileName);
        Assert.Equal(["sync", "--no-default-groups", "--group", "gpu", "--frozen"], only.Args);
        Assert.Equal(_ocrDir, only.WorkingDir);
        AssertUvEnv(only);

        // Bất biến A2: pyproject không đổi một byte nào — kể cả khi nó đang ở hình dạng pre-A2 sai flavor.
        // The A2 invariant: pyproject is byte-identical — even when it sits in the wrong pre-A2 shape.
        Assert.Equal(before, File.ReadAllBytes(runner.PyprojectPath));
        Assert.Null(runner.LastOutputTail);
    }

    [Fact]
    public async Task SyncAsync_Cpu_RunsCpuGroupSyncOnly()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);
        byte[] before = File.ReadAllBytes(_pyproject);

        int exit = await runner.SyncAsync(OcrFlavor.Cpu, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        // CPU giờ nằm TRONG lock (nhóm cpu), nên không còn lệnh `uv pip install paddlepaddle==3.4.0` thứ
        // hai như bản cũ: đúng một lời gọi, và không có "pip" trong argv.
        // CPU now lives INSIDE the lock (the cpu group), so the old second call
        // `uv pip install paddlepaddle==3.4.0` is gone: one call, and no "pip" in the argv.
        Call only = Assert.Single(fake.Calls);
        Assert.Equal(["sync", "--no-default-groups", "--group", "cpu", "--frozen"], only.Args);
        Assert.DoesNotContain("pip", only.Args);
        Assert.Equal(_ocrDir, only.WorkingDir);
        AssertUvEnv(only);

        Assert.Equal(before, File.ReadAllBytes(_pyproject));
        Assert.Null(runner.LastOutputTail);
    }

    [Fact]
    public async Task SyncAsync_ReturnsNonZeroExit_WhenUvFails_AndKeepsOutputTail()
    {
        var fake = new FakeProcessRunner();
        const string tail = "error: request to pypi.org/simple failed (proxy refused connection)";
        fake.Results.Enqueue((1, tail));

        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);
        byte[] before = File.ReadAllBytes(_pyproject);
        int exit = await runner.SyncAsync(OcrFlavor.Cpu, TestContext.Current.CancellationToken);

        // Exit code của lệnh fail được trả về và ĐUÔI OUTPUT của lệnh đó còn đọc được (Review Focus #4 —
        // màn hình lỗi hiển thị đuôi output của uv).
        // The failing command's exit code is returned and its OUTPUT TAIL stays readable (Review Focus #4 —
        // the error screen shows uv's tail).
        Assert.Equal(1, exit);
        Call only = Assert.Single(fake.Calls);
        Assert.Equal(["sync", "--no-default-groups", "--group", "cpu", "--frozen"], only.Args);
        Assert.Equal(tail, runner.LastOutputTail);
        // Lỗi proxy giữa chừng cũng không được để lại pyproject đã bị sửa.
        // A proxy failure mid-run must not leave an edited pyproject behind either.
        Assert.Equal(before, File.ReadAllBytes(_pyproject));

        // Attempt kế tiếp thành công ⇒ tail stale phải bị xoá (retry ×3 không được báo lỗi của lần trước).
        // The next attempt succeeding clears the stale tail (the retry ×3 loop must not report a
        // previous attempt's error).
        fake.Results.Clear();
        Assert.Equal(0, await runner.SyncAsync(OcrFlavor.Cpu, TestContext.Current.CancellationToken));
        Assert.Null(runner.LastOutputTail);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task SyncAsync_UnknownFlavor_ThrowsBeforeAnyUvCall()
    {
        // Flavor lạ ⇒ không được đoán group: wizard cài nhầm wheel còn tệ hơn báo lỗi (Review Focus #1).
        // An unknown flavor must not guess a group: the wizard installing the wrong wheel is worse than an
        // error (Review Focus #1).
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.SyncAsync((OcrFlavor)99, TestContext.Current.CancellationToken));
        Assert.Equal("flavor", error.ParamName);
        Assert.Empty(fake.Calls);
        Assert.Null(runner.LastOutputTail);
    }

    // ── InstalledPaddleFlavorAsync (R55: dò bằng exit code, không parse output) ────────────────────

    [Fact]
    public async Task InstalledPaddleFlavor_GpuInstalled_ReturnsGpuAndSkipsCpuProbe()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        fake.Results.Enqueue((0, "Name: paddlepaddle-gpu\nVersion: 3.4.0\n"));
        Assert.Equal(OcrFlavor.Gpu, await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        Call probe = Assert.Single(fake.Calls);
        Assert.Equal(new[] { "pip", "show", "paddlepaddle-gpu" }, probe.Args);
        Assert.Equal(_ocrDir, probe.WorkingDir);
        AssertUvEnv(probe);
        // Probe thành công ⇒ không để lại tail cũ cho màn hình lỗi.
        // A successful probe leaves no stale tail behind for the error screen.
        Assert.Null(runner.LastOutputTail);
    }

    [Fact]
    public async Task InstalledPaddleFlavor_OnlyCpuInstalled_ReturnsCpu()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        // uv trả exit ≠ 0 khi package không có trong venv (đo thật với uv 0.13.7: exit 1 +
        // "warning: Package(s) not found for: paddlepaddle-gpu").
        // uv exits non-zero when the package is not in the venv (measured with uv 0.13.7: exit 1 plus
        // "warning: Package(s) not found for: paddlepaddle-gpu").
        fake.Results.Enqueue((1, "warning: Package(s) not found for: paddlepaddle-gpu"));
        fake.Results.Enqueue((0, "Name: paddlepaddle\nVersion: 3.4.0\n"));

        Assert.Equal(OcrFlavor.Cpu, await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(new[] { "pip", "show", "paddlepaddle-gpu" }, fake.Calls[0].Args);
        Assert.Equal(new[] { "pip", "show", "paddlepaddle" }, fake.Calls[1].Args);
        foreach (Call call in fake.Calls)
        {
            Assert.Equal(UvExe, call.FileName);
            Assert.Equal(_ocrDir, call.WorkingDir);
            AssertUvEnv(call);
        }
        Assert.Null(runner.LastOutputTail);
    }

    [Fact]
    public async Task InstalledPaddleFlavor_NeitherInstalled_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        fake.Results.Enqueue((1, "warning: Package(s) not found for: paddlepaddle-gpu"));
        fake.Results.Enqueue((1, "warning: Package(s) not found for: paddlepaddle"));

        Assert.Null(await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, fake.Calls.Count);
        // Không paddle nào ⇒ caller coi như chưa cài và chạy lại sync.
        // No paddle at all ⇒ the caller treats the install as missing and syncs again.
        Assert.Null(runner.LastOutputTail);
    }

    [Fact]
    public async Task InstalledPaddleFlavor_UvFails_ReturnsNull_AndLeavesLastOutputTailAlone()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        // venv chưa có / uv.exe bị AV làm hỏng ⇒ exit ≠ 0 (uv báo "Package(s) not found" với exit 1 cho cả
        // hai trường hợp, đo thật với uv 0.13.7) ⇒ null, không được ném, và không được đoán flavor.
        // Probe không được đụng LastOutputTail: tail của lệnh sync fail trước đó là thứ màn hình lỗi phải
        // hiển thị, wizard dò flavor trước khi retry.
        // Measured with uv 0.13.7: a missing package AND a missing venv both give "Package(s) not found"
        // with exit 1 ⇒ null, and the probe must not throw or guess a flavor. The probe must also leave
        // LastOutputTail alone: the tail of the earlier failed sync is what the error screen shows, and the
        // wizard probes the flavor before it retries.
        const string syncTail = "error: request to pypi.org/simple failed (proxy refused connection)";
        fake.Results.Enqueue((1, syncTail));
        Assert.Equal(1, await runner.SyncAsync(OcrFlavor.Gpu, TestContext.Current.CancellationToken));
        Assert.Equal(syncTail, runner.LastOutputTail);

        fake.Results.Enqueue((1, "warning: Package(s) not found for: paddlepaddle-gpu"));
        fake.Results.Enqueue((1, "warning: Package(s) not found for: paddlepaddle"));
        Assert.Null(await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        Assert.Equal(syncTail, runner.LastOutputTail);
        Assert.Equal(3, fake.Calls.Count);
    }

    [Fact]
    public async Task InstalledPaddleFlavor_TruncatedOutput_StillDetectsFromExitCode()
    {
        // Lý do R55: ProcessRunner chỉ giữ ~8 KB CUỐI của output, và danh sách package xếp theo bảng chữ
        // cái ⇒ dòng paddlepaddle* có thể bị cắt mất giữa. Detection chỉ dựa vào exit code nên output
        // rỗng vẫn ra flavor đúng.
        // Why R55 exists: ProcessRunner keeps only the last ~8 KB of output and package lists are
        // alphabetical ⇒ the paddlepaddle* lines can be cut out in the middle. Detection reads exit codes
        // only, so even an output window without those lines still yields the right flavor.
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        string tailWithoutPaddle = new string('x', 8 * 1024) + "\nzope.interface 7.2.1\n";
        fake.Results.Enqueue((0, tailWithoutPaddle));
        Assert.Equal(OcrFlavor.Gpu, await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        fake.Results.Enqueue((1, tailWithoutPaddle));
        fake.Results.Enqueue((0, tailWithoutPaddle));
        Assert.Equal(OcrFlavor.Cpu, await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));
    }

    // ── Ctor fallback (R47: default là null, thân ctor lấy giá trị SetupPaths) ─────────────────────

    [Fact]
    public async Task Ctor_NullUvExeAndOcrDir_FallBackToSetupPaths()
    {
        // R47: `SetupPaths.UvExe`/`OcrDir` là static property nên không hợp lệ làm giá trị mặc định
        // trong signature ⇒ ctor nhận null rồi tự resolve. InstalledPaddleFlavorAsync không đụng
        // pyproject/đĩa (chỉ gọi runner) ⇒ quan sát được fallback mà không động tới cây OCR thật.
        // R47: those SetupPaths members are static properties, illegal as signature defaults ⇒ the ctor
        // takes null and resolves it. InstalledPaddleFlavorAsync touches no pyproject/disk (it only
        // calls the runner), so the fallback is observable without touching the repo's real OCR tree.
        var uvOnly = new FakeProcessRunner();
        await new UvSyncRunner(uvOnly, ocrDir: _ocrDir).InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken);
        Call uvFallback = Assert.Single(uvOnly.Calls);
        Assert.Equal(SetupPaths.UvExe, uvFallback.FileName);
        Assert.Equal(_ocrDir, uvFallback.WorkingDir);

        var ocrOnly = new FakeProcessRunner();
        await new UvSyncRunner(ocrOnly, UvExe).InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken);
        Call ocrFallback = Assert.Single(ocrOnly.Calls);
        Assert.Equal(UvExe, ocrFallback.FileName);
        Assert.Equal(SetupPaths.OcrDir, ocrFallback.WorkingDir);

        Assert.Throws<ArgumentNullException>("runner", () => new UvSyncRunner(null!));
    }

    // ── Packaging bất biến (Amendment A2) — đọc pyproject/uv.lock THẬT của repo ───────────────────

    [Fact]
    public void Packaging_DependencyGroups_PinExactlyTheMeasuredFlavours()
    {
        string? toml = RepoPyprojectOrNull();
        if (toml is null) return;

        // Nhóm gpu: ĐÚNG 3 pin đã đo (paddle wheel + cudnn + cublas bản paddle khai báo), không hơn không
        // kém — thêm pin nvidia nào là +GB tải về vô ích (Amendment A2).
        // The gpu group: EXACTLY the three pins that were measured (the paddle wheel + cudnn + the cublas
        // build paddle declares), no more — any extra nvidia pin is a pointless GB of download.
        Assert.Equal(
            ["paddlepaddle-gpu==3.4.0", "nvidia-cudnn-cu12==9.9.0.52", "nvidia-cublas-cu12==12.9.0.13"],
            GroupEntries(toml, "gpu"));

        // Nhóm cpu: đúng wheel paddle CPU 3.4.0 (PyPI chỉ tới 3.3.1 cho cp313/win_amd64).
        // The cpu group: exactly the 3.4.0 CPU paddle wheel (PyPI only reaches 3.3.1 for cp313/win_amd64).
        Assert.Equal(["paddlepaddle==3.4.0"], GroupEntries(toml, "cpu"));

        // dev phải còn để `uv sync` / `uv run pytest` của máy dev giữ nguyên hành vi (default-groups).
        // dev must survive so dev machines keep their `uv sync` / `uv run pytest` behaviour (default-groups).
        Assert.Contains("pytest>=9.1.1", GroupEntries(toml, "dev"));
    }

    [Fact]
    public void Packaging_ProjectDependencies_CarryNoFlavourPin()
    {
        // [project].dependencies là phần KHÔNG phụ thuộc flavor. Pin paddle/nvidia ở đây sẽ đi vào cả hai
        // nhóm resolve và vô hiệu `conflicts` (Amendment A2).
        // [project].dependencies is the flavour-independent part. A paddle/nvidia pin there would land in
        // BOTH group resolutions and defeat `conflicts` (Amendment A2).
        string? toml = RepoPyprojectOrNull();
        if (toml is null) return;

        Assert.Equal(["numpy>=1.24,<2.4", "paddleocr>=3.7.0", "pillow>=12.3.0"],
            ArrayEntries(SectionLines(toml, "[project]"), "dependencies"));

        foreach (string entry in ArrayEntries(SectionLines(toml, "[project]"), "dependencies"))
        {
            Assert.False(
                entry.StartsWith("paddlepaddle", StringComparison.OrdinalIgnoreCase)
                || entry.StartsWith("nvidia-", StringComparison.OrdinalIgnoreCase),
                $"dependency flavor nằm trong [project]: {entry}");
        }
    }

    [Fact]
    public void Packaging_ToolUv_DeclaresConflictsDefaultGroupsAndTwoIndexes()
    {
        string? toml = RepoPyprojectOrNull();
        if (toml is null) return;

        List<string> toolUv = SectionLines(toml, "[tool.uv]");
        // conflicts: uv chỉ từ chối đồng thời hai nhóm khi được khai báo — đây là thứ khiến `--group cpu`
        // không bao giờ cài chồng wheel GPU lên môi trường đang chạy.
        // conflicts: uv only refuses the two groups together when declared — this is what stops `--group
        // cpu` from layering the GPU wheel onto a running environment.
        string conflicts = toolUv.FirstOrDefault(static line =>
            line.Trim().StartsWith("conflicts", StringComparison.Ordinal)) ?? string.Empty;
        Assert.Contains("\"gpu\"", conflicts, StringComparison.Ordinal);
        Assert.Contains("\"cpu\"", conflicts, StringComparison.Ordinal);

        string defaults = toolUv.FirstOrDefault(static line =>
            line.Trim().StartsWith("default-groups", StringComparison.Ordinal)) ?? string.Empty;
        Assert.Contains("\"dev\"", defaults, StringComparison.Ordinal);
        Assert.Contains("\"gpu\"", defaults, StringComparison.Ordinal);

        // Hai index paddle, cả hai explicit; sources trỏ đúng từng distribution sang index của nó.
        // Two paddle indexes, both explicit; the sources point each distribution at its own index.
        List<string> indexes = SectionLines(toml, "[[tool.uv.index]]");
        Assert.Equal(2, indexes.Count(static line => line.Trim() == "explicit = true"));
        Assert.Contains("paddle-cu129", IndexNames(indexes));
        Assert.Contains("paddle-cpu", IndexNames(indexes));
        Assert.True(indexes.Any(static line => line.Contains("/stable/cu129/", StringComparison.Ordinal)),
            "thiếu url index cu129");
        Assert.True(indexes.Any(static line => line.Contains("/stable/cpu/", StringComparison.Ordinal)),
            "thiếu url index cpu");

        List<string> sources = SectionLines(toml, "[tool.uv.sources]");
        Assert.Equal(
            ["paddlepaddle-gpu = { index = \"paddle-cu129\" }", "paddlepaddle = { index = \"paddle-cpu\" }"],
            sources.Select(static line => line.Trim()).ToArray());
    }

    [Fact]
    public void Packaging_ShippedLockCarriesBothPaddleDistributions()
    {
        // `uv sync --frozen` (đường wizard đi) KHÔNG resolve lại: mọi thứ flavor cần phải có sẵn trong
        // uv.lock ship kèm. Thiếu một trong hai distribution là wizard chết trên máy sạch.
        // `uv sync --frozen` (the wizard's path) does NOT re-resolve: everything a flavour needs must
        // already be in the shipped uv.lock. A missing distribution kills the wizard on a clean machine.
        string? root = OcrBootstrap.FindRepoRoot(AppContext.BaseDirectory);
        if (root is null) return;

        string lockPath = Path.Combine(root, "ReTran.OCR", "uv.lock");
        if (!File.Exists(lockPath))
        {
            Assert.Fail($"thiếu {lockPath} — wizard chạy `uv sync --frozen` nên lock phải được ship.");
            return;
        }

        string lockText = File.ReadAllText(lockPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("name = \"paddlepaddle-gpu\"\nversion = \"3.4.0\"", lockText, StringComparison.Ordinal);
        Assert.Contains("name = \"paddlepaddle\"\nversion = \"3.4.0\"", lockText, StringComparison.Ordinal);
        Assert.Contains("www.paddlepaddle.org.cn/packages/stable/cu129/", lockText, StringComparison.Ordinal);
        Assert.Contains("www.paddlepaddle.org.cn/packages/stable/cpu/", lockText, StringComparison.Ordinal);
        // Bản cublas đã đo thật (không phải bản suy ra từ cudnn) phải là bản được chốt.
        // The cublas build actually measured (not the one implied by cudnn) must be the pinned one.
        Assert.Contains("name = \"nvidia-cublas-cu12\"\nversion = \"12.9.0.13\"", lockText, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the repo's real <c>ReTran.OCR/pyproject.toml</c>, or null outside the repo layout (e.g. a
    /// packaged build) — the same polite skip gate <see cref="OcrCliTests"/> uses.
    /// Đọc <c>ReTran.OCR/pyproject.toml</c> thật của repo, hoặc null khi ngoài layout repo (vd build đóng
    /// gói) — đúng gate bỏ qua lịch sự mà <see cref="OcrCliTests"/> dùng.
    /// </summary>
    private static string? RepoPyprojectOrNull()
    {
        string? root = OcrBootstrap.FindRepoRoot(AppContext.BaseDirectory);
        if (root is null) return null;

        string path = Path.Combine(root, "ReTran.OCR", "pyproject.toml");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// Raw non-blank lines owned by a TOML header (<c>[[tool.uv.index]]</c> repeats per array element), empty
    /// when the file has no such section. Line comments stay in — the entry parsers skip them.
    /// Các dòng thô không trống thuộc một header TOML (<c>[[tool.uv.index]]</c> lặp lại theo phần tử array),
    /// rỗng khi file không có section. Dòng comment vẫn nằm lại — parser entry sẽ bỏ qua chúng.
    /// </summary>
    private static List<string> SectionLines(string toml, string header)
    {
        List<string> lines = [];
        bool inSection = false;
        foreach (string raw in toml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = raw.Trim();
            if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                inSection = trimmed == header;
                continue;
            }

            if (inSection && trimmed.Length > 0) lines.Add(raw);
        }

        return lines;
    }

    /// <summary>
    /// Quoted entries of a multi-line array <c>key = [ ... ]</c> inside a section, comments skipped.
    /// Entry có dấu bao của mảng nhiều dòng <c>key = [ ... ]</c> trong một section, bỏ qua comment.
    /// </summary>
    private static List<string> ArrayEntries(List<string> sectionLines, string key)
    {
        List<string> entries = [];
        bool inArray = false;
        foreach (string raw in sectionLines)
        {
            string trimmed = raw.Trim();
            if (!inArray)
            {
                int equals = trimmed.IndexOf('=');
                if (equals < 0 || trimmed[..equals].Trim() != key) continue;
                if (!trimmed.EndsWith("[", StringComparison.Ordinal)) continue;
                inArray = true;
                continue;
            }

            if (trimmed.StartsWith("]")) break;
            if (trimmed.StartsWith("#")) continue;
            if (Quoted(trimmed) is string entry) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Entries of one PEP 735 dependency group in <c>[dependency-groups]</c>.
    /// Entry của một dependency group PEP 735 trong <c>[dependency-groups]</c>.
    /// </summary>
    private static List<string> GroupEntries(string toml, string group)
        => ArrayEntries(SectionLines(toml, "[dependency-groups]"), group);

    /// <summary>The <c>name = "..."</c> values of the <c>[[tool.uv.index]]</c> blocks.</summary>
    private static List<string> IndexNames(List<string> indexLines)
        => indexLines
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("name", StringComparison.OrdinalIgnoreCase))
            .Select(static line => Quoted(line) ?? string.Empty)
            .Where(static name => name.Length > 0)
            .ToList();

    /// <summary>The quoted right-hand value of <c>key = "value"</c> / <c>"entry",</c>, null when unquoted.</summary>
    private static string? Quoted(string trimmed)
    {
        int quote = trimmed.IndexOf('"');
        if (quote < 0) return null;

        int close = trimmed.IndexOf('"', quote + 1);
        return close < 0 ? null : trimmed[(quote + 1)..close];
    }

    private static void AssertUvEnv(Call call)
    {
        Assert.True(call.Env.TryGetValue("UV_PYTHON_PREFERENCE", out string? value),
            $"thiếu UV_PYTHON_PREFERENCE: {string.Join(", ", call.Env.Keys)}");
        Assert.Equal("only-managed", value);
    }

    private sealed record Call(string FileName, string[] Args, string WorkingDir, Dictionary<string, string> Env);

    /// <summary>
    /// Records every invocation and replays scripted exit codes/output; never launches a process.
    /// Ghi lại mọi lời gọi và phát lại exit code/output đã dựng sẵn; không khởi tiến trình nào.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<Call> Calls { get; } = [];

        public Queue<(int ExitCode, string OutputTail)> Results { get; } = new();

        public Task<(int ExitCode, string OutputTail)> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string workingDir,
            IDictionary<string, string>? extraEnv,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new Call(
                fileName,
                [.. args],
                workingDir,
                extraEnv is null ? [] : new Dictionary<string, string>(extraEnv)));
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : (0, "Using CPython 3.13.2 (managed)"));
        }
    }
}

/// <summary>
/// Tests for the real <see cref="ProcessRunner"/> against harmless <c>cmd.exe</c> built-ins only — never
/// against real <c>uv</c>: exit code, merged stdout+stderr tail, extraEnv merge, tail bound, and
/// cancellation killing the child process tree.
/// Test cho <see cref="ProcessRunner"/> thật với các lệnh builtin vô hại của <c>cmd.exe</c> — tuyệt đối
/// không gọi <c>uv</c> thật: exit code, output gộp stdout+stderr (đuôi), gộp extraEnv, giới hạn đuôi, và
/// hủy lệnh thì kill cả cây tiến trình con.
/// </summary>
public class ProcessRunnerTests : IDisposable
{
    private readonly string _dir;

    public ProcessRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"retran-procrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RunAsync_ReturnsExitCode_AndCapturesStdoutAndStderr()
    {
        var runner = new ProcessRunner();

        var (exit, tail) = await runner.RunAsync(
            "cmd.exe", ["/c", "echo out-line & echo err-line 1>&2 & exit 3"], _dir, extraEnv: null, TestContext.Current.CancellationToken);

        Assert.Equal(3, exit);
        Assert.Contains("out-line", tail);
        Assert.Contains("err-line", tail);
    }

    [Fact]
    public async Task RunAsync_MergesProcessEnvironmentWithExtraEnv()
    {
        string marker = $"RETRAN_TEST_{Guid.NewGuid():N}";
        var runner = new ProcessRunner();

        var (exit, tail) = await runner.RunAsync(
            "cmd.exe",
            ["/c", $"echo inline=%{marker}% inherited=%ComSpec%"],
            _dir,
            new Dictionary<string, string> { [marker] = "abc123" },
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("inline=abc123", tail);
        // Env của process hiện tại vẫn còn đó (ProcessRunner chỉ THÊM extraEnv, không thay cả block).
        // The current process environment is still visible (ProcessRunner ADDS extraEnv, it does not
        // replace the whole block).
        Assert.Contains("inherited=", tail);
        Assert.DoesNotContain("inherited=%ComSpec%", tail);
    }

    [Fact]
    public async Task RunAsync_KeepsOnlyTheTail_ForLargeOutput()
    {
        var runner = new ProcessRunner();

        var (exit, tail) = await runner.RunAsync(
            "cmd.exe", ["/c", "for /L %i in (1,1,4000) do @echo line-%i"], _dir, extraEnv: null, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.True(tail.Length <= ProcessRunner.OutputTailLimitChars,
            $"OutputTail dài {tail.Length} > giới hạn {ProcessRunner.OutputTailLimitChars}");
        // Đuôi thật sự là PHẦN CUỐI của output (không phải đầu), và reader đã drain hết trước khi return.
        // The tail really is the END of the output (not the start), and the readers drained before return.
        Assert.EndsWith("line-4000", tail.TrimEnd('\r', '\n'));
        Assert.DoesNotContain("line-1\n", tail);
    }

    [Fact]
    public async Task RunAsync_Cancellation_KillsTreeAndThrowsOperationCanceled()
    {
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            "cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"], _dir, extraEnv: null, cts.Token));

        sw.Stop();
        // cmd + ping con chỉ kết thúc sớm nếu cây bị kill; nếu chỉ hủy token mà không kill thì ≈ 30 s.
        // cmd plus its ping child only end early when the tree is killed; cancelling the token without
        // killing would take ≈ 30 s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"cây tiến trình không bị kill (elapsed {sw.Elapsed.TotalSeconds:0.#} s)");
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_ThrowsWin32()
    {
        var runner = new ProcessRunner();

        // uv.exe bị AV/SmartScreen gỡ đi (Review Focus #3) ⇒ lỗi spawn phải nổi lên, không bị nuốt.
        // A removed/quarantined uv.exe (Review Focus #3) must surface the spawn error, never swallow it.
        await Assert.ThrowsAnyAsync<Win32Exception>(() => runner.RunAsync(
            @"C:\retran-does-not-exist\uv.exe", ["sync"], _dir, extraEnv: null, CancellationToken.None));
    }
}
