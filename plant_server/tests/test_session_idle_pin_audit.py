"""Idle sign-out of server sessions and the audit trail with the mobile PIN events (Part 11 PT11-05, PT11-06,
PT11-08, PT11-20, PT11-21, PT11-23; PART11-DESIGN 3 and 5)."""
import json
import time

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from backend import audit
from backend.api import create_app
from backend.auth import (DISPLAY, PIN_AT_SKEW_S, PIN_MAX_WRONG, SESSION_IDLE_S, SESSION_TTL_S, LoginThrottle, login, logout,
                          pin_event, resolve_token, session_ref)
from backend.live import LiveEngine
from backend.store import SqliteStore
from backend.topology import topology_from_dict
from conftest import new_store
from test_api_rbac import bearer, make_admin
from test_auth import make_user


def age(store, token, seconds):
    """Pretends the session's last call was `seconds` ago."""
    s = store.session_get(token)
    store.session_touch(token, s["last_used_at"] - seconds)


def actions(store, **kw):
    return [e["action"] for e in reversed(audit.entries(store, **kw)) if e["action"] != "chain.check"]


def of_session(store, token):
    return audit.entries(store, object_type="session", object_id=session_ref(token))


# ---------------- idle limit ----------------
def test_a_session_ends_after_the_idle_time_and_the_end_is_audited():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, expires = login(store, "asha", "s3cret!")
    assert expires - time.time() <= SESSION_TTL_S == 12 * 3600      # PART11-DESIGN 3: 12 h at most
    age(store, token, SESSION_IDLE_S - 5)
    assert resolve_token(store, token) is not None          # a call inside the limit is use: the clock starts again
    age(store, token, SESSION_IDLE_S + 1)
    assert resolve_token(store, token) is None
    assert store.session_get(token) is None                 # ended for good, not only refused
    assert resolve_token(store, token) is None
    assert actions(store) == ["sign-in", "sign-out"]        # audited once
    assert of_session(store, token)[0]["detail"]["note"].startswith("idle")
    assert resolve_token(store, token, idle_s=None) is None # ended sessions do not come back without the limit


def test_reading_without_touch_does_not_count_as_use():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    age(store, token, 600)
    before = store.session_get(token)["last_used_at"]
    assert resolve_token(store, token, touch=False) is not None
    assert store.session_get(token)["last_used_at"] == before


def test_signing_out_after_the_idle_time_is_audited_as_an_idle_end():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    age(store, token, SESSION_IDLE_S + 60)
    logout(store, token)
    assert of_session(store, token)[0]["detail"]["note"].startswith("idle")


def test_a_view_only_wall_screen_keeps_only_the_session_limit():
    store = new_store()
    make_user(store, "wall", password="s3cret!", roles=[(DISPLAY, "org", None)])
    make_user(store, "mixed", password="s3cret!", roles=[(DISPLAY, "org", None), ("plant_manager", "org", None)])
    _, wall, _ = login(store, "wall", "s3cret!")
    _, mixed, _ = login(store, "mixed", "s3cret!")
    for t in (wall, mixed):
        age(store, t, 3 * 3600)
    assert resolve_token(store, wall) is not None
    assert resolve_token(store, mixed) is None              # any right to change something: idle limit applies


def test_api_rejects_an_idle_token_and_tells_the_idle_time_at_sign_in(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = TestClient(create_app(eng, session_idle_s=60))
    make_admin(c)
    r = c.post("/auth/login", json={"username": "root", "password": "rootpass1", "device": "Pixel 8 / a1b2\x00"})
    assert r.json()["idle_sign_out_s"] == 60
    token = r.json()["token"]
    assert c.get("/auth/me", headers=bearer(token)).status_code == 200
    age(eng.store, token, 61)
    assert c.get("/auth/me", headers=bearer(token)).status_code == 401
    assert audit.entries(eng.store, action="sign-in", limit=1)[0]["client"] == "Pixel 8 / a1b2"   # control characters dropped


def test_idle_limit_can_be_turned_off_per_server(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = TestClient(create_app(eng, session_idle_s=0))
    token = make_admin(c)
    age(eng.store, token, 86400)
    assert c.get("/auth/me", headers=bearer(token)).status_code == 200


def test_an_open_stream_closes_when_its_session_ends(base_cfg, monkeypatch):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = TestClient(create_app(eng))
    token = make_admin(c)
    line = next(iter(eng.topo.lines))
    now = [1000.0]
    monkeypatch.setattr("backend.api.time.monotonic", lambda: now[0])
    with c.websocket_connect(f"/stream/lines/{line}?interval=0.2&token={token}") as ws:
        ws.receive_json()
        c.post("/auth/logout", headers=bearer(token))
        now[0] += 6
        with pytest.raises(WebSocketDisconnect) as closed:
            for _ in range(5):
                ws.receive_json()
    assert closed.value.code == 4401


# ---------------- PIN events ----------------
def test_wrong_pins_never_keep_a_session_alive():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    age(store, token, 600)
    before = store.session_get(token)["last_used_at"]
    assert pin_event(store, token, "pin_wrong") == {"session": "open", "wrong_in_a_row": 1}
    assert store.session_get(token)["last_used_at"] == before
    assert pin_event(store, token, "pin_unlock") == {"session": "open", "wrong_in_a_row": 0}
    assert store.session_get(token)["last_used_at"] > before


def test_five_wrong_pins_in_a_row_end_the_session_on_the_server():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    for _ in range(PIN_MAX_WRONG - 1):
        pin_event(store, token, "pin_wrong")
    pin_event(store, token, "pin_unlock")                   # a right PIN starts the count again
    for i in range(1, PIN_MAX_WRONG):
        assert pin_event(store, token, "pin_wrong")["wrong_in_a_row"] == i
    assert pin_event(store, token, "pin_wrong") == {"session": "ended", "wrong_in_a_row": PIN_MAX_WRONG}
    assert resolve_token(store, token) is None
    assert pin_event(store, token, "pin_wrong") is None     # nothing more is accepted on that token
    assert [e["action"] for e in of_session(store, token)].count("lock") == 1


def test_a_lockout_reported_by_the_phone_ends_the_session_once():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    assert pin_event(store, token, "pin_lockout", device="Pixel 8 / a1b2")["session"] == "ended"
    assert pin_event(store, token, "pin_lockout") is None
    e = audit.entries(store, action="lock")
    assert len(e) == 1 and e[0]["outcome"] == "refused" and e[0]["client"] == "Pixel 8 / a1b2"
    assert e[0]["user"] == {"kind": "person", "id": e[0]["user"]["id"], "username": "asha", "full_name": "Asha Rao", "roles": []}


def test_pin_event_after_the_idle_time_is_refused():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    age(store, token, SESSION_IDLE_S + 1)
    assert pin_event(store, token, "pin_unlock") is None
    assert actions(store) == ["sign-in", "sign-out"]


def test_pin_events_endpoint_needs_the_sign_ins_own_token(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = TestClient(create_app(eng, api_key="plant-key"))
    c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"}, headers={"X-API-Key": "plant-key"})
    token = c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]
    assert c.post("/auth/pin-events", json={"event": "pin_wrong"}, headers={"X-API-Key": "plant-key"}).status_code == 401
    assert c.post("/auth/pin-events", json={"event": "pin_wrong"}, headers=bearer("not-a-token")).status_code == 401
    assert c.post("/auth/pin-events", json={"event": "pin_guess"}, headers=bearer(token)).status_code == 422
    r = c.post("/auth/pin-events", json={"event": "pin_wrong", "device": "Pixel 8 / a1b2"}, headers=bearer(token))
    assert r.status_code == 200 and r.json() == {"ok": True, "session": "open", "wrong_in_a_row": 1}
    r = c.post("/auth/pin-events", json={"event": "pin_lockout"}, headers=bearer(token))
    assert r.json()["session"] == "ended"
    assert c.get("/auth/me", headers=bearer(token)).status_code == 401


def test_the_phone_time_of_a_pin_event_is_kept_next_to_the_server_time():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    typed = time.time() - 120                               # queued on the phone for 2 minutes
    assert pin_event(store, token, "pin_wrong", at=typed)["wrong_in_a_row"] == 1
    e = of_session(store, token)[0]
    assert e["detail"]["phone_at"] == audit._iso(typed)[0]
    assert e["ts"] != e["detail"]["phone_at"]               # the entry keeps the server's own time of arrival
    pin_event(store, token, "pin_unlock")                   # no phone time: recorded as before
    assert "phone_at" not in of_session(store, token)[0]["detail"]


def test_a_phone_time_in_the_future_or_before_the_sign_in_is_refused_and_not_recorded():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    began = store.session_get(token)["created_at"]
    for at in (time.time() + PIN_AT_SKEW_S + 60, began - PIN_AT_SKEW_S - 60):
        with pytest.raises(ValueError):
            pin_event(store, token, "pin_lockout", at=at)
    assert actions(store) == ["sign-in"]                    # nothing recorded, and the session is still open
    assert resolve_token(store, token) is not None
    assert pin_event(store, token, "pin_unlock", at=time.time() + PIN_AT_SKEW_S - 30) is not None   # a drifted clock
    assert pin_event(store, token, "pin_unlock", at=began - PIN_AT_SKEW_S + 30) is not None


def test_pin_events_endpoint_takes_an_optional_phone_time(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = TestClient(create_app(eng, api_key="plant-key"))
    c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"}, headers={"X-API-Key": "plant-key"})
    token = c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]
    def post(**kw):
        return c.post("/auth/pin-events", json={"event": "pin_wrong", **kw}, headers=bearer(token))
    typed = int(time.time()) - 60
    iso = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(typed))
    assert post(at=iso + "+05:30").status_code == 422      # an offset is read as one: 5.5 h earlier, before the sign-in
    assert post(at=iso).status_code == 422                 # no time zone
    assert post(at="2020-01-01T00:00:00Z").status_code == 422
    assert post(at="later").status_code == 422
    assert post(at=iso + "Z").json() == {"ok": True, "session": "open", "wrong_in_a_row": 1}
    assert post().json()["wrong_in_a_row"] == 2
    got = [e["detail"].get("phone_at") for e in audit.entries(eng.store, action="unlock.failed")]
    assert got == [None, iso + ".000Z"]


# ---------------- audit trail ----------------
def test_sign_in_events_are_audited_without_the_token_or_a_mistyped_secret(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = TestClient(create_app(eng))
    token = make_admin(c)
    assert c.post("/auth/login", json={"username": "root", "password": "wrong"}).status_code == 401
    assert c.post("/auth/login", json={"username": "MyPassw0rd!", "password": "x"}).status_code == 401
    for _ in range(4):                                      # 5 wrong for root from this device in all
        c.post("/auth/login", json={"username": "root", "password": "wrong"})
    for _ in range(3):                                      # refused tries: the lock is written once per window
        assert c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).status_code == 429
    c.post("/auth/logout", headers=bearer(token))
    store = eng.store
    assert actions(store) == ["sign-in"] + ["sign-in.failed"] * 6 + ["lock", "sign-out"]
    rows = audit.entries(store, limit=100)
    unknown = next(r for r in rows if r["detail"] and r["detail"].get("note") == "unknown user")
    assert unknown["user"] is None and unknown["detail"]["typed_username"] == "My…(11)"
    raw = json.dumps(rows)
    assert token not in raw and "MyPassw0rd" not in raw
    root = next(r for r in rows if r["action"] == "sign-in")
    assert root["user"]["roles"] == ["system_administrator@org"] and root["detail"]["address"] == "testclient"
    assert root["object"]["type"] == "session" and root["tz_offset"][0] in "+-" and root["ts"].endswith("Z")


def test_the_lock_is_noted_once_per_window():
    now = [1000.0]
    t = LoginThrottle(window_s=60, clock=lambda: now[0])
    assert t.first_refusal("asha", "10.0.0.5") and not t.first_refusal("asha", "10.0.0.5")
    assert t.first_refusal("asha", "10.0.0.9")
    now[0] += 61
    assert t.first_refusal("asha", "10.0.0.5")


def test_password_change_and_disable_are_audited_without_the_password(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = TestClient(create_app(eng))
    token = make_admin(c)
    uid = c.post("/admin/users", json={"username": "asha", "name": "Asha Rao", "password": "ashapass1"}, headers=bearer(token)).json()["id"]
    r = c.post("/auth/change-password", json={"old_password": "rootpass1", "new_password": "rootpass2"}, headers=bearer(token))
    assert r.status_code == 200
    e = audit.entries(eng.store, action="edit", limit=1)[0]
    assert e["changes"] == [{"path": "/password", "old": "***", "new": "***"}] and "rootpass" not in json.dumps(e)
    token = c.post("/auth/login", json={"username": "root", "password": "rootpass2"}).json()["token"]
    assert c.post(f"/admin/users/{uid}/deactivate", json={"reason": "left the company"}, headers=bearer(token)).status_code == 200
    d = audit.entries(eng.store, action="disable", limit=1)[0]
    assert d["object"]["name"] == "asha" and d["user"]["username"] == "root" and d["changes"] == [{"path": "/active", "old": True, "new": False}]
    assert d["reason"] == "left the company"


def test_only_admin_reads_the_trail_and_the_chain_is_checked_at_start(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    app = create_app(eng)
    assert app.state.audit_check == {"ok": True, "entries": 0, "first_bad_seq": None}
    c = TestClient(app)
    admin = make_admin(c)
    c.post("/admin/users", json={"username": "wall", "name": "Wall screen", "password": "wallpass1",
                                 "roles": [{"role": DISPLAY, "scope_level": "org"}]}, headers=bearer(admin))
    wall = c.post("/auth/login", json={"username": "wall", "password": "wallpass1"}).json()["token"]
    assert c.get("/admin/audit", headers=bearer(wall)).status_code == 403
    assert c.get("/admin/audit/check", headers=bearer(wall)).status_code == 403
    r = c.get("/admin/audit", params={"action": "sign-in", "limit": 1}, headers=bearer(admin))
    assert r.status_code == 200 and r.json()[0]["user"]["username"] == "wall"
    got = c.get("/admin/audit/check", headers=bearer(admin)).json()
    assert got["ok"] and got["entries"] == 3                # chain.check at start, two sign-ins
    assert audit.entries(eng.store, limit=1)[0]["action"] == "chain.check"
    assert create_app(eng).state.audit_check["ok"]          # a restart checks again


def test_the_chain_links_every_entry_and_starts_from_its_source():
    store = new_store()
    first = audit.write(store, "sign-in")
    second = audit.write(store, "sign-out")
    assert first["seq"] == 1 and second["seq"] == 2
    assert first["prev_hash"] == audit.sha256(audit.canonical(audit.SOURCE) + first["ts"])
    assert second["prev_hash"] == first["hash"]
    assert audit.check(store) == {"ok": True, "entries": 2, "first_bad_seq": None}


def test_the_database_refuses_to_change_or_remove_an_entry():
    store = new_store()
    for a in ("sign-in", "sign-out", "sign-in"):
        audit.write(store, a)
    for sql in ("UPDATE audit_entry SET action = 'x' WHERE seq = 2", "DELETE FROM audit_entry WHERE seq = 3"):
        try:
            store._x(sql)
            store.db.commit()
        except Exception:
            store.db.rollback()
    assert [e["action"] for e in reversed(audit.entries(store))] == ["sign-in", "sign-out", "sign-in"]
    assert audit.check(store)["ok"]


def test_the_chain_check_finds_a_changed_removed_or_rehashed_entry():
    store = SqliteStore()                                   # someone with file access drops the guards
    for a in ("sign-in", "sign-out", "sign-in", "sign-out", "sign-in"):
        audit.write(store, a)
    store.db.execute("DROP TRIGGER audit_entry_no_update")
    store.db.execute("DROP TRIGGER audit_entry_no_delete")
    body, = store.db.execute("SELECT entry FROM audit_entry WHERE seq = 2").fetchone()
    forged = body.replace("sign-out", "sign-in")
    store.db.execute("UPDATE audit_entry SET entry = ? WHERE seq = 2", (forged,))
    assert audit.check(store) == {"ok": False, "entries": 1, "first_bad_seq": 2}
    store.db.execute("UPDATE audit_entry SET entry = ?, hash = ? WHERE seq = 2", (forged, audit.sha256(forged)))
    assert audit.check(store)["first_bad_seq"] == 3        # a rehashed entry breaks the next link
    store.db.execute("UPDATE audit_entry SET entry = ?, hash = ? WHERE seq = 2", (body, audit.sha256(body)))
    assert audit.check(store)["ok"]
    store.db.execute("DELETE FROM audit_entry WHERE seq = 4")
    assert audit.check(store) == {"ok": False, "entries": 3, "first_bad_seq": 4}
