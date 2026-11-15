using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReTran.Core.Llm;

/// <summary>
/// Minimal OpenAI-compatible chat-completions client: one POST per translation, recommended
/// Hy-MT2 sampling (temperature 0.7, top_p 0.6; llama.cpp-only extras top_k/repeat_penalty are
/// sent in local mode only so remote servers never see unknown fields). No retry — the sink's
/// no-retry session semantics apply.
/// Client chat-completions tối giản theo chuẩn OpenAI: mỗi lượt dịch một POST, tham số lấy mẫu
/// khuyến nghị của Hy-MT2 (temperature 0,7, top_p 0,6; riêng top_k/repeat_penalty chỉ gửi ở chế
/// độ local để server remote không gặp field lạ). Không retry — theo ngữ nghĩa không retry của
/// phiên dịch.
/// </summary>
/// <param name="client">HTTP client used for every request. HTTP client dùng cho mọi yêu cầu.</param>
public sealed class OpenAiChatClient(HttpClient client)
{
    /// <summary>
    /// Request timeout; local generation can be slow on the first tokens and CPU-flavour machines
    /// generate slowly throughout, so the cap is generous. This constant is the EFFECTIVE cap: the
    /// Core wiring creates its <see cref="HttpClient"/> with an infinite timeout so the .NET default
    /// (100 s) can never fire first. Timeout yêu cầu; sinh cục bộ chậm ở token đầu và máy flavor CPU
    /// sinh chậm suốt lượt nên trần đặt rộng. Hằng số này là trần HIỆU LỰC: chỗ nối ở Core tạo
    /// <see cref="HttpClient"/> với timeout vô hạn để mặc định 100 s của .NET không bao giờ chặn trước.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(300);

    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>
    /// Sends one chat completion and returns the trimmed assistant message. Gửi một lượt chat
    /// completion và trả về nội dung trợ lý đã trim.
    /// </summary>
    /// <param name="baseUrl">Endpoint base URL without the path. URL gốc endpoint, chưa kèm path.</param>
    /// <param name="apiKey">Optional bearer key. Bearer key tuỳ chọn.</param>
    /// <param name="model">Model name sent to the endpoint. Tên model gửi tới endpoint.</param>
    /// <param name="systemPrompt">System message; empty means none. Message system; rỗng nghĩa là không gửi.</param>
    /// <param name="userPrompt">User message with all placeholders already filled. Message user với mọi placeholder đã điền.</param>
    /// <param name="local">True to include llama.cpp-only sampling fields. True để gửi thêm các field lấy mẫu riêng llama.cpp.</param>
    /// <param name="ct">Token used to cancel the request. Token dùng để hủy yêu cầu.</param>
    /// <returns>The trimmed assistant content. Nội dung trợ lý đã trim.</returns>
    /// <exception cref="HttpRequestException">Thrown on a non-200 response. Khi phản hồi khác 200.</exception>
    /// <exception cref="TimeoutException">Thrown when the request exceeds <see cref="Timeout"/>. Khi yêu cầu vượt <see cref="Timeout"/>.</exception>
    /// <exception cref="FormatException">Thrown when the body is not a usable chat-completions response. Khi body không phải phản hồi chat-completions dùng được.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> cancels. Khi <paramref name="ct"/> hủy.</exception>
    public async Task<string> CompleteAsync(
        string baseUrl, string? apiKey, string model, string systemPrompt, string userPrompt,
        bool local, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.7,
            ["top_p"] = 0.6,
            ["max_tokens"] = 4096,
        };
        if (local)
        {
            // llama.cpp accepts these extensions; remote OpenAI-compatible servers may not.
            // llama.cpp chấp nhận các field mở rộng này; server remote có thể không.
            body["top_k"] = 20;
            body["repeat_penalty"] = 1.05;
        }

        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = userPrompt });
        body["messages"] = messages;

        string url = baseUrl.TrimEnd('/') + "/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        HttpResponseMessage resp;
        try
        {
            resp = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM request timed out after {Timeout.TotalSeconds:0} s.");
        }

        using (resp)
        {
            string responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                string snippet = responseBody.Length > 300 ? responseBody[..300] : responseBody;
                throw new HttpRequestException($"LLM endpoint returned HTTP {(int)resp.StatusCode}: {snippet}");
            }
            return Parse(responseBody);
        }
    }

    /// <summary>
    /// Extracts and trims <c>choices[0].message.content</c>. Trích và trim
    /// <c>choices[0].message.content</c>.
    /// </summary>
    /// <param name="raw">Raw response body. Body phản hồi thô.</param>
    /// <returns>The trimmed content. Nội dung đã trim.</returns>
    /// <exception cref="FormatException">Thrown when the shape is missing or not a string. Khi thiếu field hoặc sai kiểu.</exception>
    private static string Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("LLM endpoint returned an empty response body.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new FormatException("LLM endpoint returned a body that is not valid JSON.", ex);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out JsonElement message)
                || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("LLM response does not contain choices[0].message.content.");
            }

            return content.GetString()!.Trim();
        }
    }
}
