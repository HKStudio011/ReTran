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
            ConsoleRedirectGate.Release();
        }
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
