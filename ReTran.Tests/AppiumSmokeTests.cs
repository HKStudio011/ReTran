using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Windows;
using ReTran_App.Core;
using Xunit;

namespace RetranCore.Tests;

public class AppiumSmokeTests
{
    private static readonly Uri AppiumUri = new("http://127.0.0.1:4723/");

    [Fact]
    public void StartCore_Ping_ShowsPong()
    {
        if (!AppiumUp()) return; // no Appium server on 127.0.0.1:4723 -> skip
        if (FindAppExe() is not string appExe) return; // App not built -> skip
        if (CoreLocator.FindCoreExe() is null) return; // Core not resolvable (no exe, no RETRAN_CORE_EXE) -> skip

        var opts = new AppiumOptions();
        opts.App = appExe; // "app" capability: the real ReTran.App.exe to launch
        opts.AutomationName = "Windows";
        using var driver = CreateDriverOrNull(opts);
        if (driver is null) return; // Appium up but `windows` driver not installed -> skip (environment)
        try
        {
            // Grab the status label by AutomationId — the label's Name (Text) is locale-dependent
            // (vi/en after i18n) and a name search for an absent string hangs WinAppDriver.
            // Lấy status label theo AutomationId — Name (Text) phụ thuộc ngôn ngữ (vi/en sau i18n)
            // và tìm theo name khi chuỗi vắng mặt sẽ treo WinAppDriver.
            var status = WaitByAutomationId(driver, "coreStatusLabel", TimeSpan.FromSeconds(15));
            WaitStatusShowsAny(status, TimeSpan.FromSeconds(5), "Core: stopped", "Core: đã dừng"); // giá trị ban đầu theo locale
            WaitByAutomationId(driver, "startCoreButton", TimeSpan.FromSeconds(45)).Click();
            WaitStatusShowsAny(status, TimeSpan.FromSeconds(30), "Core: started", "Core: đã chạy");
            WaitByAutomationId(driver, "pingButton", TimeSpan.FromSeconds(15)).Click();
            WaitStatusShows(status, "Ping: pong", TimeSpan.FromSeconds(30)); // technical, không đổi qua i18n
        }
        catch
        {
            SaveFailureScreenshot(); // what the desktop (and the app window) actually showed
            throw;
        }
        finally
        {
            try { driver.Quit(); } catch { /* session may already be gone */ } // never let the real app window linger
        }
    }

    /// <summary>
    /// Creates the Windows session, or returns null when the Appium `windows` driver is missing
    /// (environment, not a product failure — skip rather than fail). Any other session error propagates.
    /// </summary>
    private static WindowsDriver? CreateDriverOrNull(AppiumOptions opts)
    {
        try
        {
            return new WindowsDriver(AppiumUri, opts, TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) when (
            ex is NoSuchDriverException
            || (ex is WebDriverException && ex.Message.Contains("driver", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
    }

    /// <summary>
    /// Polls a captured status-label element until its UIA Name (or Text) contains <paramref name="expected"/>.
    /// Throws NoSuchElementException on timeout, reporting the last observed text.
    /// </summary>
    private static void WaitStatusShows(IWebElement status, string expected, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        string last = "";
        while (sw.Elapsed < timeout)
        {
            try
            {
                last = status.GetAttribute("Name") ?? "";
                if (last.Length == 0) last = status.Text ?? "";
                if (last.Contains(expected, StringComparison.Ordinal)) return;
            }
            catch (StaleElementReferenceException ex)
            {
                throw new NoSuchElementException($"Status label went stale before showing '{expected}': {ex.Message}");
            }
            Thread.Sleep(250);
        }
        throw new NoSuchElementException(
            $"Status label never showed '{expected}' within {timeout.TotalSeconds:0}s (last text: '{last}')");
    }

    /// <summary>
    /// Saves a desktop screenshot to %TEMP%\retran-smoke-failure.png so a failed run is diagnosable
    /// visually. Appium.WebDriver 9's WindowsDriver has no screenshot API, so capture the screen directly.
    /// </summary>
    private static void SaveFailureScreenshot()
    {
        try
        {
            // Virtual-screen bounds via GetSystemMetrics (no WinForms reference in this project).
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            using var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(new System.Drawing.Point(x, y), System.Drawing.Point.Empty, new System.Drawing.Size(w, h));
            bmp.Save(Path.Combine(Path.GetTempPath(), "retran-smoke-failure.png"));
        }
        catch { /* diagnostics are best-effort */ }
    }

    /// <summary>
    /// Polls until an element with the given UIA AutomationId appears. Throws NoSuchElementException on timeout.
    /// Quét cho tới khi phần tử có AutomationId cho trước xuất hiện. Ném NoSuchElementException khi hết thời gian.
    /// </summary>
    private static IWebElement WaitByAutomationId(WindowsDriver driver, string automationId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { return driver.FindElement(MobileBy.AccessibilityId(automationId)); }
            catch (NoSuchElementException) { /* keep polling */ }
            Thread.Sleep(500);
        }
        throw new NoSuchElementException($"No element with AutomationId '{automationId}' within {timeout.TotalSeconds:0}s");
    }

    /// <summary>
    /// Polls a captured status-label element until its UIA Name contains ANY of <paramref name="expected"/>
    /// (locale-dependent values are accepted in both vi and en forms).
    /// Quét status label đã nắm cho tới khi UIA Name chứa MỘT trong các giá trị (nhận cả 2 dạng vi/en).
    /// </summary>
    private static void WaitStatusShowsAny(IWebElement status, TimeSpan timeout, params string[] expected)
    {
        var sw = Stopwatch.StartNew();
        string last = "";
        while (sw.Elapsed < timeout)
        {
            try
            {
                last = status.GetAttribute("Name") ?? "";
                if (last.Length == 0) last = status.Text ?? "";
                foreach (var candidate in expected)
                {
                    if (last.Contains(candidate, StringComparison.Ordinal)) return;
                }
            }
            catch (StaleElementReferenceException ex)
            {
                throw new NoSuchElementException(
                    $"Status label went stale before showing any of [{string.Join(", ", expected)}]: {ex.Message}");
            }
            Thread.Sleep(250);
        }
        throw new NoSuchElementException(
            $"Status label never showed any of [{string.Join(", ", expected)}] within {timeout.TotalSeconds:0}s (last text: '{last}')");
    }

    private const int SM_XVIRTUALSCREEN = 0;
    private const int SM_YVIRTUALSCREEN = 1;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>True when an Appium server accepts connections on 127.0.0.1:4723.</summary>
    private static bool AppiumUp()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 4723).Wait(2000) && c.Connected;
        }
        catch { return false; }
    }

    /// <summary>Walks up from the test bin dir to the repo root looking for the built App exe.</summary>
    private static string? FindAppExe()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null)
        {
            string p = Path.Combine(d.FullName, "ReTran.App", "bin", "Debug",
                "net10.0-windows10.0.19041.0", "win-x64", "ReTran.App.exe");
            if (File.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }
}
