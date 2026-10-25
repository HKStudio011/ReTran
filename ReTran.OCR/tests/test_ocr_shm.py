"""ShmReader contract tests: Python writes sections exactly like OcrShmWriter does.

Every test opens its own unique mapping name, so a run can never collide with
the real ``Local\\ReTran.Ocr.V1`` section (Core/sidecar) or with another test.
"""
from __future__ import annotations

import mmap
import struct
import threading
import time
import uuid

import numpy as np
import pytest

from retran_core.ocr.shm import (
    FORMAT_BGRA32,
    HEADER_SIZE,
    MAGIC,
    MAX_HEIGHT,
    MAX_WIDTH,
    OFFSET_FORMAT,
    OFFSET_HEIGHT,
    OFFSET_SEQ,
    OFFSET_STRIDE,
    OFFSET_VERSION,
    OFFSET_WIDTH,
    PAYLOAD_CAPACITY,
    SHM_NAME,
    TOTAL_SIZE,
    VERSION,
    ShmReader,
    ShmUnavailable,
)


def _unique_name() -> str:
    """A fresh mapping name per test (never the real OCR section name)."""
    return rf"Local\ReTran.Tests.OcrShm.{uuid.uuid4().hex}"


def _gradient(width: int, height: int, salt: int = 0) -> bytes:
    """Deterministic BGRA payload bytes for a width*height frame."""
    ramp = np.arange(width * height * 4, dtype=np.uint64)
    return ((ramp * 7 + salt) % 251).astype(np.uint8).tobytes()


def _open_writable(name: str) -> mmap.mmap:
    """Create (or open) the named section writable at the fixed protocol size."""
    return mmap.mmap(-1, TOTAL_SIZE, tagname=name)


def _publish(section: mmap.mmap, seq: int, width: int, height: int, pixels: bytes) -> None:
    """Write one frame exactly like OcrShmWriter: seq odd while writing, even when stable."""
    stride = width * 4
    struct.pack_into("<q", section, OFFSET_SEQ, seq | 1)
    section[0:4] = MAGIC
    struct.pack_into("<i", section, OFFSET_VERSION, VERSION)
    struct.pack_into("<4i", section, OFFSET_WIDTH, width, height, stride, FORMAT_BGRA32)
    section[HEADER_SIZE : HEADER_SIZE + height * stride] = pixels
    struct.pack_into("<q", section, OFFSET_SEQ, seq)


def _as_frame(pixels: bytes, width: int, height: int) -> np.ndarray:
    return np.frombuffer(pixels, dtype=np.uint8).reshape(height, width, 4)


def test_protocol_constants_are_pinned():
    """The name and fixed capacity are the cross-language contract with OcrShm.cs."""
    assert SHM_NAME == r"Local\ReTran.Ocr.V1"
    assert MAGIC == b"RTOC"
    assert VERSION == 1
    assert HEADER_SIZE == 64
    assert (MAX_WIDTH, MAX_HEIGHT) == (3840, 2160)
    assert PAYLOAD_CAPACITY == 3840 * 2160 * 4
    assert TOTAL_SIZE == HEADER_SIZE + PAYLOAD_CAPACITY
    # Header field offsets (little-endian): version, seq, width, height, stride, format.
    assert (OFFSET_VERSION, OFFSET_SEQ, OFFSET_WIDTH, OFFSET_HEIGHT, OFFSET_STRIDE, OFFSET_FORMAT) == (
        4,
        8,
        16,
        20,
        24,
        28,
    )


def test_read_returns_published_frame():
    name = _unique_name()
    pixels = _gradient(4, 3)
    section = _open_writable(name)
    try:
        _publish(section, seq=2, width=4, height=3, pixels=pixels)
        reader = ShmReader(name)

        frame = reader.read(2)

        assert frame.dtype == np.uint8
        assert frame.shape == (3, 4, 4)  # H x W x 4 (BGRA)
        assert np.array_equal(frame, _as_frame(pixels, 4, 3))
        # The mapping is cached: a second read must not re-open it.
        assert np.array_equal(reader.read(2), frame)
    finally:
        section.close()


def test_read_waits_for_expected_seq_to_appear():
    name = _unique_name()
    section = _open_writable(name)
    early, late = _gradient(4, 3), _gradient(4, 3, salt=17)
    timer = threading.Timer(0.1, lambda: _publish(section, seq=4, width=4, height=3, pixels=late))
    timer.daemon = True
    try:
        _publish(section, seq=2, width=4, height=3, pixels=early)
        reader = ShmReader(name)
        timer.start()
        t0 = time.monotonic()

        frame = reader.read(4)

        # It really waited for the seq-4 publish, and returned exactly that frame.
        assert time.monotonic() - t0 >= 0.05
        assert np.array_equal(frame, _as_frame(late, 4, 3))
    finally:
        timer.join(2)
        section.close()


def test_read_wrong_magic_closes_provisional_section():
    # The name does not exist: opening it with ACCESS_READ creates a zero-filled
    # read-only section (Task 1 spike). The reader must reject it AND close it —
    # a leaked handle would keep the section alive read-only forever.
    name = _unique_name()
    reader = ShmReader(name)
    with pytest.raises(ShmUnavailable):
        reader.read(2)

    # If the provisional handle leaked, this writable open fails with OSError
    # (Windows returns the existing PAGE_READONLY section).
    section = _open_writable(name)
    try:
        pixels = _gradient(2, 2)
        _publish(section, seq=2, width=2, height=2, pixels=pixels)
        # The reader recovers on the next call: it re-opens and now succeeds.
        assert np.array_equal(reader.read(2), _as_frame(pixels, 2, 2))
    finally:
        section.close()


def test_read_seq_never_appears_times_out_after_bounded_wait():
    name = _unique_name()
    section = _open_writable(name)
    try:
        _publish(section, seq=2, width=4, height=3, pixels=_gradient(4, 3))
        reader = ShmReader(name)
        t0 = time.monotonic()

        with pytest.raises(ShmUnavailable):
            reader.read(8)

        # The bounded wait is real (it waited), but bounded (well under 5 s).
        assert time.monotonic() - t0 >= 0.5
        assert time.monotonic() - t0 < 5
    finally:
        section.close()


def test_read_write_in_progress_never_returns_partial_frame():
    # seq stuck odd = a publish never finished: the reader must not touch the payload.
    name = _unique_name()
    section = _open_writable(name)
    try:
        _publish(section, seq=2, width=4, height=3, pixels=_gradient(4, 3))
        struct.pack_into("<q", section, OFFSET_SEQ, 3)  # write in flight forever
        reader = ShmReader(name)
        with pytest.raises(ShmUnavailable):
            reader.read(3)
    finally:
        section.close()


def test_read_overwritten_mid_copy_is_never_returned_torn():
    # A 4K payload keeps the copy window open for milliseconds; a concurrent
    # "writer" flips the seq during the copy. The torn read must be discarded:
    # read() raises instead of ever returning a half-old half-new frame.
    name = _unique_name()
    section = _open_writable(name)
    width, height = MAX_WIDTH, MAX_HEIGHT
    _publish(section, seq=2, width=width, height=height, pixels=bytes([0xAB]) * (width * height * 4))

    def flip_seq() -> None:
        for _ in range(60):  # ~30 ms of flipping covers the whole copy window
            struct.pack_into("<q", section, OFFSET_SEQ, 3)
            time.sleep(0.0005)

    flipper = threading.Thread(target=flip_seq, daemon=True)
    flipper.start()
    try:
        reader = ShmReader(name)
        with pytest.raises(ShmUnavailable):
            reader.read(2)
    finally:
        flipper.join(2)
        section.close()


def test_read_invalid_frame_header_is_retried_not_hard_failed():
    # The frame header is invalid while the seqlock reads even (the writer was
    # caught mid-update): the reader must treat that as a torn window and
    # retry — not fail the transport on first sight. A "writer" completing
    # the header shortly after makes the read succeed.
    name = _unique_name()
    section = _open_writable(name)
    good = _gradient(4, 3)
    try:
        _publish(section, seq=2, width=4, height=3, pixels=good)
        struct.pack_into("<i", section, OFFSET_STRIDE, 999)  # torn header, seq still even
        fix = threading.Timer(
            0.05, lambda: _publish(section, seq=2, width=4, height=3, pixels=good)
        )
        fix.daemon = True

        reader = ShmReader(name)
        fix.start()
        frame = reader.read(2)

        fix.join(2)
        assert np.array_equal(frame, _as_frame(good, 4, 3))
    finally:
        section.close()


def test_read_invalid_frame_header_gives_up_at_deadline():
    # A header that never becomes valid must give up within the bounded retry
    # budget (100 tries AND the ~1s wall-clock deadline), raising
    # ShmUnavailable instead of spinning forever. For this tiny frame each
    # retry is one 1 ms poll, so the 100-count cap fires first (~0.1 s); the
    # deadline bound is what caps slow retries (e.g. torn 4K copies).
    name = _unique_name()
    section = _open_writable(name)
    try:
        _publish(section, seq=2, width=4, height=3, pixels=_gradient(4, 3))
        struct.pack_into("<i", section, OFFSET_STRIDE, 999)  # invalid forever
        reader = ShmReader(name)
        t0 = time.monotonic()

        with pytest.raises(ShmUnavailable):
            reader.read(2)

        # It really retried (100 polls ~ 0.1 s — the old code failed on first
        # sight in ~1 ms), but bounded.
        elapsed = time.monotonic() - t0
        assert elapsed >= 0.05
        assert elapsed < 5
    finally:
        section.close()
