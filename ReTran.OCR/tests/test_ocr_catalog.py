"""Unit tests for the static OCR model catalog (pure data, no paddle import)."""
from __future__ import annotations

from dataclasses import FrozenInstanceError

import pytest

from retran_core.ocr.catalog import CATALOG, ModelSpec, recommended, spec

V6_NAMES = {f"PP-OCRv6_{tier}_{kind}" for tier in ("tiny", "small", "medium") for kind in ("det", "rec")}

LEGACY_NAMES = {
    "korean_PP-OCRv5_mobile_rec",
    "cyrillic_PP-OCRv5_mobile_rec",
    "arabic_PP-OCRv5_mobile_rec",
    "th_PP-OCRv5_mobile_rec",
    "ta_PP-OCRv5_mobile_rec",
    "te_PP-OCRv5_mobile_rec",
    "devanagari_PP-OCRv5_mobile_rec",
    "el_PP-OCRv5_mobile_rec",
    "eslav_PP-OCRv5_mobile_rec",
    "chinese_cht_PP-OCRv3_mobile_rec",
}


def test_catalog_has_all_three_v6_tiers_for_det_and_rec():
    """The v6 group must offer tiny/small/medium in both kinds."""
    for name in V6_NAMES:
        assert spec(name) is not None, f"missing catalog entry: {name}"


def test_catalog_groups_match_the_plan():
    """v6 = exactly the 6 tier models; legacy = exactly the 10 v5/v3 fallback recs."""
    v6 = {m.name for m in CATALOG if m.group == "v6"}
    assert v6 == V6_NAMES
    legacy = {m.name for m in CATALOG if m.group == "legacy"}
    assert legacy == LEGACY_NAMES
    for m in CATALOG:
        if m.group == "legacy":
            assert m.kind == "rec"  # fallback models are rec-only; det stays v6


def test_every_spec_has_nonempty_langs_and_valid_kind():
    for m in CATALOG:
        assert m.name
        assert m.langs, f"{m.name} declares no languages"
        assert m.kind in ("det", "rec")
        assert m.group in ("v6", "legacy")
        assert m.size_mb > 0


def test_model_spec_is_frozen():
    m = CATALOG[0]
    with pytest.raises(FrozenInstanceError):
        m.name = "mutated"


def test_spec_lookup():
    m = spec("PP-OCRv6_small_rec")
    assert m is not None and m.kind == "rec" and m.group == "v6"
    assert spec("no_such_model_name") is None


def test_v6_unified_covers_50_languages_tiny_49_without_japanese():
    """Upstream: v6 small/medium cover zh/TC/en/ja + 46 Latin (50); tiny drops Japanese (49).

    The Latin split is verified against paddleocr 3.7.0's routing table
    (`_PPOCRV6_LANGS`): Latin (la) is covered, Pali (pi) is not.
    """
    for name in ("PP-OCRv6_small_det", "PP-OCRv6_small_rec", "PP-OCRv6_medium_det", "PP-OCRv6_medium_rec"):
        assert len(spec(name).langs) == 50, name
    for name in ("PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec"):
        langs = spec(name).langs
        assert len(langs) == 49, name
        assert "ja" not in langs
    langs = spec("PP-OCRv6_small_rec").langs
    for code in ("zh", "zh-hant", "en", "ja", "fr", "de", "es", "la"):  # ReTran app codes covered by v6
        assert code in langs
    # Pali is the one Latin-script language paddleocr 3.7 routes AWAY from v6.
    assert "pi" not in langs
    # Languages v6 does NOT cover must never appear (they route to v5 fallbacks):
    for code in ("ko", "ru", "ar", "th", "ta", "te", "hi", "el"):
        assert code not in langs


def test_legacy_models_declare_documented_languages():
    assert "ko" in spec("korean_PP-OCRv5_mobile_rec").langs
    assert "ru" in spec("cyrillic_PP-OCRv5_mobile_rec").langs
    assert "zh-hant" in spec("chinese_cht_PP-OCRv3_mobile_rec").langs
    # East Slavic model overlaps cyrillic for ru/be/uk; cyrillic wins the routing.
    assert set(spec("eslav_PP-OCRv5_mobile_rec").langs) >= {"ru", "be", "uk"}


def test_recommended_korean_uses_v5_korean_rec():
    det, rec = recommended("ko", "vi")
    assert rec == "korean_PP-OCRv5_mobile_rec"
    assert det == "PP-OCRv6_small_det"


def test_recommended_russian_uses_v5_cyrillic_rec():
    det, rec = recommended("ru", "vi")
    assert rec == "cyrillic_PP-OCRv5_mobile_rec"
    assert det == "PP-OCRv6_small_det"


def test_recommended_auto_uses_v6_small_pair():
    assert recommended("auto", "vi") == ("PP-OCRv6_small_det", "PP-OCRv6_small_rec")


def test_recommended_routes_app_source_languages():
    """Every language code the ReTran app can send must resolve to a catalog pair."""
    v6_pair = ("PP-OCRv6_small_det", "PP-OCRv6_small_rec")
    cases = {
        "auto": v6_pair,
        "en": v6_pair,
        "ja": v6_pair,
        "zh": v6_pair,
        "fr": v6_pair,
        "de": v6_pair,
        "es": v6_pair,
        "ko": ("PP-OCRv6_small_det", "korean_PP-OCRv5_mobile_rec"),
        "ru": ("PP-OCRv6_small_det", "cyrillic_PP-OCRv5_mobile_rec"),
    }
    for src, expected in cases.items():
        assert recommended(src, "vi") == expected, src
        det_name, rec_name = recommended(src, "vi")
        assert spec(det_name) is not None and spec(det_name).kind == "det"
        assert spec(rec_name) is not None and spec(rec_name).kind == "rec"


def test_recommended_uncovered_source_falls_back_to_v6_small():
    """Unknown / not-yet-covered src still yields the broadest multilingual pair."""
    assert recommended("xx-not-a-language", "vi") == ("PP-OCRv6_small_det", "PP-OCRv6_small_rec")
