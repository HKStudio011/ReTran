"""Newline-delimited JSON-RPC 2.0 server helpers (stdlib only).

Each request is one line of JSON on stdin; each response is one line of JSON
on stdout. This keeps the framing identical to the C# core's transport so the
two sides can be tested against each other without extra tooling.
"""
from __future__ import annotations

import json
from typing import Any, Callable, Dict, Optional

Handler = Callable[[Optional[dict]], Any]


def _resp(id_: Any, result: Any) -> str:
    return json.dumps({"jsonrpc": "2.0", "id": id_, "result": result}, ensure_ascii=False)


def _err(id_: Any, code: int, message: str) -> str:
    return json.dumps(
        {"jsonrpc": "2.0", "id": id_, "error": {"code": code, "message": message}},
        ensure_ascii=False,
    )


def handle_line(line: str, handlers: Dict[str, Handler]) -> Optional[str]:
    """Process one request line; return one response line, or None for blank lines.

    Args:
        line: A single line read from stdin (may be empty).
        handlers: Mapping of method name to handler callable.

    Returns:
        One JSON-RPC 2.0 response line, or ``None`` when the input line is blank.
    """
    if not line.strip():
        return None
    try:
        req = json.loads(line)
    except json.JSONDecodeError:
        return _err(None, -32700, "Parse error")
    if not isinstance(req, dict) or req.get("jsonrpc") != "2.0" or "method" not in req:
        return _err(req.get("id") if isinstance(req, dict) else None, -32600, "Invalid Request")
    id_ = req.get("id")
    method = req["method"]
    params = req.get("params")
    handler = handlers.get(method)
    if handler is None:
        return _err(id_, -32601, f"Method not found: {method}")
    try:
        return _resp(id_, handler(params))
    except Exception as exc:  # noqa: BLE001 - report any handler failure as internal error
        return _err(id_, -32603, str(exc))


def serve(handlers: Dict[str, Handler]) -> None:
    """Read request lines from stdin and write response lines to stdout until EOF.

    Args:
        handlers: Mapping of method name to handler callable.
    """
    import sys

    for line in sys.stdin:
        out = handle_line(line, handlers)
        if out is not None:
            print(out, flush=True)
