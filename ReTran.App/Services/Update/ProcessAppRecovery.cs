using System.Diagnostics;

namespace ReTran_App.Services.Update;

/// <summary>
/// Starts a fresh instance of the current application after a post-stop update failure.
/// Khởi chạy một instance mới của ứng dụng hiện tại sau lỗi cập nhật xảy ra sau bước dừng service.
/// </summary>
public interface IAppRecovery
{
    /// <summary>
    /// Starts the current application executable without launching the updater or quitting the old process.
    /// Khởi chạy executable của ứng dụng hiện tại, không khởi chạy updater và không thoát process cũ.
    /// </summary>
    void RestartCurrentApp();
}

/// <summary>
/// Process-based <see cref="IAppRecovery"/> implementation that starts the installed current executable.
/// Triển khai <see cref="IAppRecovery"/> bằng process để khởi chạy executable hiện tại của bản cài đặt.
/// </summary>
public sealed class ProcessAppRecovery : IAppRecovery
{
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    /// <summary>
    /// Creates recovery over <see cref="Environment.ProcessPath"/> and <see cref="AppContext.BaseDirectory"/>.
    /// Tạo recovery từ <see cref="Environment.ProcessPath"/> và <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The current executable path is unavailable. Không xác định được đường dẫn executable hiện tại.</exception>
    public ProcessAppRecovery()
        : this(
            Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable."),
            AppContext.BaseDirectory,
            static startInfo => Process.Start(startInfo))
    {
    }

    internal ProcessAppRecovery(
        string executablePath,
        string workingDirectory,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentNullException.ThrowIfNull(startProcess);

        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
        _startProcess = startProcess;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No process was started. Không có process nào được khởi chạy.</exception>
    /// <remarks>
    /// Process-start failures propagate so the coordinator can log them and quit the torn-down instance.
    /// Lỗi khởi chạy process được ném để coordinator ghi log và thoát instance đã bị dừng service.
    /// </remarks>
    public void RestartCurrentApp()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = true,
        };

        if (_startProcess(startInfo) is null)
        {
            throw new InvalidOperationException("The current application process could not be started.");
        }
    }
}
