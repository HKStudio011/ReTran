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
        // File đang ở cu126 (đã mất pin cudnn) + wheel lạ ⇒ xử lý như cu129. Đích cu129 CÓ pin cudnn, nên
        // apply phải dựng lại cả index lẫn pin: kết quả chỉ phụ thuộc flavor/variant yêu cầu, không phụ thuộc
        // biến thể trước đó của file (R54 canonical). Trước R54 ở đây pin cudnn vẫn thiếu — chính là lỗi
        // CPU→GPU / cu126→cu129 install sai wheel.
        // File currently at cu126 (cudnn pin dropped) + an unlisted wheel ⇒ behave as cu129. The cu129 target
        // HAS the cudnn pin, so the apply rebuilds both the index and the pin: the result depends only on the
        // requested flavor/variant, not on the file's previous variant (R54 canonical). Before R54 the cudnn
        // pin stayed missing here — the very defect that installed the wrong wheel on CPU→GPU and
        // cu126→cu129 transitions.
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu126", _pyproject);
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu123", _pyproject);

        string text = Norm(File.ReadAllText(_pyproject));
        Assert.Equal(LfFile(PyprojectHead), text);
        Assert.Contains("name = \"paddle-cu129\"", text);
        Assert.Contains("https://www.paddlepaddle.org.cn/packages/stable/cu129/", text);
        Assert.Contains("paddlepaddle-gpu = { index = \"paddle-cu129\" }", text);
        Assert.Contains("nvidia-cudnn-cu12", text);
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

    // ── R54: apply là canonical — nội dung đích không phụ thuộc flavor trước đó ────────────────────

    [Fact]
    public void ApplyFlavor_CpuThenGpuCu126_EqualsShippedThenCu126()
    {
        // Regression chính: apply CPU xoá pin GPU + cả 2 section uv; nếu apply cu126 sau đó chỉ sửa cái
        // đang tồn tại thì file còn lại KHÔNG còn dependency GPU lẫn index ⇒ uv sync cài môi trường CPU
        // trong khi setup.json ghi Gpu/cu126. Apply phải dựng lại đủ, ra đúng byte như đi thẳng
        // từ file đi kèm sang cu126.
        // The headline regression: the CPU apply drops the GPU pin and both uv sections; a cu126 apply that
        // only edits what is still there leaves a file with NO GPU dependency and NO index ⇒ uv sync builds
        // a CPU environment while setup.json records Gpu/cu126. The apply must rebuild what is missing and
        // produce the same bytes as going straight from the shipped file to cu126.
        byte[] direct = ApplySequence((OcrFlavor.Gpu, "cu126"));
        byte[] viaCpu = ApplySequence((OcrFlavor.Cpu, null), (OcrFlavor.Gpu, "cu126"));

        Assert.Equal(direct, viaCpu);
        string text = Text(viaCpu);
        Assert.Equal(LfFile(ExpectedVariant("cu126")), text);
        // Guard chuỗi CPU→GPU cụ thể mà wizard đi (spec §5.4 "user đổi card").
        Assert.Contains("paddlepaddle-gpu==3.4.0", text);
        Assert.DoesNotContain("nvidia-cudnn-cu12", text);
        Assert.Contains("name = \"paddle-cu126\"", text);
    }

    [Fact]
    public void ApplyFlavor_CpuThenGpuCu129_RestoresTheShippedFileByteForByte()
    {
        // Đường repair/updater: updater ghi đè pyproject đi kèm (cu129) rồi wizard áp lại flavor; nếu máy
        // từng bị hạ xuống CPU, apply cu129 phải dựng lại từng byte của file đi kèm.
        // Updater/repair path: the updater restores the shipped (cu129) pyproject and the wizard re-applies
        // the flavor; on a machine that was dropped to CPU, the cu129 apply must rebuild the shipped file
        // byte for byte.
        byte[] shipped = WriteSeqFile(Head(crlf: true));
        byte[] restored = ApplySequence((OcrFlavor.Cpu, null), (OcrFlavor.Gpu, "cu129"));

        Assert.Equal(shipped, restored);
        Assert.Equal(LfFile(PyprojectHead), Text(restored));
    }

    [Fact]
    public void ApplyFlavor_GpuVariantSwitch_Cu126ToCu130ToCu126()
    {
        // Đổi wheel qua lại phải hội tụ về cùng một nội dung, và pin cudnn chỉ có ở cu129.
        // Switching wheels back and forth must converge on one content, and the cudnn pin belongs to
        // cu129 only.
        byte[] expected = ApplySequence((OcrFlavor.Gpu, "cu126"));
        byte[] switched = ApplySequence((OcrFlavor.Gpu, "cu126"), (OcrFlavor.Gpu, "cu130"), (OcrFlavor.Gpu, "cu126"));
        Assert.Equal(expected, switched);

        string cu130 = Text(ApplySequence((OcrFlavor.Gpu, "cu126"), (OcrFlavor.Gpu, "cu130")));
        Assert.Equal(LfFile(ExpectedVariant("cu130")), cu130);
        Assert.DoesNotContain("nvidia-cudnn-cu12", cu130);

        string cu126 = Text(switched);
        Assert.DoesNotContain("nvidia-cudnn-cu12", cu126);
        Assert.Contains("name = \"paddle-cu126\"", cu126);

        // cu129 là biến thể duy nhất có pin cudnn — quay lại cu129 phải thêm lại nó.
        // cu129 is the only variant with the cudnn pin — returning to cu129 must add it back.
        string backTo129 = Text(ApplySequence((OcrFlavor.Gpu, "cu130"), (OcrFlavor.Gpu, "cu129")));
        Assert.Contains("\"nvidia-cudnn-cu12==9.9.0.52\",", backTo129);
        Assert.Equal(LfFile(PyprojectHead), backTo129);
    }

    [Fact]
    public void ApplyFlavor_GpuAfterCpu_KeepsGpuDependencyAndIndexSections()
    {
        // Kiểm NỘI DUNG (không chỉ "file đã đổi"): dependency GPU, khối index với url đúng, và entry
        // sources phải có mặt, đúng thứ tự canonical của file đi kèm.
        // Assert CONTENT (not merely "the file changed"): the GPU dependency, the index block with the
        // right url, and the sources entry must be present, in the shipped file's canonical order.
        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Cpu, cudaWheel: null, _pyproject);
        Assert.DoesNotContain("paddlepaddle-gpu", File.ReadAllText(_pyproject));

        UvSyncRunner.ApplyFlavorToPyproject(OcrFlavor.Gpu, "cu129", _pyproject);

        string text = File.ReadAllText(_pyproject);
        Assert.Contains("\"paddlepaddle-gpu==3.4.0\",", text);
        Assert.Contains("\"nvidia-cudnn-cu12==9.9.0.52\",", text);
        Assert.Contains("[[tool.uv.index]]", text);
        Assert.Contains("name = \"paddle-cu129\"", text);
        Assert.Contains("url = \"https://www.paddlepaddle.org.cn/packages/stable/cu129/\"", text);
        Assert.Contains("explicit = true", text);
        Assert.Contains("[tool.uv.sources]", text);
        Assert.Contains("paddlepaddle-gpu = { index = \"paddle-cu129\" }", text);

        // Thứ tự canonical: paddleocr → gpu → cudnn → pillow; index trước sources.
        int paddleocr = text.IndexOf("\"paddleocr", StringComparison.Ordinal);
        int gpu = text.IndexOf("\"paddlepaddle-gpu", StringComparison.Ordinal);
        int cudnn = text.IndexOf("\"nvidia-cudnn-cu12", StringComparison.Ordinal);
        int pillow = text.IndexOf("\"pillow", StringComparison.Ordinal);
        Assert.True(paddleocr < gpu && gpu < cudnn && cudnn < pillow,
            $"thứ tự dependency không canonical: paddleocr={paddleocr} gpu={gpu} cudnn={cudnn} pillow={pillow}");
        Assert.True(text.IndexOf("[[tool.uv.index]]", StringComparison.Ordinal)
                    < text.IndexOf("[tool.uv.sources]", StringComparison.Ordinal));

        // Phần còn lại của file không được đụng tới.
        foreach (string keep in new[]
                 {
                     "# numpy<2.4: forced by paddlex>=3.7.0 (required by paddleocr>=3.7.0);",
                     "\"numpy>=1.24,<2.4\",", "\"pillow>=12.3.0\",", "[build-system]",
                     "[tool.hatch.build.targets.wheel]", "[dependency-groups]", "\"pytest>=9.1.1\",",
                     "[tool.pytest.ini_options]", "testpaths = [\"tests\"]",
                 })
        {
            Assert.Contains(keep, text);
        }
        AssertCrlfPreserved(_pyproject);
    }

    [Theory]
    [InlineData(OcrFlavor.Cpu, null)]
    [InlineData(OcrFlavor.Gpu, "cu126")]
    [InlineData(OcrFlavor.Gpu, "cu129")]
    [InlineData(OcrFlavor.Gpu, "cu130")]
    public void ApplyFlavor_Idempotent_ForEveryFlavorAndVariant(OcrFlavor flavor, string? wheel)
    {
        // Mọi tổ hợp flavor/variant: áp nhiều lần từ file đi kèm không đổi thêm byte nào, và đi đường vòng
        // (qua flavor/variant khác) rồi áp lại cũng ra đúng cùng nội dung — tính canonical của R54.
        // Every flavor/variant combination: re-applying on the shipped file adds no bytes, and taking a
        // detour through another flavor/variant then re-applying lands on the same content — R54's
        // canonicality claim.
        byte[] once = ApplySequence((flavor, wheel));
        byte[] twice = ApplySequence((flavor, wheel), (flavor, wheel));
        byte[] thrice = ApplySequence((flavor, wheel), (flavor, wheel), (flavor, wheel));
        Assert.Equal(once, twice);
        Assert.Equal(twice, thrice);

        (OcrFlavor, string?)[] detour = flavor == OcrFlavor.Cpu
            ? [(OcrFlavor.Gpu, "cu126"), (flavor, wheel)]
            : [(OcrFlavor.Cpu, null), (flavor, wheel), (flavor, wheel),
               (OcrFlavor.Gpu, "cu130"), (flavor, wheel)];
        byte[] viaDetour = ApplySequence(detour);
        Assert.Equal(once, viaDetour);
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
        Assert.Equal(1, await runner.SyncAsync(OcrFlavor.Gpu, "cu129", TestContext.Current.CancellationToken));
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

    /// <summary>
    /// Writes <paramref name="content"/> to its own file in this test's temp tree and returns the bytes on
    /// disk — the same writer the fixture uses, so the comparison has no BOM/encoding surprises.
    /// Ghi <paramref name="content"/> ra file riêng trong cây temp của test và trả byte trên đĩa — dùng
    /// đúng writer như fixture nên phép so sánh không dính BOM/encoding.
    /// </summary>
    private byte[] WriteSeqFile(string content)
    {
        string path = Path.Combine(_dir, $"seq-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, content);
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Applies a flavor/wheel sequence to a fresh copy of the HEAD fixture in its own file and returns the
    /// resulting bytes, so every canonicality case is independent of the shared fixture file.
    /// Áp một chuỗi flavor/wheel lên bản copy của fixture HEAD trong file riêng và trả byte kết quả, để mỗi
    /// case canonical không phụ thuộc file fixture dùng chung.
    /// </summary>
    private byte[] ApplySequence(params (OcrFlavor Flavor, string? Wheel)[] applies)
    {
        string path = Path.Combine(_dir, $"seq-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, Head(crlf: true));
        foreach ((OcrFlavor flavor, string? wheel) in applies)
        {
            UvSyncRunner.ApplyFlavorToPyproject(flavor, wheel, path);
        }

        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Result bytes as normalized LF file text — the shape the fixture constants are compared in.
    /// Byte kết quả dưới dạng text file LF đã chuẩn hoá — hình dạng để so với các constant fixture.
    /// </summary>
    private static string Text(byte[] bytes) => Norm(Encoding.UTF8.GetString(bytes));

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
