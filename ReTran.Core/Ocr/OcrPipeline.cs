using System.Diagnostics;
using System.Text.Json.Nodes;
using ReTran.Core.OcrClient;

namespace ReTran.Core.Ocr;

/// <summary>
/// Runs one OCR pass through the sidecar and appends Core's new-text diff to the result.
/// Chạy một lượt OCR qua sidecar và nối diff text mới của Core vào kết quả.
/// </summary>
public sealed class OcrPipeline
{
    private readonly ISidecarRpc _rpc;
    private readonly OcrDiff _diff;

    /// <summary>
    /// Per-call timeout including first-time model load (spec: 30s).
    /// Timeout mỗi lời gọi, tính cả lần nạp model đầu tiên (spec: 30s).
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Creates a pipeline over an RPC channel and a session diff tracker. Tạo pipeline trên kênh RPC và bộ diff theo phiên.</summary>
    public OcrPipeline(ISidecarRpc rpc, OcrDiff diff)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(diff);
        _rpc = rpc;
        _diff = diff;
    }

    /// <summary>
    /// Calls sidecar ocr.spot for a frame already published into the OCR shared-memory section
    /// (<see cref="OcrShm"/>) and returns {spots, elapsed_ms, new_texts}. The image itself
    /// travels through shared memory; the request only carries {seq, width, height}.
    /// Gọi ocr.spot của sidecar cho khung đã ghi vào vùng shared memory OCR (<see cref="OcrShm"/>)
    /// và trả về {spots, elapsed_ms, new_texts}. Ảnh đi qua shared memory; request chỉ mang
    /// {seq, width, height}.
    /// </summary>
    /// <param name="seq">Stable seq returned by <c>OcrShmWriter.Publish</c> for the frame. Seq ổn định do <c>OcrShmWriter.Publish</c> trả về cho khung.</param>
    /// <param name="width">Frame width in pixels (informational). Chiều rộng khung theo pixel (thông tin).</param>
    /// <param name="height">Frame height in pixels (informational). Chiều cao khung theo pixel (thông tin).</param>
    /// <param name="ct">Caller cancellation token. Token hủy của caller.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="seq"/>, <paramref name="width"/> or <paramref name="height"/> is zero or negative. Khi <paramref name="seq"/>, <paramref name="width"/> hoặc <paramref name="height"/> bằng 0 hoặc âm.</exception>
    /// <exception cref="TimeoutException">Thrown when <see cref="Timeout"/> elapses. Khi hết <see cref="Timeout"/>.</exception>
    /// <exception cref="SidecarRpcException">Propagated from the sidecar (a failed shared-memory read surfaces as -32603). Đẩy ra từ sidecar (đọc shared memory thất bại thành -32603).</exception>
    /// <exception cref="InvalidOperationException">Thrown when the reply has no spots array. Khi phản hồi thiếu mảng spots.</exception>
    public Task<JsonObject> SpotAsync(long seq, int width, int height, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seq);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return SpotAsync(new JsonObject
        {
            ["seq"] = seq,
            ["width"] = width,
            ["height"] = height,
        }, ct);
    }

    /// <summary>
    /// Calls sidecar ocr.spot with a base64 JPEG (or PNG) image and returns {spots, elapsed_ms, new_texts}.
    /// Gọi ocr.spot với ảnh JPEG (hoặc PNG) base64 và trả về {spots, elapsed_ms, new_texts}.
    /// </summary>
    /// <param name="imageBase64">Base64-encoded JPEG or PNG without data-url prefix. JPEG hoặc PNG base64 không prefix data-url.</param>
    /// <param name="ct">Caller cancellation token. Token hủy của caller.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="imageBase64"/> is null. Khi <paramref name="imageBase64"/> null.</exception>
    /// <exception cref="TimeoutException">Thrown when <see cref="Timeout"/> elapses. Khi hết <see cref="Timeout"/>.</exception>
    /// <exception cref="SidecarRpcException">Propagated from the sidecar. Được đẩy ra từ sidecar.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the reply has no spots array. Khi phản hồi thiếu mảng spots.</exception>
    public Task<JsonObject> SpotAsync(string imageBase64, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageBase64);
        return SpotAsync(new JsonObject { ["image"] = imageBase64 }, ct);
    }

    /// <summary>
    /// Shared request body: one ocr.spot call with per-call timeout, then Core's new-text diff
    /// appended to the sidecar's reply.
    /// Thân request dùng chung: một lời gọi ocr.spot với timeout mỗi lần, rồi nối diff text mới
    /// của Core vào phản hồi của sidecar.
    /// </summary>
    /// <param name="params">JSON-RPC params (either {image} or {seq,width,height}). Tham số JSON-RPC ({image} hoặc {seq,width,height}).</param>
    /// <param name="ct">Caller cancellation token. Token hủy của caller.</param>
    /// <exception cref="TimeoutException">Thrown when <see cref="Timeout"/> elapses. Khi hết <see cref="Timeout"/>.</exception>
    /// <exception cref="SidecarRpcException">Propagated from the sidecar. Được đẩy ra từ sidecar.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the reply has no spots array. Khi phản hồi thiếu mảng spots.</exception>
    private async Task<JsonObject> SpotAsync(JsonObject @params, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        JsonNode? result;
        try
        {
            result = await _rpc.CallAsync("ocr.spot", @params, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"ocr.spot timed out after {Timeout.TotalSeconds:0}s (includes first-time model load).");
        }

        if (result is not JsonObject obj || obj["spots"] is not JsonArray spotsArr)
            throw new InvalidOperationException("sidecar returned no 'spots' array.");

        var texts = new List<string>();
        foreach (var s in spotsArr)
            if (s is JsonObject so && so["text"] is JsonValue tv && tv.TryGetValue<string>(out var t))
                texts.Add(t);

        var newArr = new JsonArray();
        foreach (var n in _diff.Feed(texts)) newArr.Add(n);
        obj["new_texts"] = newArr;
        return obj;
    }
}
