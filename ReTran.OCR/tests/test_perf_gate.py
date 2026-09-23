"""M3 perf-gate unit tests: engine constructor kwargs (no real paddle needed)."""
from __future__ import annotations

import sys
import types

import numpy as np

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


def test_engine_uses_mobile_det_and_disables_doc_pipelines(monkeypatch):
    fake = types.ModuleType("paddleocr")
    fake.PaddleOCR = RecordingPaddleOCR
    monkeypatch.setitem(sys.modules, "paddleocr", fake)

    engine = PaddleOcrEngine(lang="en")
    spots = engine.spot(np.zeros((40, 80, 3), dtype=np.uint8))

    kwargs = RecordingPaddleOCR.last_kwargs
    assert kwargs["lang"] == "en"
    assert kwargs["enable_mkldnn"] is False
    assert kwargs["text_detection_model_name"] == "PP-OCRv5_mobile_det"
    assert kwargs["use_doc_orientation_classify"] is False
    assert kwargs["use_doc_unwarping"] is False
    assert kwargs["use_textline_orientation"] is False
    # rec stays whatever lang resolves (en_PP-OCRv5_mobile_rec) — never pinned here:
    assert "text_recognition_model_name" not in kwargs
    # parse path still works with the fake predict payload:
    assert [s.text for s in spots] == ["HI"]
    assert spots[0].box == [[10.0, 10.0], [60.0, 10.0], [60.0, 30.0], [10.0, 30.0]]
