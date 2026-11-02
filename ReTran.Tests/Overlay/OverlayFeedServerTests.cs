using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReTran_App.Core;
using ReTran_App.Services.OverlayFeed;

namespace ReTran.Tests;

public sealed class OverlayFeedServerTests
{
    // Tests pick ports from a quiet low band on purpose: the OS only hands out ports from the
    // ephemeral range (49152+) to outbound sockets, so the test host's own loopback IPC chatter
    // can never steal base+1..base+10 the way it can for an ephemeral base. The band also stays
    // clear of the app's real default range (17863..17873) and every current Windows port
    // exclusion range.
    // Các test cố tình chọn cổng từ dải thấp yên tĩnh: OS chỉ phát cổng dải ephemeral (49152+)
    // cho socket outbound, nên chatter IPC loopback của chính test host không thể đánh cắp
    // base+1..base+10 như với base ephemeral. Dải này cũng tránh range mặc định thật của app
    // (17863..17873) và mọi range loại trừ cổng Windows hiện tại.
    private const int QuietBandMin = 18100;
    private const int QuietBandMax = 18300;

    private static int FreePort()
    {
        while (true)
        {
            int port = Random.Shared.Next(QuietBandMin, QuietBandMax + 1);
            var probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
                return port;
            }
            catch (SocketException)
            {
                // Taken by a static local app or an OS exclusion — try another one.
                // Bị chiếm bởi app local tĩnh hoặc vùng loại trừ của OS — thử cổng khác.
            }
            finally
            {
                probe.Stop();
            }
        }
    }

    // Binds 11 consecutive listeners (the whole fallback range) and returns them; retries with a
    // new base when any port in the block is taken.
    // Bind 11 listener liền kề (toàn dải fallback) và trả về chúng; thử base khác khi bất kỳ
    // cổng nào trong khối bị chiếm.
    private static List<TcpListener> OccupyFallbackRange(out int basePort)
    {
        while (true)
        {
            int candidate = Random.Shared.Next(QuietBandMin, QuietBandMax - 10);
            var blockers = new List<TcpListener>();
            try
            {
                for (var offset = 0; offset <= 10; offset++)
                {
                    var blocker = new TcpListener(IPAddress.Loopback, candidate + offset);
                    blocker.Start();
                    blockers.Add(blocker);
                }
                basePort = candidate;
                return blockers;
            }
            catch (SocketException)
            {
                foreach (var blocker in blockers)
                {
                    blocker.Stop();
                }
            }
        }
    }

    private static async Task<string> ReadNextDataLineAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            string? line = await reader.ReadLineAsync(timeout.Token);
            if (line is null)
                throw new IOException("SSE stream closed before the next data event. / Luồng SSE đóng trước sự kiện data kế tiếp.");
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return line["data: ".Length..];
        }
    }

    // Reads until EOF or connection failure; a timeout means the server never closed the stream.
    // Đọc đến EOF hoặc mất kết nối; timeout nghĩa là server không đóng stream.
    private static async Task AssertStreamClosesAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0) return; // graceful EOF — the subscriber was dropped
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return; // reset mid-drain — the connection is gone either way
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Stream stayed open for {timeout.TotalSeconds:0}s — the subscriber was not dropped. " +
                        $"/ Stream mở nguyên {timeout.TotalSeconds:0} giây — subscriber không bị drop.");
            throw;
        }
    }

    [Fact]
    public async Task Start_ServesOverlayPage()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);
        Assert.True(server.Started);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Contains("text/html", response.Content.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task Overlay_Assets_AreServedWithNoStore()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // The page must reference its own assets (absolute paths: it is served at /overlay).
        // Trang phải tham chiếu asset của chính nó (đường dẫn tuyệt đối: nó được phục vụ tại /overlay).
        using var page = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("/overlay/overlay.js", html);
        Assert.Contains("/overlay/overlay.css", html);
        // M4: OBS/CEF must not serve a stale page after an asset update.
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());

        using var js = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay/overlay.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.NotNull(js.Content.Headers.ContentType);
        Assert.Contains("javascript", js.Content.Headers.ContentType.ToString());
        Assert.Equal("no-store", js.Headers.CacheControl?.ToString());
        Assert.Contains("EventSource", await js.Content.ReadAsStringAsync());

        using var css = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay/overlay.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.NotNull(css.Content.Headers.ContentType);
        Assert.Contains("text/css", css.Content.Headers.ContentType.ToString());
        Assert.Equal("no-store", css.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/no-such-path");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NonGetMethod_Returns405()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.PostAsync($"http://127.0.0.1:{server.Port}/overlay", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Events_SendsInitialSnapshot()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var payload = await ReadNextDataLineAsync(reader);

        using var doc = JsonDocument.Parse(payload);
        Assert.Empty(doc.RootElement.GetProperty("boxes").EnumerateArray());
    }

    [Fact]
    public async Task Events_PushesPublishedBoxes()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/events", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        await ReadNextDataLineAsync(reader); // initial snapshot / snapshot ban đầu

        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            [new OverlayBox(0.5, 0.25, 0.2, 0.1, "A", "B"),
             new OverlayBox(0.6, 0.3, 0.15, 0.05, "C", "D", IsParagraph: true)]));

        var payload = await ReadNextDataLineAsync(reader);
        using var doc = JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.GetProperty("seq").GetInt64() > 0);
        var box = doc.RootElement.GetProperty("boxes")[0];
        Assert.Equal(0.5, box.GetProperty("x").GetDouble());
        Assert.Equal(0.25, box.GetProperty("y").GetDouble());
        Assert.Equal(0.2, box.GetProperty("w").GetDouble());
        Assert.Equal(0.1, box.GetProperty("h").GetDouble());
        Assert.Equal("A", box.GetProperty("text").GetString());
        Assert.Equal("B", box.GetProperty("translated").GetString());
        Assert.False(box.GetProperty("paragraph").GetBoolean());
        var para = doc.RootElement.GetProperty("boxes")[1];
        Assert.Equal("D", para.GetProperty("translated").GetString());
        Assert.True(para.GetProperty("paragraph").GetBoolean());
    }

    [Fact]
    public async Task PortBusy_FallsBackToNextPort()
    {
        var state = new OverlayState();
        var blocker = new TcpListener(IPAddress.Loopback, FreePort());
        blocker.Start();
        try
        {
            var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
            await using var server = new OverlayFeedServer(state, busyPort);
            await server.StartAsync(CancellationToken.None);

            Assert.True(server.Port > busyPort);
            Assert.True(server.Started);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task AllPortsBusy_ThrowsAndReportsNotStarted()
    {
        var state = new OverlayState();
        var blockers = OccupyFallbackRange(out int basePort);
        try
        {
            await using var server = new OverlayFeedServer(state, basePort);
            await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(CancellationToken.None));

            // The card must not show/copy a URL for a server that never bound anything.
            // Card không được hiện/copy URL cho server chưa từng bind cổng nào.
            Assert.False(server.Started);
        }
        finally
        {
            foreach (var blocker in blockers)
            {
                blocker.Stop();
            }
        }
    }

    [Fact]
    public void Ctor_InvalidPort_Throws()
    {
        var state = new OverlayState();
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayFeedServer(state, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayFeedServer(state, -1));
        // port+10 must stay a valid port — 65526 already leaves no room for the fallback range.
        // port+10 phải còn là cổng hợp lệ — 65526 đã không còn chỗ cho dải fallback.
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayFeedServer(state, 65526));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayFeedServer(state, 65536));
        _ = new OverlayFeedServer(state, 65525); // highest port that still leaves room for +10
    }

    [Fact]
    public async Task Events_ClientDisconnects_ServerSurvives()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/events", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        await ReadNextDataLineAsync(reader); // make sure the SSE client is fully served / đảm bảo SSE client đã kết nối trọn vẹn

        response.Dispose(); // abrupt mid-stream disconnect / ngắt đột ngột giữa luồng

        for (var i = 0; i < 3; i++)
        {
            state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow, []));
            await Task.Delay(50);
        }

        using var check = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
    }

    [Fact]
    public async Task Events_StalledClient_DoesNotBlockPublisher_HealthyClientStillServed()
    {
        // I1 end-to-end: one subscriber connects but never reads (its kernel buffers fill and the
        // server-side write stalls) while a second subscriber drains normally. Publishing must stay
        // fast, the stalled subscriber must get dropped, and the healthy one must keep receiving.
        // I1 đầu-cuối: một subscriber kết nối nhưng không bao giờ đọc (buffer kernel đầy và lệnh ghi
        // phía server bị kẹt) trong khi subscriber thứ hai xả bình thường. Publish phải nhanh,
        // subscriber chết cứng phải bị drop, và client khỏe vẫn nhận frame.
        const int Frames = 250;
        const int BoxesPerFrame = 60;
        string lastText = $"frame-{Frames - 1}-" + new string('x', 80);

        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        // Stalled subscriber: a raw socket that sends the request and then never reads. The tiny
        // pinned receive buffer disables loopback window auto-tuning, so the server-side write
        // provably stalls early instead of the kernel absorbing megabytes.
        // Subscriber chết cứng: socket thô gửi request rồi không bao giờ đọc. Bộ đệm nhận nhỏ
        // cố định tắt auto-tune cửa sổ loopback, nên lệnh ghi phía server chắn chắn kẹt sớm
        // thay vì kernel hấp thụ hàng MB.
        using var stalled = new TcpClient();
        stalled.Client.ReceiveBufferSize = 8190;
        await stalled.ConnectAsync(IPAddress.Loopback, server.Port);
        var stalledStream = stalled.GetStream();
        byte[] request = Encoding.ASCII.GetBytes($"GET /events HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n");
        await stalledStream.WriteAsync(request);

        // Healthy subscriber, drained in the background for the whole burst.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var healthy = await http.GetAsync($"http://127.0.0.1:{server.Port}/events", HttpCompletionOption.ResponseHeadersRead);
        await using var healthyStream = await healthy.Content.ReadAsStreamAsync();
        using var healthyReader = new StreamReader(healthyStream);

        var lastSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line = await healthyReader.ReadLineAsync();
                    if (line is null) return;
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    using var doc = JsonDocument.Parse(line["data: ".Length..]);
                    var boxes = doc.RootElement.GetProperty("boxes");
                    if (boxes.GetArrayLength() == 0) continue;
                    if (boxes[0].GetProperty("text").GetString() == lastText)
                    {
                        lastSeen.TrySetResult();
                        return;
                    }
                }
            }
            catch
            {
                // A dropped/failed healthy client makes lastSeen time out below with a clear message.
            }
        });

        // A ~9 KB frame keeps the burst ~2 MB — far more than the stalled client can absorb,
        // so its server-side write really stalls mid-burst.
        // Khung ~9 KB giữ burst ~2 MB — nhiều hơn xa dung lượng client chết cứng hấp thụ,
        // nên lệnh ghi phía server của nó thực sự kẹt giữa burst.
        var filler = new OverlayBox[BoxesPerFrame - 1];
        for (var b = 0; b < filler.Length; b++)
        {
            filler[b] = new OverlayBox(0.01, 0.02 * (b + 1), 0.02, 0.02, "text-" + new string('y', 60), "dịch " + b);
        }

        var maxPublishMs = 0L;
        for (var i = 0; i < Frames; i++)
        {
            string marker = i == Frames - 1 ? lastText : $"frame-{i}-" + new string('x', 80);
            var frameBoxes = new OverlayBox[BoxesPerFrame];
            frameBoxes[0] = new OverlayBox(0.5, 0.25, 0.2, 0.1, marker, "bản dịch");
            Array.Copy(filler, 0, frameBoxes, 1, filler.Length);
            var publish = Stopwatch.StartNew();
            state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow, frameBoxes));
            publish.Stop();
            maxPublishMs = Math.Max(maxPublishMs, publish.ElapsedMilliseconds);
            await Task.Delay(3); // pace so the healthy client drains easily; the stalled one cannot
        }
        Assert.True(maxPublishMs < 1000,
            $"A single Publish stalled for {maxPublishMs} ms — a blocked client froze the pipeline. " +
            $"/ Một lệnh Publish chặn {maxPublishMs} ms — client chết cứng đóng băng pipeline.");

        // The healthy subscriber kept receiving through (and after) the burst.
        await lastSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The stalled subscriber was dropped: the server closed its connection.
        await AssertStreamClosesAsync(stalledStream, TimeSpan.FromSeconds(10));

        // The server still serves other requests afterwards.
        using var check = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
    }

    [Fact]
    public async Task Events_SendsHeartbeat()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort(), heartbeat: TimeSpan.FromMilliseconds(100));
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/events", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line is null)
                    Assert.Fail("SSE stream closed before any heartbeat. / Luồng SSE đóng trước khi có heartbeat.");
                if (line == ":ping")
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("No ':ping' within 3 seconds. / Không đọc được ':ping' trong 3 giây.");
        }
    }

    [Fact]
    public async Task SseClient_BlockedWrite_FillsQueue_DropsClientWithoutBlocking()
    {
        // I1 mechanics: with the writer stuck on a blocked stream, enqueueing beyond the queue
        // capacity must fail fast (never block the publisher), and dropping the client (what
        // Broadcast does on a full queue) must not hang on the stuck writer.
        // Cơ chế I1: khi writer kẹt trên stream bị chặn, đưa vượt dung lượng hàng phải sai
        // ngay (không chặn publisher), và bỏ client (Broadcast làm khi hàng đầy) không được
        // treo trên writer đang kẹt.
        var stream = new BlockingWriteStream();
        var dead = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadCount = 0;
        using var client = new OverlayFeedServer.SseClient(
            stream,
            onDead: _ =>
            {
                deadCount++;
                dead.TrySetResult(null);
            },
            writeDeadline: TimeSpan.FromSeconds(30),
            capacity: 2);

        // Frame 1 leaves the queue immediately — the writer is now stuck writing it.
        // Khung 1 rời hàng ngay — writer giờ kẹt viết nó.
        Assert.True(client.TryEnqueue("data: first\n\n"));
        await stream.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var sw = Stopwatch.StartNew();
        Assert.True(client.TryEnqueue("data: a\n\n"));
        Assert.True(client.TryEnqueue("data: b\n\n"));
        Assert.False(client.TryEnqueue("data: c\n\n")); // queue full → dropped, not blocked
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"TryEnqueue blocked for {sw.ElapsedMilliseconds} ms. / TryEnqueue chặn {sw.ElapsedMilliseconds} ms.");

        client.Dispose();
        Assert.True(await client.WaitExitedAsync(TimeSpan.FromSeconds(5)),
            "Writer did not exit after Dispose. / Writer không thoát sau Dispose.");
        await dead.Task.WaitAsync(TimeSpan.FromSeconds(5)); // onDead fired in the writer's exit path
        Assert.Equal(1, deadCount);
        Assert.False(client.TryEnqueue("data: late\n\n")); // disposed → refuse new frames
    }

    [Fact]
    public async Task SseClient_WriteDeadline_DropsClient()
    {
        // I1 mechanics: a write that exceeds its deadline reports the client dead instead of
        // blocking the writer (and through it, the publisher) forever.
        // Cơ chế I1: lệnh ghi quá deadline báo client chết thay vì chặn writer (và qua nó,
        // cả publisher) mãi mãi.
        var stream = new BlockingWriteStream();
        var dead = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new OverlayFeedServer.SseClient(
            stream,
            onDead: _ => dead.TrySetResult(null),
            writeDeadline: TimeSpan.FromMilliseconds(100),
            capacity: 8);

        Assert.True(client.TryEnqueue("data: stuck\n\n"));
        await dead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await client.WaitExitedAsync(TimeSpan.FromSeconds(5)),
            "Writer did not exit after the deadline. / Writer không thoát sau deadline.");
    }

    /// <summary>
    /// Test double for a stalled SSE connection: the first write never completes.
    /// Bản thử cho kết nối SSE chết cứng: lệnh ghi đầu tiên không bao giờ hoàn tất.
    /// </summary>
    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstWriteStarted => _firstWrite.Task;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _firstWrite.TrySetResult();
            // Never completes: simulates a client whose receive window is full.
            // Không bao giờ hoàn tất: mô phỏng client có cửa sổ nhận đầy.
            return new TaskCompletionSource<bool>().Task;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
