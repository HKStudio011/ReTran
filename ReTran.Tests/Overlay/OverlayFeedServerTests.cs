using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
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

    // Drains an /events stream into a queue. Tests need both "the next event is X" and "nothing more
    // for a while" on the SAME connection; cancelling a read on an HttpClient response stream aborts
    // the connection (ObjectDisposedException on the next read), so the drain never takes a token and
    // the assertions wait on the queue instead.
    // Xả luồng /events vào một hàng đợi. Test cần cả "event kế tiếp là X" lẫn "không gì thêm trong một
    // khoảng" trên CÙNG kết nối; hủy một lệnh đọc trên response stream của HttpClient sẽ hủy kết nối
    // (ObjectDisposedException ở lệnh đọc sau), nên vòng xả không nhận token và các assert chờ trên hàng
    // đợi thay vào đó.
    private sealed class SseCollector : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly Task _drain;
        private readonly ConcurrentQueue<string> _events = new();
        private readonly SemaphoreSlim _arrived = new(0);

        private SseCollector(HttpResponseMessage response)
        {
            _response = response;
            _drain = Task.Run(DrainAsync);
        }

        public static async Task<SseCollector> OpenAsync(HttpClient http, int port)
        {
            HttpResponseMessage response = await http.GetAsync(
                $"http://127.0.0.1:{port}/events", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return new SseCollector(response);
        }

        private async Task DrainAsync()
        {
            try
            {
                using var reader = new StreamReader(await _response.Content.ReadAsStreamAsync());
                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line is null) break;
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        _events.Enqueue(line["data: ".Length..]);
                        _arrived.Release();
                    }
                }
            }
            catch (Exception)
            {
                // A dropped connection ends the drain; the assertions below report it as "closed".
                // Kết nối bị drop kết thúc vòng xả; các assert bên dưới báo nó là "đã đóng".
            }
            finally
            {
                // One permit so a waiter can notice the stream ended.
                // Một permit để waiter thấy luồng đã kết thúc.
                _arrived.Release();
            }
        }

        /// <summary>
        /// The next data event, failing when none arrives within 10 s or the connection closed first.
        /// Event data kế tiếp, fail khi không có event nào trong 10 giây hoặc kết nối đóng trước.
        /// </summary>
        public async Task<string> NextEventAsync(string because)
        {
            bool arrived = await _arrived.WaitAsync(TimeSpan.FromSeconds(10));
            if (arrived && _events.TryDequeue(out string? payload)) return payload;
            Assert.Fail(arrived
                ? $"The connection closed before another data event ({because}). " +
                  $"/ Kết nối đóng trước khi có event data nữa ({because})."
                : $"No data event within 10 s ({because}). / Không có event data trong 10 giây ({because}).");
            return string.Empty; // Assert.Fail always throws / Assert.Fail luôn ném
        }

        /// <summary>
        /// The next data event that actually carries boxes — a hidden→shown transition may send an
        /// empty snapshot first when the session has not produced a frame yet.
        /// Event data kế tiếp thực sự mang box — transition ẩn→hiện có thể gửi snapshot rỗng trước khi
        /// phiên kịp có khung hình.
        /// </summary>
        public async Task<string> NextBoxEventAsync(string because)
        {
            while (true)
            {
                string payload = await NextEventAsync(because);
                if (BoxCount(payload) > 0) return payload;
            }
        }

        /// <summary>
        /// Fails if any data event arrives inside the window — the "no wasted SSE traffic" half of
        /// spec K9.
        /// Fail nếu có event data nào tới trong khoảng chờ — nửa "không tốn traffic SSE vô ích" của
        /// spec K9.
        /// </summary>
        public async Task AssertSilenceAsync(TimeSpan window, string because)
        {
            if (await _arrived.WaitAsync(window))
            {
                string payload = _events.TryDequeue(out string? dequeued) ? dequeued : "(connection closed)";
                Assert.Fail($"Unexpected SSE data event while none was allowed ({because}): {payload} " +
                            $"/ Bất ngờ có sự kiện data khi không được phép có ({because}): {payload}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _response.Dispose(); // ends the drain / kết thúc vòng xả
            try { await _drain; } catch { }
            _arrived.Dispose();
        }
    }

    private static int BoxCount(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("boxes").GetArrayLength();
    }

    private static string FirstBoxText(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("boxes")[0].GetProperty("text").GetString() ?? string.Empty;
    }

    // The feed's own sequence number — a suppressed payload must not move it.
    // Số seq của chính feed — một payload bị chặn không được làm nó chạy.
    private static long SeqOf(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("seq").GetInt64();
    }

    // One frame holding a single named box — the name is what a test matches the SSE payload against.
    // Một khung hình chứa đúng một box có tên — tên là thứ test dùng để so với payload SSE.
    private static OverlayFrame FrameWithBox(string text) => new(1920, 1080, DateTime.UtcNow,
        [new OverlayBox(0.5, 0.25, 0.2, 0.1, text, "dịch của " + text)]);

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

    // ---------- visibility gate (spec K8/K9/K10) ----------

    [Fact]
    public async Task Hidden_BroadcastsOneEmptyPayload_ThenNothing()
    {
        var state = new OverlayState();
        var visibility = new FakeVisibility { Shown = true };
        await using var server = new OverlayFeedServer(state, FreePort(), visibility: visibility);
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var events = await SseCollector.OpenAsync(http, server.Port);
        Assert.Equal(0, BoxCount(await events.NextEventAsync("initial snapshot")));

        state.Publish(FrameWithBox("A"));
        Assert.Equal("A", FirstBoxText(await events.NextEventAsync("published box A")));

        visibility.Shown = false; // one edge ⇒ exactly one clear payload / một edge ⇒ đúng một payload xóa
        Assert.Equal(0, BoxCount(await events.NextEventAsync("the hidden edge")));

        for (var i = 0; i < 3; i++)
        {
            state.Publish(FrameWithBox($"hidden-{i}"));
        }
        // Frames really were published and OverlayState does hold them — only the gate may keep them
        // off the wire. Khung hình thật sự được xuất bản và OverlayState có giữ chúng — chỉ gate mới
        // được phép giữ chúng ra khỏi wire.
        Assert.Single(state.Latest!.Boxes);
        Assert.Equal("hidden-2", state.Latest!.Boxes[0].Text);

        await events.AssertSilenceAsync(TimeSpan.FromMilliseconds(1500), "3 frames published while hidden");
    }

    [Fact]
    public async Task Shown_AfterHidden_BroadcastsCurrentSnapshot()
    {
        var state = new OverlayState();
        var visibility = new FakeVisibility { Shown = true };
        await using var server = new OverlayFeedServer(state, FreePort(), visibility: visibility);
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var events = await SseCollector.OpenAsync(http, server.Port);
        Assert.Equal(0, BoxCount(await events.NextEventAsync("initial snapshot")));

        state.Publish(FrameWithBox("first"));
        Assert.Equal("first", FirstBoxText(await events.NextEventAsync("published box first")));

        visibility.Shown = false;
        Assert.Equal(0, BoxCount(await events.NextEventAsync("the hidden edge")));

        state.Publish(FrameWithBox("while-hidden")); // gated / bị chặn ở gate
        await events.AssertSilenceAsync(TimeSpan.FromMilliseconds(500), "frame published while hidden");

        visibility.Shown = true; // bật lại khi pipeline vẫn chạy ⇒ snapshot hiện tại tới ngay
        string shown = await events.NextEventAsync("the shown edge");
        Assert.Equal(1, BoxCount(shown));
        Assert.Equal("while-hidden", FirstBoxText(shown));
    }

    [Fact]
    public async Task FramePreemptedBetweenGateCheckAndEnqueue_NeverLandsAfterTheClear()
    {
        // The reported interleaving, made deterministic: the publish thread's own gate read is parked, so
        // the hidden edge and its clear payload land while that thread still sits between the check and
        // the enqueue. The payload it wanted to queue must be dropped by the gate re-check under the
        // client lock, and it must not have burned a sequence number either.
        // Đúng interleaving bị báo lỗi, được làm cho tất định: phép đọc gate của thread publish bị kẹt, nên
        // edge ẩn và payload xóa của nó rơi xuống khi thread đó vẫn còn nằm giữa kiểm tra và xếp hàng.
        // Payload nó muốn xếp phải bị phép đọc lại gate dưới khóa client bỏ, và cũng không được tiêu tốn
        // số seq.
        var state = new OverlayState();
        var gate = new ParkingGateVisibility();
        await using var server = new OverlayFeedServer(state, FreePort(), visibility: gate);
        await server.StartAsync(TestContext.Current.CancellationToken);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var events = await SseCollector.OpenAsync(http, server.Port);
        await events.NextEventAsync("initial snapshot");

        gate.ParkNextRead();
        // The publish runs on its own thread: parking its gate read must not starve the thread pool.
        // Publish chạy trên thread riêng: việc kẹt phép đọc gate của nó không được làm đói thread pool.
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Thread(() =>
        {
            state.Publish(FrameWithBox("preempted"));
            published.TrySetResult();
        })
        { IsBackground = true };
        publisher.Start();

        await gate.Parked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The user turns the overlay off while that publish is parked inside its gate check.
        // Người dùng tắt overlay ngay lúc lệnh publish đang kẹt trong phép kiểm tra gate của nó.
        gate.HideWithoutEdge();
        gate.RaiseEdge();
        string clear = await events.NextEventAsync("the hidden edge's clear");
        Assert.Equal(0, BoxCount(clear));
        long clearSeq = SeqOf(clear);

        gate.Release();
        var resumed = await Task.WhenAny(published.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(resumed == published.Task, "The parked publish never resumed. / Publish bị kẹt không bao giờ chạy tiếp.");

        await events.AssertSilenceAsync(TimeSpan.FromMilliseconds(1000), "the preempted frame after the clear");

        // Turning the overlay back on delivers the frame that was waiting with the number right after the
        // clear: the dropped payload left the counter untouched.
        // Bật overlay lại trả chính khung hình đã chờ với số ngay sau lệnh xóa: payload bị bỏ không đụng gì
        // bộ đếm.
        gate.ShowWithoutEdge();
        gate.RaiseEdge();
        string shown = await events.NextEventAsync("the shown edge");
        Assert.Equal("preempted", FirstBoxText(shown));
        Assert.Equal(clearSeq + 1, SeqOf(shown));
    }

    [Fact]
    public async Task ClientConnecting_WhileHidden_GetsEmptySnapshot()
    {
        // Latest still holds a frame (a frame.translated queued on stdio can repopulate it after
        // Clear — Task 8), so a hidden feed must answer from the gate, not from Latest.
        // Latest vẫn giữ khung hình (frame.translated còn xếp hàng trên stdio có thể đổ lại nó sau
        // Clear — Task 8), nên feed đang ẩn phải trả lời theo gate, không theo Latest.
        var state = new OverlayState();
        var visibility = new FakeVisibility(); // hidden from the start / ẩn từ đầu
        await using var server = new OverlayFeedServer(state, FreePort(), visibility: visibility);
        await server.StartAsync(CancellationToken.None);

        state.Publish(FrameWithBox("stale"));
        Assert.NotNull(state.Latest);            // Latest is NOT empty — the gate is what answers
        Assert.Single(state.Latest!.Boxes);      // Latest không rỗng — gate mới là thứ trả lời

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var events = await SseCollector.OpenAsync(http, server.Port);
        // Empty boxes even though Latest is not null. / Boxes rỗng dù Latest không phải null.
        Assert.Equal(0, BoxCount(await events.NextEventAsync("hidden snapshot for a late client")));
    }

    [Fact]
    public async Task ClientConnecting_AfterClear_GetsEmptySnapshot()
    {
        // The switch is ON and the pipeline ran: K10 makes Clear() the reason a late subscriber sees
        // an empty stage, not the gate.
        // Công tắc BẬT và pipeline đã chạy: K10 lấy Clear() làm lý do subscriber đến muộn thấy sân
        // khấu rỗng, không phải gate.
        var state = new OverlayState();
        var visibility = new FakeVisibility { Shown = true };
        await using var server = new OverlayFeedServer(state, FreePort(), visibility: visibility);
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var running = await SseCollector.OpenAsync(http, server.Port);
        Assert.Equal(0, BoxCount(await running.NextEventAsync("initial snapshot")));

        state.Publish(FrameWithBox("session"));
        Assert.Equal("session", FirstBoxText(await running.NextEventAsync("published session frame")));

        state.Clear();
        // Stage cleared on the wire. / Sân khấu đã xóa trên wire.
        Assert.Equal(0, BoxCount(await running.NextEventAsync("the Clear() event")));
        Assert.Null(state.Latest);

        await using var late = await SseCollector.OpenAsync(http, server.Port);
        Assert.Equal(0, BoxCount(await late.NextEventAsync("snapshot after Clear")));
    }

    [Fact]
    public async Task NoVisibility_BroadcastsEveryPublish()
    {
        // Regression for every existing feed test: with no visibility injected nothing is gated.
        // Regression cho toàn bộ test feed hiện có: không truyền visibility thì không có gì bị chặn.
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var events = await SseCollector.OpenAsync(http, server.Port);
        Assert.Equal(0, BoxCount(await events.NextEventAsync("initial snapshot")));

        for (var i = 1; i <= 3; i++)
        {
            state.Publish(FrameWithBox($"frame-{i}"));
            string payload = await events.NextEventAsync($"published frame {i}");
            Assert.Equal(1, BoxCount(payload));
            Assert.Equal($"frame-{i}", FirstBoxText(payload));
        }
    }

    [Fact]
    public async Task UserStop_ClearsFeed_WithExactlyOneEmptyPayload()
    {
        // StopAsync raises ONLY StateChanged (PipelineController:297-300): the Clear() that precedes
        // it must not add a second empty payload on top of the hidden edge.
        // StopAsync chỉ nổ StateChanged (PipelineController:297-300): Clear() đứng trước nó không được
        // cộng thêm payload rỗng thứ hai lên edge ẩn.
        await using var harness = new PipelineFeedHarness();
        await harness.Server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var events = await SseCollector.OpenAsync(http, harness.Server.Port);
        // Nothing is running yet, so the snapshot is empty. / Chưa chạy gì nên snapshot rỗng.
        Assert.Equal(0, BoxCount(await events.NextEventAsync("snapshot before the pipeline ran")));

        await harness.StartPipelineAsync();
        harness.PushTranslatedFrame("HELLO");
        Assert.Equal("HELLO", FirstBoxText(await events.NextBoxEventAsync("the translated frame")));

        await harness.Pipeline.StopAsync(CancellationToken.None);

        Assert.False(harness.Visibility.ShouldShowObs);
        Assert.Null(harness.State.Latest);
        // Exactly one clear payload, then silence. / Đúng một payload xóa, rồi im lặng.
        Assert.Equal(0, BoxCount(await events.NextEventAsync("the stop's clear payload")));
        await events.AssertSilenceAsync(TimeSpan.FromMilliseconds(1500), "anything after the stop");
    }

    [Fact]
    public async Task CoreSideStop_ClearsFeed_WithExactlyOneEmptyPayload()
    {
        // One unexpected stop raises PipelineStateChanged AND StateChanged back to back
        // (PipelineController:839-842) — Review Focus #4: still exactly one empty payload.
        // Một lần dừng đột xuất nổ PipelineStateChanged VÀ StateChanged liền nhau
        // (PipelineController:839-842) — Review Focus #4: vẫn đúng một payload rỗng.
        await using var harness = new PipelineFeedHarness();
        await harness.Server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await using var events = await SseCollector.OpenAsync(http, harness.Server.Port);
        // Nothing is running yet, so the snapshot is empty. / Chưa chạy gì nên snapshot rỗng.
        Assert.Equal(0, BoxCount(await events.NextEventAsync("snapshot before the pipeline ran")));

        await harness.StartPipelineAsync();
        harness.PushTranslatedFrame("HELLO");
        Assert.Equal("HELLO", FirstBoxText(await events.NextBoxEventAsync("the translated frame")));

        harness.Peer.RaiseNotification("pipeline.state", new JsonObject
        {
            ["running"] = false,
            ["reason"] = "sidecar-exited",
        });

        Assert.False(harness.Visibility.ShouldShowObs);
        Assert.Null(harness.State.Latest);
        // Exactly one clear payload, then silence. / Đúng một payload xóa, rồi im lặng.
        Assert.Equal(0, BoxCount(await events.NextEventAsync("the stop's clear payload")));
        await events.AssertSilenceAsync(TimeSpan.FromMilliseconds(1500), "anything after the stop");
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

    /// <summary>
    /// Test double for the visibility gate: a plain value whose <see cref="Changed"/> only fires when
    /// the value flips, exactly like the real <see cref="OverlayVisibility"/> (spec 5.14).
    /// Bản thử cho gate hiển thị: giá trị thường với <see cref="Changed"/> chỉ nổ khi giá trị đổi, đúng
    /// như <see cref="OverlayVisibility"/> thật (spec 5.14).
    /// </summary>
    private sealed class FakeVisibility : IOverlayVisibility
    {
        private int _shown;

        public event Action? Changed;

        public bool ShouldShowObs => Volatile.Read(ref _shown) == 1;

        public bool Shown
        {
            get => Volatile.Read(ref _shown) == 1;
            set
            {
                int flag = value ? 1 : 0;
                if (Interlocked.Exchange(ref _shown, flag) == flag) return; // edge only / chỉ khi edge đổi
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Gate double that parks one chosen read inside <see cref="ShouldShowObs"/>: it answers from the value
    /// it read <em>before</em> parking, which is exactly the state of a thread preempted between its gate
    /// check and the enqueue. <see cref="HideWithoutEdge"/>/<see cref="ShowWithoutEdge"/> change the value
    /// with no edge — the flip that parked read is stale about — and <see cref="RaiseEdge"/> announces it
    /// the way the real <see cref="OverlayVisibility"/> does.
    /// Bản thử gate kẹt một lần đọc được chọn bên trong <see cref="ShouldShowObs"/>: nó trả lời theo giá trị
    /// đọc <em>trước</em> lúc kẹt, đúng bằng trạng thái của một thread bị preempt giữa kiểm tra gate và việc
    /// xếp hàng. <see cref="HideWithoutEdge"/>/<see cref="ShowWithoutEdge"/> đổi giá trị mà không nổ edge —
    /// cú lật mà lần đọc đang kẹt bị cũ so với nó — còn <see cref="RaiseEdge"/> báo lại edge đó đúng như
    /// <see cref="OverlayVisibility"/> thật.
    /// </summary>
    private sealed class ParkingGateVisibility : IOverlayVisibility
    {
        // Monitor (not a Task) for the hold: the parked getter answers inside a property, and a bounded
        // Monitor wait cannot hang the suite even if the feed never resumes it.
        // Dùng Monitor (không phải Task) cho chỗ kẹt: getter bị kẹt chạy bên trong một property, và một
        // Monitor wait có giới hạn không thể treo cả bộ test kể cả khi feed không chạy tiếp.
        private readonly object _holdLock = new();
        private TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _released;
        private int _shown = 1;

        public event Action? Changed;

        /// <summary>Completes once the armed read is parked. / Hoàn tất khi lần đọc đã được vũ trang đang kẹt.</summary>
        public Task Parked => _parked.Task;

        public bool ShouldShowObs
        {
            get
            {
                bool shown = Volatile.Read(ref _shown) == 1;
                if (Interlocked.Exchange(ref _armed, 0) != 1) return shown;

                _parked.TrySetResult();
                lock (_holdLock)
                {
                    while (Volatile.Read(ref _released) == 0 && Monitor.Wait(_holdLock, TimeSpan.FromSeconds(5)))
                    {
                    }
                }
                return shown; // the value from before the flip / giá trị trước cú lật
            }
        }

        /// <summary>Arms the next read to park. / Vũ trang để lần đọc kế tiếp kẹt lại.</summary>
        public void ParkNextRead()
        {
            Volatile.Write(ref _released, 0);
            _parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _armed, 1);
        }

        /// <summary>Frees the parked read. / Giải phóng lần đọc đang kẹt.</summary>
        public void Release()
        {
            Volatile.Write(ref _released, 1);
            lock (_holdLock)
            {
                Monitor.PulseAll(_holdLock);
            }
        }

        /// <summary>Flips the gate off without an edge. / Lật gate sang ẩn mà không nổ edge.</summary>
        public void HideWithoutEdge() => Volatile.Write(ref _shown, 0);

        /// <summary>Flips the gate back on without an edge. / Lật gate sang hiện mà không nổ edge.</summary>
        public void ShowWithoutEdge() => Volatile.Write(ref _shown, 1);

        /// <summary>Announces the current value as an edge, like the real gate. / Báo giá trị hiện tại như một edge, giống gate thật.</summary>
        public void RaiseEdge() => Changed?.Invoke();
    }

    /// <summary>
    /// Real pipeline + real <see cref="OverlayVisibility"/> + the feed bound to a loopback port, so
    /// both stop paths run exactly as in the app (<c>IsRunning = false</c> → <c>Clear()</c> → pipeline
    /// events) and the payload count on the wire is what gets asserted.
    /// Pipeline thật + <see cref="OverlayVisibility"/> thật + feed bind cổng loopback, để cả hai đường
    /// dừng chạy đúng như trong app (<c>IsRunning = false</c> → <c>Clear()</c> → event pipeline) và con
    /// số bị assert là số payload trên wire.
    /// </summary>
    private sealed class PipelineFeedHarness : IAsyncDisposable
    {
        private readonly string _settingsPath;

        public OverlayState State { get; } = new();
        public FakeCorePeer Peer { get; } = new() { IsStarted = true };
        public AppSettingsService Settings { get; }
        public PipelineController Pipeline { get; }
        public OverlayVisibility Visibility { get; }
        public OverlayFeedServer Server { get; }

        public PipelineFeedHarness()
        {
            _settingsPath = Path.Combine(Path.GetTempPath(), $"retran-feed-{Guid.NewGuid():N}.json");
            Settings = new AppSettingsService(_settingsPath);
            Settings.Save(AppSettings.Default with { OverlayOnGame = true });

            // The context is never queried here (no translate.* notification), so no migration runs
            // and the db file is never created.
            // Context không bao giờ bị truy vấn ở đây (không có notification translate.*), nên không
            // migrate và file db không bao giờ được tạo.
            var dbOptions = new DbContextOptionsBuilder<RetranDbContext>()
                .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"retran-feed-{Guid.NewGuid():N}.db")};Pooling=False")
                .Options;
            Pipeline = new PipelineController(
                Peer,
                Settings,
                new TranslationCache(new RetranDbContext(dbOptions)),
                State,
                NullLogger<PipelineController>.Instance);
            Visibility = new OverlayVisibility(Settings, Pipeline, NullLogger<OverlayVisibility>.Instance);
            Server = new OverlayFeedServer(State, FreePort(), visibility: Visibility);
        }

        public Task StartPipelineAsync()
            => Pipeline.StartAsync(new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null), CancellationToken.None);

        /// <summary>
        /// Pushes one <c>frame.translated</c> through the wired handler so the feed has real boxes to
        /// lose. Đẩy một <c>frame.translated</c> qua handler đã gắn để feed có box thật cần mất.
        /// </summary>
        public void PushTranslatedFrame(string text) => Peer.RaiseNotification("frame.translated", new JsonObject
        {
            ["seq"] = 1,
            ["w"] = 1920,
            ["h"] = 1080,
            ["boxes"] = new JsonArray(new JsonObject
            {
                ["x"] = 0.10, ["y"] = 0.20, ["w"] = 0.30, ["h"] = 0.05,
                ["text"] = text, ["translated"] = "dịch của " + text,
            }),
        });

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Visibility.Dispose();
            try { File.Delete(_settingsPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FakeCorePeer : ICorePeer
    {
        public bool IsStarted { get; set; }

        public event Action<string, JsonNode?>? NotificationReceived;
        public event Action<Exception>? HandlerError;
        public event Action<int>? Exited;

        public Task StartCoreAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
            => Task.FromResult<JsonNode?>(new JsonObject { ["running"] = method == "core.start" });

        public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler) { }

        public void RaiseNotification(string method, JsonNode? @params)
            => NotificationReceived?.Invoke(method, @params);

        public void RaiseHandlerError(Exception ex) => HandlerError?.Invoke(ex);

        public void RaiseExited(int exitCode) => Exited?.Invoke(exitCode);
    }
}
