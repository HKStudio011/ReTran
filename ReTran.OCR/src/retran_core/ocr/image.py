"""PNG base64 decode to the BGR array paddle expects (with M3 downscale cap)."""
from __future__ import annotations

import base64
import io

import numpy as np
import numpy.typing as npt
from PIL import Image

MAX_INPUT_DIM = 1280


def decode_png_b64(image_b64: str) -> npt.NDArray[np.uint8]:
    """Decode a base64 PNG string to an HWC uint8 BGR array.

    Inputs whose long side exceeds ``MAX_INPUT_DIM`` (1280) are downscaled
    with aspect preserved before conversion — detection latency scales with
    input size (Docs/performance.md M2 baseline).

    Args:
        image_b64: Base64-encoded PNG bytes, no data-url prefix.

    Returns:
        Image pixels in BGR channel order (paddle/OpenCV convention).

    Raises:
        ValueError: The input is not valid base64 or not a decodable PNG.
    """
    try:
        raw = base64.b64decode(image_b64, validate=True)
        rgb = Image.open(io.BytesIO(raw)).convert("RGB")
    except Exception as exc:
        raise ValueError(f"invalid PNG image: {exc}") from exc
    rgb.thumbnail((MAX_INPUT_DIM, MAX_INPUT_DIM), Image.Resampling.BILINEAR)
    return np.asarray(rgb, dtype=np.uint8)[:, :, ::-1].copy()
