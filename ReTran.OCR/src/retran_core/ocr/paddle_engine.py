"""Real PP-OCR engine: lazy PaddleOCR wrapper (model loads on first spot call)."""
from __future__ import annotations

from typing import Any

import numpy as np
import numpy.typing as npt

from .engine import Spot


class PaddleOcrEngine:
    """Text spotting via PaddleOCR, pinned to a caller-chosen det/rec pair.

    ``det_model``/``rec_model`` are read-only properties exposing PaddleOCR
    registry names (the sidecar picks them from the catalog module) that are
    forwarded verbatim to ``PaddleOCR(...)``. Pair switches must go through
    :meth:`set_models` so the cached predictor resets together with the
    names — writing the attributes directly would leave the old predictor
    serving the old models. The paddleocr import and model load happen inside
    the first spot() call so the sidecar stays stdlib-fast to start and
    reports a clean -32603 when the optional deps are missing.

    Attributes:
        det_model: PaddleOCR text-detection model registry name (read-only).
        rec_model: PaddleOCR text-recognition model registry name (read-only).
    """

    def __init__(self, det_model: str, rec_model: str) -> None:
        self._det_model = det_model
        self._rec_model = rec_model
        self._ocr: Any = None

    @property
    def det_model(self) -> str:
        """Text-detection model registry name passed to the next predictor."""
        return self._det_model

    @property
    def rec_model(self) -> str:
        """Text-recognition model registry name passed to the next predictor."""
        return self._rec_model

    def set_models(self, det_model: str, rec_model: str) -> None:
        """Switch the det/rec pair; the predictor reloads lazily.

        Stores the new pair and resets the cached predictor (``_ocr = None``)
        so the next :meth:`spot` call rebuilds PaddleOCR with the new names.

        Args:
            det_model: New text-detection model registry name.
            rec_model: New text-recognition model registry name.
        """
        self._det_model = det_model
        self._rec_model = rec_model
        self._ocr = None

    def spot(self, image_bgr: npt.NDArray[np.uint8]) -> list[Spot]:
        """Detect + recognize text in one BGR image.

        Args:
            image_bgr: HWC uint8 image in BGR order.

        Returns:
            Detected spots in engine order.

        Raises:
            RuntimeError: paddleocr is not installed, model init failed, or
                the installed API matches neither known shape.
        """
        if self._ocr is None:
            self._ocr = self._create()
        if hasattr(self._ocr, "predict"):
            return self._parse_v3(self._ocr.predict(image_bgr))
        if hasattr(self._ocr, "ocr"):
            return self._parse_v2(self._ocr.ocr(image_bgr))
        raise RuntimeError("model init failed: PaddleOCR has neither predict() nor ocr()")

    def _create(self) -> Any:
        try:
            from paddleocr import PaddleOCR
        except ImportError as exc:
            raise RuntimeError(f"paddleocr not installed: {exc}") from exc
        try:
            # The paddlepaddle 3.3.x oneDNN/PIR crash (PaddlePaddle/Paddle#77340)
            # is gone on the paddleocr 3.7 / paddlepaddle 3.4 stack, so the
            # engine no longer passes enable_mkldnn=False; paddleocr's own
            # default (enabled) applies. Both model names come from the
            # caller (catalog pair), so lang is not passed at all: paddleocr
            # 3.x only warns that lang is ignored when model names are
            # pinned. Game/UI text does not need the doc preprocessor; the
            # three disabled pipelines are the M2 latency gate
            # (Docs/performance.md M2 baseline).
            return PaddleOCR(
                text_detection_model_name=self.det_model,
                text_recognition_model_name=self.rec_model,
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
            )
        except Exception as exc:
            raise RuntimeError(f"model init failed: {exc}") from exc

    @staticmethod
    def _poly_to_box(poly: Any) -> list[list[float]]:
        return [[float(x), float(y)] for x, y in np.asarray(poly).reshape(4, 2)]

    def _parse_v3(self, result: Any) -> list[Spot]:
        """Parse paddleocr >=3 predict output (list of dicts with rec_* / dt_polys)."""
        spots: list[Spot] = []
        for page in result:
            polys = np.asarray(page["dt_polys"])
            texts = list(page["rec_texts"])
            scores = [float(s) for s in page["rec_scores"]]
            for poly, text, score in zip(polys, texts, scores):
                spots.append(Spot(box=self._poly_to_box(poly), text=text, score=score))
        return spots

    def _parse_v2(self, result: Any) -> list[Spot]:
        """Parse paddleocr 2.x ocr() output ([[ [poly, (text, score)], ... ]] per image)."""
        spots: list[Spot] = []
        for page in result or []:
            for entry in page or []:
                poly, (text, score) = entry
                spots.append(Spot(box=self._poly_to_box(poly), text=text, score=float(score)))
        return spots
