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
    /// Calls sidecar ocr.spot with a base64 PNG and returns {spots, elapsed_ms, new_texts}.
    /// Gọi ocr.spot với PNG base64 và trả về {spots, elapsed_ms, new_texts}.
    /// </summary>
    /// <param name="pngBase64">Base64-encoded PNG without data-url prefix. PNG base64 không prefix data-url.</param>
    /// <param name="ct">Caller cancellation token. Token hủy của caller.</param>
    /// <exception cref="TimeoutException">Thrown when <see cref="Timeout"/> elapses. Khi hết <see cref="Timeout"/>.</exception>
    /// <exception cref="SidecarRpcException">Propagated from the sidecar. Được đẩy ra từ sidecar.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the reply has no spots array. Khi phản hồi thiếu mảng spots.</exception>
    public async Task<JsonObject> SpotAsync(string pngBase64, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pngBase64);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        JsonNode? result;
        try
        {
            result = await _rpc.CallAsync("ocr.spot", new JsonObject { ["image"] = pngBase64 }, cts.Token);
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
