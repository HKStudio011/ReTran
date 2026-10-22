"""Static OCR model catalog: PP-OCRv6 tiers plus v5/v3 per-language rec fallbacks.

Pure data module — no downloads, no paddle import. Language metadata and sizes
come from the upstream docs (checked 2026-10-23):

- PP-OCRv6 intro: the unified small/medium models cover 50 languages
  (Simplified Chinese, Traditional Chinese, English, Japanese + 46
  Latin-script); tiny covers 49 (no Japanese).
- PP-OCRv5 multilingual "Models and Their Supported Languages" table:
  the exact language sets and published storage sizes of the v5/v3
  per-language rec models.

Language codes are lowercase ISO 639-1 where a code exists (matching the
codes the ReTran app sends); script splits without one keep the PaddleOCR
abbreviation (`zh-hant`, `rs_latin`, `rs_cyrillic`, ...).
"""
from __future__ import annotations

from dataclasses import dataclass

# Latin-script languages the v6 unified models cover. This is the
# latin_PP-OCRv5 documented set minus liturgical Latin, i.e. 46 languages.
_LATIN_LANGS: tuple[str, ...] = (
    "af", "az", "bs", "ca", "cs", "cy", "da", "de", "es", "et", "eu", "fi", "fr", "ga",
    "gl", "hr", "hu", "id", "is", "it", "ku", "lb", "lt", "lv", "mi", "ms", "mt", "nl",
    "no", "oc", "pi", "pl", "pt", "qu", "rm", "ro", "rs_latin", "sk", "sl", "sq", "sv",
    "sw", "tl", "tr", "uz", "vi",
)

# 50 languages (zh/TC/en/ja + the 46 Latin above) for the v6 small/medium tiers.
_V6_LANGS: tuple[str, ...] = ("zh", "zh-hant", "en", "ja", *_LATIN_LANGS)

# Tiny supports 49: the same coverage without Japanese (upstream tier claim).
_V6_TINY_LANGS: tuple[str, ...] = ("zh", "zh-hant", "en", *_LATIN_LANGS)

# Cyrillic-script languages of cyrillic_PP-OCRv5_mobile_rec (33 + English).
_CYRILLIC_LANGS: tuple[str, ...] = (
    "ru", "be", "uk", "bg", "sr", "rs_cyrillic", "mk", "mn", "ab", "ady", "kbd", "av",
    "dar", "inh", "ce", "lki", "lez", "tab", "kk", "ky", "tg", "tt", "cv", "ba", "mhr",
    "mo", "udm", "kv", "os", "bua", "xal", "tyv", "sah", "kaa", "en",
)

# Arabic-script languages of arabic_PP-OCRv5_mobile_rec.
_ARABIC_LANGS: tuple[str, ...] = ("ar", "fa", "ug", "ur", "ps", "ku", "sd", "bal", "en")

# Devanagari-script languages of devanagari_PP-OCRv5_mobile_rec.
_DEVANAGARI_LANGS: tuple[str, ...] = (
    "hi", "mr", "ne", "bh", "mai", "anp", "bho", "mah", "sat", "new", "gom", "sa", "bgc",
    "en",
)


@dataclass(frozen=True)
class ModelSpec:
    """One selectable OCR model.

    Attributes:
        name: Registry name understood by PaddleOCR (e.g. "PP-OCRv6_small_rec").
        kind: Model role, "det" (text detection) or "rec" (text recognition).
        langs: Lowercase language codes the model covers (display hint + routing).
        size_mb: Published inference-model storage size in megabytes.
        group: "v6" (unified tiers) or "legacy" (per-language v5/v3 fallback).
    """

    name: str
    kind: str  # "det" | "rec"
    langs: tuple[str, ...]
    size_mb: float
    group: str  # "v6" | "legacy"


CATALOG: tuple[ModelSpec, ...] = (
    # --- v6 group: unified PP-OCRv6 tiers (recommended default pair: small) ---
    ModelSpec("PP-OCRv6_tiny_det", "det", _V6_TINY_LANGS, 1.9, "v6"),
    ModelSpec("PP-OCRv6_small_det", "det", _V6_LANGS, 9.6, "v6"),
    ModelSpec("PP-OCRv6_medium_det", "det", _V6_LANGS, 59.4, "v6"),
    ModelSpec("PP-OCRv6_tiny_rec", "rec", _V6_TINY_LANGS, 4.4, "v6"),
    ModelSpec("PP-OCRv6_small_rec", "rec", _V6_LANGS, 20.4, "v6"),
    ModelSpec("PP-OCRv6_medium_rec", "rec", _V6_LANGS, 73.3, "v6"),
    # --- legacy group: v5/v3 per-language rec fallbacks for scripts v6 lacks.
    # Order matters for routing: cyrillic (general) before eslav (East Slavic
    # subset) so "ru" resolves to cyrillic_PP-OCRv5_mobile_rec.
    ModelSpec("korean_PP-OCRv5_mobile_rec", "rec", ("ko", "en"), 14.0, "legacy"),
    ModelSpec("cyrillic_PP-OCRv5_mobile_rec", "rec", _CYRILLIC_LANGS, 7.7, "legacy"),
    ModelSpec("arabic_PP-OCRv5_mobile_rec", "rec", _ARABIC_LANGS, 7.6, "legacy"),
    ModelSpec("th_PP-OCRv5_mobile_rec", "rec", ("th", "en"), 7.5, "legacy"),
    ModelSpec("ta_PP-OCRv5_mobile_rec", "rec", ("ta", "en"), 7.5, "legacy"),
    ModelSpec("te_PP-OCRv5_mobile_rec", "rec", ("te", "en"), 7.5, "legacy"),
    ModelSpec("devanagari_PP-OCRv5_mobile_rec", "rec", _DEVANAGARI_LANGS, 7.5, "legacy"),
    ModelSpec("el_PP-OCRv5_mobile_rec", "rec", ("el", "en"), 7.5, "legacy"),
    ModelSpec("eslav_PP-OCRv5_mobile_rec", "rec", ("ru", "be", "uk", "en"), 14.0, "legacy"),
    ModelSpec("chinese_cht_PP-OCRv3_mobile_rec", "rec", ("zh-hant",), 10.8, "legacy"),
)

# Default pair per design C1: v6 small balances accuracy and size.
_DEFAULT_DET = "PP-OCRv6_small_det"
_DEFAULT_REC = "PP-OCRv6_small_rec"


def spec(name: str) -> ModelSpec | None:
    """Look up a catalog entry by exact model name.

    Args:
        name: Registry name, e.g. "PP-OCRv6_small_rec".

    Returns:
        The matching spec, or `None` when the name is not in the catalog.
    """
    for m in CATALOG:
        if m.name == name:
            return m
    return None


def recommended(src: str, dst: str) -> tuple[str, str]:
    """Return the recommended (det, rec) model pair for a source language.

    "auto" and any language covered by the v6 unified model resolve to the
    default v6 small pair; languages outside v6 coverage fall back to the
    first legacy rec model that declares the language (e.g. ko -> korean,
    ru -> cyrillic). Unrecognized codes fall back to the v6 small pair, the
    broadest multilingual model available.

    The det slot is always the default v6 tier: detection is line/script
    level and the catalog intentionally ships no legacy det models.

    Args:
        src: Source language code the app OCRs (e.g. "auto", "ja", "ko").
        dst: Translation target code; kept for interface stability — it does
            not influence the pair because OCR only reads the source text.

    Returns:
        (det_model_name, rec_model_name) suitable for PaddleOCR constructor.
    """
    code = src.strip().lower()
    if code == "auto":
        return (_DEFAULT_DET, _DEFAULT_REC)
    if code in spec(_DEFAULT_REC).langs:  # v6 unified coverage wins
        return (_DEFAULT_DET, _DEFAULT_REC)
    for m in CATALOG:
        if m.kind == "rec" and m.group == "legacy" and code in m.langs:
            return (_DEFAULT_DET, m.name)
    return (_DEFAULT_DET, _DEFAULT_REC)
