using System.Diagnostics;
using ReTran_App.Services.Update;

namespace ReTran.Tests.Update;

public class ProcessAppRecoveryTests
{
    [Fact]
    public void RestartCurrentApp_UsesCurrentExecutableAndWorkingDirectory()
    {
        const string executablePath = @"C:\Program Files\ReTran\ReTran.App.exe";
        const string appDir = @"C:\Program Files\ReTran\";
        ProcessStartInfo? captured = null;
        var recovery = new ProcessAppRecovery(executablePath, appDir, startInfo =>
        {
            captured = startInfo;
            return Process.GetCurrentProcess();
        });

        recovery.RestartCurrentApp();

        Assert.NotNull(captured);
        Assert.Equal(executablePath, captured.FileName);
        Assert.Equal(appDir, captured.WorkingDirectory);
        Assert.True(captured.UseShellExecute);
        Assert.Equal(string.Empty, captured.Arguments);
        Assert.Empty(captured.ArgumentList);
        Assert.DoesNotContain("ReTran.Updater", captured.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestartCurrentApp_WhenProcessStartReturnsNull_Throws()
    {
        var recovery = new ProcessAppRecovery(
            @"C:\Program Files\ReTran\ReTran.App.exe",
            @"C:\Program Files\ReTran\",
            _ => null);

        var thrown = Assert.Throws<InvalidOperationException>(() => recovery.RestartCurrentApp());

        Assert.Contains("could not be started", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }
}
