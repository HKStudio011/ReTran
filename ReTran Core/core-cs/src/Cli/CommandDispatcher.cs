namespace ReTran.Core.Cli;

/// <summary>
/// Maps a CLI subcommand name to a handler. Shared by one-shot CLI and (later) runtime methods.
/// Ánh xạ tên lệnh CLI tới handler. Dùng chung cho CLI một lần và (sau này) phương thức runtime.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly Dictionary<string, Func<string[], Task<int>>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a subcommand handler.</summary>
    public void Register(string name, Func<string[], Task<int>> handler) => _handlers[name] = handler;

    /// <summary>Dispatches args[0] as the subcommand; remaining args are passed through. Returns exit code.</summary>
    public async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0 || !_handlers.TryGetValue(args[0], out var handler))
        {
            Console.Error.WriteLine("usage: ReTran.Core <serve|version|ocr|translate|capture> [args]");
            return 2;
        }
        return await handler(args.Skip(1).ToArray());
    }
}
