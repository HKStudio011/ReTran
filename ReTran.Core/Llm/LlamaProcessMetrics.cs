using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReTran.Core.Llm;

/// <summary>
/// One cumulative activity sample from a llama-server process.
/// Một mẫu hoạt động tích lũy từ tiến trình llama-server.
/// </summary>
/// <param name="CpuTime">Total kernel and user CPU time. Tổng thởi gian CPU kernel và user.</param>
/// <param name="WorkingSetBytes">Current working-set size, used only for activity detection. Kích thước working-set hiện tại, chỉ dùng phát hiện hoạt động.</param>
/// <param name="ReadBytes">Cumulative IO bytes read. Tổng số byte IO đã đọc tích lũy.</param>
/// <param name="PageFaults">Cumulative page-fault count. Tổng số page fault tích lũy.</param>
public sealed record LlamaProcessSample(
    TimeSpan CpuTime, long WorkingSetBytes, long ReadBytes, long PageFaults);

/// <summary>
/// Reads bounded activity counters for one already-selected process.
/// Đọc các counter hoạt động có giới hạn cho một tiến trình đã được chọn sẵn.
/// </summary>
public interface ILlamaProcessMetrics
{
    /// <summary>
    /// Attempts to read all counters without throwing for an exited or inaccessible process.
    /// Cố gắng đọc toàn bộ counter, không ném exception khi tiến trình đã thoát hoặc không truy cập được.
    /// </summary>
    /// <param name="process">Process whose own counters are read. Tiến trình cần đọc counter của chính nó.</param>
    /// <param name="sample">Latest counters when the read succeeds. Counter mới nhất khi đọc thành công.</param>
    /// <returns>True when every counter is available; otherwise false. True khi mọi counter khả dụng; ngược lại false.</returns>
    bool TryRead(Process process, out LlamaProcessSample sample);
}

/// <summary>
/// Approximate GGUF loading progress derived from process IO, never from working-set size.
/// Tiến trình nạp GGUF xấp xỉ từ IO của tiến trình, tuyệt đối không dùng kích thước working-set.
/// </summary>
/// <param name="Phase">Loading phase: stopped, loading, finalizing, ready, or error. Giai đoạn nạp: stopped, loading, finalizing, ready hoặc error.</param>
/// <param name="BytesLoaded">High-water IO bytes read, or null when unavailable. Mức cao nhất của byte IO đã đọc, hoặc null khi không đo được.</param>
/// <param name="BytesTotal">GGUF file size, or null when unavailable. Kích thước tệp GGUF, hoặc null khi không xác định.</param>
/// <param name="BytesPerSec">Latest measured IO speed, or null without a reliable sample. Tốc độ IO của mẫu mới nhất, hoặc null khi chưa có mẫu tin cậy.</param>
/// <param name="ProgressAvailable">Whether determinate pre-health progress can be shown. Có thể hiển thị tiến trình xác định trước khi healthy hay không.</param>
public sealed record LlmLoadProgressSnapshot(
    string Phase, long? BytesLoaded, long? BytesTotal, double? BytesPerSec, bool ProgressAvailable)
{
    /// <summary>
    /// Gets a monotonic display percentage capped at 99 before health and exactly 100 when ready.
    /// Lấy phần trăm hiển thị đơn điệu, giới hạn 99 trước health và đúng 100 khi ready.
    /// </summary>
    public double? Percent
    {
        get
        {
            if (Phase == "ready") return 100;
            if (!ProgressAvailable || BytesLoaded is not long loaded || BytesTotal is not long total || total <= 0)
                return null;
            return Math.Clamp((double)loaded * 100 / total, 0, 99);
        }
    }
}

/// <summary>
/// Win32 process-counter reader using IO, memory, page-fault and process-time APIs.
/// Bộ đọc counter tiến trình Win32 dùng API IO, bộ nhớ, page fault và thởi gian tiến trình.
/// </summary>
public sealed class LlamaProcessMetrics : ILlamaProcessMetrics
{
    /// <inheritdoc />
    public bool TryRead(Process process, out LlamaProcessSample sample)
    {
        sample = default!;
        try
        {
            IntPtr handle = process.Handle;
            if (!GetProcessIoCounters(handle, out IoCounters io)
                || !GetProcessMemoryInfo(handle, out MemoryCounters memory, (uint)Marshal.SizeOf<MemoryCounters>())
                || !GetProcessTimes(handle, out _, out _, out FileTime kernel, out FileTime user))
            {
                return false;
            }

            ulong cpu100ns = ToUInt64(kernel) + ToUInt64(user);
            sample = new LlamaProcessSample(
                TimeSpan.FromTicks((long)Math.Min(cpu100ns, long.MaxValue)),
                (long)Math.Min(memory.WorkingSetSize, long.MaxValue),
                (long)Math.Min(io.ReadTransferCount, long.MaxValue),
                memory.PageFaultCount);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
            or ObjectDisposedException or NotSupportedException)
        {
            return false;
        }
    }

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process, out MemoryCounters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
        internal nuint PrivateUsage;
    }
}
