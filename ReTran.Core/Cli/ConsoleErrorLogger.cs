using Microsoft.Extensions.Logging;

namespace ReTran.Core.Cli;

/// <summary>
/// Minimal ILogger writing to stderr — one-shot CLI diagnostics (stdout is reserved for results).
/// ILogger tối thiểu ghi ra stderr — chẩn đoán cho CLI một lượt (stdout dành cho kết quả).
/// </summary>
internal sealed class ConsoleErrorLogger : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
}
