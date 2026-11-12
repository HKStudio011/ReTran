using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Diagnostics;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> that writes every record through <see cref="Trace.WriteLine"/>,
/// so app logs reach OutputDebugString/DebugView in a Debug build of the app itself. It replaces
/// <c>LoggingBuilderExtensions.AddDebug</c>, whose calls to <c>Debug.WriteLine</c> live in a
/// Release-built NuGet assembly: <c>[Conditional("DEBUG")]</c> strips them there, and every app log
/// write silently disappeared — the 2026-11-13 tray investigation ran blind on exactly that.
/// Provider <see cref="ILoggerProvider"/> tối thiểu ghi mọi bản ghi qua <see cref="Trace.WriteLine"/>,
/// để log của app tới được OutputDebugString/DebugView trong build Debug của chính app. Nó thay
/// <c>LoggingBuilderExtensions.AddDebug</c> — các lời gọi <c>Debug.WriteLine</c> của provider đó nằm
/// trong assembly NuGet build Release, nơi <c>[Conditional("DEBUG")]</c> strip chúng, khiến mọi lần ghi
/// log của app lặng lẽ biến mất — cuộc điều tra tray 2026-11-13 đã đi mù đúng vì điều này.
/// </summary>
/// <remarks>
/// Deliberately logging-channel only: no levels are filtered (matching the Debug provider it
/// replaces, which also logs every level) and no listeners are managed. A record is written as one
/// line, with the exception on the following lines.
/// Cố ý chỉ là kênh log: không lọc mức nào (khớp provider Debug mà nó thay thế, vốn cũng log mọi
/// mức) và không tự quản lý listener. Mỗi bản ghi là một dòng, exception nằm ở các dòng sau.
/// </remarks>
internal sealed class TraceLoggerProvider : ILoggerProvider
{
    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new TraceLogger(categoryName);

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release: the provider owns no listener, file or native handle.
        // Không có gì để giải phóng: provider không sở hữu listener, file hay handle native nào.
    }

    private sealed class TraceLogger(string categoryName) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message = formatter(state, exception);
            string line = exception is null
                ? $"[{logLevel}] {categoryName}: {message}"
                : $"[{logLevel}] {categoryName}: {message}{Environment.NewLine}{exception}";

            // Trace.WriteLine is compiled into THIS assembly, so it is not stripped the way the
            // Release-built Debug provider's calls were, and its default listener forwards the text
            // to OutputDebugString — visible in DebugView/WinDbg without a debugger attached.
            // Trace.WriteLine được compile vào CHÍNH assembly này nên không bị strip như các lời gọi
            // của provider Debug build Release, và listener mặc định của nó chuyển tiếp nội dung tới
            // OutputDebugString — thấy được trong DebugView/WinDbg mà không cần debugger gắn vào.
            Trace.WriteLine(line);
        }
    }
}
