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
from retran_core.ocr.shm import ShmUnavailable


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


class SizeEchoEngine:
    """Returns a spot covering exactly the array the engine was given."""

    def spot(self, image_bgr: np.ndarray) -> list[Spot]:
        h, w = image_bgr.shape[:2]
        return [
            Spot(
                box=[[0.0, 0.0], [float(w), 0.0], [float(w), float(h)], [0.0, float(h)]],
                text="FULL",
                score=1.0,
            )
        ]


def test_spot_handler_maps_boxes_back_to_source_image_pixels():
    """Boxes must be in the submitted image's pixels, not the 1280-capped copy."""
    handler = make_spot_handler(SizeEchoEngine())
    result = handler({"image": _png_b64(w=1920, h=1080)})
    # decode_image_b64 downscales to 1280×720 before the engine sees it.
    assert result["spots"][0]["box"] == [
        [0.0, 0.0],
        [1920.0, 0.0],
        [1920.0, 1080.0],
        [0.0, 1080.0],
    ]


def test_spot_handler_keeps_small_image_coordinates():
    """Images below the cap are not rescaled."""
    handler = make_spot_handler(SizeEchoEngine())
    result = handler({"image": _png_b64(w=640, h=360)})
    assert result["spots"][0]["box"] == [
        [0.0, 0.0],
        [640.0, 0.0],
        [640.0, 360.0],
        [0.0, 360.0],
    ]


def test_spot_missing_image_raises_invalid_params():
    handler = make_spot_handler(FakeEngine())
    with pytest.raises(RpcInvalidParams):
        handler({})
    with pytest.raises(RpcInvalidParams):
        handler({"image": ""})


class FakeReader:
    """Records requested seqs and returns a fixed BGRA frame (HxWx4)."""

    def __init__(self, frame: np.ndarray) -> None:
        self._frame = frame
        self.calls: list[int] = []

    def read(self, expected_seq: int) -> np.ndarray:
        self.calls.append(expected_seq)
        return self._frame


class CapturingEngine:
    """Returns one fixed spot and records the array the engine was given."""

    def __init__(self) -> None:
        self.seen: np.ndarray | None = None

    def spot(self, image_bgr: np.ndarray) -> list[Spot]:
        self.seen = image_bgr
        return [
            Spot(
                box=[[0.0, 0.0], [10.0, 0.0], [10.0, 5.0], [0.0, 5.0]],
                text="HELLO",
                score=0.99,
            )
        ]


def _bgra(w: int = 6, h: int = 4, salt: int = 0) -> np.ndarray:
    """A deterministic HxWx4 BGRA frame."""
    ramp = np.arange(h * w * 4, dtype=np.uint64)
    return ((ramp * 5 + salt) % 251).astype(np.uint8).reshape(h, w, 4)


def test_spot_handler_seq_params_reads_via_reader_factory():
    reader = FakeReader(_bgra(w=6, h=4))
    engine = CapturingEngine()
    handler = make_spot_handler(engine, reader_factory=lambda: reader)

    result = handler({"seq": 2, "width": 6, "height": 4})

    assert reader.calls == [2]
    # The engine got the BGR view of the BGRA frame (alpha sliced off, no copy).
    assert engine.seen is not None
    assert engine.seen.shape == (4, 6, 3)
    assert np.array_equal(engine.seen, reader._frame[:, :, :3])
    assert result["spots"][0]["text"] == "HELLO"
    assert isinstance(result["elapsed_ms"], int)


def test_spot_handler_seq_path_delivers_full_res_without_rescale():
    # The shm path must NOT downscale to the 1280 cap: boxes stay in the
    # full-res frame's pixels with no coordinate mapping.
    reader = FakeReader(_bgra(w=1920, h=1080))
    handler = make_spot_handler(SizeEchoEngine(), reader_factory=lambda: reader)

    result = handler({"seq": 2, "width": 1920, "height": 1080})

    assert result["spots"][0]["box"] == [
        [0.0, 0.0],
        [1920.0, 0.0],
        [1920.0, 1080.0],
        [0.0, 1080.0],
    ]


def test_spot_handler_seq_reader_created_once_and_cached():
    # The factory (and so the mmap) must run at most once per handler, and only
    # on the first {seq} request — a base64-only sidecar never touches shm.
    reader = FakeReader(_bgra())
    creations = []

    def factory():
        creations.append(1)
        return reader

    handler = make_spot_handler(CapturingEngine(), reader_factory=factory)
    handler({"seq": 2, "width": 6, "height": 4})
    handler({"seq": 4, "width": 6, "height": 4})

    assert len(creations) == 1
    assert reader.calls == [2, 4]


def test_spot_handler_image_path_never_calls_reader_factory():
    reader = FakeReader(_bgra())
    handler = make_spot_handler(FakeEngine(), reader_factory=lambda: reader)

    result = handler({"image": _png_b64()})

    assert result["spots"][0]["text"] == "HELLO"
    assert reader.calls == []


def test_spot_handler_seq_without_factory_raises_invalid_params():
    handler = make_spot_handler(FakeEngine())  # no reader_factory: base64 only
    with pytest.raises(RpcInvalidParams):
        handler({"seq": 2, "width": 6, "height": 4})


def test_spot_handler_seq_malformed_raises_invalid_params():
    handler = make_spot_handler(FakeEngine(), reader_factory=lambda: FakeReader(_bgra()))
    for bad in [{"seq": "2"}, {"seq": None}, {"seq": True}, {"seq": 0},
                {"seq": 2, "width": "6"}, {"seq": 2, "height": None}]:
        with pytest.raises(RpcInvalidParams):
            handler(bad)


def test_spot_handler_seq_size_mismatch_raises_invalid_params():
    # The {seq,width,height} request must describe the frame the section
    # actually holds for that seq; anything else is bad params (-32602).
    reader = FakeReader(_bgra(w=6, h=4))
    handler = make_spot_handler(FakeEngine(), reader_factory=lambda: reader)

    with pytest.raises(RpcInvalidParams):
        handler({"seq": 2, "width": 7, "height": 4})  # width mismatch
    with pytest.raises(RpcInvalidParams):
        handler({"seq": 2, "width": 6, "height": 5})  # height mismatch
    with pytest.raises(RpcInvalidParams):
        handler({"seq": 2, "width": 0, "height": 4})  # bogus zero width


def test_handle_line_seq_size_mismatch_maps_to_32602():
    reader = FakeReader(_bgra(w=6, h=4))
    handlers = {"ocr.spot": make_spot_handler(FakeEngine(), reader_factory=lambda: reader)}
    req = json.dumps(
        {"jsonrpc": "2.0", "id": "1", "method": "ocr.spot", "params": {"seq": 2, "width": 1920, "height": 1080}}
    )
    resp = json.loads(handle_line(req, handlers))
    assert resp["error"]["code"] == -32602


def test_spot_handler_seq_shm_unavailable_propagates_not_invalid_params():
    # A failed shm read is a transport failure, not bad params: it must surface
    # as JSON-RPC -32603 (internal error), never -32602.
    class UnavailableReader:
        def read(self, expected_seq: int) -> np.ndarray:
            raise ShmUnavailable(f"seq {expected_seq} never appeared")

    handler = make_spot_handler(FakeEngine(), reader_factory=UnavailableReader)
    with pytest.raises(ShmUnavailable):
        handler({"seq": 2, "width": 6, "height": 4})


def test_handle_line_seq_malformed_maps_to_32602():
    handlers = {"ocr.spot": make_spot_handler(FakeEngine(), reader_factory=lambda: FakeReader(_bgra()))}
    req = json.dumps({"jsonrpc": "2.0", "id": "1", "method": "ocr.spot", "params": {"seq": "x"}})
    resp = json.loads(handle_line(req, handlers))
    assert resp["error"]["code"] == -32602


def test_handle_line_seq_shm_failure_maps_to_32603():
    class UnavailableReader:
        def read(self, expected_seq: int) -> np.ndarray:
            raise ShmUnavailable("frame never appeared")

    handlers = {"ocr.spot": make_spot_handler(FakeEngine(), reader_factory=UnavailableReader)}
    req = json.dumps({"jsonrpc": "2.0", "id": "1", "method": "ocr.spot", "params": {"seq": 2}})
    resp = json.loads(handle_line(req, handlers))
    assert resp["error"]["code"] == -32603


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
