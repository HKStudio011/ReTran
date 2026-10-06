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
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Shared client for the default path (no custom handler, default timeout), so production calls pool connections.
    /// Client dùng chung cho đường mặc định (không custom handler, timeout mặc định) để production gọi gộp kết nối.
    /// </summary>
    private static readonly HttpClient SharedClient = new() { Timeout = DefaultTimeout };

    private readonly HttpClient _client;

    /// <summary>
    /// Creates a translator over an optional test handler and timeout (default 5 seconds).
    /// Tạo trình dịch với handler test tùy chọn và timeout (mặc định 5 giây).
    /// </summary>
    /// <param name="handler">
    /// Optional <see cref="HttpMessageHandler"/> injected by tests; <see langword="null"/> uses the shared client.
    /// <see cref="HttpMessageHandler"/> tùy chọn do test inject; <see langword="null"/> dùng client dùng chung.
    /// </param>
    /// <param name="timeout">Request timeout; <see langword="null"/> means 5 seconds. Timeout yêu cầu; <see langword="null"/> nghĩa là 5 giây.</param>
    public GoogleWebTranslator(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (handler is null && effectiveTimeout == DefaultTimeout)
        {
            _client = SharedClient;
        }
        else
        {
            _client = handler is null ? new HttpClient() : new HttpClient(handler);
            _client.Timeout = effectiveTimeout;
        }
    }

    /// <summary>
    /// Provider name used for registry lookup. Tên nhà cung cấp dùng để tra trong registry.
    /// </summary>
    public string Name => "google";

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
    {
        var url =
            "https://translate.googleapis.com/translate_a/single"
            + "?client=gtx"
            + $"&sl={Uri.EscapeDataString(src)}"
            + $"&tl={Uri.EscapeDataString(dst)}"
            + "&dt=t"
            + $"&q={Uri.EscapeDataString(text)}";

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

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body);
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

            var sb = new StringBuilder();
            foreach (var seg in segments.EnumerateArray())
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
}
