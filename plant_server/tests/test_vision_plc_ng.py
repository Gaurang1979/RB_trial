"""A station's NG that the PLC decided (link lost, result not taken, PLC NG): stored, counted and shown as NG of tool "PLC"."""
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
IMAGE_ID = "20261031T101530123Z-000042"


def ev(kind="inspection.result", seq=1, queue="q1", station="S1", line="L", **kw):
    e = {"kind": kind, "schema": 1, "plant": "P", "line": line, "station": station, "ts": NOW - 1000 + seq, "seq": seq, "queue_id": queue}
    if kind == "inspection.result" and "ok" not in kw:
        kw = {"ok": True, "cycle_ms": 40, **kw}
    return {**e, **kw}


def plc_ng(seq, reason, **kw):
    return ev(seq=seq, ok=False, cycle_ms=120, reasons=[{"tool": "PLC", "reason": reason}], **kw)


@pytest.fixture
def plant(base_cfg):
    store = new_store()
    sync_site(store, base_cfg)
    store.device_create({"id": "S1", "kind": "vision_station", "name": "S1", "plant_id": "P", "line_id": "L", "enabled": 1, "created_at": 0})
    return store, issue_token(store, "S1"), TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg))))


def post(c, token, *events):
    return c.post("/vision/ingest/v1/events", content=json.dumps(list(events)), headers={"Authorization": f"Bearer {token}"})


def results(store):
    with store._lock:
        return store._x("SELECT ok, part_id, image_id, reasons FROM vision_result ORDER BY seq").fetchall()


def test_a_plc_link_loss_ng_is_stored_and_shown_as_ng_on_the_live_floor(plant):
    store, tok, c = plant
    r = post(c, tok, ev(seq=1, part_id="A12-0001"),
             plc_ng(2, "link_lost", part_id="A12-0002", image_id=IMAGE_ID),
             ev(kind="counter.snapshot", seq=3, good=1, bad=1, total=2, shift_start=NOW - 3_600_000))
    assert r.status_code == 200, r.text
    assert (r.json()["stored_up_to"], r.json()["stored"]) == (3, 3)
    assert ingest.count(store, "vision_event") == 3
    assert ingest.count(store, "vision_reject") == 0
    assert [(g[0], g[1], g[2], json.loads(g[3])) for g in results(store)] == [
        (1, "A12-0001", None, []),
        (0, "A12-0002", IMAGE_ID, [{"tool": "PLC", "reason": "link_lost"}])]
    ln = c.get("/vision/lines/P/L").json()
    assert (ln["good"], ln["bad"], ln["total"]) == (1, 1, 2)
    assert ln["top_ng"] == "link_lost"
    assert [(x["tool"], x["reason"], x["image_id"], x["station"]) for x in ln["last_ng"]] == [("PLC", "link_lost", IMAGE_ID, "S1")]
    floor = c.get("/vision/floor").json()
    assert [x["last_ng"][0]["tool"] for x in floor] == ["PLC"]


def test_the_ng_review_lists_the_plc_ng_part_with_its_image_id(plant):
    store, tok, c = plant
    post(c, tok, ev(seq=1, part_id="A12-0001"), plc_ng(2, "link_lost", part_id="A12-0002", image_id=IMAGE_ID))
    items = c.get("/vision/ng").json()["items"]
    assert len(items) == 1                      # the OK part is not an NG
    it = items[0]
    assert (it["part_id"], it["image_id"], it["station"], it["plant"], it["line"]) == ("A12-0002", IMAGE_ID, "S1", "P", "L")
    assert it["reasons"] == [{"tool": "PLC", "reason": "link_lost"}] and it["cycle_ms"] == 120
    assert it["verdict"] == "pending" and it["image_state"] == "waiting"      # the server asks the station for the kept image
    assert [x["part_id"] for x in c.get("/vision/ng", params={"reason": "link_lost"}).json()["items"]] == ["A12-0002"]
    assert c.get("/vision/ng", params={"reason": "not_taken"}).json()["items"] == []
    assert c.get(f"/vision/ng/{it['id']}").json()["reasons"] == it["reasons"]
    s = c.get("/vision/ng/summary").json()
    assert [(x["line"], x["ng"], x["pending"]) for x in s] == [("L", 1, 1)]


def test_a_plc_not_taken_ng_without_an_image_is_stored_and_counted_as_ng(plant):
    store, tok, c = plant
    r = post(c, tok, ev(seq=1, part_id="A12-0001"), plc_ng(2, "not_taken", part_id="A12-0003"),
             ev(kind="counter.snapshot", seq=3, good=1, bad=1, total=2, shift_start=NOW - 3_600_000))
    assert r.status_code == 200, r.text
    assert (r.json()["stored"], r.json().get("rejected", 0)) == (3, 0)
    assert ingest.count(store, "vision_event") == 3 and ingest.count(store, "vision_reject") == 0
    ln = c.get("/vision/lines/P/L").json()
    assert (ln["good"], ln["bad"]) == (1, 1) and ln["top_ng"] == "not_taken"
    assert [(x["tool"], x["reason"], x["image_id"]) for x in ln["last_ng"]] == [("PLC", "not_taken", None)]
    it = c.get("/vision/ng").json()["items"][0]
    assert (it["part_id"], it["image_id"], it["reasons"], it["image_state"]) == ("A12-0003", None, [{"tool": "PLC", "reason": "not_taken"}], "none")
    with store._lock:
        assert store._x("SELECT COUNT(*) FROM vision_image_request").fetchone()[0] == 0     # no image id, nothing to ask for
