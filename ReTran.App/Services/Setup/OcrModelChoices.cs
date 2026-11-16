namespace ReTran_App.Services.Setup;

/// <summary>
/// Preselection of the tier rows computed from what is installed on disk and the detection model the
/// settings currently point at.
/// Kết quả tick-sẵn cho các dòng tier, tính từ model đã cài trên đĩa và model det trong settings.
/// </summary>
/// <param name="Ticked">Tier names to render ticked, in tiny → small → medium order.
/// Tên các tier cần tick sẵn, theo thứ tự tiny → small → medium.</param>
/// <param name="Locked">Subset of <paramref name="Ticked"/> whose checkbox must be disabled (fully
/// installed — unticking would suggest the model is removed). Tập con của <paramref name="Ticked"/> bị
/// khoá checkbox (đã cài đủ — bỏ tick dễ hiểu nhầm thành xoá model).</param>
/// <param name="ActiveTier">Tier whose model pair the wizard will run (the "Dùng chính" radio).
/// Tier sẽ chạy cặp model của nó (radio "Dùng chính").</param>
public sealed record TierPreselect(IReadOnlyList<string> Ticked, IReadOnlyList<string> Locked, string ActiveTier);

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
/// Pure rules behind the "Chọn model OCR" and "Ngôn ngữ bổ sung" wizard screens: tier model names,
/// the legacy-recognition routing mirror of the Python catalog, v6 coverage, preselection from the
/// installed models, the needed-download set, and the pair to apply. A display mirror only — mirrors
/// <see cref="OcrCatalogDisplay"/> (drift-guarded against ReTran.OCR/src/retran_core/ocr/catalog.py);
/// the wizard no longer round-trips <c>ocr.models.recommend</c>.
/// Luật thuần sau 2 màn wizard "Chọn model OCR" và "Ngôn ngữ bổ sung": tên model theo tier, mirror
/// routing rec legacy của catalog Python, phủ v6, preselect theo model đã cài, tập cần tải, và cặp sẽ
/// áp dụng. Chỉ là mirror hiển thị — sao từ <see cref="OcrCatalogDisplay"/> (đã được test drift ghim
/// với ReTran.OCR/src/retran_core/ocr/catalog.py); wizard không còn gọi <c>ocr.models.recommend</c>.
/// </summary>
public static class OcrModelChoices
{
    /// <summary>
    /// Tier names in display order, matching the PP-OCRv6 model family.
    /// Tên các tier theo thứ tự hiển thị, khớp họ model PP-OCRv6.
    /// </summary>
    public static IReadOnlyList<string> Tiers { get; } = ["tiny", "small", "medium"];

    /// <summary>
    /// Language codes covered only by legacy recognition models, in display order for the
    /// "Ngôn ngữ bổ sung" screen. Mirrors the catalog's routing outcome: every listed code resolves
    /// through <see cref="LegacyRecFor"/> to a live rec model (the dead catalog entries chinese_cht
    /// and eslav are never selected by routing).
    /// Mã ngôn ngữ chỉ có rec legacy phủ, theo thứ tự hiển thị màn "Ngôn ngữ bổ sung". Mirror kết quả
    /// routing của catalog: mỗi mã đều giải ra rec sống qua <see cref="LegacyRecFor"/> (2 mục "chết"
    /// chinese_cht và eslav không bao giờ được routing chọn).
    /// </summary>
    public static IReadOnlyList<string> LegacyLanguageCodes { get; } = ["ko", "ru", "ar", "th", "ta", "te", "hi", "el"];

    /// <summary>
    /// Name of the v6 detection model of a tier (e.g. <c>small</c> → <c>PP-OCRv6_small_det</c>).
    /// Tên model det v6 của một tier (ví dụ <c>small</c> → <c>PP-OCRv6_small_det</c>).
    /// </summary>
    public static string TierDet(string tier) => $"PP-OCRv6_{tier}_det";

    /// <summary>
    /// Name of the v6 recognition model of a tier (e.g. <c>medium</c> → <c>PP-OCRv6_medium_rec</c>).
    /// Tên model rec v6 của một tier (ví dụ <c>medium</c> → <c>PP-OCRv6_medium_rec</c>).
    /// </summary>
    public static string TierRec(string tier) => $"PP-OCRv6_{tier}_rec";

    /// <summary>
    /// Tier a detection-model name belongs to (<c>PP-OCRv6_medium_det</c> → <c>medium</c>), or
    /// <see langword="null"/> when it is not one of the tier detectors.
    /// Tier của một tên model det (<c>PP-OCRv6_medium_det</c> → <c>medium</c>), hoặc
    /// <see langword="null"/> khi không phải một trong ba model det của tier.
    /// </summary>
    public static string? TierOfDet(string? detModel)
    {
        if (detModel is null) return null;
        foreach (string tier in Tiers)
        {
            if (detModel == TierDet(tier)) return tier;
        }
        return null;
    }

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
    /// Tick state for the three tier rows: fully installed tiers are ticked and locked, partially
    /// installed ones are ticked and free, and a machine with nothing installed starts at small. The
    /// active tier follows <paramref name="currentDet"/> when its tier is ticked, then small when
    /// ticked, then the first ticked row.
    /// Trạng thái tick cho 3 dòng tier: tier đã cài đủ → tick + khoá, bán phần → tick tự do, máy sạch
    /// mặc định small. Tier "dùng chính" theo <paramref name="currentDet"/> nếu tier đó nằm trong các
    /// dòng tick, rồi tới small nếu được tick, rồi tới dòng tick đầu tiên.
    /// </summary>
    /// <param name="installedModels">Model directory names present under <c>official_models</c>.
    /// Tên các thư mục model có trong <c>official_models</c>.</param>
    /// <param name="currentDet">Detection model the settings point at, if any.
    /// Model det trong settings, nếu có.</param>
    public static TierPreselect PreselectTiers(IReadOnlyCollection<string> installedModels, string? currentDet)
    {
        var installed = new HashSet<string>(installedModels, StringComparer.Ordinal);
        var ticked = new List<string>();
        var locked = new List<string>();
        foreach (string tier in Tiers)
        {
            bool det = installed.Contains(TierDet(tier));
            bool rec = installed.Contains(TierRec(tier));
            if (det && rec)
            {
                ticked.Add(tier);
                locked.Add(tier);
            }
            else if (det || rec)
            {
                ticked.Add(tier);
            }
        }

        if (ticked.Count == 0) ticked.Add("small");

        string? fromSettings = TierOfDet(currentDet);
        string active = fromSettings is not null && ticked.Contains(fromSettings) ? fromSettings
            : ticked.Contains("small") ? "small"
            : ticked[0];
        return new TierPreselect(ticked, locked, active);
    }

    /// <summary>
    /// Language codes whose legacy rec is already installed, in display order — the rows the
    /// "Ngôn ngữ bổ sung" screen starts ticked and locked with.
    /// Mã ngôn ngữ đã có rec legacy trên đĩa, theo thứ tự hiển thị — các dòng màn "Ngôn ngữ bổ sung"
    /// được tick sẵn và khoá.
    /// </summary>
    /// <param name="installedModels">Model directory names present under <c>official_models</c>.
    /// Tên các thư mục model có trong <c>official_models</c>.</param>
    public static IReadOnlyList<string> PreselectLegacyLanguages(IReadOnlyCollection<string> installedModels)
    {
        var installed = new HashSet<string>(installedModels, StringComparer.Ordinal);
        var picks = new List<string>();
        foreach (string lang in LegacyLanguageCodes)
        {
            string? rec = LegacyRecFor(lang);
            if (rec is not null && installed.Contains(rec)) picks.Add(lang);
        }
        return picks;
    }

    /// <summary>
    /// Distinct model names the download step must ensure, in download order: det + rec of every
    /// ticked tier, then the active tier's pair when the answers lack it (defensive — the running
    /// pair must never be missing), then the main language's legacy rec when v6 does not cover it,
    /// then each extra language's legacy rec.
    /// Các tên model không trùng mà bước tải phải đảm bảo, theo thứ tự tải: det + rec của mọi tier
    /// được tick, rồi cặp của tier đang chạy khi answers thiếu nó (phòng thủ — cặp đang chạy không
    /// bao giờ được thiếu), rồi rec legacy của ngôn ngữ chính khi ngoài phủ v6, rồi rec legacy của
    /// từng ngôn ngữ bổ sung.
    /// </summary>
    /// <param name="tiers">Ticked tier names. Tên các tier đã tick.</param>
    /// <param name="activeTier">Tier chosen as "Dùng chính". Tier được chọn "Dùng chính".</param>
    /// <param name="mainLang">Main language code or <c>auto</c>. Mã ngôn ngữ chính hoặc <c>auto</c>.</param>
    /// <param name="extraLangs">Extra language codes from the supplementary screen. Mã các ngôn ngữ bổ sung.</param>
    public static IReadOnlyList<string> NeededModels(
        IReadOnlyList<string> tiers, string activeTier, string mainLang, IReadOnlyList<string> extraLangs)
    {
        var needed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (seen.Add(name)) needed.Add(name);
        }

        foreach (string tier in tiers)
        {
            Add(TierDet(tier));
            Add(TierRec(tier));
        }

        if (!tiers.Contains(activeTier, StringComparer.Ordinal))
        {
            Add(TierDet(activeTier));
            Add(TierRec(activeTier));
        }

        if (!IsV6Covered(mainLang) && LegacyRecFor(mainLang) is { } mainRec) Add(mainRec);
        foreach (string extra in extraLangs)
        {
            if (LegacyRecFor(extra) is { } rec) Add(rec);
        }
        return needed;
    }

    /// <summary>
    /// The det/rec pair the applying step writes for the given answers: the active tier's det, and
    /// the active tier's rec when v6 covers the main language — otherwise the main language's legacy
    /// rec (falling back to the tier rec when no legacy model covers it).
    /// Cặp det/rec bước áp dụng ghi cho answers đã cho: det của tier đang chạy, và rec của tier đó khi
    /// ngôn ngữ chính được v6 phủ — ngược lại là rec legacy của ngôn ngữ chính (nếu không có rec legacy
    /// nào phủ thì lùi về rec của tier).
    /// </summary>
    /// <param name="activeTier">Tier chosen as "Dùng chính". Tier được chọn "Dùng chính".</param>
    /// <param name="mainLang">Main language code or <c>auto</c>. Mã ngôn ngữ chính hoặc <c>auto</c>.</param>
    public static (string Det, string Rec) ExpectedPair(string activeTier, string mainLang)
    {
        string rec = IsV6Covered(mainLang)
            ? TierRec(activeTier)
            : LegacyRecFor(mainLang) ?? TierRec(activeTier);
        return (TierDet(activeTier), rec);
    }

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
    /// The variant shown for a language row: the installed one when any, otherwise the first.
    /// Bản hiển thị cho một dòng ngôn ngữ: bản đã cài nếu có, ngược lại bản đầu.
    /// </summary>
    public static string DefaultVariant(LanguageChoice choice, IReadOnlyCollection<string> installed) =>
        choice.Variants.FirstOrDefault(installed.Contains) ?? choice.Variants[0];

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
