using ReTran_App.Core;

public class LlmStartupModalStateTests
{
    [Fact]
    public void ErrorDismissal_IsGenerationScopedAcrossConsecutivePreAcceptFailures()
    {
        LlmStartupInfo first = ErrorInfo(generation: 11, attempt: 0, "first pre-accept failure");
        long? dismissed = first.Generation;
        Assert.False(LlmStartupModalState.IsVisible(first, dismissed));

        LlmStartupInfo second = ErrorInfo(generation: 12, attempt: 0, "second pre-accept failure");

        Assert.True(LlmStartupModalState.IsVisible(second, dismissed));
        Assert.Equal(0, second.Attempt); // no Core attempt is required for a visible error
    }

    [Fact]
    public void AcceptedAttempt_StaysWithinTheSameDismissalGeneration()
    {
        LlmStartupInfo preAccept = ErrorInfo(generation: 21, attempt: 0, "failed before accept");
        long? dismissed = preAccept.Generation;
        LlmStartupInfo accepted = preAccept with { Attempt = 7, Error = "failed after accept" };

        Assert.False(LlmStartupModalState.IsVisible(accepted, dismissed));
    }

    [Fact]
    public void LoadingOrNoSnapshot_IsVisibleOnlyWhileStartupExists()
    {
        var loading = new LlmStartupInfo(
            0, "fixture-model", "loading", null, null, null, false, null)
        {
            Generation = 31,
        };

        Assert.True(LlmStartupModalState.IsVisible(loading, dismissedGeneration: 31));
        Assert.False(LlmStartupModalState.IsVisible(null, dismissedGeneration: null));
    }

    private static LlmStartupInfo ErrorInfo(long generation, long attempt, string error) =>
        new(attempt, "fixture-model", "error", null, null, null, false, error)
        {
            Generation = generation,
        };
}
