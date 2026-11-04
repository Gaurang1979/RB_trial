"""Alarm catalogue (docs/vision-system/UI-UX-SPEC.md 4.4, LOCALISATION.md 2): the plain title and fix of each alarm
code a station raises, for alert titles, the floor API and so the mobile app and dashboard.

Strings live in lang/<tag>/alarm.json as alarm.<code>.title and alarm.<code>.fix (LOCALISATION.md 2.1). A language
without a string falls back to English; a code not in the catalogue still alerts, titled by its code, so a new
station alarm is never dropped for want of a string.
"""
from __future__ import annotations

import functools
import json
import os

# The station agent's codes (dotnet/VisionStudio/Services/Station.cs, dotnet/LineRunner/Program.cs).
CODES = ("camera_error", "child_camera_error", "parent_camera_error", "station_error", "pc_pressure",
         "ledger_tampered", "licence_content_key")
# LOCALISATION.md 1: the 15 languages. Only these name a folder, so a caller's language never reaches the file system.
LANGS = ("en", "hi", "mr", "gu", "ta", "te", "kn", "ml", "bn", "pa", "or", "ur", "as", "zh-Hans", "es")
_DIR = os.path.join(os.path.dirname(__file__), "lang")


@functools.lru_cache(maxsize=None)
def strings(lang: str) -> dict:
    if lang not in LANGS:
        return {}
    try:
        with open(os.path.join(_DIR, lang, "alarm.json"), encoding="utf-8") as f:
            d = json.load(f)
    except FileNotFoundError:
        return {}
    d.pop("_meta", None)
    return d


def text(code: str, lang: str = "en") -> dict:
    """{"title", "fix"} of an alarm code in `lang`, else English, else the code itself."""
    out = {}
    for part in ("title", "fix"):
        key = f"alarm.{code}.{part}"
        out[part] = strings(lang).get(key) or strings("en").get(key) or ""
    out["title"] = out["title"] or f"Alarm {code}"
    return out
