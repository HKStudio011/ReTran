using CorePrompt = ReTran.Core.Llm.PromptTemplate;
using AppPrompt = ReTran_App.Services.LlmPromptDefaults;

namespace ReTran.Tests;

/// <summary>
/// Pins the App-side LLM prompt copies byte-equal to Core's <c>PromptTemplate</c> constants. The
/// App cannot reference Core (it spawns it as a separate process), so the Settings UI shows its
/// own copy when a prompt field is null; drift would silently change what the user sees as
/// "default" versus what Core actually sends on the wire.
/// Ghim bản sao prompt LLM phía App trùng từng byte với hằng số <c>PromptTemplate</c> của Core.
/// App không tham chiếu được Core (spawn process riêng) nên UI Cài đặt hiển thị bản sao của mình
/// khi field prompt là null; lệch sẽ âm thầm đổi thứ user thấy là "mặc định" so với thứ Core
/// thật sự gửi trên wire.
/// </summary>
public class PromptDefaultsSyncTests
{
    /// <summary>System prompt default must match Core exactly. System prompt mặc định phải khớp Core tuyệt đối.</summary>
    [Fact]
    public void SystemPrompt_MatchesCore()
    {
        Assert.Equal(CorePrompt.DefaultSystemPrompt, AppPrompt.SystemPrompt);
    }

    /// <summary>User prompt default must match Core exactly (Hy-MT2 model-card text). User prompt mặc định phải khớp Core tuyệt đối (nguyên văn model card Hy-MT2).</summary>
    [Fact]
    public void UserPrompt_MatchesCore()
    {
        Assert.Equal(CorePrompt.DefaultUserPrompt, AppPrompt.UserPrompt);
    }

    /// <summary>The default the UI pre-fills must carry the placeholder Core validates on start. Mặc định UI điền sẵn phải chứa placeholder mà Core kiểm tra lúc start.</summary>
    [Fact]
    public void UserPrompt_ContainsSourceTextPlaceholder()
    {
        Assert.Contains("{source_text}", AppPrompt.UserPrompt, StringComparison.Ordinal);
    }
}
