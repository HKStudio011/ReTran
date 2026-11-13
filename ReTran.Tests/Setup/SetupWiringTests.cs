using System.Text.Json.Nodes;
using ReTran_App.Core;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for the Task-14 wiring pieces that live outside the coordinator itself: the
/// <see cref="SetupCoreBridge"/> adapter (its <c>RestartAsync</c> must dispose the core peer BEFORE
/// starting it again, and its <c>CallAsync</c> must forward method + params verbatim) and the
/// <see cref="PipelineGate.CanStartPipeline"/> guard extracted from Home.razor (the pipeline may only
/// start once setup is complete OR the user chose "skip setup for this session" on the failure popup,
/// and no wizard run is in flight — spec §3.1, amendment 2026-11-13). The fake core peer records every
/// action so ordering is asserted, not assumed.
/// Test cho các mảnh wiring của Task 14 nằm ngoài coordinator: adapter <see cref="SetupCoreBridge"/>
/// (<c>RestartAsync</c> phải dispose core peer TRƯỚC khi start lại, <c>CallAsync</c> phải forward
/// nguyên vẹn method + params) và guard <see cref="PipelineGate.CanStartPipeline"/> tách ra từ
/// Home.razor (pipeline chỉ được start khi setup đã xong HOẶC user đã chọn "bỏ qua thiết lập cho
/// phiên này" trên popup màn lỗi, và wizard không đang chạy lượt nào — spec §3.1, amendment 2026-11-13).
/// Fake core peer ghi lại mọi hành động để assert đúng thứ tự, không đoán.
/// </summary>
public class SetupWiringTests
{
    [Fact]
    public async Task RestartAsync_DisposesCorePeerBeforeStartingItAgain()
    {
        var peer = new FakeCorePeer();
        var bridge = new SetupCoreBridge(peer);

        await bridge.RestartAsync(TestContext.Current.CancellationToken);

        // Kill+start: dispose trước, start sau — đảo thứ tự để lại core cũ chạy song song core mới.
        Assert.Equal(["dispose", "start"], peer.Actions);
        Assert.Empty(peer.Calls);
    }

    [Fact]
    public async Task CallAsync_ForwardsMethodAndParamsVerbatim()
    {
        var peer = new FakeCorePeer();
        var bridge = new SetupCoreBridge(peer);
        JsonNode? payload = new JsonObject { ["src"] = "vi" };

        JsonNode? result = await bridge.CallAsync("ocr.models.recommend", payload, TestContext.Current.CancellationToken);

        // Nguyên vẹn: cùng tên method, cùng THAM CHIẾU params (không copy/serialize lại).
        Assert.Single(peer.Calls);
        (string Method, JsonNode? Params) call = peer.Calls[0];
        Assert.Equal("ocr.models.recommend", call.Method);
        Assert.Same(payload, call.Params);
        // Kết quả của peer được trả thẳng về caller — cùng THAM CHIẾU node: JsonNode chỉ có reference
        // equality (không override Equals), nên Same là cách duy nhất chứng minh không copy/serialize.
        Assert.Same(peer.Result, result);
    }

    [Theory]
    // (complete, running, skipped): "Bỏ qua thiết lập" THAY THẾ completion cho phiên này, nhưng
    // "đang chạy một lượt" luôn khóa (kể cả cửa sổ rerun cũ).
    [InlineData(true, false, false, true)]   // complete + idle → cho start (cũ)
    [InlineData(true, false, true, true)]    // complete + skipped → cho start
    [InlineData(false, false, true, true)]   // ⭐ core: chưa xong nhưng đã "Bỏ qua thiết lập" → cho start
    [InlineData(false, false, false, false)] // chưa xong, chưa skip → chặn (cũ)
    [InlineData(true, true, false, false)]   // cửa sổ rerun (cũ) → chặn
    [InlineData(false, true, false, false)]  // first run đang chạy (cũ) → chặn
    [InlineData(true, true, true, false)]    // skip không mở được cửa sổ rerun
    [InlineData(false, true, true, false)]   // skip + đang chạy → chặn
    public void CanStartPipeline_RequiresIdleAndCompleteOrSkipped(
        bool setupComplete, bool setupRunning, bool skippedForSession, bool expected) =>
        Assert.Equal(expected, PipelineGate.CanStartPipeline(
            setupComplete: setupComplete, setupRunning: setupRunning, skippedForSession: skippedForSession));

    /// <summary>
    /// Recording <see cref="ICorePeer"/> double that is also <see cref="IAsyncDisposable"/> (like the
    /// production <c>CoreProcessClient</c>): every dispose/start/call lands in <see cref="Actions"/> /
    /// <see cref="Calls"/> in order.
    /// Test double <see cref="ICorePeer"/> ghi nhận, cũng là <see cref="IAsyncDisposable"/> (giống
    /// <c>CoreProcessClient</c> production): mọi dispose/start/call được ghi vào
    /// <see cref="Actions"/> / <see cref="Calls"/> theo đúng thứ tự.
    /// </summary>
    private sealed class FakeCorePeer : ICorePeer, IAsyncDisposable
    {
        /// <summary>
        /// The single result instance every call returns — its identity is what CallAsync must preserve.
        /// Instance kết quả duy nhất mỗi call trả về — chính tham chiếu của nó là thứ CallAsync phải giữ nguyên.
        /// </summary>
        public JsonNode? Result { get; } = JsonValue.Create("pong");

        public List<string> Actions { get; } = [];

        public List<(string Method, JsonNode? Params)> Calls { get; } = [];

        public bool IsStarted => Actions.Contains("start");

        public Task StartCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Actions.Add("start");
            return Task.CompletedTask;
        }

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((method, @params));
            return Task.FromResult(Result);
        }

        public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler)
        {
        }

        public ValueTask DisposeAsync()
        {
            Actions.Add("dispose");
            return ValueTask.CompletedTask;
        }

        // ICorePeer yêu cầu 3 event mà bridge không subscribe — lưu handler vào field riêng thay vì
        // event trống để không phát sinh CS0067 (handler "được dùng" qua +=/-=).
        private Action<string, JsonNode?>? _notificationReceived;
        private Action<Exception>? _handlerError;
        private Action<int>? _exited;

        public event Action<string, JsonNode?>? NotificationReceived
        {
            add { _notificationReceived += value; }
            remove { _notificationReceived -= value; }
        }

        public event Action<Exception>? HandlerError
        {
            add { _handlerError += value; }
            remove { _handlerError -= value; }
        }

        public event Action<int>? Exited
        {
            add { _exited += value; }
            remove { _exited -= value; }
        }
    }
}
