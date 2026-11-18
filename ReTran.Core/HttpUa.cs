namespace ReTran.Core;

/// <summary>
/// Browser-like default headers for the app's outbound <see cref="HttpClient"/> instances. Some hosts
/// treat unknown clients badly — the GitHub API answers 403 without a User-Agent, and the Google web
/// translate endpoint / Hugging Face throttle or challenge bare or library-default UAs — so every
/// client presents the same Chrome-like identity instead of no header at all.
/// Header mặc định giống trình duyệt cho các <see cref="HttpClient"/> đi ra ngoài của app. Một số host
/// đối xử xấu với client lạ — GitHub API trả 403 khi thiếu User-Agent, endpoint web translate của
/// Google / Hugging Face throttle hoặc challenge UA trần hay UA mặc định của thư viện — nên mọi client
/// đều trình diện cùng một danh tính giống Chrome thay vì không có header.
/// </summary>
/// <remarks>
/// Deliberately one shared identity (no per-host differences): a browser-mimicking UA is what
/// CDN-fronted endpoints filter least, and it keeps the app honest-looking to GitHub's policy without
/// a second header set to maintain. Guardrail-style reminder: requests stay read-only GETs against
/// public endpoints — the UA only keeps them from being mistaken for bots.
/// Cố tình dùng một danh tính dùng chung (không phân biệt theo host): UA giả trình duyệt là thứ các
/// endpoint trước CDN lọc ít nhất, và giữ app nhìn hợp lệ theo chính sách GitHub mà không phải duy trì
/// hai bộ header. Nhắc kiểu guardrail: các request là GET chỉ-đọc tới endpoint công khai — UA chỉ để
/// không bị nhầm thành bot.
/// </remarks>
internal static class HttpUa
{
    /// <summary>Chrome-like User-Agent presented by every outbound client. User-Agent giống Chrome mà mọi client đi ra ngoài trình diện.</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>
    /// Applies the browser-like default headers to <paramref name="client"/> (User-Agent +
    /// Accept-Language) and returns it for chaining.
    /// Gán các header mặc định giống trình duyệt cho <paramref name="client"/> (User-Agent +
    /// Accept-Language) rồi trả về để nối chuỗi.
    /// </summary>
    /// <param name="client">Client whose default request headers are updated. Client cần cập nhật default request headers.</param>
    /// <returns>The same client for chaining. Chính client đầu vào để nối chuỗi.</returns>
    public static HttpClient Apply(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> with the browser-like default headers applied.
    /// Tạo <see cref="HttpClient"/> đã gán sẵn các header mặc định giống trình duyệt.
    /// </summary>
    /// <returns>A new client with the browser-like headers. Client mới đã có header giống trình duyệt.</returns>
    public static HttpClient CreateClient() => Apply(new HttpClient());
}
