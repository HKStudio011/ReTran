using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Windows;
using Xunit;

namespace RetranCore.Tests;

public class AppiumSmokeTests
{
    private static readonly Uri AppiumUri = new("http://127.0.0.1:4723/");

    // Selenium .NET's By.Name sends the "css selector" mechanism, which WinAppDriver (appium-windows-driver)
    // rejects. Its "name" strategy is an exact UIA Name match that answers in well under a second WHEN the
    // string exists in the top visual-tree row; a string that isn't there yet falls through into the WebView2
    // subtree and hangs the server (observed: 60s no-response on "Core: started" polled right after the click).
    // So: build the raw By directly, and only search for strings known to be present — dynamic status text is
    // polled on a pre-captured element handle instead (see WaitStatusShows).
    private static readonly ConstructorInfo RawByCtor = typeof(By).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string) }, null)!;

    [Fact]
    public void StartCore_Ping_ShowsPong()
    {
        if (!AppiumUp()) return; // no Appium server on 127.0.0.1:4723 -> skip
        if (FindAppExe() is not string appExe) return; // App not built -> skip
        if (FindCoreExe() is null) return; // Core not resolvable (no exe, no RETRAN_CORE_EXE) -> skip

        var opts = new AppiumOptions();
        opts.App = appExe; // "app" capability: the real ReTran.App.exe to launch
        opts.AutomationName = "Windows";
        using var driver = new WindowsDriver(AppiumUri, opts, TimeSpan.FromSeconds(60));
        try
        {
            // Grab the status label while its text is still the known-present initial value.
            // A name search for a NOT-YET-APPEARING string falls through the top row into the
            // WebView2 subtree and hangs WinAppDriver forever (observed: 60s no-response on
            // "Core: started" polled 4ms after the click). Capturing the handle first lets us
            // poll the label's Name property — a property read on one element never walks the tree.
            var status = WaitByName(driver, "Core: stopped", TimeSpan.FromSeconds(15));
            WaitByName(driver, "Start Core", TimeSpan.FromSeconds(45)).Click();
            WaitStatusShows(status, "Core: started", TimeSpan.FromSeconds(30));
            WaitByName(driver, "Ping", TimeSpan.FromSeconds(15)).Click(); // button always exists (row 0) -> shallow hit
            WaitStatusShows(status, "Ping: pong", TimeSpan.FromSeconds(30));
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

    /// <summary>Builds a By with an arbitrary locator mechanism (bypasses Selenium's css-selector remap of By.Name).</summary>
    private static By RawBy(string mechanism, string criteria) =>
        (By)RawByCtor.Invoke(new object[] { mechanism, criteria })!;

    /// <summary>Polls until an element with the exact UIA Name appears. Throws NoSuchElementException on timeout.</summary>
    private static IWebElement WaitByName(WindowsDriver driver, string name, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { return driver.FindElement(RawBy("name", name)); }
            catch (NoSuchElementException) { /* keep polling */ }
            Thread.Sleep(500);
        }
        throw new NoSuchElementException($"No element with UIA Name '{name}' within {timeout.TotalSeconds:0}s");
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

    /// <summary>Mirrors MainPage.FindCoreExe(): RETRAN_CORE_EXE env var wins, else the default repo build path.</summary>
    private static string? FindCoreExe()
    {
        string? env = Environment.GetEnvironmentVariable("RETRAN_CORE_EXE");
        if (env is not null && File.Exists(env)) return env;

        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null)
        {
            string p = Path.Combine(d.FullName, "ReTran.Core", "bin", "Debug", "net10.0-windows", "ReTran.Core.exe");
            if (File.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }
}
