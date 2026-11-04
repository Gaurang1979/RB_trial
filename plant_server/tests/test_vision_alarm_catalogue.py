import json
import os
import re

import pytest

from backend.vision import alarms


def test_every_station_code_has_an_english_title_and_fix():
    for code in alarms.CODES:
        t = alarms.text(code)
        assert t["title"] and not t["title"].startswith("Alarm ") and t["fix"], code


def test_the_station_agent_raises_only_catalogued_codes():
    """Every code Vision Studio and LineRunner raise (Alarm("<code>", ...)) is in the catalogue."""
    root = os.path.join(os.path.dirname(__file__), "..", "..", "dotnet")
    if not os.path.isdir(root):
        pytest.skip("the line PC sources are not next to the plant server")
    raised = set()
    for f in ("VisionStudio/Services/Station.cs", "LineRunner/Program.cs"):
        raised |= set(re.findall(r'\.Alarm\("([a-z_]+)"', open(os.path.join(root, f), encoding="utf-8").read()))
    assert raised and raised <= set(alarms.CODES), raised - set(alarms.CODES)


def test_english_file_has_no_stray_keys_and_meta_names_real_keys():
    d = json.load(open(os.path.join(os.path.dirname(alarms.__file__), "lang", "en", "alarm.json"), encoding="utf-8"))
    meta = d.pop("_meta")
    want = {f"alarm.{c}.{p}" for c in alarms.CODES for p in ("title", "fix")}
    assert set(d) == want and set(meta) <= want


def test_unknown_code_or_language_falls_back_and_never_reads_outside_the_folder():
    assert alarms.text("plc_lost") == {"title": "Alarm plc_lost", "fix": ""}
    assert alarms.text("camera_error", "hi") == alarms.text("camera_error")       # no Hindi table yet
    assert alarms.strings("../../../etc") == {} and alarms.strings("xx") == {}
