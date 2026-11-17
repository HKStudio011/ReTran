using System.Net;
using System.Text;
using System.Text.Json;

namespace ReTran.Core.Translate;

/// <summary>
/// Translator that calls the public Google web endpoint (no API key required).
/// Trình dịch gọi endpoint web công khai của Google (không cần API key).
/// </summary>
public sealed class GoogleWebTranslator : ITranslator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shared client for the default path (no custom handler, default timeout), so production calls pool connections. Browser-like headers: the Google web endpoint challenges/throttles bare UAs.
    /// Client dùng chung cho đường mặc định (không custom handler, timeout mặc định) để production gọi gộp kết nối. Header giống trình duyệt: endpoint web của Google challenge/throttle UA trần.
    /// </summary>
    private static readonly HttpClient SharedClient = HttpUa.Apply(new HttpClient { Timeout = DefaultTimeout });

    private readonly HttpClient _client;

    /// <summary>
    /// Creates a translator over an optional test handler and timeout (default 60 seconds).
    /// Tạo trình dịch với handler test tùy chọn và timeout (mặc định 60 giây).
    /// </summary>
    /// <param name="handler">
    /// Optional <see cref="HttpMessageHandler"/> injected by tests; <see langword="null"/> uses the shared client.
    /// <see cref="HttpMessageHandler"/> tùy chọn do test inject; <see langword="null"/> dùng client dùng chung.
    /// </param>
    /// <param name="timeout">Request timeout; <see langword="null"/> means 60 seconds. Timeout yêu cầu; <see langword="null"/> nghĩa là 60 giây.</param>
    public GoogleWebTranslator(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (handler is null && effectiveTimeout == DefaultTimeout)
        {
            _client = SharedClient;
        }
        else
        {
            _client = HttpUa.Apply(handler is null ? new HttpClient() : new HttpClient(handler));
            _client.Timeout = effectiveTimeout;
        }
    }

    /// <summary>
    /// Provider name used for registry lookup. Tên nhà cung cấp dùng để tra trong registry.
    /// </summary>
    public string Name => "google";

    /// <summary>
    /// Maximum URL-escaped length of the newline-joined texts sent in a single request.
    /// Độ dài tối đa sau khi escape URL của phần văn bản gộp bằng xuống dòng gửi trong một yêu cầu.
    /// </summary>
    public const int MaxBatchUrlLength = 1800;

    /// <summary>
    /// Translates <paramref name="text"/> by concatenating the segments returned by Google.
    /// Dịch <paramref name="text"/> bằng cách nối các đoạn Google trả về.
    /// </summary>
    /// <param name="text">Text to translate. Văn bản cần dịch.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="ct">Token used to cancel the request. Token dùng để hủy yêu cầu.</param>
    /// <returns>The concatenated translated segments. Các đoạn dịch đã nối lại.</returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the endpoint answers with a non-200 status.
    /// Khi endpoint trả về status khác 200.
    /// </exception>
    /// <exception cref="FormatException">
    /// Thrown when the response body is empty or does not match the expected shape.
    /// Khi body phản hồi rỗng hoặc sai cấu trúc.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the request exceeds the configured timeout.
    /// Khi yêu cầu vượt quá timeout đã cấu hình.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> cancels the request.
    /// Khi <paramref name="ct"/> hủy yêu cầu.
    /// </exception>
    public async Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct = default)
        => Parse(await GetRawAsync(BuildUrl(src, dst, text), ct).ConfigureAwait(false));

    /// <summary>
    /// Translates several texts in as few Google requests as the URL length budget allows.
    /// Dịch nhiều văn bản trong ít yêu cầu Google nhất mà ngân sách độ dài URL cho phép.
    /// </summary>
    /// <param name="texts">Source texts to translate. Các văn bản nguồn cần dịch.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="ct">Token used to cancel the request. Token dùng để hủy yêu cầu.</param>
    /// <returns>
    /// One translated text per input, in the same order as <paramref name="texts"/>.
    /// Mỗi văn bản nguồn cho một kết quả đã dịch, giữ đúng thứ tự như <paramref name="texts"/>.
    /// </returns>
    /// <exception cref="FormatException">
    /// Thrown when a response has a mapping mismatch or returns a different number of translations than requested texts.
    /// Khi phản hồi lệch ánh xạ hoặc trả về số lượng bản dịch khác số văn bản đã yêu cầu.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when the endpoint answers with a non-200 status.
    /// Khi endpoint trả về status khác 200.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the request exceeds the configured timeout.
    /// Khi yêu cầu vượt quá timeout đã cấu hình.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> cancels the request.
    /// Khi <paramref name="ct"/> hủy yêu cầu.
    /// </exception>
    /// <remarks>
    /// Newlines inside each text are collapsed to spaces before joining, because the newline is the
    /// separator used to split the response back into per-text results. Calls are issued one chunk at a
    /// time so a failure never returns a partial result.
    /// Ký tự xuống dòng trong mỗi văn bản được thay bằng dấu cách trước khi gộp, vì dấu xuống dòng là dấu
    /// ngăn cách dùng để tách phản hồi trở lại thành kết quả theo từng văn bản. Các lô được gọi lần lượt nên
    /// khi lỗi sẽ không trả về kết quả dở dang.
    /// </remarks>
    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts, string src, string dst, CancellationToken ct = default)
    {
        var cleaned = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            cleaned.Add(text.Replace('\n', ' ').Replace('\r', ' '));
        }

        var results = new List<string>(texts.Count);
        foreach (var chunk in Chunk(cleaned))
        {
            var raw = await GetRawAsync(BuildUrl(src, dst, string.Join('\n', chunk)), ct).ConfigureAwait(false);
            results.AddRange(ParseBatch(raw, chunk));
        }

        return results;
    }

    /// <summary>
    /// Packs <paramref name="cleaned"/> into the fewest chunks whose newline-joined, URL-escaped form
    /// fits <see cref="MaxBatchUrlLength"/>; a text that alone exceeds the budget gets its own chunk.
    /// Gói <paramref name="cleaned"/> vào ít nhóm nhất sao cho dạng gộp bằng xuống dòng rồi escape URL vừa
    /// <see cref="MaxBatchUrlLength"/>; văn bản đơn vượt ngân sách sẽ nằm trong nhóm riêng.
    /// </summary>
    /// <param name="cleaned">Texts with newlines already collapsed to spaces. Các văn bản đã thay xuống dòng bằng dấu cách.</param>
    /// <returns>The chunks, in input order. Các nhóm, theo thứ tự đầu vào.</returns>
    private static List<List<string>> Chunk(IReadOnlyList<string> cleaned)
    {
        var chunks = new List<List<string>>();
        var current = new List<string>();
        foreach (var text in cleaned)
        {
            if (current.Count > 0
                && Uri.EscapeDataString(string.Join('\n', current.Append(text))).Length > MaxBatchUrlLength)
            {
                chunks.Add(current);
                current = [];
            }

            current.Add(text);
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    /// <summary>
    /// Maps a response back to one translation per requested text by matching the echoed originals
    /// against <paramref name="texts"/> in order.
    /// Ánh xạ phản hồi trở lại thành một bản dịch cho mỗi văn bản đã yêu cầu bằng cách khớp bản gốc echo
    /// với <paramref name="texts"/> theo thứ tự.
    /// </summary>
    /// <param name="raw">Raw response body. Body phản hồi thô.</param>
    /// <param name="texts">The chunk's texts, in request order. Các văn bản của nhóm, theo thứ tự yêu cầu.</param>
    /// <returns>One translation per text, positionally aligned. Một bản dịch cho mỗi văn bản, khớp theo vị trí.</returns>
    /// <exception cref="FormatException">
    /// Thrown when the body is empty, malformed, or its echoed originals do not match <paramref name="texts"/> exactly.
    /// Khi body rỗng, sai cấu trúc, hoặc bản gốc echo không khớp chính xác với <paramref name="texts"/>.
    /// </exception>
    private static List<string> ParseBatch(string raw, IReadOnlyList<string> texts)
    {
        var segments = ExtractSegments(raw);

        var results = new List<string>(texts.Count);
        var accOrig = new StringBuilder();
        var accTrans = new StringBuilder();
        var idx = 0;
        foreach (var seg in segments)
        {
            // Only an empty array is padding to skip. Anything else must be a usable
            // [translated, original, ...] pair, so a malformed segment is a hard mismatch.
            // Chỉ array rỗng là phần đệm được bỏ qua. Mọi thứ khác phải là cặp
            // [bản dịch, bản gốc, ...] dùng được, nên segment sai cấu trúc là lệch cứng.
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() == 0)
            {
                continue;
            }

            if (seg.ValueKind != JsonValueKind.Array
                || seg.GetArrayLength() < 2
                || seg[1].ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Google translate response did not echo the requested texts.");
            }

            if (idx >= texts.Count)
            {
                throw new FormatException("Google translate returned more segments than requested texts.");
            }

            var translated = seg[0];
            if (translated.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Google translate response contained a non-string translated segment.");
            }

            accTrans.Append(translated.GetString());
            accOrig.Append(seg[1].GetString());

            if (TrimTrailingNewline(accOrig.ToString()) == texts[idx])
            {
                results.Add(TrimTrailingNewline(accTrans.ToString()));
                accOrig.Clear();
                accTrans.Clear();
                idx++;
            }
        }

        if (idx != texts.Count || accOrig.Length != 0)
        {
            throw new FormatException("Google translate response did not echo the requested texts.");
        }

        return results;
    }

    /// <summary>
    /// Removes a single trailing newline, matching the separator Google echoes after each line.
    /// Bỏ một ký tự xuống dòng ở cuối, khớp dấu ngăn cách Google echo sau mỗi dòng.
    /// </summary>
    /// <param name="s">Candidate text. Văn bản cần xét.</param>
    /// <returns>The text without its trailing newline. Văn bản đã bỏ xuống dòng ở cuối.</returns>
    private static string TrimTrailingNewline(string s) => s.EndsWith('\n') ? s[..^1] : s;

    /// <summary>
    /// Builds the Google web endpoint URL for a single request.
    /// Dựng URL endpoint web của Google cho một yêu cầu.
    /// </summary>
    /// <param name="src">Source language code. Mã ngôn ngữ nguồn.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="q">Text to translate (newline-joined for batches). Văn bản cần dịch (gộp bằng xuống dòng cho lô).</param>
    /// <returns>The request URL. URL yêu cầu.</returns>
    private static string BuildUrl(string src, string dst, string q) =>
        "https://translate.googleapis.com/translate_a/single"
        + "?client=gtx"
        + $"&sl={Uri.EscapeDataString(src)}"
        + $"&tl={Uri.EscapeDataString(dst)}"
        + "&dt=t"
        + $"&q={Uri.EscapeDataString(q)}";

    /// <summary>
    /// Performs the HTTP request and enforces the status and timeout contract.
    /// Thực hiện yêu cầu HTTP và áp đặt ràng buộc về status và timeout.
    /// </summary>
    /// <param name="url">Fully built request URL. URL yêu cầu đã dựng đầy đủ.</param>
    /// <param name="ct">Token used to cancel the request. Token dùng để hủy yêu cầu.</param>
    /// <returns>The raw response body. Body phản hồi thô.</returns>
    /// <exception cref="HttpRequestException">Thrown on a non-200 status. Khi status khác 200.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the client timeout cancels the request and the cancellation is translated to <see cref="TimeoutException"/>.
    /// Khi timeout của client hủy yêu cầu và thao tác hủy được chuyển thành <see cref="TimeoutException"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Rethrown when <paramref name="ct"/> cancels the request.
    /// Được ném lại khi <paramref name="ct"/> hủy yêu cầu.
    /// </exception>
    private async Task<string> GetRawAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _client.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Google translation timed out after {_client.Timeout}.");
        }

        using (resp)
        {
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException(
                    $"Google translate endpoint returned HTTP {(int)resp.StatusCode}.");
            }

            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Validates the shared response envelope and returns the raw segment elements of <c>root[0]</c>.
    /// Kiểm tra phần bao chung của phản hồi và trả về các phần tử segment thô của <c>root[0]</c>.
    /// </summary>
    /// <param name="raw">Raw response body. Body phản hồi thô.</param>
    /// <returns>
    /// The segments, detached from the parsed document so they outlive it.
    /// Các segment, đã tách khỏi document đã parse để còn dùng được sau khi document bị giải phóng.
    /// </returns>
    /// <exception cref="FormatException">
    /// Thrown when the body is empty, is not valid JSON, or lacks a segment list.
    /// Khi body rỗng, không phải JSON hợp lệ, hoặc thiếu danh sách segment.
    /// </exception>
    private static List<JsonElement> ExtractSegments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("Google translate returned an empty response body.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Google translate returned a body that is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                throw new FormatException("Google translate response does not contain a segment list.");
            }

            var segments = root[0];
            if (segments.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Google translate segment list is not an array.");
            }

            var list = new List<JsonElement>();
            foreach (var seg in segments.EnumerateArray())
            {
                list.Add(seg.Clone());
            }

            return list;
        }
    }

    /// <summary>
    /// Extracts and concatenates the translated segments from a Google response body.
    /// Trích và nối các đoạn dịch từ body phản hồi của Google.
    /// </summary>
    /// <param name="raw">Raw response body. Body phản hồi thô.</param>
    /// <returns>The concatenated segments. Các đoạn đã nối.</returns>
    /// <exception cref="FormatException">Thrown when the body is empty or malformed. Khi body rỗng hoặc sai cấu trúc.</exception>
    private static string Parse(string raw)
    {
        var segments = ExtractSegments(raw);

        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            if (seg.ValueKind != JsonValueKind.Array || seg.GetArrayLength() == 0)
            {
                continue;
            }

            var piece = seg[0];
            if (piece.ValueKind == JsonValueKind.String)
            {
                sb.Append(piece.GetString());
            }
        }

        return sb.ToString();
    }
}
