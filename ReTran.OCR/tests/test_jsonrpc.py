"""Tests for the stdlib JSON-RPC line handler."""
import json

from retran_core.jsonrpc import handle_line


def _handlers():
    return {
        "sidecar.ping": lambda p: "pong",
        "sidecar.version": lambda p: "0.1.0",
    }


def test_ping_returns_pong():
    req = json.dumps({"jsonrpc": "2.0", "id": "1", "method": "sidecar.ping", "params": {}})
    resp = json.loads(handle_line(req, _handlers()))
    assert resp["result"] == "pong"
    assert resp["id"] == "1"


def test_unknown_method_returns_not_found():
    req = json.dumps({"jsonrpc": "2.0", "id": "2", "method": "sidecar.nope", "params": {}})
    resp = json.loads(handle_line(req, _handlers()))
    assert resp["error"]["code"] == -32601


def test_blank_line_returns_none():
    assert handle_line("   ", _handlers()) is None
