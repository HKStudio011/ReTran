using System.Diagnostics;
using System.Text.Json.Nodes;
using RetranCore.Tests;
using ReTran_App.Services.Setup;

/// <summary>
/// Drift test for <see cref="OcrCatalogDisplay"/>: runs the REAL Python catalog
/// (ReTran.OCR/src/retran_core/ocr/catalog.py — the source of truth) through the repo's venv python
/// and asserts field-by-field equality (name, kind, langs in order, size_mb) for every entry plus the
/// default det/rec pair. The catalog module is pure data (no paddle import, no downloads), so it runs
/// directly via <c>python -c</c> — never through <c>uv run</c>, which could re-resolve and modify the
/// shared dev venv. The repo's src dir is prepended to sys.path so the import deterministically comes
/// from THIS checkout, whatever the venv's editable pointer currently targets.
/// Test drift cho <see cref="OcrCatalogDisplay"/>: chạy catalog Python THẬT (nguồn sự thật) qua python
/// của venv repo và so khớp từng trường (name, kind, langs theo thứ tự, size_mb) cho mọi mục kèm cặp
/// det/rec mặc định. Module catalog là dữ liệu thuần (không import paddle, không tải gì), nên chạy
/// thẳng <c>python -c</c> — KHÔNG qua <c>uv run</c> vì nó có thể re-resolve và sửa venv dev dùng chung.
/// Thêm src của repo vào đầu sys.path để import chắc chắn đến từ checkout NÀY, bất kể pointer editable
/// của venv đang trỏ đâu.
/// </summary>
public class OcrCatalogDisplayTests
{
    /// <summary>
    /// The Python snippet: prints the whole catalog as JSON — one array per entry
    /// [name, kind, langs[], size_mb], in CATALOG order, plus the two default model names.
    /// Snippet Python: in toàn bộ catalog dạng JSON — mỗi mục một mảng
    /// [name, kind, langs[], size_mb] theo đúng thứ tự CATALOG, kèm hai tên model mặc định.
    /// </summary>
    private const string CatalogJsonScript = """
        import json, sys
        sys.path.insert(0, sys.argv[1])
        from retran_core.ocr.catalog import CATALOG, _DEFAULT_DET, _DEFAULT_REC
        print(json.dumps({
            "models": [[m.name, m.kind, list(m.langs), m.size_mb] for m in CATALOG],
            "default_det": _DEFAULT_DET,
            "default_rec": _DEFAULT_REC,
        }))
        """;

    [Fact]
    public void DisplayCatalog_MatchesPythonCatalog()
    {
        // Infra gate (Task 11e pattern): the venv layout is Windows-only and must be resolvable from
        // AppContext.BaseDirectory — otherwise report a skip naming the exact missing condition
        // (never a bare return, which xUnit counts as passed with zero coverage).
        // Gate hạ tầng (pattern Task 11e): layout venv chỉ có trên Windows và phải tìm được từ
        // AppContext.BaseDirectory — nếu không, báo skip nêu rõ điều kiện thiếu (không dùng return
        // trần — xUnit đếm là passed với 0 coverage).
        if (!OperatingSystem.IsWindows())
            Assert.Skip("catalog drift test is Windows-only (venv layout Scripts/python.exe)");
        if (!OcrTestSupport.TryResolveRepoWithVenv(out string repoRoot, out string pythonExe))
            Assert.Skip("no ancestor of AppContext.BaseDirectory has both ReTran.OCR/pyproject.toml and ReTran.OCR/.venv/Scripts/python.exe (repo layout or uv venv missing)");

        JsonNode root = RunCatalogJson(repoRoot, pythonExe);

        // xUnit v3's Assert.Equal has no message overload, so each check is an Assert.True with the
        // expected/actual values in the failure message — a drift names the exact entry and field.
        // xUnit v3 không có Assert.Equal kèm message, nên mỗi kiểm tra là Assert.True với giá trị
        // mong đợi/thực tế trong message lỗi — lệch sẽ chỉ ra đúng mục và trường.
        var pyModels = root["models"]!.AsArray();
        IReadOnlyList<OcrModelInfo> display = OcrCatalogDisplay.Models;
        Assert.True(display.Count == pyModels.Count,
            $"entry count: python CATALOG has {pyModels.Count}, C# mirror has {display.Count}");

        for (int i = 0; i < pyModels.Count; i++)
        {
            var e = pyModels[i]!.AsArray();
            OcrModelInfo info = display[i];
            string pyName = e[0]!.GetValue<string>();
            Assert.True(info.Name == pyName, $"entry {i} name: python='{pyName}', display='{info.Name}'");
            string pyKind = e[1]!.GetValue<string>();
            Assert.True(info.Kind == pyKind, $"entry {i} kind: python='{pyKind}', display='{info.Kind}'");
            var langs = e[2]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
            Assert.True(info.Langs.SequenceEqual(langs),
                $"entry {i} langs (order-sensitive): python=[{string.Join(", ", langs)}], display=[{string.Join(", ", info.Langs)}]");
            float pySize = e[3]!.GetValue<float>();
            Assert.True(info.SizeMb == pySize, $"entry {i} size_mb: python={pySize}, display={info.SizeMb}");
        }

        string pyDet = root["default_det"]!.GetValue<string>();
        Assert.True(OcrCatalogDisplay.DefaultDet == pyDet,
            $"default det: python='{pyDet}', display='{OcrCatalogDisplay.DefaultDet}'");
        string pyRec = root["default_rec"]!.GetValue<string>();
        Assert.True(OcrCatalogDisplay.DefaultRec == pyRec,
            $"default rec: python='{pyRec}', display='{OcrCatalogDisplay.DefaultRec}'");
    }

    /// <summary>
    /// Runs the venv python directly on <see cref="CatalogJsonScript"/> (argv[1] = repo's src dir) and
    /// parses its single-line JSON output. Any spawn/exit/parse problem is a hard failure — the
    /// environment gate above already proved the venv exists, so this is drift or breakage, not a skip.
    /// Chạy thẳng python của venv trên <see cref="CatalogJsonScript"/> (argv[1] = thư mục src của repo)
    /// và parse JSON một dòng đầu ra. Mọi lỗi spawn/exit/parse đều là fail cứng — gate môi trường phía
    /// trên đã chứng minh venv tồn tại, nên đây là lệch hoặc hỏng chứ không phải lý do skip.
    /// </summary>
    private static JsonNode RunCatalogJson(string repoRoot, string pythonExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(CatalogJsonScript);
        psi.ArgumentList.Add(Path.Combine(repoRoot, "ReTran.OCR", "src"));

        using var p = Process.Start(psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit(60_000);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            Assert.Fail("catalog python did not exit within 60s (pure-data import should take well under a second)");
        }
        // The process already exited above, so both stream reads are done — .Result cannot deadlock.
        // Process đã thoát ở trên nên cả hai lượt đọc stream đều xong — .Result không thể deadlock.
        string stderr = errTask.Result;
        if (p.ExitCode != 0)
            Assert.Fail($"catalog python exited {p.ExitCode}: {stderr}");
        string stdout = outTask.Result;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(stdout);
        }
        catch (Exception ex)
        {
            Assert.Fail($"catalog python output was not valid JSON ({ex.Message}): {stdout} / stderr: {stderr}");
            throw; // unreachable — Assert.Fail throws
        }
        return node!;
    }
}
