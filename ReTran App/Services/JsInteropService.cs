using Microsoft.JSInterop;

namespace ReTran_App.Services;

/// <summary>
/// Bridges Blazor pages and the Vite-built JS module (overlay preview, clipboard, viewport).
/// Cầu nối giữa trang Blazor và module JS do Vite build (preview overlay, clipboard, viewport).
/// </summary>
public class JsInteropService : IAsyncDisposable
{
    private Lazy<Task<IJSObjectReference>>? moduleTask;
    private IJSRuntime? jsRuntime;

    /// <summary>
    /// Whether the WebView has signaled readiness for JS calls.
    /// WebView đã báo sẵn sàng nhận lệnh gọi JS hay chưa.
    /// </summary>
    public bool IsWebViewReady { get; private set; } = false;

    /// <summary>
    /// Raised when the WebView becomes ready.
    /// Kích hoạt khi WebView sẵn sàng.
    /// </summary>
    public event Action? OnWebViewReady;

    /// <summary>
    /// Marks the WebView as ready and notifies subscribers.
    /// Đánh dấu WebView đã sẵn sàng và thông báo cho subscriber.
    /// </summary>
    public void NotifyWebViewReady()
    {
        IsWebViewReady = true;
        OnWebViewReady?.Invoke();
    }

    /// <summary>
    /// Attaches the JS runtime and prepares the Vite module import.
    /// Gắn JS runtime và chuẩn bị import module Vite.
    /// </summary>
    /// <param name="runtime">The Blazor JS runtime. JS runtime của Blazor.</param>
    public void Initialize(IJSRuntime runtime)
    {
        jsRuntime = runtime;
        moduleTask = new(() =>
            jsRuntime.InvokeAsync<IJSObjectReference>("import", "./build/assets/main.js").AsTask());
    }

    /// <summary>
    /// Invokes a void JS function in the Vite module (no-op until the WebView is ready).
    /// Gọi hàm JS không trả giá trị trong module Vite (không làm gì cho tới khi WebView sẵn sàng).
    /// </summary>
    /// <param name="method">The exported JS function name. Tên hàm JS được export.</param>
    /// <param name="args">Arguments passed to the JS function. Tham số truyền cho hàm JS.</param>
    public async ValueTask InvokeVoidAsync(string method, params object[] args)
    {
        if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return;
        var module = await moduleTask.Value;
        await module.InvokeVoidAsync(method, args);
    }

    /// <summary>
    /// Invokes a JS function in the Vite module and returns its result (default until the WebView is ready).
    /// Gọi hàm JS trong module Vite và trả về kết quả (giá trị mặc định cho tới khi WebView sẵn sàng).
    /// </summary>
    /// <typeparam name="T">The expected result type. Kiểu kết quả mong đợi.</typeparam>
    /// <param name="method">The exported JS function name. Tên hàm JS được export.</param>
    /// <param name="args">Arguments passed to the JS function. Tham số truyền cho hàm JS.</param>
    /// <returns>The JS result, or default when not ready. Kết quả JS, hoặc giá trị mặc định khi chưa sẵn sàng.</returns>
    public async ValueTask<T> InvokeAsync<T>(string method, params object[] args)
    {
        if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return default!;
        var module = await moduleTask.Value;
        return await module.InvokeAsync<T>(method, args);
    }

    /// <summary>
    /// Returns the viewport dimensions as [width, height], or <see langword="null"/>
    /// when the web view is not ready yet.
    /// Trả về kích thước viewport dạng [rộng, cao], hoặc <see langword="null"/>
    /// khi web view chưa sẵn sàng.
    /// </summary>
    /// <returns>The viewport size as a two-element array, or <see langword="null"/>. Kích thước viewport dạng mảng 2 phần tử, hoặc <see langword="null"/>.</returns>
    public async ValueTask<double[]?> GetViewportSizeAsync()
    {
        if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return null;
        var module = await moduleTask.Value;
        return await module.InvokeAsync<double[]>("getViewportSize");
    }

    /// <summary>
    /// Releases the imported JS module.
    /// Giải phóng module JS đã import.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (IsWebViewReady && moduleTask != null && moduleTask.IsValueCreated)
        {
            var module = await moduleTask.Value;
            await module.DisposeAsync();
        }
    }
}
