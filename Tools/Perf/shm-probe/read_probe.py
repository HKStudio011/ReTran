"""Reader probe: read the OCR section through the PRODUCTION ``ShmReader``.

Cross-language contract check for the OCR frame transport (Docs/superpowers/
plans/2026-10-22-ocr-models-shm.md): ``CSharpWriter`` publishes one known
BGRA frame through the production ``ReTran.Core`` ``OcrShmWriter`` into the
probe-only section ``Local\\ReTran.Ocr.Probe`` (never the production
``Local\\ReTran.Ocr.V1``), and this script reads it back through the
production ``retran_core.ocr.shm.ShmReader``. That checks the real header
layout the two sides ship (magic ``RTOC``, version 1, seqlock seq, width,
height, stride, format BGRA32) plus payload bytes — not a hand-mirrored spike
layout. Any mismatch exits non-zero.

Usage (from the repo root; ``--project`` so uv resolves the ReTran.OCR
environment — ``--directory`` does NOT work here):

    uv run --project "D:\\Code\\ReTran Project\\ReTran.OCR" python Tools/Perf/shm-probe/read_probe.py [tagname ...]

Defaults to the probe section name above; pass another tagname to check a
different section. Start CSharpWriter first — it creates the section and
holds it; run this probe while it prints ``READY``.
"""
from __future__ import annotations

import mmap
import struct
import sys

import numpy as np

from retran_core.ocr.shm import (
    FORMAT_BGRA32,
    MAGIC,
    OFFSET_FORMAT,
    OFFSET_HEIGHT,
    OFFSET_SEQ,
    OFFSET_STRIDE,
    OFFSET_VERSION,
    OFFSET_WIDTH,
    TOTAL_SIZE,
    VERSION,
    ShmReader,
    ShmUnavailable,
)

# Must match CSharpWriter/Program.cs — both sides derive the same bytes.
PROBE_NAME = r"Local\ReTran.Ocr.Probe"
WIDTH, HEIGHT = 64, 32
STRIDE = WIDTH * 4


def expected_payload() -> np.ndarray:
    """Deterministic HxWx4 BGRA payload: byte i = (i * 13 + 5) % 251."""
    ramp = np.arange(WIDTH * HEIGHT * 4, dtype=np.uint64)
    return ((ramp * 13 + 5) % 251).astype(np.uint8).reshape(HEIGHT, WIDTH, 4)


def probe(name: str) -> bool:
    """Check one section name against the production writer's frame.

    Opens the named section read-only to validate the published header
    (magic, version, seq, width, height, stride, format), then reads the frame
    through the production :class:`ShmReader` and samples payload bytes
    against the deterministic payload both sides derive.

    Args:
        name: Windows mapping name the C# writer published.

    Returns:
        True when every check matched; False otherwise (details printed).
    """
    ok = True

    # Header pass: raw read-only view of the section the writer holds.
    try:
        section = mmap.mmap(-1, TOTAL_SIZE, tagname=name, access=mmap.ACCESS_READ)
    except (OSError, ValueError) as exc:
        print(f"[probe] opening {name!r} FAILED: {type(exc).__name__}: {exc}")
        print(f"[probe]   (start CSharpWriter first — it creates and holds the section)")
        return False
    try:
        magic = bytes(section[0:4])
        (version,) = struct.unpack_from("<i", section, OFFSET_VERSION)
        (seq,) = struct.unpack_from("<q", section, OFFSET_SEQ)
        width, height, stride, fmt = struct.unpack_from("<4i", section, OFFSET_WIDTH)
    finally:
        section.close()

    print(f"[probe] {name!r}: header")
    for label, got, want in (
        ("magic", magic, MAGIC),
        ("version", version, VERSION),
        ("width", width, WIDTH),
        ("height", height, HEIGHT),
        ("stride", stride, STRIDE),
        ("format", fmt, FORMAT_BGRA32),
    ):
        good = got == want
        ok = ok and good
        shown = got.hex(" ") if isinstance(got, (bytes, bytearray)) else got
        print(f"[probe]   {label:<8} = {shown!s:<12} expected {want!s:<12} -> {'ok' if good else 'BAD'}")
    if seq % 2 != 0:
        ok = False
    print(f"[probe]   seq      = {seq} ({'stable (even)' if seq % 2 == 0 else 'write in progress (odd) — BAD'})")

    # Payload pass: through the production reader (waits for the seq above).
    try:
        frame = ShmReader(name).read(seq)
    except ShmUnavailable as exc:
        print(f"[probe]   ShmReader.read({seq}) FAILED: {exc}")
        return False

    want = expected_payload()
    print(f"[probe] {name!r}: payload via production ShmReader")
    shape_ok = frame.shape == (HEIGHT, WIDTH, 4)
    ok = ok and shape_ok
    print(f"[probe]   shape    = {frame.shape} expected ({HEIGHT}, {WIDTH}, 4) -> {'ok' if shape_ok else 'BAD'}")
    # Sample the first, middle and last payload bytes (full equality is cheap
    # at this size, so check it too).
    samples = [(0, 0, 0), (HEIGHT // 2, WIDTH // 2, 1), (HEIGHT - 1, WIDTH - 1, 3)]
    for pos in samples:
        good = frame[pos] == want[pos]
        ok = ok and good
        print(f"[probe]   byte {pos!s:<15} = {int(frame[pos]):>3} expected {int(want[pos]):>3} -> {'ok' if good else 'BAD'}")
    full_ok = bool(np.array_equal(frame, want))
    ok = ok and full_ok
    print(f"[probe]   full payload equal -> {'ok' if full_ok else 'BAD'}")

    print(f"[probe] {name!r}: {'MATCH' if ok else 'MISMATCH'}")
    return ok


def main(argv: list[str]) -> int:
    """Probe each requested section name (default: the probe section).

    Args:
        argv: Script arguments; ``argv[1:]`` overrides the section names.

    Returns:
        Process exit code — 0 when every check matched, 1 otherwise.
    """
    names = argv[1:] or [PROBE_NAME]
    matched = all(probe(name) for name in names)
    print(f"[probe] result: {'MATCH' if matched else 'NO MATCH'}")
    return 0 if matched else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
