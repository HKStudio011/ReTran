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
from retran_core.ocr.image import decode_image_b64


def _png_b64(w: int = 64, h: int = 32, color: tuple[int, int, int] = (255, 255, 255)) -> str:
    """Encode a solid-color RGB PNG as base64 ASCII."""
    buf = io.BytesIO()
    Image.new("RGB", (w, h), color).save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def _jpeg_b64(
    w: int = 64,
    h: int = 32,
    color: tuple[int, int, int] = (255, 255, 255),
    quality: int = 85,
) -> str:
    """Encode a solid-color RGB JPEG as base64 ASCII."""
    buf = io.BytesIO()
    Image.new("RGB", (w, h), color).save(buf, format="JPEG", quality=quality)
    return base64.b64encode(buf.getvalue()).decode("ascii")


def _gradient_rgb(w: int = 200, h: int = 120) -> Image.Image:
    """Deterministic RGB gradient (JPEG does real work but stays close to the source)."""
    ramp = np.tile(np.linspace(0, 255, w).astype(np.uint8), (h, 1))
    arr = np.stack([ramp, ramp[:, ::-1], np.full((h, w), 128, dtype=np.uint8)], axis=2)
    return Image.fromarray(arr, mode="RGB")


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


def test_decode_image_b64_accepts_png():
    arr = decode_image_b64(_png_b64(color=(255, 0, 0)))  # pure red RGB
    assert arr.dtype == np.uint8
    assert arr.shape == (32, 64, 3)
    assert int(arr[0, 0, 2]) == 255  # red is channel 2 in BGR
    assert int(arr[0, 0, 0]) == 0


def test_decode_image_b64_accepts_jpeg():
    arr = decode_image_b64(_jpeg_b64(color=(255, 0, 0)))  # pure red RGB
    assert arr.dtype == np.uint8
    assert arr.shape == (32, 64, 3)
    assert int(arr[..., 2].mean()) >= 250  # red channel survives q85 (BGR index 2)
    assert int(arr[..., 0].mean()) <= 5


def test_decode_image_b64_jpeg_close_to_png():
    img = _gradient_rgb()
    png_buf, jpeg_buf = io.BytesIO(), io.BytesIO()
    img.save(png_buf, format="PNG")
    img.save(jpeg_buf, format="JPEG", quality=85)
    png_arr = decode_image_b64(base64.b64encode(png_buf.getvalue()).decode("ascii"))
    jpeg_arr = decode_image_b64(base64.b64encode(jpeg_buf.getvalue()).decode("ascii"))
    mae = float(np.mean(np.abs(png_arr.astype(np.int16) - jpeg_arr.astype(np.int16))))
    assert mae < 3


def test_decode_image_b64_invalid_raises_value_error():
    with pytest.raises(ValueError):
        decode_image_b64("not-valid-base64!!")
    with pytest.raises(ValueError):
        decode_image_b64(base64.b64encode(b"definitely not an image").decode("ascii"))


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
