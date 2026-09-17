using Microsoft.JSInterop;

namespace ReTran_App.Services;

/// <summary>
/// Bridges .NET and the WebView JavaScript module.
/// Cầu nối giữa .NET và module JavaScript trong WebView.
/// </summary>
public class JsInteropService : IAsyncDisposable
{
    private Lazy<Task<IJSObjectReference>>? moduleTask;
    private IJSRuntime? jsRuntime;

    /// <summary>
    /// Whether the WebView is ready for JS interop.
    /// WebView đã sẵn sàng cho JS interop hay chưa.
    /// </summary>
    public bool IsWebViewReady { get; private set; } = false;
    /// <summary>
    /// Raised when the WebView becomes ready.
    /// Được kích hoạt khi WebView trở nên sẵn sàng.
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
    /// Initializes the service with the JS runtime.
    /// Khởi tạo service với JS runtime.
    /// </summary>
    /// <param name="runtime">The Blazor JS runtime. JS runtime của Blazor.</param>
    public void Initialize(IJSRuntime runtime)
    {
        jsRuntime = runtime;
        moduleTask = new(() =>
            jsRuntime.InvokeAsync<IJSObjectReference>("import", "./build/assets/main.js").AsTask());
    }

    /// <summary>
    /// Invokes a JavaScript function without a return value.
    /// Gọi một hàm JavaScript không có giá trị trả về.
    /// </summary>
    /// <param name="method">The exported function name. Tên hàm được export.</param>
    /// <param name="args">Arguments passed to the function. Tham số truyền cho hàm.</param>
    public async ValueTask InvokeVoidAsync(string method, params object[] args)
    {
        if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return;
        var module = await moduleTask.Value;
        await module.InvokeVoidAsync(method, args);
    }

    /// <summary>
    /// Invokes a JavaScript function and returns its result.
    /// Gọi một hàm JavaScript và trả về kết quả.
    /// </summary>
    /// <typeparam name="T">The expected result type. Kiểu kết quả mong đợi.</typeparam>
    /// <param name="method">The exported function name. Tên hàm được export.</param>
    /// <param name="args">Arguments passed to the function. Tham số truyền cho hàm.</param>
    /// <returns>The JS result value. Giá trị kết quả từ JS.</returns>
    public async ValueTask<T> InvokeAsync<T>(string method, params object[] args)
    {
        if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return default!;
        var module = await moduleTask.Value;
        return await module.InvokeAsync<T>(method, args);
    }

    /// <summary>
    /// Returns the viewport dimensions as [width, height], or <see langword="null"/>
    /// when the web view is not ready yet.
    /// Trả về kích thước viewport dạng [width, height], hoặc <see langword="null"/>
    /// khi web view chưa sẵn sàng.
    /// </summary>
    /// <returns>The viewport size as a two-element array, or <see langword="null"/>. Kích thước viewport dạng mảng hai phần tử, hoặc <see langword="null"/>.</returns>
    public async ValueTask<double[]?> GetViewportSizeAsync()
    {
        if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return null;
        var module = await moduleTask.Value;
        return await module.InvokeAsync<double[]>("getViewportSize");
    }

    /// <summary>Sets the UI theme (light/dark/system) via the Vite module. Đặt theme giao diện (sáng/tối/theo hệ thống) qua module Vite.</summary>
    public async ValueTask SetThemeAsync(string theme) => await InvokeVoidAsync("setTheme", theme);

    /// <summary>Sets the accent color pair via the Vite module. Đặt cặp màu nhấn qua module Vite.</summary>
    public async ValueTask SetColorAsync(string colorId) => await InvokeVoidAsync("setColor", colorId);

    /// <summary>
    /// Disposes the imported JS module.
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
