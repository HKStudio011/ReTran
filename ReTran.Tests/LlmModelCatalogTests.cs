using ReTran.Core.Llm;

namespace RetranCore.Tests;

/// <summary>
/// Tests for the data-file LLM catalog loader (wizard vòng 2 §8): the curated list lives in
/// <c>Llm/llm-models.json</c> next to the assembly, so adding a quant or a size class is a data edit.
/// Test cho loader catalog LLM từ file dữ liệu (wizard vòng 2 §8): danh sách nằm trong
/// <c>Llm/llm-models.json</c> cạnh assembly, nên thêm quant hay cỡ là sửa dữ liệu.
/// </summary>
public class LlmModelCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"retran-llmcatalog-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Load_CustomPath_ReadsEntries()
    {
        Directory.CreateDirectory(_dir);
        string json = Path.Combine(_dir, "catalog.json");
        File.WriteAllText(json, """
            [{ "name": "X-1B-Q4", "repo": "r/x", "file": "X.gguf", "sizeBytes": 123, "quant": "Q4", "sizeClass": "1B" }]
            """);

        IReadOnlyList<LlmModelSpec> specs = LlmModelCatalog.Load(json);

        LlmModelSpec spec = Assert.Single(specs);
        Assert.Equal("X-1B-Q4", spec.Name);
        Assert.Equal("r/x", spec.Repo);
        Assert.Equal("X.gguf", spec.File);
        Assert.Equal(123, spec.SizeBytes);
        Assert.Equal("Q4", spec.Quant);
        Assert.Equal("1B", spec.SizeClass);
    }

    [Fact]
    public void Load_Malformed_ReturnsEmptyNoThrow()
    {
        Directory.CreateDirectory(_dir);
        string json = Path.Combine(_dir, "bad.json");
        File.WriteAllText(json, "not json");

        Assert.Empty(LlmModelCatalog.Load(json));
    }

    [Fact]
    public void Load_Missing_ReturnsEmpty()
    {
        Assert.Empty(LlmModelCatalog.Load(Path.Combine(_dir, "nope.json")));
    }
}
