"""OCR A/B benchmark: one det/rec pair over a folder of real PNG frames.

Runs the production spot handler (`make_spot_handler(PaddleOcrEngine(det, rec))`)
on every ``*.png`` in ``--data`` via the base64 ``{image}`` params (decode +
downscale to 1280 + engine inference are all inside the timed `elapsed_ms`,
exactly what the sidecar reports per cycle). The shared-memory transport is
the production path, but it needs the C# writer on the other end; the base64
path exercises the same engine and is the simplest realistic bench.

One throwaway warmup pass (model download/load + CUDA init) runs before the
timed loop so the reported numbers are steady-state.

Usage (retran_core + paddle come from the ReTran.OCR uv environment):

    # from the repo root
    uv run --project ReTran.OCR python Tools/Perf/ocrbench/bench.py ^
        --data Tools/Perf/ocrbench/data ^
        --det PP-OCRv6_small_det --rec PP-OCRv6_small_rec ^
        --label v6_small --csv Tools/Perf/ocrbench/bench_v6_small.csv
"""
from __future__ import annotations

import argparse
import base64
import csv
import json
import statistics
import subprocess
import sys
import time
from pathlib import Path

# Allow running straight from the repo without a prior `uv sync` install:
# prefer the installed package, fall back to the source tree beside the repo.
_REPO_ROOT = Path(__file__).resolve().parents[2]
_SRC = _REPO_ROOT / "ReTran.OCR" / "src"
if _SRC.is_dir() and str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

from retran_core.ocr.handlers import make_spot_handler  # noqa: E402
from retran_core.ocr.paddle_engine import PaddleOcrEngine  # noqa: E402


def _load_frames(data_dir: Path) -> list[tuple[str, str, tuple[int, int]]]:
    """Return ``(name, image_b64, (width, height))`` per PNG, sorted by name."""
    from PIL import Image

    frames: list[tuple[str, str, tuple[int, int]]] = []
    for path in sorted(data_dir.glob("*.png")):
        with Image.open(path) as img:
            size = img.size
        frames.append((path.name, base64.b64encode(path.read_bytes()).decode("ascii"), size))
    return frames


def _gpu_memory_used_mib() -> int | None:
    """Return the GPU's total used memory in MiB per nvidia-smi, or None.

    Per-process attribution is unavailable on this stack (the driver's
    nvidia-smi rejects ``--query-computed-apps``), so the benchmark reports
    the global-usage delta around its own model load instead.
    """
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            timeout=10,
        ).stdout.strip()
        return int(out.splitlines()[0])
    except (OSError, subprocess.SubprocessError, ValueError, IndexError):
        return None


def main() -> int:
    # OCR text is arbitrary Unicode (e.g. '□') and the Windows console's
    # cp1252 default crashes mid-bench with UnicodeEncodeError; force UTF-8.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", required=True, help="folder containing the PNG frames")
    parser.add_argument("--det", required=True, help="detection model name, e.g. PP-OCRv6_small_det")
    parser.add_argument("--rec", required=True, help="recognition model name, e.g. PP-OCRv6_small_rec")
    parser.add_argument("--label", default="", help="pair label used in the summary line")
    parser.add_argument("--csv", default="", help="optional per-frame CSV output path")
    parser.add_argument("--warmup", type=int, default=1, help="untimed warmup passes (default 1)")
    args = parser.parse_args()

    data_dir = Path(args.data)
    frames = _load_frames(data_dir)
    if not frames:
        print(f"no *.png frames in {data_dir}", file=sys.stderr)
        return 2

    import paddle
    import paddleocr

    print(f"label: {args.label or f'{args.det}+{args.rec}'}")
    print(f"det: {args.det}  rec: {args.rec}")
    print(f"frames: {len(frames)} from {data_dir}")
    print(f"stack: paddle {paddle.__version__} (cuda {paddle.is_compiled_with_cuda()}), paddleocr {paddleocr.__version__}")

    handler = make_spot_handler(PaddleOcrEngine(det_model=args.det, rec_model=args.rec))
    # Sampled before the engine loads any model (lazy init on first spot).
    gpu_baseline = _gpu_memory_used_mib()

    for i in range(args.warmup):
        t0 = time.perf_counter()
        handler({"image": frames[0][1]})
        print(f"warmup {i + 1}/{args.warmup}: {int((time.perf_counter() - t0) * 1000)} ms wall (model load + inference, untimed)")
    gpu_after_warmup = _gpu_memory_used_mib()

    rows: list[dict[str, object]] = []
    b64_request_bytes: list[int] = []
    for name, image_b64, size in frames:
        result = handler({"image": image_b64})
        spots = result["spots"]
        texts = [s["text"] for s in spots]
        elapsed_ms = result["elapsed_ms"]
        rows.append({"image": name, "elapsed_ms": elapsed_ms, "spot_count": len(spots), "texts": texts})
        b64_request_bytes.append(len(json.dumps({"image": image_b64}, separators=(",", ":"))))
        print(f"{name}: elapsed_ms={elapsed_ms} spots={len(spots)}")
        print(f"  texts: {' | '.join(texts)}")

    elapsed = [int(r["elapsed_ms"]) for r in rows]
    total_spots = sum(int(r["spot_count"]) for r in rows)
    w, h = frames[0][2]
    shm_request_b = len(json.dumps({"seq": 2, "width": w, "height": h}, separators=(",", ":")))

    print("summary:")
    print(f"  frames={len(rows)} elapsed_ms min={min(elapsed)} median={statistics.median(elapsed)} "
          f"mean={round(statistics.mean(elapsed), 1)} max={max(elapsed)}")
    print(f"  total_spots={total_spots} mean_spots_per_frame={round(total_spots / len(rows), 1)}")
    print(f"  request_bytes_per_frame: base64_png_mean={round(statistics.mean(b64_request_bytes))} "
          f"shm={shm_request_b} ({{seq,width,height}} for {w}x{h})")
    gpu_after = _gpu_memory_used_mib()
    print(f"  gpu_memory_used_mib: baseline={gpu_baseline} after_warmup={gpu_after_warmup} after_loop={gpu_after} "
          "(global nvidia-smi; per-process VRAM unsupported by this driver)")

    if args.csv:
        csv_path = Path(args.csv)
        csv_path.parent.mkdir(parents=True, exist_ok=True)
        with csv_path.open("w", newline="", encoding="utf-8") as f:
            writer = csv.writer(f)
            writer.writerow(["image", "elapsed_ms", "spot_count", "texts"])
            for r in rows:
                writer.writerow([r["image"], r["elapsed_ms"], r["spot_count"], " | ".join(r["texts"])])
        print(f"  csv: {csv_path}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
