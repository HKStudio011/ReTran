"""OCR model manager: list/download/delete/set plus one-shot paddlex migration.

The sidecar owns the model cache paddlex downloads into
(``<PADDLE_PDX_CACHE_HOME>/official_models/<name>``). This module derives
``installed`` purely from the filesystem, downloads on a background thread so
``ocr.spot`` is never blocked, refuses to delete the active pair, forwards
pair switches to the engine, and migrates existing models from the legacy
``~/.paddlex`` cache on first run.

Offline by design: the download backend is injectable, so tests never touch
paddlex or the network.
"""
from __future__ import annotations

import os
import shutil
import sys
import threading
from pathlib import Path
from typing import Any, Callable, Optional

from ..jsonrpc import Handler, RpcInvalidParams
from .catalog import CATALOG, ModelSpec, recommended

# paddlex's fixed layout: models live in <cache>/official_models/<name>
# (paddlex's own _ModelManager saves there; we must match it both for
# `installed` detection and for paddlex to find migrated files).
_OFFICIAL_DIR = "official_models"

# Marker file `official_models/<name>.partial`: present while a download is
# running and after it failed; hides a non-empty (partial) model dir from
# `installed`.
_PARTIAL_SUFFIX = ".partial"

# One-shot migration marker in the cache root; its presence means the copy
# from ~/.paddlex already ran and must not run again.
_MIGRATED_MARKER = ".migrated"

Downloader = Callable[[str], Any]


def model_cache_dir() -> Path:
    """Return the model cache directory, reading the env at call time.

    Honors ``PADDLE_PDX_CACHE_HOME`` — the same env var paddlex itself reads
    at import time (``paddlex/cache.py``); the C# core sets it when spawning
    the sidecar. An empty value is treated as unset. Falls back to
    ``%LOCALAPPDATA%\\ReTran\\models`` on Windows and to ``~/.paddlex``
    elsewhere.

    Returns:
        The directory paddlex should treat as its cache home.
    """
    env = os.environ.get("PADDLE_PDX_CACHE_HOME")
    if env:
        return Path(env)
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
        return Path(base) / "ReTran" / "models"
    return Path.home() / ".paddlex"


def _paddlex_downloader(name: str) -> Path:
    """Download one model with paddlex's own official-models machinery.

    Uses the ``official_models`` singleton — the same entry point
    ``paddlex.inference.models`` uses to resolve a model by name:
    ``official_models[name]`` downloads into
    ``<PADDLE_PDX_CACHE_HOME>/official_models/<name>`` when missing and
    returns the local path. paddlex 3.7.x has no cleaner public per-model
    download function.

    Limitation: paddlex reads ``PADDLE_PDX_CACHE_HOME`` at import time and the
    singleton binds its save directory once, so the env must already point at
    the manager's cache dir before the first paddlex import. The sidecar entry
    point keeps them in sync (``__main__`` sets the env at startup when
    missing; the C# core sets it when spawning the sidecar).

    Args:
        name: Catalog model name, e.g. ``"PP-OCRv6_small_rec"``.

    Returns:
        The local model directory paddlex downloaded into.

    Raises:
        Exception: Whatever the paddlex download raises (network failure,
            model unknown to the hosters). The caller keeps the
            ``.partial`` marker so the model does not count as installed.
    """
    from paddlex.inference.utils.official_models import official_models

    return Path(official_models[name])


class ModelManager:
    """Tracks installed OCR models and the active det/rec pair.

    The manager is the sidecar-side source of truth behind the
    ``ocr.models.*`` JSON-RPC handlers. ``installed`` is derived from the
    filesystem: the model dir exists, is non-empty, and no ``.partial`` marker
    sits next to it. Downloads run on background threads (a marker makes the
    in-progress state visible to ``list()``), the active pair can never be
    deleted, and pair switches are forwarded to the engine so the next
    ``spot()`` rebuilds with the new models.

    Attributes:
        cache_dir: The paddlex cache home this manager manages.
    """

    def __init__(
        self,
        cache_dir: Path,
        engine: Any = None,
        catalog: tuple[ModelSpec, ...] = CATALOG,
        downloader: Optional[Downloader] = None,
    ) -> None:
        """Create a manager over one cache directory.

        Args:
            cache_dir: Paddlex cache home (``PADDLE_PDX_CACHE_HOME``);
                models live in ``cache_dir/official_models/<name>``.
            engine: Optional engine holding the active pair. When given, the
                manager initializes ``active`` from the engine's current
                ``det_model``/``rec_model`` and ``set_pair`` also calls
                ``engine.set_models(det, rec)`` so the next spot call reloads
                with the new pair.
            catalog: Models selectable through this manager.
            downloader: Callable that makes one model present under
                ``official_models/`` (default: paddlex's own downloader).
                Injected so tests never touch the network.
        """
        self.cache_dir = Path(cache_dir)
        self._engine = engine
        self._catalog = tuple(catalog)
        self._downloader: Downloader = downloader or _paddlex_downloader
        self._lock = threading.Lock()
        self._threads: dict[str, threading.Thread] = {}
        if engine is not None:
            self._det: str = engine.det_model
            self._rec: str = engine.rec_model
        else:
            self._det, self._rec = recommended("auto", "vi")

    # -- filesystem layout --------------------------------------------------

    def _official_dir(self) -> Path:
        return self.cache_dir / _OFFICIAL_DIR

    def _model_dir(self, name: str) -> Path:
        return self._official_dir() / name

    def _marker(self, name: str) -> Path:
        return self._official_dir() / f"{name}{_PARTIAL_SUFFIX}"

    def _installed(self, name: str) -> bool:
        model_dir = self._model_dir(name)
        return not self._marker(name).exists() and model_dir.is_dir() and any(model_dir.iterdir())

    def _find(self, name: str, kind: Optional[str] = None) -> ModelSpec:
        """Look up a catalog entry, optionally pinning its kind.

        Args:
            name: Model name to look up.
            kind: When given, the entry's kind must match (det/rec slot).

        Returns:
            The matching spec.

        Raises:
            RpcInvalidParams: The name is not in the catalog, or its kind
                does not match the requested slot.
        """
        for m in self._catalog:
            if m.name == name:
                if kind is not None and m.kind != kind:
                    raise RpcInvalidParams(
                        f"model '{name}' is a {m.kind} model, expected a {kind} model"
                    )
                return m
        raise RpcInvalidParams(f"unknown model: {name}")

    # -- public API ---------------------------------------------------------

    def list(self) -> dict:
        """Return catalog states and the active pair for the UI.

        Returns:
            ``{"catalog": [{name, kind, langs, sizeMb, installed, inUse}],
            "active": {"det": ..., "rec": ...}}`` — one entry per catalog
            model in catalog order. ``inUse`` is true only for the two
            models of the active pair; ``installed`` is derived from the
            filesystem.
        """
        entries = []
        for m in self._catalog:
            entries.append(
                {
                    "name": m.name,
                    "kind": m.kind,
                    "langs": list(m.langs),
                    "sizeMb": m.size_mb,
                    "installed": self._installed(m.name),
                    "inUse": m.name == self._det or m.name == self._rec,
                }
            )
        return {"catalog": entries, "active": {"det": self._det, "rec": self._rec}}

    def download(self, name: str) -> None:
        """Start downloading one model on a background thread; never blocks.

        Writes the ``<name>.partial`` marker synchronously (so ``list()``
        immediately reports the model as not installed while the download
        runs), then downloads on a daemon thread. The marker is removed only
        on success; a failed download keeps it so partial files never count
        as installed, and a retry clears the leftovers first.

        Args:
            name: Catalog model name.

        Raises:
            RpcInvalidParams: The model is not in the catalog.
        """
        self._find(name)
        with self._lock:
            if name in self._threads or self._installed(name):
                return  # already downloading, or nothing to do
            self._official_dir().mkdir(parents=True, exist_ok=True)
            self._marker(name).write_text("", encoding="utf-8")
            thread = threading.Thread(
                target=self._download_async,
                args=(name,),
                daemon=True,
                name=f"model-download-{name}",
            )
            self._threads[name] = thread
        thread.start()

    def _download_async(self, name: str) -> None:
        """Run one download to completion; keep the marker on failure."""
        model_dir = self._model_dir(name)
        try:
            if model_dir.exists():
                # Leftovers of a failed attempt would make paddlex treat the
                # model as cached; always start from a clean directory.
                shutil.rmtree(model_dir, ignore_errors=True)
            self._downloader(name)
        except Exception as exc:  # noqa: BLE001 - report, keep marker, stay alive
            print(
                f"ocr.models.download failed for '{name}': {exc}",
                file=sys.stderr,
                flush=True,
            )
        else:
            self._marker(name).unlink(missing_ok=True)
        finally:
            with self._lock:
                self._threads.pop(name, None)

    def delete(self, name: str) -> None:
        """Delete one model's files from the cache.

        Idempotent for models that are not installed, but refuses to delete
        a model that is part of the active pair or is currently downloading.

        Args:
            name: Catalog model name.

        Raises:
            RpcInvalidParams: The model is unknown, is part of the active
                pair, or a download of it is still running.
        """
        self._find(name)
        if name == self._det or name == self._rec:
            raise RpcInvalidParams(f"model in use: '{name}' is part of the active pair")
        with self._lock:
            if name in self._threads:
                raise RpcInvalidParams(f"model '{name}' is being downloaded")
        model_dir = self._model_dir(name)
        if model_dir.is_dir():
            shutil.rmtree(model_dir)
        self._marker(name).unlink(missing_ok=True)

    def set_pair(self, det: str, rec: str) -> None:
        """Switch the active det/rec pair.

        Updates the pair this manager reports and (when constructed with an
        engine) forwards the switch via ``engine.set_models`` so the next
        ``spot()`` rebuilds the predictor lazily with the new models. The
        models do not need to be installed yet — a missing model surfaces as
        a load error on the next ``spot()`` call instead (the core's
        ``core.start`` performs the explicit installed check).

        Args:
            det: Catalog det model name.
            rec: Catalog rec model name.

        Raises:
            RpcInvalidParams: A name is not in the catalog or its kind does
                not match the slot.
        """
        self._find(det, kind="det")
        self._find(rec, kind="rec")
        self._det, self._rec = det, rec
        if self._engine is not None:
            self._engine.set_models(det, rec)


def migrate_from_paddlex(cache_dir: Path, source: Path | None = None) -> list[str]:
    """One-shot copy of catalog models from the legacy paddlex cache.

    Copies each catalog model found in ``source/official_models/<name>`` into
    ``cache_dir/official_models/<name>`` (skipping models already present in
    the cache) and writes a ``.migrated`` marker in the cache root so the
    copy never runs again. The source is only ever read — never moved or
    deleted. Each model is best-effort: a failing copy is logged and the
    model simply stays "not installed" (it can be downloaded again).

    Args:
        cache_dir: The sidecar's model cache (``PADDLE_PDX_CACHE_HOME``).
        source: Legacy paddlex cache home; defaults to ``~/.paddlex``.

    Returns:
        Names of the models actually copied, in catalog order. Empty when
        the marker already exists (or nothing was found to copy).
    """
    cache = Path(cache_dir)
    marker = cache / _MIGRATED_MARKER
    if marker.exists():
        return []
    src_official = (Path(source) if source is not None else Path.home() / ".paddlex") / _OFFICIAL_DIR
    copied: list[str] = []
    if src_official.is_dir():
        dst_official = cache / _OFFICIAL_DIR
        dst_official.mkdir(parents=True, exist_ok=True)
        for m in CATALOG:
            src_model = src_official / m.name
            dst_model = dst_official / m.name
            if not src_model.is_dir() or dst_model.exists():
                continue
            try:
                shutil.copytree(src_model, dst_model)
            except OSError as exc:
                print(f"model migration: skipping '{m.name}': {exc}", file=sys.stderr, flush=True)
                continue
            copied.append(m.name)
    cache.mkdir(parents=True, exist_ok=True)
    marker.touch()
    return copied


def _require_name(params: Any) -> str:
    """Extract and validate the 'name' param of a model RPC.

    Args:
        params: Raw JSON-RPC params.

    Returns:
        The model name.

    Raises:
        RpcInvalidParams: The params are not an object with a non-empty
            string 'name'.
    """
    if not isinstance(params, dict):
        raise RpcInvalidParams("params must be an object with 'name'")
    name = params.get("name")
    if not isinstance(name, str) or not name.strip():
        raise RpcInvalidParams("missing or invalid 'name' (non-empty model name string)")
    return name


def _require_pair(params: Any) -> tuple[str, str]:
    """Extract and validate the 'det'/'rec' params of ocr.models.set.

    Args:
        params: Raw JSON-RPC params.

    Returns:
        The (det, rec) model names.

    Raises:
        RpcInvalidParams: The params are not an object with non-empty string
            'det' and 'rec' entries.
    """
    if not isinstance(params, dict):
        raise RpcInvalidParams("params must be an object with 'det' and 'rec'")
    det, rec = params.get("det"), params.get("rec")
    if not isinstance(det, str) or not det.strip() or not isinstance(rec, str) or not rec.strip():
        raise RpcInvalidParams("missing or invalid 'det'/'rec' (non-empty model name strings)")
    return det, rec


def make_model_handlers(manager: ModelManager) -> dict[str, Handler]:
    """Build the ocr.models.* JSON-RPC handlers bound to a manager.

    Args:
        manager: Manager serving the requests; not owned by the handlers.

    Returns:
        Handlers for ``ocr.models.list`` (params ignored), ``ocr.models.download``
        (returns ``{"accepted": true}`` immediately — the download continues
        on a background thread), ``ocr.models.delete`` and ``ocr.models.set``
        (returns the new active pair). Invalid params and unknown/in-use
        models raise ``RpcInvalidParams`` (-32602).
    """

    def download(params: Optional[dict]) -> dict:
        manager.download(_require_name(params))
        return {"accepted": True}

    def delete(params: Optional[dict]) -> dict:
        manager.delete(_require_name(params))
        return {"deleted": True}

    def set_pair(params: Optional[dict]) -> dict:
        det, rec = _require_pair(params)
        manager.set_pair(det, rec)
        return {"active": {"det": det, "rec": rec}}

    return {
        "ocr.models.list": lambda params: manager.list(),
        "ocr.models.download": download,
        "ocr.models.delete": delete,
        "ocr.models.set": set_pair,
    }
