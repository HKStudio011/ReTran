"""OCR engine parameter surface: schema, replace semantics, deferred reload.

The sidecar is the single source of truth for the PaddleOCR engine parameters
ReTran exposes (design 2026-10-26, §6): :data:`PARAMS` holds every exposed
parameter with its UI group, JSON type, ReTran default and reload behaviour;
:data:`HIDDEN_KEYS` lists the parameters deliberately kept off the wire; and
:class:`OcrConfig` holds the user's override map with *replace* semantics —
:meth:`OcrConfig.set` validates the whole payload before touching any state
and reports which keys took effect immediately (``live``) and which need a
model reload (``deferred`` until the pipeline stops).

Offline by design: no paddle import, so the schema and its semantics are
testable without the optional OCR stack.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any, Callable, Optional

from ..jsonrpc import RpcInvalidParams


@dataclass(frozen=True)
class ParamSpec:
    """One exposed OCR engine parameter.

    Attributes:
        key: PaddleOCR keyword name, also the ``ocr.config.set`` key.
        group: UI group — ``device``, ``models``, ``preprocess``, ``det`` or
            ``rec`` (design §6.1–6.5).
        type: Validation/rendering type — ``bool``, ``int``, ``float``,
            ``string``, ``enum``, ``json`` or ``shape``.
        default: ReTran's default value, used when the key is absent or
            ``null``; ``None`` means "let PaddleOCR/PaddleX decide".
        reload_required: ``True`` for constructor options (changing them
            reloads the model), ``False`` for live ``predict()`` options.
        values: Allowed strings for ``enum`` params, ``None`` otherwise.
        min: Inclusive lower bound for ``int``/``float`` params.
        max: Inclusive upper bound for ``int``/``float`` params.
        step: UI input step for numeric params.
        placeholder: UI hint shown while the field is empty.
    """

    key: str
    group: str
    type: str
    default: Any
    reload_required: bool
    values: Optional[tuple[str, ...]] = None
    min: Optional[float] = None
    max: Optional[float] = None
    step: Optional[float] = None
    placeholder: Optional[str] = None

    def to_dict(self) -> dict:
        """Return this param's camelCase wire form (spec §7.1)."""
        return {
            "key": self.key,
            "group": self.group,
            "type": self.type,
            "default": self.default,
            "reloadRequired": self.reload_required,
            "values": list(self.values) if self.values is not None else None,
            "min": self.min,
            "max": self.max,
            "step": self.step,
            "placeholder": self.placeholder,
        }


# The 27 exposed params, in schema/UI order (design §6.1–6.5). Defaults are
# ReTran's pinned values and must match what the pinned stack ran with before
# this surface existed (§10: no behaviour change for an untouched config).
PARAMS: tuple[ParamSpec, ...] = (
    # §6.1 Device & engine (constructor: model reload).
    ParamSpec("device", "device", "string", None, True, placeholder="auto | cpu | gpu:0"),
    ParamSpec(
        "engine", "device", "enum", None, True,
        values=("paddle", "paddle_static", "paddle_dynamic", "transformers", "onnxruntime"),
    ),
    ParamSpec("precision", "device", "enum", "fp32", True, values=("fp32", "fp16")),
    ParamSpec("use_tensorrt", "device", "bool", False, True),
    ParamSpec("enable_hpi", "device", "bool", None, True),
    ParamSpec("enable_cinn", "device", "bool", False, True),
    ParamSpec("enable_mkldnn", "device", "bool", True, True),
    ParamSpec("mkldnn_cache_capacity", "device", "int", 10, True, min=0, max=512),
    ParamSpec("cpu_threads", "device", "int", 10, True, min=1, max=64),
    ParamSpec("engine_config", "device", "json", None, True),
    # §6.2 Model load (constructor: model reload).
    ParamSpec("text_recognition_batch_size", "models", "int", 6, True, min=1, max=64),
    ParamSpec("textline_orientation_batch_size", "models", "int", 6, True, min=1, max=64),
    ParamSpec("text_det_input_shape", "models", "shape", None, True, placeholder="N,C,H,W"),
    ParamSpec("text_rec_input_shape", "models", "shape", None, True, placeholder="N,C,H,W"),
    ParamSpec(
        "doc_orientation_classify_model_name", "models", "string", "PP-LCNet_x1_0_doc_ori", True
    ),
    ParamSpec("doc_unwarping_model_name", "models", "string", "UVDoc", True),
    ParamSpec(
        "textline_orientation_model_name", "models", "string", "PP-LCNet_x1_0_textline_ori", True
    ),
    # §6.3 Preprocessing (constructor: model reload).
    ParamSpec("use_doc_orientation_classify", "preprocess", "bool", False, True),
    ParamSpec("use_doc_unwarping", "preprocess", "bool", False, True),
    ParamSpec("use_textline_orientation", "preprocess", "bool", False, True),
    # §6.4 Detection (live: goes to predict()).
    ParamSpec("text_det_limit_side_len", "det", "int", 64, False, min=32, max=4096),
    ParamSpec("text_det_limit_type", "det", "enum", "min", False, values=("min", "max")),
    ParamSpec("text_det_thresh", "det", "float", 0.3, False, min=0.0, max=1.0, step=0.05),
    ParamSpec("text_det_box_thresh", "det", "float", 0.6, False, min=0.0, max=1.0, step=0.05),
    ParamSpec("text_det_unclip_ratio", "det", "float", 1.5, False, min=0.1, max=10.0, step=0.1),
    # §6.5 Recognition (live: goes to predict()).
    ParamSpec("text_rec_score_thresh", "rec", "float", 0.0, False, min=0.0, max=1.0, step=0.05),
    ParamSpec("return_word_box", "rec", "bool", False, False),
)

# The 10 params deliberately kept off the wire (design §6.7): they are either
# meaningless for ReTran (lang/ocr_version), bypass the model catalog and
# download manager (model dirs, paddlex_config, decision D4/C2/C11), or are
# already controlled by the model manager UI (det/rec model names).
HIDDEN_KEYS: frozenset[str] = frozenset(
    {
        "lang",
        "ocr_version",
        "text_detection_model_dir",
        "text_recognition_model_dir",
        "doc_orientation_classify_model_dir",
        "doc_unwarping_model_dir",
        "textline_orientation_model_dir",
        "paddlex_config",
        "text_detection_model_name",
        "text_recognition_model_name",
    }
)

_BY_KEY: dict[str, ParamSpec] = {p.key: p for p in PARAMS}


def schema() -> dict:
    """Return the full wire schema for ``ocr.config.schema``.

    Returns:
        ``{"params": [...]}`` — one camelCase entry per :data:`PARAMS` item,
        in schema order (spec §7.1).
    """
    return {"params": [p.to_dict() for p in PARAMS]}


def _spec(key: str) -> ParamSpec:
    """Look up one exposed param.

    Args:
        key: Parameter name.

    Returns:
        The matching spec.

    Raises:
        RpcInvalidParams: The key is unknown or hidden from the schema.
    """
    spec = _BY_KEY.get(key)
    if spec is None:
        raise RpcInvalidParams(f"unsupported param: {key}")
    return spec


def _is_int(value: Any) -> bool:
    """JSON integer check that rejects booleans (bool subclasses int)."""
    return isinstance(value, int) and not isinstance(value, bool)


def _is_number(value: Any) -> bool:
    """Finite JSON number check (int or float, never bool)."""
    return _is_int(value) or (isinstance(value, float) and math.isfinite(value))


def _is_valid_value(spec: ParamSpec, value: Any) -> bool:
    """Check one value against its param's declared type."""
    if spec.type == "bool":
        return isinstance(value, bool)
    if spec.type == "int":
        return _is_int(value)
    if spec.type == "float":
        return _is_number(value)
    if spec.type == "string":
        return isinstance(value, str)
    if spec.type == "enum":
        return isinstance(value, str) and value in (spec.values or ())
    if spec.type == "json":
        return isinstance(value, dict)
    if spec.type == "shape":
        return (
            isinstance(value, list)
            and len(value) == 4
            and all(_is_int(dim) and dim > 0 for dim in value)
        )
    return False  # unknown schema type: reject rather than guess


def _validate(spec: ParamSpec, value: Any) -> None:
    """Validate one value against its param spec.

    Args:
        spec: Param the value belongs to.
        value: Non-null value to check.

    Raises:
        RpcInvalidParams: Wrong type, or (for numeric params) outside the
            spec's inclusive ``min``/``max`` range.
    """
    if not _is_valid_value(spec, value):
        raise RpcInvalidParams(f"invalid {spec.key}: expected {spec.type}")
    if spec.type in ("int", "float") and (
        (spec.min is not None and value < spec.min)
        or (spec.max is not None and value > spec.max)
    ):
        raise RpcInvalidParams(f"invalid {spec.key}: out of range [{spec.min}, {spec.max}]")


class OcrConfig:
    """User override map for the OCR engine parameters, with replace semantics.

    ``set`` treats the payload as the *whole* desired override map (§7.2):
    keys absent from the payload — or sent as ``null`` — fall back to their
    :data:`PARAMS` default, so ``{}`` restores every default. Validation is
    atomic: every key is checked before any state changes, and a rejected
    payload leaves the map untouched. Constructor options (``reload_required``)
    report through ``deferred`` when the caller postpones the model reload.

    Attributes:
        overrides: The keys the user set, and only those (a copy).
    """

    def __init__(self, on_reload: Optional[Callable[[], None]] = None) -> None:
        """Create an empty override map (all defaults).

        Args:
            on_reload: Called once after a non-deferred ``set`` that changes
                at least one constructor option; the engine uses it to drop
                the cached predictor so the next spot rebuilds with the new
                values.
        """
        self._on_reload = on_reload
        self._overrides: dict[str, Any] = {}

    @property
    def overrides(self) -> dict[str, Any]:
        """The keys the user set (only those; a copy)."""
        return dict(self._overrides)

    def get(self, key: str) -> Any:
        """Return the effective value of one param.

        Args:
            key: Parameter name from :data:`PARAMS`.

        Returns:
            The user's override, or the schema default when the key is unset.

        Raises:
            RpcInvalidParams: The key is unknown or hidden from the schema.
        """
        spec = _spec(key)
        return self._overrides.get(key, spec.default)

    def set(self, raw_params: Any, *, defer_reload: bool) -> dict:
        """Replace the override map with ``raw_params`` after validating it.

        The payload is the whole desired override map, not a patch: a key
        that is absent or ``null`` returns to its schema default, and ``{}``
        restores every default. Every key is validated before any state
        changes, so a rejected payload leaves the map untouched (atomic).

        Args:
            raw_params: Requested override map; must be a JSON object.
            defer_reload: When true, changed constructor options are stored
                but no model reload happens now — the cached predictor is
                kept and ``on_reload`` is not called; the next ``core.start``
                rebuilds with the new values.

        Returns:
            ``{"live": [...], "deferred": [...], "reloadRequired": bool}`` —
            the keys whose effective value changed, in :data:`PARAMS` order.
            ``live`` were applied now, ``deferred`` wait for the next reload,
            and ``reloadRequired`` is true when at least one changed key is a
            constructor option.

        Raises:
            RpcInvalidParams: The payload is not an object, contains a key
                that is not an exposed param (hidden keys are rejected the
                same way), or holds a value of the wrong type or range.
        """
        if not isinstance(raw_params, dict):
            raise RpcInvalidParams("'params' must be an object")
        # null means "use the default", exactly like an absent key (C5).
        new_overrides = {key: value for key, value in raw_params.items() if value is not None}
        # Validate the whole payload before mutating any state: a rejected
        # request must not leave a half-old/half-new map behind (atomic).
        for key, value in new_overrides.items():
            _validate(_spec(key), value)

        changed = [
            spec.key
            for spec in PARAMS
            if self._overrides.get(spec.key, spec.default)
            != new_overrides.get(spec.key, spec.default)
        ]
        self._overrides = new_overrides

        reload_changed = [key for key in changed if _BY_KEY[key].reload_required]
        deferred = reload_changed if defer_reload else []
        if reload_changed and not defer_reload and self._on_reload is not None:
            self._on_reload()
        return {
            "live": [key for key in changed if key not in deferred],
            "deferred": deferred,
            "reloadRequired": bool(reload_changed),
        }

    def constructor_kwargs(self) -> dict:
        """Resolve the constructor options for the next ``PaddleOCR(...)``.

        Returns:
            One ``key: value`` entry per constructor (``reload_required``)
            param, in schema order; entries whose effective value is ``None``
            are dropped so PaddleOCR/PaddleX pick their own default.
        """
        return {
            p.key: value
            for p in PARAMS
            if p.reload_required and (value := self.get(p.key)) is not None
        }

    def predict_kwargs(self) -> dict:
        """Resolve the live options for the next ``PaddleOCR.predict(...)``.

        Returns:
            One ``key: value`` entry per live (``not reload_required``) param,
            in schema order; entries whose effective value is ``None`` are
            dropped.
        """
        return {
            p.key: value
            for p in PARAMS
            if not p.reload_required and (value := self.get(p.key)) is not None
        }
