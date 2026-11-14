using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.Llm;
using ReTran.Core.Serve;
using ReTran.Core.Translate;

namespace RetranCore.Tests;

/// <summary>
/// Schema and factory-error behavior of <c>translate.provider = "llm"</c> on core.start: every
/// contract violation is -32602 before the sidecar/state checks, and a factory that rejects the
/// local model/runtime surfaces its own -32603. The full run-through lives in
/// PipelineHostTests (which owns the sidecar/source fakes).
/// Hành vi schema và lỗi factory của <c>translate.provider = "llm"</c> trên core.start: mọi vi
/// phạm hợp đồng là -32602 trước các check sidecar/state, và factory từ chối model/runtime local
/// nổi lên -32603 của chính nó. Đường chạy trọn nằm ở PipelineHostTests (nơi có fake sidecar/source).
/// </summary>
public class LlmStartTests
{
    private static JsonObject LlmStartParams(JsonObject? llm)
    {
        var translate = new JsonObject { ["provider"] = "llm", ["src"] = "auto", ["dst"] = "vi" };
        if (llm is not null) translate["llm"] = llm;
        return new JsonObject
        {
            ["source"] = new JsonObject { ["kind"] = "screen", ["monitor"] = 0 },
            ["translate"] = translate,
        };
    }

    private static PipelineHost CreateHost(Func<LlmSettings, ITranslator>? factory) =>
        new(null, new TranslatorRegistry(), new JsonRpcServer(), new StringWriter(), llmFactory: factory);

    private sealed class StubTranslator : ITranslator
    {
        public string Name => "llm";

        public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct = default) =>
            Task.FromResult(text);
    }

    private static async Task<RpcMethodException> ThrowsAsync(
        Func<LlmSettings, ITranslator>? factory, JsonObject? llm)
    {
        await using PipelineHost host = CreateHost(factory);
        return await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(LlmStartParams(llm), CancellationToken.None));
    }

    [Fact]
    public async Task Start_LlmMissingLlmBlock_InvalidParams()
    {
        RpcMethodException ex = await ThrowsAsync(_ => new StubTranslator(), null);

        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
        Assert.Contains("translate.llm", ex.Message);
    }

    [Fact]
    public async Task Start_LlmMissingMode_InvalidParams()
    {
        RpcMethodException ex = await ThrowsAsync(_ => new StubTranslator(), new JsonObject());

        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Start_LlmUnknownMode_InvalidParams()
    {
        RpcMethodException ex = await ThrowsAsync(
            _ => new StubTranslator(), new JsonObject { ["mode"] = "gpt" });

        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Start_LlmApiWithoutBaseUrl_InvalidParams()
    {
        RpcMethodException ex = await ThrowsAsync(
            _ => new StubTranslator(), new JsonObject { ["mode"] = "api" });

        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Start_LlmLocalWithoutModel_InvalidParams()
    {
        RpcMethodException ex = await ThrowsAsync(
            _ => new StubTranslator(), new JsonObject { ["mode"] = "local" });

        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Start_LlmUserPromptWithoutPlaceholder_InvalidParams()
    {
        RpcMethodException ex = await ThrowsAsync(_ => new StubTranslator(), new JsonObject
        {
            ["mode"] = "api",
            ["baseUrl"] = "http://x",
            ["userPrompt"] = "no placeholder here",
        });

        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Start_LlmFactoryRejectsLocalModel_InternalErrorPropagates()
    {
        await using PipelineHost host = CreateHost(_ =>
            throw new RpcMethodException(RpcErrorCodes.InternalError, "llm-model-missing: m"));

        RpcMethodException ex = await Assert.ThrowsAsync<RpcMethodException>(() => host.StartAsync(
            LlmStartParams(new JsonObject { ["mode"] = "local", ["model"] = "m" }),
            CancellationToken.None));

        Assert.Equal(RpcErrorCodes.InternalError, ex.Code);
        Assert.Contains("llm-model-missing", ex.Message);
    }

    [Fact]
    public async Task Start_LlmFactoryReceivesParsedSettings()
    {
        LlmSettings? captured = null;
        await using PipelineHost host = CreateHost(settings =>
        {
            captured = settings;
            throw new RpcMethodException(RpcErrorCodes.InternalError, "stop here");
        });

        await Assert.ThrowsAsync<RpcMethodException>(() => host.StartAsync(
            LlmStartParams(new JsonObject
            {
                ["mode"] = "api",
                ["baseUrl"] = "http://x/v1",
                ["apiKey"] = "k",
                ["model"] = "m",
                ["systemPrompt"] = "S",
                ["userPrompt"] = "T {source_text}",
            }),
            CancellationToken.None));

        Assert.NotNull(captured);
        Assert.Equal("api", captured!.Mode);
        Assert.Equal("http://x/v1", captured.BaseUrl);
        Assert.Equal("k", captured.ApiKey);
        Assert.Equal("m", captured.Model);
        Assert.Equal("S", captured.SystemPrompt);
        Assert.Equal("T {source_text}", captured.UserPrompt);
    }
}
