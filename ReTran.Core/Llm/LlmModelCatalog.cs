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
/// Curated GGUF catalog: Hy-MT2 1.8B and 7B from the unsloth repos, six quantizations each.
/// <c>UD-Q4_K_XL</c> is the default the model card's llama.cpp examples use. Sizes are from the
/// Hugging Face file listings (verified 2026-11-12), rounded to the decimal-GB figures shown there.
/// Catalog GGUF tuyển chọn: Hy-MT2 1.8B và 7B từ repo unsloth, mỗi bên sáu mức lượng tử hoá.
/// <c>UD-Q4_K_XL</c> là mặc định mà ví dụ llama.cpp của model card dùng. Kích thước lấy từ listing
/// file trên Hugging Face (kiểm chứng 2026-11-12), làm tròn theo số GB thập phân hiển thị ở đó.
/// </summary>
public static class LlmModelCatalog
{
    /// <summary>All selectable models. Toàn bộ model chọn được.</summary>
    public static IReadOnlyList<LlmModelSpec> All { get; } =
    [
        // 1.8B — unsloth/Hy-MT2-1.8B-GGUF
        new("Hy-MT2-1.8B-UD-Q4_K_XL", "unsloth/Hy-MT2-1.8B-GGUF", "Hy-MT2-1.8B-UD-Q4_K_XL.gguf", 1_170_000_000, "UD-Q4_K_XL", "1.8B"),
        new("Hy-MT2-1.8B-UD-Q3_K_XL", "unsloth/Hy-MT2-1.8B-GGUF", "Hy-MT2-1.8B-UD-Q3_K_XL.gguf", 990_000_000, "UD-Q3_K_XL", "1.8B"),
        new("Hy-MT2-1.8B-Q4_K_M", "unsloth/Hy-MT2-1.8B-GGUF", "Hy-MT2-1.8B-Q4_K_M.gguf", 1_130_000_000, "Q4_K_M", "1.8B"),
        new("Hy-MT2-1.8B-Q5_K_M", "unsloth/Hy-MT2-1.8B-GGUF", "Hy-MT2-1.8B-Q5_K_M.gguf", 1_300_000_000, "Q5_K_M", "1.8B"),
        new("Hy-MT2-1.8B-Q6_K", "unsloth/Hy-MT2-1.8B-GGUF", "Hy-MT2-1.8B-Q6_K.gguf", 1_470_000_000, "Q6_K", "1.8B"),
        new("Hy-MT2-1.8B-Q8_0", "unsloth/Hy-MT2-1.8B-GGUF", "Hy-MT2-1.8B-Q8_0.gguf", 1_910_000_000, "Q8_0", "1.8B"),

        // 7B — unsloth/Hy-MT2-7B-GGUF
        new("Hy-MT2-7B-UD-Q4_K_XL", "unsloth/Hy-MT2-7B-GGUF", "Hy-MT2-7B-UD-Q4_K_XL.gguf", 4_780_000_000, "UD-Q4_K_XL", "7B"),
        new("Hy-MT2-7B-UD-Q3_K_XL", "unsloth/Hy-MT2-7B-GGUF", "Hy-MT2-7B-UD-Q3_K_XL.gguf", 3_960_000_000, "UD-Q3_K_XL", "7B"),
        new("Hy-MT2-7B-Q4_K_M", "unsloth/Hy-MT2-7B-GGUF", "Hy-MT2-7B-Q4_K_M.gguf", 4_620_000_000, "Q4_K_M", "7B"),
        new("Hy-MT2-7B-Q5_K_M", "unsloth/Hy-MT2-7B-GGUF", "Hy-MT2-7B-Q5_K_M.gguf", 5_370_000_000, "Q5_K_M", "7B"),
        new("Hy-MT2-7B-Q6_K", "unsloth/Hy-MT2-7B-GGUF", "Hy-MT2-7B-Q6_K.gguf", 6_160_000_000, "Q6_K", "7B"),
        new("Hy-MT2-7B-Q8_0", "unsloth/Hy-MT2-7B-GGUF", "Hy-MT2-7B-Q8_0.gguf", 7_980_000_000, "Q8_0", "7B"),
    ];
}
