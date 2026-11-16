using System.Text.Json;

namespace ReTran.Core.Llm;

/// <summary>
/// One selectable GGUF model: catalog name, Hugging Face repo, file name and approximate size.
/// Một model GGUF chọn được: tên catalog, repo Hugging Face, tên file và kích thước xấp xỉ.
/// </summary>
/// <param name="Name">Catalog name, e.g. <c>Hy-MT2-1.8B-UD-Q4_K_XL</c>. Tên catalog.</param>
/// <param name="Repo">Hugging Face repo, e.g. <c>unsloth/Hy-MT2-1.8B-GGUF</c>. Repo Hugging Face.</param>
/// <param name="File">GGUF file name inside the repo. Tên file GGUF trong repo.</param>
/// <param name="SizeBytes">Approximate file size in bytes (HF listing, decimal GB). Kích thước file xấp xỉ theo byte (theo listing HF, GB thập phân).</param>
/// <param name="Quant">Quantization label, e.g. <c>UD-Q4_K_XL</c>. Nhãn lượng tử hoá.</param>
/// <param name="SizeClass"><c>1.8B</c> or <c>7B</c>. Lớp kích thước <c>1.8B</c> hoặc <c>7B</c>.</param>
public sealed record LlmModelSpec(
    string Name, string Repo, string File, long SizeBytes, string Quant, string SizeClass);

/// <summary>
/// Curated GGUF catalog loaded from <c>Llm/llm-models.json</c> next to the assembly (wizard vòng 2
/// §8): adding a quantization or a size class is a data edit — no code change. <c>UD-Q4_K_XL</c> is
/// the default the model card's llama.cpp examples use. A missing or malformed file yields an empty
/// catalog and one stderr line — never a throw.
/// Catalog GGUF tuyển chọn nạp từ <c>Llm/llm-models.json</c> cạnh assembly (wizard vòng 2 §8): thêm
/// một mức lượng tử hoá hay lớp kích thước là sửa dữ liệu — không sửa code. <c>UD-Q4_K_XL</c> là mặc
/// định mà ví dụ llama.cpp của model card dùng. File thiếu hoặc hỏng cho catalog rỗng kèm một dòng
/// stderr — không bao giờ ném.
/// </summary>
public static class LlmModelCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<IReadOnlyList<LlmModelSpec>> LazyAll = new(() => Load());

    /// <summary>All selectable models, in file order. Toàn bộ model chọn được, theo thứ tự trong file.</summary>
    public static IReadOnlyList<LlmModelSpec> All => LazyAll.Value;

    /// <summary>
    /// Loads the catalog from a JSON array file; the default path is <c>Llm/llm-models.json</c> next
    /// to the assembly. Missing, malformed or unreadable input returns an empty catalog after one
    /// stderr line.
    /// Nạp catalog từ file mảng JSON; đường dẫn mặc định là <c>Llm/llm-models.json</c> cạnh assembly.
    /// File thiếu, hỏng hoặc không đọc được trả catalog rỗng kèm một dòng stderr.
    /// </summary>
    /// <param name="path">Override path (tests), or <see langword="null"/> for the default. Đường dẫn ghi đè (test), hoặc <see langword="null"/> để dùng mặc định.</param>
    /// <returns>The specs in file order, or an empty list. Các spec theo thứ tự file, hoặc danh sách rỗng.</returns>
    internal static IReadOnlyList<LlmModelSpec> Load(string? path = null)
    {
        string target = path ?? Path.Combine(AppContext.BaseDirectory, "Llm", "llm-models.json");
        try
        {
            if (!File.Exists(target))
            {
                Console.Error.WriteLine($"llm.catalog: missing {target}");
                return [];
            }

            List<LlmModelSpec>? specs =
                JsonSerializer.Deserialize<List<LlmModelSpec>>(File.ReadAllText(target), SerializerOptions);
            return specs ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"llm.catalog: unreadable {target}: {ex.Message}");
            return [];
        }
    }
}
