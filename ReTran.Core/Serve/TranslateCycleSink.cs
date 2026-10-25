namespace ReTran.Core.Serve;

using System.Text.Json.Nodes;
using ReTran.Core.Cli;
using ReTran.Core.JsonRpc;
using ReTran.Core.Translate;

/// <summary>
/// Serve-mode cycle sink: emits <c>frame.translated</c> with the full box list on every cycle
/// and runs cache lookup + translation in the background so the tick never blocks on them.
/// Sink chu kỳ ở chế độ serve: emit <c>frame.translated</c> với danh sách box đầy đủ mỗi chu kỳ
/// và chạy tra cache + dịch ở chế độ nền để tick không bao giờ bị block bởi chúng.
/// </summary>
/// <remarks>
/// Box coordinates are normalized to 0–1 and every box carries an explicit <c>translated</c>
/// field: <see langword="null"/> until a translation exists, then the cached or freshly
/// translated text (spec §3.3). A text is queued at most once per session — once translated
/// (session hit) or while its translation is in flight it is never scheduled again.
/// Toạ độ box được chuẩn hoá về 0–1 và mọi box đều có field <c>translated</c> tường minh:
/// <see langword="null"/> khi chưa có bản dịch, sau đó là text đã dịch từ cache hoặc vừa dịch
/// (spec §3.3). Mỗi text chỉ được xếp hàng tối đa một lần mỗi phiên — khi đã có bản dịch
/// (trúng session) hoặc đang dịch dở thì không bao giờ bị xếp hàng lại.
/// </remarks>
public sealed class TranslateCycleSink : ICycleSink
{
    /// <summary>Upper bound on one cache lookup so a slow App reply cannot stall the chain.
    /// Giới trên một lượt tra cache để App trả lời chậm không thể chặn chuỗi task nền.</summary>
    private static readonly TimeSpan CacheLookupTimeout = TimeSpan.FromSeconds(2);

    private readonly string _context;
    private readonly string _src;
    private readonly string _dst;
    private readonly ITranslator _translator;
    private readonly ICacheChannel _cache;
    private readonly IJsonRpcNotifier _notifier;
    private readonly TextWriter _stderr;

    /// <summary>Hand-off slot for the annotation loop (preview). Khe giao cho vòng ghi chú khung (preview).</summary>
    private readonly AnnotationSnapshot _snapshot;

    /// <summary>Bounds concurrent translator calls to 4 (spec §4.2). Giới số lời gọi dịch đồng thời ở 4 (spec §4.2).</summary>
    private readonly SemaphoreSlim _translateGate = new(4, 4);

    private readonly object _sync = new();

    /// <summary>
    /// Session translations, keyed case-insensitively like <c>OcrDiff</c>. Bản dịch trong phiên,
    /// key không phân biệt hoa thường như <c>OcrDiff</c>.
    /// </summary>
    private readonly Dictionary<string, string> _session =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Texts queued for (or inside) background processing. Text đã xếp hàng (hoặc đang chạy nền).
    /// </summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Chain of background batches — each new batch awaits the previous link so lookups stay
    /// serialized; updated only under <see cref="_sync"/>. Chuỗi các lô nền — mỗi lô mới chờ
    /// link trước đó để lượt tra cache luôn tuần tự; chỉ cập nhật dưới <see cref="_sync"/>.
    /// </summary>
    private Task _bg = Task.CompletedTask;

    /// <summary>
    /// Creates a sink that emits <c>frame.translated</c> and translates new texts in the background.
    /// Tạo sink emit <c>frame.translated</c> và dịch text mới ở chế độ nền.
    /// </summary>
    /// <param name="context">Content-context key for cache calls (e.g. <c>screen:0</c>). Key ngữ cảnh nội dung cho các lệnh cache (vd <c>screen:0</c>).</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="translator">Translation provider used for cache misses. Nhà cung cấp dịch cho các text miss cache.</param>
    /// <param name="cache">Batched cache channel to the App. Kênh cache theo lô tới App.</param>
    /// <param name="notifier">JSON-RPC sender for <c>frame.translated</c>. Bộ gửi JSON-RPC cho <c>frame.translated</c>.</param>
    /// <param name="stderr">Writer for best-effort warnings; never the hot path. Nơi ghi cảnh báo best-effort; không thuộc hot path.</param>
    /// <param name="snapshot">Slot publishing the box list for the frame annotator. Khe đăng bố danh sách box cho bộ ghi chú khung.</param>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    public TranslateCycleSink(
        string context, string src, string dst,
        ITranslator translator,
        ICacheChannel cache,
        IJsonRpcNotifier notifier,
        TextWriter stderr,
        AnnotationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(snapshot);
        _context = context;
        _src = src;
        _dst = dst;
        _translator = translator;
        _cache = cache;
        _notifier = notifier;
        _stderr = stderr;
        _snapshot = snapshot;
    }

    /// <summary>
    /// Emits <c>frame.translated</c> for this cycle — always, even with zero boxes — then queues
    /// background cache lookup and translation for texts not yet handled, without awaiting them.
    /// Emit <c>frame.translated</c> cho chu kỳ này — luôn luôn, kể cả khi không có box nào — rồi
    /// xếp hàng tra cache + dịch nền cho các text chưa xử lý, không await chúng.
    /// </summary>
    /// <param name="seq">1-based cycle number; never reset. Số chu kỳ tính từ 1; không bao giờ reset.</param>
    /// <param name="frameWidth">Captured frame width in pixels. Chiều rộng khung hình (pixel).</param>
    /// <param name="frameHeight">Captured frame height in pixels. Chiều cao khung hình (pixel).</param>
    /// <param name="spotResult">
    /// Pipeline result: <c>{spots:[{box,text,score}], elapsed_ms, new_texts:[...]}</c>.
    /// Kết quả pipeline: <c>{spots:[{box,text,score}], elapsed_ms, new_texts:[...]}</c>.
    /// </param>
    /// <param name="ct">Cancellation token for the cycle. Token hủy của chu kỳ.</param>
    /// <returns>
    /// A task that completes once the notification was sent and the batch was queued — it never
    /// waits for translation. Task hoàn tất ngay khi thông báo đã gửi và lô đã xếp hàng — không
    /// bao giờ chờ dịch xong.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="spotResult"/> is null.</exception>
    public async Task OnCycleAsync(
        long seq, int frameWidth, int frameHeight, JsonObject spotResult, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spotResult);

        // (1) Build the full box list from the session so every cycle re-annotates all spots.
        // (1) Dựng danh sách box đầy đủ từ session để mỗi chu kỳ ghi chú lại toàn bộ spot.
        var boxes = new JsonArray();
        var annotBoxes = new List<AnnotBox>();
        if (spotResult["spots"] is JsonArray spots)
        {
            foreach (JsonNode? node in spots)
            {
                if (node is not JsonObject spot) continue;
                string text = ReadText(spot["text"]);
                var (x, y, w, h) = SpotGeometry.NormalizeQuad(
                    spot["box"] as JsonArray, frameWidth, frameHeight);
                string? translated;
                lock (_sync) _session.TryGetValue(text, out translated);
                boxes.Add(new JsonObject
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["w"] = w,
                    ["h"] = h,
                    ["text"] = text,
                    // Explicit null until a translation exists (spec §3.3). Tường minh null khi chưa có bản dịch (spec §3.3).
                    ["translated"] = translated,
                });
                annotBoxes.Add(new AnnotBox(x, y, w, h, text, translated));
            }
        }

        // Publish for the annotation loop before emitting: the annotator draws exactly this list
        // (an empty list clears vanished text). Đăng cho vòng ghi chú trước khi emit: bộ ghi chú
        // vẽ đúng danh sách này (list rỗng = text đã biến mất).
        _snapshot.Publish(frameWidth, frameHeight, annotBoxes);

        // (2) Emit before scheduling: the first emit must show pre-background state.
        // (2) Emit trước khi xếp hàng: lần emit đầu phải cho thấy trạng thái trước khi chạy nền.
        await _notifier.NotifyAsync("frame.translated", new JsonObject
        {
            ["seq"] = seq,
            ["w"] = frameWidth,
            ["h"] = frameHeight,
            ["boxes"] = boxes,
        }, ct);

        // (3)+(4) Queue unknown texts; empty new_texts is the common no-op path.
        // (3)+(4) Xếp hàng các text chưa biết; new_texts rỗng là path phổ biến không làm gì.
        var candidates = new List<string>();
        if (spotResult["new_texts"] is JsonArray newTexts)
        {
            foreach (JsonNode? node in newTexts)
            {
                string t = ReadText(node);
                if (!string.IsNullOrWhiteSpace(t)) candidates.Add(t);
            }
        }

        lock (_sync)
        {
            var scheduled = new List<string>();
            foreach (string t in candidates.Distinct())
            {
                if (_session.ContainsKey(t)) continue;          // already translated / cached this session
                if (!_inFlight.Add(t)) continue;                // already queued or running
                scheduled.Add(t);
            }
            if (scheduled.Count == 0) return;

            Task previous = _bg;
            _bg = Task.Run(() => ChainAsync(previous, scheduled, ct));
        }
    }

    /// <summary>
    /// Awaits the current background chain until it drains, looping while new links get appended
    /// during the wait. Test/diagnostic helper — the production path never calls it.
    /// Chờ chuỗi task nền hiện tại drain xong, lặp lại nếu có link mới nối vào trong lúc chờ.
    /// Helper cho test/diagnostic — production path không gọi.
    /// </summary>
    /// <returns>
    /// A task that completes when no background work remains. Task hoàn tất khi không còn việc nền.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Propagated when the sink was stopped while draining. lan truyền khi sink bị dừng trong lúc drain.
    /// </exception>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task snapshot;
            lock (_sync) snapshot = _bg;
            await snapshot;
            lock (_sync)
            {
                if (ReferenceEquals(_bg, snapshot)) return;
            }
        }
    }

    /// <summary>
    /// Runs one queued batch after the previous chain link finishes; a failed link is logged there
    /// and never breaks the chain. Chạy một lô đã xếp hàng sau khi link trước của chuỗi xong;
    /// link lỗi đã được ghi log ở đó và không bao giờ làm đứt chuỗi.
    /// </summary>
    private async Task ChainAsync(Task previous, List<string> texts, CancellationToken ct)
    {
        try
        {
            await previous;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // stopped mid-drain -> surface cancellation, skip this link. đang dừng giữa chừng -> lan truyền hủy, bỏ qua link này
        }
        catch (Exception)
        {
            // Previous link already logged its own failure; keep draining. Link trước đã tự ghi log lỗi; tiếp tục drain.
        }
        await BackgroundAsync(texts, ct);
    }

    /// <summary>
    /// One background batch: cache lookup (hard 2 s bound, failure = all-miss) → session hits →
    /// parallel translation (≤ 4) → <see cref="ICacheChannel.PutAsync"/> (the <c>translate.put</c> path).
    /// All failures are logged to stderr, never rethrown, except cancellation when the sink is stopped.
    /// Một lô nền: tra cache (giới cứng 2 s, lỗi = all-miss) → ghi hit vào session → dịch song song
    /// (≤ 4) → <see cref="ICacheChannel.PutAsync"/> (đường <c>translate.put</c>). Mọi lỗi đều ghi
    /// stderr, không lan truyền, trừ khi bị hủy do sink dừng.
    /// </summary>
    private async Task BackgroundAsync(List<string> texts, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<string> misses;
            Task<CacheLookupResult>? lookupTask = null;
            try
            {
                using var lookupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lookupCts.CancelAfter(CacheLookupTimeout);
                lookupTask = _cache.LookupAsync(_context, _src, _dst, texts, lookupCts.Token);

                // Hard 2 s bound even when the channel ignores the cancellation token, so the
                // background chain can never stall forever (spec §4.1).
                // Giới cứng 2 s kể cả khi channel bỏ qua token hủy, để chuỗi nền không bao giờ
                // bị đình trệ vĩnh viễn (spec §4.1).
                CacheLookupResult result = await lookupTask.WaitAsync(CacheLookupTimeout, ct);
                misses = result.Misses;
                lock (_sync)
                {
                    foreach (KeyValuePair<string, string> hit in result.Hits)
                        _session[hit.Key] = hit.Value;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                AbandonLookup(lookupTask);
                throw; // stopped while looking up -> no point translating under a dead token. đang dừng khi tra -> không dịch với token đã hủy
            }
            catch (Exception ex)
            {
                // Lookup failed or timed out (incl. the hard WaitAsync bound): treat the whole
                // batch as misses (spec §4.2).
                // Tra lỗi hoặc hết 2 s (gồm cả giới cứng WaitAsync): coi toàn bộ lô là miss (spec §4.2).
                AbandonLookup(lookupTask);
                _stderr.WriteLine($"warn: cache lookup failed ({ex.Message})");
                misses = texts;
            }

            List<string> pending;
            lock (_sync)
            {
                pending = misses.Where(t => !_session.ContainsKey(t)).Distinct().ToList();
            }

            if (pending.Count > 0)
            {
                (string Text, string Translated)?[] results = await Task.WhenAll(
                    pending.Select(t => TranslateOneAsync(t, ct)));
                List<(string Text, string Translated)> entries = results
                    .Where(r => r is not null)
                    .Select(r => r!.Value)
                    .ToList();
                if (entries.Count > 0)
                    await _cache.PutAsync(_context, _src, _dst, entries, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: translate background failed ({ex.Message})");
        }
        finally
        {
            lock (_sync)
            {
                foreach (string t in texts) _inFlight.Remove(t);
            }
        }
    }

    /// <summary>
    /// Observes a late fault on an abandoned lookup task so it never surfaces as an unobserved
    /// task exception. Quan sát fault đến muộn của một task lookup bị bỏ dở để nó không bao giờ
    /// thành unobserved task exception.
    /// </summary>
    private static void AbandonLookup(Task<CacheLookupResult>? lookupTask)
    {
        if (lookupTask is null) return; // synchronous throw before the task was obtained. ném đồng bộ trước khi có task
        _ = lookupTask.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Translates one text under the 4-slot gate, records it in the session, and logs a stderr
    /// warning on failure without retrying. Dịch một text dưới gate 4 khe, ghi vào session, và ghi
    /// cảnh báo stderr khi thất bại mà không retry.
    /// </summary>
    /// <returns>The pair on success, or <see langword="null"/> when translation failed. Cặp khi
    /// thành công, hoặc <see langword="null"/> khi dịch thất bại.</returns>
    private async Task<(string Text, string Translated)?> TranslateOneAsync(
        string text, CancellationToken ct)
    {
        await _translateGate.WaitAsync(ct);
        try
        {
            string translated = await _translator.TranslateAsync(text, _src, _dst, ct);
            lock (_sync) _session[text] = translated;
            return (text, translated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // stopped mid-translate -> propagate, do not log as a translation failure. đang dừng khi dịch -> lan truyền, không ghi là lỗi dịch
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"warn: translate failed '{text}': {ex.Message}");
            return null;
        }
        finally
        {
            _translateGate.Release();
        }
    }

    /// <summary>
    /// Reads a JSON node as text, yielding <see cref="string.Empty"/> for missing or non-string
    /// values so malformed spots never crash the cycle. Đọc node JSON thành text, trả
    /// <see cref="string.Empty"/> khi thiếu hoặc không phải string để spot hỏng không làm crash chu kỳ.
    /// </summary>
    private static string ReadText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null
            ? text
            : string.Empty;
}
