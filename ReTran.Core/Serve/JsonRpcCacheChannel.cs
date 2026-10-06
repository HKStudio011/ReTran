namespace ReTran.Core.Serve;

using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;

/// <summary>
/// Production cache channel that talks to the App over the core's JSON-RPC peer:
/// batch lookup via <c>translate.request_cache</c> (request/response, self-cancels after 2 s;
/// the hard round-trip bound lives in <see cref="TranslateCycleSink"/>) and
/// best-effort write-back via the <c>translate.put</c> notification.
/// Kênh cache production trao đổi với App qua JSON-RPC peer của core: tra lô qua
/// <c>translate.request_cache</c> (request/response, tự hủy sau 2 s; giới cứng lượt tra nằm ở
/// <see cref="TranslateCycleSink"/>) và ghi best-effort qua thông báo <c>translate.put</c>.
/// </summary>
/// <remarks>
/// Any failure (timeout, peer error, malformed reply) is thrown so <see cref="TranslateCycleSink"/>
/// can treat the whole batch as a cache miss — the cycle never stalls on the App.
/// Mọi lỗi (hết giờ, lỗi peer, phản hồi sai định dạng) đều được ném ra để
/// <see cref="TranslateCycleSink"/> coi cả lô là miss — chu kỳ không bao giờ bị chặn vì App.
/// </remarks>
public sealed class JsonRpcCacheChannel : ICacheChannel
{
    /// <summary>Cooperative cancel for one lookup round trip (<c>CancelAfter</c>); the hard 2 s
    /// bound is enforced by the sink's own timeout. Hủy hợp tác cho một lượt tra
    /// (<c>CancelAfter</c>); giới cứng 2 s do timeout của sink đảm nhận.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    private readonly JsonRpcServer _server;

    /// <summary>
    /// Creates a channel backed by <paramref name="server"/>.
    /// Tạo kênh dựa trên <paramref name="server"/>.
    /// </summary>
    /// <param name="server">Peer server used for both request and notification. Peer server dùng cho cả request và notification.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="server"/> is null. Ném khi <paramref name="server"/> là null.</exception>
    public JsonRpcCacheChannel(JsonRpcServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    /// <summary>
    /// Looks up a batch of texts via <c>translate.request_cache</c>. The channel cancels itself
    /// after 2 s and honors <paramref name="ct"/>; the hard round-trip bound is enforced by the
    /// caller (<see cref="TranslateCycleSink"/>), not by a wait inside this method.
    /// Tra một lô text qua <c>translate.request_cache</c>. Kênh tự hủy sau 2 s và tôn trọng
    /// <paramref name="ct"/>; giới cứng lượt tra do chính caller
    /// (<see cref="TranslateCycleSink"/>) đảm nhận, không phải bằng một lượt chờ trong method này.
    /// </summary>
    /// <param name="context">Content-context key (e.g. <c>screen:0</c>). Key ngữ cảnh nội dung (vd <c>screen:0</c>).</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="texts">Source texts to look up. Text nguồn cần tra.</param>
    /// <param name="ct">Token used to cancel the wait. Token dùng để hủy lượt chờ.</param>
    /// <returns>The hits/misses partition returned by the App. Phân hoạch hit/miss do App trả về.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the connection closes before a reply arrives.
    /// Khi kết nối đóng trước khi có phản hồi.
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled or the internal 2 s <c>CancelAfter</c>
    /// elapses — <c>JsonRpcServer.CallAsync</c> cancels its waiter and surfaces this, never a
    /// <see cref="TimeoutException"/>.
    /// Khi <paramref name="ct"/> bị hủy hoặc 2 s <c>CancelAfter</c> bên trong hết hạn —
    /// <c>JsonRpcServer.CallAsync</c> hủy lượt chờ và xuất hiện dạng này, không bao giờ là
    /// <see cref="TimeoutException"/>.
    /// </exception>
    /// <exception cref="RpcMethodException">Thrown when the App answers with an error object. Khi App trả về đối tượng lỗi.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the reply is not a <c>{hits, misses}</c> object. Khi phản hồi không phải object <c>{hits, misses}</c>.</exception>
    public async Task<CacheLookupResult> LookupAsync(
        string context, string src, string dst, IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(texts);

        var request = new JsonObject
        {
            ["context"] = context,
            ["src"] = src,
            ["dst"] = dst,
            ["texts"] = new JsonArray(texts.Select(t => (JsonNode?)t).ToArray()),
        };

        using var lookupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookupCts.CancelAfter(LookupTimeout);
        JsonNode? result = await _server.CallAsync("translate.request_cache", request, lookupCts.Token);

        if (result is not JsonObject obj
            || obj["hits"] is not JsonObject hitsNode
            || obj["misses"] is not JsonArray missesNode)
        {
            throw new InvalidOperationException("translate.request_cache reply must be {hits:{...}, misses:[...]}.");
        }

        var hits = new Dictionary<string, string>();
        foreach (KeyValuePair<string, JsonNode?> pair in hitsNode)
        {
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null)
                hits[pair.Key] = text;
        }
        var misses = new List<string>();
        foreach (JsonNode? node in missesNode)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null)
                misses.Add(text);
        }
        return new CacheLookupResult(hits, misses);
    }

    /// <summary>
    /// Sends newly translated entries to the App via the <c>translate.put</c> notification; an empty batch is a no-op.
    /// Gửi các mục vừa dịch cho App qua thông báo <c>translate.put</c>; lô rỗng thì không làm gì.
    /// </summary>
    /// <param name="context">Content-context key the translations belong to. Key ngữ cảnh nội dung mà bản dịch thuộc về.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="entries">Text → translation pairs to store. Cặp text → bản dịch cần lưu.</param>
    /// <param name="ct">Token used to cancel the send. Token dùng để hủy việc gửi.</param>
    /// <returns>A task that completes once the notification was written. Task hoàn tất khi đã ghi xong thông báo.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the peer read loop has not been started. Khi vòng lặp đọc của peer chưa khởi động.</exception>
    public async Task PutAsync(
        string context, string src, string dst,
        IReadOnlyList<(string Text, string Translated)> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        var array = new JsonArray();
        foreach ((string text, string translated) in entries)
            array.Add(new JsonObject { ["text"] = text, ["translated"] = translated });

        await _server.NotifyAsync("translate.put", new JsonObject
        {
            ["context"] = context,
            ["src"] = src,
            ["dst"] = dst,
            ["entries"] = array,
        }, ct);
    }
}
