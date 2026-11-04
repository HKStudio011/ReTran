using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using ReTran_App.Core;
using Xunit;

namespace RetranCore.Tests;

public class SidecarProcessTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Creates the fixture and captures the output helper used to report environment skips.
    /// Tạo fixture và lấy output helper dùng để báo các nhánh skip vì môi trường.
    /// </summary>
    /// <param name="output">xUnit output helper. Output helper của xUnit.</param>
    public SidecarProcessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ResponseLine_DeliversDetachedClone_SerializableResponse()
    {
        JsonNode? delivered = null;
        string? deliveredId = null;
        bool handled = SidecarProcess.TryRouteResponseLine(
            "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"result\":{\"params\":[\"a\"]}}",
            (id, result) => { deliveredId = id; delivered = result; },
            (_, ex) => throw ex);

        Assert.True(handled);
        Assert.Equal("7", deliveredId);
        // Trước fix: node còn parent (con của envelope) → dòng dưới ném
        // InvalidOperationException "The node already has a parent."
        string line = JsonRpc.SerializeResponse(new JsonRpcResponse(deliveredId!, delivered, null));
        Assert.Contains("\"params\":[\"a\"]", line);
    }

    [Fact]
    public async Task Ping_RoundTrips_ThroughRealPythonProcess()
    {
        if (!OperatingSystem.IsWindows()) return; // sidecar spawn is Windows-focused for M0
        if (OcrBootstrap.FindRepoRoot() is not string repoRoot) return; // no repo layout -> skip
        string pythonExe = Path.Combine(repoRoot, "ReTran.OCR", ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) return; // venv not created yet (Task 4: uv sync) -> skip
        await using var sp = await SidecarProcess.StartAsync(pythonExe, repoRoot, CancellationToken.None);
        JsonNode? pong = await sp.CallAsync("sidecar.ping", null, CancellationToken.None);
        Assert.Equal("pong", pong!.GetValue<string>());
    }

    [Fact]
    public async Task Spot_InvalidImage_SurfacesSidecarErrorAsException()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (OcrBootstrap.FindRepoRoot() is not string repoRoot) return;
        string pythonExe = Path.Combine(repoRoot, "ReTran.OCR", ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) return;
        await using var sp = await SidecarProcess.StartAsync(pythonExe, repoRoot, CancellationToken.None);
        var p = new JsonObject { ["image"] = "@@@" };
        var ex = await Assert.ThrowsAsync<SidecarRpcException>(() =>
            sp.CallAsync("ocr.spot", p, CancellationToken.None));
        Assert.Equal(-32602, ex.Code);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    //
    // BuildStartInfo — the sidecar process env: the model cache lives next to the OCR project
    // so venv and models survive app updates together (spec 2026-11-03-first-run-setup §5.5).
    // BuildStartInfo — env của tiến trình sidecar: cache model nằm cạnh dự án OCR để venv và
    // model sống qua update cùng nhau.
    //

    [Fact]
    public void BuildStartInfo_SetsModelCacheEnvNextToOcrProject()
    {
        var info = SidecarProcess.BuildStartInfo("C:\\py\\python.exe", @"D:\app");
        Assert.Equal(@"D:\app\ReTran.OCR\models", info.EnvironmentVariables["PADDLE_PDX_CACHE_HOME"]);
    }

    [Fact]
    public void BuildStartInfo_KeepsWorkingDirectoryAndArgs()
    {
        var info = SidecarProcess.BuildStartInfo("C:\\py\\python.exe", @"D:\app");
        Assert.Equal(@"D:\app\ReTran.OCR", info.WorkingDirectory);
        Assert.Equal(new[] { "-m", "retran_core", "serve" }, info.ArgumentList.ToArray());
    }

    //
    // Integration (spec §7): a "clean app dir" — copied ReTran.OCR sources + a junction to the
    // repo venv, nothing else — must be enough for ReTran.Core.exe --serve to find the venv,
    // spawn the sidecar and answer ocr.models.list. Environment preconditions are skips, never
    // failures; a sidecar that cannot spawn in that layout is the regression this guards.
    // Integration (spec §7): một "app dir sạch" — nguồn ReTran.OCR đã copy + junction tới venv
    // của repo, không gì khác — phải đủ để ReTran.Core.exe --serve tìm venv, spawn sidecar và
    // trả ocr.models.list. Thiếu điều kiện môi trường là skip, không bao giờ fail; sidecar
    // không spawn được trong layout đó chính là regression test này canh.
    //

    [Fact]
    public async Task Serve_RealCore_FindsVenvInCleanAppDir()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("SKIPPED (env): junction + ReTran.Core.exe layout is Windows-only");
            return;
        }
        string? repoRoot = FindSourceRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
        {
            _output.WriteLine("SKIPPED (env): source repo root (ReTran.Core.csproj + ReTran.OCR/pyproject.toml) not found");
            return;
        }
        string ocrDir = Path.Combine(repoRoot, "ReTran.OCR");
        string realVenv = Path.Combine(ocrDir, ".venv");
        if (!File.Exists(Path.Combine(realVenv, "Scripts", "python.exe")))
        {
            _output.WriteLine(@"SKIPPED (env): ReTran.OCR\.venv not created yet (uv sync)");
            return;
        }
        if (CoreLocator.FindCoreExe() is not string coreExe)
        {
            _output.WriteLine("SKIPPED (env): ReTran.Core.exe not built/RETRAN_CORE_EXE unset");
            return;
        }

        string appDir = Path.Combine(Path.GetTempPath(), $"retran-cleanappdir-{Guid.NewGuid():N}");
        string appVenvLink = Path.Combine(appDir, "ReTran.OCR", ".venv");
        var coreStderr = new ConcurrentQueue<string>();
        Process? core = null;
        try
        {
            // Fake the installed-app layout: only pyproject.toml + src/** are copied, the venv
            // arrives as a junction. FindRepoRoot must stop at appDir and the ResolvePythonExe
            // fallback must hit the junctioned venv — no repo layout above, no user config.
            // Giả layout app đã cài: chỉ copy pyproject.toml + src/**, venv đến qua junction.
            // FindRepoRoot phải dừng ở appDir và fallback ResolvePythonExe phải gặp venv qua
            // junction — không có layout repo ở trên, không có config người dùng.
            Directory.CreateDirectory(Path.Combine(appDir, "ReTran.OCR"));
            File.Copy(Path.Combine(ocrDir, "pyproject.toml"), Path.Combine(appDir, "ReTran.OCR", "pyproject.toml"));
            CopyOcrSrcTree(Path.Combine(ocrDir, "src"), Path.Combine(appDir, "ReTran.OCR", "src"));
            if (!TryCreateJunction(appVenvLink, realVenv)) return;

            core = StartCoreServe(coreExe, appDir, coreStderr);
            core.StandardInput.NewLine = "\n";
            core.StandardInput.Write(JsonRpc.SerializeRequest(new JsonRpcRequest("1", "ocr.models.list", null)) + "\n");
            core.StandardInput.Flush();

            // ~120s bound: python startup and the one-shot model migrations both run before
            // the sidecar serves its first request.
            // Giới ~120s: python khởi động và các lần migration model one-shot đều chạy trước
            // khi sidecar phục vụ request đầu tiên.
            JsonNode? response = await ReadRpcReplyAsync(core, "1", TimeSpan.FromSeconds(120));
            Assert.True(response is not null,
                $"no ocr.models.list reply from core within 120s (or core exited early); core stderr tail: {StderrTail(coreStderr)}");

            if (response!["error"] is JsonObject err)
                Assert.Fail(
                    $"ocr.models.list through the clean appDir failed (sidecar not spawned?): {err.ToJsonString()}; "
                    + $"core stderr tail: {StderrTail(coreStderr)}");
            var catalog = Assert.IsType<JsonArray>(response["result"]?["catalog"]);
            Assert.NotEmpty(catalog);
        }
        finally
        {
            if (core is not null)
            {
                try { core.Kill(entireProcessTree: true); } catch { /* already gone */ }
                try { core.WaitForExit(5_000); } catch { /* best effort */ }
                core.Dispose();
            }
            // Drop the junction first so the recursive delete never walks into the repo's real
            // .venv.
            // Bỏ junction trước để lần xóa đệ quy không đi vào .venv thật của repo.
            TryRemoveJunction(appVenvLink);
            try { Directory.Delete(appDir, recursive: true); }
            catch (Exception ex) { _output.WriteLine($"cleanup of {appDir} failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> to the source repo root — the directory with
    /// both <c>ReTran.OCR/pyproject.toml</c> and <c>ReTran.Core/ReTran.Core.csproj</c>. The
    /// csproj marker matters: the test bin gets a copy of ReTran.OCR (App's content copy), and
    /// <see cref="OcrBootstrap.FindRepoRoot"/> would stop at that build-output copy, which has
    /// no <c>.venv</c> to junction.
    /// Đi ngược từ <paramref name="startDir"/> tới gốc repo nguồn — thư mục có cả
    /// <c>ReTran.OCR/pyproject.toml</c> và <c>ReTran.Core/ReTran.Core.csproj</c>. Mốc csproj rất
    /// quan trọng: bin test nhận bản copy của ReTran.OCR (content copy của App), và
    /// <see cref="OcrBootstrap.FindRepoRoot"/> sẽ dừng ở bản copy trong build output đó, nơi không
    /// có <c>.venv</c> để junction.
    /// </summary>
    private static string? FindSourceRepoRoot(string startDir)
    {
        for (var d = new DirectoryInfo(startDir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ReTran.OCR", "pyproject.toml")) &&
                File.Exists(Path.Combine(d.FullName, "ReTran.Core", "ReTran.Core.csproj")))
                return d.FullName;
        return null;
    }

    /// <summary>
    /// Copies the ReTran.OCR src tree into the fake app dir, skipping __pycache__ dirs.
    /// Copy cây src của ReTran.OCR vào app dir giả, bỏ qua các thư mục __pycache__.
    /// </summary>
    private static void CopyOcrSrcTree(string srcDir, string dstDir)
    {
        foreach (string file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            if (rel.Contains("__pycache__", StringComparison.Ordinal)) continue;
            string dst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst);
        }
    }

    /// <summary>
    /// Creates a directory junction with cmd's built-in mklink; on failure writes a
    /// SKIPPED (env) line and returns false (junctions need no elevation, so a failure here
    /// is machine policy, not a product regression).
    /// Tạo junction thư mục bằng builtin mklink của cmd; khi lỗi ghi dòng SKIPPED (env) và trả
    /// false (junction không cần quyền admin, nên lỗi ở đây là chính sách máy, không phải
    /// regression của sản phẩm).
    /// </summary>
    private bool TryCreateJunction(string linkDir, string targetDir)
    {
        (int exit, string output) = RunCmd("mklink", "/J", linkDir, targetDir);
        if (exit != 0)
        {
            _output.WriteLine($"SKIPPED (env): mklink /J failed (exit {exit}): {output.Trim()}");
            return false;
        }
        return true;
    }

    /// <summary>Removes the junction itself (never the target), best effort. Xóa chính junction (không bao giờ target), best effort.</summary>
    private static void TryRemoveJunction(string linkDir)
    {
        try { RunCmd("rmdir", linkDir); } catch { /* best effort */ }
    }

    /// <summary>
    /// Runs one cmd.exe built-in command (each token passed as a separate argument so paths
    /// with spaces are quoted by ProcessStartInfo, never hand-escaped); returns exit code and
    /// merged output.
    /// Chạy một builtin của cmd.exe (mỗi token là một argument riêng để ProcessStartInfo tự
    /// quote các path có space, không tự escape thủ công); trả mã thoát và output gộp.
    /// </summary>
    private static (int Exit, string Output) RunCmd(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process cmd = Process.Start(psi)!;
        string stdout = cmd.StandardOutput.ReadToEnd();
        string stderr = cmd.StandardError.ReadToEnd();
        if (!cmd.WaitForExit(30_000))
        {
            try { cmd.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, "cmd timed out");
        }
        return (cmd.ExitCode, stdout + stderr);
    }

    /// <summary>
    /// Spawns <c>ReTran.Core.exe --serve</c> with WorkingDirectory = appDir and redirects its
    /// stdio; its stderr lines are appended to <paramref name="stderr"/> for failure messages.
    /// Khởi chạy <c>ReTran.Core.exe --serve</c> với WorkingDirectory = appDir và redirect stdio;
    /// stderr được nối vào <paramref name="stderr"/> để dùng khi báo lỗi.
    /// </summary>
    private static Process StartCoreServe(string coreExe, string appDir, ConcurrentQueue<string> stderr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = coreExe,
            WorkingDirectory = appDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--serve");
        Process core = Process.Start(psi)!;
        core.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Enqueue(e.Data); };
        core.BeginErrorReadLine();
        return core;
    }

    /// <summary>
    /// Reads core's stdout until the response for request id <paramref name="id"/> arrives;
    /// returns null on timeout or EOF (core exited before replying).
    /// Đọc stdout của core tới khi nhận phản hồi cho request id <paramref name="id"/>; trả null
    /// khi hết giờ hoặc EOF (core thoát trước khi trả lời).
    /// </summary>
    private static async Task<JsonNode?> ReadRpcReplyAsync(Process core, string id, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        while (true)
        {
            string? line;
            try
            {
                line = await core.StandardOutput.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (line is null) return null;
            if (JsonRpc.ParseLine(line) is not { } node) continue;
            if (node["id"] is JsonValue idValue && idValue.TryGetValue<string>(out string? gotId) && gotId == id)
                return node;
        }
    }

    /// <summary>Last 30 core stderr lines, joined for failure messages. 30 dòng cuối stderr của core, nối lại để báo lỗi.</summary>
    private static string StderrTail(ConcurrentQueue<string> lines) =>
        string.Join(" | ", lines.ToArray().TakeLast(30));
}
