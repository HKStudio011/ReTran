using ReTran_App.Core;

namespace ReTran.Tests;

public class LlmRuntimePollTests
{
    [Fact]
    public void Evaluate_SameTagInstalledWithoutActiveDownload_Completes()
    {
        var status = new LlmRuntimeStatusInfo("b190", true, @"C:\llm\b190\llama-server.exe");

        Assert.Equal(LlmRuntimePollOutcome.Completed, LlmRuntimePoll.Evaluate(status));
    }

    [Fact]
    public void Evaluate_PreviousInstallStillDownloading_Waits()
    {
        var status = new LlmRuntimeStatusInfo("b190", false, @"C:\llm\b190\llama-server.exe")
        {
            Downloading = true,
        };

        Assert.Equal(LlmRuntimePollOutcome.Waiting, LlmRuntimePoll.Evaluate(status));
    }

    [Fact]
    public void Evaluate_FailedUpdateWithPreviousInstall_FailsFast()
    {
        var status = new LlmRuntimeStatusInfo("b190", true, @"C:\llm\b190\llama-server.exe")
        {
            DownloadFailed = true,
        };

        Assert.Equal(LlmRuntimePollOutcome.Failed, LlmRuntimePoll.Evaluate(status));
    }

    [Fact]
    public void Evaluate_NewerAttemptSupersedesPolledAttempt()
    {
        var status = new LlmRuntimeStatusInfo("b190", true, @"C:\llm\b190\llama-server.exe")
        {
            Attempt = 2,
        };

        Assert.Equal(LlmRuntimePollOutcome.Superseded, LlmRuntimePoll.Evaluate(status, attempt: 1));
    }

    [Fact]
    public void Evaluate_NotInstalledYet_Waits()
    {
        var status = new LlmRuntimeStatusInfo(null, false, null);

        Assert.Equal(LlmRuntimePollOutcome.Waiting, LlmRuntimePoll.Evaluate(status));
    }
}
