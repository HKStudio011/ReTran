"""Unit tests for the ocr.spot handler contract (FakeEngine, no paddle)."""
from __future__ import annotations

import base64
import io
import json

import numpy as np
import pytest
from PIL import Image

from retran_core.jsonrpc import RpcInvalidParams, handle_line
from retran_core.ocr.engine import Spot
from retran_core.ocr.handlers import make_spot_handler
from retran_core.ocr.image import decode_png_b64


def _png_b64(w: int = 64, h: int = 32, color: tuple[int, int, int] = (255, 255, 255)) -> str:
    """Encode a solid-color RGB PNG as base64 ASCII."""
    buf = io.BytesIO()
    Image.new("RGB", (w, h), color).save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


class FakeEngine:
    """Deterministic engine: always returns one fixed spot."""

    def spot(self, image_bgr: np.ndarray) -> list[Spot]:
        assert image_bgr.ndim == 3 and image_bgr.shape[2] == 3
        return [
            Spot(
                box=[[0.0, 0.0], [10.0, 0.0], [10.0, 5.0], [0.0, 5.0]],
                text="HELLO",
                score=0.99,
            )
        ]


def test_decode_png_b64_returns_bgr_uint8():
    arr = decode_png_b64(_png_b64(color=(255, 0, 0)))  # pure red RGB
    assert arr.dtype == np.uint8
    assert arr.shape == (32, 64, 3)
    assert int(arr[0, 0, 2]) == 255  # red is channel 2 in BGR
    assert int(arr[0, 0, 0]) == 0


def test_decode_png_b64_invalid_raises_value_error():
    with pytest.raises(ValueError):
        decode_png_b64("not-valid-base64!!")


def test_spot_handler_returns_spots_and_elapsed():
    handler = make_spot_handler(FakeEngine())
    result = handler({"image": _png_b64()})
    assert result["spots"][0]["text"] == "HELLO"
    assert result["spots"][0]["box"] == [[0.0, 0.0], [10.0, 0.0], [10.0, 5.0], [0.0, 5.0]]
    assert result["spots"][0]["score"] == pytest.approx(0.99)
    assert isinstance(result["elapsed_ms"], int)
    assert result["elapsed_ms"] >= 0
    assert "new_texts" not in result  # Core adds that, not the sidecar


def test_spot_missing_image_raises_invalid_params():
    handler = make_spot_handler(FakeEngine())
    with pytest.raises(RpcInvalidParams):
        handler({})
    with pytest.raises(RpcInvalidParams):
        handler({"image": ""})


def test_handle_line_maps_invalid_image_to_32602():
    handlers = {"ocr.spot": make_spot_handler(FakeEngine())}
    req = json.dumps({"jsonrpc": "2.0", "id": "1", "method": "ocr.spot", "params": {"image": "@@@"}})
    resp = json.loads(handle_line(req, handlers))
    assert resp["error"]["code"] == -32602


def test_handle_line_happy_path_returns_result():
    handlers = {"ocr.spot": make_spot_handler(FakeEngine())}
    req = json.dumps({"jsonrpc": "2.0", "id": "7", "method": "ocr.spot", "params": {"image": _png_b64()}})
    resp = json.loads(handle_line(req, handlers))
    assert resp["id"] == "7"
    assert resp["result"]["spots"][0]["text"] == "HELLO"
