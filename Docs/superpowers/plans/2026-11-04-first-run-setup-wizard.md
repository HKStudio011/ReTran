# Setup Wizard Lần Đầu Chạy — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Máy mới giải nén zip ReTran → lần đầu chạy app hiện modal setup tự động (check system → chọn ngôn ngữ → uv sync → tải model) cho tới khi pipeline hoạt động, không cần terminal.

**Architecture:** State machine `SetupCoordinator` trong C# (`ReTran.App/Services/Setup/`) điều phối các bước, mỗi bước retry ×3; UI là Razor component `SetupModal.razor` theo đúng pattern `UpdateModal.razor` (coordinator + event `Changed`). Sidecar Python thêm RPC `ocr.models.recommend` và migration model từ vị trí cũ; Core chỉ đổi env `PADDLE_PDX_CACHE_HOME`. Source `ReTran.OCR` + `uv.exe` vào build output qua csproj `Content` items.

**Tech Stack:** C# .NET 10 (net10.0-windows), xUnit v3, Python 3.13 (uv), Razor/Blazor Hybrid, GitHub Actions.

**Spec:** `Docs/superpowers/specs/2026-11-03-first-run-setup-wizard-design.md` — executor đọc cả spec lẫn plan; spec là nguồn của mọi giá trị (ngưỡng, đường dẫn, hành vi retry).

## Global Constraints

- .NET 10, xUnit v3 (MTP runner); C# public API có doc comment song ngữ (câu đầu tiếng Anh, ngay sau đó tiếng Việt) theo AGENTS.md; Python docstring Google-style.
- KHÔNG `git add -A` — repo có ~97 file WIP của user chưa commit. Mỗi commit chỉ `git add` đúng các file liệt kê trong task.
- Đường dẫn cố định (spec §6): `%LOCALAPPDATA%\ReTran\setup.json`; `<appDir>\ReTran.OCR\` chứa source + `.venv` + `models`; uv tại `runtimes\win-x64\uv.exe`.
- Retry: mỗi bước fail → tự retry **3 lần** (tổng 4 attempts) rồi mới tới màn hình lỗi; màn hình lỗi đúng 2 nút: **Thử lại** / **Bỏ qua** (bỏ qua = đóng app).
- Flavor GPU = `uv sync` nguyên vẹn; flavor CPU = ghi đè `pyproject.toml` (bỏ 2 dòng pin `paddlepaddle-gpu==3.4.0`/`nvidia-cudnn-cu12==9.9.0.52` + section `[tool.uv.index]` + `[tool.uv.sources]`) rồi `uv sync` + `uv pip install "paddlepaddle==3.4.0"`. Mọi lệnh uv chạy với env `UV_PYTHON_PREFERENCE=only-managed`.
- `PADDLE_PDX_CACHE_HOME` = `<repoRoot>\ReTran.OCR\models` (spec §5.5).
- Ngưỡng đĩa: 8 GB (GPU) / 5 GB (CPU) — nếu đo thật khác xa, hiệu chỉnh và ghi vào spec §10.
- Lệnh build/test chuẩn: `dotnet build "ReTran Project.slnx"`; `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`; Python: `uv run pytest tests -v` từ `ReTran.OCR/`.

## Review Focus

Năm failure mode spec không test trực tiếp — mỗi dòng có test ghim vào task sở hữu:

1. **Driver NVIDIA không khớp đúng nấc thang wheel** (≥580→cu130, ≥576.02→cu129, ≥560.76→cu126, <560.76→CPU+notice) → chọn sai wheel hoặc fail oan cả setup. → tests biên trong Task 6 (`SystemCheckTests`) + rewrite pyproject theo variant trong Task 7 (`UvSyncRunnerTests`).
2. **Đĩa hết giữa chừng tải model** (check đầu pass) → marker `.partial` giữ trạng thái trung thực; resume chỉ tải lại model thiếu/partial. → test trong Task 8 (`SetupCoordinatorTests`).
3. **AV/SmartScreen cách ly `uv.exe` hoặc file venv sau khi bước verify đã pass** → lần chạy sau `NeedsSetup` phát hiện lại, wizard resume đúng bước hỏng, không hỏi lại ngôn ngữ. → tests trong Task 5 + Task 8.
4. **Proxy công ty: probe pass nhưng PyPI chặn giữa `uv sync`** → uv exit ≠ 0 → retry ×3 rồi màn hình lỗi kèm đuôi output của uv. → test trong Task 7 + Task 8.
5. **User chọn ngôn ngữ mà toàn bộ model đã cài sẵn** → bước "áp dụng lựa chọn" vẫn phải ghi `OcrDet/OcrRec` vào settings.json (không được short-circuit vì "không có gì để tải"). → test trong Task 8.

---

### Task 1: Vendor `uv.exe` + csproj Content items (source Python + uv vào mọi build)

**Files:**
- Create: `runtimes/win-x64/uv.exe` (binary, ~15 MB), `runtimes/win-x64/uv.version.txt`
- Modify: `ReTran.App/ReTran.App.csproj` (thêm 1 ItemGroup sau block ProjectReference Updater tại dòng 78-84)

**Interfaces:**
- Consumes: không
- Produces: build output chứa `<output>\ReTran.OCR\pyproject.toml`, `<output>\ReTran.OCR\src\**`, `<output>\runtimes\win-x64\uv.exe` — Task 5 (`SetupPaths`) và Task 10 (UI) dựa vào layout này.

- [ ] **Step 1: Tải uv standalone phiên bản stable mới nhất**

```bash
# Từ repo root, dùng Git Bash hoặc pwsh:
curl -sL https://api.github.com/repos/astral-sh/uv/releases/latest | grep -o '"tag_name": *"[^"]*"' | head -1
curl -sL -o %TEMP%\uv.zip https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip
```

Ghi lại tag version tra được (ví dụ `0.7.x`) — dùng cho `uv.version.txt`.

- [ ] **Step 2: Giải nén, đặt file, ghi metadata audit**

Tạo thư mục `runtimes/win-x64/`, giải `uv.exe` từ zip vào đó. Tính SHA256 của `uv.exe` rồi tạo `runtimes/win-x64/uv.version.txt`:

```
version=<tag vừa tra, không kèm "v">
sha256=<hash 64 hex chữ thường>
source=https://github.com/astral-sh/uv/releases/tag/<tag>
```

- [ ] **Step 3: Thêm Content items vào `ReTran.App.csproj`**

Sau `</ItemGroup>` đóng block ProjectReference Updater (dòng ~84), thêm:

```xml
    <!-- First-run setup: source sidecar + uv standalone vào mọi build output (spec
         2026-11-03-first-run-setup-wizard §5.3) — cùng cơ chế với ProjectReference Updater. -->
    <ItemGroup>
        <Content Include="..\ReTran.OCR\pyproject.toml" Link="ReTran.OCR\pyproject.toml" CopyToOutputDirectory="PreserveNewest" />
        <Content Include="..\ReTran.OCR\src\**\*.*" Link="ReTran.OCR\src\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
        <Content Include="..\runtimes\win-x64\uv.exe" Link="runtimes\win-x64\uv.exe" CopyToOutputDirectory="PreserveNewest" />
        <Content Include="..\runtimes\win-x64\uv.version.txt" Link="runtimes\win-x64\uv.version.txt" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>
```

- [ ] **Step 4: Verify build output**

Run: `dotnet build "ReTran Project.slnx"`
Expected: Build succeeded. Sau đó verify (pwsh):

```powershell
Get-ChildItem "ReTran.App\bin" -Recurse -Filter "pyproject.toml" | Select-Object -First 1 -ExpandProperty FullName
Get-ChildItem "ReTran.App\bin" -Recurse -Filter "uv.exe" | Select-Object -First 1 -ExpandProperty FullName
```

Cả hai phải trả về path tồn tại (dưới `ReTran.OCR/` và `runtimes/win-x64/` tương ứng).

- [ ] **Step 5: Commit**

```bash
git add runtimes/win-x64/uv.exe runtimes/win-x64/uv.version.txt ReTran.App/ReTran.App.csproj
git commit -m "feat(setup): vendor uv.exe and copy ReTran.OCR source into build output"
```

---

### Task 2: Sidecar RPC `ocr.models.recommend`

**Files:**
- Modify: `ReTran.OCR/src/retran_core/ocr/models.py` (thêm handler trong `make_model_handlers` tại dòng ~396 + helper `_require_src`)
- Test: `ReTran.OCR/tests/test_ocr_models.py` (file có sẵn — thêm tests)

**Interfaces:**
- Consumes: `recommended(src, dst)` từ `retran_core.ocr.catalog` (models.py đã import tại dòng 23); `RpcInvalidParams` từ `..jsonrpc`.
- Produces: method JSON-RPC `ocr.models.recommend`, params `{"src": "<lang-code>"}` → result `{"det": <name>, "rec": <name>}`. Task 8 (SetupCoordinator) gọi qua core proxy.

- [ ] **Step 1: Viết failing tests**

Thêm vào `ReTran.OCR/tests/test_ocr_models.py` (theo style test có sẵn trong file):

```python
def test_recommend_v6_covered_language_returns_default_small_pair():
    handler = make_model_handlers(ModelManager(tmp_path))["ocr.models.recommend"]
    assert handler({"src": "vi"}) == {"det": "PP-OCRv6_small_det", "rec": "PP-OCRv6_small_rec"}
    assert handler({"src": "ja"}) == {"det": "PP-OCRv6_small_det", "rec": "PP-OCRv6_small_rec"}

def test_recommend_legacy_language_routes_to_script_rec_model():
    handler = make_model_handlers(ModelManager(tmp_path))["ocr.models.recommend"]
    assert handler({"src": "ko"}) == {"det": "PP-OCRv6_small_det", "rec": "korean_PP-OCRv5_mobile_rec"}
    assert handler({"src": "ru"}) == {"det": "PP-OCRv6_small_det", "rec": "cyrillic_PP-OCRv5_mobile_rec"}
    assert handler({"src": "th"}) == {"det": "PP-OCRv6_small_det", "rec": "th_PP-OCRv5_mobile_rec"}

def test_recommend_invalid_src_raises_rpc_invalid_params():
    import pytest
    from retran_core.jsonrpc import RpcInvalidParams
    handler = make_model_handlers(ModelManager(tmp_path))["ocr.models.recommend"]
    for bad in (None, {}, {"src": ""}, {"src": 5}):
        with pytest.raises(RpcInvalidParams):
            handler(bad)
```

(`tmp_path` là fixture có sẵn của pytest — khớp cách các test hiện tại tạo `ModelManager`.)

- [ ] **Step 2: Chạy test verify fail**

Run (từ `ReTran.OCR/`): `uv run pytest tests/test_ocr_models.py -v -k recommend`
Expected: FAIL — `KeyError: 'ocr.models.recommend'`.

- [ ] **Step 3: Implement trong `models.py`**

Thêm helper cạnh `_require_name` (dòng ~354):

```python
def _require_src(params: Any) -> str:
    """Extract and validate the 'src' param of ocr.models.recommend.

    Args:
        params: Raw JSON-RPC params.

    Returns:
        The source language code (non-empty string).

    Raises:
        RpcInvalidParams: The params are not an object with a non-empty
            string 'src'.
    """
```

Trong `make_model_handlers`, thêm closure `recommend(params)` gọi `_require_src` rồi `det, rec = recommended(src, "vi")` và trả `{"det": det, "rec": rec}`; đăng ký `"ocr.models.recommend": recommend` trong dict trả về. Cập nhật docstring của `make_model_handlers` liệt kê method mới (đừng bịa hành vi — mô tả đúng: trả cặp theo routing `recommended`, dst cố định vì không ảnh hưởng pair).

- [ ] **Step 4: Chạy test verify pass + toàn bộ suite Python**

Run: `uv run pytest tests -v`
Expected: PASS (cả mới lẫn cũ).

- [ ] **Step 5: Commit**

```bash
git add ReTran.OCR/src/retran_core/ocr/models.py ReTran.OCR/tests/test_ocr_models.py
git commit -m "feat(ocr): add ocr.models.recommend RPC for language-to-pair routing"
```

---

### Task 3: Sidecar migration model từ vị trí cũ (`%LOCALAPPDATA%\ReTran\models`)

**Files:**
- Modify: `ReTran.OCR/src/retran_core/ocr/models.py` (thêm hằng marker + hàm `migrate_from_retran_legacy`)
- Modify: `ReTran.OCR/src/retran_core/__main__.py:64-74` (gọi migration mới trong `_prepare_model_cache`)
- Test: `ReTran.OCR/tests/test_ocr_models.py`

**Interfaces:**
- Consumes: `CATALOG`, `_OFFICIAL_DIR`, pattern của `migrate_from_paddlex` (`models.py:311-351`).
- Produces: `migrate_from_retran_legacy(cache_dir: Path, source: Path | None = None) -> list[str]` — idempotent qua marker `.migrated-retran` trong cache root.

- [ ] **Step 1: Viết failing tests**

```python
def test_migrate_from_retran_legacy_copies_models_and_writes_marker(tmp_path):
    from retran_core.ocr.models import migrate_from_retran_legacy
    old = tmp_path / "old"
    model_name = CATALOG[1].name  # PP-OCRv6_small_det
    (old / "official_models" / model_name).mkdir(parents=True)
    (old / "official_models" / model_name / "weights.inference.json").write_text("{}")
    cache = tmp_path / "cache"
    copied = migrate_from_retran_legacy(cache, source=old)
    assert copied == [model_name]
    assert (cache / "official_models" / model_name / "weights.inference.json").exists()
    assert (cache / ".migrated-retran").exists()

def test_migrate_from_retran_legacy_is_idempotent(tmp_path):
    from retran_core.ocr.models import migrate_from_retran_legacy
    old = tmp_path / "old"
    model_name = CATALOG[1].name
    (old / "official_models" / model_name).mkdir(parents=True)
    (old / "official_models" / model_name / "f").write_text("x")
    cache = tmp_path / "cache"
    migrate_from_retran_legacy(cache, source=old)
    assert migrate_from_retran_legacy(cache, source=old) == []

def test_migrate_from_retran_legacy_default_source_noop_off_windows(tmp_path, monkeypatch):
    from retran_core.ocr import models as m
    monkeypatch.setattr(m.sys, "platform", "linux")
    assert m.migrate_from_retran_legacy(tmp_path / "cache") == []
```

- [ ] **Step 2: Chạy test verify fail**

Run: `uv run pytest tests/test_ocr_models.py -v -k retran_legacy`
Expected: FAIL — `ImportError`/`AttributeError` (chưa có hàm).

- [ ] **Step 3: Implement**

Trong `models.py`, cạnh `migrate_from_paddlex`: hằng `_RETRAN_LEGACY_MARKER = ".migrated-retran"` và hàm `migrate_from_retran_legacy(cache_dir, source=None)`:
- default `source`: `Path(os.environ.get("LOCALAPPDATA", ...)) / "ReTran" / "models"` khi `sys.platform == "win32"`, ngược lại `None` → trả `[]` ngay.
- marker `.migrated-retran` trong cache root — present → trả `[]`.
- copy best-effort từng model catalog từ `source/official_models/<name>` (chỉ đọc source, không move/delete), ghi marker cuối cùng dù có fail từng model — đúng ngữ nghĩa của `migrate_from_paddlex`, docstring Google-style mô tả rõ đây là legacy vị trí cũ của ReTran trước spec 2026-11-03.

Trong `__main__.py` `_prepare_model_cache` (sau block try/except của `migrate_from_paddlex`, dòng ~64-74): thêm block try/except tương tự gọi `migrate_from_retran_legacy(cache_dir)`, log stderr khi có model được migrate (`migrated model(s) from legacy ReTran cache: ...`).

- [ ] **Step 4: Chạy test verify pass + suite**

Run: `uv run pytest tests -v`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.OCR/src/retran_core/ocr/models.py ReTran.OCR/src/retran_core/__main__.py ReTran.OCR/tests/test_ocr_models.py
git commit -m "feat(ocr): one-shot migration of models from legacy LOCALAPPDATA cache"
```

---

### Task 4: Core — `PADDLE_PDX_CACHE_HOME` trỏ vào `<repoRoot>\ReTran.OCR\models` + proxy `ocr.models.recommend`

**Files:**
- Modify: `ReTran.Core/OcrClient/SidecarProcess.cs:27-64` (trích `BuildStartInfo`, đổi env)
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (thêm method forward `ocr.models.recommend`, mirror `ListOcrModelsAsync`) + `ReTran.Core/Program.cs` (dòng ~98-101, thêm 1 `server.Register`)
- Test: `ReTran.Tests/SidecarProcessTests.cs` (file có sẵn — thêm tests), `ReTran.Tests/PipelineHostTests.cs` (file có sẵn — thêm test proxy)

**Interfaces:**
- Consumes: RPC sidecar `ocr.models.recommend` (Task 2 đã tạo ở `retran_core`).
- Produces: `internal static ProcessStartInfo BuildStartInfo(string pythonExe, string workingDir)` trên `SidecarProcess`; env `PADDLE_PDX_CACHE_HOME` = `Path.Combine(workingDir, "ReTran.OCR", "models")`; core method `ocr.models.recommend` nhận `{"src": ...}` → trả nguyên văn `{det, rec}` từ sidecar (-32603 khi sidecar unavailable) — Task 8 gọi method này qua `ISetupCoreBridge`.

- [ ] **Step 1: Viết failing test**

```csharp
[Fact]
public void BuildStartInfo_SetsModelCacheEnvNextToOcrProject()
{
    var info = SidecarProcess.BuildStartInfo("C:\\py\\python.exe", @"D:\app");
    Assert.Equal(@"D:\app\ReTran.OCR\models", info.EnvironmentVariables["PADDLE_PDX_CACHE_HOME"]);
}

[Fact]
public void BuildStartInfo_KeepsWorkingDirectoryAndArgs()
{
    var info = SidecarProcess.BuildStartInfo("C:\\py\\python.exe", @"D:\app");
    Assert.Equal(@"D:\app\ReTran.OCR", info.WorkingDirectory);
    Assert.Equal(new[] { "-m", "retran_core", "serve" }, info.ArgumentList.ToArray());
}

[Fact]
public async Task Serve_RealCore_FindsVenvInCleanAppDir()
{
    // Integration (spec §7) — SKIP khi repo .venv hoặc ReTran.Core.exe chưa có.
    // 1. Tạo fake appDir trong temp: copy source ReTran.OCR (pyproject.toml + src/**) từ repo,
    //    junction `ReTran.OCR\.venv` trỏ vào venv thật của repo (cmd /c mklink /J — fail → skip).
    // 2. Chạy ReTran.Core.exe --serve với WorkingDirectory = fake appDir.
    // 3. Gửi ocr.models.list qua stdio JSON-RPC → expect result chứa "catalog" (sidecar sống,
    //    Core tự tìm venv qua FindRepoRoot + fallback OcrBootstrap.cs:35-36, không cần config).
    // Timeout ~120s (paddle import lúc sidecar start). Dọn fake appDir trong finally.
}
```

Thêm vào `ReTran.Tests/PipelineHostTests.cs` (dùng `FakeSidecarRpc` + `CreateHost` có sẵn trong file):

```csharp
[Fact]
public async Task RecommendOcrModels_ForwardsSrcToSidecar_Verbatim()
{
    // FakeSidecarRpc ghi nhận method "ocr.models.recommend" + params gửi lên; trả về {"det":"d1","rec":"r1"}.
    // host.RecommendOcrModelsAsync({"src":"ko"}, ct) → result đúng {"det":"d1","rec":"r1"};
    // method name phía sidecar đúng là "ocr.models.recommend", params chuyển tiếp nguyên văn.
}

[Fact]
public async Task RecommendOcrModels_NullSidecar_ThrowsMinus32603()
{
    // host tạo với rpc=null → RpcMethodException code -32603 (đúng pattern các test ocr.models.* khi sidecar null).
}
```

- [ ] **Step 2: Chạy test verify fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SidecarProcessTests"`
Expected: FAIL — `BuildStartInfo` chưa tồn tại (compile error). Các test PipelineHost cũng fail compile (chưa có `RecommendOcrModelsAsync`).

- [ ] **Step 3: Implement trong `SidecarProcess.cs`**

Trích toàn bộ body tạo `ProcessStartInfo` trong `StartAsync` (dòng 29-53) thành `internal static ProcessStartInfo BuildStartInfo(string pythonExe, string workingDir)`; `StartAsync` gọi nó. Thay khối env `PADDLE_PDX_CACHE_HOME` (dòng 41-50, gồm cả nhánh LOCALAPPDATA) bằng:

```csharp
// Model cache lives next to the OCR project so venv and models survive app updates
// together (spec 2026-11-03-first-run-setup-wizard §5.5).
// Cache model nằm cạnh dự án OCR để venv và model sống qua update cùng nhau.
psi.EnvironmentVariables["PADDLE_PDX_CACHE_HOME"] = Path.Combine(workingDir, "ReTran.OCR", "models");
```

Cả hai hằng env (`PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK`, `PADDLE_PDX_CACHE_HOME`) giữ nguyên vị trí trong `BuildStartInfo`. Doc comment song ngữ cho `BuildStartInfo`.

Bước proxy (ruling từ Task 2 — sidecar đã có method, Core chưa forward): trong `ReTran.Core/Serve/PipelineHost.cs`, thêm `public async Task<JsonObject> RecommendOcrModelsAsync(JsonObject? args, CancellationToken ct)` mirror từng dòng `ListOcrModelsAsync` (dùng `ForwardToSidecarAsync` với method name `"ocr.models.recommend"`), và trong `ReTran.Core/Program.cs` (khối `server.Register` cho `ocr.models.*`, dòng ~98-101) thêm `server.Register("ocr.models.recommend", ...)` theo đúng pattern 2 đăng ký bên cạnh. Doc comment song ngữ.

- [ ] **Step 4: Chạy test verify pass + suite C#**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: toàn bộ PASS (không test cũ nào assert env LOCALAPPDATA — đã verify trước khi viết plan). Test integration chạy thật trên máy dev có venv; ở môi trường thiếu điều kiện nó skip, không fail.

- [ ] **Step 5: Commit**

```bash
git add ReTran.Core/OcrClient/SidecarProcess.cs ReTran.Tests/SidecarProcessTests.cs
git commit -m "feat(core): point PADDLE_PDX_CACHE_HOME at <repoRoot>/ReTran.OCR/models"

git add ReTran.Core/Serve/PipelineHost.cs ReTran.Core/Program.cs ReTran.Tests/PipelineHostTests.cs
git commit -m "feat(core): forward ocr.models.recommend to sidecar"
```

(Hai commit riêng — hai thay đổi độc lập; các test filter ở Step 2/4 đã phủ cả hai.)

---

### Task 5: `SetupPaths` + `SetupState` (đường dẫn, trạng thái setup.json)

**Files:**
- Create: `ReTran.App/Services/Setup/SetupPaths.cs`
- Create: `ReTran.App/Services/Setup/SetupState.cs`
- Test: `ReTran.Tests/Setup/SetupStateTests.cs`

**Interfaces:**
- Consumes: không
- Produces (dùng bởi Task 6, 7, 8, 10):

```csharp
namespace ReTran_App.Services.Setup;

public static class SetupPaths
{
    public static string AppDir { get; }            // AppContext.BaseDirectory, bỏ separator cuối
    public static string OcrDir => Path.Combine(AppDir, "ReTran.OCR");
    public static string VenvPythonExe => Path.Combine(OcrDir, ".venv", "Scripts", "python.exe");
    public static string ModelsDir => Path.Combine(OcrDir, "models");
    public static string UvExe => Path.Combine(AppDir, "runtimes", "win-x64", "uv.exe");
    public static string SetupJsonPath { get; }     // %LOCALAPPDATA%\ReTran\setup.json
}

public sealed record SetupState(string? Flavor = null, string? Language = null, Dictionary<string, string>? Steps = null)
{
    public bool IsComplete { get; init; }           // true khi wizard chạy tới bước Done
    public int Version { get; init; } = 1;          // khóa "version" theo schema spec §8 — giữ cho migration trạng thái tương lai
    // path gắn với instance (null → SetupPaths.SetupJsonPath); Load(path) trả state nhớ path đó
    public static SetupState Load(string? path = null);   // missing/hỏng JSON → new SetupState() (vẫn nhớ path)
    public void Save();                             // atomic: tmp + File.Move(overwrite), tạo thư mục cha; ghi ra path đã gắn
    public static bool NeedsSetup(SetupState state, string venvPythonExe, string modelsDir);
}
```

Round-trip test phải chốt `version` được ghi và đọc lại (mặc định 1 khi file thiếu khóa).

- [ ] **Step 1: Viết failing tests** (`ReTran.Tests/Setup/SetupStateTests.cs`)

```csharp
public class SetupStateNeedsSetupTests
{
    [Fact]
    public void NeedsSetup_True_WhenVenvMissing_EvenIfStateComplete()
    {
        var state = new SetupState("gpu", "vi", null) { IsComplete = true };
        Assert.True(SetupState.NeedsSetup(state, @"C:\nope\python.exe", MakeModelsDir()));
    }

    [Fact]
    public void NeedsSetup_True_WhenModelsDirEmpty()
    { /* models dir tồn tại nhưng rỗng → true */ }

    [Fact]
    public void NeedsSetup_False_WhenCompleteAndFilesPresent()
    { /* venv + models/official_models có file → false */ }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyState() { /* trỏ SetupJsonPath vào path rỗng qua overload Load(path) → new */ }

    [Fact]
    public void SaveThenLoad_RoundTrips_FlavorAndLanguage() { }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyState_NoThrow() { /* ghi "not json" vào file */ }
}
```

`MakeModelsDir()` = helper tạo `<tmp>\models\official_models\x` + 1 file. Test round-trip và corrupt-json dùng `Load(path)`/`Save()` với temp path (path gắn instance — xem Interfaces).

- [ ] **Step 2: Chạy test verify fail** → `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SetupStateNeedsSetupTests"` → FAIL (compile).

- [ ] **Step 3: Implement** `SetupPaths.cs` + `SetupState.cs` đúng signatures trên. Logic `NeedsSetup`: `!state.IsComplete || !File.Exists(venvPythonExe) || !(modelsDir\official_models tồn tại && có ít nhất 1 mục)` — chỉ test file, không chạy python (spec §5.9).

- [ ] **Step 4: Chạy test verify pass** → Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Setup/SetupPaths.cs ReTran.App/Services/Setup/SetupState.cs ReTran.Tests/Setup/SetupStateTests.cs
git commit -m "feat(setup): add SetupPaths and SetupState (setup.json) with NeedsSetup predicate"
```

---

### Task 6: `SystemCheck` (GPU/driver, đĩa, mạng, trạng thái hiện có)

**Files:**
- Create: `ReTran.App/Services/Setup/SystemCheck.cs` (chứa enum + records + static class)
- Create: `ReTran.App/Services/Setup/VideoAdapterSource.cs` (WMI implementation của interface)
- Test: `ReTran.Tests/Setup/SystemCheckTests.cs`

**Interfaces:**
- Consumes: `SetupPaths` (Task 5).
- Produces (dùng bởi Task 8):

```csharp
namespace ReTran_App.Services.Setup;

public enum OcrFlavor { Gpu, Cpu }

public sealed record VideoAdapterInfo(string Name, bool IsDiscrete, string? DriverVersion); // DriverVersion = file version nvdrv.dll dạng "31.0.xx.x"
public interface IVideoAdapterSource { IReadOnlyList<VideoAdapterInfo> GetAdapters(); }

public sealed record SystemCheckResult(
    bool Ok, OcrFlavor Flavor, string? Notice, string? FailureReason,
    bool UvExePresent, bool VenvPresent,
    string? CudaWheel);   // "cu130"|"cu129"|"cu126" khi Flavor==Gpu; null khi Cpu

public static class SystemCheck
{
    public const long MinFreeBytesGpu = 8L * 1024 * 1024 * 1024;
    public const long MinFreeBytesCpu = 5L * 1024 * 1024 * 1024;
    public static readonly string[] NetworkProbeHosts = { "pypi.org", "www.paddlepaddle.org.cn", "github.com" };
    // Host model của paddlex xác nhận khi implement (spec §10) — thêm vào array lúc đó.

    // Thang wheel (rule ở dưới): helper thuần, test được trực tiếp tại các biên 580 / 576.02 / 560.76.
    public static string? CudaWheelForDriverVersion(int major, int minor, int build);

    public static SystemCheckResult Run(
        IVideoAdapterSource adapters,
        Func<string, long> diskFreeBytes,          // nhận drive letter/path → bytes trống
        Func<string, bool> isHostReachable);       // host → reachable (HTTPS probe, timeout ngắn)
}
```

Quy tắc quyết định (spec §3.5, §5.7 — ruling 2026-11-05: KHÔNG hard-pin một wheel; chọn wheel paddlepaddle-gpu 3.4.x CAO NHẤT mà driver hỗ trợ đầy đủ, thang NVIDIA Table 3 + Table 2): NVIDIA discrete + driver ≥ 580 → `Gpu` + `CudaWheel="cu130"`; ≥ 576.02 (12.9 GA) → `Gpu` + `"cu129"`; ≥ 560.76 (12.6 GA, kể cả driver nhánh 570/572 hỗ trợ 12.7/12.8 mà không có wheel tương ứng) → `Gpu` + `"cu126"`; < 560.76 hoặc DriverVersion parse fail/null → `Cpu` + notice "driver chưa đủ mới". AMD discrete → `Cpu` + notice theo đúng giọng spec ("không cài được bản GPU cho card AMD trên Windows, sẽ dùng bản CPU"); không có GPU rời → `Cpu` + notice nhẹ. Fail (Ok=false) khi: sai arch (chạy trên non-x64), thiếu `uv.exe`, đĩa < ngưỡng theo flavor dự kiến, hoặc 1 host probe không tới được (`FailureReason` chỉ rõ host).

- [ ] **Step 1: Viết failing tests** — fake `IVideoAdapterSource` + funcs. Cases tối thiểu (mỗi case 1 test):

```csharp
[Fact] Gpu_Cu130_WhenDriver580Plus()          // "NVIDIA GeForce RTX 5070", DriverVersion tương đương 580.x → Ok, Gpu, CudaWheel="cu130", Notice null
[Fact] Gpu_Cu129_WhenDriverBetween576And580()// driver ~576.02 → Gpu, CudaWheel="cu129"
[Fact] Gpu_Cu126_WhenDriver570Branch()       // driver 572.61 (hỗ trợ 12.8, không có wheel 12.8) → Gpu, CudaWheel="cu126"   (Review Focus #1)
[Fact] Cpu_WithNotice_WhenDriverBelow560_76()// 560.75 hoặc DriverVersion null/rác → Ok, Flavor=Cpu, Notice != null
[Fact] CudaWheelForDriverVersion_Boundaries()// helper thuần: 580.0.0→cu130, 579.99→cu129 edge, 576.02→cu129, 576.01→cu126 edge, 560.76→cu126, 560.75→null
[Fact] Cpu_WithAmdNotice_WhenDiscreteAmd()    // "AMD Radeon RX 7800 XT", IsDiscrete=true → Cpu + notice chứa "CPU"
[Fact] Cpu_LightNotice_WhenOnlyIgpu()         // chỉ iGPU (IsDiscrete=false) → Cpu + notice nhẹ, KHÔNG nhắc "card AMD"
[Fact] Fail_WhenUvExeMissing()                // Ok=false, FailureReason nhắc uv.exe
[Fact] Fail_WhenDiskBelowCpuThreshold()       // free 4 GB, dự kiến CPU → Ok=false
[Fact] Fail_NamesUnreachableHost()            // isHostReachable("github.com")=false → FailureReason chứa "github.com"
```

- [ ] **Step 2: Chạy test verify fail** → FAIL (compile).

- [ ] **Step 3: Implement** `SystemCheck.Run` + `VideoAdapterSource` (WMI `Win32_VideoController` qua `System.Management` — kiểm tra csproj đã có package; nếu chưa, thêm PackageReference `System.Management`. Heuristic discrete AMD: tên chứa "Radeon" VÀ khớp pattern rời (chứa "RX "); iGPU không khớp. Driver NVIDIA: parse ra (major, minor, build) từ nguồn đáng tin nhất trên máy — empirical capture 4 nguồn (WMI DriverVersion, file version nvdrv.dll, registry nvlddmkm, nvidia-smi) rồi chọn; so với các mốc 580 / 576.02 / 560.76 (hằng dẫn xuất CUDA Table 3/Table 2, comment ghi rõ nguồn + CUDA 13.0 cột Windows = N/A, sàn 580 theo Table 2). Ghi THANG wheel vào spec §10 (doc string đã có trong rule ở trên — commit docs riêng).

- [ ] **Step 4: Chạy test verify pass + suite** → Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Setup/SystemCheck.cs ReTran.App/Services/Setup/VideoAdapterSource.cs ReTran.Tests/Setup/SystemCheckTests.cs
# (nếu thêm System.Management vào csproj — git add ReTran.App/ReTran.App.csproj kèm theo)
git commit -m "feat(setup): add SystemCheck with GPU/CPU flavor decision and network probes"
```

---

### Task 7: `UvSyncRunner` (flavor apply + chạy uv + dò flavor đã cài)

**Files:**
- Create: `ReTran.App/Services/Setup/ProcessRunner.cs` (`IProcessRunner` + implementation thật)
- Create: `ReTran.App/Services/Setup/UvSyncRunner.cs`
- Test: `ReTran.Tests/Setup/UvSyncRunnerTests.cs`

**Interfaces:**
- Consumes: `OcrFlavor`, `SetupPaths` (Task 5, 6).
- Produces (dùng bởi Task 8):

```csharp
public interface IProcessRunner
{
    Task<(int ExitCode, string OutputTail)> RunAsync(string fileName, IReadOnlyList<string> args, string workingDir, IDictionary<string, string>? extraEnv, CancellationToken ct);
}

public sealed class UvSyncRunner
{
    public UvSyncRunner(IProcessRunner runner, string uvExe = SetupPaths.UvExe, string ocrDir = SetupPaths.OcrDir);

    // Rewrite pyproject.toml theo flavor + wheel variant (ruling 2026-11-05 — SystemCheck chọn CudaWheel):
    // - CPU: bỏ 2 dòng pin GPU ("paddlepaddle-gpu", "nvidia-cudnn-cu12") + section [tool.uv.index]/[tool.uv.sources].
    // - GPU cu129: giữ nguyên nội dung như pyproject HEAD (pin ==3.4.0 + cudnn 9.9.0.52 + index paddle-cu129).
    // - GPU cu126/cu130: index name/url → paddle-<variant>/.../packages/stable/<variant>/ + [tool.uv.sources] trỏ index mới;
    //   BỎ dòng pin "nvidia-cudnn-cu12==9.9.0.52" (cu126=cudnn9.0, cu130=cu13 packages — paddle wheel tự kéo đúng bản).
    // Idempotent — chạy lại trên file đã rewrite không phá gì; chỉ ghi file khi nội dung đổi.
    public static void ApplyFlavorToPyproject(OcrFlavor flavor, string? cudaWheel, string pyprojectPath); // cudaWheel null ⇒ coi như "cu129"

    // GPU: ApplyFlavor(flavor, wheel) → uv sync. CPU: ApplyFlavor → uv sync → uv pip install "paddlepaddle==3.4.0".
    // Mọi lệnh chạy với extraEnv UV_PYTHON_PREFERENCE=only-managed, workingDir = ocrDir.
    public Task<int> SyncAsync(OcrFlavor flavor, string? cudaWheel, CancellationToken ct);   // trả exit code của lệnh fail (0 nếu hết)

    // "uv pip list" → parse: có paddlepaddle-gpu → Gpu; chỉ paddlepaddle → Cpu; không có → null.
    public Task<OcrFlavor?> InstalledPaddleFlavorAsync();
}
```

- [ ] **Step 1: Viết failing tests** — fake `IProcessRunner` ghi lại lệnh + trả exit code giả:

```csharp
[Fact] ApplyFlavor_Cpu_StripsGpuPinsAndIndexSections()   // file pyproject mẫu (nội dung thật của ReTran.OCR/pyproject.toml) → sau rewrite: không còn "paddlepaddle-gpu", "nvidia-cudnn", "[tool.uv.index]", "[tool.uv.sources]"; các deps khác nguyên vẹn
[Fact] ApplyFlavor_Cpu_IsIdempotent()                    // chạy 2 lần → nội dung ổn định
[Fact] ApplyFlavor_GpuCu129_IsNoOp()                     // file đúng bằng pyproject HEAD hiện tại → không đổi byte
[Fact] ApplyFlavor_GpuCu126_RewritesIndexDropsCudnnPin() // index url → .../cu126/, sources trỏ paddle-cu126, mất dòng "nvidia-cudnn-cu12", còn "paddlepaddle-gpu==3.4.0"
[Fact] ApplyFlavor_GpuCu130_RewritesIndexDropsCudnnPin() // tương tự với cu130
[Fact] ApplyFlavor_Gpu_IsIdempotent()                    // cu126 chạy 2 lần → ổn định
[Fact] SyncAsync_Gpu_RunsRewriteThenSyncOnly()           // fake runner: đúng 1 lệnh "sync"; extraEnv có UV_PYTHON_PREFERENCE=only-managed; workingDir = ocrDir
[Fact] SyncAsync_Cpu_RunsRewriteSyncAndCpuWheelInstall() // 2 lệnh: "sync" rồi "pip install paddlepaddle==3.4.0" (theo thứ tự)
[Fact] SyncAsync_ReturnsNonZeroExit_WhenUvFails_AndKeepsOutputTail()  // fake trả exit 1 → SyncAsync = 1; OutputTail giữ được (Review Focus #4)
[Fact] InstalledPaddleFlavor_ParsesGpuCpuAndNone()       // stdout giả "paddlepaddle-gpu 3.4.0" → Gpu; "paddlepaddle 3.4.0" → Cpu; rỗng → null
```

**Caveat dev-run (ghi vào báo cáo task):** trên máy dev, appDir = repo → ApplyFlavor variants khác cu129 sẽ sửa `ReTran.OCR/pyproject.toml` + `uv.lock` của repo. Đây là hành vi đúng của wizard nhưng user đang dev cu129 — Task 8/smoke phải nhắc; không tự động revert.

- [ ] **Step 2: Chạy test verify fail** → FAIL (compile).

- [ ] **Step 3: Implement** `ProcessRunner.cs` (Process + RedirectStandardOutput/Error, bắt giữ ~8 KB output cuối cho `OutputTail`, env gộp từ `Environment.Process` + extraEnv) và `UvSyncRunner`. Parse pyproject: thao tác text theo dòng/section (file nhỏ, format đã biết — không cần TOML parser); viết lại file chỉ khi nội dung đổi.

- [ ] **Step 4: Chạy test verify pass + suite** → Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Setup/ProcessRunner.cs ReTran.App/Services/Setup/UvSyncRunner.cs ReTran.Tests/Setup/UvSyncRunnerTests.cs
git commit -m "feat(setup): add UvSyncRunner with flavor-specific sync and CPU wheel install"
```

---

### Task 8: `SetupCoordinator` — state machine (retry ×3, resume, events, skip)

**Files:**
- Create: `ReTran.App/Services/Setup/SetupCoordinator.cs`
- Test: `ReTran.Tests/Setup/SetupCoordinatorTests.cs`

**Interfaces:**
- Consumes: `SystemCheck` + `OcrFlavor` (Task 6), `UvSyncRunner` (Task 7), `SetupState`/`SetupPaths` (Task 5), `AppSettingsService` (có sẵn, `ReTran.App/Services/AppSettingsService.cs`).
- Produces (dùng bởi Task 10 — UI):

```csharp
public enum SetupStep { Checking, ChoosingLanguage, VerifyingFiles, Syncing, VerifyingSidecar, DownloadingModels, Applying, Done, Failed }

public sealed class SetupCoordinator
{
    // Deps qua constructor (tất cả injectable để test bằng fake):
    //   IVideoAdapterSource, Func<string,long> diskFree, Func<string,bool> hostReachable,
    //   UvSyncRunner, ISetupCoreBridge, AppSettingsService, Action closeApp,
    //   Func<SetupState> stateFactory = () => SetupState.Load()  (test trỏ temp path)

    public SetupStep Step { get; private set; }        // bắt đầu: Checking
    public string? Error { get; private set; }         // thông điệp lỗi bước hiện tại (kèm OutputTail nếu có)
    public string? Notice { get; private set; }        // notice GPU/CPU từ SystemCheck, giữ cho UI hiển thị
    public OcrFlavor Flavor { get; private set; }
    public bool IsComplete => Step == SetupStep.Done;
    public event Action? Changed;                       // bắn sau MỌI đổi state (UI marshal như UpdateModal)

    // Chạy check system → nếu state.Language còn null: dừng ở ChoosingLanguage (UI hỏi).
    // Nếu language đã có (resume): chạy tiếp từ bước chưa done đầu tiên. force=true bỏ qua IsComplete.
    public Task StartAsync(bool force = false, CancellationToken ct = default);
    // UI gọi sau khi user chọn ngôn ngữ — lưu vào state rồi tiếp tục.
    public Task ContinueWithLanguageAsync(string langCode, CancellationToken ct = default);
    // Chạy lại bước đang Failed (đếm retry lại từ đầu).
    public Task RetryAsync(CancellationToken ct = default);
    // Bỏ qua: gọi closeApp() (đóng app) — spec §3.6. Không ghi state gì thêm.
    public void Skip();
}

public interface ISetupCoreBridge   // adapter DI bọc CoreProcessClient (Task 10 wiring)
{
    Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct);
    Task RestartAsync(CancellationToken ct);   // dispose + start lại tiến trình core
}
```

Hành vi bắt buộc (spec §3, §4):

- **Retry**: mỗi bước 4–7 fail → tự retry tới 3 lần (hằng `MaxRetries = 3`), vẫn fail → `Step = Failed`, `Error` = thông điệp + đuôi output, `Changed`. Bước `VerifyingFiles` thiếu file và bước `Checking` fail điều kiện cứng → thẳng `Failed`, không retry.
- **Flavor match trước khi sync** (spec §5.4): đầu bước `Syncing`, gọi `InstalledPaddleFlavorAsync()` — nếu bằng flavor đã quyết định VÀ venv tồn tại → đánh dấu bước done, skip chạy `uv sync` (đường nhanh cho "Chạy lại setup" — smoke #8). Lệch hoặc chưa có venv → chạy `SyncAsync(check.Flavor, check.CudaWheel, ct)` đầy đủ (`check` = `SystemCheckResult` lưu từ bước Checking; `uv sync` reconcile venv với pyproject đã rewrite nên tự sửa cả trường hợp user đổi card/driver). Không cần persist CudaWheel vào setup.json — wizard luôn chạy Checking trước (spec §3), giá trị tính lại được từ driver hiện tại.
- **Resume**: `StartAsync` đọc state — bước nào `Steps[step] == "done"` thì skip; `Language` đã lưu thì không dừng ở `ChoosingLanguage` lại (Review Focus #3). Ghi `Steps[step] = "done"` + `Save()` SAU khi bước thực sự thành công.
- **Bước VerifyingSidecar**: `bridge.RestartAsync` → `CallAsync("ocr.models.list")` phải trả về catalog; fail = sidecar chưa sống.
- **Bước DownloadingModels**: `bridge.CallAsync("ocr.models.recommend", {"src": lang})` → cặp (det, rec); gọi `ocr.models.download` cho từng model trong cặp chưa `installed` (theo `ocr.models.list`); poll `ocr.models.list` mỗi 2 s tới khi cả hai `installed`, timeout tổng 60 phút rồi fail. Model có marker `.partial` (không installed) được download lại — idempotent phía sidecar (Review Focus #2).
- **Bước Applying**: LUÔN chạy dù không model nào tải (Review Focus #5): đọc `AppSettingsService.Load()`, ghi `OcrDet/OcrRec` từ cặp recommend qua `with { OcrDet = det, OcrRec = rec }` + `Save()`.
- **Done**: `SetupState` với `IsComplete = true` + `Save()`; `Step = Done`.

- [ ] **Step 1: Viết failing tests** — fake toàn bộ deps (`FakeCoreBridge` ghi lại calls, `FakeUvSyncRunner`, funcs giả). Cases tối thiểu:

```csharp
[Fact] Start_WithoutLanguage_PausesAtChoosingLanguage()          // state rỗng → Step == ChoosingLanguage sau check pass
[Fact] ContinueWithLanguage_RunsRemainingStepsAndCompletes()     // fake bridge trả catalog + recommend; cuối: Step == Done, state.IsComplete, settings nhận đúng OcrDet/OcrRec (fake AppSettingsService hoặc temp file thật)
[Fact] FailedStep_RetriesThreeTimes_ThenFails()                  // fake sync luôn fail → sau StartAsync: Step == FakeUvSyncRunner gọi đúng 4 lần, Error != null
[Fact] Retry_RestartsAttemptCounter()                            // sau Failed: RetryAsync → thêm tối đa 4 attempts nữa
[Fact] Resume_SkipsDoneSteps_KeepsLanguage()                     // state có Steps{syncing=done}+Language="ko" → StartAsync không gọi sync lại, không dừng ở ChoosingLanguage (Review Focus #3)
[Fact] AllModelsAlreadyInstalled_StillWritesSettingsPair()       // list trả installed=true cả hai → bước Applying vẫn ghi settings (Review Focus #5)
[Fact] PartialModel_IsDownloadedAgain_OnResume()                 // model A installed, model B có partial (installed=false) → download chỉ gọi cho B (Review Focus #2)
[Fact] VerifyFiles_MissingUv_GoesToFailed_WithoutRetry()         // fake UvExe missing → Failed sau 1 attempt, Error nhắc giải nén lại zip
[Fact] Check_FailHard_GoesToFailed_Directly()                    // diskFree thấp → Step == Failed, không bước nào khác chạy
[Fact] Skip_CallsCloseApp()                                      // Action fake ghi flag → true
```

- [ ] **Step 2: Chạy test verify fail** → FAIL (compile).

- [ ] **Step 3: Implement** `SetupCoordinator` theo bảng bước spec §4 + các ràng buộc trên. Mọi đổi state đi qua một method riêng (set property → `Changed?.Invoke()`) để không quên event; marshal thread-safety do UI lo (như `UpdateModal.OnChanged`).

- [ ] **Step 4: Chạy test verify pass + suite** → Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Setup/SetupCoordinator.cs ReTran.Tests/Setup/SetupCoordinatorTests.cs
git commit -m "feat(setup): add SetupCoordinator state machine with retry, resume and skip"
```

---

### Task 9: `OcrCatalogDisplay` + drift test so với catalog Python thật

**Files:**
- Create: `ReTran.App/Services/Setup/OcrCatalogDisplay.cs`
- Test: `ReTran.Tests/Setup/OcrCatalogDisplayTests.cs`

**Interfaces:**
- Consumes: không (dữ liệu copy từ `ReTran.OCR/src/retran_core/ocr/catalog.py` — source of truth).
- Produces (dùng bởi Task 10 — màn hình chọn ngôn ngữ):

```csharp
public sealed record OcrModelInfo(string Name, string Kind, IReadOnlyList<string> Langs, float SizeMb);
public static class OcrCatalogDisplay
{
    public static IReadOnlyList<OcrModelInfo> Models { get; }   // đúng 16 mục của CATALOG, cùng thứ tự
    public static string DefaultDet { get; }                    // "PP-OCRv6_small_det"
    public static string DefaultRec { get; }                    // "PP-OCRv6_small_rec"
}
```

- [ ] **Step 1: Viết test drift** — chạy Python thật qua uv, so khớp từng mục (name/kind/langs/sizeMb). Skip gracefully khi máy không có uv hoặc venv (theo pattern skip-env có sẵn trong `ReTran.Tests/ServeM4IntegrationTests.cs`):

```csharp
[Fact]
public void DisplayCatalog_MatchesPythonCatalog()
{
    // "uv run --project ReTran.OCR python -c ..." in JSON [[name, kind, langs, sizeMb], ...]
    // So với OcrCatalogDisplay.Models — Assert.Equal từng trường, thứ tự giữ nguyên.
}
```

- [ ] **Step 2: Chạy test verify fail** → FAIL (`OcrCatalogDisplay` chưa có; hoặc drift nếu copy sai).

- [ ] **Step 3: Implement** — copy đúng 16 `ModelSpec` từ `catalog.py:80-101` (đọc file khi implement, không gõ tay nhớ); doc comment ghi rõ đây là bản sao chỉ-để-hiển-thị và test drift chặn lệch.

- [ ] **Step 4: Chạy test verify pass + suite** → Expected: PASS (test drift chạy thật trên máy dev có uv).

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/Setup/OcrCatalogDisplay.cs ReTran.Tests/Setup/OcrCatalogDisplayTests.cs
git commit -m "feat(setup): add display-only OCR catalog mirror with drift test"
```

---

### Task 10: Wiring DI + `SetupModal.razor` + chặn pipeline + nút "Chạy lại setup" + i18n

**Files:**
- Modify: `ReTran.App/MauiProgram.cs` (DI registrations)
- Create: `ReTran.App/Components/Shared/SetupModal.razor`
- Modify: `ReTran.App/Components/Layout/MainLayout.razor:74` (render `<SetupModal />` cạnh `<UpdateModal />`)
- Modify: `ReTran.App/Components/Pages/Home.razor` (~dòng 473 — guard Start)
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor` (nút "Chạy lại setup")
- Modify: `ReTran.App/Services/I18nStrings.cs` (keys Vi + En)
- Test: `ReTran.Tests/Setup/SetupWiringTests.cs`

**Interfaces:**
- Consumes: `SetupCoordinator` (Task 8), `OcrCatalogDisplay` (Task 9), `IVideoAdapterSource`, `UvSyncRunner`, `IProcessRunner`, `CoreProcessClient`/`ICorePeer` (có sẵn), `AppSettingsService` (có sẵn).
- Produces: adapter `SetupCoreBridge : ISetupCoreBridge` bọc `CoreProcessClient` (`CallAsync` forward; `RestartAsync` = `DisposeAsync` + `StartCoreAsync`); DI singleton `SetupCoordinator`.

DI registrations trong `MauiProgram.cs` (cạnh block tray/update có sẵn): `IProcessRunner`→implementation thật, `UvSyncRunner`, `IVideoAdapterSource`→`VideoAdapterSource`, `ISetupCoreBridge`→adapter, `SetupCoordinator` (singleton, ctor nhận các deps trên + `closeApp: _ => IHostApplicationLifetime.StopApplication()`).

`SetupModal.razor` — theo đúng pattern `UpdateModal.razor` (inject coordinator, subscribe `Changed` với `InvokeAsync(StateHasChanged)`, `IDisposable`):

- **Tự trigger lúc app start** (điểm entry duy nhất của wizard): trong `OnInitialized`, nếu `SetupState.Load()` + `SetupState.NeedsSetup(state, SetupPaths.VenvPythonExe, SetupPaths.ModelsDir)` = true và chưa từng start → `_ = Coordinator.StartAsync()` (guard flag local chống gọi trùng). State hoàn tất + file đủ → không làm gì, không render modal (đường rẻ — spec §5.9). Logic `NeedsSetup` đã được test ở Task 5; code-behind Razor không test riêng (đúng style `UpdateModal`).
- Render khi `!Coordinator.IsComplete` — full-screen `fixed inset-0 z-50` như UpdateModal.
- Screen theo `Step`:
  - `Checking`: label đang kiểm tra + notice (nếu có).
  - `ChoosingLanguage`: danh sách ngôn ngữ từ `OcrCatalogDisplay` (mỗi mục: tên ngôn ngữ, model sẽ dùng, dung lượng; tổng dung lượng trước nút xác nhận) + tuỳ chọn "Tự động" (= cặp default); preselect theo `AppSettingsService.Load().Src` khi là code hợp lệ, không thì "Tự động". Button xác nhận → `ContinueWithLanguageAsync(code)`.
  - Các bước chạy (`VerifyingFiles`…`Applying`): tên bước hiện tại + progress bar indeterminate (spec cho phép fallback indeterminate).
  - `Failed`: `Error` + đúng 2 nút — **Thử lại** → `RetryAsync()`, **Bỏ qua** → `Skip()`.

Guard pipeline trong `Home.razor`: trước `await Pipeline.StartAsync(...)` (~dòng 473) — nếu `!setupCoordinator.IsComplete` thì return (modal đang che màn hình; không start pipeline — spec §3.1).

`SettingsPanel.razor`: nút "Chạy lại setup" (hiện khi `IsComplete`) → `StartAsync(force: true)` — hoàn tất gần tức thì khi mọi thứ sẵn sàng (smoke #8).

i18n keys (cả 2 bảng Vi + En trong `I18nStrings.cs`): `setup.title`, `setup.checking`, `setup.chooseLang`, `setup.auto`, `setup.totalSize`, `setup.confirm`, `setup.step.{verifyingFiles,syncing,verifyingSidecar,downloadingModels,applying}`, `setup.error.retry`, `setup.error.skip`, `setup.notice.amdCpu`, `setup.notice.noGpu`, `setup.notice.oldDriver`, `setup.rerun`.

- [ ] **Step 1: Viết failing test** — `SetupWiringTests`: (a) adapter `SetupCoreBridge.RestartAsync` gọi dispose rồi start trên `FakeCorePeer`; (b) `CallAsync` forward nguyên vẹn method+params; (c) guard Home: extract logic guard thành `internal static bool CanStartPipeline(bool setupComplete)` trong file nhỏ `ReTran.App/Services/Setup/PipelineGate.cs` + test true/false.

- [ ] **Step 2: Chạy test verify fail** → FAIL (compile).

- [ ] **Step 3: Implement** DI + adapter + `PipelineGate` + modal + 2 điểm UI + i18n. Modal KHÔNG gọi uv/python trực tiếp — mọi thứ qua coordinator (spec §5.11).

- [ ] **Step 4: Verify build + suite**

Run: `dotnet build "ReTran Project.slnx"` rồi `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"`
Expected: cả hai PASS, 0 error.

- [ ] **Step 5: Commit**

```bash
git add ReTran.App/MauiProgram.cs ReTran.App/Components/Shared/SetupModal.razor ReTran.App/Components/Layout/MainLayout.razor ReTran.App/Components/Pages/Home.razor ReTran.App/Components/Shared/SettingsPanel.razor ReTran.App/Services/I18nStrings.cs ReTran.App/Services/Setup/PipelineGate.cs ReTran.Tests/Setup/SetupWiringTests.cs
git commit -m "feat(setup): wire SetupCoordinator, add SetupModal, gate pipeline start, rerun button"
```

---

### Task 11: Khoá hành vi "updater không xoá" + verify zip trong CI

**Files:**
- Modify: `ReTran.Updater/ReTran.Updater.csproj` (thêm InternalsVisibleTo)
- Modify: `ReTran.Updater/UpdateStager.cs:110` (comment trên `CopyDirectory`)
- Create: `ReTran.Tests/UpdateStagerTests.cs`
- Modify: `.github/workflows/release.yml` (bước "Verify executables in publish output", dòng ~86-98)

**Interfaces:**
- Consumes: `UpdateStager.CopyDirectory(string stagingDir, string appDir)` (internal static, có sẵn).
- Produces: không — task bảo vệ.

- [ ] **Step 1: Thêm InternalsVisibleTo vào `ReTran.Updater.csproj`** — cùng syntax với `ReTran.App.csproj:74-76`: `<ItemGroup><InternalsVisibleTo Include="ReTran.Tests" /></ItemGroup>`.

- [ ] **Step 2: Viết failing test** (`ReTran.Tests/UpdateStagerTests.cs`):

```csharp
[Fact]
public void CopyDirectory_PreservesForeignFilesAndDirectories_InAppDir()
{
    // appDir chứa: .venv\Scripts\python.exe, models\official_models\m\f.txt, extra.txt
    // staging chứa: ReTran.App.exe (file mới)
    // CopyDirectory(staging, appDir)
    // Assert: 3 mục "lạ" còn nguyên nội dung + ReTran.App.exe đã được copy vào appDir
}
```

- [ ] **Step 3: Chạy test** → Expected: PASS ngay (hành vi hiện tại đã đúng — test này là hàng rào, spec §5.2). Nếu FAIL thì DỪNG và báo controller — đó là regression thật.

- [ ] **Step 4: Thêm comment trên `CopyDirectory`** (song ngữ): "Overwrite-only by design — never delete files in appDir: user state (.venv, models) must survive updates (spec 2026-11-03-first-run-setup-wizard §5.2). Chỉ ghi đè theo thiết kế — không bao giờ xoá file trong appDir."

- [ ] **Step 5: Sửa `release.yml` — verify zip + ký uv.exe**

Trong block "Verify executables in publish output" thêm 2 kiểm tra theo đúng style có sẵn:

```powershell
if (-not (Test-Path "${{ env.OUTPUT_DIR }}/runtimes/win-x64/uv.exe")) { throw "uv.exe missing from publish output" }
if (-not (Test-Path "${{ env.OUTPUT_DIR }}/ReTran.OCR/pyproject.toml")) { throw "ReTran.OCR source missing from publish output" }
```

Trong bước "Sign binaries (optional)" — lệnh `AzureSignTool sign` (dòng ~124-126) thêm `"${{ env.OUTPUT_DIR }}/runtimes/win-x64/uv.exe"` vào danh sách file ký (spec §5.12: tránh SmartScreen với binary lạ chạy lần đầu).

- [ ] **Step 6: Chạy suite + commit**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` → Expected: PASS.

```bash
git add ReTran.Updater/ReTran.Updater.csproj ReTran.Updater/UpdateStager.cs ReTran.Tests/UpdateStagerTests.cs .github/workflows/release.yml
git commit -m "test(updater): pin overwrite-only copy behavior; verify uv.exe and OCR source in release zip"
```

---

## Sau khi hết tasks — smoke thủ công (user, máy thật)

Chạy checklist 9 mục trong spec §9 trên máy thật trước khi coi tính năng xong — đặc biệt mục 1 (máy sạch/VM), 4 (giết mạng giữa sync), 6 (update giữ venv/models) và 9 (migration model dev). Số đo thực tế (dung lượng sync, driver tối thiểu, host model) ghi ngược vào spec §10.
