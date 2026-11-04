"""PL2: vision alarms and silent vision stations as core alerts (lifecycle, escalation, acknowledge, scope)."""
import json
import time

import pytest
from fastapi.testclient import TestClient

from backend.alerts import MemoryNotifier
from backend.api import create_app
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.topology import topology_from_dict
from conftest import new_store, run
from test_live import feed

NOW = int(time.time() * 1000)
T = NOW // 1000


@pytest.fixture(autouse=True)
def _clock_now():
    """The server stamps alerts with its wall clock when a batch arrives, so each test takes its own "now"
    at its start; a time taken when the module loads falls behind by however long earlier tests ran."""
    global NOW, T
    NOW = int(time.time() * 1000)
    T = NOW // 1000


def ev(kind, seq, station="S1", line="L", **kw):
    return {"kind": kind, "schema": 1, "plant": "P", "line": line, "station": station, "ts": NOW - 1000 + seq, "seq": seq, "queue_id": "q1", **kw}


@pytest.fixture
def plant(base_cfg):
    def make(rules=None):
        store = new_store()
        raw = {**base_cfg, **({"alert_rules": rules} if rules is not None else {})}
        sync_site(store, raw)
        store.master_upsert("line", {"id": "L4", "plant_id": "P", "area_id": "AR", "name": "Line 4", "source": "admin"})
        for d, line in (("S1", "L"), ("S4", "L4")):
            store.device_create({"id": d, "kind": "vision_station", "name": d, "plant_id": "P", "line_id": line, "enabled": 1, "created_at": 0})
        tokens = {d: issue_token(store, d) for d in ("S1", "S4")}
        memory = MemoryNotifier()
        eng = LiveEngine(store, topology_from_dict(raw), alerts=True, notifiers={"memory": memory})
        app = create_app(eng)
        return eng, TestClient(app), tokens, app.state.vision_alerts, memory
    return make


def post(c, token, *events):
    r = c.post("/vision/ingest/v1/events", content=json.dumps(list(events)), headers={"Authorization": f"Bearer {token}"})
    assert r.status_code == 200, r.text


def vision(eng):
    return [a for a in eng.alerts.list() if a["type"].startswith("vision_")]


def test_a_station_alarm_opens_an_alert_with_its_priority_and_is_listed(plant):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="plc_lost", priority="high"))
    (a,) = vision(eng)
    assert (a["type"], a["severity"], a["state"], a["plant_id"], a["line_id"]) == ("vision_alarm", "high", "OPEN", "P", "L")
    assert "plc_lost" in a["title"] and a["evidence"]["station"] == "S1"
    assert [x["id"] for x in c.get("/alerts?state=active&line_id=L").json()] == [a["id"]]
    va.evaluate(T + 5)   # still raised: the same alert, not a second one
    assert len(vision(eng)) == 1


def test_acknowledged_at_the_station_stops_escalation_and_cleared_resolves(plant):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="dark", priority="medium"), ev("alarm.acknowledged", 2, code="dark", by="ravi"))
    (a,) = vision(eng)
    assert (a["state"], a["acked_by"]) == ("ACKNOWLEDGED", "ravi (at station S1)")
    va.evaluate(T + 2000)
    assert vision(eng)[0]["level"] == 0
    post(c, tok["S1"], ev("alarm.cleared", 3, code="dark"))
    (a,) = vision(eng)
    assert a["state"] == "RESOLVED" and a["resolution"] == "auto: alarm cleared at the station"


def test_an_unacknowledged_alarm_escalates(plant):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="plc_lost", priority="high"))
    va.evaluate(T + 600)
    assert vision(eng)[0]["level"] == 0
    va.evaluate(T + 901)
    d = eng.alerts.detail(vision(eng)[0]["id"])
    assert d["level"] == 1 and any(n["kind"] == "escalated" and n["recipient"] == "plant_manager" for n in d["notifications"])


def test_titles_name_the_station_by_its_device_name_and_the_alarm_by_its_catalogue_title(plant):
    eng, c, tok, va, _ = plant()
    eng.store.device_update("S1", name="Case packer 1")
    post(c, tok["S1"], ev("alarm.raised", 1, code="parent_camera_error", priority="high"))
    (a,) = vision(eng)
    assert a["title"].endswith("Case packer 1: Case camera not working") and "S1" not in a["title"]
    assert "Check its cable" in a["detail"] and "code parent_camera_error" in a["detail"]
    assert (a["evidence"]["station"], a["evidence"]["code"]) == ("S1", "parent_camera_error")   # the ids stay in the evidence
    floor = c.app.state.vision_floor
    floor.heartbeat({"plant": "P", "line": "L", "station": "S1", "seq": 0}, NOW)
    va.evaluate(T + 75)
    assert any(x["type"] == "vision_station_offline" and "vision station Case packer 1 offline" in x["title"] for x in vision(eng))
    # The floor API carries the same names, for the mobile app and the dashboard.
    (al,) = c.get("/vision/lines/P/L").json()["alarms"]
    assert (al["station"], al["station_name"], al["title"]) == ("S1", "Case packer 1", "Case camera not working") and al["fix"]
    assert c.get("/vision/floor?lang=hi").json()[0]["alarms"][0]["title"] == "Case camera not working"   # no Hindi yet: English


def test_a_silent_station_opens_an_offline_alert_that_resolves_when_it_returns(plant):
    eng, c, tok, va, _ = plant()
    floor = c.app.state.vision_floor
    floor.heartbeat({"plant": "P", "line": "L", "station": "S1", "seq": 0}, NOW)
    va.evaluate(T + 30)                  # offline on the floor, but below the rule's 60 s
    assert vision(eng) == []
    va.evaluate(T + 75)
    (a,) = vision(eng)
    assert (a["type"], a["severity"], a["state"]) == ("vision_station_offline", "high", "OPEN") and "S1" in a["title"]
    floor.heartbeat({"plant": "P", "line": "L", "station": "S1", "seq": 0}, (T + 100) * 1000)
    va.evaluate(T + 101)
    assert vision(eng)[0]["state"] == "OPEN"     # back, but clear for 30 s first
    floor.heartbeat({"plant": "P", "line": "L", "station": "S1", "seq": 0}, (T + 125) * 1000)
    va.evaluate(T + 131)
    assert vision(eng)[0]["state"] == "RESOLVED"


def test_production_evaluation_never_touches_vision_alerts(plant, base_cfg):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="plc_lost", priority="high"))
    sim, events = run(base_cfg, 1200)            # a healthy production line on its own data time (2026-01-01)
    feed(eng, events)
    assert vision(eng)[0]["state"] == "OPEN" and vision(eng)[0]["level"] == 0


def test_acknowledge_and_resolve_through_the_api_with_no_production_data(plant):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="dark", priority="low"))
    aid = vision(eng)[0]["id"]
    r = c.post(f"/alerts/{aid}/acknowledge", json={"user": "asha", "note": "looking"})
    assert r.status_code == 200, r.text
    assert r.json()["state"] == "ACKNOWLEDGED" and r.json()["acked_at"] is not None
    assert c.post(f"/alerts/{aid}/resolve", json={"user": "asha", "reason": "lamp changed"}).json()["state"] == "RESOLVED"


def test_alerts_of_a_vision_only_line_are_scoped_by_role(plant):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="a"))
    post(c, tok["S4"], ev("alarm.raised", 1, station="S4", line="L4", code="b"))
    assert c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"}).status_code == 200
    root = {"Authorization": "Bearer " + c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]}
    for user, line in (("user4", "L4"), ("userl", "L")):
        assert c.post("/admin/users", headers=root, json={"username": user, "name": user, "password": "linepass1",
                                                          "roles": [{"role": "factory_display", "scope_level": "line", "scope_id": line}]}).status_code == 200
    u4 = {"Authorization": "Bearer " + c.post("/auth/login", json={"username": "user4", "password": "linepass1"}).json()["token"]}
    assert [a["line_id"] for a in c.get("/alerts", headers=u4).json()] == ["L4"]
    assert [a["line_id"] for a in c.get("/alerts?line_id=L4", headers=u4).json()] == ["L4"]
    assert c.get("/alerts?line_id=L", headers=u4).status_code == 403
    assert c.get("/alerts?line_id=NOPE", headers=u4).status_code == 404
    assert {a["line_id"] for a in c.get("/alerts", headers=root).json()} == {"L", "L4"}


def test_a_vision_rule_can_be_turned_off_and_the_other_stays_on(plant):
    eng, c, tok, va, _ = plant(rules=[{"id": "vision-alarm", "type": "vision_alarm", "enabled": False}])
    assert [r["id"] for r in eng.alerts.rules] == ["vision-station-offline", "vision-grade-drift", "repeated-locks", "audit-chain-broken"]
    post(c, tok["S1"], ev("alarm.raised", 1, code="dark"))
    assert vision(eng) == []


def test_tuning_one_vision_rule_keeps_the_other_default(plant):
    eng, c, tok, va, _ = plant(rules=[{"id": "off", "type": "vision_station_offline", "threshold_s": 120}])
    assert [r["id"] for r in eng.alerts.rules] == ["off", "vision-alarm", "vision-grade-drift", "repeated-locks", "audit-chain-broken"]
    post(c, tok["S1"], ev("alarm.raised", 1, code="dark", priority="high"))
    assert [a["type"] for a in vision(eng)] == ["vision_alarm"]


def test_an_alarm_cleared_and_raised_again_is_a_new_alert(plant):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="dark", priority="low"), ev("alarm.acknowledged", 2, code="dark", by="ravi"))
    post(c, tok["S1"], ev("alarm.cleared", 3, code="dark"), ev("alarm.raised", 4, code="dark", priority="high"))
    old, new = sorted(vision(eng), key=lambda a: a["id"])
    assert (old["state"], old["resolution"]) == ("RESOLVED", "auto: alarm cleared at the station")
    assert (new["state"], new["severity"], new["acked_by"]) == ("OPEN", "high", None)


def test_a_failing_alert_pass_never_fails_the_stations_batch(plant, monkeypatch):
    eng, c, tok, va, _ = plant()

    def boom(*a, **k):
        raise RuntimeError("database gone")
    monkeypatch.setattr(eng.alerts, "_apply", boom)
    post(c, tok["S1"], ev("alarm.raised", 1, code="dark"))     # post() asserts 200
    monkeypatch.undo()
    va.evaluate()
    assert len(vision(eng)) == 1


def test_a_disabled_station_raises_no_alerts_and_its_alerts_resolve(plant):
    eng, c, tok, va, _ = plant()
    post(c, tok["S1"], ev("alarm.raised", 1, code="dark"))
    c.app.state.vision_floor.heartbeat({"plant": "P", "line": "L", "station": "S1", "seq": 0}, NOW)
    eng.store._x("UPDATE device SET enabled = 0 WHERE id = 'S1'")
    va.evaluate(T + 200)
    (a,) = vision(eng)
    assert (a["state"], a["resolution"]) == ("RESOLVED", "auto: station disabled or moved to another line")


def test_after_a_start_a_station_gets_the_threshold_to_call_in(plant):
    eng, c, tok, va, _ = plant()
    c.app.state.vision_floor.heartbeat({"plant": "P", "line": "L", "station": "S1", "seq": 0}, NOW - 3600_000)   # quiet before the start
    va.started = T
    va.evaluate(T + 30)
    assert vision(eng) == []
    va.evaluate(T + 61)
    assert [a["type"] for a in vision(eng)] == ["vision_station_offline"]
