"""Real PP-OCR engine: lazy PaddleOCR wrapper (model loads on first spot call)."""
from __future__ import annotations

from typing import Any

import numpy as np
import numpy.typing as npt

from .engine import Spot


class PaddleOcrEngine:
    """Text spotting via PaddleOCR; lang passed through to PaddleOCR(lang=...).

    The paddleocr import and model load happen inside the first spot() call so
    the sidecar stays stdlib-fast to start and reports a clean -32603 when the
    optional deps are missing.
    """

    def __init__(self, lang: str = "en") -> None:
        self.lang = lang
        self._ocr: Any = None

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
            # enable_mkldnn=False: paddlepaddle 3.3.x oneDNN/PIR path crashes on
            # PP-OCRv5 (ConvertPirAttribute2RuntimeAttribute, onednn_instruction.cc;
            # see PaddlePaddle/Paddle#77340). Official workaround: disable oneDNN.
            # M3 perf gate: lang="en" would resolve PP-OCRv5_server_det plus the
            # doc-orientation/unwarping/textline pipelines — three extra models and
            # most of the M2 latency (Docs/performance.md M2 baseline). Game/UI
            # text does not need the doc preprocessor; pin mobile det instead.
            # Once det is pinned, paddleocr ignores lang (rec would fall back to
            # the server model), so pin mobile rec too (spec §4.1).
            return PaddleOCR(
                lang=self.lang,
                enable_mkldnn=False,
                text_detection_model_name="PP-OCRv5_mobile_det",
                text_recognition_model_name=f"{self.lang}_PP-OCRv5_mobile_rec",
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
