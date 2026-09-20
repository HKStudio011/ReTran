using System.Net.Http;
using System.Threading;
using ReTran_App.Core;
using Xunit;

public class OverlayFeedServerTests
{
    [Fact]
    public async Task Serves_OverlayHtml_And_SseEvent()
    {
        var state = new OverlayState();
        await using var server = new ReTran_App.Services.OverlayFeedServer(state, port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var http = new HttpClient();
        string html = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/overlay", cts.Token);
        Assert.Contains("overlay-root", html);

        using var sse = await http.GetStreamAsync($"http://127.0.0.1:{server.Port}/events", cts.Token);
        using var reader = new StreamReader(sse);
        var readTask = reader.ReadLineAsync();
        state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.1, 0.1, 0.2, 0.05, "a", "A") }));
        string? first = await readTask.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.StartsWith("data: {\"boxes\":[{\"x\":0.1", first);
    }
}
