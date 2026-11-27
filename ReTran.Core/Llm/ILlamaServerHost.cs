namespace ReTran.Core.Llm;

/// <summary>
/// Lifecycle surface of one lazily started llama.cpp server.
/// Bề mặt vòng đời của một server llama.cpp được khởi động lười.
/// </summary>
public interface ILlamaServerHost : IDisposable
{
    /// <summary>OpenAI-compatible base URL after the server starts. URL gốc OpenAI-compatible sau khi server khởi động.</summary>
    /// <exception cref="InvalidOperationException">Thrown before the server has started. Ném khi server chưa khởi động.</exception>
    string BaseUrl { get; }

    /// <summary>
    /// Current model-loading phase and nullable IO measurements.
    /// Giai đoạn nạp model hiện tại và các số liệu IO có thể null.
    /// </summary>
    LlmLoadProgressSnapshot LoadProgress { get; }

    /// <summary>
    /// Ensures the process is running and healthy before translation.
    /// Đảm bảo tiến trình đang chạy và khỏe trước khi dịch.
    /// </summary>
    /// <param name="ct">Token used to cancel startup and health waiting. Token hủy khởi động và chờ health.</param>
    /// <exception cref="InvalidOperationException">Thrown when the binary/model is missing or the server never becomes healthy. Ném khi thiếu binary/model hoặc server không khỏe kịp.</exception>
    Task EnsureStartedAsync(CancellationToken ct);
}
