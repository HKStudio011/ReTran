using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ReTran.Core.Ocr;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="UvSyncRunner"/>: the text-level rewrite of <c>pyproject.toml</c> per wheel flavor
/// and CUDA variant, the exact uv command sequence (working dir, env, order, first non-zero exit code,
/// output tail kept for the error screen), and the installed-flavor probe.
/// Test cho <see cref="UvSyncRunner"/>: rewrite dạng text của <c>pyproject.toml</c> theo flavor và biến
/// thể CUDA, đúng sequence lệnh uv (working dir, env, thứ tự, exit code khác 0 đầu tiên, giữ đuôi output
/// cho màn hình lỗi), và probe flavor đã cài.
/// </summary>
/// <remarks>
/// Every fixture is a COPY of the repo's <c>ReTran.OCR/pyproject.toml</c> (content as of this commit,
/// CRLF, no BOM) written into a throwaway temp directory. The suite must never read or write the repo's
/// own <c>pyproject.toml</c>/<c>uv.lock</c>, and never invokes real <c>uv</c> — real <c>uv sync</c>
/// measurement belongs to the clean-machine smoke, not here.
/// Mọi fixture là bản COPY nội dung <c>ReTran.OCR/pyproject.toml</c> của repo (tại commit này, CRLF,
/// không BOM) ghi vào thư mục temp. Suite không được đọc/ghi <c>pyproject.toml</c> và <c>uv.lock</c> của
/// repo, và không gọi <c>uv</c> thật — đo <c>uv sync</c> thật thuộc smoke trên máy sạch, không ở đây.
/// </remarks>
public class UvSyncRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _ocrDir;
    private readonly string _pyproject;

    /// <summary>Path of a uv.exe that does not exist: the fake runner never launches it.</summary>
    private const string UvExe = @"C:\retran-test-fake\runtimes\win-x64\uv.exe";

    /// <summary>
    /// Byte-for-byte copy of the repo's <c>ReTran.OCR/pyproject.toml</c> at HEAD: the cu129 GPU pin
    /// (<c>paddlepaddle-gpu==3.4.0</c>), the <c>nvidia-cudnn-cu12</c> pin, and the
    /// <c>[[tool.uv.index]]</c> + <c>[tool.uv.sources]</c> sections the rewrite must handle.
    /// Bản copy y hệt byte của <c>ReTran.OCR/pyproject.toml</c> của repo tại HEAD: pin GPU cu129
    /// (<c>paddlepaddle-gpu==3.4.0</c>), pin <c>nvidia-cudnn-cu12</c>, cùng section
    /// <c>[[tool.uv.index]]</c> và <c>[tool.uv.sources]</c> mà bản rewrite phải xử lý.
    /// </summary>
    private const string PyprojectHead = """
        [project]
        name = "retran-core"
        version = "0.1.0"
        description = ""
        requires-python = ">=3.13"
        dependencies = [
            # numpy<2.4: forced by paddlex>=3.7.0 (required by paddleocr>=3.7.0);
            # newer numpy would make the PP-OCRv6 stack unresolvable.
            "numpy>=1.24,<2.4",
            "paddleocr>=3.7.0",
            "paddlepaddle-gpu==3.4.0",
            "nvidia-cudnn-cu12==9.9.0.52",
            "pillow>=12.3.0",
        ]

        [build-system]
        requires = ["hatchling"]
        build-backend = "hatchling.build"

        [tool.hatch.build.targets.wheel]
        packages = ["src/retran_core"]

        [dependency-groups]
        dev = [
            "pytest>=9.1.1",
        ]

        [tool.pytest.ini_options]
        pythonpath = ["src"]
        testpaths = ["tests"]

        [[tool.uv.index]]
        name = "paddle-cu129"
        url = "https://www.paddlepaddle.org.cn/packages/stable/cu129/"
        explicit = true

        [tool.uv.sources]
        paddlepaddle-gpu = { index = "paddle-cu129" }
        """;

    /// <summary>
    /// Expected CPU result: both GPU pins gone, both uv sections gone, every other dependency and
    /// section untouched (content pinned in full so an accidental extra edit fails).
    /// Nội dung CPU mong đợi: bỏ cả 2 pin GPU, bỏ cả 2 section uv, mọi dependency và section khác nguyên
    /// vẹn (nội dung ghim toàn bộ để sửa ngoài ý muốn là fail).
    /// </summary>
    private const string PyprojectCpuExpected = """
        [project]
        name = "retran-core"
        version = "0.1.0"
        description = ""
        requires-python = ">=3.13"
        dependencies = [
            # numpy<2.4: forced by paddlex>=3.7.0 (required by paddleocr>=3.7.0);
            # newer numpy would make the PP-OCRv6 stack unresolvable.
            "numpy>=1.24,<2.4",
            "paddleocr>=3.7.0",
            "pillow>=12.3.0",
        ]

        [build-system]
        requires = ["hatchling"]
        build-backend = "hatchling.build"

        [tool.hatch.build.targets.wheel]
        packages = ["src/retran_core"]

        [dependency-groups]
        dev = [
            "pytest>=9.1.1",
        ]

        [tool.pytest.ini_options]
        pythonpath = ["src"]
        testpaths = ["tests"]
        """;

    public UvSyncRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"retran-uvsync-{Guid.NewGuid():N}");
        _ocrDir = Path.Combine(_dir, "ReTran.OCR");
        _pyproject = Path.Combine(_ocrDir, "pyproject.toml");
        Directory.CreateDirectory(_ocrDir);
        WriteHead(crlf: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── ApplyFlavorToPyproject ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyFlavor_Cpu_StripsGpuPinsAndIndexSections()
    {
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Cpu, cudaWheel: null, _pyproject);

        string text = File.ReadAllText(_pyproject);
        Assert.Equal(LfFile(PyprojectCpuExpected), Norm(text));
        // Cấu trúc còn lại phải còn nguyên: deps khác, build-system, hatch, dev group, pytest.
        foreach (string keep in new[]
                 {
                     "\"numpy>=1.24,<2.4\",", "\"paddleocr>=3.7.0\",", "\"pillow>=12.3.0\",",
                     "[build-system]", "[tool.hatch.build.targets.wheel]", "[dependency-groups]",
                     "\"pytest>=9.1.1\",", "[tool.pytest.ini_options]", "testpaths = [\"tests\"]",
                 })
        {
            Assert.Contains(keep, text);
        }
        foreach (string gone in new[] { "paddlepaddle-gpu", "nvidia-cudnn", "[tool.uv.index]", "[tool.uv.sources]", "paddlepaddle.org.cn" })
        {
            Assert.DoesNotContain(gone, text);
        }
        // CRLF của file được giữ nguyên sau rewrite (repo file là CRLF).
        AssertCrlfPreserved(_pyproject);
    }

    [Fact]
    public void ApplyFlavor_Cpu_IsIdempotent()
    {
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Cpu, cudaWheel: null, _pyproject);
        string once = File.ReadAllText(_pyproject);
        byte[] onceBytes = File.ReadAllBytes(_pyproject);

        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Cpu, cudaWheel: null, _pyproject);
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Cpu, cudaWheel: null, _pyproject);

        Assert.Equal(once, File.ReadAllText(_pyproject));
        // Lần chạy lại trên file đã rewrite: không đổi một byte nào (idempotent + không ghi vô ích).
        Assert.Equal(onceBytes, File.ReadAllBytes(_pyproject));
    }

    [Fact]
    public void ApplyFlavor_GpuCu129_IsNoOp()
    {
        byte[] before = File.ReadAllBytes(_pyproject);

        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu129", _pyproject);

        // So sánh byte: ghim luôn rằng file không bị đổi xuống dòng hay mất xuống dòng cuối.
        Assert.Equal(before, File.ReadAllBytes(_pyproject));
        Assert.Equal(LfFile(PyprojectHead), Norm(File.ReadAllText(_pyproject)));
        AssertCrlfPreserved(_pyproject);

        // cudaWheel null ⇒ coi như "cu129" (brief) ⇒ cũng no-op trên file HEAD.
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, cudaWheel: null, _pyproject);
        Assert.Equal(before, File.ReadAllBytes(_pyproject));
    }

    [Fact]
    public void ApplyFlavor_GpuCu126_RewritesIndexDropsCudnnPin()
    {
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu126", _pyproject);

        Assert.Equal(LfFile(ExpectedVariant("cu126")), Norm(File.ReadAllText(_pyproject)));
        AssertCrlfPreserved(_pyproject);
    }

    [Fact]
    public void ApplyFlavor_GpuCu130_RewritesIndexDropsCudnnPin()
    {
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu130", _pyproject);

        Assert.Equal(LfFile(ExpectedVariant("cu130")), Norm(File.ReadAllText(_pyproject)));
        AssertCrlfPreserved(_pyproject);
    }

    [Fact]
    public void ApplyFlavor_Gpu_IsIdempotent()
    {
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu126", _pyproject);
        byte[] once = File.ReadAllBytes(_pyproject);

        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu126", _pyproject);

        Assert.Equal(once, File.ReadAllBytes(_pyproject));
    }

    [Fact]
    public void ApplyFlavor_GpuUnknownWheel_TreatedAsCu129()
    {
        // File đang ở cu126 (không còn pin cudnn) + wheel lạ ⇒ xử lý như cu129: index quay về cu129;
        // rule chỉ BỎ pin cudnn (cu126/cu130) nên pin không được thêm lại — hành vi đã chốt.
        // File currently at cu126 (cudnn pin dropped) + an unknown wheel ⇒ behave as cu129: the index
        // returns to cu129; the rule only DROPS the cudnn pin (cu126/cu130), so it is not re-added.
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu126", _pyproject);
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu123", _pyproject);

        string text = Norm(File.ReadAllText(_pyproject));
        Assert.Contains("name = \"paddle-cu129\"", text);
        Assert.Contains("https://www.paddlepaddle.org.cn/packages/stable/cu129/", text);
        Assert.Contains("paddlepaddle-gpu = { index = \"paddle-cu129\" }", text);
        Assert.DoesNotContain("nvidia-cudnn-cu12", text);
        Assert.Contains("paddlepaddle-gpu==3.4.0", text);
    }

    [Fact]
    public void Fixture_MatchesRepoPyproject_AsLongAsRepoLayoutPresent()
    {
        // Drift guard: fixture phải là bản copy đúng của pyproject repo. FindRepoRoot trả null khi
        // chạy ngoài layout repo (vd build đóng gói) ⇒ bỏ qua, giống gate của OcrCliTests.
        // Drift guard: the fixture must be a faithful copy of the repo pyproject. FindRepoRoot returns
        // null outside the repo layout (e.g. a packaged build) ⇒ skip, same gate as OcrCliTests.
        string? root = OcrBootstrap.FindRepoRoot(AppContext.BaseDirectory);
        if (root is null) return;

        string repoText = File.ReadAllText(Path.Combine(root, "ReTran.OCR", "pyproject.toml"));
        Assert.Equal(LfFile(PyprojectHead), Norm(repoText));
    }

    [Fact]
    public void ApplyFlavor_PreservesBomAndLfOnlyNewline()
    {
        // File LF-only + UTF-8 BOM (khác định dạng repo file) ⇒ rewrite phải giữ đúng cả hai.
        // LF-only file with a UTF-8 BOM (unlike the repo file): the rewrite must keep both. GetBytes does
        // not emit the preamble, so the BOM bytes are written explicitly.
        File.WriteAllBytes(_pyproject, [.. new UTF8Encoding(true).GetPreamble(), .. new UTF8Encoding(false).GetBytes(LfFile(PyprojectHead))]);

        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Cpu, cudaWheel: null, _pyproject);

        byte[] bytes = File.ReadAllBytes(_pyproject);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            $"BOM bị mất: {Convert.ToHexString(bytes.AsSpan(0, 3))}");
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(LfFile(PyprojectCpuExpected), Norm(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)));
    }

    // ── SyncAsync ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_Gpu_RunsRewriteThenSyncOnly()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        int exit = await runner.SyncAsync(OcrFlavor.Gpu, "cu126", TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        // GPU ⇒ ĐÚNG một lệnh "sync" — không có uv pip install, không có cờ nào thêm (R49).
        // GPU ⇒ EXACTLY one "sync" command — no uv pip install, no extra flags (R49).
        Call call = Assert.Single(fake.Calls);
        Assert.Equal(UvExe, call.FileName);
        Assert.Equal(new[] { "sync" }, call.Args);
        Assert.Equal(_ocrDir, call.WorkingDir);
        AssertUvEnv(call);
        // Bước rewrite đã chạy trên đĩa trước khi sync: index trỏ cu126, pin cudnn bị bỏ.
        Assert.Contains("name = \"paddle-cu126\"", File.ReadAllText(_pyproject));
        Assert.DoesNotContain("nvidia-cudnn-cu12", File.ReadAllText(_pyproject));
        // Thành công ⇒ không để lại đuôi output cũ (màn hình lỗi không được báo tail stale).
        Assert.Null(runner.LastOutputTail);
    }

    [Fact]
    public async Task SyncAsync_Cpu_RunsRewriteSyncAndCpuWheelInstall()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        int exit = await runner.SyncAsync(OcrFlavor.Cpu, cudaWheel: null, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(new[] { "sync" }, fake.Calls[0].Args);
        Assert.Equal(new[] { "pip", "install", "paddlepaddle==3.4.0" }, fake.Calls[1].Args);
        foreach (Call call in fake.Calls)
        {
            Assert.Equal(UvExe, call.FileName);
            Assert.Equal(_ocrDir, call.WorkingDir);
            AssertUvEnv(call);
        }
        // pyproject trên đĩa đã ở dạng CPU trước khi sync (không còn pin GPU/index).
        Assert.Equal(LfFile(PyprojectCpuExpected), Norm(File.ReadAllText(_pyproject)));
    }

    [Fact]
    public async Task SyncAsync_ReturnsNonZeroExit_WhenUvFails_AndKeepsOutputTail()
    {
        var fake = new FakeProcessRunner();
        const string tail = "error: request to pypi.org/simple failed (proxy refused connection)";
        fake.Results.Enqueue((1, tail));

        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);
        int exit = await runner.SyncAsync(OcrFlavor.Cpu, cudaWheel: null, TestContext.Current.CancellationToken);

        // Exit code của lệnh fail đầu tiên được trả về, và ĐUÔI OUTPUT của lệnh đó phải còn đọc được
        // (Review Focus #4 — màn hình lỗi hiển thị đuôi output của uv). Lệnh sau không được chạy.
        // The FIRST failing command's exit code is returned and its OUTPUT TAIL stays readable (Review
        // Focus #4 — the error screen shows uv's tail). The next command must not run.
        Assert.Equal(1, exit);
        Call only = Assert.Single(fake.Calls);
        Assert.Equal(new[] { "sync" }, only.Args);
        Assert.Equal(tail, runner.LastOutputTail);

        // Lệnh thứ hai fail ⇒ exit code + tail của CHÍNH lệnh đó (không phải của lệnh đã thành công).
        fake.Results.Clear();
        fake.Results.Enqueue((0, "sync ok"));
        fake.Results.Enqueue((2, "pip boom"));
        int secondExit = await runner.SyncAsync(OcrFlavor.Cpu, cudaWheel: null, TestContext.Current.CancellationToken);
        Assert.Equal(2, secondExit);
        Assert.Equal("pip boom", runner.LastOutputTail);
        Assert.Equal(3, fake.Calls.Count);

        // Attempt kế tiếp thành công ⇒ tail stale phải bị xoá (retry ×3 không được báo lỗi của lần trước).
        fake.Results.Clear();
        int retryExit = await runner.SyncAsync(OcrFlavor.Cpu, cudaWheel: null, TestContext.Current.CancellationToken);
        Assert.Equal(0, retryExit);
        Assert.Null(runner.LastOutputTail);
    }

    // ── InstalledPaddleFlavorAsync ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstalledPaddleFlavor_ParsesGpuCpuAndNone()
    {
        var fake = new FakeProcessRunner();
        var runner = new UvSyncRunner(fake, UvExe, _ocrDir);

        fake.Results.Enqueue((0, "Package          Version\n" +
                                 "---------------- -------\n" +
                                 "numpy            2.3.2\n" +
                                 "paddleocr        3.7.0\n" +
                                 "paddlepaddle-gpu 3.4.0\n" +
                                 "pillow           12.3.0\n"));
        Assert.Equal(OcrFlavor.Gpu, await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        fake.Results.Enqueue((0, "Package  Version\npaddlepaddle 3.4.0\npillow 12.3.0\n"));
        Assert.Equal(OcrFlavor.Cpu, await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        fake.Results.Enqueue((0, ""));
        Assert.Null(await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        // uv fail (venv chưa có) ⇒ null, không được đoán flavor.
        fake.Results.Enqueue((2, "error: no virtualenv found"));
        Assert.Null(await runner.InstalledPaddleFlavorAsync(TestContext.Current.CancellationToken));

        Assert.Equal(4, fake.Calls.Count);
        Assert.Equal(new[] { "pip", "list" }, fake.Calls[0].Args);
        foreach (Call call in fake.Calls)
        {
            Assert.Equal(_ocrDir, call.WorkingDir);
            AssertUvEnv(call);
        }
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

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Expected pyproject content for a GPU variant: the HEAD file minus the <c>nvidia-cudnn-cu12</c>
    /// pin, with the index name/url and the sources entry pointed at <c>paddle-&lt;variant&gt;</c>.
    /// Nội dung pyproject mong đợi cho biến thể GPU: file HEAD bỏ pin <c>nvidia-cudnn-cu12</c>, index
    /// name/url và mục sources trỏ <c>paddle-&lt;variant&gt;</c>.
    /// </summary>
    private static string ExpectedVariant(string variant)
        => Norm(PyprojectHead)
            .Replace("name = \"paddle-cu129\"", $"name = \"paddle-{variant}\"", StringComparison.Ordinal)
            .Replace("stable/cu129/", $"stable/{variant}/", StringComparison.Ordinal)
            .Replace("index = \"paddle-cu129\"", $"index = \"paddle-{variant}\"", StringComparison.Ordinal)
            .Replace("    \"nvidia-cudnn-cu12==9.9.0.52\",\n", "", StringComparison.Ordinal);

    private void WriteHead(bool crlf)
        => File.WriteAllText(_pyproject, Head(crlf));

    /// <summary>
    /// The HEAD fixture as file bytes-as-text: every line plus the final newline the repo file ends with,
    /// in CRLF or LF form.
    /// Fixture HEAD dưới dạng text của file: mọi dòng kèm xuống dòng cuối mà file repo có, dạng CRLF hoặc LF.
    /// </summary>
    private static string Head(bool crlf)
    {
        string newline = crlf ? "\r\n" : "\n";
        return Norm(PyprojectHead).Replace("\n", newline, StringComparison.Ordinal) + newline;
    }

    /// <summary>
    /// A fixture constant as complete LF file content (trailing newline included) — the shape every rewrite
    /// result is compared against.
    /// Constant fixture dưới dạng nội dung file LF hoàn chỉnh (kèm xuống dòng cuối) — hình dạng để so với mọi
    /// kết quả rewrite.
    /// </summary>
    private static string LfFile(string constant) => Norm(constant) + "\n";

    private static string Norm(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void AssertCrlfPreserved(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int lf = bytes.Count(static b => b == (byte)'\n');
        int crlf = 0;
        for (int i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && bytes[i - 1] == (byte)'\r') crlf++;
        }
        Assert.True(lf > 0, "fixture không có xuống dòng");
        Assert.Equal(lf, crlf);
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
