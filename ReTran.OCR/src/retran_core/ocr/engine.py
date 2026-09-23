"""OCR engine contract shared by fake and real implementations."""
from __future__ import annotations

from typing import NamedTuple, Protocol

import numpy as np
import numpy.typing as npt


class Spot(NamedTuple):
    """One detected text region.

    Attributes:
        box: Quadrilateral corners [[x, y], [x, y], [x, y], [x, y]] in pixels.
        text: Recognized text.
        score: Recognition confidence in [0, 1].
    """

    box: list[list[float]]
    text: str
    score: float

    def to_dict(self) -> dict:
        """Serialize to the ocr.spot JSON shape (no rounding)."""
        return {"box": self.box, "text": self.text, "score": self.score}


class OcrEngine(Protocol):
    """Detects and recognizes text in a BGR image."""

    def spot(self, image_bgr: npt.NDArray[np.uint8]) -> list[Spot]:
        """Return detected text regions for one image (HWC uint8 BGR)."""
        ...
