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
    model_cache_dir,
)
from .ocr.paddle_engine import PaddleOcrEngine
from .ocr.shm import ShmReader


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
        # ShmReader is handed over as the reader factory: the sidecar maps the
        # OCR shared-memory section lazily, on the first {seq} spot request.
        "ocr.spot": make_spot_handler(engine, reader_factory=ShmReader),
    }
    handlers.update(make_model_handlers(manager))
    handlers.update(make_config_handlers(engine.config))
    return handlers


def _prepare_model_cache() -> None:
    """Sync the paddlex env vars and run the one-shot `~/.paddlex` migration.

    Both ``PADDLE_PDX_CACHE_HOME`` and ``PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK``
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
