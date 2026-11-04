"""Changeover verification (docs/vision-system/CHANGEOVER-VERIFY.md): the setting, the request kept by a heartbeat, the answer, the
decisions and the web page."""
import hashlib
import json
import time

import pytest
from fastapi.testclient import TestClient

from backend import auth
from backend.api import create_app
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.topology import topology_from_dict
from backend.vision import changeover
from conftest import new_store

NOW = int(time.time() * 1000)
STATIONS = {"S1": ("P", "L"), "S2": ("P", "L"), "S3": ("P", "L2")}


def bearer(t):
    return {"Authorization": f"Bearer {t}"}


def record(job="Carton 24", version=7, variant="", operator="ravi", ask=1, batch=None, changed_at=NOW, digest=None):
    batch = [{"key": "lot", "label": "Lot", "value": "L2611"}] if batch is None else batch
    c = {"job": job, "job_version": version, "variant": variant, "batch": batch, "operator": operator, "ask": ask, "changed_at": changed_at}
    if digest is not None:
        c["job_digest"] = digest
    c["code"] = changeover.code_of(c)
    return c


@pytest.fixture
def env(base_cfg, tmp_path):
    store = new_store()
    sync_site(store, base_cfg)
    store.master_upsert("line", {"id": "L2", "plant_id": "P", "area_id": None, "name": "L2", "target_per_hour": 0, "source": "admin"})
    store.master_upsert("plant", {"id": "P2", "name": "P2", "source": "admin"})
    store.master_upsert("line", {"id": "L3", "plant_id": "P2", "area_id": None, "name": "L3", "target_per_hour": 0, "source": "admin"})
    for d, (p, ln) in STATIONS.items():
        store.device_create({"id": d, "kind": "vision_station", "name": d, "plant_id": p, "line_id": ln, "enabled": 1, "created_at": 0})
    tok = {d: issue_token(store, d) for d in STATIONS}

    class E:
        pass
    e = E()
    e.store, e.tok = store, tok
    e.c = TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg)), vision_image_dir=str(tmp_path / "images")))

    def person(name, role, level="org", sid=None):
        uid = store.user_create(name, name.title(), "x", None, "test", 0)
        store.role_add(uid, role, level, sid, 0)
        t = auth.new_token()
        store.session_create(t, uid, int(time.time()), int(time.time()) + 3600)
        return bearer(t)
    e.person = person

    def hb(station="S1", co=None, raw=False, **extra):
        p, ln = STATIONS[station]
        body = {"kind": "heartbeat", "schema": 1, "plant": p, "line": ln, "station": station, "ts": NOW, "seq": 0, "agent_version": "1", **extra}
        if co is not None:
            body["changeover"] = co
        r = e.c.post("/vision/ingest/v1/heartbeat", content=json.dumps(body), headers=bearer(tok[station]))
        return r if raw else (r.raise_for_status(), r.json())[1]
    e.hb = hb

    def rows(sql, args=()):
        with store._lock:
            return store._x(sql, args).fetchall()
    e.rows = rows
    e.admin = person("adm", auth.ADMIN)
    e.qm = person("priya", auth.QUALITY_MANAGER, "line", "L")

    def setting(plant, line, value, who=None):
        return e.c.put("/admin/vision/settings", json={"key": "changeover_verify", "plant": plant, "line": line, "value": value}, headers=who or e.admin)
    e.setting = setting
    return e


def decide(env, who, n=1, decision="accept", note=""):
    return env.c.post(f"/vision/changeover/{n}/decide", json={"decision": decision, "note": note}, headers=who)


# ---------------- the code ----------------
def test_the_canonical_form_and_code_follow_the_document():
    c = {"job": "A", "job_version": 7, "variant": "", "batch": [{"key": "lot", "label": "Lot", "value": "L1"}]}
    assert changeover.canonical("A", 7, "", c["batch"]) == b"1:A1:70:3:lot3:Lot2:L1"
    assert changeover.code_of(c) == hashlib.sha256(b"1:A1:70:3:lot3:Lot2:L1").hexdigest()
    # lengths are UTF-8 bytes, not characters
    assert changeover.canonical("café", 0, "", []) == "5:café".encode() + b"1:00:"
    # moving text between fields changes the bytes (the length prefixes keep fields apart)
    assert changeover.canonical("ab", 1, "", []) != changeover.canonical("a", 1, "b", [])


DIGEST_A, DIGEST_B = "a" * 64, "b" * 64


def test_the_job_digest_ends_the_canonical_form_only_when_there_is_one():
    b = [{"key": "lot", "label": "Lot", "value": "L1"}]
    assert changeover.canonical("A", 7, "", b, "") == changeover.canonical("A", 7, "", b)          # an old station keeps its code
    assert changeover.canonical("A", 7, "", b, DIGEST_A) == b"1:A1:70:3:lot3:Lot2:L1" + b"64:" + DIGEST_A.encode()
    assert changeover.code_of({"job": "A", "job_version": 7, "batch": b, "job_digest": DIGEST_A}) != changeover.code_of({"job": "A", "job_version": 7, "batch": b})
    assert changeover.code_of({"job": "A", "job_version": 7, "batch": b, "job_digest": DIGEST_A}) != changeover.code_of({"job": "A", "job_version": 7, "batch": b, "job_digest": DIGEST_B})


def test_a_job_copied_by_hand_with_the_same_name_and_version_is_asked_again(env):
    first = record(digest=DIGEST_A)
    env.hb("S1", first)
    decide(env, env.qm)
    assert env.hb("S1", first)["changeover"]["state"] == "accepted"
    copy = record(digest=DIGEST_B, ask=2)           # same job, version, variant and batch: only the file differs
    assert copy["code"] != first["code"]
    assert env.hb("S1", copy)["changeover"]["state"] == "pending"
    two = env.c.get("/vision/changeover/2", headers=env.qm).json()
    assert two["record"]["job_digest"] == DIGEST_B
    assert two["changes"] == [{"key": "job_digest", "label": "Job file digest", "old": DIGEST_A, "new": DIGEST_B}]


def test_records_without_a_digest_read_as_before_and_show_no_digest_change(env):
    env.hb("S1", record())
    decide(env, env.qm)
    env.hb("S1", record(version=8))
    one, two = (env.c.get(f"/vision/changeover/{i}", headers=env.qm).json() for i in (1, 2))
    assert "job_digest" not in one["record"] and "job_digest" not in two["record"]
    assert [c["key"] for c in two["changes"]] == ["job_version"]


@pytest.mark.parametrize("bad", ["A" * 64, "a" * 63, 5, None])
def test_a_bad_job_digest_is_left_out_of_the_heartbeat(env, bad):
    co = {**record(), "job_digest": bad}
    r = env.hb("S1", co, raw=True)
    assert r.status_code == 200 and "changeover" not in r.json()


# ---------------- the setting ----------------
def test_the_check_is_on_by_default_a_plant_can_turn_it_off_and_a_line_overrides_the_plant(env):
    assert env.hb("S1", record())["changeover"]["state"] == "pending"
    assert env.setting("P", None, False).status_code == 200
    r = env.hb("S1", record(ask=2))["changeover"]
    assert r["required"] is False and r["state"] == "not_needed"
    assert env.hb("S3", record())["changeover"]["state"] == "not_needed"      # another line of the plant
    assert env.setting("P", "L2", True).status_code == 200                    # the line's own setting wins over its plant's
    assert env.hb("S3", record())["changeover"]["state"] == "pending"
    assert env.hb("S1", record(ask=3))["changeover"]["state"] == "not_needed"
    assert env.setting("P", "L2", False).status_code == 200 and env.setting("P", None, True).status_code == 200
    assert env.hb("S3", record())["changeover"]["state"] == "not_needed"      # line off beats plant on
    assert env.hb("S1", record(ask=3))["changeover"]["state"] == "pending"


def test_off_keeps_no_request_and_every_setting_change_is_kept(env):
    env.setting("P", None, False)
    a = env.hb("S1", record())["changeover"]
    assert a == {"required": False, "code": record()["code"], "ask": 1, "state": "not_needed", "by": "", "at": 0, "note": ""}
    assert env.rows("SELECT COUNT(*) FROM vision_changeover") == [(0,)]
    env.setting("P", None, True)
    env.setting("P", None, False)
    assert env.rows("SELECT scope_level, scope_id, value, updated_by FROM vision_setting ORDER BY id") == [
        ("plant", "P", "false", "adm"), ("plant", "P", "true", "adm"), ("plant", "P", "false", "adm")]


def test_the_settings_endpoints_are_for_admins_and_check_their_input(env):
    assert env.setting("P", None, False, env.qm).status_code == 403
    assert env.setting("P", None, False, env.person("plant-boss", auth.PLANT_MANAGER, "plant", "P")).status_code == 403
    assert env.c.get("/admin/vision/settings", headers=env.qm).status_code == 403
    assert env.c.get("/admin/vision/settings", headers=env.person("viewer", auth.DISPLAY, "line", "L")).status_code == 403
    body = lambda **kw: {"key": "changeover_verify", "plant": "P", "line": None, "value": True, **kw}      # noqa: E731
    put = lambda b: env.c.put("/admin/vision/settings", json=b, headers=env.admin)      # noqa: E731
    assert put(body(value="yes")).status_code == 400 and put(body(value=1)).status_code == 400 and put(body(value=None)).status_code == 400
    assert put(body(key="other")).status_code == 400
    assert put(body(plant="nope")).status_code == 404
    assert put(body(plant="P2", line="L")).status_code == 400          # L is in plant P
    assert put(body(plant="P", line="nope")).status_code == 400
    assert env.rows("SELECT COUNT(*) FROM vision_setting") == [(0,)]
    assert put(body(value=False)).status_code == 200 and put(body(plant="P", line="L", value=True)).status_code == 200
    got = env.c.get("/admin/vision/settings", headers=env.admin).json()["items"]
    assert {(s["plant"], s["line"], s["value"]) for s in got} == {("P", "L", True), ("P", None, False)}
    assert all(s["key"] == "changeover_verify" and s["updated_by"] == "adm" and s["updated_at"] > 0 for s in got)


# ---------------- the heartbeat makes the request ----------------
def test_a_heartbeat_makes_a_pending_request_once_per_code_and_ask(env):
    co = record()
    a = env.hb("S1", co)["changeover"]
    assert a == {"required": True, "code": co["code"], "ask": 1, "state": "pending", "by": "", "at": 0, "note": ""}
    env.hb("S1", co)
    env.hb("S1", co)
    assert env.rows("SELECT station, plant, line, code, ask, operator, state FROM vision_changeover") == [("S1", "P", "L", co["code"], 1, "ravi", "pending")]
    [(rec,)] = env.rows("SELECT record FROM vision_changeover")
    assert json.loads(rec) == {"job": "Carton 24", "job_version": 7, "variant": "", "batch": [{"key": "lot", "label": "Lot", "value": "L2611"}]}
    env.hb("S1", {**co, "ask": 2, "code": co["code"]})        # the same code asked again is its own request
    assert env.rows("SELECT ask, state FROM vision_changeover ORDER BY id") == [(1, "superseded"), (2, "pending")]
    assert "changeover" not in env.hb("S1")                      # a heartbeat without a record has no answer for it


def test_a_newer_request_supersedes_the_older_pending_ones_of_the_same_station_only(env):
    a, b = record(batch=[{"key": "lot", "label": "Lot", "value": "A"}]), record(batch=[{"key": "lot", "label": "Lot", "value": "B"}])
    env.hb("S1", a)
    env.hb("S2", a)
    env.hb("S1", b)
    assert env.rows("SELECT station, state FROM vision_changeover ORDER BY id") == [("S1", "superseded"), ("S2", "pending"), ("S1", "pending")]
    assert decide(env, env.qm, 1).status_code == 409                     # superseded: cannot be decided
    assert decide(env, env.qm, 2).status_code == 200
    # the station goes back to the first values: that request is the current one again
    assert env.hb("S1", a)["changeover"]["state"] == "pending"
    assert env.rows("SELECT state FROM vision_changeover WHERE station = 'S1' ORDER BY id") == [("pending",), ("superseded",)]


def test_accept_by_an_approver_is_the_answer_and_a_refusal_stays_refused_for_its_code(env):
    co = record()
    env.hb("S1", co)
    r = decide(env, env.qm, 1)
    assert r.status_code == 200 and r.json()["state"] == "accepted" and r.json()["decided_by"] == "priya"
    a = env.hb("S1", co)["changeover"]
    assert (a["state"], a["by"], a["note"], a["required"]) == ("accepted", "Priya", "", True) and a["at"] > 0
    assert [(d["name"], d["decision"]) for d in r.json()["decisions"]] == [("Priya", "accept")]
    bad = record(batch=[{"key": "lot", "label": "Lot", "value": "L9"}])
    env.hb("S1", bad)
    assert decide(env, env.qm, 2, "refuse", "lot is wrong").status_code == 200
    a = env.hb("S1", bad)["changeover"]
    assert (a["state"], a["note"], a["by"]) == ("refused", "lot is wrong", "Priya")
    again = env.hb("S1", {**bad, "ask": 2})["changeover"]                 # Ask again: a new request, pending
    assert again["state"] == "pending" and again["ask"] == 2
    assert env.hb("S1", bad)["changeover"]["state"] == "refused"          # ask 1 is still refused


def test_a_refusal_needs_a_note_and_a_decision_is_validated(env):
    env.hb("S1", record())
    assert decide(env, env.qm, 1, "refuse").status_code == 400
    assert decide(env, env.qm, 1, "refuse", "   ").status_code == 400
    assert decide(env, env.qm, 1, "maybe").status_code == 400
    assert env.c.post("/vision/changeover/1/decide", json={"decision": "refuse", "note": "x" * 501}, headers=env.qm).status_code == 422
    assert env.rows("SELECT COUNT(*) FROM vision_changeover_decision") == [(0,)]
    assert env.rows("SELECT state FROM vision_changeover") == [("pending",)]


def test_the_operator_cannot_accept_their_own_changeover(env):
    env.hb("S1", record(operator="priya"))
    r = decide(env, env.qm)
    assert r.status_code == 403 and "second person" in r.json()["detail"]
    assert decide(env, env.admin).status_code == 200                      # another admin may
    env.hb("S1", record(operator="PRIYA", ask=2))                         # the name is compared without regard to case
    assert decide(env, env.qm, 2).status_code == 403
    env.hb("S1", record(operator="", ask=3))                               # nobody signed in at the station: no clash
    assert decide(env, env.qm, 3).status_code == 200
    assert env.rows("SELECT COUNT(*) FROM vision_changeover_decision") == [(2,)]


def test_a_service_key_and_anyone_without_a_person_cannot_decide(base_cfg, tmp_path):
    store = new_store()
    sync_site(store, base_cfg)
    store.device_create({"id": "S1", "kind": "vision_station", "name": "S1", "plant_id": "P", "line_id": "L", "enabled": 1, "created_at": 0})
    tok = issue_token(store, "S1")
    mk = lambda key: TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg)), api_key=key, vision_image_dir=str(tmp_path)))      # noqa: E731
    c = mk("k")
    body = {"kind": "heartbeat", "schema": 1, "plant": "P", "line": "L", "station": "S1", "ts": NOW, "seq": 0, "changeover": record()}
    assert c.post("/vision/ingest/v1/heartbeat", content=json.dumps(body), headers=bearer(tok)).status_code == 200
    svc = {"X-API-Key": "k"}
    assert len(c.get("/vision/changeover", headers=svc).json()["items"]) == 1
    r = c.post("/vision/changeover/1/decide", json={"decision": "accept"}, headers=svc)
    assert r.status_code == 403 and "person" in r.json()["detail"]
    assert mk(None).post("/vision/changeover/1/decide", json={"decision": "accept"}).status_code == 403      # no sign-in at all
    assert store._x("SELECT state FROM vision_changeover").fetchall() == [("pending",)]
    # The service key is on every phone: it may read the setting but never turn the check off (CV-02 needs a name).
    put = c.put("/admin/vision/settings", json={"key": "changeover_verify", "plant": "P", "line": None, "value": False}, headers=svc)
    assert put.status_code == 403
    assert c.get("/admin/vision/settings", headers=svc).status_code == 200
    assert store._x("SELECT COUNT(*) FROM vision_setting").fetchall() == [(0,)]


def test_who_may_decide(env):
    env.hb("S1", record())
    for role in (auth.DISPLAY, auth.CORPORATE, auth.MAINTENANCE_MANAGER, auth.IT_OT):
        who = env.person(f"x-{role}", role, "line", "L")
        assert decide(env, who).status_code == 403, role
    assert decide(env, env.person("corp", auth.CORPORATE)).status_code == 403
    assert decide(env, env.person("other-line", auth.QUALITY_MANAGER, "line", "L2")).status_code == 404      # cannot even see it
    assert decide(env, env.person("other-plant", auth.PLANT_MANAGER, "plant", "P2")).status_code == 404
    assert env.rows("SELECT state FROM vision_changeover") == [("pending",)]
    for i, role in enumerate((auth.PRODUCTION_MANAGER, auth.PLANT_MANAGER)):
        env.hb("S1", record(ask=i + 2))
        assert decide(env, env.person(f"ok-{role}", role, "plant", "P"), i + 2).status_code == 200


def test_a_second_decision_is_a_conflict_and_changes_nothing(env):
    env.hb("S1", record())
    assert decide(env, env.qm).status_code == 200
    assert decide(env, env.admin, 1, "refuse", "too late").status_code == 409
    assert decide(env, env.admin).status_code == 409
    assert env.rows("SELECT decision FROM vision_changeover_decision") == [("accept",)]
    assert env.rows("SELECT state, decided_by, decided_name FROM vision_changeover") == [("accepted", "priya", "Priya")]
    assert decide(env, env.admin, 99).status_code == 404


# ---------------- what the heartbeat must carry ----------------
def test_a_code_that_is_not_the_record_is_left_out_and_the_heartbeat_still_counts(env):
    # A refused heartbeat would stop the station's events too; without an answer the line stays stopped.
    co = record()
    r = env.hb("S1", {**co, "code": "0" * 64}, raw=True)
    assert r.status_code == 200 and "changeover" not in r.json()
    r = env.hb("S1", {**co, "batch": [{"key": "lot", "label": "Lot", "value": "L2612"}]}, raw=True)      # a value changed, the code did not
    assert r.status_code == 200 and "changeover" not in r.json()
    assert env.rows("SELECT COUNT(*) FROM vision_changeover") == [(0,)]
    assert env.rows("SELECT COUNT(*) FROM vision_station_status") == [(1,)]


@pytest.mark.parametrize("change", [
    {"code": "A" * 64}, {"code": "a" * 63}, {"code": 5}, {"ask": 0}, {"ask": "1"}, {"ask": True}, {"ask": 1.5}, {"job": ""}, {"job": "x" * 129}, {"job": 3},
    {"job_version": -1}, {"job_version": "7"}, {"job_version": 2**31}, {"variant": 3}, {"operator": "x" * 65}, {"operator": None}, {"changed_at": "now"},
    {"batch": "lot"}, {"batch": [{"key": "", "label": "L", "value": "v"}]}, {"batch": [{"key": "k" * 65, "label": "L", "value": "v"}]},
    {"batch": [{"key": "k", "label": "L" * 129, "value": "v"}]}, {"batch": [{"key": "k", "label": "L", "value": "v" * 257}]},
    {"batch": [{"key": "k", "label": "L"}]}, {"batch": ["lot"]}, {"batch": [{"key": "k", "label": "L", "value": "v\u0000"}]},
    {"batch": [{"key": "k%d" % i, "label": "L", "value": "v"} for i in range(33)]}, {"job": "a\ud800"}])
def test_a_bad_changeover_field_is_left_out_of_the_heartbeat(env, change):
    co = {**record(), **change}
    r = env.hb("S1", co, raw=True)
    assert r.status_code == 200 and "changeover" not in r.json(), change
    assert env.rows("SELECT COUNT(*) FROM vision_changeover") == [(0,)]


def test_the_limits_themselves_are_allowed(env):
    batch = [{"key": "k" * 64 if i == 0 else f"k{i}", "label": "L" * 128, "value": "v" * 256} for i in range(32)]
    co = record(job="j" * 128, version=0, batch=batch, operator="o" * 64)
    assert env.hb("S1", co)["changeover"]["state"] == "pending"
    assert env.hb("S1", record(batch=[], version=2**31 - 1))["changeover"]["state"] == "pending"
    assert env.hb("S1", record(job="café 相机", batch=[{"key": "k", "label": "é", "value": "\U0001F600"}]))["changeover"]["state"] == "pending"
    assert env.hb("S1", None, changeover=None)["ok"] is True       # a null changeover is none


def test_a_failure_in_the_check_never_fails_the_heartbeat(env, monkeypatch):
    def boom(*a, **k):
        raise RuntimeError("database gone")
    monkeypatch.setattr(changeover.Changeover, "on_heartbeat", boom)
    r = env.hb("S1", record())
    assert r["ok"] is True and "changeover" not in r and r["image_requests"] == []
    assert env.rows("SELECT COUNT(*) FROM vision_station_status") == [(1,)]


# ---------------- reading ----------------
def test_previous_is_the_last_accepted_record_of_the_station_and_changes_say_what_differs(env):
    first = record(batch=[{"key": "lot", "label": "Lot", "value": "L1"}, {"key": "mrp", "label": "MRP", "value": "99"}])
    env.hb("S1", first)
    one = env.c.get("/vision/changeover/1", headers=env.qm).json()
    assert one["previous"] is None and one["changes"] == [] and one["short_code"] == first["code"][:8]
    decide(env, env.qm)
    second = record(version=8, variant="red", batch=[{"key": "lot", "label": "Lot", "value": "L2"}, {"key": "mrp", "label": "MRP", "value": "99"},
                                                    {"key": "exp", "label": "Expiry", "value": "2027"}])
    env.hb("S1", second)
    two = env.c.get("/vision/changeover/2", headers=env.qm).json()
    assert two["previous"]["id"] == 1 and two["previous"]["code"] == first["code"] and two["previous"]["decided_name"] == "Priya"
    assert two["changes"] == [{"key": "job_version", "label": "Job version", "old": "7", "new": "8"}, {"key": "variant", "label": "Variant", "old": "", "new": "red"},
                              {"key": "lot", "label": "Lot", "old": "L1", "new": "L2"}, {"key": "exp", "label": "Expiry", "old": None, "new": "2027"}]
    decide(env, env.admin, 2)
    third = record(version=8, variant="red", batch=[{"key": "lot", "label": "Lot", "value": "L2"}])     # MRP and Expiry removed, job unchanged
    env.hb("S1", third)
    ch = env.c.get("/vision/changeover/3", headers=env.qm).json()["changes"]
    assert ch == [{"key": "mrp", "label": "MRP", "old": "99", "new": None}, {"key": "exp", "label": "Expiry", "old": "2027", "new": None}]
    env.hb("S2", record(job="Other"))                                       # another station has its own history
    assert env.c.get("/vision/changeover/4", headers=env.qm).json()["previous"] is None
    renamed = record(version=8, variant="red", batch=[{"key": "lot", "label": "Batch", "value": "L2"}])
    env.hb("S1", renamed)
    [c] = env.c.get("/vision/changeover/5", headers=env.qm).json()["changes"][:1]
    assert c == {"key": "lot", "label": "Batch", "old": "L2", "new": "L2", "label_old": "Lot"}


def test_the_list_filters_and_is_scoped_by_line(env):
    env.hb("S1", record(batch=[{"key": "lot", "label": "Lot", "value": "A"}]))
    env.hb("S3", record())
    env.hb("S1", record(batch=[{"key": "lot", "label": "Lot", "value": "B"}]))
    decide(env, env.admin, 2)
    g = lambda who, **q: env.c.get("/vision/changeover", params=q, headers=who).json()["items"]      # noqa: E731
    assert [x["id"] for x in g(env.admin)] == [3]                                    # pending by default, newest first
    assert [x["id"] for x in g(env.admin, state="all")] == [3, 2, 1]
    assert [x["id"] for x in g(env.admin, state="superseded")] == [1] and [x["id"] for x in g(env.admin, state="accepted")] == [2]
    assert [x["id"] for x in g(env.admin, state="all", line="L2")] == [2] and g(env.admin, state="all", plant="P2") == []
    assert [x["id"] for x in g(env.admin, state="all", limit=1)] == [3]
    assert [x["id"] for x in g(env.qm, state="all")] == [3, 1]                       # a manager of line L does not see L2
    assert g(env.person("nobody", auth.QUALITY_MANAGER, "line", "nowhere"), state="all") == []
    assert env.c.get("/vision/changeover/2", headers=env.qm).status_code == 404
    assert env.c.get("/vision/changeover", params={"state": "bogus"}, headers=env.admin).status_code == 400
    assert env.c.get("/vision/changeover", params={"limit": 201}, headers=env.admin).status_code == 422
    item = g(env.admin, state="all")[0]
    assert {"id", "station", "plant", "line", "code", "short_code", "ask", "record", "operator", "changed_at", "asked_at", "state", "decided_by",
            "decided_at", "note", "previous", "changes"} <= set(item)


def test_the_page_is_served(env):
    r = env.c.get("/vision/changeover/page")
    assert r.status_code == 200 and "Changeover check" in r.text and "/vision/changeover" in r.text and "/admin/vision/settings" in r.text


# ---------------- accepted at the line PC while the server could not be reached (owner, 2026-11-01) ----------------
def test_an_acceptance_at_the_line_is_recorded_as_that_persons_decision(env):
    la = {"by": "asha", "name": "Asha Rao", "at": NOW + 5}
    a = env.hb("S1", {**record(), "accepted_at_line": la})["changeover"]
    assert (a["state"], a["by"], a["at"]) == ("accepted", "Asha Rao", NOW + 5)
    assert env.rows("SELECT state, decided_by, note FROM vision_changeover") == [("accepted", "line:asha", changeover.LINE_NOTE)]
    assert env.rows("SELECT user_id, name, decision FROM vision_changeover_decision") == [(None, "Asha Rao", "accept_at_line")]
    # sent again until the station has the answer: recorded once
    env.hb("S1", {**record(), "accepted_at_line": la})
    assert env.rows("SELECT COUNT(*) FROM vision_changeover_decision") == [(1,)]


def test_a_refusal_given_here_first_wins_over_an_acceptance_at_the_line(env):
    env.hb("S1", record())
    assert decide(env, env.qm, 1, "refuse", "wrong lot").status_code == 200
    a = env.hb("S1", {**record(), "accepted_at_line": {"by": "asha", "name": "Asha", "at": NOW}})["changeover"]
    assert a["state"] == "refused"
    assert env.rows("SELECT decision FROM vision_changeover_decision") == [("refuse",)]


@pytest.mark.parametrize("la", [{"by": ""}, {"by": "a" * 65, "at": NOW}, {"by": "a", "at": 0}, {"by": "a", "name": "n" * 129, "at": NOW}, "asha"])
def test_a_bad_acceptance_at_the_line_leaves_the_record_out(env, la):
    r = env.hb("S1", {**record(), "accepted_at_line": la}, raw=True)
    assert r.status_code == 200 and "changeover" not in r.json()
    assert env.rows("SELECT COUNT(*) FROM vision_changeover") == [(0,)]
