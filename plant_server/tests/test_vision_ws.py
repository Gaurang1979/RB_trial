"""Live vision floor over the WebSocket (Req 17.3 LV-01..03): sign-in, scoping by plant and line, and the push."""
import json
import time

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from backend import auth
from backend.api import create_app
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.topology import topology_from_dict
from conftest import new_store

NOW = int(time.time() * 1000)
STATIONS = {"S1": ("P", "L"), "S3": ("P", "L2"), "S4": ("P2", "L3")}


def ev(station, seq, ok=True):
    p, ln = STATIONS[station]
    e = {"kind": "inspection.result", "schema": 1, "plant": p, "line": ln, "station": station, "ts": NOW - 10_000 + seq, "seq": seq,
         "queue_id": "q1", "ok": ok, "cycle_ms": 40}
    return {**e, "reasons": [{"tool": "pin", "reason": "missing"}]} if not ok else e


@pytest.fixture
def env(base_cfg):
    store = new_store()
    sync_site(store, base_cfg)
    for d, (p, ln) in STATIONS.items():
        store.device_create({"id": d, "kind": "vision_station", "name": d, "plant_id": p, "line_id": ln, "enabled": 1, "created_at": 0})
    tok = {d: issue_token(store, d) for d in STATIONS}

    class E:
        pass
    e = E()
    e.store = store

    def make(api_key=None):
        e.c = TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg)), api_key=api_key))
        return e.c

    def send(station, *events):
        r = e.c.post("/vision/ingest/v1/events", content=json.dumps(list(events)), headers={"Authorization": f"Bearer {tok[station]}"})
        assert r.status_code == 200, r.text

    def person(name, role, level="org", sid=None):
        uid = store.user_create(name, name, "x", None, "test", 0)
        store.role_add(uid, role, level, sid, 0)
        t = auth.new_token()
        store.session_create(t, uid, int(time.time()), int(time.time()) + 3600)
        return uid, t
    e.make, e.send, e.person = make, send, person
    return e


def refused(c, path) -> int:
    with pytest.raises(WebSocketDisconnect) as closed:
        with c.websocket_connect(path) as ws:
            ws.receive_json()
    return closed.value.code


def lines(msg) -> list:
    assert msg["type"] == "floor"
    return [(x["plant"], x["line"]) for x in msg["lines"]]


def test_the_floor_is_sent_on_connect_and_pushed_when_an_event_changes_it(env):
    c = env.make()
    env.send("S1", ev("S1", 1))
    with c.websocket_connect("/vision/ws") as ws:
        first = ws.receive_json()
        assert lines(first) == [("P", "L")] and first["lines"][0]["bad"] == 0
        # the same lines as GET /vision/floor
        assert first["lines"] == c.get("/vision/floor").json()
        env.send("S1", ev("S1", 2, ok=False))
        pushed = ws.receive_json()
        assert pushed["lines"][0]["last_ng"][0]["reason"] == "missing"
        env.send("S3", ev("S3", 1))
        assert lines(ws.receive_json()) == [("P", "L"), ("P", "L2")]


def test_an_unchanged_snapshot_is_not_sent_again(env, monkeypatch):
    c = env.make()
    env.send("S1", ev("S1", 1))
    clock = [1000.0]

    def tick():
        clock[0] += 10   # every look is past the 5 s refresh: the snapshot is built again each tick
        return clock[0]
    monkeypatch.setattr("backend.vision.api.time.monotonic", tick)
    with c.websocket_connect("/vision/ws") as ws:
        first = ws.receive_json()
        time.sleep(1.2)   # a few ticks with nothing new
        env.send("S1", ev("S1", 2, ok=False))
        nxt = ws.receive_json()
        assert nxt != first and nxt["lines"][0]["last_ng"], "an unchanged snapshot was sent again"


def test_a_server_with_a_service_key_refuses_a_missing_or_wrong_credential(env):
    uid, token = env.person("org", auth.CORPORATE)
    c = env.make(api_key="s3cret")
    env.send("S1", ev("S1", 1))
    assert refused(c, "/vision/ws") == 4401
    assert refused(c, "/vision/ws?api_key=wrong") == 4401
    assert refused(c, "/vision/ws?token=not-a-token") == 4401
    with c.websocket_connect("/vision/ws?api_key=s3cret") as ws:
        assert lines(ws.receive_json()) == [("P", "L")]
    with c.websocket_connect(f"/vision/ws?token={token}") as ws:
        assert lines(ws.receive_json()) == [("P", "L")]


def test_each_caller_is_sent_only_the_lines_it_may_view(env):
    c = env.make(api_key="s3cret")
    env.send("S1", ev("S1", 1))
    env.send("S3", ev("S3", 1))
    env.send("S4", ev("S4", 1))
    _, line_l2 = env.person("line-l2", auth.QUALITY_MANAGER, "line", "L2")
    _, plant_p = env.person("plant-p", auth.PLANT_MANAGER, "plant", "P")
    _, nobody = env.person("nobody", auth.QUALITY_MANAGER, "line", "nowhere")

    def first(q):
        with c.websocket_connect(f"/vision/ws?{q}") as ws:
            return lines(ws.receive_json())
    assert first(f"token={line_l2}") == [("P", "L2")]
    assert first(f"token={plant_p}") == [("P", "L"), ("P", "L2")]
    assert first(f"token={nobody}") == []
    assert first("api_key=s3cret") == [("P", "L"), ("P", "L2"), ("P2", "L3")]
    assert first("api_key=s3cret&plant=P2") == [("P2", "L3")]
    assert first(f"token={plant_p}&plant=P&line=L") == [("P", "L")]
    assert refused(c, f"/vision/ws?token={line_l2}&plant=P&line=L") == 4403
    assert refused(c, f"/vision/ws?token={plant_p}&line=L") == 4400


def test_a_sign_out_closes_the_socket_and_a_new_role_widens_it(env, monkeypatch):
    c = env.make(api_key="s3cret")
    env.send("S1", ev("S1", 1))
    env.send("S3", ev("S3", 1))
    uid, token = env.person("line-l2", auth.QUALITY_MANAGER, "line", "L2")
    clock = [1000.0]
    monkeypatch.setattr("backend.vision.api.time.monotonic", lambda: clock[0])
    with c.websocket_connect(f"/vision/ws?token={token}") as ws:
        assert lines(ws.receive_json()) == [("P", "L2")]
        env.store.role_add(uid, auth.QUALITY_MANAGER, "line", "L", 0)
        clock[0] += 6   # the session's recheck picks up the new role
        assert lines(ws.receive_json()) == [("P", "L"), ("P", "L2")]
        c.post("/auth/logout", headers={"Authorization": f"Bearer {token}"})
        clock[0] += 6
        with pytest.raises(WebSocketDisconnect) as closed:
            for _ in range(5):
                ws.receive_json()
    assert closed.value.code == 4401
