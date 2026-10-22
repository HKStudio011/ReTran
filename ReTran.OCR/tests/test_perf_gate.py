"""M3 perf-gate unit tests: engine constructor kwargs (no real paddle needed)."""
from __future__ import annotations

import sys
import types

import numpy as np

from retran_core.ocr.catalog import recommended
from retran_core.ocr.paddle_engine import PaddleOcrEngine


class RecordingPaddleOCR:
    """Stands in for paddleocr.PaddleOCR and records constructor kwargs."""

    last_kwargs: dict = {}

    def __init__(self, **kwargs):
        RecordingPaddleOCR.last_kwargs = kwargs

    def predict(self, image):
        return [
            {
                "dt_polys": np.array([[[10, 10], [60, 10], [60, 30], [10, 30]]]),
                "rec_texts": ["HI"],
                "rec_scores": [0.98],
            }
        ]


def test_spot_uses_pinned_models(monkeypatch):
    """The det/rec pair given to the constructor must reach PaddleOCR verbatim."""
    fake = types.ModuleType("paddleocr")
    fake.PaddleOCR = RecordingPaddleOCR
    monkeypatch.setitem(sys.modules, "paddleocr", fake)

    # A non-default catalog pair (ko -> legacy korean rec), so a hardcoded
    # default inside the engine would fail this test instead of passing it.
    det, rec = recommended("ko", "vi")
    engine = PaddleOcrEngine(det, rec)
    engine.spot(np.zeros((40, 80, 3), dtype=np.uint8))

    kwargs = RecordingPaddleOCR.last_kwargs
    assert kwargs["text_detection_model_name"] == det
    assert kwargs["text_recognition_model_name"] == rec
    # lang-based naming is gone for good: paddleocr 3.x only warns that lang
    # is ignored when model names are pinned, so do not pass it at all.
    assert "lang" not in kwargs


def test_engine_disables_doc_pipelines_and_mkldnn(monkeypatch):
    fake = types.ModuleType("paddleocr")
    fake.PaddleOCR = RecordingPaddleOCR
    monkeypatch.setitem(sys.modules, "paddleocr", fake)

    engine = PaddleOcrEngine(det_model="PP-OCRv6_small_det", rec_model="PP-OCRv6_small_rec")
    spots = engine.spot(np.zeros((40, 80, 3), dtype=np.uint8))

    kwargs = RecordingPaddleOCR.last_kwargs
    assert kwargs["enable_mkldnn"] is False
    assert kwargs["use_doc_orientation_classify"] is False
    assert kwargs["use_doc_unwarping"] is False
    assert kwargs["use_textline_orientation"] is False
    # parse path still works with the fake predict payload:
    assert [s.text for s in spots] == ["HI"]
    assert spots[0].box == [[10.0, 10.0], [60.0, 10.0], [60.0, 30.0], [10.0, 30.0]]


import base64
import io

from PIL import Image

from retran_core.ocr.image import MAX_INPUT_DIM, decode_image_b64


def _png_b64(w: int, h: int) -> str:
    """Solid white RGB PNG as base64 ASCII."""
    buf = io.BytesIO()
    Image.new("RGB", (w, h), (255, 255, 255)).save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def test_downscale_caps_long_side_at_max_input_dim():
    assert MAX_INPUT_DIM == 1280
    arr = decode_image_b64(_png_b64(1920, 1080))
    assert arr.shape == (720, 1280, 3)  # 1920x1080 -> 1280x720, aspect kept


def test_downscale_leaves_small_input_untouched():
    arr = decode_image_b64(_png_b64(800, 600))
    assert arr.shape == (600, 800, 3)


def test_downscale_handles_extreme_aspect_ratio():
    arr = decode_image_b64(_png_b64(2000, 500))
    assert arr.shape == (320, 1280, 3)
