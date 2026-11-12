using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Config;
using ReTran.Core.Ocr;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using Frame = ReTran.Core.Capture.Frame;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RetranCore.Tests;

/// <summary>
/// Shared OCR test helpers extracted from OcrCliTests (fixture, warm-up, redirect harness).
/// Helper OCR dùng chung trích từ OcrCliTests (fixture, warm-up, harness redirect).
/// </summary>
internal static class OcrTestSupport
{
    /// <summary>Process-wide gate serializing the Console redirect window across parallel test classes.</summary>
    private static readonly SemaphoreSlim ConsoleRedirectGate = new(1, 1);

    /// <summary>
    /// Runs the dispatcher with Console redirected; snapshots stdout/stderr AFTER the await.
    /// Serialized via a static gate so parallel test classes cannot clobber each other's Console writers.
    /// </summary>
    public static async Task<(string Stdout, string Stderr, int Exit)> RunDispatcherAsync(
        CommandDispatcher d, string[] args)
    {
        await ConsoleRedirectGate.WaitAsync();
        try
        {
            return await RunDispatcherCoreAsync(d, args);
        }
        finally
        {
            ConsoleRedirectGate.Release();
        }
    }

    /// <summary>
    /// Runs the dispatcher with Console redirected AND the process working directory pinned to
    /// <paramref name="repoRoot"/> for the whole call; both are restored in a finally. The CWD pin is
    /// required because the production ocr CLI resolves its repo/venv from the process CWD (the "Core
    /// runs with CWD = app dir" contract): under the test host the CWD sits in build output, which holds
    /// a venv-less ReTran.OCR copy and would make the CLI fail with "python not found". Serialized via
    /// the same gate as <see cref="RunDispatcherAsync"/> so parallel test classes never observe a
    /// shifted CWD or a half-redirected Console during another class's dispatcher window.
    /// Chạy dispatcher với Console được redirect VÀ CWD của process được ghim tại
    /// <paramref name="repoRoot"/> trong suốt lượt gọi; cả hai đều được khôi phục trong finally. Việc ghim
    /// CWD là bắt buộc vì CLI ocr production định vị repo/venv từ CWD của process (hợp đồng "Core chạy
    /// với CWD = app dir"): dưới test host, CWD nằm trong build output, nơi có bản copy ReTran.OCR/ không
    /// .venv và CLI sẽ fail "python not found". Được serialize bằng cùng gate như
    /// <see cref="RunDispatcherAsync"/> để các class test song song không bao giờ thấy CWD bị lệch hay
    /// Console đang redirect dở trong cửa sổ dispatcher của class khác.
    /// </summary>
    public static async Task<(string Stdout, string Stderr, int Exit)> RunDispatcherAtRepoRootAsync(
        CommandDispatcher d, string[] args, string repoRoot)
    {
        await ConsoleRedirectGate.WaitAsync();
        string originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(repoRoot);
        try
        {
            return await RunDispatcherCoreAsync(d, args);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            ConsoleRedirectGate.Release();
        }
    }

    private static async Task<(string Stdout, string Stderr, int Exit)> RunDispatcherCoreAsync(
        CommandDispatcher d, string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        using var so = new StringWriter();
        using var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            int exit = await d.DispatchAsync(args);
            return (so.ToString(), se.ToString(), exit);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    /// <summary>
    /// A fresh OCR shared-memory mapping name per call, so tests never collide with each other
    /// or with the real <see cref="ReTran.Core.Ocr.OcrShm.Name"/> section.
    /// Tên mapping OCR mới mỗi lần gọi, để test không đụng nhau và không chạm vùng
    /// <see cref="ReTran.Core.Ocr.OcrShm.Name"/> thật.
    /// </summary>
    public static string UniqueOcrShmName(string stem) =>
        $@"Local\ReTran.Tests.OcrShm.{stem}.{Guid.NewGuid():N}";

    /// <summary>
    /// Locates the repo root for real-sidecar tests: walks up from <see cref="AppContext.BaseDirectory"/>
    /// (NOT the process CWD — under the test host the CWD lands in build output, which holds a
    /// Content-propagated copy of ReTran.OCR/ but no .venv) and returns the FIRST ancestor containing BOTH
    /// ReTran.OCR/pyproject.toml AND ReTran.OCR/.venv/Scripts/python.exe. Requiring both markers is the
    /// point: pyproject.toml alone (the old CWD-based gate) matched the venv-less build-output copy and
    /// silently skipped the sidecar tests while counting them as passed.
    /// Tìm gốc repo cho test sidecar thật: đi lên từ <see cref="AppContext.BaseDirectory"/> (KHÔNG phải CWD —
    /// dưới test host, CWD nằm trong build output, nơi có bản copy ReTran.OCR/ do Content nhưng không có
    /// .venv) và trả về ancestor ĐẦU TIÊN chứa CẢ ReTran.OCR/pyproject.toml lẫn
    /// ReTran.OCR/.venv/Scripts/python.exe. Bắt buộc cả hai dấu mốc: chỉ pyproject.toml (gate cũ theo CWD)
    /// khớp với bản copy trong build output không có .venv, khiến test sidecar bị bỏ qua im lặng nhưng vẫn
    /// được đếm là passed.
    /// </summary>
    /// <param name="repoRoot">The repo root found, or empty when none qualifies. Gốc repo tìm được, hoặc rỗng khi không có.</param>
    /// <param name="pythonExe">The venv python.exe under that root, or empty when none qualifies.
    /// Đường dẫn python.exe của venv dưới gốc đó, hoặc rỗng khi không có.</param>
    /// <returns>False when no ancestor has the full repo + venv layout; callers must then report a skip with
    /// a reason (never a bare return, which xUnit counts as passed with zero coverage).
    /// False khi không có ancestor nào đủ layout repo + venv; caller khi đó phải báo skip kèm lý do
    /// (không dùng return trần — xUnit đếm là passed với 0 coverage).</returns>
    public static bool TryResolveRepoWithVenv(out string repoRoot, out string pythonExe)
    {
        repoRoot = "";
        pythonExe = "";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string venvPython = Path.Combine(dir.FullName, "ReTran.OCR", ".venv", "Scripts", "python.exe");
            if (File.Exists(Path.Combine(dir.FullName, "ReTran.OCR", "pyproject.toml")) && File.Exists(venvPython))
            {
                repoRoot = dir.FullName;
                pythonExe = venvPython;
                return true;
            }
            dir = dir.Parent;
        }
        return false;
    }

    /// <summary>Draws HELLO RETRAN on white; returns the temp PNG path (caller deletes).</summary>
    public static string CreateTextFixturePng()
    {
        string path = Path.Combine(Path.GetTempPath(), $"retran-ocr-fixture-{Guid.NewGuid():N}.png");
        using var bmp = new Bitmap(640, 96);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var font = new Font("Arial", 48f, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("HELLO RETRAN", font, Brushes.Black, 10f, 20f);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Runs one python snippet that loads PaddleOCR and OCRs the fixture (warms the model cache).</summary>
    public static async Task<bool> TryWarmUpModel(string python, string repoRoot, string fixturePng)
    {
        string script = Path.Combine(Path.GetTempPath(), $"retran-warmup-{Guid.NewGuid():N}.py");
        File.WriteAllText(script, """
            import sys
            import numpy as np
            from PIL import Image
            from paddleocr import PaddleOCR
            # Keep kwargs in sync with PaddleOcrEngine._create (M3 perf gate):
            img = np.array(Image.open(sys.argv[1]).convert("RGB"))[:, :, ::-1]
            PaddleOCR(
                lang="en",
                enable_mkldnn=False,
                text_detection_model_name="PP-OCRv5_mobile_det",
                text_recognition_model_name="en_PP-OCRv5_mobile_rec",
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
            ).predict(img)
            print("WARM_OK")
            """);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(fixturePng);
            using var p = Process.Start(psi)!;
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            bool exited = p.WaitForExit(300_000);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            _ = await errTask;
            string stdout = await outTask;
            return p.ExitCode == 0 && stdout.Contains("WARM_OK");
        }
        catch { return false; }
        finally { if (File.Exists(script)) File.Delete(script); }
    }

    /// <summary>Loads a PNG file as an owned BGRA Frame (handles stride padding).</summary>
    public static Frame FrameFromPng(string pngPath)
    {
        using var bmp = new Bitmap(pngPath);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride),
                    pixels, y * bmp.Width * 4, bmp.Width * 4);
            return Frame.FromBgra(bmp.Width, bmp.Height, pixels);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
