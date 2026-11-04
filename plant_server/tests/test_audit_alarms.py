"""Repeated-lock alert, audit chain alarm, the anchored chain head, the session sweep and the reason for disabling a
user (Part 11 PT11-05, PT11-09, PT11-22, PT11-23; follow-ups of PT11-08)."""
import json
import os
import time

from fastapi.testclient import TestClient

from backend import audit
from backend.api import create_app
from backend.auth import DISPLAY, SESSION_IDLE_S, login, resolve_token, session_ref, sweep_sessions
from backend.live import LiveEngine
from backend.store import SqliteStore
from backend.topology import topology_from_dict
from conftest import new_store
from test_api_rbac import bearer, make_admin
from test_auth import make_user
from test_session_idle_pin_audit import age


def served(base_cfg, store=None, **kw):
    eng = LiveEngine(store or new_store(), topology_from_dict(base_cfg), alerts=True)
    app = create_app(eng, **kw)
    return eng, app, TestClient(app)


def security_alerts(eng, rule_type):
    return [a for a in eng.alerts.list() if a["type"] == rule_type]


def drop_guards(store):
    """Someone with database access removes the append-only guards."""
    if isinstance(store, SqliteStore):
        for op in ("update", "delete"):
            store.db.execute(f"DROP TRIGGER audit_entry_no_{op}")
    else:
        for op in ("update", "delete"):
            store._x(f"DROP RULE audit_entry_no_{op} ON audit_entry")
    store.db.commit()


def remove_from(store, seq):
    store._x("DELETE FROM audit_entry WHERE seq >= ?", (seq,))
    store.db.commit()


# ---------------- repeated locks (PT11-05) ----------------
def test_three_locks_of_one_user_in_an_hour_alert_admin_once(base_cfg):
    eng, _, c = served(base_cfg)
    admin = make_admin(c)
    c.post("/admin/users", json={"username": "asha", "name": "Asha Rao", "password": "ashapass1"}, headers=bearer(admin))
    for _ in range(5):                                       # lock 1: wrong passwords from this device
        assert c.post("/auth/login", json={"username": "asha", "password": "nope"}).status_code == 401
    assert c.post("/auth/login", json={"username": "asha", "password": "nope"}).status_code == 429
    c.app.state.login_throttle._fails.clear()               # the lock has run out
    assert security_alerts(eng, "security_repeated_locks") == []
    for n in (2, 3, 4):                                      # locks 2..4: the phone reports a PIN lockout
        token = c.post("/auth/login", json={"username": "asha", "password": "ashapass1"}).json()["token"]
        assert c.post("/auth/pin-events", json={"event": "pin_lockout"}, headers=bearer(token)).json()["session"] == "ended"
        got = security_alerts(eng, "security_repeated_locks")
        assert len(got) == (0 if n == 2 else 1)              # two locks are no alert; the third raises one, the fourth keeps it
    a = got[0]
    assert a["severity"] == "high" and a["recipients"] == ["system_administrator"] and a["state"] == "OPEN"
    assert "Asha Rao (asha)" in a["title"] and len(a["evidence"]["locks"]) == 4
    assert c.get("/alerts", headers=bearer(admin)).json()[0]["type"] == "security_repeated_locks"


def test_locks_outside_the_window_and_unknown_names_raise_nothing(base_cfg):
    eng, _, c = served(base_cfg)
    uid = make_user(eng.store, password="s3cret!")
    u = eng.store.user_by_id(uid)
    for _ in range(3):
        audit.write(eng.store, "lock", user=audit.user_of(eng.store, u), user_id=uid, outcome="refused")
    assert eng.alerts.repeated_locks(u, now=int(time.time()) + 3601) is None   # an hour later the three no longer count
    assert eng.alerts.repeated_locks(None) is None                              # a name that matches no account
    assert eng.alerts.repeated_locks(u)["state"] == "OPEN"


def test_a_resolved_lock_alert_opens_again_on_the_next_lock(base_cfg):
    eng, _, _ = served(base_cfg)
    uid = make_user(eng.store, password="s3cret!")
    u = eng.store.user_by_id(uid)
    for _ in range(3):
        audit.write(eng.store, "lock", user_id=uid, outcome="refused")
    first = eng.alerts.repeated_locks(u)
    eng.alerts.resolve(first["id"], "root", "talked to Asha, she forgot her password")
    audit.write(eng.store, "lock", user_id=uid, outcome="refused")
    again = eng.alerts.repeated_locks(u)
    assert again["id"] != first["id"] and again["state"] == "OPEN"


# ---------------- chain alarm (PT11-23) and anchor (PT11-09 c) ----------------
def test_a_broken_chain_raises_an_alarm_at_start_and_on_demand(base_cfg):
    store = SqliteStore()
    for a in ("sign-in", "sign-out", "sign-in"):
        audit.write(store, a)
    drop_guards(store)
    store.db.execute("UPDATE audit_entry SET action = 'x', entry = replace(entry, 'sign-out', 'sign-in') WHERE seq = 2")
    eng, app, c = served(base_cfg, store)
    assert app.state.audit_check["first_bad_seq"] == 2
    got = security_alerts(eng, "audit_chain_broken")
    assert len(got) == 1 and got[0]["severity"] == "high" and got[0]["recipients"] == ["system_administrator", "it_ot"]
    assert "seq 2" in got[0]["detail"]
    admin = make_admin(c)
    assert c.get("/admin/audit/check", headers=bearer(admin)).json()["ok"] is False
    assert len(security_alerts(eng, "audit_chain_broken")) == 1     # the same alert, refreshed
    eng.alerts.resolve(got[0]["id"], "root", "copy of the database taken; investigating")
    c.get("/admin/audit/check", headers=bearer(admin))
    assert [a["state"] for a in security_alerts(eng, "audit_chain_broken")] == ["OPEN", "RESOLVED"]


def test_a_good_chain_raises_nothing(base_cfg):
    eng, app, c = served(base_cfg)
    admin = make_admin(c)
    assert app.state.audit_check["ok"] and c.get("/admin/audit/check", headers=bearer(admin)).json()["ok"]
    assert security_alerts(eng, "audit_chain_broken") == []


def test_the_anchor_finds_the_newest_entries_removed(tmp_path):
    store = new_store()                                      # SQLite, or PostgreSQL with SPI_TEST_PG_DSN
    path = str(tmp_path / "head")
    audit.use_anchor(store, path)
    for a in ("sign-in", "sign-out", "sign-in", "sign-out", "sign-in"):
        audit.write(store, a)
    assert json.loads(open(path).read())["seq"] == 5 and oct(os.stat(path).st_mode & 0o777) == "0o600"
    assert audit.verify(store)["ok"]
    drop_guards(store)
    remove_from(store, 4)
    assert audit.check(store)["ok"]                          # what is left still links: the chain alone cannot tell
    got = audit.verify(store)
    assert not got["ok"] and got["anchor"]["seq"] == 5 and "removed" in got["anchor"]["note"]
    for a in ("sign-out", "sign-in", "sign-out"):            # new entries 4..6 never cover it up
        audit.write(store, a)
    assert json.loads(open(path).read())["seq"] == 5         # frozen at the break
    assert "rewritten" in audit.verify(store)["anchor"]["note"]
    audit.use_anchor(store, path)                            # a restart finds the same break
    assert not audit.verify(store)["ok"]


def test_the_anchor_starts_at_the_head_never_moves_back_and_refuses_an_unreadable_file(tmp_path):
    store = new_store()
    audit.write(store, "sign-in")
    audit.write(store, "sign-out")
    a = audit.use_anchor(store, str(tmp_path / "head"))
    assert a.check(store) == {"ok": True, "seq": 2, "note": "anchor started at seq 2 (no anchor file before)"}
    a.moved(1, "0" * 64)
    assert a.head["seq"] == 2
    (tmp_path / "bad").write_text("{not json")
    bad = audit.use_anchor(store, str(tmp_path / "bad"))
    assert not bad.check(store)["ok"] and "unreadable" in bad.check(store)["note"]
    audit.write(store, "sign-in")
    assert (tmp_path / "bad").read_text() == "{not json"     # never silently started afresh


def test_entries_removed_while_running_are_found_by_the_sweep(base_cfg, tmp_path):
    store = SqliteStore()
    eng, app, c = served(base_cfg, store, audit_anchor=str(tmp_path / "head"))
    make_admin(c)
    app.state.sweep()
    assert security_alerts(eng, "audit_chain_broken") == []
    drop_guards(store)
    head = store.audit_head()[0]
    remove_from(store, head - 1)
    app.state.sweep()
    app.state.sweep()                                        # found once: one failed check entry, one alert
    failed = [e for e in audit.entries(store, action="chain.check") if e["outcome"] == "failed"]
    assert len(failed) == 1 and failed[0]["detail"]["anchor"]["seq"] == head
    assert len(security_alerts(eng, "audit_chain_broken")) == 1


def test_the_server_anchors_its_head_from_the_start(base_cfg, tmp_path):
    path = str(tmp_path / "head")
    eng, app, c = served(base_cfg, audit_anchor=path)
    assert app.state.audit_check["anchor"]["ok"]
    make_admin(c)
    assert json.loads(open(path).read())["seq"] == eng.store.audit_head()[0]


# ---------------- session sweep (PT11-09 d) ----------------
def test_the_sweep_notes_why_and_ends_each_session_once():
    store = new_store()
    make_user(store, "idle", password="s3cret!")
    make_user(store, "old", password="s3cret!")
    make_user(store, "wall", password="s3cret!", roles=[(DISPLAY, "org", None)])
    idle, old, wall = (login(store, n, "s3cret!")[1] for n in ("idle", "old", "wall"))
    age(store, idle, SESSION_IDLE_S + 1)
    age(store, wall, SESSION_IDLE_S + 1)
    now = int(time.time())
    assert sweep_sessions(store, now=now) == 1               # only the idle one; the wall screen has no idle limit
    later = max(store.session_get(t)["expires_at"] for t in (old, wall)) + 1
    assert sweep_sessions(store, now=later) == 2             # past the 12 h: the old one and the wall screen
    assert sweep_sessions(store, now=later) == 0
    notes = {e["user"]["username"]: e["detail"]["note"] for e in audit.entries(store, action="sign-out")}
    assert notes["idle"].startswith("idle") and notes["idle"].endswith("ended by the session sweep")
    assert notes["old"].startswith("expired: 12 h") and notes["wall"].startswith("expired")
    assert {e["object"]["id"] for e in audit.entries(store, action="sign-out")} == {session_ref(t) for t in (idle, old, wall)}


def test_the_sweep_leaves_a_session_used_meanwhile():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    seen = store.session_get(token)["last_used_at"]
    store.session_touch(token, seen + 1)                     # used between the sweep's read and its delete
    assert store.session_delete(token, last_used_at=seen) is False and store.session_get(token) is not None


def test_an_expired_session_used_again_is_audited_too():
    store = new_store()
    make_user(store, password="s3cret!")
    _, token, _ = login(store, "asha", "s3cret!")
    store._x("UPDATE user_session SET expires_at = ? WHERE token = ?", (int(time.time()) - 1, token))
    store.db.commit()
    assert resolve_token(store, token) is None
    assert audit.entries(store, action="sign-out", limit=1)[0]["detail"]["note"].startswith("expired")


def test_the_running_server_sweeps_by_itself(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    with TestClient(create_app(eng, session_sweep_s=0.05)) as c:
        token = make_admin(c)
        age(eng.store, token, SESSION_IDLE_S + 1)
        deadline = time.time() + 5
        while eng.store.session_get(token) is not None and time.time() < deadline:
            time.sleep(0.05)
        assert eng.store.session_get(token) is None
    assert audit.entries(eng.store, action="sign-out", limit=1)[0]["detail"]["note"].endswith("ended by the session sweep")


# ---------------- disable reason (PT11-22) ----------------
def test_disabling_a_user_needs_a_reason(base_cfg):
    eng, _, c = served(base_cfg)
    admin = make_admin(c)
    uid = c.post("/admin/users", json={"username": "asha", "name": "Asha Rao", "password": "ashapass1"}, headers=bearer(admin)).json()["id"]
    assert c.post(f"/admin/users/{uid}/deactivate", headers=bearer(admin)).status_code == 422
    assert c.post(f"/admin/users/{uid}/deactivate", json={"reason": "  \x00 "}, headers=bearer(admin)).status_code == 422
    assert c.post(f"/admin/users/{uid}/deactivate", json={"reason": " left the company\x07 "}, headers=bearer(admin)).status_code == 200
    assert audit.entries(eng.store, action="disable", limit=1)[0]["reason"] == "left the company"
