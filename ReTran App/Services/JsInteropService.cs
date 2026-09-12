using Microsoft.JSInterop;

namespace T3AI.Services
{
    public class JsInteropService : IAsyncDisposable
    {
        private Lazy<Task<IJSObjectReference>>? moduleTask;
        private IJSRuntime? jsRuntime;

        public bool IsWebViewReady { get; private set; } = false;
        public event Action? OnWebViewReady;

        public void NotifyWebViewReady()
        {
            IsWebViewReady = true;
            OnWebViewReady?.Invoke();
        }

        public void Initialize(IJSRuntime runtime)
        {
            jsRuntime = runtime;
            moduleTask = new(() =>
                jsRuntime.InvokeAsync<IJSObjectReference>("import", "./build/assets/main.js").AsTask());
        }

        public async ValueTask InvokeVoidAsync(string method, params object[] args)
        {
            if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return;
            var module = await moduleTask.Value;
            await module.InvokeVoidAsync(method, args);
        }

        public async ValueTask<T> InvokeAsync<T>(string method, params object[] args)
        {
            if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return default!;
            var module = await moduleTask.Value;
            return await module.InvokeAsync<T>(method, args);
        }

        /// <summary>
        /// Returns the viewport dimensions as [width, height], or <see langword="null"/>
        /// when the web view is not ready yet.
        /// </summary>
        /// <returns>The viewport size as a two-element array, or <see langword="null"/>.</returns>
        public async ValueTask<double[]?> GetViewportSizeAsync()
        {
            if (!IsWebViewReady || jsRuntime == null || moduleTask == null) return null;
            var module = await moduleTask.Value;
            return await module.InvokeAsync<double[]>("getViewportSize");
        }

        public async ValueTask DisposeAsync()
        {
            if (IsWebViewReady && moduleTask != null && moduleTask.IsValueCreated)
            {
                var module = await moduleTask.Value;
                await module.DisposeAsync();
            }
        }
    }
}
