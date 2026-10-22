"""Reader probe: open the named shared-memory section written by CSharpWriter.

Task 1 spike (Docs/superpowers/plans/2026-10-22-ocr-models-shm.md): prove that
Python ``mmap`` can open the .NET named mapping ``Local\\ReTran.Ocr.V1`` and
read back the exact bytes the C# writer put there, so Task 8 can build the
OCR image transport on it.

Layout mirrored from ``CSharpWriter/Program.cs`` (little-endian):
- off 0: 4-byte magic ``[0x4F, 0x43, 0x52, 0x31]`` ("OCR1")
- off 8: int64 seq = 7

Usage:
    uv run --directory ReTran.OCR python Tools/Perf/shm-probe/read_probe.py [tagname ...]

Tries each candidate tagname in order (defaults: ``Local\\ReTran.Ocr.V1``, then
the namespace-less ``ReTran.Ocr.V1``), prints exactly what it read per name,
and exits 0 when at least one candidate returned the expected magic and seq.
"""
from __future__ import annotations

import mmap
import struct
import sys

MAP_SIZE = 64
OFFSET_MAGIC = 0
OFFSET_SEQ = 8
EXPECTED_MAGIC = bytes((0x4F, 0x43, 0x52, 0x31))
EXPECTED_SEQ = 7
DEFAULT_NAMES = (r"Local\ReTran.Ocr.V1", r"ReTran.Ocr.V1")


def read_one(tagname: str) -> tuple[bytes, int]:
    """Open the named section read-only and read the magic + seq pair.

    Args:
        tagname: Windows mapping name, with or without the ``Local\\``
            namespace prefix.

    Returns:
        ``(magic, seq)`` — the 4 raw bytes at offset 0 and the int64 at
        offset 8.

    Raises:
        OSError: The mapping cannot be opened (missing name, access denied).
        ValueError: ``mmap`` refuses to open a read-only view of the name.
    """
    section = mmap.mmap(-1, MAP_SIZE, tagname=tagname, access=mmap.ACCESS_READ)
    try:
        magic = bytes(section[OFFSET_MAGIC : OFFSET_MAGIC + 4])
        (seq,) = struct.unpack_from("<q", section, OFFSET_SEQ)
    finally:
        section.close()
    return magic, seq


def main(argv: list[str]) -> int:
    """Probe each candidate name and report whether the writer's bytes came back.

    Args:
        argv: Script arguments; ``argv[1:]`` overrides the candidate names.

    Returns:
        Process exit code — 0 when at least one name matched, 1 otherwise.
    """
    names = argv[1:] or list(DEFAULT_NAMES)
    matched = False
    for name in names:
        print(f"[probe] opening {name!r} (mmap ACCESS_READ, {MAP_SIZE} bytes)")
        try:
            magic, seq = read_one(name)
        except (OSError, ValueError) as exc:
            print(f"[probe]   OPEN FAILED: {type(exc).__name__}: {exc}")
            continue
        text = "".join(chr(b) if 32 <= b < 127 else "." for b in magic)
        magic_ok = magic == EXPECTED_MAGIC
        seq_ok = seq == EXPECTED_SEQ
        matched = matched or (magic_ok and seq_ok)
        print(
            f"[probe]   magic = [{magic.hex(' ')}] ('{text}') "
            f"expected [{EXPECTED_MAGIC.hex(' ')}] -> {'ok' if magic_ok else 'BAD'}"
        )
        print(f"[probe]   seq   = {seq} (int64 LE) expected {EXPECTED_SEQ} -> {'ok' if seq_ok else 'BAD'}")
        print(f"[probe]   {'MATCH' if magic_ok and seq_ok else 'MISMATCH'}")
    print(f"[probe] result: {'MATCH' if matched else 'NO MATCH'}")
    return 0 if matched else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
