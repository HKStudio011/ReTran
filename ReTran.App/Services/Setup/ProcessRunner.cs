using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Setup;

/// <summary>
/// Runs one external command to completion and reports its exit code plus the tail of its merged
/// stdout+stderr. This is the seam that lets every setup step be unit-tested with a fake, and that lets
/// the error screen quote the failing tool's own output (spec §5.1).
/// Chạy một lệnh ngoài tới khi kết thúc và báo exit code cùng đuôi output gộp stdout+stderr. Đây là seam
/// giúp mọi bước setup unit-test được bằng fake, và cho phép màn hình lỗi trích đúng output của lệnh hỏng
/// (spec §5.1).
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="args"/> inside <paramref name="workingDir"/>,
    /// layering <paramref name="extraEnv"/> on top of the current process environment. Cancelling
    /// <paramref name="ct"/> kills the child's whole process tree before the exception propagates.
    /// Chạy <paramref name="fileName"/> với <paramref name="args"/> trong <paramref name="workingDir"/>,
    /// cộng <paramref name="extraEnv"/> trên nền environment của process hiện tại. Hủy
    /// <paramref name="ct"/> sẽ kill toàn bộ cây tiến trình con trước khi exception lan lên.
    /// </summary>
    /// <param name="fileName">
    /// Executable to launch. / Executable cần khởi chạy.
    /// </param>
    /// <param name="args">
    /// One entry per argument — never a hand-joined command line, so quoting stays with
    /// <see cref="ProcessStartInfo"/>. Mỗi entry một argument — không tự nối thành chuỗi lệnh, việc quote
    /// để mặc <see cref="ProcessStartInfo"/>.
    /// </param>
    /// <param name="workingDir">
    /// Working directory for the child. / Thư mục làm việc của tiến trình con.
    /// </param>
    /// <param name="extraEnv">
    /// Extra environment variables, or <see langword="null"/> for none. / Biến môi trường bổ sung, hoặc
    /// <see langword="null"/> nếu không có.
    /// </param>
    /// <param name="ct">
    /// Cancellation token; cancelling kills the tree. / Token hủy; hủy sẽ kill cây tiến trình.
    /// </param>
    /// <returns>
    /// The exit code and at most <see cref="ProcessRunner.OutputTailLimitChars"/> characters of merged
    /// output, ending at the child's last line. / Exit code và tối đa
    /// <see cref="ProcessRunner.OutputTailLimitChars"/> ký tự output gộp, kết thúc đúng dòng cuối của con.
    /// </returns>
    /// <exception cref="Win32Exception">
    /// The executable cannot be started (missing, or quarantined by AV/SmartScreen).
    /// Không khởi động được executable (thiếu, hoặc bị AV/SmartScreen cách ly).
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled; the child tree was killed first.
    /// <paramref name="ct"/> bị hủy; cây tiến trình con đã bị kill trước đó.
    /// </exception>
    Task<(int ExitCode, string OutputTail)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDir,
        IDictionary<string, string>? extraEnv,
        CancellationToken ct);
}

/// <summary>
/// <see cref="IProcessRunner"/> over <see cref="Process"/>: stdio redirected and merged, no window, the
/// last <see cref="OutputTailLimitChars"/> characters of output kept, the current environment inherited
/// plus the caller's extra variables, and cancellation killing the entire child tree (uv spawns Python
/// children, so killing only the direct child would leave them writing to the pipe).
/// <see cref="IProcessRunner"/> bọc <see cref="Process"/>: redirect và gộp stdio, không cửa sổ, giữ đoạn
/// cuối <see cref="OutputTailLimitChars"/> ký tự của output, kế thừa environment hiện tại cộng biến caller
/// thêm, và khi hủy thì kill toàn bộ cây tiến trình con (uv spawn tiến trình Python con, nên kill mỗi con
/// trực tiếp sẽ bỏ chúng tiếp tục ghi vào pipe).
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// How many characters of merged stdout+stderr <see cref="IProcessRunner.RunAsync"/> keeps: ~8 KB,
    /// counted in UTF-16 characters. Older output is dropped from the FRONT because uv, pip and Python
    /// report the reason a command failed at the END (and that tail is what the error screen shows).
    /// Số ký tự output gộp stdout+stderr mà <see cref="IProcessRunner.RunAsync"/> giữ: ~8 KB, tính theo ký
    /// tự UTF-16. Phần cũ bị cắt ở ĐẦU vì uv, pip và Python báo nguyên nhân lỗi ở CUỐI output (đuôi đó là
    /// thứ màn hình lỗi hiển thị).
    /// </summary>
    public const int OutputTailLimitChars = 8_192;

    // Sau khi con exit, reader có thể còn vài dòng trong pipe buffer: chờ EOF ngắn để OutputTail không thiếu
    // dòng cuối, nhưng có trần — một tiến trình cháu vẫn giữ pipe (uv để lại) không được làm treo wizard.
    // After the child exits a few lines may still sit in the pipe buffer: wait briefly for EOF so the tail
    // is never missing its last lines, but cap it — a grandchild still holding the pipe must never wedge
    // the wizard.
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillSettleTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<ProcessRunner>? _logger;

    /// <summary>
    /// Creates the runner. The logger is optional and null-allowed (same shape as
    /// <see cref="VideoAdapterSource"/>) so the class stays constructible before DI registers it; the
    /// wizard path is expected to pass the DI logger so failed uv commands land in the log.
    /// Tạo runner. Logger là optional và cho phép null (cùng shape với <see cref="VideoAdapterSource"/>) để
    /// class vẫn dựng được trước khi DI đăng ký; đường wizard được kỳ vọng truyền logger DI để lệnh uv lỗi
    /// vào được log.
    /// </summary>
    /// <param name="logger">
    /// DI logger, or <see langword="null"/> to log nothing. / Logger DI, hoặc <see langword="null"/> để
    /// không ghi log.
    /// </param>
    public ProcessRunner(ILogger<ProcessRunner>? logger = null) => _logger = logger;

    /// <inheritdoc cref="IProcessRunner.RunAsync"/>
    public async Task<(int ExitCode, string OutputTail)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDir,
        IDictionary<string, string>? extraEnv,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrEmpty(workingDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in args) startInfo.ArgumentList.Add(argument);

        // EnvironmentVariables được init từ environment của process hiện tại ⇒ chỉ cần đè từng khóa của
        // extraEnv, không thay cả block (child vẫn thấy PATH, SYSTEMROOT...).
        // EnvironmentVariables starts as a copy of the current process environment, so only each extraEnv
        // key is overwritten — the child still sees PATH, SYSTEMROOT and friends.
        if (extraEnv is not null)
        {
            foreach ((string key, string value) in extraEnv) startInfo.EnvironmentVariables[key] = value;
        }

        var tail = new TailBuffer(OutputTailLimitChars);
        var stdoutEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => ForwardLine(e.Data, tail, stdoutEof);
        process.ErrorDataReceived += (_, e) => ForwardLine(e.Data, tail, stderrEof);

        _logger?.LogInformation(
            "setup: chạy '{FileName}' {Args} trong '{WorkingDir}'", fileName, string.Join(' ', args), workingDir);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(KillSettleTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Con sống dai sau kill: quyền hủy vẫn trả về caller, không nuốt OperationCanceled.
                // The child outlived the kill: cancellation still propagates, it is never swallowed.
                _logger?.LogWarning("setup: '{FileName}' vẫn chưa thoát sau khi kill", fileName);
            }

            await DrainStreamsAsync(stdoutEof, stderrEof).ConfigureAwait(false);
            _logger?.LogWarning("setup: '{FileName}' bị caller hủy (đã kill cây tiến trình)", fileName);
            throw;
        }

        await DrainStreamsAsync(stdoutEof, stderrEof).ConfigureAwait(false);

        int exitCode = process.ExitCode;
        string outputTail = tail.Snapshot();
        if (exitCode != 0)
        {
            _logger?.LogWarning("setup: '{FileName}' exit {ExitCode}: {OutputTail}", fileName, exitCode, outputTail);
        }

        return (exitCode, outputTail);
    }

    /// <summary>
    /// Appends one redirected line, or signals EOF when <see cref="Process"/> reports a null line (the
    /// sentinel that ends <c>BeginOutputReadLine</c>).
    /// Nối một dòng đã redirect, hoặc báo EOF khi <see cref="Process"/> trả dòng null (tín hiệu kết thúc
    /// <c>BeginOutputReadLine</c>).
    /// </summary>
    private static void ForwardLine(string? line, TailBuffer tail, TaskCompletionSource eof)
    {
        if (line is null) eof.TrySetResult();
        else tail.Append(line);
    }

    /// <summary>
    /// Waits for both redirected readers to reach EOF, bounded by <see cref="StreamDrainTimeout"/>. Without
    /// this the returned tail can lose the last lines, because the async readers finish slightly after the
    /// process handle signals exit.
    /// Chờ cả hai reader đã redirect chạm EOF, giới hạn bởi <see cref="StreamDrainTimeout"/>. Không có bước
    /// này thì tail trả về có thể mất mấy dòng cuối, vì reader async kết thúc hơi trễ so với lúc handle của
    /// process báo exit.
    /// </summary>
    private static async Task DrainStreamsAsync(TaskCompletionSource stdoutEof, TaskCompletionSource stderrEof)
    {
        try
        {
            await Task.WhenAll(stdoutEof.Task, stderrEof.Task).WaitAsync(StreamDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Output đã nhận được vẫn còn giá trị cho màn hình lỗi — không ném thêm.
            // Whatever was captured is still useful to the error screen — do not add another throw.
        }
    }

    /// <summary>
    /// Best-effort <c>Kill(entireProcessTree: true)</c>; a child that exits mid-kill is not an error (the
    /// same tolerance used by <c>CoreProcessClient</c> and <c>SidecarProcess</c>).
    /// <c>Kill(entireProcessTree: true)</c> best-effort; con exit ngay giữa lúc kill không phải lỗi (cùng
    /// tolerance như <c>CoreProcessClient</c> và <c>SidecarProcess</c>).
    /// </summary>
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process đã thoát/giải phóng. / The process already exited or was disposed.
        }
        catch (Win32Exception)
        {
            // Kill thắng/thua race với exit — coi như đã kill. / Kill raced with exit — treat as killed.
        }
    }

    /// <summary>
    /// Thread-safe ring of merged output lines that keeps only the last characters: stdout and stderr
    /// arrive on separate reader threads, so every access takes a lock. The buffer grows to 4× the limit
    /// before cutting back to 2× so a chatty command is not trimmed line by line.
    /// Vòng đệm line output gộp, thread-safe, chỉ giữ các ký tự cuối: stdout và stderr đến từ hai luồng
    /// reader khác nhau nên mọi truy cập phải có lock. Buffer phình tới 4× limit rồi mới cắt về 2× để lệnh
    /// nói nhiều không bị cắt từng dòng.
    /// </summary>
    private sealed class TailBuffer
    {
        private readonly object _gate = new();
        private readonly StringBuilder _buffer = new();
        private readonly int _limit;

        public TailBuffer(int limit) => _limit = limit;

        public void Append(string line)
        {
            lock (_gate)
            {
                _buffer.Append(line).Append('\n');
                if (_buffer.Length > _limit * 4) _buffer.Remove(0, _buffer.Length - _limit * 2);
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                int length = _buffer.Length;
                return length <= _limit ? _buffer.ToString() : _buffer.ToString(length - _limit, _limit);
            }
        }
    }
}
