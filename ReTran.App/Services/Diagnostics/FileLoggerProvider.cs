using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Diagnostics;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> appending Information-and-above records to a daily log file
/// under <c>%LOCALAPPDATA%\ReTran\logs</c>, so a packaged (Release) run is debuggable too: the Debug
/// build has <see cref="TraceLoggerProvider"/> and Blazor devtools, a Release run has neither — the
/// generic Blazor error banner is then the only symptom, which is exactly how the 2026-11-17 wizard
/// crash report surfaced (an unhandled render exception with no stack anywhere). Information level is
/// included so the wizard's flow (start/pause/done) is traceable without a debugger.
/// Provider <see cref="ILoggerProvider"/> toi thieu, ghi cac ban ghi tu Information tro len vao file
/// log theo ngay duoi <c>%LOCALAPPDATA%\ReTran\logs</c>, de phien chay dong goi (Release) cung khai
/// thac duoc: build Debug co <see cref="TraceLoggerProvider"/> va Blazor devtools, con Release thi
/// khong co ca hai — banner loi Blazor chung chung la trieu chung duy nhat, dung cach bao cao crash
/// wizard 2026-11-17 xuat hien (exception render khong xu ly ma khong co stack o dau). Muc Information
/// duoc ghi de trace duoc luong wizard (bat dau/dung/xong) ma khong can debugger.
/// </summary>
/// <remarks>
/// Deliberately belt-and-braces: writes are serialized by a lock, the file is opened per record (no
/// long-lived handle to fight a second app instance over), and every disk failure is swallowed —
/// logging must never take the app down. Information-and-up keeps the wizard flow traceable without
/// burying the stacks.
/// Co tinh an-toan-cap-kip: ghi duoc hoa bang lock, file mo theo tung ban ghi (khong giu handle lau
/// de tranh voi instance app thu hai), va moi loi dia deu bi nuot — logging khong bao gio dap app.
/// Tu Information tro len de file con dung doc (log Info nhieu se vui stack).
/// </remarks>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _gate = new();

    /// <summary>
    /// Creates the provider; <paramref name="logDirectory"/> overrides the default
    /// <c>%LOCALAPPDATA%\ReTran\logs</c> (tests).
    /// Tao provider; <paramref name="logDirectory"/> ghi de mac dinh
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
        // Khong co gi can giai phong: tung ban ghi append theo lan ghi va provider khong giu handle.
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
            // Disk refused (lock/full/ACL) — drop the line; logging must never crash the app.
            // Dia tu choi (khoa/day/quyen) — bo dong log, khong bao gio de logging lam app sap.
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string categoryName) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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
