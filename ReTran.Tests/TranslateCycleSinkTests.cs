using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.Serve;
using ReTran.Core.Translate;

namespace RetranCore.Tests;

public class TranslateCycleSinkTests
{
    private static JsonObject Result(params string[] new_texts)
    {
        var spots = new JsonArray();
        foreach (string t in new_texts)
            spots.Add(new JsonObject
            {
                ["text"] = t,
                ["box"] = JsonNode.Parse("[[100,50],[300,50],[300,150],[100,150]]"),
                ["score"] = 0.9,
            });
        return new JsonObject
        {
            ["spots"] = spots,
            ["new_texts"] = new JsonArray(new_texts.Select(t => (JsonNode?)t).ToArray()), // brief ghi Cast<object>() — CS1503 (JsonArray chỉ nhận JsonNode[]); sửa cơ học, semantic giữ nguyên
            ["elapsed_ms"] = 1,
        };
    }

    private static JsonObject MergedBlockResult() // 1 spot: box [[100,50],[400,50],[400,250],[100,250]], text "HELLO WORLD", paragraph true, new_texts ["HELLO WORLD"]
    {
        var spots = new JsonArray();
        spots.Add(new JsonObject
        {
            ["text"] = "HELLO WORLD",
            ["box"] = JsonNode.Parse("[[100,50],[400,50],[400,250],[100,250]]"),
            ["score"] = 0.9,
            ["paragraph"] = true,
        });
        return new JsonObject
        {
            ["spots"] = spots,
            ["new_texts"] = new JsonArray(new JsonNode[] { "HELLO WORLD" }),
            ["elapsed_ms"] = 1,
        };
    }

    private sealed class StubTranslator : ITranslator
    {
        public string Name => "stub";
        public string Result = "β";
        public bool ThrowOnTranslate;
        public bool BlockForever;
        public List<string> CallTexts { get; } = new();
        public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct)
        {
            CallTexts.Add(text);
            if (BlockForever)
                return new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously).Task; // treo có chủ đích
            if (ThrowOnTranslate) throw new HttpRequestException("stub translate failed");
            return Task.FromResult(Result);
        }
    }

    private sealed class StubCache : ICacheChannel
    {
        public Dictionary<string, string> Hits { get; set; } = new();
        public List<string>? Misses { get; set; }   // null -> tính từ input trừ Hits
        public bool ThrowOnLookup;
        public TaskCompletionSource<bool>? HangLookup; // nếu set, lookup treo tới khi test release (fix round 2)
        public List<IReadOnlyList<(string Text, string Translated)>> Puts { get; } = new();

        public Task<CacheLookupResult> LookupAsync(string context, string src, string dst, IReadOnlyList<string> texts, CancellationToken ct)
        {
            if (HangLookup is not null)
                return HangLookup.Task.ContinueWith(
                    _ => new CacheLookupResult(new Dictionary<string, string>(), texts),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            if (ThrowOnLookup) throw new TimeoutException("stub cache down");
            var hits = new Dictionary<string, string>();
            foreach (string t in texts)
                if (Hits.TryGetValue(t, out string? v)) hits[t] = v;
            IReadOnlyList<string> misses = Misses ?? texts.Where(t => !hits.ContainsKey(t)).ToList();
            return Task.FromResult(new CacheLookupResult(hits, misses));
        }

        public Task PutAsync(string context, string src, string dst, IReadOnlyList<(string Text, string Translated)> entries, CancellationToken ct)
        {
            Puts.Add(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class StubNotifier : IJsonRpcNotifier
    {
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public IEnumerable<string> Methods => Calls.Select(c => c.Method);
        public JsonNode? Single(string method) => Calls.Where(c => c.Method == method).Select(c => c.Params).Single();
        public JsonNode? Last(string method) => Calls.Last(c => c.Method == method).Params;
        public Task NotifyAsync(string method, JsonNode? @params, CancellationToken ct = default)
        {
            Calls.Add((method, @params));
            return Task.CompletedTask;
        }
    }

    private static TranslateCycleSink Create(
        ITranslator tr, ICacheChannel cache, StubNotifier n, TextWriter? err = null,
        AnnotationSnapshot? snapshot = null)
        => new("screen:0", "auto", "vi", tr, cache, n, err ?? new StringWriter(),
            snapshot ?? new AnnotationSnapshot());

    [Fact]
    public async Task Cycle_EmitsFrameTranslated_EveryTime_EvenWithoutNewTexts()
    {
        var n = new StubNotifier();
        await Create(new StubTranslator(), new StubCache(), n).OnCycleAsync(1, 1000, 500, Result(), CancellationToken.None);
        var p = n.Single("frame.translated");
        Assert.Equal(1, p!["seq"]!.GetValue<long>());
        Assert.Equal(1000, p["w"]!.GetValue<int>());
        Assert.Empty(p["boxes"]!.AsArray());
    }

    [Fact]
    public async Task Cycle_BoxesNormalized_AnnotatedOnNextCycle()
    {
        var cache = new StubCache { Hits = new() { ["HELLO"] = "Xin chào" } };
        var n = new StubNotifier();
        var sink = Create(new StubTranslator { Result = "β" }, cache, n);
        await sink.OnCycleAsync(1, 1000, 500, Result("HELLO", "FRESH"), CancellationToken.None);
        // chu kỳ 1 emit trước khi background chạy -> cả 2 box chưa có bản dịch
        Assert.Null(n.Single("frame.translated")!["boxes"]!.AsArray()[1]!["translated"]);
        await sink.WhenIdleAsync(); // hit HELLO vào session; FRESH dịch "β" + put
        await sink.OnCycleAsync(2, 1000, 500, Result("HELLO", "FRESH"), CancellationToken.None);

        var boxes = n.Last("frame.translated")!["boxes"]!.AsArray();
        Assert.Equal(2, boxes.Count);
        var hello = boxes[0]!.AsObject();
        Assert.Equal("HELLO", hello["text"]!.GetValue<string>());
        Assert.Equal("Xin chào", hello["translated"]!.GetValue<string>());
        Assert.Equal(0.1, hello["x"]!.GetValue<double>(), 6); // 100/1000
        Assert.Equal(0.1, hello["y"]!.GetValue<double>(), 6); // 50/500
        Assert.Equal(0.2, hello["w"]!.GetValue<double>(), 6); // (300-100)/1000
        Assert.Equal(0.2, hello["h"]!.GetValue<double>(), 6); // (150-50)/500
        Assert.Equal("β", boxes[1]!.AsObject()["translated"]!.GetValue<string>());
    }

    [Fact]
    public async Task NewTexts_Misses_TranslatedThenPut()
    {
        var cache = new StubCache { Hits = new() { ["A"] = "α" }, Misses = new List<string> { "B" } };
        var tr = new StubTranslator { Result = "β" };
        var n = new StubNotifier();
        var sink = Create(tr, cache, n);
        await sink.OnCycleAsync(1, 100, 100, Result("A", "B"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.Equal(new[] { "B" }, tr.CallTexts); // A là hit -> không gọi dịch
        // ruling T-F3: observe tại cache layer (sink không notify translate.put trực tiếp)
        var put = Assert.Single(cache.Puts);
        var entry = Assert.Single(put);
        Assert.Equal("B", entry.Text);
        Assert.Equal("β", entry.Translated);
    }

    [Fact]
    public async Task OnCycle_PublishesAnnotationSnapshot()
    {
        var cache = new StubCache { Hits = new() { ["HELLO"] = "Xin chào" } };
        var snapshot = new AnnotationSnapshot();
        var sink = Create(new StubTranslator(), cache, new StubNotifier(), snapshot: snapshot);

        await sink.OnCycleAsync(1, 1000, 500, Result("HELLO"), CancellationToken.None);
        await sink.WhenIdleAsync(); // HELLO hit -> session có bản dịch
        await sink.OnCycleAsync(2, 1000, 500, Result("HELLO"), CancellationToken.None);

        AnnotSnapshot? latest = snapshot.TryGet();
        Assert.NotNull(latest);
        Assert.Equal(1000, latest!.FrameWidth);
        Assert.Equal(500, latest.FrameHeight);
        AnnotBox box = Assert.Single(latest.Boxes);
        Assert.Equal("HELLO", box.Text);
        Assert.Equal("Xin chào", box.Translated);
        Assert.Equal(0.1, box.X, 6); // 100/1000
        Assert.Equal(0.1, box.Y, 6); // 50/500
        Assert.Equal(0.2, box.W, 6); // (300-100)/1000
        Assert.Equal(0.2, box.H, 6); // (150-50)/500
    }

    [Fact]
    public async Task Cycle_MergedSpot_SingleBoxWithParagraphFlag()
    {
        var n = new StubNotifier();
        var snapshot = new AnnotationSnapshot();
        var sink = Create(new StubTranslator(), new StubCache(), n, snapshot: snapshot);
        await sink.OnCycleAsync(1, 1000, 500, MergedBlockResult(), CancellationToken.None);

        // wire: đúng 1 box với cờ paragraph (spec 2026-11-01)
        var box = Assert.Single(n.Single("frame.translated")!["boxes"]!.AsArray())!.AsObject();
        Assert.Equal("HELLO WORLD", box["text"]!.GetValue<string>());
        Assert.True(box["paragraph"]!.GetValue<bool>());

        // snapshot: AnnotBox mang cùng cờ cho bộ ghi chú khung
        AnnotSnapshot? latest = snapshot.TryGet();
        Assert.NotNull(latest);
        AnnotBox annot = Assert.Single(latest!.Boxes);
        Assert.Equal("HELLO WORLD", annot.Text);
        Assert.True(annot.IsParagraph);
    }

    [Fact]
    public async Task Cycle_PlainSpot_BoxHasParagraphFalse()
    {
        var n = new StubNotifier();
        var snapshot = new AnnotationSnapshot();
        var sink = Create(new StubTranslator(), new StubCache(), n, snapshot: snapshot);
        await sink.OnCycleAsync(1, 1000, 500, Result("HELLO"), CancellationToken.None);

        // spot thường không có key paragraph -> wire false (App cũ bỏ qua field vẫn an toàn)
        var box = Assert.Single(n.Single("frame.translated")!["boxes"]!.AsArray())!.AsObject();
        Assert.False(box["paragraph"]!.GetValue<bool>());

        AnnotSnapshot? latest = snapshot.TryGet();
        Assert.NotNull(latest);
        Assert.False(Assert.Single(latest!.Boxes).IsParagraph);
    }

    [Fact]
    public async Task CacheFailure_TreatedAsAllMisses()
    {
        var cache = new StubCache { ThrowOnLookup = true };
        var tr = new StubTranslator { Result = "β" };
        var n = new StubNotifier();
        var sink = Create(tr, cache, n);
        await sink.OnCycleAsync(1, 100, 100, Result("X"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.Equal(new[] { "X" }, tr.CallTexts); // lookup ném -> coi hết là miss -> dịch hết
    }

    [Fact]
    public async Task TranslateFailure_NoPut_NoThrow()
    {
        var err = new StringWriter();
        var tr = new StubTranslator { ThrowOnTranslate = true };
        var n = new StubNotifier();
        var cache = new StubCache();
        var sink = Create(tr, cache, n, err);
        await sink.OnCycleAsync(1, 100, 100, Result("BAD"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.DoesNotContain("translate.put", n.Methods);  // không ghi DB dòng lỗi
        Assert.Empty(cache.Puts); // ruling T-F3: dịch lỗi -> không PutAsync -> không notification, không ghi cache
        Assert.Contains("warn", err.ToString(), StringComparison.OrdinalIgnoreCase); // có log stderr
    }

    [Fact]
    public async Task SlowTranslate_DoesNotBlockCycle()
    {
        var tr = new StubTranslator { BlockForever = true };
        var sink = Create(tr, new StubCache { Misses = new List<string> { "SLOW" } }, new StubNotifier());
        Task cycle = sink.OnCycleAsync(1, 100, 100, Result("SLOW"), CancellationToken.None);
        Task done = await Task.WhenAny(cycle, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.Same(cycle, done); // chu kỳ trả về dù dịch còn treo
                               // (không WhenIdleAsync ở test này — background treo có chủ đích)
    }

    [Fact]
    public async Task RepeatTexts_Deduped_InFlightAndSession()
    {
        var tr = new StubTranslator { Result = "β" };
        var n = new StubNotifier();
        var sink = Create(tr, new StubCache(), n);
        await sink.OnCycleAsync(1, 100, 100, Result("DUP"), CancellationToken.None);
        await sink.OnCycleAsync(2, 100, 100, Result("DUP"), CancellationToken.None);
        await sink.WhenIdleAsync();
        Assert.Single(tr.CallTexts); // chỉ dịch 1 lần
    }

    [Fact]
    public async Task LookupHanging_TimesOutAllMiss_NoHang()
    {
        var hang = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new StubCache { HangLookup = hang };
        var tr = new StubTranslator { Result = "β" };
        var n = new StubNotifier();
        var sink = Create(tr, cache, n);
        await sink.OnCycleAsync(1, 100, 100, Result("STUCK"), CancellationToken.None);
        // hard bound 2s: chain drain xong trước 10s -> không treo dù channel bỏ qua token
        await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(n.Single("frame.translated")!["boxes"]!.AsArray()[0]!["translated"]);
        Assert.Equal(new[] { "STUCK" }, tr.CallTexts); // lookup treo -> timeout -> all-miss -> dịch hết
        hang.TrySetResult(true); // release để dọn dẹp
    }
}
