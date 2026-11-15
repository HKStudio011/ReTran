using System.Diagnostics.CodeAnalysis;
using ReTran.Core.Translate;

namespace ReTran.Core.Cli;

/// <summary>
/// <c>translate</c> CLI subcommand: one-shot translation (prints the translation to stdout).
/// Subcommand CLI <c>translate</c>: dịch một lần (in bản dịch ra stdout).
/// </summary>
public static class TranslateCommands
{
    /// <summary>
    /// Usage line for the <c>translate</c> subcommand. Dòng hướng dẫn dùng cho subcommand <c>translate</c>.
    /// </summary>
    public static string Usage =>
        "usage: ReTran.Core translate \"<text>\" --dst <lang> [--src auto] [--provider google]";

    /// <summary>
    /// Registers the <c>translate</c> command on <paramref name="d"/>, optionally with an injected translator.
    /// Đăng ký lệnh <c>translate</c> vào <paramref name="d"/>, tùy chọn với trình dịch được inject.
    /// </summary>
    /// <param name="d">Dispatcher to register on. Dispatcher cần đăng ký.</param>
    /// <param name="translator">
    /// Translator to resolve by provider name instead of the default Google provider; <see langword="null"/> keeps production behavior.
    /// Trình dịch được tra theo tên provider thay cho Google mặc định; <see langword="null"/> giữ nguyên hành vi production.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="d"/> is null. Ném khi <paramref name="d"/> là null.</exception>
    public static void Register(CommandDispatcher d, ITranslator? translator = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        d.Register("translate", args => RunAsync(args, translator));
    }

    private static async Task<int> RunAsync(string[] args, ITranslator? injected)
    {
        if (!TryParse(args, out TranslateOptions? options, out string? error))
        {
            Console.Error.WriteLine(Usage);
            if (error is not null)
                Console.Error.WriteLine(error);
            return 2;
        }

        var registry = new TranslatorRegistry();
        registry.Register(injected ?? new GoogleWebTranslator());
        ITranslator? translator = registry.Get(options.Provider);
        if (translator is null)
        {
            Console.Error.WriteLine($"unknown provider: {options.Provider}.");
            return 1;
        }

        try
        {
            string translated = await translator.TranslateAsync(
                options.Text, options.Src, options.Dst, CancellationToken.None);
            Console.Out.WriteLine(translated);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"translate failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Parsed arguments for one <c>translate</c> run. Đối số đã parse cho một lần chạy <c>translate</c>.
    /// </summary>
    /// <param name="Text">Text to translate (positional words joined by spaces). Văn bản cần dịch (các từ nối bằng khoảng trắng).</param>
    /// <param name="Src">Source language code (default <c>auto</c>). Mã ngôn ngữ nguồn (mặc định <c>auto</c>).</param>
    /// <param name="Dst">Target language code (--dst). Mã ngôn ngữ đích (--dst).</param>
    /// <param name="Provider">Provider name (default <c>google</c>). Tên nhà cung cấp (mặc định <c>google</c>).</param>
    private sealed record TranslateOptions(string Text, string Src, string Dst, string Provider);

    /// <summary>
    /// Parses translate arguments: one or more positional words form the text, <c>--dst</c> is required,
    /// <c>--src</c> (default <c>auto</c>) and <c>--provider</c> (default <c>google</c>) are optional.
    /// Phân tích đối số translate: một hoặc nhiều từ thuần túy tạo thành văn bản, <c>--dst</c> bắt buộc,
    /// <c>--src</c> (mặc định <c>auto</c>) và <c>--provider</c> (mặc định <c>google</c>) tùy chọn.
    /// </summary>
    /// <param name="args">Arguments after <c>translate</c>. Đối số sau <c>translate</c>.</param>
    /// <param name="options">Parsed options on success. Tùy chọn đã parse khi thành công.</param>
    /// <param name="error">Null on success; otherwise the reason. Không null khi thành công; ngược lại là lý do.</param>
    /// <returns><see langword="true"/> when the arguments are valid. <see langword="true"/> khi đối số hợp lệ.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="args"/> is null.</exception>
    private static bool TryParse(
        string[] args,
        [NotNullWhen(true)] out TranslateOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = null;
        string? dst = null;
        string src = "auto";
        string provider = "google";
        var words = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dst":
                    if (i + 1 >= args.Length)
                    {
                        error = "missing value for --dst (expected a language code).";
                        return false;
                    }
                    dst = args[++i];
                    break;
                case "--src":
                    if (i + 1 >= args.Length)
                    {
                        error = "missing value for --src (expected a language code or auto).";
                        return false;
                    }
                    src = args[++i];
                    break;
                case "--provider":
                    if (i + 1 >= args.Length)
                    {
                        error = "missing value for --provider (expected a provider name).";
                        return false;
                    }
                    provider = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"unknown argument '{args[i]}'.";
                        return false;
                    }
                    words.Add(args[i]);
                    break;
            }
        }

        string text = string.Join(' ', words);
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "missing <text> to translate.";
            return false;
        }
        if (dst is null)
        {
            error = "missing required --dst <lang>.";
            return false;
        }

        options = new TranslateOptions(text, src, dst, provider);
        return true;
    }
}
