using System.IO.Pipes;
using System.Reflection;
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

    /// <summary>
    /// In-memory reader that blocks until a line is enqueued, so tests control exactly when the
    /// client's read loop sees each message (StringReader would race: pre-fed lines can be consumed
    /// before CallAsync registers its pending id). <see cref="Complete"/> signals clean EOF.
    /// Reader trong bộ nhớ chặn cho tới khi một dòng được nạp vào, để test kiểm soát chính xác
    /// thời điểm vòng lặp đọc của client thấy từng tin (StringReader sẽ race: dòng nạp sẵn có thể
    /// bị tiêu thụ trước khi CallAsync đăng ký id pending). <see cref="Complete"/> báo EOF sạch.
    /// </summary>
    private sealed class BlockingTextReader : TextReader
    {
        private readonly Queue<string> _lines = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _gate = new();
        private bool _eof;

        public void Enqueue(string line)
        {
            lock (_gate) _lines.Enqueue(line);
            _signal.Release();
        }

        public void Complete()
        {
            lock (_gate) _eof = true;
            _signal.Release();
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_lines.Count > 0) return _lines.Dequeue();
                    if (_eof) return null;
                }
                await _signal.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// In-memory writer that records complete lines and lets tests wait for them. The partial-line
    /// buffer is deliberately unsynchronized: concurrent unsynchronized writers must tear the line,
    /// so a missing write-lock in the client fails the test.
    /// Bộ nhớ ghi ghi lại các dòng hoàn chỉnh và cho phép test chờ chúng. Bộ đệm dòng dở dang cố
    /// ý không khóa: hai luồng ghi đồng thời không đồng bộ sẽ làm dòng bị rách, nên thiếu write-lock
    /// ở client sẽ làm test fail.
    /// </summary>
    private sealed class RecordingWriter : TextWriter
    {
        private readonly StringBuilder _partial = new();
        private readonly List<string> _lines = new();
        private readonly object _gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            foreach (char c in buffer.Span)
            {
                if (c == '\n')
                {
                    lock (_gate)
                    {
                        _lines.Add(_partial.ToString());
                        _partial.Clear();
                    }
                }
                else
                {
                    _partial.Append(c);
                }
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> Lines
        {
            get { lock (_gate) return _lines.ToArray(); }
        }

        public async Task WaitForLinesAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (Lines.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Expected {count} written lines, saw {Lines.Count}.");
                await Task.Delay(10);
            }
        }
    }

    /// <summary>
    /// Writer that fails every write, simulating a broken pipe to the peer process.
    /// Bộ nhớ ghi luôn thất bại, mô phỏng pipe hỏng tới tiến trình đối tác.
    /// </summary>
    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("pipe broken");
    }

    [Fact]
    public async Task CallAsync_StillMatchesResponseById()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);

        Task<JsonNode?> call = client.CallAsync("core.ping", null, CancellationToken.None);
        await output.WaitForLinesAsync(1);
        var sent = JsonNode.Parse(output.Lines[0])!;
        Assert.Equal("core.ping", sent["method"]!.GetValue<string>());
        string id = sent["id"]!.GetValue<string>();

        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":\"pong\"}}");
        JsonNode? result = await call;
        Assert.Equal("pong", result!.GetValue<string>());
    }

    [Fact]
    public async Task IncomingRequest_InvokesHandler_AndRepliesResult()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);

        string? seenMethod = null;
        JsonNode? seenParams = null;
        client.SetRequestHandler((method, @params) =>
        {
            seenMethod = method;
            seenParams = @params;
            return Task.FromResult<JsonNode?>(JsonValue.Create("hit"));
        });

        input.Enqueue(
            "{\"jsonrpc\":\"2.0\",\"id\":\"5\",\"method\":\"translate.request_cache\",\"params\":{\"key\":\"xp\"}}");
        await output.WaitForLinesAsync(1);

        var reply = JsonNode.Parse(output.Lines[0])!;
        Assert.Equal("5", reply["id"]!.GetValue<string>());
        Assert.Equal("hit", reply["result"]!.GetValue<string>());
        Assert.Equal("translate.request_cache", seenMethod);
        Assert.Equal("xp", seenParams!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task IncomingRequest_HandlerThrows_ReplyErrorCode32603()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);
        client.SetRequestHandler((_, _) => throw new InvalidOperationException("boom"));

        input.Enqueue("{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"method\":\"m\",\"params\":{}}");
        await output.WaitForLinesAsync(1);

        var reply = JsonNode.Parse(output.Lines[0])!;
        Assert.Equal("7", reply["id"]!.GetValue<string>());
        Assert.Equal(-32603, reply["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task IncomingRequest_NoHandler_ReplyMethodNotFound()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);

        input.Enqueue("{\"jsonrpc\":\"2.0\",\"id\":\"9\",\"method\":\"core.nope\",\"params\":{}}");
        await output.WaitForLinesAsync(1);

        var reply = JsonNode.Parse(output.Lines[0])!;
        Assert.Equal("9", reply["id"]!.GetValue<string>());
        Assert.Equal(-32601, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Method not found: core.nope", reply["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notification_WithoutId_RaisesEvent_AndSurvivesThrow()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);

        var seen = new List<string>();
        Exception? handlerError = null;
        client.HandlerError += ex => handlerError = ex;
        client.NotificationReceived += (method, _) =>
        {
            lock (seen) seen.Add(method);
            if (method == "frame.translated") throw new InvalidOperationException("boom");
        };

        input.Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"frame.translated\",\"params\":{\"n\":1}}");
        input.Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"pipeline.state\",\"params\":{\"state\":\"running\"}}");

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (seen) { if (seen.Count >= 2) break; }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected both notifications, saw {seen.Count}.");
            await Task.Delay(10);
        }

        lock (seen) Assert.Equal(new[] { "frame.translated", "pipeline.state" }, seen);
        Assert.NotNull(handlerError);
        Assert.Equal("boom", handlerError!.Message);
        Assert.Empty(output.Lines);
    }

    [Fact]
    public async Task ConcurrentCallAndReply_WritesCleanLines()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);

        Task<JsonNode?> call1 = Task.Run(() => client.CallAsync("core.ping", null, CancellationToken.None));
        Task<JsonNode?> call2 = Task.Run(() => client.CallAsync("core.status", null, CancellationToken.None));

        await output.WaitForLinesAsync(2);
        string[] sent = output.Lines.ToArray();
        Assert.Equal(2, sent.Length);
        foreach (string line in sent)
        {
            var req = JsonNode.Parse(line)!;
            string id = req["id"]!.GetValue<string>();
            input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":\"ok\"}}");
        }

        JsonNode?[] results = await Task.WhenAll(call1, call2);
        Assert.All(results, r => Assert.Equal("ok", r!.GetValue<string>()));
        Assert.Equal(2, output.Lines.Count);
    }

    [Fact]
    public async Task ReadLoop_Eof_CompletesPendingCalls()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);

        Task<JsonNode?> call = client.CallAsync("core.ping", null, CancellationToken.None);
        await output.WaitForLinesAsync(1);

        input.Complete();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReadLoop_Dispose_CompletesPendingCalls()
    {
        var input = new BlockingTextReader();
        var output = new RecordingWriter();
        await using var client = new JsonRpcClient(input, output);

        Task<JsonNode?> call = client.CallAsync("core.ping", null, CancellationToken.None);
        await output.WaitForLinesAsync(1);

        await client.DisposeAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CallAsync_WriteFailure_RemovesPendingEntry()
    {
        var input = new BlockingTextReader();
        var output = new FailingWriter();
        await using var client = new JsonRpcClient(input, output);

        IOException ex = await Assert.ThrowsAsync<IOException>(
            () => client.CallAsync("core.ping", null, CancellationToken.None));
        Assert.Equal("pipe broken", ex.Message);

        var pending = (System.Collections.IDictionary)typeof(JsonRpcClient)
            .GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        Assert.Empty(pending);
    }
}
