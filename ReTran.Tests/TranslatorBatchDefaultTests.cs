using ReTran.Core.Translate;

namespace RetranCore.Tests;

public class TranslatorBatchDefaultTests
{
    private sealed class PerTextOnly : ITranslator
    {
        public List<string> Calls { get; } = [];

        public string Name => "per-text-only";

        public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct = default)
        {
            Calls.Add(text);
            return Task.FromResult($"«{text}»");
        }
    }

    [Fact]
    public async Task DefaultBatch_LoopsPerText_PreservingOrder()
    {
        ITranslator t = new PerTextOnly();

        var result = await t.TranslateBatchAsync(["a", "b", "c"], "en", "vi", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "«a»", "«b»", "«c»" }, result);
        Assert.Equal(new[] { "a", "b", "c" }, ((PerTextOnly)t).Calls);
    }
}
