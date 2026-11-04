"""Unit tests for the OCR model manager (list/download/delete/set + migration).

Everything here is offline: downloads run against an injected fake
downloader, the cache lives in ``tmp_path`` (bound via
``PADDLE_PDX_CACHE_HOME``), and the paddlex migration copies from a fake
``~/.paddlex`` layout under ``tmp_path``. No network, no real model loads.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import types
from pathlib import Path

import numpy as np
import pytest

from retran_core.jsonrpc import RpcInvalidParams, handle_line
from retran_core.ocr.catalog import CATALOG, recommended, spec
from retran_core.ocr.models import (
    ModelManager,
    make_model_handlers,
    migrate_from_paddlex,
    model_cache_dir,
)
from retran_core.ocr.paddle_engine import PaddleOcrEngine

# Default active pair (what __main__ builds from the catalog), plus a second
# valid pair used to exercise switching.
DET, REC = recommended("auto", "vi")
TINY_DET, TINY_REC = "PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec"

_SRC = Path(__file__).resolve().parents[1] / "src"


@pytest.fixture
def cache(tmp_path, monkeypatch):
    """Fresh model cache dir; PADDLE_PDX_CACHE_HOME points at it."""
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path))
    return tmp_path


def install(cache: Path, name: str) -> None:
    """Make a model look installed: non-empty dir, no .partial marker."""
    model_dir = cache / "official_models" / name
    model_dir.mkdir(parents=True, exist_ok=True)
    (model_dir / "inference.pdmodel").write_bytes(b"x")


def entry(manager: ModelManager, name: str) -> dict:
    """Return the list() catalog entry for one model."""
    return next(e for e in manager.list()["catalog"] if e["name"] == name)


def join_download(manager: ModelManager, name: str, timeout: float = 10.0) -> None:
    """Wait for the background download thread of `name` to finish.

    Uses the manager's private thread registry: the only synchronization the
    fake downloader offers is an event that is set while the worker is parked
    inside the downloader, so the thread object is guaranteed to still be
    registered when the test grabs it.
    """
    thread = manager._threads.get(name)
    assert thread is not None, "download thread should be registered"
    thread.join(timeout=timeout)
    assert not thread.is_alive()


class BlockingDownloader:
    """Fake downloader: creates the model files, then parks until released.

    Lets tests probe the mid-download state (files exist, ``installed`` still
    false because of the marker) and join deterministically.

    Attributes:
        cache: Cache dir whose ``official_models`` layout the fake fills.
        fail: When True, raise after release instead of succeeding.
        entered: Set once the model files exist on disk.
        release: The test sets this to let the download finish (or fail).
        names: Every name the downloader was called with.
    """

    def __init__(self, cache: Path, fail: bool = False) -> None:
        self.cache = cache
        self.fail = fail
        self.entered = threading.Event()
        self.release = threading.Event()
        self.names: list[str] = []

    def __call__(self, name: str) -> None:
        self.names.append(name)
        install(self.cache, name)
        self.entered.set()
        assert self.release.wait(timeout=10), "test never released the downloader"
        if self.fail:
            raise RuntimeError("download boom")


class FakeEngine:
    """Records set_models calls; mimics the pair an engine would hold."""

    def __init__(self, det: str, rec: str) -> None:
        self.det_model = det
        self.rec_model = rec
        self.calls: list[tuple[str, str]] = []

    def set_models(self, det: str, rec: str) -> None:
        self.calls.append((det, rec))
        self.det_model, self.rec_model = det, rec


class CountingPaddleOCR:
    """Stands in for paddleocr.PaddleOCR; records every construction."""

    instances: list["CountingPaddleOCR"] = []
    last_kwargs: dict = {}

    def __init__(self, **kwargs):
        CountingPaddleOCR.instances.append(self)
        CountingPaddleOCR.last_kwargs = kwargs

    def predict(self, image, **kwargs):
        return [
            {
                "dt_polys": np.array([[[0, 0], [1, 0], [1, 1], [0, 1]]]),
                "rec_texts": ["X"],
                "rec_scores": [0.5],
            }
        ]


def _fake_paddleocr(monkeypatch) -> None:
    fake = types.ModuleType("paddleocr")
    fake.PaddleOCR = CountingPaddleOCR
    monkeypatch.setitem(sys.modules, "paddleocr", fake)
    CountingPaddleOCR.instances = []
    CountingPaddleOCR.last_kwargs = {}


# ---------------------------------------------------------------------------
# model_cache_dir


def test_model_cache_dir_reads_env_at_call_time(tmp_path, monkeypatch):
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path / "a"))
    assert model_cache_dir() == tmp_path / "a"
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path / "b"))
    assert model_cache_dir() == tmp_path / "b"


@pytest.mark.skipif(sys.platform != "win32", reason="Windows-only default: %LOCALAPPDATA%\\ReTran\\models")
def test_model_cache_dir_windows_default_is_localappdata(monkeypatch):
    monkeypatch.delenv("PADDLE_PDX_CACHE_HOME", raising=False)
    assert model_cache_dir() == Path(os.environ["LOCALAPPDATA"]) / "ReTran" / "models"


def test_model_cache_dir_posix_default_is_home_paddlex(monkeypatch):
    monkeypatch.delenv("PADDLE_PDX_CACHE_HOME", raising=False)
    monkeypatch.setattr(sys, "platform", "linux")
    assert model_cache_dir() == Path.home() / ".paddlex"


# ---------------------------------------------------------------------------
# list / installed


def test_list_returns_catalog_with_states(cache):
    install(cache, DET)
    manager = ModelManager(cache)
    out = manager.list()
    assert [e["name"] for e in out["catalog"]] == [m.name for m in CATALOG]
    assert out["active"] == {"det": DET, "rec": REC}

    det_entry = entry(manager, DET)
    assert det_entry["kind"] == "det"
    assert det_entry["installed"] is True
    assert det_entry["inUse"] is True
    assert det_entry["sizeMb"] == spec(DET).size_mb
    assert "vi" in det_entry["langs"]

    tiny = entry(manager, TINY_DET)
    assert tiny["installed"] is False
    assert tiny["inUse"] is False


def test_installed_requires_nonempty_dir_and_no_marker(cache):
    manager = ModelManager(cache)
    official = cache / "official_models"
    assert not entry(manager, TINY_DET)["installed"]  # missing dir
    (official / TINY_DET).mkdir(parents=True)
    assert not entry(manager, TINY_DET)["installed"]  # empty dir
    install(cache, TINY_DET)
    assert entry(manager, TINY_DET)["installed"]  # non-empty, no marker
    (official / f"{TINY_DET}.partial").write_text("", encoding="utf-8")
    assert not entry(manager, TINY_DET)["installed"]  # marker hides partial files


# ---------------------------------------------------------------------------
# download


def test_download_success_marks_installed(cache):
    downloader = BlockingDownloader(cache)
    manager = ModelManager(cache, downloader=downloader)
    manager.download(REC)
    assert downloader.entered.wait(timeout=10)

    # Mid-download: files already exist but the .partial marker hides them.
    assert (cache / "official_models" / REC / "inference.pdmodel").exists()
    assert not entry(manager, REC)["installed"]

    downloader.release.set()
    join_download(manager, REC)
    assert entry(manager, REC)["installed"]
    assert not (cache / "official_models" / f"{REC}.partial").exists()
    assert downloader.names == [REC]


def test_download_failure_stays_not_installed(cache):
    downloader = BlockingDownloader(cache, fail=True)
    manager = ModelManager(cache, downloader=downloader)
    manager.download(REC)
    assert downloader.entered.wait(timeout=10)
    downloader.release.set()
    join_download(manager, REC)
    assert not entry(manager, REC)["installed"]
    # the marker survives the failure — partial files must not count
    assert (cache / "official_models" / f"{REC}.partial").exists()


def test_download_unknown_model_raises_invalid_params(cache):
    manager = ModelManager(cache, downloader=BlockingDownloader(cache))
    with pytest.raises(RpcInvalidParams):
        manager.download("no_such_model")
    assert not (cache / "official_models").exists()


def test_download_handler_returns_accepted_without_blocking(cache):
    downloader = BlockingDownloader(cache)
    manager = ModelManager(cache, downloader=downloader)
    handlers = make_model_handlers(manager)

    result = handlers["ocr.models.download"]({"name": REC})
    assert result == {"accepted": True}  # returns while the download parks
    assert downloader.entered.wait(timeout=10)
    assert not downloader.release.is_set()
    assert not entry(manager, REC)["installed"]  # mid-download via handler

    downloader.release.set()
    join_download(manager, REC)
    assert entry(manager, REC)["installed"]


# ---------------------------------------------------------------------------
# delete


def test_delete_active_model_raises_invalid_params(cache):
    manager = ModelManager(cache)
    with pytest.raises(RpcInvalidParams):
        manager.delete(DET)
    with pytest.raises(RpcInvalidParams):
        manager.delete(REC)


def test_delete_removes_model_dir_and_marker(cache):
    install(cache, TINY_DET)
    # a stale marker from an earlier failed attempt must be cleaned too
    (cache / "official_models" / f"{TINY_DET}.partial").write_text("", encoding="utf-8")
    manager = ModelManager(cache)
    assert not entry(manager, TINY_DET)["installed"]

    manager.delete(TINY_DET)
    assert not (cache / "official_models" / TINY_DET).exists()
    assert not (cache / "official_models" / f"{TINY_DET}.partial").exists()
    assert not entry(manager, TINY_DET)["installed"]


def test_delete_unknown_model_raises_invalid_params(cache):
    manager = ModelManager(cache)
    with pytest.raises(RpcInvalidParams):
        manager.delete("no_such_model")


def test_delete_while_downloading_raises(cache):
    downloader = BlockingDownloader(cache)
    manager = ModelManager(cache, downloader=downloader)
    manager.download(REC)
    assert downloader.entered.wait(timeout=10)
    with pytest.raises(RpcInvalidParams):
        manager.delete(REC)
    downloader.release.set()
    join_download(manager, REC)
    assert entry(manager, REC)["installed"]  # rejected delete broke nothing


# ---------------------------------------------------------------------------
# set_pair (+ engine switching)


def test_set_pair_updates_active_and_engine(cache):
    engine = FakeEngine(DET, REC)
    manager = ModelManager(cache, engine=engine)
    assert manager.list()["active"] == {"det": DET, "rec": REC}

    manager.set_pair(TINY_DET, TINY_REC)
    assert engine.calls == [(TINY_DET, TINY_REC)]
    assert manager.list()["active"] == {"det": TINY_DET, "rec": TINY_REC}
    assert entry(manager, TINY_DET)["inUse"] is True
    assert entry(manager, DET)["inUse"] is False

    # the old det is no longer in the pair → deletable
    install(cache, DET)
    manager.delete(DET)

    # active pair is initialized from the engine the manager was given
    manager2 = ModelManager(cache, engine=FakeEngine(TINY_DET, TINY_REC))
    assert manager2.list()["active"] == {"det": TINY_DET, "rec": TINY_REC}


def test_set_pair_rejects_unknown_and_kind_mismatch(cache):
    manager = ModelManager(cache)
    with pytest.raises(RpcInvalidParams):
        manager.set_pair("no_such_model", REC)
    with pytest.raises(RpcInvalidParams):
        manager.set_pair(DET, "no_such_model")
    with pytest.raises(RpcInvalidParams):
        manager.set_pair(REC, TINY_REC)  # rec model in the det slot
    with pytest.raises(RpcInvalidParams):
        manager.set_pair(TINY_DET, DET)  # det model in the rec slot
    assert manager.list()["active"] == {"det": DET, "rec": REC}


def test_engine_set_models_switches_pair_lazily(monkeypatch):
    _fake_paddleocr(monkeypatch)
    engine = PaddleOcrEngine(DET, REC)
    image = np.zeros((12, 24, 3), dtype=np.uint8)

    engine.spot(image)
    assert CountingPaddleOCR.last_kwargs["text_detection_model_name"] == DET
    assert len(CountingPaddleOCR.instances) == 1

    engine.set_models(TINY_DET, TINY_REC)
    assert engine.det_model == TINY_DET
    assert engine.rec_model == TINY_REC

    engine.spot(image)  # rebuilds the predictor with the new pair
    kwargs = CountingPaddleOCR.last_kwargs
    assert kwargs["text_detection_model_name"] == TINY_DET
    assert kwargs["text_recognition_model_name"] == TINY_REC
    assert len(CountingPaddleOCR.instances) == 2


def test_engine_model_names_are_read_only():
    engine = PaddleOcrEngine(DET, REC)
    with pytest.raises(AttributeError):
        engine.det_model = "x"
    with pytest.raises(AttributeError):
        engine.rec_model = "x"


# ---------------------------------------------------------------------------
# migrate_from_paddlex


def make_source(root: Path) -> Path:
    """Fake ~/.paddlex: two catalog models, one unknown model, one stray file."""
    official = root / ".paddlex" / "official_models"
    for name in (DET, "korean_PP-OCRv5_mobile_rec", "SomeUnrelatedModel"):
        model_dir = official / name
        model_dir.mkdir(parents=True, exist_ok=True)
        (model_dir / "inference.pdmodel").write_bytes(b"src")
    (official / "stray.txt").write_text("not a model", encoding="utf-8")
    return root / ".paddlex"


def test_migrate_copies_only_catalog_models_and_never_touches_source(tmp_path):
    source = make_source(tmp_path / "old")
    cache = tmp_path / "cache"

    copied = migrate_from_paddlex(cache, source=source)

    assert copied == [DET, "korean_PP-OCRv5_mobile_rec"]  # catalog order
    for name in (DET, "korean_PP-OCRv5_mobile_rec"):
        assert (cache / "official_models" / name / "inference.pdmodel").read_bytes() == b"src"
    assert not (cache / "official_models" / "SomeUnrelatedModel").exists()
    assert not (cache / "official_models" / "stray.txt").exists()
    # copy-only: the source keeps everything
    src_det = source / "official_models" / DET
    assert src_det.is_dir()
    assert (src_det / "inference.pdmodel").read_bytes() == b"src"
    assert (source / "official_models" / "SomeUnrelatedModel").is_dir()


def test_migrate_is_idempotent_via_marker(tmp_path):
    source = make_source(tmp_path / "old")
    cache = tmp_path / "cache"
    assert migrate_from_paddlex(cache, source=source) == [DET, "korean_PP-OCRv5_mobile_rec"]
    assert (cache / ".migrated").exists()

    # mutate the source: a second call must not copy again
    (source / "official_models" / DET / "inference.pdmodel").write_bytes(b"changed")
    assert migrate_from_paddlex(cache, source=source) == []
    assert (cache / "official_models" / DET / "inference.pdmodel").read_bytes() == b"src"


def test_migrate_without_source_writes_marker(tmp_path):
    cache = tmp_path / "cache"
    assert migrate_from_paddlex(cache, source=tmp_path / "missing") == []
    assert (cache / ".migrated").exists()
    assert migrate_from_paddlex(cache, source=tmp_path / "missing") == []


def test_migrate_skips_models_already_in_cache(tmp_path):
    source = make_source(tmp_path / "old")
    cache = tmp_path / "cache"
    cached_det = cache / "official_models" / DET
    cached_det.mkdir(parents=True)
    (cached_det / "inference.pdmodel").write_bytes(b"cache-version")

    copied = migrate_from_paddlex(cache, source=source)

    assert copied == ["korean_PP-OCRv5_mobile_rec"]
    assert (cached_det / "inference.pdmodel").read_bytes() == b"cache-version"


def test_migrate_default_source_is_dot_paddlex_home(tmp_path, monkeypatch):
    monkeypatch.setenv("USERPROFILE", str(tmp_path))  # Path.home() on Windows
    official = tmp_path / ".paddlex" / "official_models" / DET
    official.mkdir(parents=True)
    (official / "inference.pdmodel").write_bytes(b"src")
    cache = tmp_path / "cache"

    assert migrate_from_paddlex(cache) == [DET]


# ---------------------------------------------------------------------------
# migrate_from_retran_legacy (%LOCALAPPDATA%\ReTran\models -> app-local cache)


def test_migrate_from_retran_legacy_copies_models_and_writes_marker(tmp_path):
    from retran_core.ocr.models import migrate_from_retran_legacy
    old = tmp_path / "old"
    model_name = CATALOG[1].name  # PP-OCRv6_small_det
    (old / "official_models" / model_name).mkdir(parents=True)
    (old / "official_models" / model_name / "weights.inference.json").write_text("{}")
    cache = tmp_path / "cache"
    copied = migrate_from_retran_legacy(cache, source=old)
    assert copied == [model_name]
    assert (cache / "official_models" / model_name / "weights.inference.json").exists()
    assert (cache / ".migrated-retran").exists()


def test_migrate_from_retran_legacy_is_idempotent(tmp_path):
    from retran_core.ocr.models import migrate_from_retran_legacy
    old = tmp_path / "old"
    model_name = CATALOG[1].name
    (old / "official_models" / model_name).mkdir(parents=True)
    (old / "official_models" / model_name / "f").write_text("x")
    cache = tmp_path / "cache"
    migrate_from_retran_legacy(cache, source=old)
    assert migrate_from_retran_legacy(cache, source=old) == []


def test_migrate_from_retran_legacy_default_source_noop_off_windows(tmp_path, monkeypatch):
    from retran_core.ocr import models as m
    monkeypatch.setattr(m.sys, "platform", "linux")
    assert m.migrate_from_retran_legacy(tmp_path / "cache") == []


# ---------------------------------------------------------------------------
# handlers + __main__ wiring


def test_model_handlers_via_handle_line(cache):
    manager = ModelManager(cache)
    handlers = make_model_handlers(manager)

    def call(method, params):
        request = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params})
        return json.loads(handle_line(request, handlers))

    out = call("ocr.models.list", None)
    assert out["result"]["active"] == {"det": DET, "rec": REC}
    assert len(out["result"]["catalog"]) == len(CATALOG)

    assert call("ocr.models.delete", {"name": DET})["error"]["code"] == -32602
    assert call("ocr.models.download", {})["error"]["code"] == -32602  # missing name
    assert call("ocr.models.set", {"det": DET, "rec": "nope"})["error"]["code"] == -32602

    ok = call("ocr.models.set", {"det": TINY_DET, "rec": TINY_REC})
    assert ok["result"]["active"] == {"det": TINY_DET, "rec": TINY_REC}
    assert call("ocr.models.list", None)["result"]["active"] == {"det": TINY_DET, "rec": TINY_REC}

    install(cache, DET)  # no longer part of the pair → deletable via RPC
    assert call("ocr.models.delete", {"name": DET})["result"]["deleted"] is True


# ---------------------------------------------------------------------------
# ocr.models.recommend


def test_recommend_v6_covered_language_returns_default_small_pair(cache):
    handler = make_model_handlers(ModelManager(cache))["ocr.models.recommend"]
    assert handler({"src": "vi"}) == {"det": "PP-OCRv6_small_det", "rec": "PP-OCRv6_small_rec"}
    assert handler({"src": "ja"}) == {"det": "PP-OCRv6_small_det", "rec": "PP-OCRv6_small_rec"}


def test_recommend_legacy_language_routes_to_script_rec_model(cache):
    handler = make_model_handlers(ModelManager(cache))["ocr.models.recommend"]
    assert handler({"src": "ko"}) == {"det": "PP-OCRv6_small_det", "rec": "korean_PP-OCRv5_mobile_rec"}
    assert handler({"src": "ru"}) == {"det": "PP-OCRv6_small_det", "rec": "cyrillic_PP-OCRv5_mobile_rec"}
    assert handler({"src": "th"}) == {"det": "PP-OCRv6_small_det", "rec": "th_PP-OCRv5_mobile_rec"}


def test_recommend_invalid_src_raises_rpc_invalid_params(cache):
    handler = make_model_handlers(ModelManager(cache))["ocr.models.recommend"]
    for bad in (None, {}, {"src": ""}, {"src": 5}):
        with pytest.raises(RpcInvalidParams):
            handler(bad)


def test_main_handlers_register_model_rpc(tmp_path, monkeypatch):
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path))
    from retran_core import __main__ as main_module

    handlers = main_module._handlers()
    assert set(handlers) == {
        "sidecar.ping",
        "sidecar.version",
        "ocr.spot",
        "ocr.models.list",
        "ocr.models.download",
        "ocr.models.delete",
        "ocr.models.set",
        "ocr.models.recommend",
        "ocr.config.schema",
        "ocr.config.set",
    }

    # One engine shared: ocr.models.set must change the active pair that
    # ocr.models.list reports (no paddle import happens — engine is lazy).
    handlers["ocr.models.set"]({"det": TINY_DET, "rec": TINY_REC})
    assert handlers["ocr.models.list"](None)["active"] == {"det": TINY_DET, "rec": TINY_REC}


def test_serve_startup_runs_migration_and_serves_model_list(tmp_path):
    home = tmp_path / "home"
    src_model = home / ".paddlex" / "official_models" / DET
    src_model.mkdir(parents=True)
    (src_model / "inference.pdmodel").write_bytes(b"src")
    cache = tmp_path / "cache"
    env = {
        **os.environ,
        "PYTHONPATH": str(_SRC),
        "PADDLE_PDX_CACHE_HOME": str(cache),
        "USERPROFILE": str(home),
    }
    snippet = "from retran_core.__main__ import main\nraise SystemExit(main(['serve']))\n"
    request = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ocr.models.list"})

    proc = subprocess.run(
        [sys.executable, "-c", snippet],
        input=(request + "\n").encode("utf-8"),
        capture_output=True,
        env=env,
        timeout=60,
    )

    assert proc.returncode == 0, proc.stderr.decode("utf-8", errors="replace")
    resp = json.loads(proc.stdout.decode("utf-8").splitlines()[0])
    assert resp["result"]["active"] == {"det": DET, "rec": REC}
    det_entry = next(e for e in resp["result"]["catalog"] if e["name"] == DET)
    assert det_entry["installed"] is True  # migrated before serving
    assert (cache / "official_models" / DET / "inference.pdmodel").read_bytes() == b"src"
    assert (cache / ".migrated").exists()
