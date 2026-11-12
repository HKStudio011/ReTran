"""Entry point: `python -m retran_core serve` runs the JSON-RPC server on stdio."""
from __future__ import annotations

import os
import sys

from . import __version__
from .jsonrpc import serve
from .ocr.catalog import recommended
from .ocr.config import make_config_handlers
from .ocr.handlers import make_spot_handler
from .ocr.models import (
    ModelManager,
    make_model_handlers,
    migrate_from_paddlex,
    migrate_from_retran_legacy,
    model_cache_dir,
)
from .ocr.paddle_engine import PaddleOcrEngine
from .ocr.shm import ShmReader


def _device_info() -> dict:
    """Report the paddle device and its CUDA compute capability for setup verification.

    The first-run wizard calls this after the sidecar ping to gate the GPU
    flavor on the card's compute capability (the PaddlePaddle 3.4.0 cu129
    wheels only fatbin sm_75 and newer). ``paddle`` is imported inside the
    body on purpose: the engine must stay lazy, so serving this verb never
    pays the paddle import cost at sidecar startup.

    Returns:
        ``{"device": <str(paddle.device.get_device())>,
        "capability": [major, minor] | None}``. Two tiers of failure: when
        paddle cannot be imported or report the current device at all, BOTH
        values are ``None``; when the device is known but probing its compute
        capability fails, ``device`` is kept and only ``capability`` is
        ``None``. Both failures carry a short ``error`` field. A CPU-only
        build (or no visible GPU) is not a failure: it simply reports
        ``capability: None`` without an error. The sidecar never dies because
        of this probe.
    """
    try:
        import paddle

        device = str(paddle.device.get_device())
    except Exception as exc:  # noqa: BLE001 - report, never kill the sidecar
        return {"device": None, "capability": None, "error": f"{type(exc).__name__}: {exc}"[:200]}

    capability = None
    if paddle.device.is_compiled_with_cuda() and paddle.device.cuda.device_count() > 0:
        try:
            major, minor = paddle.device.cuda.get_device_capability()
            capability = [int(major), int(minor)]
        except Exception as exc:  # noqa: BLE001 - keep the device, report the probe failure
            return {"device": device, "capability": None, "error": f"{type(exc).__name__}: {exc}"[:200]}
    return {"device": device, "capability": capability}


def _handlers():
    # One engine instance shared by ocr.spot, the model handlers and the
    # config handlers, so ocr.models.set switches the engine the next spot
    # call will use and ocr.config.set targets the engine's parameter map.
    # The engine stays lazy: models load on the first spot call.
    det, rec = recommended("auto", "vi")
    engine = PaddleOcrEngine(det, rec)
    manager = ModelManager(model_cache_dir(), engine=engine)
    handlers = {
        "sidecar.ping": lambda p: "pong",
        "sidecar.version": lambda p: __version__,
        # Lazy probe (paddle imports inside the handler body): the setup wizard
        # gates the GPU flavor on the reported compute capability.
        "sidecar.device": lambda p: _device_info(),
        # ShmReader is handed over as the reader factory: the sidecar maps the
        # OCR shared-memory section lazily, on the first {seq} spot request.
        "ocr.spot": make_spot_handler(engine, reader_factory=ShmReader),
    }
    handlers.update(make_model_handlers(manager))
    handlers.update(make_config_handlers(engine.config))
    return handlers


def _prepare_model_cache() -> None:
    """Sync the paddlex env vars and run the one-shot model-cache migrations.

    Runs both one-shot copies: from the legacy ``~/.paddlex`` cache and from
    ReTran's own previous location (``%LOCALAPPDATA%\\ReTran\\models``). Both
    ``PADDLE_PDX_CACHE_HOME`` and ``PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK``
    are read by paddlex at import time, so they must be pinned here — before
    any lazy paddlex/paddleocr import later in the process. Best-effort: a
    migration failure is logged and never kills the sidecar (the affected
    models simply stay "not installed" and can be downloaded again).
    """
    cache_dir = model_cache_dir()
    # paddlex reads PADDLE_PDX_CACHE_HOME at import time (paddlex/cache.py),
    # so pin it before any lazy paddlex/paddleocr import: the manager and
    # paddlex must agree on the directory also when spawned standalone (the
    # C# core sets the same env when it spawns the sidecar).
    os.environ.setdefault("PADDLE_PDX_CACHE_HOME", str(cache_dir))
    # Skip paddlex's import-time hoster connectivity checks (up to four HEAD
    # requests with 1 s timeouts): downloads already fall back between
    # hosters on failure, so the checks only add startup latency and noise.
    # Also read at paddlex import time, so it must run before any lazy
    # paddlex/paddleocr import. setdefault keeps Core's explicit value
    # authoritative when it spawns the sidecar.
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
    try:
        migrated = migrate_from_paddlex(cache_dir)
    except Exception as exc:  # noqa: BLE001 - migration must never be fatal
        print(f"model migration failed: {exc}", file=sys.stderr, flush=True)
        return
    if migrated:
        print(
            f"migrated model(s) from ~/.paddlex: {', '.join(migrated)}",
            file=sys.stderr,
            flush=True,
        )
    try:
        migrated = migrate_from_retran_legacy(cache_dir)
    except Exception as exc:  # noqa: BLE001 - migration must never be fatal
        print(f"legacy ReTran model migration failed: {exc}", file=sys.stderr, flush=True)
        return
    if migrated:
        print(
            f"migrated model(s) from legacy ReTran cache: {', '.join(migrated)}",
            file=sys.stderr,
            flush=True,
        )


def main(argv=None) -> int:
    """Run the sidecar.

    Args:
        argv: Argument list (defaults to ``sys.argv[1:]``). Only ``serve`` is supported.

    Returns:
        Process exit code: 0 on success, 2 on unknown usage.
    """
    argv = list(sys.argv[1:] if argv is None else argv)
    if not argv or argv[0] == "serve":
        _prepare_model_cache()
        serve(_handlers())
        return 0
    print(f"usage: python -m retran_core serve", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
