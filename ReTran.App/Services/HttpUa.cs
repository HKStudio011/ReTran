namespace ReTran_App.Services;

/// <summary>
/// Browser-like default headers for the app's outbound <see cref="HttpClient"/> instances — same
/// identity as Core's <c>HttpUa</c> (the App does not reference the Core project, so the constant
/// lives here too). Some hosts treat unknown clients badly: the GitHub API answers 403 without a
/// User-Agent (the app-update check runs against it), and CDN-fronted endpoints throttle or challenge
/// bare or library-default UAs.
/// Header mặc định giống trình duyệt cho các <see cref="HttpClient"/> đi ra ngoài của app — cùng danh
/// tính với <c>HttpUa</c> phía Core (App không tham chiếu project Core nên hằng số nằm ở đây). Một số
/// host đối xử xấu với client lạ: GitHub API trả 403 khi thiếu User-Agent (check cập nhật của app gọi
/// thẳng vào đó), còn endpoint trước CDN throttle hoặc challenge UA trần hay UA mặc định của thư viện.
/// </summary>
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
}
