using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Diagnostics;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> appending Warning-and-above records to a daily log file under
/// <c>%LOCALAPPDATA%\ReTran\logs</c>, so a packaged (Release) run is debuggable too: the Debug build
/// has <see cref="TraceLoggerProvider"/> and Blazor devtools, a Release run has neither — the generic
/// Blazor error banner is then the only symptom, which is exactly how the 2026-11-17 wizard crash
/// report surfaced (an unhandled render exception with no stack anywhere).
/// Provider <see cref="ILoggerProvider"/> t?i thi?u, ghi các b?n ghi t? Warning tr? lên vào file log
/// theo ngày d??i <c>%LOCALAPPDATA%\ReTran\logs</c>, d? phiên ch?y đóng gói (Release) cung khai thác
/// du?c: build Debug có <see cref="TraceLoggerProvider"/> và Blazor devtools, còn Release thì kh�ng có
/// c? hai — banner l?i Blazor chung chung l� tri?u ch?ng duy nh?t, đúng cách báo c� crash wizard
/// 2026-11-17 xu?t hi?n (exception render kh�ng x? l? mà kh�ng có stack ? dau).
/// </summary>
/// <remarks>
/// Deliberately belt-and-braces: writes are serialized by a lock, the file is opened per record (no
/// long-lived handle to fight a second app instance over), and every disk failure is swallowed —
/// logging must never take the app down. Warnings-and-up only keeps the file useful (Info chatter
/// would bury the stack).
/// C? tình an-toàn-c?-k?p: ghi du?c ph?i hóa b?ng lock, file m? theo t?ng b?n ghi (không gi? handle
/// l�u đ? tranh v?i instance app th? hai), và m?i l?i đĩa đ?u b? nu?t — logging kh�ng bao gi? đ� app.
/// Ch? t? Warning tr? lên đ? file c�n d?ng d?c (log Info s? v�i stack).
/// </remarks>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _gate = new();

    /// <summary>
    /// Creates the provider; <paramref name="logDirectory"/> overrides the default
    /// <c>%LOCALAPPDATA%\ReTran\logs</c> (tests).
    /// T?o provider; <paramref name="logDirectory"/> ghi đè m?c d?nh
    /// <c>%LOCALAPPDATA%\ReTran\logs</c> (test).
    /// </summary>
    internal FileLoggerProvider(string? logDirectory = null) =>
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTran", "logs");

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release: records are appended per write and the provider owns no handle.
        // Không có gì cần giải phóng: từng bản ghi append theo lần ghi và provider không giữ handle.
    }

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(
                    Path.Combine(_logDirectory, $"app-{DateTime.Now:yyyyMMdd}.log"),
                    line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Đĩa từ chối (khoá/đầy/quyền) — bỏ dòng log, không bao giờ để logging làm app sập.
            // Disk refused (lock/full/ACL) — drop the line; logging must never crash the app.
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string categoryName) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message = formatter(state, exception);
            string line = exception is null
                ? $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {categoryName}: {message}"
                : $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {categoryName}: {message}{Environment.NewLine}{exception}";

            owner.Write(line);
        }
    }
}
