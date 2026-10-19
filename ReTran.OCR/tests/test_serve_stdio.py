"""Regression: serve stdio must stay UTF-8 even when the pipe encoding is cp1252.

Giải thích: on Windows a redirected pipe defaults to the ANSI codepage (cp1252), so
`print` in serve() crashed the sidecar with UnicodeEncodeError as soon as OCR text
contained characters outside cp1252 (e.g. Vietnamese diacritics or U+2211) — the
Core then reported "sidecar-exited".
"""
import json
import os
import subprocess
import sys

_ENV = {**os.environ, "PYTHONIOENCODING": "cp1252"}


def _run_serve(snippet: str, request: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-c", snippet],
        input=(request + "\n").encode("utf-8"),
        capture_output=True,
        env=_ENV,
        timeout=30,
    )


def test_serve_result_outside_cp1252_does_not_kill_process():
    snippet = (
        "from retran_core.jsonrpc import serve\n"
        "serve({'hit': lambda p: 'SUM ∑ ẻ ✓'})\n"
    )
    proc = _run_serve(snippet, '{"jsonrpc":"2.0","id":1,"method":"hit"}')
    assert proc.returncode == 0, proc.stderr.decode("utf-8", errors="replace")
    resp = json.loads(proc.stdout.decode("utf-8"))
    assert resp["result"] == "SUM ∑ ẻ ✓"


def test_serve_decodes_non_ascii_params_as_utf8():
    snippet = (
        "from retran_core.jsonrpc import serve\n"
        "serve({'echo': lambda p: p})\n"
    )
    request = json.dumps({"jsonrpc": "2.0", "id": 2, "method": "echo",
                          "params": {"t": "∑ ẻ ✓"}})
    proc = _run_serve(snippet, request)
    assert proc.returncode == 0, proc.stderr.decode("utf-8", errors="replace")
    resp = json.loads(proc.stdout.decode("utf-8"))
    assert resp["result"] == {"t": "∑ ẻ ✓"}
