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
}
