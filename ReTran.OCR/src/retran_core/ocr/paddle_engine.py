"""Real PP-OCR engine: lazy PaddleOCR wrapper (model loads on first spot call)."""
from __future__ import annotations

from typing import Any

import numpy as np
import numpy.typing as npt

from .config import OcrConfig
from .engine import Spot


class PaddleOcrEngine:
    """Text spotting via PaddleOCR, pinned to a caller-chosen det/rec pair.

    ``det_model``/``rec_model`` are read-only properties exposing PaddleOCR
    registry names (the sidecar picks them from the catalog module) that are
    forwarded verbatim to ``PaddleOCR(...)``. Pair switches must go through
    :meth:`set_models` so the cached predictor resets together with the
    names — writing the attributes directly would leave the old predictor
    serving the old models. The engine owns the :class:`OcrConfig` exposed as
    :attr:`config`: constructor options resolved from it are merged into
    every ``PaddleOCR(...)`` build, the live options go to ``predict()`` on
    the paddleocr 3.x path, and a non-deferred config change that touches a
    constructor option drops the cached predictor so the next spot() rebuilds
    it. The paddleocr import and model load happen inside the first spot()
    call so the sidecar stays stdlib-fast to start and reports a clean -32603
    when the optional deps are missing.

    Attributes:
        det_model: PaddleOCR text-detection model registry name (read-only).
        rec_model: PaddleOCR text-recognition model registry name (read-only).
        config: The engine's parameter override map (read-only).
    """

    def __init__(self, det_model: str, rec_model: str) -> None:
        """Create an engine bound to one det/rec pair.

        The parameter override map starts empty, so the first predictor is
        built entirely from schema defaults (the pre-config behaviour).

        Args:
            det_model: PaddleOCR text-detection model registry name.
            rec_model: PaddleOCR text-recognition model registry name.
        """
        self._det_model = det_model
        self._rec_model = rec_model
        self._ocr: Any = None
        self._config = OcrConfig(on_reload=self._reset_predictor)

    @property
    def det_model(self) -> str:
        """Text-detection model registry name passed to the next predictor."""
        return self._det_model

    @property
    def rec_model(self) -> str:
        """Text-recognition model registry name passed to the next predictor."""
        return self._rec_model

    @property
    def config(self) -> OcrConfig:
        """The engine's parameter override map, owned by the engine.

        Its reload hook drops the cached predictor, so a non-deferred
        :meth:`OcrConfig.set` of a constructor option is applied by the next
        :meth:`spot` call.
        """
        return self._config

    def set_models(self, det_model: str, rec_model: str) -> None:
        """Switch the det/rec pair; the predictor reloads lazily.

        Stores the new pair and resets the cached predictor so the next
        :meth:`spot` call rebuilds PaddleOCR with the new names.

        Args:
            det_model: New text-detection model registry name.
            rec_model: New text-recognition model registry name.
        """
        self._det_model = det_model
        self._rec_model = rec_model
        self._reset_predictor()

    def _reset_predictor(self) -> None:
        """Drop the cached predictor so the next spot call rebuilds it.

        Bound to the config's reload hook (a non-deferred constructor-option
        change) and used by :meth:`set_models`; the model itself stays
        unloaded until the next :meth:`spot` call.
        """
        self._ocr = None

    def spot(self, image_bgr: npt.NDArray[np.uint8]) -> list[Spot]:
        """Detect + recognize text in one BGR image.

        The first call loads the model, merging the constructor options
        resolved from :attr:`config`. On the paddleocr 3.x path the live
        options resolved from :attr:`config` are forwarded to ``predict()``;
        the 2.x ``ocr()`` fallback takes no options.

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
            return self._parse_v3(self._ocr.predict(image_bgr, **self._config.predict_kwargs()))
        if hasattr(self._ocr, "ocr"):
            return self._parse_v2(self._ocr.ocr(image_bgr))
        raise RuntimeError("model init failed: PaddleOCR has neither predict() nor ocr()")

    def _create(self) -> Any:
        try:
            from paddleocr import PaddleOCR
        except ImportError as exc:
            raise RuntimeError(f"paddleocr not installed: {exc}") from exc
        try:
            # The det/rec registry names come from the caller (catalog pair)
            # and stay pinned here, so lang is not passed at all: paddleocr
            # 3.x only warns that lang is ignored when model names are
            # pinned. The three orientation/unwarping pipelines start pinned
            # off — game/UI text does not need the doc preprocessor, and the
            # disabled pipelines are the M2 latency gate
            # (Docs/performance.md M2 baseline) — but a user override from
            # the config merged below can turn them back on. oneDNN stays off
            # by default: the paddlepaddle 3.4 PIR/oneDNN CPU path crashes
            # (Paddle#77340), and GPU ignores the flag anyway.
            kwargs: dict[str, Any] = {
                "text_detection_model_name": self.det_model,
                "text_recognition_model_name": self.rec_model,
                "use_doc_orientation_classify": False,
                "use_doc_unwarping": False,
                "use_textline_orientation": False,
            }
            # Constructor options from the config win over the pins above;
            # its defaults reproduce the previous pinned behaviour, while a
            # user override (e.g. use_textline_orientation=true) takes effect
            # on this rebuild.
            kwargs.update(self._config.constructor_kwargs())
            return PaddleOCR(**kwargs)
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
