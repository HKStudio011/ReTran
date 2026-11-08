using System.Text.Json.Nodes;
using ReTran_App.Core;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for the Task-14 wiring pieces that live outside the coordinator itself: the
/// <see cref="SetupCoreBridge"/> adapter (its <c>RestartAsync</c> must dispose the core peer BEFORE
/// starting it again, and its <c>CallAsync</c> must forward method + params verbatim) and the
/// <see cref="PipelineGate.CanStartPipeline"/> guard extracted from Home.razor (the pipeline may only
/// start once setup is complete — spec §3.1). The fake core peer records every action so ordering is
/// asserted, not assumed.
/// Test cho các mảnh wiring của Task 14 nằm ngoài coordinator: adapter <see cref="SetupCoreBridge"/>
/// (<c>RestartAsync</c> phải dispose core peer TRƯỚC khi start lại, <c>CallAsync</c> phải forward
/// nguyên vẹn method + params) và guard <see cref="PipelineGate.CanStartPipeline"/> tách ra từ
/// Home.razor (pipeline chỉ được start khi setup đã xong — spec §3.1). Fake core peer ghi lại mọi
/// hành động để assert đúng thứ tự, không đoán.
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

    [Fact]
    public void CanStartPipeline_TrueWhenSetupComplete() =>
        Assert.True(PipelineGate.CanStartPipeline(setupComplete: true));

    [Fact]
    public void CanStartPipeline_FalseWhileSetupIncomplete() =>
        Assert.False(PipelineGate.CanStartPipeline(setupComplete: false));

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

        public event Action<string, JsonNode?>? NotificationReceived;

        public event Action<Exception>? HandlerError;

        public event Action<int>? Exited;
    }
}
