# Plan: phơi tham số PaddleOCR + bỏ panel mode + bỏ cỡ chữ thủ công

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sửa lỗi node-parent đang chặn mọi `ocr.models.*`, phơi 27 tham số engine PaddleOCR lên Settings (schema từ sidecar, live/reload đúng ngữ nghĩa), gửi `ocrFps`/`captureFps` từ App, và xoá hẳn panel mode + cỡ chữ thủ công.

**Architecture:** Sidecar Python là nguồn sự thật cho danh sách + validate tham số (`ocr.config.schema` / `ocr.config.set`, replace toàn map, `deferReload` do Core ép theo `IsRunning`). Core proxy nguyên văn + lọc key lạ trong `core.start`. App render động từ schema, lưu non-default vào `settings.json` (`ocrEngine`), gửi kèm `core.start`. Overlay chỉ còn subtitle, cỡ chữ = công thức tự động.

**Tech Stack:** .NET 10 (xUnit v3 / MTP), Python 3.13 + uv + pytest (paddleocr 3.7), Blazor Razor + i18n dictionary, JSON-RPC stdio + JsonNode.

**Spec:** `Docs/superpowers/specs/2026-10-26-ocr-engine-settings-overlay-simplify-design.md`

## Global Constraints

- **Mặc định không đổi hành vi:** default schema phải trùng giá trị PaddleX 3.7 đang chạy (`64/min`, `0.3/0.6/1.5`, `0.0`, batch `6`, threads `10`, 3 cờ orientation `false`). Riêng oneDNN **tắt** (đổi 2026-11-01 khi kiểm chứng Task 10: `enable_mkldnn=true` + `device=cpu` làm sidecar chết vì bug Paddle#77340; GPU không bị ảnh hưởng).
- **27 phơi + 10 ẩn = 37** tham số `PaddleOCR.__init__` (26 tên + 11 kwarg chung). 10 ẩn: `lang`, `ocr_version`, 5 `*_model_dir`, `paddlex_config`, `text_detection_model_name`, `text_recognition_model_name`.
- **`ocr.config.set` là replace:** key vắng/`null` = default; `{}` = toàn mặc định; validate toàn bộ trước khi áp (atomic); key lạ/ẩn → -32602 `unsupported param: <key>`; sai kiểu/khoảng → -32602 `invalid <key>: <lý do>`.
- **`core.start` lọc key lạ** (kể cả key ẩn) bằng schema, ghi `warn:`, không fail; `ocr.params` vắng mặt = không gọi set.
- **DeepClone tại mọi biên wire:** không node JsonNode nào bị gắn vào 2 parent (response→serialize, settings→request).
- **Bỏ hẳn:** panel mode, `OverlayMode`/`OverlayCorner`/`OverlayViewOptions`/`IsPanelLine`, cỡ chữ thủ công (`OverlayFontScale`, `core.set_font_scale`, `fontScale` trên wire). File `settings.json` cũ vẫn đọc được (key lạ bị bỏ qua).
- **Không đụng thay đổi uncommitted khác:** repo đang có line-ending churn + UI v2 work; chỉ sửa file trong plan.
- **Lệnh kiểm chứng:** `dotnet build "ReTran Project.slnx"`; `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` (filter nhanh: `-- --filter-class "<Class>"`); Python: `uv run pytest tests -v` chạy trong `ReTran.OCR/`.
- **Doc comments:** C# public API bilingual (EN + VI) theo AGENTS.md; Python Google-style docstring.
- **Commit nhỏ theo từng task**; không gộp task.

## Review Focus

Năm lớp lỗi spec ngụ ý nhưng không bài test nào của spec tự phủ — mỗi dòng có test ở task sở hữu:

1. **`settings.json` bị sửa tay (key lạ / giá trị sai) phải không làm chết `core.start`** — key lạ bị lọc + warn (Task 4), key sai giá trị trả -32602 hiển thị lên UI, không treo app (Task 5).
2. **JsonNode tái sử dụng ở hai parent** — response→`SerializeResponse` (Task 1: 3 biên) và settings→`core.start` gọi lần hai (Task 5: StartAsync gọi 2 lần không ném).
3. **Xoá một key / "Khôi phục mặc định" phải thực sự reset sidecar đang sống** — replace semantics (Task 2: set subset → default; Task 6/7: lưu map rỗng gửi `{}`).
4. **Đổi tham số reload khi pipeline đang chạy không được reset predictor giữa phiên** — `deferReload` (Task 3: hook không chạy; Task 4: Core ép `deferReload = IsRunning`).
5. **Sidecar chết/venv thiếu** — Settings hiện "không lấy được schema" không crash (Task 7 catch; controller ném nguyên văn — Task 5 test), `core.start` vẫn -32603 như cũ (không regression).

---

### Task 1: Sửa lỗi node-parent ở 3 biên wire (bắt buộc trước mọi task RPC khác)

**Files:**
- Modify: `ReTran.Core/OcrClient/SidecarProcess.cs` (~L68–100)
- Modify: `ReTran.Core/JsonRpc/JsonRpcServer.cs` (~L178–197)
- Modify: `ReTran.App/Core/JsonRpcClient.cs` (~L124–146)
- Test: `ReTran.Tests/SidecarProcessTests.cs`, `ReTran.Tests/JsonRpcServerTests.cs`, `ReTran.Tests/JsonRpcClientTests.cs`

**Interfaces:**
- Produces: `internal static bool SidecarProcess.TryRouteResponseLine(string line, Action<string, JsonNode?> deliver, Action<string, Exception> fail)` — parse 1 dòng response, route; result giao ra là **DeepClone đã tách parent**; trả `false` khi dòng không phải response hợp lệ (blank/notification).
- Produces: `JsonRpcServer.RouteResponse` đổi `private` → `internal static` (giữ signature `(TaskCompletionSource<JsonNode?> waiter, JsonNode response)`).

- [ ] **Step 1: Viết test fail cho SidecarProcess** (thêm vào `SidecarProcessTests`)

```csharp
[Fact]
public void ResponseLine_DeliversDetachedClone_SerializableResponse()
{
    JsonNode? delivered = null;
    string? deliveredId = null;
    bool handled = SidecarProcess.TryRouteResponseLine(
        "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"result\":{\"params\":[\"a\"]}}",
        (id, result) => { deliveredId = id; delivered = result; },
        (_, ex) => throw ex);

    Assert.True(handled);
    Assert.Equal("7", deliveredId);
    // Trước fix: node còn parent (con của envelope) → dòng dưới ném
    // InvalidOperationException "The node already has a parent."
    string line = JsonRpc.SerializeResponse(new JsonRpcResponse(deliveredId!, delivered, null));
    Assert.Contains("\"params\":[\"a\"]", line);
}
```

- [ ] **Step 2: Chạy test xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-method "*ResponseLine_DeliversDetachedClone*"`
Expected: FAIL — chưa có `TryRouteResponseLine` (compile error) hoặc (sau khi trích hàm nhưng chưa clone) `InvalidOperationException: The node already has a parent.`

- [ ] **Step 3: Viết test fail cho JsonRpcServer** (thêm vào `JsonRpcServerTests`)

```csharp
[Fact]
public async Task RouteResponse_DetachesResultFromEnvelope()
{
    var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
    JsonRpcServer.RouteResponse(waiter,
        JsonNode.Parse("""{"jsonrpc":"2.0","id":"1","result":{"a":1}}""")!);

    JsonNode? result = await waiter.Task;
    string line = JsonRpc.SerializeResponse(new JsonRpcResponse("1", result, null));
    Assert.Contains("\"a\":1", line);
}
```

- [ ] **Step 4: Viết test fail cho JsonRpcClient** (thêm vào `JsonRpcClientTests`, theo đúng mẫu pipe của `CallAsync_WritesRequest_AndResolvesOnMatchingResponse`: gửi response `{"jsonrpc":"2.0","id":"1","result":{"a":1}}`)

```csharp
[Fact]
public async Task CallAsync_ResultNode_CanBeReparented()
{
    // ... pipe setup y hệt test happy-path ...
    JsonNode? result = await callTask;
    // Trước fix: node còn parent → ném "The node already has a parent."
    var envelope = new JsonObject { ["copy"] = result };
    Assert.Equal(1, envelope["copy"]!["a"]!.GetValue<int>());
}
```

- [ ] **Step 5: Chạy 3 test xác nhận fail**

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-method "*Detached*|*Reparented*"`
Expected: FAIL (compile + node-parent).

- [ ] **Step 6: Implement**

`SidecarProcess.cs` — trích hàm từ `StartReadLoop` (giữ nguyên read loop gọi `TryRouteResponseLine(line, Deliver, Fail)`):

```csharp
/// <summary>Parses and routes one response line; the result is a detached clone (never a child of the parsed envelope).</summary>
internal static bool TryRouteResponseLine(string line, Action<string, JsonNode?> deliver, Action<string, Exception> fail)
{
    if (Rpc.ParseLine(line) is not { } n || n["id"]?.GetValue<string>() is not { } id)
        return false;
    if (n["result"] is { } r) { deliver(id, r.DeepClone()); return true; }
    if (n["error"] is { } err)
    {
        int code = err["code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : RpcErrorCodes.InternalError;
        string msg = err["message"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : "sidecar error";
        fail(id, new SidecarRpcException(code, msg));
        return true;
    }
    return false;
}
```

`JsonRpcServer.cs`: `private static` → `internal static`, và `waiter.TrySetResult(response["result"]?.DeepClone());`
`JsonRpcClient.cs`: `tcs.TrySetResult(node["result"]?.DeepClone());`

- [ ] **Step 7: Chạy test xác nhận pass** (mỗi class một lệnh filter)

Run: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "SidecarProcessTests"`, rồi `... --filter-class "JsonRpcServerTests"`, rồi `... --filter-class "JsonRpcClientTests"`
Expected: PASS toàn bộ (SidecarProcessTests vẫn skip phần cần venv nếu thiếu).

- [ ] **Step 8: Commit**

```bash
git add ReTran.Core/OcrClient/SidecarProcess.cs ReTran.Core/JsonRpc/JsonRpcServer.cs ReTran.App/Core/JsonRpcClient.cs ReTran.Tests/SidecarProcessTests.cs ReTran.Tests/JsonRpcServerTests.cs ReTran.Tests/JsonRpcClientTests.cs
git commit -m "fix(rpc): detach result nodes from parsed envelopes at all three wire boundaries"
```

---

### Task 2: Python — module `ocr/config.py` (schema + validate + replace + defer)

**Files:**
- Create: `ReTran.OCR/src/retran_core/ocr/config.py`
- Test: `ReTran.OCR/tests/test_ocr_config.py`

**Interfaces:**
- Produces: `ParamSpec` dataclass `(key, group, type, default, reload_required, values=None, min=None, max=None, step=None, placeholder=None)` với `to_dict()` trả đúng camelCase: `{"key","group","type","default","reloadRequired","values","min","max","step","placeholder"}`.
- Produces: `PARAMS: tuple[ParamSpec, ...]` (27), `HIDDEN_KEYS: frozenset[str]` (10), `schema() -> dict` (`{"params": [...]}`).
- Produces: `class OcrConfig(on_reload: Callable[[], None] | None = None)`:
  - `get(key: str) -> Any` — giá trị hiệu dụng (override hoặc default).
  - `set(raw_params: Any, *, defer_reload: bool) -> dict` — trả `{"live": [...], "deferred": [...], "reloadRequired": bool}`.
  - `constructor_kwargs() -> dict` — 20 tham số reload đã resolve (bỏ giá trị `None`).
  - `predict_kwargs() -> dict` — 7 tham số live đã resolve.
  - thuộc tính `overrides: dict[str, Any]` (chỉ key user đặt).

- [ ] **Step 1: Tạo `test_ocr_config.py` với test schema + completeness (fail vì chưa có module)**

```python
"""Unit tests for the OCR engine config surface (schema / replace / defer). Offline: no paddle import."""
from __future__ import annotations

import importlib.util

import pytest

from retran_core.jsonrpc import RpcInvalidParams
from retran_core.ocr import config as cfg

pytestmark = pytest.mark.skipif(
    importlib.util.find_spec("paddleocr") is None,
    reason="paddleocr not installed (uv sync) -> skip",
)

# 11 kwarg chung của PaddleOCR 3.7 (paddleocr/_common_args.py: default_vals) + paddlex_config.
# Test khi paddleocr đổi bản: test_schema_covers_real_signature sẽ bắt lệch tên.
COMMON_ARGS = (
    "device", "engine", "engine_config", "enable_hpi", "use_tensorrt", "precision",
    "enable_mkldnn", "mkldnn_cache_capacity", "cpu_threads", "enable_cinn", "paddlex_config",
)


def test_schema_shape():
    out = cfg.schema()
    assert [p["key"] for p in out["params"]] == [p.key for p in cfg.PARAMS]
    assert len(cfg.PARAMS) == 27
    assert len(cfg.HIDDEN_KEYS) == 10
    assert {p["group"] for p in out["params"]} == {"device", "models", "preprocess", "det", "rec"}


def test_retran_defaults_are_pinned():
    by_key = {p.key: p.default for p in cfg.PARAMS}
    assert by_key["use_doc_orientation_classify"] is False
    assert by_key["use_doc_unwarping"] is False
    assert by_key["use_textline_orientation"] is False
    assert by_key["text_det_limit_side_len"] == 64
    assert by_key["text_det_limit_type"] == "min"
    assert by_key["text_det_thresh"] == 0.3
    assert by_key["text_det_box_thresh"] == 0.6
    assert by_key["text_det_unclip_ratio"] == 1.5
    assert by_key["text_rec_score_thresh"] == 0.0
    assert by_key["text_recognition_batch_size"] == 6
    # oneDNN tắt mặc định vì bug Paddle#77340 trên CPU (Task 10 ruling).
    assert by_key["enable_mkldnn"] is False
    assert by_key["cpu_threads"] == 10


def test_schema_covers_real_signature():
    import inspect
    from paddleocr import PaddleOCR

    named = {
        n for n in inspect.signature(PaddleOCR.__init__).parameters if n not in ("self", "kwargs")
    }
    exposed = {p.key for p in cfg.PARAMS}
    assert named | set(COMMON_ARGS) <= exposed | cfg.HIDDEN_KEYS
    # khẳng định chiều ngược để bắt lệch khi schema tự thêm key không còn tồn tại
    assert exposed <= named | set(COMMON_ARGS)
```

Và các test validate/replace/defer (viết cùng lúc, cũng phải fail ở Step 2):

```python
def test_set_is_replace():
    c = cfg.OcrConfig()
    c.set({"text_det_thresh": 0.4, "cpu_threads": 8}, defer_reload=False)
    out = c.set({"text_det_thresh": 0.4}, defer_reload=False)
    assert c.get("cpu_threads") == 10          # key vắng -> default
    assert out["reloadRequired"] is True        # cpu_threads đổi về default
    assert "cpu_threads" in out["live"]


def test_empty_map_resets_everything():
    c = cfg.OcrConfig()
    c.set({"text_det_thresh": 0.4, "device": "cpu"}, defer_reload=False)
    c.set({}, defer_reload=False)
    assert c.overrides == {}
    assert c.get("text_det_thresh") == 0.3 and c.get("device") is None


def test_set_is_atomic():
    c = cfg.OcrConfig()
    c.set({"text_det_thresh": 0.4}, defer_reload=False)
    with pytest.raises(RpcInvalidParams):
        c.set({"text_det_thresh": 0.9, "bogus": 1}, defer_reload=False)
    assert c.get("text_det_thresh") == 0.4      # không nửa cũ nửa mới


def test_unknown_and_hidden_keys_rejected():
    for key in ("bogus", "paddlex_config", "text_recognition_model_dir", "lang"):
        with pytest.raises(RpcInvalidParams, match="unsupported param"):
            cfg.OcrConfig().set({key: 1}, defer_reload=False)


def test_defer_reload_skips_hook():
    calls = []
    c = cfg.OcrConfig(on_reload=lambda: calls.append(1))
    out = c.set({"device": "gpu:0"}, defer_reload=True)
    assert out == {"live": [], "deferred": ["device"], "reloadRequired": True}
    assert calls == []
    out = c.set({"device": "gpu:0"}, defer_reload=False)  # cùng giá trị: không đổi
    assert out["live"] == [] and calls == []
    out = c.set({"device": "cpu"}, defer_reload=False)
    assert out["live"] == ["device"] and calls == [1]


def test_predict_kwargs_have_all_live_defaults():
    assert cfg.OcrConfig().predict_kwargs() == {
        "text_det_limit_side_len": 64, "text_det_limit_type": "min", "text_det_thresh": 0.3,
        "text_det_box_thresh": 0.6, "text_det_unclip_ratio": 1.5,
        "text_rec_score_thresh": 0.0, "return_word_box": False,
    }
    c = cfg.OcrConfig()
    c.set({"return_word_box": True, "device": "cpu"}, defer_reload=True)
    assert c.predict_kwargs()["return_word_box"] is True
    assert "device" not in c.predict_kwargs()                 # reload group không đi predict
```

- [ ] **Step 2: Chạy xác nhận fail** — `uv run pytest tests/test_ocr_config.py -v` trong `ReTran.OCR/`. Expected: FAIL `ModuleNotFoundError: retran_core.ocr.config`.

- [ ] **Step 3: Implement `config.py`**

Tạo `PARAMS` bằng cách **transcribe bảng spec §6.1–§6.5** (key/type/default/group/live-reload/min/max/step/values đúng từng dòng; `reload_required = True` cho §6.1–6.3, `False` cho §6.4–6.5). `HIDDEN_KEYS` = 10 key ở spec §6.7. Ví dụ 3 entry để chốt shape:

```python
ParamSpec("device", "device", "string", None, True, placeholder="auto | cpu | gpu:0"),
ParamSpec("engine", "device", "enum", None, True, values=("paddle", "paddle_static", "paddle_dynamic", "transformers", "onnxruntime")),
ParamSpec("text_det_thresh", "det", "float", 0.3, False, min=0.0, max=1.0, step=0.05),
```

`OcrConfig.set` (thuật toán, không cần viết từng dòng trong plan):

1. `raw_params` phải là `dict` → nếu không: `raise RpcInvalidParams("'params' must be an object")`.
2. Chuẩn hoá input: bỏ key có value `None`; validate **mọi key còn lại** trước khi thay đổi state (atomic) theo `type`:
   `bool` → `isinstance(v, bool)`; `int` → `isinstance(v, int) and not isinstance(v, bool)`; `float` → số thực hữu hạn; `string` → `str`; `enum` → `str in values`; `json` → `dict`; `shape` → `list` 4 phần tử int dương. Ngoài `min/max` → `invalid <key>: out of range [min, max]`. Lỗi khác → `invalid <key>: expected <type>`.
   Key không có trong `PARAMS` (kể cả `HIDDEN_KEYS`) → `unsupported param: <key>`.
3. Tính `changed` = các key có giá trị hiệu dụng (`override or default`) khác trước/sau (so sánh `==`, đủ cho các kiểu phẳng + list/dict JSON).
4. Gán `self._overrides = input` (replace).
5. `reload_changed = [k for k in changed if spec(k).reload_required]`; nếu có và **không** `defer_reload`: gọi `self._on_reload()` nếu có.
6. Trả `{"live": [k for k in changed if not deferred], "deferred": reload_changed if defer_reload else [], "reloadRequired": bool(reload_changed)}` — thứ tự giữ theo thứ tự `PARAMS`.

`constructor_kwargs()`: `{p.key: value for p in PARAMS if p.reload_required and (value := self.get(p.key)) is not None}`.
`predict_kwargs()`: tương tự cho `not p.reload_required`.

- [ ] **Step 4: Chạy toàn bộ test mới** — `uv run pytest tests/test_ocr_config.py -v`. Expected: PASS (riêng `test_schema_covers_real_signature` cần paddleocr — có trong venv, import chậm lần đầu).

- [ ] **Step 5: Commit**

```bash
git add ReTran.OCR/src/retran_core/ocr/config.py ReTran.OCR/tests/test_ocr_config.py
git commit -m "feat(ocr): sidecar config schema module with replace/defer semantics"
```

---

### Task 3: Python — nối config vào engine + đăng ký `ocr.config.*`

**Files:**
- Modify: `ReTran.OCR/src/retran_core/ocr/config.py` (thêm `make_config_handlers`)
- Modify: `ReTran.OCR/src/retran_core/ocr/paddle_engine.py`
- Modify: `ReTran.OCR/src/retran_core/__main__.py`
- Test: `ReTran.OCR/tests/test_ocr_config.py`

**Interfaces:**
- Produces: `make_config_handlers(config: OcrConfig) -> dict[str, Handler]` với `ocr.config.schema` (bỏ qua params) và `ocr.config.set` (`{params: {...}, deferReload?: bool}`, mặc định `deferReload=false`).
- Produces: `PaddleOcrEngine.config -> OcrConfig` (instance do engine sở hữu, `on_reload=self._reset_predictor`).
- Consumes: `OcrConfig` từ Task 2.

- [ ] **Step 1: Viết test fail cho engine + handlers** (thêm vào `test_ocr_config.py`)

```python
class RecordingPaddleOCR:
    instances: list["RecordingPaddleOCR"] = []
    ctor_kwargs: dict = {}
    predict_kwargs: dict = {}

    def __init__(self, **kwargs):
        RecordingPaddleOCR.instances.append(self)
        RecordingPaddleOCR.ctor_kwargs = kwargs

    def predict(self, image, **kwargs):
        RecordingPaddleOCR.predict_kwargs = kwargs
        return [{"dt_polys": [[[0, 0], [1, 0], [1, 1], [0, 1]]], "rec_texts": ["X"], "rec_scores": [0.5]}]


@pytest.fixture
def fake_paddle(monkeypatch):
    import sys, types
    fake = types.ModuleType("paddleocr")
    fake.PaddleOCR = RecordingPaddleOCR
    monkeypatch.setitem(sys.modules, "paddleocr", fake)
    RecordingPaddleOCR.instances = []
    yield RecordingPaddleOCR


def test_engine_passes_schema_defaults_to_constructor(fake_paddle):
    from retran_core.ocr.paddle_engine import PaddleOcrEngine
    import numpy as np
    engine = PaddleOcrEngine("PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec")
    engine.spot(np.zeros((4, 4, 3), dtype=np.uint8))
    kw = fake_paddle.ctor_kwargs
    assert kw["text_detection_model_name"] == "PP-OCRv6_tiny_det"
    assert kw["use_doc_orientation_classify"] is False
    assert kw["text_recognition_batch_size"] == 6        # reload group đi constructor
    assert "text_det_thresh" not in kw                   # live group không đi constructor
    assert fake_paddle.predict_kwargs["text_det_thresh"] == 0.3
    assert fake_paddle.predict_kwargs["text_det_limit_side_len"] == 64


def test_reload_param_resets_predictor_and_applies_next_spot(fake_paddle):
    from retran_core.ocr.paddle_engine import PaddleOcrEngine
    import numpy as np
    engine = PaddleOcrEngine("PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec")
    engine.spot(np.zeros((4, 4, 3), dtype=np.uint8))
    assert len(fake_paddle.instances) == 1
    engine.config.set({"text_recognition_batch_size": 12}, defer_reload=True)
    engine.spot(np.zeros((4, 4, 3), dtype=np.uint8))
    assert len(fake_paddle.instances) == 1          # defer: không nạp lại
    engine.config.set({"text_recognition_batch_size": 12}, defer_reload=False)  # không đổi -> no-op
    engine.config.set({"cpu_threads": 4}, defer_reload=False)
    engine.spot(np.zeros((4, 4, 3), dtype=np.uint8))
    assert len(fake_paddle.instances) == 2
    assert fake_paddle.ctor_kwargs["cpu_threads"] == 4


def test_handlers_schema_and_set():
    from retran_core.jsonrpc import handle_line
    from retran_core.ocr.config import make_config_handlers
    c = cfg.OcrConfig()
    handlers = make_config_handlers(c)

    line = handle_line('{"jsonrpc":"2.0","id":"1","method":"ocr.config.schema"}', handlers)
    assert '"params"' in line
    line = handle_line(
        '{"jsonrpc":"2.0","id":"2","method":"ocr.config.set","params":{"params":{"text_det_thresh":0.35}}}',
        handlers)
    assert '"live":["text_det_thresh"]' in line.replace(" ", "")
    assert c.get("text_det_thresh") == 0.35
    line = handle_line(
        '{"jsonrpc":"2.0","id":"3","method":"ocr.config.set","params":{"params":{"bogus":1}}}', handlers)
    assert '"code":-32602' in line.replace(" ", "")
```

- [ ] **Step 2: Chạy xác nhận fail** — `uv run pytest tests/test_ocr_config.py -v`. Expected: FAIL (`PaddleOcrEngine` chưa có `.config`, chưa có handler).

- [ ] **Step 3: Implement**

`paddle_engine.py`:

```python
def __init__(self, det_model: str, rec_model: str) -> None:
    self._det_model = det_model
    self._rec_model = rec_model
    self._ocr: Any = None
    self._config = OcrConfig(on_reload=self._reset_predictor)

@property
def config(self) -> OcrConfig: return self._config

def _reset_predictor(self) -> None: self._ocr = None
```

`_create()`: giữ pin 3 cờ + tên model như cũ, rồi `kwargs.update(self._config.constructor_kwargs())` trước khi gọi `PaddleOCR(**kwargs)`.
`spot()`: nhánh v3 gọi `self._ocr.predict(image_bgr, **self._config.predict_kwargs())` (nhánh v2 giữ nguyên, không kwargs).
Cập nhật docstring constructor/`spot`/class theo hành vi mới.

`config.py` thêm:

```python
def make_config_handlers(config: OcrConfig) -> dict[str, Handler]:
    def set_config(params: Any) -> dict:
        if not isinstance(params, dict):
            raise RpcInvalidParams("params must be an object ({params, deferReload?})")
        raw = params.get("params")
        if not isinstance(raw, dict):
            raise RpcInvalidParams("'params' must be an object")
        defer = params.get("deferReload", False)
        return config.set(raw, defer_reload=bool(defer))
    return {"ocr.config.schema": lambda _p: schema(), "ocr.config.set": set_config}
```

`__main__.py`: `from .ocr.config import make_config_handlers`; sau `handlers.update(make_model_handlers(manager))` thêm `handlers.update(make_config_handlers(engine.config))`.

- [ ] **Step 4: Chạy pass** — `uv run pytest tests/test_ocr_config.py tests/test_ocr_models.py tests/test_ocr_handlers.py tests/test_jsonrpc.py -v`. Expected: PASS toàn bộ (test cũ không được vỡ: engine vẫn nhận kwargs cũ + thêm default mới).

- [ ] **Step 5: Commit**

```bash
git add ReTran.OCR/src/retran_core/ocr/config.py ReTran.OCR/src/retran_core/ocr/paddle_engine.py ReTran.OCR/src/retran_core/__main__.py ReTran.OCR/tests/test_ocr_config.py
git commit -m "feat(ocr): wire config into engine and register ocr.config.* handlers"
```

---

### Task 4: Core — proxy `ocr.config.*` + `core.start` gửi/lọc params + ocrFps/captureFps

**Files:**
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (`SetOcrConfigAsync`, `OcrConfigSchemaAsync`, `StartAsync`, `ParseStart`, `StartSpec`)
- Modify: `ReTran.Core/Program.cs` (~L94, đăng ký 2 method mới)
- Test: `ReTran.Tests/OcrConfigRpcTests.cs` (mới), `ReTran.Tests/PipelineHostTests.cs`

**Interfaces:**
- Produces: `PipelineHost.OcrConfigSchemaAsync(CancellationToken ct) -> Task<JsonNode?>` — forward `ocr.config.schema` (sidecar null → `RpcMethodException -32603` qua `ForwardToSidecarAsync`).
- Produces: `PipelineHost.SetOcrConfigAsync(JsonNode? @params, CancellationToken ct) -> Task<JsonNode?>` — ép `deferReload = IsRunning` trong bản clone của params rồi forward.
- Produces: `StartSpec` thêm field cuối `JsonObject? OcrParams`; `ParseStart` đọc `ocr.params` (không phải object → -32602 `'ocr.params' must be an object.`).
- Consumes: sidecar `ocr.config.*` (Task 3).

- [ ] **Step 1: Viết test fail `OcrConfigRpcTests.cs`**

```csharp
public class OcrConfigRpcTests
{
    private sealed class RecordingRpc : ISidecarRpc
    {
        public JsonNode NextResult { get; } = new JsonObject { ["params"] = new JsonArray() };
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            Calls.Add((method, @params));
            return Task.FromResult<JsonNode?>(NextResult);
        }
    }

    private static PipelineHost CreateHost(ISidecarRpc? rpc) =>
        new(rpc, new TranslatorRegistry(), new JsonRpcServer(), new StringWriter());

    [Fact]
    public async Task Schema_ForwardsVerbatim()
    {
        var rpc = new RecordingRpc();
        await using var host = CreateHost(rpc);
        await host.OcrConfigSchemaAsync(CancellationToken.None);
        Assert.Equal("ocr.config.schema", Assert.Single(rpc.Calls).Method);
    }

    [Fact]
    public async Task Set_ForcesDeferReloadFalse_WhenIdle_AndDoesNotMutateCallerNode()
    {
        var rpc = new RecordingRpc();
        await using var host = CreateHost(rpc);
        JsonNode callerParams = new JsonObject { ["params"] = new JsonObject { ["device"] = "cpu" } };
        await host.SetOcrConfigAsync(callerParams, CancellationToken.None);

        var sent = (JsonObject)rpc.Calls.Single().Params!;
        Assert.False(sent["deferReload"]!.GetValue<bool>());
        // caller giữ nguyên node gốc (không bị mutate/attach)
        Assert.Null(((JsonObject)callerParams)["deferReload"]);
    }

    [Fact]
    public async Task StackedProxy_IsNotAvailable_WhenSidecarMissing()
    {
        await using var host = CreateHost(null);
        await Assert.ThrowsAsync<RpcMethodException>(() =>
            host.OcrConfigSchemaAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Viết test fail cho `core.start` (thêm vào `PipelineHostTests`)**

Dùng `FakeSidecarRpc` sẵn có; thêm result cho `ocr.config.schema` qua `ResultsByMethod`:

```csharp
[Fact]
public async Task Start_SendsFilteredOcrParams_BeforeModelsSet()
{
    var rpc = new FakeSidecarRpc();
    rpc.ResultsByMethod["ocr.config.schema"] = JsonNode.Parse(
        """{"params":[{"key":"device","group":"device","type":"string","default":null,"reloadRequired":true},
                    {"key":"text_det_thresh","group":"det","type":"float","default":0.3,"reloadRequired":false}]}""");
    rpc.ResultsByMethod["ocr.models.list"] = JsonNode.Parse(
        """{"catalog":[{"name":"PP-OCRv6_small_det","kind":"det","installed":true},
                      {"name":"PP-OCRv6_small_rec","kind":"rec","installed":true}]}""");
    await using var host = CreateHost(rpc, /* sourceFactory + annotator như các test start hiện có */);
    await host.StartAsync(JsonNode.Parse("""
        {"source":{"kind":"screen","monitor":0},
         "translate":{"provider":"google","src":"auto","dst":"vi"},
         "ocr":{"det":"PP-OCRv6_small_det","rec":"PP-OCRv6_small_rec",
                "params":{"device":"cpu","bogus":1}}}
        """), CancellationToken.None);

    var setCall = rpc.Calls.Single(c => c.Method == "ocr.config.set");
    var sent = (JsonObject)setCall.Params!;
    Assert.True(sent["params"]!["device"]!.GetValue<string>() == "cpu");
    Assert.Null(sent["params"]!["bogus"]);           // key lạ bị lọc
    Assert.False(sent["deferReload"]!.GetValue<bool>());
    // thứ tự: config.set trước models.set
    var methods = rpc.Calls.Select(c => c.Method).ToList();
    Assert.True(methods.IndexOf("ocr.config.set") < methods.IndexOf("ocr.models.set"));

    // gọi lần hai sau core.stop không được ném node-parent
    await host.StopAsync(null, CancellationToken.None);
    await host.StartAsync(/* cùng payload */ JsonNode.Parse(/* ... */), CancellationToken.None);
}
```

(Ghi chú: tái dùng helper `CreateHost` + fake source/annotator đang có trong `PipelineHostTests`; test lần hai dùng cùng node payload — nếu `StartAsync` attach thẳng node từ payload vào object mới thì sẽ ném.)

```csharp
[Fact]
public async Task SetOcrConfig_WhileRunning_ForcesDeferReloadTrue()
{
    // start một phiên bằng harness hiện có (fake source + FakeSidecarRpc trả schema/spot)
    // rồi gọi SetOcrConfigAsync với params không có deferReload:
    await host.SetOcrConfigAsync(new JsonObject { ["params"] = new JsonObject { ["device"] = "cpu" } },
        CancellationToken.None);

    var sent = (JsonObject)rpc.Calls.Last(c => c.Method == "ocr.config.set").Params!;
    Assert.True(sent["deferReload"]!.GetValue<bool>());   // IsRunning => Core ép true
}
```

- [ ] **Step 3: Chạy xác nhận fail** — `dotnet test ... -- --filter-class "OcrConfigRpcTests"`. Expected: FAIL (chưa có method).

- [ ] **Step 4: Implement `PipelineHost`**

Thêm (cạnh các proxy `ocr.models.*`):

```csharp
private bool IsRunning { get { lock (_sync) return _session is not null; } }

public Task<JsonNode?> OcrConfigSchemaAsync(CancellationToken ct) =>
    ForwardToSidecarAsync("ocr.config.schema", null, ct);

public Task<JsonNode?> SetOcrConfigAsync(JsonNode? @params, CancellationToken ct)
{
    // Clone trước khi thêm deferReload: node params gốc thuộc về caller/server.
    JsonNode? forwarded = @params is JsonObject o
        ? new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())))
        {
            ["deferReload"] = IsRunning,
        }
        : @params;
    return ForwardToSidecarAsync("ocr.config.set", forwarded, ct);
}

/// <summary>Fetches ocr.config.schema once per call and returns the set of allowed keys.</summary>
private static async Task<HashSet<string>> LoadOcrSchemaKeysAsync(ISidecarRpc rpc, CancellationToken ct)
{
    JsonNode? schema = await rpc.CallAsync("ocr.config.schema", null, ct);
    if (schema?["params"] is not JsonArray paramsArray)
        throw new RpcMethodException(RpcErrorCodes.InternalError, "ocr.config.schema: missing 'params'.");
    var keys = new HashSet<string>(StringComparer.Ordinal);
    foreach (JsonNode? p in paramsArray)
        if (p?["key"]?.GetValue<string>() is { } key) keys.Add(key);
    return keys;
}
```

Trong `StartAsync`, ngay sau khi có `rpc` và **trước** `EnsureOcrPairInstalledAsync`:

```csharp
if (spec.OcrParams is JsonObject requested)
{
    HashSet<string> allowed = await LoadOcrSchemaKeysAsync(rpc, ct);
    var filtered = new JsonObject();
    foreach (KeyValuePair<string, JsonNode?> kv in requested)
    {
        if (allowed.Contains(kv.Key)) filtered[kv.Key] = kv.Value?.DeepClone();
        else _stderr.WriteLine($"warn: ocr.params: dropping unknown key '{kv.Key}'.");
    }
    await rpc.CallAsync("ocr.config.set",
        new JsonObject { ["params"] = filtered, ["deferReload"] = false }, ct);
}
```

`ParseStart`: trong nhánh `ocr` có sẵn, đọc thêm:

```csharp
if (ocr.TryGetPropertyValue("params", out JsonNode? paramsNode) && paramsNode is not null)
{
    ocrParams = paramsNode as JsonObject
        ?? throw new RpcMethodException(RpcErrorCodes.InvalidParams, "'ocr.params' must be an object.");
}
```

`StartSpec` thêm `JsonObject? OcrParams` cuối record; cập nhật `new StartSpec(...)`.

`Program.cs` thêm 2 dòng cạnh proxy `ocr.models.*`:

```csharp
server.Register("ocr.config.schema", _ => host.OcrConfigSchemaAsync(CancellationToken.None));
server.Register("ocr.config.set", p => host.SetOcrConfigAsync(p, CancellationToken.None));
```

- [ ] **Step 5: Chạy pass** (mỗi class một lệnh): `... -- --filter-class "OcrConfigRpcTests"` rồi `... --filter-class "PipelineHostTests"`. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ReTran.Core/Serve/PipelineHost.cs ReTran.Core/Program.cs ReTran.Tests/OcrConfigRpcTests.cs ReTran.Tests/PipelineHostTests.cs
git commit -m "feat(core): proxy ocr.config.* and thread filtered ocr params through core.start"
```

---

### Task 5: App — settings `ocrEngine`/`ocrFps`/`captureFps` + controller

**Files:**
- Modify: `ReTran.App/Services/AppSettingsService.cs`
- Modify: `ReTran.App/Core/PipelineController.cs`
- Test: `ReTran.Tests/AppSettingsServiceTests.cs`, `ReTran.Tests/PipelineControllerTests.cs`

**Interfaces:**
- Produces: `AppSettings` thêm `Dictionary<string, JsonNode?>? OcrEngine = null`, `int OcrFps = 3`, `int CaptureFps = 10` (đặt sau `OcrRec`; các field overlay giữ nguyên — Task 8/9 xoá sau).
- Produces: `PipelineController.GetOcrConfigSchemaAsync(CancellationToken ct) -> Task<IReadOnlyList<OcrParamSpec>>`; `SetOcrConfigAsync(IReadOnlyDictionary<string, JsonNode?> values, CancellationToken ct) -> Task<OcrConfigSetResult>`;
  records:

```csharp
public sealed record OcrParamSpec(string Key, string Group, string Type, JsonNode? Default, bool ReloadRequired,
    IReadOnlyList<string> Values, double? Min, double? Max, double? Step, string? Placeholder);
public sealed record OcrConfigSetResult(IReadOnlyList<string> Live, IReadOnlyList<string> Deferred, bool ReloadRequired);
```

- Consumes: Core `ocr.config.*` (Task 4).

- [ ] **Step 1: Viết test fail cho AppSettings round-trip** (thêm vào `AppSettingsServiceTests`)

```csharp
[Fact]
public void OcrEngine_RoundTrips_AndUnknownKeysSurviveLoad()
{
    string path = Path.Combine(Path.GetTempPath(), $"retran-settings-{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(path, """
            {"provider":"google","src":"auto","dst":"vi",
             "ocrEngine":{"text_det_thresh":0.35,"bogus":{"x":1}},"ocrFps":5,"captureFps":12}
            """);
        var svc = new AppSettingsService(path);
        AppSettings loaded = svc.Load();
        Assert.Equal(0.35, loaded.OcrEngine!["text_det_thresh"]!.GetValue<double>());
        Assert.NotNull(loaded.OcrEngine!["bogus"]);   // App giữ nguyên; Core lọc khi start
        Assert.Equal(5, loaded.OcrFps);
        Assert.Equal(12, loaded.CaptureFps);
        svc.Save(loaded);
        AppSettings again = svc.Load();
        Assert.Equal(0.35, again.OcrEngine!["text_det_thresh"]!.GetValue<double>());
    }
    finally { File.Delete(path); }
}

[Fact]
public void OcrEngine_Absent_MeansDefaults()
{
    // file cũ không có ocrEngine/ocrFps/captureFps -> null / 3 / 10
}
```

- [ ] **Step 2: Viết test fail cho StartAsync** (thêm vào `PipelineControllerTests`, dùng fake peer đang có)

```csharp
[Fact]
public async Task StartAsync_SendsOcrParamsAndFps_AndSurvivesSecondStart()
{
    // settings: OcrEngine = {"text_det_thresh": 0.35}, OcrFps = 5, CaptureFps = 12
    await controller.StartAsync(source, ct);
    JsonObject sent = /* peer request "core.start" params */;
    Assert.Equal(0.35, sent["ocr"]!["params"]!["text_det_thresh"]!.GetValue<double>());
    Assert.Equal(5, sent["ocrFps"]!.GetValue<int>());
    Assert.Equal(12, sent["captureFps"]!.GetValue<int>());
    Assert.Null(sent["fontScale"]);            // cỡ chữ thủ công đã bỏ (sau Task 9 vẫn đúng)

    await controller.StopAsync(ct);
    await controller.StartAsync(source, ct);   // lần hai: cùng JsonNode từ settings, không được ném
    Assert.Equal(2, /* số lần "core.start" đã gửi */);
}

[Fact]
public async Task OcrConfigSchema_PropagatesRpcError() { /* peer ném RpcException -> ném ra nguyên văn */ }
```

- [ ] **Step 3: Chạy xác nhận fail** — filter class `AppSettingsServiceTests` + `PipelineControllerTests`. Expected: FAIL.

- [ ] **Step 4: Implement**

`AppSettingsService`: thêm 3 field + cập nhật doc comment `<param>`.
`PipelineController`:

```csharp
// StartAsync: thay ["fontScale"] bằng
["ocrFps"] = settings.OcrFps,
["captureFps"] = settings.CaptureFps,
```
và trong object `ocr`:

```csharp
Dictionary<string, JsonNode?> overrides = settings.OcrEngine ?? new();
var ocr = new JsonObject { ["det"] = settings.OcrDet, ["rec"] = settings.OcrRec };
JsonObject? ocrParams = BuildOcrParams(overrides);
if (ocrParams is not null) ocr["params"] = ocrParams;   // rỗng -> bỏ hẳn property
```

và trong `@params` dùng `["ocr"] = ocr,` thay cho object dựng sẵn.

```csharp
private static JsonObject? BuildOcrParams(Dictionary<string, JsonNode?> overrides)
{
    if (overrides.Count == 0) return null;
    var o = new JsonObject();
    foreach ((string key, JsonNode? value) in overrides)
        if (value is not null) o[key] = value.DeepClone();   // tách parent trước khi attach
    return o.Count == 0 ? null : o;
}
```

`GetOcrConfigSchemaAsync`: `_peer.CallAsync("ocr.config.schema", null, ct)` → parse `params` array → `OcrParamSpec` (đọc `key/group/type/default/reloadRequired/values/min/max/step/placeholder`, chịu thiếu → null/rỗng).
`SetOcrConfigAsync`: dựng `{"params": {key: value.DeepClone()}}` (**không** gửi `deferReload` — Core tự ép) → parse kết quả `{live, deferred, reloadRequired}`.

- [ ] **Step 5: Chạy pass** — 2 class trên. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ReTran.App/Services/AppSettingsService.cs ReTran.App/Core/PipelineController.cs ReTran.Tests/AppSettingsServiceTests.cs ReTran.Tests/PipelineControllerTests.cs
git commit -m "feat(app): persist ocrEngine/ocrFps/captureFps and send them on core.start"
```

---

### Task 6: App — helper validate/parse cho UI (thuần, test được)

**Files:**
- Create: `ReTran.App/Services/OcrConfigValidation.cs`
- Test: `ReTran.Tests/OcrConfigValidationTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class OcrConfigValidation
{
    /// null = hợp lệ; "type" | "range" | "enum" | "shape" | "json" = mã lỗi (UI map i18n).
    public static string? Validate(OcrParamSpec spec, JsonNode? value);
    public static bool TryParseShape(string text, out JsonArray? shape, out string? error);   // "1,3,640,640"
    public static bool TryParseJsonObject(string text, out JsonObject? obj, out string? error);
    public static string? FormatDefault(OcrParamSpec spec);   // để hiện placeholder mặc định
}
```

- [ ] **Step 1: Viết test fail**

```csharp
public class OcrConfigValidationTests
{
    private static OcrParamSpec Spec(string type, double? min = null, double? max = null,
        string[]? values = null) =>
        new("k", "det", type, null, false, values ?? Array.Empty<string>(), min, max, null, null);

    [Theory]
    [InlineData("float", 0.0, 1.0, 0.9, null)]
    [InlineData("float", 0.0, 1.0, 1.5, "range")]
    [InlineData("int", 1, 64, 0, "range")]
    [InlineData("int", 1, 64, 1.5, "type")]
    public void Validate_Numbers(string type, double min, double max, double value, string? expected)
    {
        Assert.Equal(expected, OcrConfigValidation.Validate(Spec(type, min, max), JsonValue.Create(value)));
    }

    [Fact]
    public void Validate_BoolRejectsNumbers()
        => Assert.Equal("type", OcrConfigValidation.Validate(Spec("bool"), JsonValue.Create(1)));

    [Fact]
    public void Validate_EnumUsesValues()
    {
        var spec = Spec("enum", values: ["min", "max"]);
        Assert.Null(OcrConfigValidation.Validate(spec, JsonValue.Create("max")));
        Assert.Equal("enum", OcrConfigValidation.Validate(spec, JsonValue.Create("mid")));
    }

    [Fact]
    public void ParseShape_RejectsWrongArity()
    {
        Assert.True(OcrConfigValidation.TryParseShape("1,3,640,640", out JsonArray? ok, out _));
        Assert.Equal(640, ok![3]!.GetValue<int>());
        Assert.False(OcrConfigValidation.TryParseShape("640,640", out _, out string? error));
        Assert.Equal("shape", error);
    }

    [Fact]
    public void ParseJsonObject_ReportsInvalid()
    {
        Assert.True(OcrConfigValidation.TryParseJsonObject("""{"run_mode":"trt_fp16"}""", out JsonObject? ok, out _));
        Assert.Equal("trt_fp16", ok!["run_mode"]!.GetValue<string>());
        Assert.False(OcrConfigValidation.TryParseJsonObject("{oops", out _, out string? error));
        Assert.Equal("json", error);
    }
}
```

- [ ] **Step 2: Chạy xác nhận fail** — filter class `OcrConfigValidationTests`. Expected: FAIL (compile).

- [ ] **Step 3: Implement** theo interface trên (dùng `CultureInfo.InvariantCulture` cho số).
- [ ] **Step 4: Chạy pass** — filter class `OcrConfigValidationTests`.
- [ ] **Step 5: Commit**

```bash
git add ReTran.App/Services/OcrConfigValidation.cs ReTran.Tests/OcrConfigValidationTests.cs
git commit -m "feat(app): schema-driven ocr param validation helpers"
```

---

### Task 7: App — khối "Tham số máy OCR" trong SettingsPanel + i18n

**Files:**
- Modify: `ReTran.App/Components/Shared/SettingsPanel.razor`
- Modify: `ReTran.App/Services/I18nStrings.cs` (cả `vi` và `en`)
- Test: kiểm tay (Task 10); i18n dùng key mới.

**Interfaces:**
- Consumes: `Pipeline.GetOcrConfigSchemaAsync` / `Pipeline.SetOcrConfigAsync` (Task 5), `OcrConfigValidation` (Task 6).

- [ ] **Step 1: Thêm i18n keys** (cả 2 ngôn ngữ). Nhãn/hint/tip lấy từ bảng spec §6.1–6.5 (vi có sẵn; en viết tương đương ngắn gọn):

  - `deck.ocr.params.title` = "Tham số máy OCR" / "OCR engine parameters"
  - `ocr.group.device|models|preprocess|det|rec|rhythm` (6 nhãn nhóm; `rhythm` dùng cho khối nhịp hard-code)
  - `ocr.param.<key>.label`, `ocr.param.<key>.hint`, `ocr.param.<key>.tip` cho **27 key**
  - `ocr.param.error.type|range|enum|shape|json`
  - `deck.ocr.params.loadFailed`, `deck.ocr.params.invalid`, `deck.ocr.params.saveFailed`,
    `deck.ocr.params.applied` ("Áp dụng ngay"), `deck.ocr.params.deferred` ("Đã lưu — áp dụng sau khi Dừng"),
    `deck.ocr.params.save`, `deck.ocr.params.restore`
  - `deck.ocr.rhythm.ocrFps`, `deck.ocr.rhythm.captureFps`, `deck.ocr.rhythm.note`
    ("Áp dụng ở lần Start kế tiếp")

- [ ] **Step 2: Thêm state + nạp schema vào `SettingsPanel`**

Thêm field: `_ocrSchema` (`IReadOnlyList<OcrParamSpec>?`), `_ocrSchemaFailed`, `_ocrParamsBusy`,
`_ocrText` (`Dictionary<string, string>`), `_ocrBool` (`Dictionary<string, bool>`),
`_ocrValues` (`Dictionary<string, JsonNode?>` — key user đã đặt), `_ocrFpsText`, `_captureFpsText`.

`OnInitialized`: gọi thêm `_ = LoadOcrSchemaAsync();` (fire-and-forget như `LoadOcrModelsAsync`).
`LoadOcrSchemaAsync`: fetch schema; lỗi → `_ocrSchemaFailed = true`, giữ page sống; thành công → `ApplySettingsToOcrState()`.

`ApplySettingsToOcrState()`: đọc `SettingsStore.Load()`; `_ocrValues = new Dictionary<string, JsonNode?>(settings.OcrEngine ?? new())`;
mỗi spec: `_ocrBool[key] = value ?? spec.Default` (bool), `_ocrText[key] = Format(value ?? spec.Default)` (còn lại);
`_ocrFpsText = settings.OcrFps.ToString()`, `_captureFpsText = settings.CaptureFps.ToString()`.

- [ ] **Step 3: Render (Razor)** — sau bảng catalog model, cùng `<section>` OCR:

```razor
@if (_ocrSchema is null)
{
    <p class="rt-hint">@(_ocrSchemaFailed ? L["deck.ocr.params.loadFailed"] : L["deck.ocr.loading"])</p>
}
else
{
    <h4 class="rt-hint" style="margin: 12px 0 6px;">@(L["deck.ocr.params.title"])</h4>
    @foreach (IGrouping<string, OcrParamSpec> group in _ocrSchema.GroupBy(p => p.Group))
    {
        <details open="@(group.Key is not ("device" or "models"))">
            <summary>@(L["ocr.group." + group.Key])</summary>
            @foreach (OcrParamSpec p in group)
            {
                <div class="rt-srow" title="@(L["ocr.param." + p.Key + ".tip"])">
                    <span>@(L["ocr.param." + p.Key + ".label"])</span>
                    @* control theo p.Type — xem Step 4 *@
                    <p class="rt-hint">@(L["ocr.param." + p.Key + ".hint"])</p>
                </div>
            }
        </details>
    }
    <div class="rt-field-row" style="...">
        <button ... @onclick="SaveOcrParamsAsync">@(L["deck.ocr.params.save"])</button>
        <button ... @onclick="RestoreOcrParamsAsync">@(L["deck.ocr.params.restore"])</button>
        <span class="rt-hint">@_ocrStatusText</span>
    </div>
    @* 2 dòng nhịp: input number 1..60 cho _ocrFpsText/_captureFpsText + note *@
}
```

- [ ] **Step 4: Control theo `type`** (mỗi dòng: label + control + hint + `title` tooltip):
  - `bool` → `<input type="checkbox" @bind="_ocrBool[p.Key]">`
  - `enum` → `<select>` với `p.Values`, thêm option đầu `@(L["settings.opt.auto"])` khi `p.Default is null`
  - `int`/`float` → `<input type="number" min max step @bind="_ocrText[p.Key]">`, placeholder = default đã format
  - `string` → `<input type="text" @bind="_ocrText[p.Key]">`, placeholder = `p.Placeholder ?? Format(default)`
  - `json`/`shape` → `<input type="text" @bind="_ocrText[p.Key]">` + validate khi lưu

- [ ] **Step 5: Save + Restore**

`SaveOcrParamsAsync()`:
1. `_ocrParamsBusy` guard.
2. Với mỗi spec: build giá trị wire:
   - bool → `_ocrBool[key]`;
   - enum → chuỗi select (bỏ option "default" → coi như không set);
   - int/float → parse invariant; lỗi → status `ocr.param.error.type`;
   - json → `TryParseJsonObject`; shape → `TryParseShape`; string → text (rỗng = không set);
   - `OcrConfigValidation.Validate(spec, value)` ≠ null → status lỗi tương ứng, **return** (không gọi RPC).
   - Nếu `JsonNode.DeepEquals(value, spec.Default)` → coi như không set (không ghim default — C5).
3. Parse `_ocrFpsText`/`_captureFpsText` int 1..60 (sai → status lỗi, return).
4. `OcrConfigSetResult r = await Pipeline.SetOcrConfigAsync(map, ct)` — **set trước, ghi file sau** (C14).
5. Thành công → `SettingsStore.Save(SettingsStore.Load() with { OcrEngine = map, OcrFps = ..., CaptureFps = ... })`;
   status = `r.Deferred.Count > 0 ? L["deck.ocr.params.deferred"] : L["deck.ocr.params.applied"]`; toast tương ứng.
6. `catch (Exception)` → status `deck.ocr.params.saveFailed` (không ghi file).

`RestoreOcrParamsAsync()`: `_ocrValues.Clear()`, `ApplySettingsToOcrState()` (mọi control về default), rồi gọi `SaveOcrParamsAsync()` (map rỗng → Core gửi `{}` → sidecar reset; C12/C5).

- [ ] **Step 6: Build + chạy test hiện có** — `npm run build` không cần (Razor thuần); `dotnet build "ReTran Project.slnx"`; `dotnet test ... -- --filter-class "I18nServiceTests"`.
Expected: build sạch; i18n test cũ pass.

- [ ] **Step 7: Commit**

```bash
git add ReTran.App/Components/Shared/SettingsPanel.razor ReTran.App/Services/I18nStrings.cs
git commit -m "feat(app): schema-driven OCR parameter block in settings"
```

---

### Task 8: Bỏ hẳn panel mode

**Files:**
- Modify: `ReTran.App/Services/OverlayWindow/OverlayLayout.cs`
- Modify: `ReTran.App/Services/OverlayWindow/OverlayWindowService.cs`
- Modify: `ReTran.App/Services/OverlayWindow/Win32OverlayHost.cs` (nhánh panel trong `DrawBoxes`)
- Modify: `ReTran.App/Services/AppSettingsService.cs` (`OverlayMode`, `OverlayCorner`)
- Modify: `ReTran.App/Components/Pages/Home.razor` (dropdown mode + corner)
- Modify: `ReTran.App/Services/I18nStrings.cs` (`overlay.mode.*`, `overlay.corner.*`, `deck.overlay.mode`)
- Modify: `Docs/superpowers/specs/2026-10-15-game-overlay-design.md` + `2026-10-06-m4-translation-design.md` (ghi chú "superseded bởi spec 2026-10-26" ở đầu mỗi file)
- Test: `ReTran.Tests/Overlay/OverlayLayoutTests.cs`, `ReTran.Tests/Overlay/OverlayWindowServiceTests.cs`, `ReTran.Tests/Overlay/Win32OverlayHostRenderTests.cs`, `ReTran.Tests/AppSettingsServiceTests.cs`

- [ ] **Step 1: Sửa test trước** — xoá mọi test/assert panel (`RenderPanel`, `PanelMaxLines`, `IsPanelLine: true`, mode/corner) và chuyển `Subtitle(scale)` helper thành `Render(frame, 1920, 1080)` (không options). Trong `OverlayWindowServiceTests` xoá case panel (L~229). Trong `Win32OverlayHostRenderTests` bỏ tham số `IsPanelLine`. Trong `AppSettingsServiceTests` bỏ `OverlayMode/OverlayCorner` khỏi dữ liệu test.
- [ ] **Step 2: Chạy xác nhận fail (compile)** — filter 4 class trên. Expected: FAIL compile.
- [ ] **Step 3: Implement xoá** theo spec §9.1: xoá enum `OverlayMode`/`OverlayCorner`, `OverlayViewOptions`, `PanelMaxLines/PanelMargin/PanelLineGap/PanelWidthRatio/PanelLineHeight`, `RenderPanel`, field `DrawBox.IsPanelLine`; `OverlayLayout.Render(OverlayFrame frame, double vw, double vh)` chỉ còn subtitle; `OverlayWindowService` bỏ `TryParseMode/TryParseCorner/ModeToString/CornerToString`, `ApplyOptions(mode|corner)`, `_options` chỉ giữ phần cỡ chữ (Task 9 xoá nốt); panel branch trong `Win32OverlayHost.DrawBoxes` + call sites; `Home.razor` bỏ `_overlayMode/_overlayCorner` + 2 dropdown + `ApplyOverlayMode/ApplyOverlayCorner`; `AppSettings` bỏ 2 field; i18n bỏ 3 nhóm key.
- [ ] **Step 4: Chạy pass** — 4 class trên + `dotnet build`. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add -A ReTran.App ReTran.Tests Docs/superpowers/specs
git commit -m "refactor(overlay): remove panel mode, keep subtitle only"
```

---

### Task 9: Bỏ hẳn cỡ chữ thủ công

**Files:**
- Modify: `ReTran.Core/Serve/PipelineHost.cs` (`_annotationFontScale`, `SetFontScaleAsync`, `fontScale` trong `ParseStart`/`StartSpec`, call `Annotate(...)` L~781)
- Modify: `ReTran.Core/Program.cs` (bỏ `core.set_font_scale` L~94)
- Modify: `ReTran.Core/Serve/AnnotationSnapshot.cs` (`IFrameAnnotator.Annotate` bỏ `double fontScale`)
- Modify: `ReTran.Core/Serve/FrameAnnotator.cs`, `ReTran.Core/Serve/BoxAnnotationStyle.cs`
- Modify: `ReTran.App/Services/OverlayWindow/BoxAnnotationStyle.cs` (bản sync), `Win32OverlayHost.cs`, `IOverlayHost.cs`, `OverlayWindowService.cs` (`ApplyOptions(fontScale)`, `OverlayViewOptions.FontScale` đã xoá ở Task 8 — còn `LoadOptions`), `Home.razor` (segmented Nhỏ/Trung bình/Lớn + `SetOverlayFont`), `AppSettingsService.cs` (`OverlayFontScale`), `PipelineController.cs` (`SetAnnotationFontScaleAsync`)
- Modify: `ReTran.App/Services/I18nStrings.cs` (`overlay.font.*`, `deck.overlay.font`)
- Test: `ReTran.Tests/BoxAnnotationStyleTests.cs`, `ReTran.Tests/FrameAnnotatorTests.cs`, `ReTran.Tests/Overlay/Win32OverlayHostRenderTests.cs`, `ReTran.Tests/Overlay/OverlayWindowServiceTests.cs`, `ReTran.Tests/PipelineHostTests.cs` (FakeAnnotator `LastFontScale`), `ReTran.Tests/AppSettingsServiceTests.cs`

**Công thức giữ nguyên:** `0.5 × boxHeight` (kẹp `12..40` px), shrink tối thiểu `9` px — bỏ hệ số nhân người dùng ở cả 2 bản `BoxAnnotationStyle` (`BaseFontSize(float boxHeight, float maxFontSize = MaxFontSize)`; `Fit(...)` bỏ tham số `fontScale`).

- [ ] **Step 1: Sửa test trước** — bỏ mọi assert `fontScale`/`LastFontScale`; `BoxAnnotationStyleTests` gọi API mới không hệ số; xoá test `core.set_font_scale`.
- [ ] **Step 2: Chạy xác nhận fail (compile)**.
- [ ] **Step 3: Implement xoá** theo spec §9.2.
- [ ] **Step 4: Chạy pass** — toàn bộ `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj"` (đây là task chạm nhiều file nhất — full suite để bắt sót).
- [ ] **Step 5: Commit**

```bash
git add -A ReTran.Core ReTran.App ReTran.Tests
git commit -m "refactor(overlay): drop manual font scale; boxes always auto-size"
```

---

### Task 10: Tài liệu + kiểm chứng đầu-cuối

**Files:**
- Modify: `Docs/performance.md` (mục "OCR engine params: reload cost + first-use model download")
- Modify: `Docs/superpowers/specs/2026-10-26-ocr-engine-settings-overlay-simplify-design.md` (Trạng thái → "đã thực hiện 2026-10-26" khi xong)

- [ ] **Step 1: Đo lại mặc định (không được lệch bản cũ)** — chạy `Tools/Perf` annotate benchmark + 1 phiên OCR thật với mặc định mới (không key trong `ocrEngine`): annotation ≤ 40 ms/khung, OCR ~3 fps, VRAM < 1 GB. Ghi số vào `Docs/performance.md`.
- [ ] **Step 2: Ghi 2 mục perf mới**: (a) đổi tham số reload khi idle → `spot()` kế tiếp chậm ~27 s, warmup `core.start` che; (b) bật 3 cờ orientation/unwarping lần đầu → PaddleX tải model ngoài catalog, `spot()` timeout 30 s liên tiếp, preview đứng vài phút — ngoại lệ có chủ đích.
- [ ] **Step 3: Kiểm tay end-to-end (spec §11)**:
  1. Settings → OCR: đổi `text_det_box_thresh` khi pipeline chạy → preview đổi ngay, status "Áp dụng ngay".
  2. Đổi `device` khi chạy → status "Đã lưu — áp dụng sau khi Dừng"; Dừng → Start → log Core nhận `device` mới.
  3. "Khôi phục mặc định" khi chạy → live param về default ngay.
  4. Sửa tay `settings.json`: thêm key lạ → Start vẫn chạy, log có `warn: ocr.params: dropping unknown key`.
  5. Tắt/kill sidecar rồi mở Settings → hiện "Không lấy được schema", app không crash.
  6. OBS Browser Source không đổi; overlay cục bộ chỉ còn subtitle; không còn control "Cỡ chữ" hay "Chế độ".
- [ ] **Step 4: Commit**

```bash
git add Docs
git commit -m "docs: record ocr config perf notes and verification for the new settings surface"
```

---

## Ghi chú thực thi

- Thứ tự bắt buộc: Task 1 → (2,3) → 4 → (5,6,7) → (8,9 song song được ngay sau Task 1 vì không đụng RPC mới) → 10. Task 8/9 chạm `AppSettingsService`/`PipelineController` mà Task 5 cũng chạm — nếu chạy song song phải merge cẩn thận; an toàn nhất là tuần tự.
- Task 4 và Task 9 cùng sửa `PipelineHost.cs`: làm 4 trước rồi 9 để tránh conflict.
- `dotnet test` trên máy này chạy xUnit v3/MTP; filter dùng `-- --filter-class "X"` (đã ghi trong AGENTS.md).
- Không commit file `*.tmp`/CSV đo perf thô vào git (chỉ số trong `Docs/performance.md`).
