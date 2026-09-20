using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ReTran_App.Core;
using Xunit;

public class JsonRpcClientTests
{
    [Fact]
    public async Task CallAsync_WritesRequest_AndResolvesOnMatchingResponse()
    {
        // Two connected anonymous pipe pairs: requests flow client -> serverIn, responses flow serverOut -> client.
        // Note: the server-side handles are intentionally NOT disposed — disposing a server-side anonymous
        // pipe handle in this process crashes testhost on Windows/.NET 10 (observed empirically).
        var serverIn = new AnonymousPipeServerStream(PipeDirection.In);
        var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, serverIn.ClientSafePipeHandle);
        var serverOut = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverOut.ClientSafePipeHandle);

        await using var _ = clientIn;
        await using var __ = clientOut;

        await using var client = new JsonRpcClient(
            new StreamReader(clientIn),
            new StreamWriter(clientOut) { NewLine = "\n" });

        // Start the call (it writes the request, then waits for a response).
        Task<JsonNode?> callTask = client.CallAsync("core.ping", null, CancellationToken.None);

        // Read what the client sent to us.
        string? sentLine = await new StreamReader(serverIn).ReadLineAsync();

        // Feed the matching response back — flush explicitly: an unflushed StreamWriter
        // leaves the line in its buffer, so the client's read loop never sees it and hangs.
        var respWriter = new StreamWriter(serverOut) { NewLine = "\n" };
        await respWriter.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":\"pong\"}");
        await respWriter.FlushAsync();

        JsonNode? result = await callTask;
        Assert.Equal("pong", result!.GetValue<string>());

        var req = JsonNode.Parse(sentLine!)!;
        Assert.Equal("core.ping", req["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task CallAsync_ErrorResponse_FaultsWithRpcException()
    {
        // Same pipe setup as the happy-path test; server-side handles intentionally NOT disposed
        // (disposing them crashes testhost on Windows/.NET 10).
        var serverIn = new AnonymousPipeServerStream(PipeDirection.In);
        var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, serverIn.ClientSafePipeHandle);
        var serverOut = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverOut.ClientSafePipeHandle);

        await using var _ = clientIn;
        await using var __ = clientOut;

        await using var client = new JsonRpcClient(
            new StreamReader(clientIn),
            new StreamWriter(clientOut) { NewLine = "\n" });

        Task<JsonNode?> callTask = client.CallAsync("core.missing", null, CancellationToken.None);

        // The request line must be exactly the JsonObject-serialized wire format (byte-compatible).
        string? sentLine = await new StreamReader(serverIn).ReadLineAsync();
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"core.missing\",\"params\":{}}", sentLine);

        // Feed a JSON-RPC error response for the same id.
        var respWriter = new StreamWriter(serverOut) { NewLine = "\n" };
        await respWriter.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}");
        await respWriter.FlushAsync();

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() => callTask);
        Assert.Equal(-32601, ex.Code);
        Assert.Equal("Method not found", ex.Message);
    }
}
