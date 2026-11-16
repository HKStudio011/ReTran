namespace ReTran_App.Services.Setup;

/// <summary>
/// One language row of step 3: the primary language and the legacy rec variants serving it.
/// Một dòng ngôn ngữ của bước 3: ngôn ngữ chính và các bản rec legacy phục vụ nó.
/// </summary>
/// <param name="PrimaryLang">First non-<c>en</c> language of the entry, e.g. <c>ko</c>. Ngôn ngữ đầu khác <c>en</c> của entry.</param>
/// <param name="Variants">Legacy rec model names kept after redundant-coverage filtering. Tên rec legacy còn lại sau lọc phủ-trùng.</param>
public sealed record LanguageChoice(string PrimaryLang, IReadOnlyList<string> Variants);

/// <summary>
/// Preselection of one flat checkbox list: ticked names, the subset locked (installed), and the
/// active pick.
/// Kết quả tick-sẵn của một danh sách phẳng: tên đã tick, tập con bị khoá (đã cài), và lựa chọn chạy chính.
/// </summary>
/// <param name="Ticked">Names to render ticked, in candidate order. Tên cần tick sẵn, theo thứ tự danh sách.</param>
/// <param name="Locked">Subset of <paramref name="Ticked"/> whose checkbox must be disabled. Tập con của <paramref name="Ticked"/> bị khoá checkbox.</param>
/// <param name="Active">The active pick (always one of <paramref name="Ticked"/> when non-empty). Lựa chọn chạy chính (luôn nằm trong <paramref name="Ticked"/> khi danh sách có phần tử).</param>
public sealed record ModelPreselect(IReadOnlyList<string> Ticked, IReadOnlyList<string> Locked, string Active);

/// <summary>
/// Pure rules behind the wizard's model screens (spec 17-11): derive the det/rec, legacy-language
/// and LLM lists from the OCR catalog mirror, v6 coverage, preselection from the installed models,
/// the needed-download set, and the pair to apply. A display mirror only — mirrors
/// <see cref="OcrCatalogDisplay"/> (drift-guarded against ReTran.OCR/src/retran_core/ocr/catalog.py);
/// the wizard no longer round-trips <c>ocr.models.recommend</c>.
/// Luật thuần sau các màn model của wizard (spec 17-11): suy danh sách det/rec, ngôn ngữ legacy và
/// LLM từ mirror catalog OCR, phủ v6, preselect theo model đã cài, tập cần tải, và cặp sẽ áp dụng.
/// Chỉ là mirror hiển thị — sao từ <see cref="OcrCatalogDisplay"/> (đã được test drift ghim với
/// ReTran.OCR/src/retran_core/ocr/catalog.py); wizard không còn gọi <c>ocr.models.recommend</c>.
/// </summary>
public static class OcrModelChoices
{
    /// <summary>
    /// First legacy (non-v6) recognition model whose catalog languages include
    /// <paramref name="lang"/>, mirroring the catalog's routing order; <see langword="null"/> when no
    /// legacy model covers it. Callers reach for this only after <see cref="IsV6Covered"/> said the
    /// language is outside v6.
    /// Rec legacy (không phải v6) đầu tiên phủ <paramref name="lang"/> theo thứ tự routing của catalog;
    /// <see langword="null"/> khi không rec nào phủ. Chỉ nên gọi sau khi <see cref="IsV6Covered"/> nói
    /// ngôn ngữ ngoài phủ v6.
    /// </summary>
    public static string? LegacyRecFor(string lang)
    {
        string code = lang.Trim().ToLowerInvariant();
        OcrModelInfo? legacy = OcrCatalogDisplay.Models.FirstOrDefault(m =>
            m.Kind == "rec"
            && !m.Name.StartsWith("PP-OCRv6", StringComparison.Ordinal)
            && m.Langs.Contains(code));
        return legacy?.Name;
    }

    /// <summary>
    /// Whether the v6 recognition family covers the language: <c>auto</c> and the languages of the
    /// default rec do; legacy-only languages and unknown codes do not. Matching is case-insensitive.
    /// Ngôn ngữ có được họ rec v6 phủ không: <c>auto</c> và các ngôn ngữ của rec mặc định thì có; ngôn
    /// ngữ chỉ có rec legacy và mã lạ thì không. So khớp không phân biệt hoa thường.
    /// </summary>
    public static bool IsV6Covered(string langOrAuto)
    {
        if (string.Equals(langOrAuto, "auto", StringComparison.OrdinalIgnoreCase)) return true;
        string code = langOrAuto.Trim().ToLowerInvariant();
        OcrModelInfo? v6 = OcrCatalogDisplay.Models.FirstOrDefault(m => m.Name == OcrCatalogDisplay.DefaultRec);
        return v6?.Langs.Contains(code) ?? false;
    }

    /// <summary>
    /// Published size of a catalog model in megabytes; 0 for a name outside the catalog.
    /// Dung lượng công bố của một model trong catalog (MB); 0 với tên ngoài catalog.
    /// </summary>
    public static float SizeMbOf(string modelName) =>
        OcrCatalogDisplay.Models.FirstOrDefault(m => m.Name == modelName)?.SizeMb ?? 0f;

    /// <summary>
    /// Model directory names present under <c>&lt;modelsDir&gt;/official_models</c> (a directory per
    /// installed model, matching the sidecar's cache layout). A missing or unreadable directory reads
    /// as "nothing installed" — the RPC-backed download step remains the safety net.
    /// Tên các thư mục model có trong <c>&lt;modelsDir&gt;/official_models</c> (mỗi model đã cài là một
    /// thư mục, khớp layout cache của sidecar). Thư mục thiếu hoặc không đọc được coi như "chưa cài gì"
    /// — bước tải qua RPC vẫn là chốt an toàn.
    /// </summary>
    /// <param name="modelsDir">Model cache root (contains <c>official_models</c>). Thư mục gốc cache model (chứa <c>official_models</c>).</param>
    public static HashSet<string> ScanInstalledModels(string modelsDir)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string official = Path.Combine(modelsDir, SetupState.OfficialModelsSubdir);
            if (!Directory.Exists(official)) return found;
            foreach (string dir in Directory.EnumerateDirectories(official))
            {
                found.Add(Path.GetFileName(dir));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Đọc đĩa lỗi → coi như chưa cài gì; bước tải (RPC) vẫn là chốt an toàn.
        }
        return found;
    }

    // ── v2 (2026-11-17): derive lựa chọn model từ catalog — không hardcode cỡ/ngôn ngữ ─────────────

    /// <summary>
    /// All det model names in catalog order (kind <c>det</c>); the whole det family is v6 today.
    /// Mọi tên model det theo thứ tự catalog (kind <c>det</c>); hôm nay cả họ det là v6.
    /// </summary>
    public static IReadOnlyList<string> DetModels(IReadOnlyList<OcrModelInfo>? catalog = null) =>
        [.. (catalog ?? OcrCatalogDisplay.Models).Where(m => m.Kind == "det").Select(m => m.Name)];

    /// <summary>
    /// All v6 rec model names in catalog order (kind <c>rec</c>, <c>PP-OCRv6</c> family).
    /// Mọi tên model rec họ v6 theo thứ tự catalog (kind <c>rec</c>, họ <c>PP-OCRv6</c>).
    /// </summary>
    public static IReadOnlyList<string> RecModels(IReadOnlyList<OcrModelInfo>? catalog = null) =>
        [.. (catalog ?? OcrCatalogDisplay.Models)
            .Where(m => m.Kind == "rec" && m.Name.StartsWith("PP-OCRv6", StringComparison.Ordinal))
            .Select(m => m.Name)];

    /// <summary>
    /// Size token of a model name (<c>PP-OCRv6_medium_det</c> → <c>medium</c>); an unrecognized name
    /// is returned unchanged so the UI can show it as-is.
    /// Token cỡ của tên model (<c>PP-OCRv6_medium_det</c> → <c>medium</c>); tên không nhận dạng được
    /// trả về nguyên trạng để UI hiển thị thẳng.
    /// </summary>
    public static string SizeToken(string modelName)
    {
        const string prefix = "PP-OCRv6_";
        if (!modelName.StartsWith(prefix, StringComparison.Ordinal)) return modelName;
        int end = modelName.LastIndexOf('_');
        return end > prefix.Length ? modelName[prefix.Length..end] : modelName;
    }

    /// <summary>
    /// Distinct size tokens across the v6 entries, in catalog order — a new tier (e.g. <c>big</c>) in
    /// the catalog appears here without any code change.
    /// Các token cỡ khác nhau của họ v6, theo thứ tự catalog — cỡ mới (vd <c>big</c>) trong catalog tự
    /// xuất hiện, không cần sửa code.
    /// </summary>
    public static IReadOnlyList<string> SizeTokens(IReadOnlyList<OcrModelInfo>? catalog = null)
    {
        var tokens = new List<string>();
        foreach (OcrModelInfo m in (catalog ?? OcrCatalogDisplay.Models)
            .Where(m => m.Name.StartsWith("PP-OCRv6", StringComparison.Ordinal)))
        {
            string token = SizeToken(m.Name);
            if (!tokens.Contains(token, StringComparer.Ordinal)) tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>
    /// Step-3 language rows derived from the legacy recs: groups by primary language (first non-<c>en</c>
    /// code; <c>en</c>-only entries dropped), keeps only primaries outside v6 coverage, and inside each
    /// group drops entries whose language set is a strict subset of another's (redundant coverage --
    /// e.g. eslav ⊂ cyrillic). The remaining entries per group are the selectable "bản" variants.
    /// Các dòng ngôn ngữ bước 3 suy từ rec legacy: gom theo ngôn ngữ chính (mã đầu khác <c>en</c>;
    /// entry chỉ có <c>en</c> bị bỏ), chỉ giữ ngôn ngữ chính ngoài phủ v6, và trong nhóm bỏ entry có
    /// tập ngôn ngữ là tập con thật của entry khác (phủ trùng — vd eslav ⊂ cyrillic). Các entry còn lại
    /// của nhóm chính là các "bản" chọn được.
    /// </summary>
    public static IReadOnlyList<LanguageChoice> LanguageChoices(IReadOnlyList<OcrModelInfo>? catalog = null)
    {
        var groups = new Dictionary<string, List<OcrModelInfo>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (OcrModelInfo m in (catalog ?? OcrCatalogDisplay.Models)
            .Where(m => m.Kind == "rec" && !m.Name.StartsWith("PP-OCRv6", StringComparison.Ordinal)))
        {
            string? primary = m.Langs.FirstOrDefault(l => l != "en");
            if (primary is null || IsV6Covered(primary)) continue;
            if (!groups.TryGetValue(primary, out List<OcrModelInfo>? list))
            {
                list = [];
                groups[primary] = list;
                order.Add(primary);
            }
            list.Add(m);
        }

        var choices = new List<LanguageChoice>();
        foreach (string primary in order)
        {
            List<OcrModelInfo> members = groups[primary];
            var kept = members
                .Where((m, i) => !members.Where((o, j) => j != i)
                    .Any(o => IsStrictSubsetOf(m.Langs, o.Langs)))
                .Select(m => m.Name)
                .ToList();
            choices.Add(new LanguageChoice(primary, kept));
        }
        return choices;
    }

    /// <summary>
    /// The variant shown for a language row: the installed one when any, otherwise the first; an
    /// empty string when the choice carries no variants (defensive — catalog-derived rows always
    /// have at least one).
    /// Bản hiển thị cho một dòng ngôn ngữ: bản đã cài nếu có, ngược lại bản đầu; chuỗi rỗng khi
    /// choice không có bản nào (phòng thủ — dòng suy từ catalog luôn có ít nhất một).
    /// </summary>
    public static string DefaultVariant(LanguageChoice choice, IReadOnlyCollection<string> installed) =>
        choice.Variants.FirstOrDefault(installed.Contains) ?? choice.Variants.FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Preselection for one OCR block (det or rec): installed candidates are ticked and locked, a
    /// clean machine starts at <c>small</c> (free) because the block requires at least one pick, and
    /// the active pick follows <paramref name="current"/>, then <c>small</c>, then the first ticked.
    /// Kết quả tick-sẵn cho một khối OCR (det hoặc rec): ứng viên đã cài → tick + khoá; máy sạch mặc
    /// định <c>small</c> (tự do) vì khối cần ít nhất 1; lựa chọn chạy theo <paramref name="current"/>,
    /// rồi <c>small</c>, rồi dòng tick đầu.
    /// </summary>
    public static ModelPreselect PreselectModels(
        IReadOnlyCollection<string> installed, string? current, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return new ModelPreselect([], [], string.Empty);

        var ticked = candidates.Where(installed.Contains).ToList();
        var locked = new List<string>(ticked);
        if (ticked.Count == 0)
        {
            ticked.Add(candidates.FirstOrDefault(c => SizeToken(c) == "small") ?? candidates[0]);
            locked.Clear();
        }

        string active = current is not null && ticked.Contains(current, StringComparer.Ordinal) ? current
            : ticked.FirstOrDefault(c => SizeToken(c) == "small") ?? ticked[0];
        return new ModelPreselect(ticked, locked, active);
    }

    /// <summary>
    /// Preselection for the step-4 LLM list: installed candidates are ticked and locked; nothing is
    /// ticked on a clean machine (the whole screen is optional), and the active pick is the current
    /// model only when it is ticked.
    /// Kết quả tick-sẵn cho danh sách LLM bước 4: ứng viên đã cài → tick + khoá; máy sạch không tick
    /// gì (cả màn là tuỳ chọn); lựa chọn chạy chỉ là model hiện tại khi nó đang được tick.
    /// </summary>
    public static (IReadOnlyList<string> Ticked, IReadOnlyList<string> Locked, string? Active) PreselectLlmModels(
        IReadOnlyCollection<string> installed, string? current, IReadOnlyList<string> candidates)
    {
        var ticked = candidates.Where(installed.Contains).ToList();
        var locked = new List<string>(ticked);
        string? active = current is not null && ticked.Contains(current, StringComparer.Ordinal) ? current : null;
        return (ticked, locked, active);
    }

    /// <summary>
    /// Preselection for step 3: rows whose default variant is installed are ticked and locked, and
    /// the active "Đọc chính" is the settings rec when it is one of the ticked variants (otherwise
    /// <see langword="null"/> — including the fully-empty answer).
    /// Kết quả tick-sẵn cho bước 3: dòng có bản mặc định đã cài → tick + khoá; "Đọc chính" là rec
    /// trong settings khi rec đó nằm trong các bản đã tick (ngược lại <see langword="null"/> — kể cả
    /// trường hợp trả lời trống hoàn toàn).
    /// </summary>
    public static (IReadOnlyList<string> Ticked, IReadOnlyList<string> Locked, string? Active) PreselectLanguages(
        IReadOnlyCollection<string> installed, string? currentRec, IReadOnlyList<OcrModelInfo>? catalog = null)
    {
        var ticked = new List<string>();
        foreach (LanguageChoice choice in LanguageChoices(catalog))
        {
            string variant = DefaultVariant(choice, installed);
            if (installed.Contains(variant)) ticked.Add(variant);
        }

        string? active = currentRec is not null && ticked.Contains(currentRec, StringComparer.Ordinal)
            ? currentRec
            : null;
        return (ticked, [.. ticked], active);
    }

    /// <summary>
    /// The distinct model names the download step must ensure: every ticked det, every ticked rec,
    /// and the legacy rec of every ticked language row, in that order.
    /// Các tên model không trùng mà bước tải phải đảm bảo: mọi det đã tick, mọi rec đã tick, và rec
    /// legacy của từng dòng ngôn ngữ đã tick, theo thứ tự đó.
    /// </summary>
    public static IReadOnlyList<string> NeededModels(
        IReadOnlyList<string> dets, IReadOnlyList<string> recs, IReadOnlyList<string> langs)
    {
        var needed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (seen.Add(name)) needed.Add(name);
        }

        foreach (string d in dets) Add(d);
        foreach (string r in recs) Add(r);
        foreach (string l in langs) Add(l);
        return needed;
    }

    /// <summary>
    /// The pair the applying step writes: the active det, and the active legacy rec when one is
    /// marked "Đọc chính", otherwise the active v6 rec.
    /// Cặp bước áp dụng ghi: det đang chạy, và rec legacy đang được chấm "Đọc chính" nếu có, ngược lại
    /// rec v6 đang chạy.
    /// </summary>
    public static (string Det, string Rec) ExpectedPair(string activeDet, string? activeLangRec, string activeRec) =>
        (activeDet, activeLangRec ?? activeRec);

    private static bool IsStrictSubsetOf(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count < b.Count && a.All(b.Contains);
}
