"""Entry point: `python -m retran_core serve` runs the JSON-RPC server on stdio."""
from __future__ import annotations

import sys

from . import __version__
from .jsonrpc import serve
from .ocr.handlers import make_spot_handler
from .ocr.paddle_engine import PaddleOcrEngine


def _handlers():
    return {
        "sidecar.ping": lambda p: "pong",
        "sidecar.version": lambda p: __version__,
        "ocr.spot": make_spot_handler(PaddleOcrEngine(lang="en")),
    }


def main(argv=None) -> int:
    """Run the sidecar.

    Args:
        argv: Argument list (defaults to ``sys.argv[1:]``). Only ``serve`` is supported.

    Returns:
        Process exit code: 0 on success, 2 on unknown usage.
    """
    argv = list(sys.argv[1:] if argv is None else argv)
    if not argv or argv[0] == "serve":
        serve(_handlers())
        return 0
    print(f"usage: python -m retran_core serve", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
