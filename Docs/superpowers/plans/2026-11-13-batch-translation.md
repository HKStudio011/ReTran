# Plan: Gộp nhiều text thành một request dịch (batch Google)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mỗi lô text mới trong một chu kỳ chỉ tốn ⌈N/K⌉ request Google (thay vì N), bằng cách nối các text bằng `\n` trong một `q`, tách kết quả theo bản gốc echo lại, fallback per-text khi mapping lỗi.

**Architecture:** `ITranslator.TranslateBatchAsync` (default impl = loop per-text, provider khác không bị ảnh hưởng) → `GoogleWebTranslator` override: chuẩn hoá text → chia lô theo độ dài URL → 1 GET/lô → walk segment để map ngược. `TranslateCycleSink.BackgroundAsync` gọi batch 1 lần qua gate 4 slot; `FormatException` (mapping sai) → fallback per-text; lỗi HTTP → cả lô thất bại, không fallback (chống dội request khi rate limit).

**Tech Stack:** C# .NET 10 (`ReTran.Core`), xUnit v3 (MTP runner), `System.Text.Json`, `HttpClient` sẵn có — không dependency mới.

**Spec:** `Docs/superpowers/specs/2026-11-13-batch-translation-design.md` (user duyệt 2026-11-13). Spec nền: `2026-10-06-m4-translation-design.md` §4.2–4.3.

## Global Constraints

- **Không `git add -A`.** Repo có nhiều file WIP của user. Mỗi commit chỉ `git add` đúng các file của task. Không push. Branch `master`.
- Build chuẩn: `dotnet build "ReTran Project.slnx"` → 0 error (warning `xUnit1051` có sẵn).
- Test chuẩn: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<TênĐầyĐủ>"`. Test Core mang prefix `RetranCore.Tests.`; test App ở global namespace. **Baseline đã đo 2026-11-13: 486 passed / 0 failed.** Mọi task phải giữ nguyên con số đó trở lên.
- `ITranslator` là interface public — **default interface implementation** bắt buộc để 486 test hiện có xanh nguyên (stub cũ chỉ implement `TranslateAsync`).
- Không đổi hành vi đường lẻ `TranslateAsync`/`Parse` (test cũ phải xanh không sửa assert; chỉ được phép sửa comment).
- Hằng số lô `MaxBatchUrlLength = 1800` là `public const` (đo được: URL 1.474 ký tự OK với 12 dòng; test không truy cập private).
- Fallback CHỈ khi `FormatException` (mapping/parse sai). Lỗi HTTP/timeout/429 → cả lô thất bại, ghi stderr, không retry trong phiên (giữ nguyên M4 §4.2).
- Không đổi wire Core↔App (`core.start`, `translate.request_cache`, `translate.put`, `frame.translated`).
- Doc comment C# public API song ngữ Anh → Việt theo `AGENTS.md`.

## Review Focus

Năm failure mode spec ngụ ý nhưng không task nào tự phủ — mỗi dòng đã ghim test vào task sở hữu:

1. **Google gộp/tách dòng không theo input** (mapping sai) ⇒ không được mất chữ hay map lệch: phải ném `FormatException` → fallback per-text. → Task 2 `Batch_OriginalMismatch_ThrowsFormatException`, Task 3 `BatchMappingFailure_FallsBackPerText`.
2. **Text chứa `\n` (dữ liệu bẩn)** ⇒ không được phá khung chia lô/ánh xạ (D4 chuẩn hoá trước khi join). → Task 2 `Batch_TextWithNewline_IsNormalizedBeforeJoin`.
3. **Lỗi HTTP/429 giữa đường** ⇒ không được bắn thêm N request con (fallback kép làm nặng rate limit). → Task 3 `BatchHttpFailure_NoFallback_NoPut`.
4. **Thứ tự kết quả phải khớp input kể cả khi chia nhiều lô con** (map sai chữ-thành-dịch là bug im lặng). → Task 2 `Batch_Chunks_ByUrlLength_OrderPreserved`.
5. **Translator trả về sai số lượng kết quả** (contract violation của provider sau này) ⇒ sink không được index tràn, phải rơi về fallback. → Task 3 `Batch_WrongResultCount_FallsBack`.

---

### Task 1: `ITranslator.TranslateBatchAsync` (default impl) + test

**Files:**
- Modify: `ReTran.Core/Translate/ITranslator.cs` (thêm method sau `TranslateAsync` :24)
- Create: `ReTran.Tests/TranslatorBatchDefaultTests.cs`

**Interfaces:**
- Consumes: không.
- Produces: method public trên `ITranslator` (Task 2 override, Task 3 gọi):
  ```csharp
  Task<IReadOnlyList<string>> TranslateBatchAsync(
      IReadOnlyList<string> texts, string src, string dst, CancellationToken ct = default);
  ```
  Hợp đồng (ghi vào doc comment): kết quả **đúng thứ tự** input; thất bại = ném exception, không partial. Default impl là `async` default interface member: `await Task.WhenAll(texts.Select(t => TranslateAsync(t, src, dst, ct)))` trả `string[]`.

- [ ] **Step 1: Viết test failing** — `TranslatorBatchDefaultTests` (namespace `RetranCore.Tests`), stub chỉ implement `TranslateAsync`:
  - `DefaultBatch_LoopsPerText_PreservingOrder`: `ITranslator t = new PerTextOnly()` (gọi qua **interface**, default member không gọi được qua class); input `["a","b","c"]` → kết quả `["«a»","«b»","«c»"]` và `Calls == ["a","b","c"]` (thứ tự gọi = thứ tự input).
- [ ] **Step 2: Chạy test — xác nhận fail** (`TranslatorBatchDefaultTests`, compile error).
- [ ] **Step 3: Implement** — thêm method vào `ITranslator` với default impl + doc comment song ngữ (summary, param, returns, exception).
- [ ] **Step 4: Chạy test — pass** + full suite vẫn 486+ (`dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`).
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.Core/Translate/ITranslator.cs" "ReTran.Tests/TranslatorBatchDefaultTests.cs"
  git commit -m "feat(core): ITranslator.TranslateBatchAsync with per-text default implementation"
  ```

---

### Task 2: `GoogleWebTranslator.TranslateBatchAsync` — join `\n`, chia lô, map ngược

**Files:**
- Modify: `ReTran.Core/Translate/GoogleWebTranslator.cs` (TranslateAsync :76-107 tách helper; thêm batch path)
- Modify: `ReTran.Tests/GoogleWebTranslatorTests.cs` (thêm handler echo + 5 test)

**Interfaces:**
- Consumes: `ITranslator.TranslateBatchAsync` (Task 1).
- Produces (Task 3 gọi qua interface, test đọc const):
  ```csharp
  public const int MaxBatchUrlLength = 1800;   // ngân sách URL-escaped mỗi lô con
  public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string src, string dst, CancellationToken ct = default)
  private static List<List<string>> Chunk(IReadOnlyList<string> cleaned)
  private static List<string> ParseBatch(string raw, IReadOnlyList<string> texts)   // ném FormatException khi lệch
  private string BuildUrl(string src, string dst, string q)                          // tách từ TranslateAsync
  private async Task<string> GetRawAsync(string url, CancellationToken ct)           // HTTP + status + timeout (logic cũ)
  ```
  `TranslateAsync` lẻ chỉ còn `Parse(await GetRawAsync(BuildUrl(src, dst, text), ct))` — hành vi và message lỗi giữ nguyên.

- [ ] **Step 1: Viết test failing** — thêm vào `GoogleWebTranslatorTests`:
  - Handler mới `EchoHandler` (test infra — implementer chép đúng khối này): parse `q` từ URL (`Query.TrimStart('?').Split('&')` → tìm tham số `q=` → decode bằng `Uri.UnescapeDataString`), cắt theo `\n`, dựng body `[[[...segments...], null, "en", ...]}`; mỗi segment echo bản gốc (`i < last` thì thêm `\n` đuôi, khớp đo thật), bản dịch = `"«" + line + "»"` (cùng quy tắc `\n`), mọi chuỗi bọc `JsonSerializer.Serialize` để escape đúng. Cờ test: `bool CorruptOriginals` (echo thêm "X"), `bool SplitSegmentsPerLine` (cắt mỗi dòng thành 2 segment), `List<string> Urls`.
  - `Batch_SingleRequest_TranslatesAllTexts`: input `["A","B"]` → 1 URL; `Urls[0]` chứa `q=A%0AB`; kết quả `["«A»","«B»"]`.
  - `Batch_TextWithNewline_IsNormalizedBeforeJoin`: input `["A\nB"]` → `Urls[0]` chứa `q=A%20B` (không còn `%0A` trong text); kết quả `["«A B»"]` (bản dịch của text đã chuẩn hoá — sink map theo vị trí).
  - `Batch_OriginalMismatch_ThrowsFormatException`: `CorruptOriginals` → `Assert.ThrowsAsync<FormatException>`.
  - `Batch_MultiSegmentPerText_MapsBack`: `SplitSegmentsPerLine` → text "AB" trả 2 segment (`«A»`+`«B»`) vẫn map về "AB" (accOrig cộng dồn tới khớp).
  - `Batch_Chunks_ByUrlLength_OrderPreserved`: 3 text 900 ký tự "x" → `Urls.Count == 3` (mỗi lô 1 text: 900+900+1 = 1801 > 1800) và kết quả đúng thứ tự.
- [ ] **Step 2: Chạy test — xác nhận fail** (`GoogleWebTranslatorTests`).
- [ ] **Step 3: Implement** — `TranslateBatchAsync`: cleaned = mỗi text `Replace('\n',' ').Replace('\r',' ')`; với mỗi chunk từ `Chunk(cleaned)`: `joined = string.Join('\n', chunk)` → `GetRawAsync(BuildUrl(src, dst, joined))` → `results.AddRange(ParseBatch(raw, chunk))`; trả `results`. `Chunk`: greedy — thử thêm text kế tiếp, đo `Uri.EscapeDataString(string.Join('\n', tentative)).Length`; vượt 1800 và chunk hiện tại không rỗng → chốt chunk, mở chunk mới (text đơn vượt hạn mức → chunk riêng). `ParseBatch` (thuật toán không suy ra từ chữ ký — bám đúng khối này):
  ```csharp
  // segments = root[0]; mỗi seg phải là array ≥ 2 phần tử, seg[1] là string bản gốc.
  // Duyệt seg: bỏ qua array rỗng (như Parse hiện tại); idx >= texts.Count -> FormatException.
  //   accOrig += seg[1]; accTrans += seg[0] (string, thiếu -> "")
  //   nếu TrimTrailingNewline(accOrig) == texts[idx] -> results.Add(TrimTrailingNewline(accTrans)); reset acc; idx++
  // kết thúc: idx == texts.Count && accOrig.Length == 0, ngược lại FormatException.
  static string TrimTrailingNewline(string s) => s.EndsWith('\n') ? s[..^1] : s;
  ```
- [ ] **Step 4: Chạy test — pass** (cả 5 test cũ của class không sửa assert).
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.Core/Translate/GoogleWebTranslator.cs" "ReTran.Tests/GoogleWebTranslatorTests.cs"
  git commit -m "feat(core): batch translation over one Google request per chunk of texts"
  ```

---

### Task 3: `TranslateCycleSink` — batch qua gate + fallback per-text

**Files:**
- Modify: `ReTran.Core/Serve/TranslateCycleSink.cs` (`BackgroundAsync` :304-320; thêm `TranslateBatchGuardedAsync`; cập nhật remark :257-263)
- Modify: `ReTran.Tests/TranslateCycleSinkTests.cs` (mở rộng `StubTranslator` + 4 test)

**Interfaces:**
- Consumes: `ITranslator.TranslateBatchAsync` (Task 1/2).
- Produces: private `Task<IReadOnlyList<string>> TranslateBatchGuardedAsync(List<string> texts, CancellationToken ct)` — gate 4 slot giữ nguyên nhưng **theo lô**; kiểm `translated.Count == texts.Count` (lệch → `FormatException`); thành công thì ghi cả lô vào `_session` dưới lock (thay cho việc `TranslateOneAsync` tự ghi từng text).

- [ ] **Step 1: Viết test failing** — `StubTranslator` thêm: `List<IReadOnlyList<string>> BatchCalls`, `bool ThrowFormatOnBatch`, `bool ThrowHttpOnBatch`, `bool WrongResultCount`; override `TranslateBatchAsync`: record `BatchCalls`; throw theo cờ; nếu không throw thì loop per-text ghi `CallTexts` (giữ nguyên các assert cũ) và trả `Result` (WrongResultCount → bỏ 1 phần tử). Test mới:
  - `Batch_OneCallForManyMisses`: misses `{A,B}` → `BatchCalls.Count == 1`, `BatchCalls[0] == [A,B]`; `Puts` đúng 1 lô 2 entry.
  - `BatchMappingFailure_FallsBackPerText`: `ThrowFormatOnBatch` → `BatchCalls.Count == 1`; `CallTexts == [A,B]` (fallback per-text chạy); `Puts` có 2 entry.
  - `BatchHttpFailure_NoFallback_NoPut`: `ThrowHttpOnBatch` → `CallTexts` rỗng; `Puts` rỗng; stderr chứa "warn".
  - `Batch_WrongResultCount_FallsBack`: `WrongResultCount` → `CallTexts == [A,B]`, `Puts` có entry.
  - Không sửa 11 test hiện có (chúng đi qua đường batch của stub nhưng assert giữ nguyên — xem Step 4).
- [ ] **Step 2: Chạy test — xác nhận fail** (`TranslateCycleSinkTests`).
- [ ] **Step 3: Implement** — thay khối dịch trong `BackgroundAsync`:
  ```csharp
  if (pending.Count > 0)
  {
      List<(string Text, string Translated)> entries;
      try
      {
          IReadOnlyList<string> translated = await TranslateBatchGuardedAsync(pending, ct);
          entries = ... zip pending[i] + translated[i] ...;
      }
      catch (FormatException ex)
      {
          _stderr.WriteLine($"warn: batch translate failed ({ex.Message}); falling back to per-text");
          var results = await Task.WhenAll(pending.Select(t => TranslateOneAsync(t, ct)));
          entries = results.Where(r => r is not null).Select(r => r!.Value).ToList();
      }
      if (entries.Count > 0) await _cache.PutAsync(_context, _src, _dst, entries, ct);
  }
  ```
  `TranslateBatchGuardedAsync`: `await _translateGate.WaitAsync(ct)` → try: gọi `_translator.TranslateBatchAsync(texts, _src, _dst, ct)`; lệch count → `throw new FormatException(...)`; ghi `_session` cả lô dưới `lock (_sync)`; finally release. `TranslateOneAsync` giữ nguyên (đường fallback). HTTP error từ batch → rơi vào catch `(Exception)` sẵn có của `BackgroundAsync` ("translate background failed"), không fallback (D6).
- [ ] **Step 4: Chạy test — pass** + full suite. Ghi chú hành vi giữ nguyên dù đường đi đổi: `TranslateFailure_NoPut_NoThrow` giờ là batch-ném-HTTP (assert cũ vẫn đúng); `SlowTranslate_DoesNotBlockCycle` treo trong batch loop của stub; `RepeatTexts_Deduped…` chỉ 1 lần gọi.
- [ ] **Step 5: Commit**
  ```bash
  git add "ReTran.Core/Serve/TranslateCycleSink.cs" "ReTran.Tests/TranslateCycleSinkTests.cs"
  git commit -m "feat(core): translate one batch per cycle with per-text fallback on mapping failure"
  ```

---

### Task 4: Verification cuối + ghi perf

**Files:**
- Modify: `Docs/performance.md` (mục ngắn "M4-batch")
- No code.

- [ ] **Step 1: Build + test full** — `dotnet build "ReTran Project.slnx"` (0 error); `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` ⇒ toàn bộ PASS, ≥ 486 + ~11 test mới.
- [ ] **Step 2: Smoke có tương tác — DEFERRED TO CONTROLLER** (subagent không thấy desktop):
  1. Start pipeline trên cửa sổ nhiều text mới (menu game) → bản dịch vẫn hiện đủ từng box sau 1–2 chu kỳ.
  2. Text cố định (cache hit) → không phát sinh request; text mới đổi liên tục → vẫn mượt, không log "falling back to per-text" liên tục.
  3. Ngắt mạng → stderr có "warn: translate background failed"; bật lại mạng → chu kỳ sau (text mới) dịch lại bình thường (text cũ giữ null tới hết phiên — đúng M4).
- [ ] **Step 3: Ghi `Docs/performance.md`** — mục ngắn: số request N → ⌈N/K⌉, hằng số 1800 ký tự URL (đo 12 dòng = 1.474 ký tự), đường lẻ/fallback giữ nguyên; stdio/SHM không đổi.
- [ ] **Step 4: Commit**
  ```bash
  git add "Docs/performance.md"
  git commit -m "docs(perf): batched translation request count"
  ```

---

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** D1 → Task 1; D2/D3/D4/D5 → Task 2; D6/D7 → Task 3; D8 + hiệu năng + verification → Task 4. §4 thuật toán → Task 2 Step 3. Không có gap.
2. **Step scan:** mỗi step một hành động check được; code block chỉ ở chỗ thuật toán/chữ ký không suy ra được (ParseBatch walk, Chunk greedy, guard count, handler echo).
3. **Type consistency:** `TranslateBatchAsync(IReadOnlyList<string>, string, string, CancellationToken)` dùng nhất quán Task 1/2/3; `MaxBatchUrlLength` ở Task 2 (test đọc); `BatchCalls/ThrowFormatOnBatch/ThrowHttpOnBatch/WrongResultCount` chỉ ở Task 3.
4. **Review Focus:** 5 dòng, test ghim vào Task 2 (`Batch_OriginalMismatch…`, `Batch_TextWithNewline…`, `Batch_Chunks…`) và Task 3 (`BatchMappingFailure…`, `BatchHttpFailure…`, `Batch_WrongResultCount…`) — mỗi dòng có test sở hữu.
5. **Proportion:** plan ngắn hơn spec; các body dài được thay bằng mô tả thuật toán ngắn + assert chính xác.
