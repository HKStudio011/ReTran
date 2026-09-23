"""Integration test against the real PaddleOCR engine (skips when paddle is absent)."""
from __future__ import annotations

import base64
import importlib.util
import io

import pytest
from PIL import Image, ImageDraw, ImageFont

pytestmark = pytest.mark.skipif(
    importlib.util.find_spec("paddleocr") is None,
    reason="paddleocr not installed (uv sync) -> skip",
)


def _text_png_b64() -> str:
    """Render 'HELLO RETRAN' on white and return base64 PNG.

    Font size 40: PP-OCRv5 det misses the ~11px PIL default bitmap font
    (0 boxes on a verified run); size 40 is detected reliably.
    """
    img = Image.new("RGB", (640, 96), "white")
    d = ImageDraw.Draw(img)
    d.text((20, 24), "HELLO RETRAN", fill="black", font=ImageFont.load_default(size=40))
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def test_paddle_spot_finds_hello():
    from retran_core.ocr.image import decode_png_b64
    from retran_core.ocr.paddle_engine import PaddleOcrEngine

    engine = PaddleOcrEngine(lang="en")
    spots = engine.spot(decode_png_b64(_text_png_b64()))
    assert len(spots) > 0
    joined = " ".join(s.text.upper() for s in spots)
    assert "HELLO" in joined  # Rec model may split lines; require the word somewhere
    for s in spots:
        assert len(s.box) == 4
        assert 0.0 <= s.score <= 1.0


def test_paddle_engine_is_lazy():
    """Module import must not pull paddleocr (stdlib-fast sidecar startup)."""
    import retran_core.ocr.paddle_engine as mod

    src = open(mod.__file__, encoding="utf-8").read()
    assert "import paddleocr" not in src.split("def ")[0]  # no top-level paddleocr import
