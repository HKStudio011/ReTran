using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReTran_App.Core;
using ReTran_App.Services.OverlayFeed;

namespace ReTran.Tests;

public sealed class OverlayFeedServerTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
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

    [Fact]
    public async Task Start_ServesOverlayPage()
    {
        var state = new OverlayState();
        await using var server = new OverlayFeedServer(state, FreePort());
        await server.StartAsync(CancellationToken.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Contains("text/html", response.Content.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task Overlay_Assets_AreServed()
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

        using var js = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay/overlay.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.NotNull(js.Content.Headers.ContentType);
        Assert.Contains("javascript", js.Content.Headers.ContentType.ToString());
        Assert.Contains("EventSource", await js.Content.ReadAsStringAsync());

        using var css = await http.GetAsync($"http://127.0.0.1:{server.Port}/overlay/overlay.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.NotNull(css.Content.Headers.ContentType);
        Assert.Contains("text/css", css.Content.Headers.ContentType.ToString());
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
            [new OverlayBox(0.5, 0.25, 0.2, 0.1, "A", "B")]));

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
        }
        finally
        {
            blocker.Stop();
        }
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
}
