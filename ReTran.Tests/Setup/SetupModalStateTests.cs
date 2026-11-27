using System.Text.Json.Nodes;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests the pure UI state and download-failure rules used by the Setup modal.
/// Kiểm thử các quy tắc thuần về trạng thái UI và lỗi tải mà Setup modal sử dụng.
/// </summary>
public class SetupModalStateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("catalog")]
    public void FindFailedDownload_OnlyReturnsNeededFailedIdleModel(string? arrayKey)
    {
        JsonArray entries =
        [
            new JsonObject
            {
                ["name"] = "unrelated",
                ["downloading"] = false,
                ["downloadError"] = "ignore this model",
            },
            new JsonObject
            {
                ["name"] = "needed-busy",
                ["downloading"] = true,
                ["downloadError"] = "still retrying",
            },
            new JsonObject
            {
                ["name"] = "needed-done",
                ["installed"] = true,
                ["downloading"] = false,
            },
            new JsonObject
            {
                ["name"] = "needed-failed",
                ["installed"] = false,
                ["downloading"] = false,
                ["downloadError"] = "disk full",
            },
        ];
        JsonNode list = arrayKey is null ? entries : new JsonObject { [arrayKey] = entries };

        Assert.Null(SetupModelSelection.FindFailedDownload(
            list, arrayKey, ["needed-busy", "needed-done"]));

        (string Name, string Error)? failed = SetupModelSelection.FindFailedDownload(
            list, arrayKey, ["needed-busy", "needed-done", "needed-failed"]);

        Assert.NotNull(failed);
        Assert.Equal("needed-failed", failed.Value.Name);
        Assert.Equal("disk full", failed.Value.Error);
    }

    [Fact]
    public void LlmCatalog_LoadedEmpty_CanContinueWithoutAutoAnswer()
    {
        Assert.True(SetupModelSelection.CanContinueLlm(busy: false, loading: false));
        Assert.False(SetupModelSelection.CanContinueLlm(busy: false, loading: true));
        Assert.False(SetupModelSelection.CanContinueLlm(busy: true, loading: false));
    }
}
