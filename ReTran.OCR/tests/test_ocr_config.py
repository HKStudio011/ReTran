"""Unit tests for the OCR engine config surface (schema / replace / defer),
its ocr.config.* handlers and the engine wiring. Offline: the engine tests
inject a fake paddleocr; only the signature-coverage test imports the real one."""
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
