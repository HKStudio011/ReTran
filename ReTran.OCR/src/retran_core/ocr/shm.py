"""Reader side of the raw OCR frame transport (Core writes, sidecar reads).

Layout (little-endian, mirrored byte-for-byte by ``ReTran.Core/Ocr/OcrShm.cs``):
- off 0   4  magic ``RTOC`` (0x52 0x54 0x4F 0x43)
- off 4   4  version = 1
- off 8   8  seq (seqlock: odd = write in progress, even = frame stable)
- off 16  4  width
- off 20  4  height
- off 24  4  stride (= width * 4)
- off 28  4  format = 1 (BGRA32)
- off 32     reserved, zero up to the payload
- off 64     BGRA32 payload (height * stride bytes)

Opening a name that does not exist does NOT fail on Windows: ``mmap`` with
``ACCESS_READ`` creates a zero-filled section instead (Task 1 spike). The
reader therefore validates the header magic/version after mapping and closes
the provisional mapping when they do not match — otherwise the sidecar would
pin the section alive read-only and Core could never (re)create it read-write.
"""
from __future__ import annotations

import mmap
import struct
import time

import numpy as np

SHM_NAME = r"Local\ReTran.Ocr.V1"
MAGIC = b"RTOC"
VERSION = 1
FORMAT_BGRA32 = 1

# Header field offsets (little-endian).
OFFSET_VERSION = 4
OFFSET_SEQ = 8
OFFSET_WIDTH = 16
OFFSET_HEIGHT = 20
OFFSET_STRIDE = 24
OFFSET_FORMAT = 28

HEADER_SIZE = 64
PAYLOAD_OFFSET = HEADER_SIZE
MAX_WIDTH = 3840
MAX_HEIGHT = 2160
PAYLOAD_CAPACITY = MAX_WIDTH * MAX_HEIGHT * 4
TOTAL_SIZE = HEADER_SIZE + PAYLOAD_CAPACITY

# Bounded wait for the requested seq to appear in the section.
_WAIT_FOR_SEQ_SECONDS = 1.0
# Seqlock retry budget for torn reads (payload overwritten mid-copy).
_MAX_TORN_RETRIES = 100
_POLL_SECONDS = 0.001


class ShmUnavailable(RuntimeError):
    """The OCR frame shared memory is missing, invalid, or never reached the requested seq."""


class ShmReader:
    """Reads BGRA frames Core published into a named shared-memory section.

    The section is mapped lazily on the first :meth:`read` and kept open for
    the reader's lifetime ("map once" — the capacity is fixed, so the section
    is never recreated). Only an invalid header (missing section, wrong
    magic/version) makes the reader close its mapping again; a later read then
    re-opens and re-validates it.

    Attributes:
        name: Windows mapping name this reader opens.
    """

    def __init__(self, name: str = SHM_NAME) -> None:
        self._name = name
        self._section: mmap.mmap | None = None

    def read(self, expected_seq: int) -> np.ndarray:
        """Return the stable frame published under ``expected_seq``.

        Waits up to ~1 s for the section's seqlock counter to reach
        ``expected_seq`` (even = stable), then copies the payload out under
        seqlock rules: a frame overwritten mid-read (torn) — or one whose
        header is invalid mid-copy, i.e. caught in a writer's update window —
        is discarded and retried, at most 100 times and never past the ~1 s
        deadline.

        Args:
            expected_seq: The stable seq ``OcrShmWriter.Publish`` returned for
                the frame Core wants read.

        Returns:
            An HxWx4 ``uint8`` BGRA array owned by the caller (slice
            ``[:, :, :3]`` for the BGR view the engine expects).

        Raises:
            ShmUnavailable: The section cannot be opened, its header is not a
                ReTran OCR section, the expected seq never appeared, or reads
                stayed torn (or the header stayed invalid) past the retry
                budget.
        """
        section = self._ensure_section()
        deadline = time.monotonic() + _WAIT_FOR_SEQ_SECONDS
        torn = 0
        while True:
            (seq_before,) = struct.unpack_from("<q", section, OFFSET_SEQ)
            if seq_before != expected_seq or seq_before % 2 != 0:
                if time.monotonic() >= deadline:
                    raise ShmUnavailable(
                        f"frame seq {expected_seq} in shared memory {self._name!r} was not "
                        f"readable within {_WAIT_FOR_SEQ_SECONDS:.0f}s (header seq {seq_before}, "
                        f"{torn} torn read(s) discarded)"
                    )
                time.sleep(_POLL_SECONDS)
                continue

            try:
                frame = self._copy_frame(section)
            except ShmUnavailable as exc:
                # A frame header that is invalid while the seqlock reads even
                # is a torn window (the writer was mid-update when we copied):
                # retry like a torn read instead of failing the transport.
                torn += 1
                if torn >= _MAX_TORN_RETRIES or time.monotonic() >= deadline:
                    raise ShmUnavailable(
                        f"frame header at seq {expected_seq} in shared memory "
                        f"{self._name!r} stayed invalid for {torn} read(s) "
                        f"(cap {_MAX_TORN_RETRIES} or {_WAIT_FOR_SEQ_SECONDS:.0f}s deadline)"
                    ) from exc
                time.sleep(_POLL_SECONDS)
                continue

            (seq_after,) = struct.unpack_from("<q", section, OFFSET_SEQ)
            if seq_after == seq_before:
                return frame
            # Torn: a writer moved on while we were copying — discard and retry.
            torn += 1
            if torn >= _MAX_TORN_RETRIES or time.monotonic() >= deadline:
                raise ShmUnavailable(
                    f"frame at seq {expected_seq} in shared memory {self._name!r} stayed torn "
                    f"after {torn} read(s) (cap {_MAX_TORN_RETRIES} or "
                    f"{_WAIT_FOR_SEQ_SECONDS:.0f}s deadline)"
                )
            time.sleep(_POLL_SECONDS)

    def _ensure_section(self) -> mmap.mmap:
        """Open + validate the mapping once; close it again when the header is not ours."""
        if self._section is not None:
            return self._section
        try:
            section = mmap.mmap(-1, TOTAL_SIZE, tagname=self._name, access=mmap.ACCESS_READ)
        except (OSError, ValueError) as exc:
            raise ShmUnavailable(f"cannot open shared memory {self._name!r}: {exc}") from exc
        try:
            (version,) = struct.unpack_from("<i", section, OFFSET_VERSION)
            magic = bytes(section[0:4])
            if magic != MAGIC or version != VERSION:
                raise ShmUnavailable(
                    f"shared memory {self._name!r} is not a ReTran OCR section "
                    f"(magic {magic.hex(' ')}, version {version})"
                )
        except Exception:
            # Release the provisional mapping so Core can create the section
            # read-write later (a leaked ACCESS_READ handle would pin it
            # read-only forever).
            section.close()
            raise
        self._section = section
        return section

    def _copy_frame(self, section: mmap.mmap) -> np.ndarray:
        """Snapshot the payload into an owned array (never a view of the section)."""
        width, height, stride, fmt = struct.unpack_from("<4i", section, OFFSET_WIDTH)
        if (
            fmt != FORMAT_BGRA32
            or width <= 0
            or height <= 0
            or width > MAX_WIDTH
            or height > MAX_HEIGHT
            or stride != width * 4
        ):
            raise ShmUnavailable(
                f"invalid frame header in shared memory {self._name!r} "
                f"({width}x{height}, stride {stride}, format {fmt})"
            )
        length = height * stride
        view = np.frombuffer(section, dtype=np.uint8, count=length, offset=PAYLOAD_OFFSET)
        return view.reshape(height, width, 4).copy()
