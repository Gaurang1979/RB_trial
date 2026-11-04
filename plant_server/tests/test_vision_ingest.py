"""PL2: station ingest of the Vision module (the rules of PR #49's .NET server, ported)."""
import json
import time

import pytest
from fastapi.testclient import TestClient

from backend.api import create_app
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.topology import topology_from_dict
from backend.vision import ingest
from conftest import new_store

NOW = int(time.time() * 1000)


def ev(kind="inspection.result", seq=1, queue="q1", station="S1", line="L", **kw):
    e = {"kind": kind, "schema": 1, "plant": "P", "line": line, "station": station, "ts": NOW - 1000 + seq, "seq": seq, "queue_id": queue}
    if kind == "inspection.result" and "ok" not in kw:
        kw = {"ok": True, "cycle_ms": 40, **kw}
    return {**e, **kw}


@pytest.fixture
def plant(base_cfg):
    store = new_store()
    sync_site(store, base_cfg)
    for d in ("S1", "S2"):
        store.device_create({"id": d, "kind": "vision_station", "name": d, "plant_id": "P", "line_id": "L", "enabled": 1, "created_at": 0})
    tokens = {d: issue_token(store, d) for d in ("S1", "S2")}

    def make(replay_hours=24):
        return TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg)), vision_replay_hours=replay_hours))
    return store, tokens, make


def post(c, token, *events, path="/vision/ingest/v1/events"):
    return c.post(path, content=json.dumps(list(events)), headers={"Authorization": f"Bearer {token}"})


def test_a_batch_is_stored_and_acknowledged(plant):
    store, tok, make = plant
    r = post(make(), tok["S1"], ev(seq=1), ev(seq=2), ev(seq=3))
    assert r.status_code == 200, r.text
    assert (r.json()["stored_up_to"], r.json()["stored"]) == (3, 3)
    assert ingest.count(store, "vision_event") == 3


def test_no_token_a_wrong_token_or_a_gateway_token_is_refused(plant):
    store, tok, make = plant
    c = make()
    assert c.post("/vision/ingest/v1/events", content=json.dumps([ev()])).status_code == 401
    assert post(c, "not-a-token", ev()).status_code == 401
    store.device_create({"id": "G1", "kind": "production_gateway", "name": "G1", "plant_id": "P", "line_id": "L", "enabled": 1, "created_at": 0})
    assert post(c, issue_token(store, "G1"), ev()).status_code == 401
    assert ingest.count(store, "vision_event") == 0


def test_a_resent_batch_is_stored_once(plant):
    store, tok, make = plant
    c = make()
    post(c, tok["S1"], ev(seq=1), ev(seq=2))
    r = post(c, tok["S1"], ev(seq=1), ev(seq=2), ev(seq=3)).json()
    assert (r["stored_up_to"], r["stored"], r["duplicates"]) == (3, 1, 2)
    assert ingest.count(store, "vision_event") == 3


def test_a_jump_in_the_sequence_is_a_gap_and_a_late_event_fills_it(plant):
    store, tok, make = plant
    c = make()
    assert post(c, tok["S1"], ev(seq=3), ev(seq=4), ev(seq=9)).json()["gaps"] == 2   # 1-2 and 5-8
    assert ingest.count(store, "vision_gap") == 2
    assert post(c, tok["S1"], ev(seq=6)).json()["stored"] == 1
    assert ingest.count(store, "vision_event") == 4


def test_an_invalid_event_is_kept_as_a_reject_and_does_not_block_the_queue(plant):
    store, tok, make = plant
    c = make()
    bad = [ev(seq=2, ok=False), ev(kind="camera.exploded", seq=3), ev(seq=4, ts=NOW * 1000), ev(seq=5, values={"x": float("nan")})]
    r = post(c, tok["S1"], ev(seq=1), *bad, ev(seq=6))
    assert r.status_code == 200, r.text
    assert (r.json()["stored_up_to"], r.json()["stored"], r.json()["rejected"]) == (6, 2, 4)
    # sent again: every one of them is a duplicate, the rejects included
    assert post(c, tok["S1"], ev(seq=1), *bad, ev(seq=6)).json()["duplicates"] == 6


def test_a_station_cannot_write_for_another_station_or_line(plant):
    store, tok, make = plant
    c = make()
    assert post(c, tok["S1"], ev(seq=1), ev(seq=2, station="S2")).status_code == 403
    assert post(c, tok["S1"], ev(seq=1, line="OTHER")).status_code == 403
    assert ingest.count(store, "vision_event") == 0


def test_a_malformed_batch_is_refused_whole(plant):
    store, tok, make = plant
    c = make()
    assert post(c, tok["S1"], ev(seq=2), ev(seq=1)).status_code == 400
    assert post(c, tok["S1"], ev(seq=1), ev(seq=2, queue="q2")).status_code == 400
    assert post(c, tok["S1"]).status_code == 400
    assert c.post("/vision/ingest/v1/events", content="not json", headers={"Authorization": f"Bearer {tok['S1']}"}).status_code == 400
    assert ingest.count(store, "vision_event") == 0


def test_a_new_queue_file_starts_again_at_one(plant):
    store, tok, make = plant
    c = make()
    post(c, tok["S1"], ev(seq=1), ev(seq=2))
    r = post(c, tok["S1"], ev(seq=1, queue="q2"), ev(seq=2, queue="q2")).json()
    assert (r["stored"], r["duplicates"]) == (2, 0)


def test_a_number_sent_again_with_other_content_is_a_conflict_and_nothing_is_stored(plant):
    store, tok, make = plant
    c = make()
    post(c, tok["S1"], ev(seq=1), ev(seq=2))
    restored = ev(seq=2, ok=False, reasons=[{"tool": "ocv", "reason": "wrong_char"}], cycle_ms=9)
    r = post(c, tok["S1"], ev(seq=1), restored, ev(seq=3))
    assert r.status_code == 409
    assert ingest.count(store, "vision_conflict") == 1
    assert ingest.count(store, "vision_event") == 2


def test_a_batch_above_the_limit_is_taken_in_part(plant):
    store, tok, make = plant
    r = post(make(), tok["S1"], *[ev(seq=i) for i in range(1, ingest.MAX_BATCH + 6)])
    assert r.status_code == 200
    assert r.json()["stored_up_to"] == ingest.MAX_BATCH


def test_a_disabled_station_is_refused(plant):
    store, tok, make = plant
    store.device_update("S1", enabled=0)
    assert post(make(), tok["S1"], ev()).status_code == 401


def test_heartbeat_updates_the_station_and_is_checked(plant):
    store, tok, make = plant
    c = make()
    hb = {"kind": "heartbeat", "schema": 1, "plant": "P", "line": "L", "station": "S1", "ts": NOW, "seq": 4, "agent_version": "1.0.0", "queue_depth": 2}
    h = {"Authorization": f"Bearer {tok['S1']}"}
    assert c.post("/vision/ingest/v1/heartbeat", content=json.dumps(hb), headers=h).status_code == 200
    assert c.post("/vision/ingest/v1/heartbeat", content=json.dumps({**hb, "station": "S2"}), headers=h).status_code == 403
    assert c.post("/vision/ingest/v1/heartbeat", content=json.dumps(ev()), headers=h).status_code == 400
    line = c.get("/vision/lines/P/L").json()
    assert line["stations"][0]["station"] == "S1" and line["stations"][0]["connected"] is True


def test_the_floor_is_scoped_by_role(plant):
    store, tok, make = plant
    # a line only vision uses, and a station on it
    store.master_upsert("line", {"id": "L4", "plant_id": "P", "area_id": "AR", "name": "Line 4", "source": "admin"})
    store.device_create({"id": "S4", "kind": "vision_station", "name": "S4", "plant_id": "P", "line_id": "L4", "enabled": 1, "created_at": 0})
    t4 = issue_token(store, "S4")
    c = make()
    post(c, tok["S1"], ev(seq=1))
    post(c, t4, ev(seq=1, station="S4", line="L4"))
    assert c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"}).status_code == 200
    root = {"Authorization": "Bearer " + c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]}
    r = c.post("/admin/users", headers=root, json={"username": "lineuser", "name": "Line user", "password": "linepass1",
                                                 "roles": [{"role": "factory_display", "scope_level": "line", "scope_id": "L"}]})
    assert r.status_code == 200, r.text
    lu = {"Authorization": "Bearer " + c.post("/auth/login", json={"username": "lineuser", "password": "linepass1"}).json()["token"]}
    assert [ln["line"] for ln in c.get("/vision/floor", headers=lu).json()] == ["L"]
    assert c.get("/vision/lines/P/L4", headers=lu).status_code == 403
    assert {ln["line"] for ln in c.get("/vision/floor", headers=root).json()} == {"L", "L4"}


def test_a_restart_rebuilds_open_alarms_line_states_and_jobs_however_old(plant):
    store, tok, make = plant
    post(make(), tok["S1"], ev("alarm.raised", 1, code="plc_lost", priority="high"), ev("alarm.acknowledged", 2, code="plc_lost", by="ravi"),
         ev("alarm.raised", 3, code="dark", priority="low"), ev("alarm.cleared", 4, code="dark"),
         ev("line.state", 5, state="stopped", reason="plc"), ev("job.changed", 6, to="Carton-24"),
         ev("counter.snapshot", 7, good=5, bad=1, total=6, shift_start=NOW - 3600_000))
    line = make(replay_hours=0).get("/vision/lines/P/L").json()   # no recent window at all
    assert [(a["code"], a["acknowledged_by"]) for a in line["alarms"]] == [("plc_lost", "ravi")]
    assert (line["state"], line["job"]) == ("stopped", "Carton-24")
    assert line["total"] == 0   # counters come only from the recent window
    assert make().get("/vision/lines/P/L").json()["total"] == 6


def test_a_refused_batch_never_undoes_another_modules_pending_write(plant):
    store, tok, make = plant
    c = make()
    post(c, tok["S1"], ev(seq=1))
    store.device_update("S2", name="Renamed")          # left pending on the shared connection, as the admin code does
    assert post(c, tok["S1"], ev(seq=1, ok=False, reasons=[{"tool": "t", "reason": "r"}])).status_code == 409
    store.db.rollback()
    assert store.device_get("S2")["name"] == "Renamed"


def test_a_bad_heartbeat_is_refused_and_leaves_the_database_usable(plant):
    store, tok, make = plant
    c = make()
    hb = {"kind": "heartbeat", "schema": 1, "plant": "P", "line": "L", "station": "S1", "ts": NOW, "seq": 4, "agent_version": "1.0.0"}
    h = {"Authorization": f"Bearer {tok['S1']}"}
    for bad in ({"seq": 2**70}, {"agent_version": 5}, {"queue_id": "q\x00"}, {"queue_id": "q" * 300}, {"queue_depth": -1},
                {"dropped": 2**64}):
        r = c.post("/vision/ingest/v1/heartbeat", content=json.dumps({**hb, **bad}), headers=h)
        assert r.status_code == 400, (bad, r.text)
    assert c.post("/vision/ingest/v1/heartbeat", content=json.dumps(hb), headers=h).status_code == 200
    assert post(c, tok["S1"], ev(seq=1)).json()["stored"] == 1


@pytest.mark.parametrize("bad", [{"seq": 2**63}, {"queue_id": "q\x00"}, {"queue_id": "q" * 201}, {"queue_id": 7}])
def test_a_batch_with_an_unholdable_seq_or_queue_id_is_refused_whole(plant, bad):
    store, tok, make = plant
    assert post(make(), tok["S1"], ev(seq=1), {**ev(seq=2), **bad}).status_code == 400
    assert ingest.count(store, "vision_event") == 0


@pytest.mark.parametrize("bad", [
    ev(seq=1, values={"x": 10**400}), ev(seq=1, job_version=2**40), ev(seq=1, camera=5), ev(seq=1, job=["a"]),
    ev(seq=1, part_id=3), ev(seq=1, image_id={}), ev(seq=1, models={"ocr": 2}), ev(seq=1, ts=2**64),
    ev("counter.snapshot", 1, good=2**64, bad=0, total=2**64, shift_start=NOW),
    ev("line.state", 1, state="running", reason=5), ev("alarm.raised", 1, code="x", by=5),
    ev("job.changed", 1, to="J", signature_id=1), ev("model.deployed", 1, tool="t", model_version="1", validation=1),
    ev("health.sample", 1, frame_drops=-1)])
def test_a_wrongly_typed_or_oversized_field_is_a_reject_not_a_failure(plant, bad):
    store, tok, make = plant
    r = post(make(), tok["S1"], bad)
    assert r.status_code == 200, r.text
    assert (r.json()["stored"], r.json()["rejected"]) == (0, 1)


def test_after_a_restart_a_line_whose_stations_went_quiet_shows_offline(plant):
    store, tok, make = plant
    c = make()
    hb = {"kind": "heartbeat", "schema": 1, "plant": "P", "line": "L", "station": "S1", "ts": NOW, "seq": 1, "agent_version": "1.0.0"}
    assert c.post("/vision/ingest/v1/heartbeat", content=json.dumps(hb), headers={"Authorization": f"Bearer {tok['S1']}"}).status_code == 200
    post(c, tok["S1"], ev("line.state", 1, state="running"))
    ingest.save_heartbeat(store, store.device_get("S1"), hb, NOW - 60_000)   # last heard a minute ago
    line = make().get("/vision/lines/P/L").json()
    assert line["state"] == "offline"
    assert [(s["station"], s["connected"], s["agent_version"]) for s in line["stations"]] == [("S1", False, "1.0.0")]


def test_the_replay_keeps_only_the_latest_state_job_and_open_alarms(plant):
    store, tok, make = plant
    c = make()
    evs = [ev("line.state", i, state="running" if i % 2 else "stopped") for i in range(1, 11)]
    evs += [ev("alarm.raised", 11, code="a"), ev("alarm.cleared", 12, code="a"), ev("alarm.raised", 13, code="b"),
            ev("alarm.acknowledged", 14, code="b", by="ravi"), ev("job.changed", 15, to="J1"), ev("job.changed", 16, to="J2"),
            ev("line.state", 17, state="fault", ts=NOW - 5000)]   # older by the station's clock: changes nothing
    assert post(c, tok["S1"], *evs).json()["stored"] == 17
    kept = ingest.replay(store, NOW + 3600_000)                    # nothing recent
    assert sorted((e["kind"], e.get("code"), e.get("state"), e.get("to")) for e in kept) == [
        ("alarm.acknowledged", "b", None, None), ("alarm.raised", "b", None, None), ("job.changed", None, None, "J2"),
        ("line.state", None, "stopped", None)]
    line = make(replay_hours=0).get("/vision/lines/P/L").json()
    assert (line["state"], line["job"], [(a["code"], a["acknowledged_by"]) for a in line["alarms"]]) == ("stopped", "J2", [("b", "ravi")])


def test_the_live_ng_strip_names_each_part_by_its_result_row_live_and_after_a_restart(plant):
    store, tok, make = plant
    c = make()
    ng = {"ok": False, "reasons": [{"tool": "t", "reason": "r"}], "result_id": 999}   # a station's own result_id is never taken
    assert post(c, tok["S1"], ev(seq=1), ev(seq=2, image_id="i2", **ng), ev(seq=3, **ng)).json()["stored"] == 3
    with store._lock:
        rows = dict(store._x("SELECT seq, id FROM vision_result WHERE device_id = 'S1' AND ok = 0").fetchall())

    def strip(client):
        return [(n["image_id"], n["result_id"]) for n in client.get("/vision/lines/P/L").json()["last_ng"]]
    want = [(None, rows[3]), ("i2", rows[2])]
    assert strip(c) == want and 999 not in rows.values()
    assert strip(make()) == want                    # rebuilt from the stored events at start


def test_text_the_database_cannot_hold_is_a_reject_and_the_batch_goes_on(plant):
    store, tok, make = plant
    r = post(make(), tok["S1"], ev(seq=1, part_id="a\x00b"), ev(seq=2))
    assert r.status_code == 200, r.text
    # SQLite keeps a NUL; PostgreSQL refuses it, which must reject that one event only
    assert r.json()["stored_up_to"] == 2 and r.json()["stored"] + r.json()["rejected"] == 2 and r.json()["stored"] >= 1
