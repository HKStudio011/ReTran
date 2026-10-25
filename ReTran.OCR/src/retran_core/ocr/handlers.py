"""JSON-RPC handler factory for ocr.spot (engine injected for testability)."""
from __future__ import annotations

import time
from typing import Any, Callable, Optional

from ..jsonrpc import RpcInvalidParams
from .engine import OcrEngine, Spot
from .image import decode_image_b64_with_size
from .shm import ShmUnavailable

Handler = Callable[[Optional[dict]], Any]


def _is_int(value: Any) -> bool:
    """JSON integer check that rejects booleans (bool subclasses int)."""
    return isinstance(value, int) and not isinstance(value, bool)


def make_spot_handler(
    engine: OcrEngine,
    reader_factory: Optional[Callable[[], Any]] = None,
) -> Handler:
    """Build the ocr.spot handler bound to an engine.

    Args:
        engine: Engine used for every call; not owned by the handler.
        reader_factory: Zero-arg callable returning the shared-memory reader
            used for ``{seq, width, height}`` params. Called at most once (on
            the first such request) and cached, so the mapping opens lazily
            and exactly once per sidecar process. When ``None``, ``{seq}``
            params are rejected with RpcInvalidParams and only the base64
            ``{image}`` path works.

    Returns:
        A JSON-RPC handler for two transports. Params ``{seq, width, height}``
        read the full-res BGRA frame from shared memory (no downscale, boxes
        already in frame pixels). Params ``{image}`` decode a base64 PNG/JPEG
        and map box coordinates back to the submitted image's pixels when the
        engine ran on a downscaled copy. Both return ``{spots, elapsed_ms}``.

    Raises:
        RpcInvalidParams: Missing/undecodable ``{image}``, malformed ``{seq}``
            params, ``{seq}`` params without a configured reader factory, or
            ``width``/``height`` not matching the frame actually read from
            shared memory.
        ShmUnavailable: The shared-memory read failed (maps to JSON-RPC
            -32603, never -32602 — the params were valid).
    """
    reader: Any = None

    def spot(params: Optional[dict]) -> dict:
        nonlocal reader
        if isinstance(params, dict) and "seq" in params:
            seq = params["seq"]
            if not _is_int(seq) or seq <= 0:
                raise RpcInvalidParams("missing or invalid 'seq' (positive integer frame id)")
            for key in ("width", "height"):
                if key in params and not _is_int(params[key]):
                    raise RpcInvalidParams(f"invalid '{key}' (integer expected)")
            if reader_factory is None:
                raise RpcInvalidParams("'seq' params require a shared-memory reader (none configured)")
            if reader is None:
                reader = reader_factory()
            t0 = time.perf_counter()
            # Full-res frame straight from shared memory: the engine sees the
            # real pixels, so its boxes are already in frame coordinates.
            bgra = reader.read(seq)
            # Cross-check the request's {width, height} against the frame the
            # section actually holds for this seq: a mismatch means the request
            # describes a different (e.g. overwritten) frame — bad params, not
            # a transport failure.
            height, width = bgra.shape[0], bgra.shape[1]
            if "width" in params and params["width"] != width:
                raise RpcInvalidParams(
                    f"width mismatch: request {params['width']}, frame {width}"
                )
            if "height" in params and params["height"] != height:
                raise RpcInvalidParams(
                    f"height mismatch: request {params['height']}, frame {height}"
                )
            spots = engine.spot(bgra[:, :, :3])
            return {"spots": [s.to_dict() for s in spots], "elapsed_ms": int((time.perf_counter() - t0) * 1000)}

        if not isinstance(params, dict) or not isinstance(params.get("image"), str) or not params["image"]:
            raise RpcInvalidParams("missing or invalid 'image' (non-empty base64 PNG/JPEG string)")
        t0 = time.perf_counter()
        try:
            image_bgr, (src_w, src_h) = decode_image_b64_with_size(params["image"])
        except ValueError as exc:
            raise RpcInvalidParams(str(exc)) from exc
        spots = engine.spot(image_bgr)

        # The engine sees the downscaled array (MAX_INPUT_DIM); map its pixel
        # coordinates back to the submitted image so Core can normalize them
        # against the captured frame's real size.
        dec_h, dec_w = image_bgr.shape[:2]
        if dec_w > 0 and dec_h > 0 and (src_w != dec_w or src_h != dec_h):
            sx, sy = src_w / dec_w, src_h / dec_h
            spots = [
                Spot(
                    box=[[float(x) * sx, float(y) * sy] for x, y in s.box],
                    text=s.text,
                    score=s.score,
                )
                for s in spots
            ]

        elapsed_ms = int((time.perf_counter() - t0) * 1000)
        return {"spots": [s.to_dict() for s in spots], "elapsed_ms": elapsed_ms}

    return spot
