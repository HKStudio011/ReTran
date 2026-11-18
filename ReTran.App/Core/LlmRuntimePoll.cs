namespace ReTran_App.Core;

/// <summary>Terminal or waiting state of one runtime download poll. Trạng thái chờ hoặc kết thúc của một lần poll runtime.</summary>
internal enum LlmRuntimePollOutcome
{
    Waiting,
    Completed,
    Failed,
    Superseded,
}

/// <summary>
/// Interprets Core's runtime status without inferring completion from a tag change.
/// Diễn giải trạng thái runtime từ Core mà không suy ra hoàn tất từ việc tag thay đổi.
/// </summary>
internal static class LlmRuntimePoll
{
    /// <summary>
    /// Classifies one status snapshot; a failed update fails even when an older install remains usable.
    /// Phân loại một snapshot; update lỗi vẫn fail kể cả khi bản cũ còn dùng được.
    /// </summary>
    internal static LlmRuntimePollOutcome Evaluate(LlmRuntimeStatusInfo status, long attempt = 0)
    {
        if (attempt > 0 && status.Attempt != attempt) return LlmRuntimePollOutcome.Superseded;
        if (status.DownloadFailed) return LlmRuntimePollOutcome.Failed;
        if (status.Downloading) return LlmRuntimePollOutcome.Waiting;
        return status.Installed ? LlmRuntimePollOutcome.Completed : LlmRuntimePollOutcome.Waiting;
    }
}
