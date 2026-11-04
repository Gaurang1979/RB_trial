"""Req 17.3 PQ-01..05: print grades kept per station and tool, the trend, and the drift alarm on the vision alert lifecycle."""
import json
import time

import pytest
from fastapi.testclient import TestClient

from backend import auth
from backend.alerts import MemoryNotifier, normalize_rule
from backend.api import create_app
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.topology import topology_from_dict
from backend.vision import grades, ingest
from conftest import new_store

NOW = int(time.time() * 1000)
T = NOW // 1000


@pytest.fixture(autouse=True)
def _clock_now():
    global NOW, T
    NOW = int(time.time() * 1000)
    T = NOW // 1000


def part(seq, grade=None, station="S1", line="L", tool="Read code", ts=None, **values):
    v = dict(values)
    if grade is not None:
        v[f"{tool}.grade"] = grade
        v[f"{tool}.grade.modulation"] = grade
        v[f"{tool}.symbol_contrast_pct"] = 70.0
        v[f"{tool}.modulation"] = 0.6
    return {"kind": "inspection.result", "schema": 1, "plant": "P", "line": line, "station": station, "seq": seq, "queue_id": "q1",
            "ts": ts if ts is not None else NOW - 100_000 + seq, "ok": True, "values": v}


@pytest.fixture
def plant(base_cfg):
    def make(rules=None):
        store = new_store()
        raw = {**base_cfg, **({"alert_rules": rules} if rules is not None else {})}
        sync_site(store, raw)
        for d, line in (("S1", "L"), ("S3", "L2")):
            store.device_create({"id": d, "kind": "vision_station", "name": d, "plant_id": "P", "line_id": line, "enabled": 1, "created_at": 0})
        tokens = {d: issue_token(store, d) for d in ("S1", "S3")}
        memory = MemoryNotifier()
        eng = LiveEngine(store, topology_from_dict(raw), alerts=True, notifiers={"memory": memory})
        app = create_app(eng)

        class E:
            pass
        e = E()
        e.store, e.eng, e.c, e.va, e.tok = store, eng, TestClient(app), app.state.vision_alerts, tokens

        def post(station, *events):
            r = e.c.post("/vision/ingest/v1/events", content=json.dumps(list(events)), headers={"Authorization": f"Bearer {tokens[station]}"})
            assert r.status_code == 200, r.text
            return r.json()
        e.post = post

        def person(name, role, level, sid):
            uid = store.user_create(name, name, "x", None, "test", 0)
            store.role_add(uid, role, level, sid, 0)
            t = auth.new_token()
            store.session_create(t, uid, int(time.time()), int(time.time()) + 3600)
            return {"Authorization": f"Bearer {t}"}
        e.person = person
        return e
    return make


def drift_alerts(eng):
    return [a for a in eng.alerts.list() if a["type"] == "vision_grade_drift"]


def rows(store):
    with store._lock:
        return store._x("SELECT device_id, seq, tool, grade, ungraded, grade_min, params FROM vision_grade ORDER BY seq, tool").fetchall()


# ---- PQ-01: what is kept ----

def test_grades_of_reads_each_tool_and_ignores_other_values():
    g = grades.grades_of({"Code A.grade": 3.0, "Code A.grade.modulation": 2.0, "Code A.symbol_contrast_pct": 61.5, "Code A.modulation": 0.55,
                          "Code B.grade.ungraded": 2, "Code C.grade": 7.0, "Code C.grade_min": 2.0, "Text.score": 0.9})
    assert set(g) == {"Code A", "Code B", "Code C"}
    assert g["Code A"] == {"grade": 3.0, "ungraded": 0, "grade_min": None, "symbol_contrast_pct": 61.5, "modulation": 0.55,
                           "params": {"modulation": 2.0}}
    assert (g["Code B"]["grade"], g["Code B"]["ungraded"]) == (None, 2)
    # A grade outside 0..4 is not a grade: that part counts as ungraded for the tool.
    assert (g["Code C"]["grade"], g["Code C"]["ungraded"], g["Code C"]["grade_min"]) == (None, 1, 2.0)
    assert grades.grades_of(None) == {} and grades.grades_of({"x.grade": 1.0, "x" * 200 + ".grade": 1.0}).keys() == {"x"}


def test_a_graded_result_keeps_its_grades_once_and_an_ungraded_part_is_kept(plant):
    e = plant()
    e.post("S1", part(1, 3.0), part(2), part(3, **{"Read code.grade.ungraded": 1}))
    e.post("S1", part(1, 3.0))   # the same event again: a duplicate, nothing more kept
    assert rows(e.store) == [("S1", 1, "Read code", 3.0, 0, None, '{"modulation":3.0}'), ("S1", 3, "Read code", None, 1, None, "{}")]


def test_a_stations_own_lowest_grade_is_kept_with_the_grade(plant):
    e = plant()
    e.post("S1", part(1, 3.0, **{"Read code.grade_min": 2.0}))
    assert rows(e.store) == [("S1", 1, "Read code", 3.0, 0, 2.0, '{"modulation":3.0}')]


def test_the_backfill_keeps_the_grades_of_old_results(plant):
    e = plant()
    e.post("S1", part(1, 2.0))
    with e.store._lock:
        e.store._x("DELETE FROM vision_grade")
        e.store._x("DELETE FROM vision_result")
        e.store.db.commit()
    ingest.backfill_results(e.store)
    assert [r[:4] for r in rows(e.store)] == [("S1", 1, "Read code", 2.0)]


# ---- drift: the numbers ----

def test_drift_steady_good_print_is_ok_and_few_samples_are_not_judged():
    assert grades.drift([(4.0, None)] * 5)["state"] == "too_few"
    d = grades.drift([(4.0, None), (3.0, None)] * 10)
    assert (d["state"], d["met"], d["near"], d["lowest"], d["lowest_from"]) == ("ok", False, False, 2.0, "rule")


def test_drift_mean_near_the_lowest_grade_or_a_fast_fall_is_met_before_the_lowest_is_reached():
    near = grades.drift([(3.0, None), (2.0, None)] * 10)          # mean 2.5 = C + margin
    assert near["met"] and near["mean"] == 2.5
    falling = grades.drift([(4.0 - i * 0.06, None) for i in range(20)])   # 4.0 -> 2.86, every part still B or better
    assert min(g for g in [4.0 - i * 0.06 for i in range(20)]) > 2.5
    assert falling["met"] and falling["slope"] < 0 and falling["projected"] <= 2.0


def test_drift_counts_an_ungraded_part_as_f_and_takes_the_stations_lowest_grade():
    d = grades.drift([(4.0, None)] * 12 + [(None, None)] * 8)
    assert d["ungraded"] == 8 and d["mean"] == 2.4 and d["met"]
    d = grades.drift([(3.0, 3.0)] * 20)                           # the station grades against B: 3.0 is within its margin
    assert (d["lowest"], d["lowest_from"], d["met"]) == (3.0, "station", True)


def test_a_drift_rule_with_wrong_numbers_is_refused():
    base = {"id": "g", "type": "vision_grade_drift"}
    assert normalize_rule(base)["type"] == "vision_grade_drift"
    for bad in ({"lowest_grade": 0}, {"lowest_grade": 5}, {"margin": -1}, {"window": 1}, {"min_samples": 30, "window": 20}, {"window": 2.5}):
        with pytest.raises(ValueError):
            normalize_rule({**base, **bad})


# ---- PQ-03: the alarm on the vision alert lifecycle ----

def test_drift_opens_one_alert_per_station_and_tool_and_resolves_when_the_print_recovers(plant):
    e = plant()
    e.post("S1", *[part(i, 4.0 if i % 2 else 3.0) for i in range(1, 21)])
    assert drift_alerts(e.eng) == []
    e.post("S1", *[part(i, 2.0 if i % 2 else 3.0) for i in range(21, 41)])      # mean 2.5: within the margin of C
    (a,) = drift_alerts(e.eng)
    assert (a["state"], a["severity"], a["plant_id"], a["line_id"]) == ("OPEN", "medium", "P", "L")
    assert "Read code" in a["title"] and "toward C" in a["title"]
    assert a["evidence"]["station"] == "S1" and a["evidence"]["mean"] == 2.5 and a["evidence"]["samples"] == 20
    e.va.evaluate(T + 5)
    assert len(drift_alerts(e.eng)) == 1
    e.post("S1", *[part(i, 4.0) for i in range(41, 61)])     # print fixed: clear, then resolved after clear_after_s
    e.va.evaluate(T + 10)
    assert drift_alerts(e.eng)[0]["state"] == "OPEN"
    e.va.evaluate(T + 400)
    (a,) = drift_alerts(e.eng)
    assert a["state"] == "RESOLVED" and "print grade back above" in a["resolution"]


def test_a_mean_just_above_the_warning_level_keeps_an_open_alert_but_never_opens_one(plant):
    e = plant()
    e.post("S1", *[part(i, 2.7) for i in range(1, 21)])       # 2.7: above C + 0.5, below C + 0.75, steady
    assert drift_alerts(e.eng) == []


def test_old_grades_and_a_disabled_rule_raise_nothing(plant, base_cfg):
    e = plant()
    e.post("S1", *[part(i, 1.0, ts=NOW - 9 * 3600_000 + i) for i in range(1, 21)])   # older than max_age_s (8 h)
    assert drift_alerts(e.eng) == []
    off = plant(rules=[{"id": "g", "type": "vision_grade_drift", "enabled": False}])
    off.post("S1", *[part(i, 1.0) for i in range(1, 21)])
    assert drift_alerts(off.eng) == []


# ---- PQ-02: the trend ----

def test_the_trend_buckets_grades_per_station_and_tool_with_the_drift_status(plant):
    e = plant()
    hour0 = NOW - NOW % 3600_000 - 3 * 3600_000
    e.post("S1", part(1, 4.0, ts=hour0 + 1), part(2, 2.0, ts=hour0 + 2), part(3, ts=hour0 + 3, **{"Read code.grade.ungraded": 1}),
           part(4, 3.0, ts=hour0 + 3600_000 + 5), part(5, 3.0, tool="Lot code", ts=hour0 + 6))
    r = e.c.get(f"/vision/grades/trend?since={hour0}&until={hour0 + 2 * 3600_000}&bucket_s=3600")
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["rule"]["lowest_grade"] == 2.0
    by_tool = {s["tool"]: s for s in body["series"]}
    rc = by_tool["Read code"]
    assert (rc["plant"], rc["line"], rc["station"]) == ("P", "L", "S1")
    assert rc["buckets"] == [
        {"start": hour0, "samples": 3, "mean": 3.0, "worst": 2.0, "ungraded": 1, "symbol_contrast_pct": 70.0, "modulation": 0.6},
        {"start": hour0 + 3600_000, "samples": 1, "mean": 3.0, "worst": 3.0, "ungraded": 0, "symbol_contrast_pct": 70.0, "modulation": 0.6}]
    assert rc["drift"]["samples"] == 4 and rc["drift"]["state"] == "too_few" and rc["drift"]["ungraded"] == 1
    assert [b["samples"] for b in by_tool["Lot code"]["buckets"]] == [1]
    one = e.c.get(f"/vision/grades/trend?since={hour0}&until={hour0 + 2 * 3600_000}&tool=Lot%20code").json()["series"]
    assert [s["tool"] for s in one] == ["Lot code"]


def test_the_trend_shows_only_the_lines_the_caller_may_see_and_refuses_bad_ranges(plant):
    e = plant()
    e.post("S1", part(1, 3.0))
    e.post("S3", part(1, 2.0, station="S3", line="L2"))
    l2 = e.person("q2", auth.QUALITY_MANAGER, "line", "L2")
    assert [s["line"] for s in e.c.get("/vision/grades/trend", headers=l2).json()["series"]] == ["L2"]
    assert e.c.get("/vision/grades/trend?plant=P&line=L", headers=l2).status_code == 403
    assert sorted(s["line"] for s in e.c.get("/vision/grades/trend").json()["series"]) == ["L", "L2"]
    assert e.c.get(f"/vision/grades/trend?since={NOW}&until={NOW}").status_code == 400
    assert e.c.get(f"/vision/grades/trend?since={NOW - 400 * 86400_000}&until={NOW}&bucket_s=60").status_code == 400
    assert e.c.get("/vision/grades/trend?bucket_s=10").status_code == 422


# ---- PQ-06: the dashboard page ----
def test_the_print_quality_page_is_served_and_reads_the_trend(plant):
    e = plant()
    r = e.c.get("/vision/grades/page")
    assert r.status_code == 200 and "Print quality" in r.text and "/vision/grades/trend" in r.text
    assert "lowest" in r.text and "not gradable" in r.text and "Drifting to the lowest grade" in r.text
