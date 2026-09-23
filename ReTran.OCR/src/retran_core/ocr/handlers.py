"""JSON-RPC handler factory for ocr.spot (engine injected for testability)."""
from __future__ import annotations

import time
from typing import Any, Callable, Optional

from ..jsonrpc import RpcInvalidParams
from .engine import OcrEngine
from .image import decode_png_b64

Handler = Callable[[Optional[dict]], Any]


def make_spot_handler(engine: OcrEngine) -> Handler:
    """Build the ocr.spot handler bound to an engine.

    Args:
        engine: Engine used for every call; not owned by the handler.

    Returns:
        A JSON-RPC handler: params ``{image}`` -> ``{spots, elapsed_ms}``;
        raises RpcInvalidParams for missing/undecodable images.
    """

    def spot(params: Optional[dict]) -> dict:
        if not isinstance(params, dict) or not isinstance(params.get("image"), str) or not params["image"]:
            raise RpcInvalidParams("missing or invalid 'image' (non-empty base64 PNG string)")
        t0 = time.perf_counter()
        try:
            image_bgr = decode_png_b64(params["image"])
        except ValueError as exc:
            raise RpcInvalidParams(str(exc)) from exc
        spots = engine.spot(image_bgr)
        elapsed_ms = int((time.perf_counter() - t0) * 1000)
        return {"spots": [s.to_dict() for s in spots], "elapsed_ms": elapsed_ms}

    return spot
