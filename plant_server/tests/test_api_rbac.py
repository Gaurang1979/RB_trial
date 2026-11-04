import copy
import json
import time

import pytest
from fastapi.testclient import TestClient

from backend.alerts import DEFAULT_RULES
from backend.api import create_app
from backend.auth import ADMIN, MAINTENANCE_MANAGER, PLANT_MANAGER, hash_password
from backend.live import LiveEngine
from backend.topology import topology_from_dict
from conftest import DEMO, new_store, run, sc
from test_live import feed

DEMO_RAW = json.load(open(DEMO))
STOP = {"id": "stop", "type": "equipment_stopped", "severity": "high", "threshold_s": 60, "recipients": ["plant_manager"], "channels": ["in_app"]}


def demo_engine(duration=1500, extra_scenarios=()):
    raw = copy.deepcopy(DEMO_RAW)
    raw["scenarios"] = list(raw.get("scenarios", [])) + list(extra_scenarios)
    raw["alert_rules"] = [STOP]
    sim, ev = run(raw, duration)
    eng = LiveEngine(new_store(), topology_from_dict(raw), alerts=True)
    feed(eng, ev)
    return eng


def client(eng, key=None):
    return TestClient(create_app(eng, api_key=key))


def bearer(token):
    return {"Authorization": f"Bearer {token}"}


def make_admin(c, username="root", password="rootpass1"):
    r = c.post("/admin/users", json={"username": username, "name": "Root", "password": password})
    assert r.status_code == 200, r.text
    return login(c, username, password)


def login(c, username, password):
    r = c.post("/auth/login", json={"username": username, "password": password})
    assert r.status_code == 200, r.text
    return r.json()["token"]


# ---------------- bootstrap and login ----------------
def test_first_user_created_with_no_roles_becomes_admin(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    r = c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"})
    assert r.status_code == 200
    assert {x["role"] for x in r.json()["roles"]} == {ADMIN}


def test_second_user_cannot_be_created_without_admin(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    make_admin(c)
    r = c.post("/admin/users", json={"username": "asha", "name": "Asha", "password": "password1"})
    assert r.status_code == 403


def test_duplicate_username_is_rejected(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    token = make_admin(c)
    r = c.post("/admin/users", json={"username": "root", "name": "Other", "password": "password1"}, headers=bearer(token))
    assert r.status_code == 409


def test_login_wrong_password_and_unknown_user(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    make_admin(c, "root", "rootpass1")
    assert c.post("/auth/login", json={"username": "root", "password": "wrong"}).status_code == 401
    assert c.post("/auth/login", json={"username": "nobody", "password": "x"}).status_code == 401


def test_me_reflects_kind_and_roles(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng, key="svc")
    assert c.get("/auth/me", headers={"X-API-Key": "svc"}).json()["is_admin"] is True   # service key: full access
    token = make_admin(client(eng))
    me = c.get("/auth/me", headers=bearer(token)).json()
    assert me["username"] == "root" and me["is_admin"] is True


def test_logout_invalidates_the_token(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    token = make_admin(c)
    assert c.get("/auth/me", headers=bearer(token)).json()["username"] == "root"
    c.post("/auth/logout", headers=bearer(token))
    assert c.get("/auth/me", headers=bearer(token)).status_code == 401


def test_bad_token_is_rejected_even_with_no_api_key_configured(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)                                       # no API key: default is open ("anon")
    assert c.get("/plants").status_code == 200             # anon still works
    assert c.get("/plants", headers=bearer("not-a-real-token")).status_code == 401   # a bad token must not be ignored


def test_change_password_requires_correct_old_password_and_revokes_sessions(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    token = make_admin(c, "root", "rootpass1")
    assert c.post("/auth/change-password", json={"old_password": "wrong", "new_password": "newpassword1"}, headers=bearer(token)).status_code == 403
    r = c.post("/auth/change-password", json={"old_password": "rootpass1", "new_password": "newpassword1"}, headers=bearer(token))
    assert r.status_code == 200
    assert c.get("/auth/me", headers=bearer(token)).status_code == 401           # old session is gone
    new_token = login(c, "root", "newpassword1")
    assert c.get("/auth/me", headers=bearer(new_token)).status_code == 200


# ---------------- role management ----------------
@pytest.fixture
def admin_client(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    token = make_admin(c)
    return c, token, eng


def test_add_and_remove_role_validates_scope(admin_client, base_cfg):
    c, admin_token, eng = admin_client
    r = c.post("/admin/users", json={"username": "asha", "name": "Asha", "password": "password1", "roles": [{"role": PLANT_MANAGER, "scope_level": "plant", "scope_id": "P"}]}, headers=bearer(admin_token))
    assert r.status_code == 200
    uid = r.json()["id"]
    assert any(x["role"] == PLANT_MANAGER for x in r.json()["roles"])
    bad = c.post(f"/admin/users/{uid}/roles", json={"role": PLANT_MANAGER, "scope_level": "plant", "scope_id": "NOPE", "reason": "covers NOPE"}, headers=bearer(admin_token))
    assert bad.status_code == 400
    added = c.post(f"/admin/users/{uid}/roles", json={"role": MAINTENANCE_MANAGER, "scope_level": "line", "scope_id": "L", "reason": "covers line L"}, headers=bearer(admin_token))
    assert added.status_code == 200 and len(added.json()["roles"]) == 2
    aid = next(x["id"] for x in added.json()["roles"] if x["role"] == MAINTENANCE_MANAGER)
    removed = c.request("DELETE", f"/admin/users/{uid}/roles/{aid}", json={"reason": "moved to stores"}, headers=bearer(admin_token))
    assert removed.status_code == 200 and len(removed.json()["roles"]) == 1
    assert c.request("DELETE", f"/admin/users/{uid}/roles/999999", json={"reason": "moved to stores"}, headers=bearer(admin_token)).status_code == 404


def test_deactivate_blocks_login_and_cannot_target_self(admin_client):
    c, admin_token, eng = admin_client
    r = c.post("/admin/users", json={"username": "asha", "name": "Asha", "password": "password1"}, headers=bearer(admin_token))
    uid = r.json()["id"]
    admin_id = c.get("/auth/me", headers=bearer(admin_token)).json()["user_id"]
    assert c.post(f"/admin/users/{admin_id}/deactivate", json={"reason": "left the company"}, headers=bearer(admin_token)).status_code == 400
    assert c.post(f"/admin/users/{uid}/deactivate", json={"reason": "left the company"}, headers=bearer(admin_token)).status_code == 200
    assert c.post("/auth/login", json={"username": "asha", "password": "password1"}).status_code == 401
    assert c.post(f"/admin/users/{uid}/activate", json={"reason": "back from leave"}, headers=bearer(admin_token)).status_code == 200
    assert c.post("/auth/login", json={"username": "asha", "password": "password1"}).status_code == 200


def test_admin_endpoints_need_an_admin_not_just_any_login(admin_client):
    c, admin_token, eng = admin_client
    r = c.post("/admin/users", json={"username": "asha", "name": "Asha", "password": "password1"}, headers=bearer(admin_token))
    asha_token = login(c, "asha", "password1")
    assert c.get("/admin/users", headers=bearer(asha_token)).status_code == 403
    assert c.post("/admin/users", json={"username": "someone", "name": "x", "password": "password1"}, headers=bearer(asha_token)).status_code == 403


# ---------------- RBAC scoping over real demo data ----------------
@pytest.fixture
def rbac_demo():
    # duration chosen so both faults (300-1200s) are OPEN (past the 60s threshold, before the 60s auto-clear) when we look
    eng = demo_engine(duration=800, extra_scenarios=[sc("fault", 300, 900, equipment_id="CNC-01"), sc("fault", 300, 900, equipment_id="PRESS-01")])
    c = client(eng)
    admin_token = make_admin(c)
    c.post("/admin/users", json={"username": "vad_pm", "name": "Vadodara PM", "password": "password1",
                                 "roles": [{"role": PLANT_MANAGER, "scope_level": "plant", "scope_id": "PLANT-VAD"}]}, headers=bearer(admin_token))
    vad_token = login(c, "vad_pm", "password1")
    return eng, c, admin_token, vad_token


def test_scoped_user_only_sees_their_plant(rbac_demo):
    eng, c, admin_token, vad_token = rbac_demo
    r = c.get("/plants", headers=bearer(vad_token))
    assert {p["id"] for p in r.json()} == {"PLANT-VAD"}
    admin_r = c.get("/plants", headers=bearer(admin_token))
    assert {p["id"] for p in admin_r.json()} == {"PLANT-VAD", "PLANT-PUN"}


def test_scoped_user_denied_other_plants_lines_and_equipment(rbac_demo):
    eng, c, admin_token, vad_token = rbac_demo
    assert c.get("/plants/PLANT-PUN/lines", headers=bearer(vad_token)).status_code == 403
    assert c.get("/plants/PLANT-VAD/lines", headers=bearer(vad_token)).status_code == 200
    assert c.get("/lines/LINE-PUN-01/status", headers=bearer(vad_token)).status_code == 403
    assert c.get("/lines/LINE-VAD-01/status", headers=bearer(vad_token)).status_code == 200
    assert c.get("/equipment/CNC-01/status", headers=bearer(vad_token)).status_code == 403     # Pune equipment
    assert c.get("/equipment/PRESS-01/status", headers=bearer(vad_token)).status_code == 200   # own plant


def test_scoped_user_alert_list_is_filtered_and_detail_is_403_outside_scope(rbac_demo):
    eng, c, admin_token, vad_token = rbac_demo
    all_alerts = c.get("/alerts?state=open", headers=bearer(admin_token)).json()
    pun_alert = next(a for a in all_alerts if a["line_id"] == "LINE-PUN-01")
    scoped = c.get("/alerts?state=open", headers=bearer(vad_token)).json()
    assert all(a["line_id"] != "LINE-PUN-01" for a in scoped)
    assert c.get(f"/alerts/{pun_alert['id']}", headers=bearer(vad_token)).status_code == 403
    assert c.get(f"/alerts/{pun_alert['id']}", headers=bearer(admin_token)).status_code == 200
    assert c.get("/alerts?line_id=LINE-PUN-01", headers=bearer(vad_token)).status_code == 403


def test_scoped_user_can_acknowledge_only_within_scope_and_audit_uses_their_username(rbac_demo):
    eng, c, admin_token, vad_token = rbac_demo
    all_alerts = c.get("/alerts?state=open", headers=bearer(admin_token)).json()
    pun_alert = next(a for a in all_alerts if a["line_id"] == "LINE-PUN-01")
    vad_alert = next(a for a in all_alerts if a["line_id"] == "LINE-VAD-01")
    assert c.post(f"/alerts/{pun_alert['id']}/acknowledge", json={"user": "someone else"}, headers=bearer(vad_token)).status_code == 403
    ok = c.post(f"/alerts/{vad_alert['id']}/acknowledge", json={"user": "someone else"}, headers=bearer(vad_token))
    assert ok.status_code == 200
    assert ok.json()["acked_by"] == "vad_pm"          # the body's 'user' is ignored in favour of the authenticated identity


def test_display_role_can_view_but_not_acknowledge():
    from backend.auth import DISPLAY
    eng = demo_engine()
    c = client(eng)
    admin_token = make_admin(c)
    c.post("/admin/users", json={"username": "kiosk", "name": "Line Display", "password": "password1",
                                 "roles": [{"role": DISPLAY, "scope_level": "line", "scope_id": "LINE-VAD-01"}]}, headers=bearer(admin_token))
    kiosk_token = login(c, "kiosk", "password1")
    assert c.get("/lines/LINE-VAD-01/status", headers=bearer(kiosk_token)).status_code == 200
    a = next((x for x in c.get("/alerts?line_id=LINE-VAD-01", headers=bearer(kiosk_token)).json()), None)
    if a:
        assert c.post(f"/alerts/{a['id']}/acknowledge", json={}, headers=bearer(kiosk_token)).status_code == 403


def test_corporate_role_sees_everything_but_cannot_act():
    from backend.auth import CORPORATE
    eng = demo_engine(extra_scenarios=[sc("fault", 300, 900, equipment_id="PRESS-01")])
    c = client(eng)
    admin_token = make_admin(c)
    c.post("/admin/users", json={"username": "corp", "name": "Corp Exec", "password": "password1",
                                 "roles": [{"role": CORPORATE, "scope_level": "org", "scope_id": None}]}, headers=bearer(admin_token))
    corp_token = login(c, "corp", "password1")
    assert {p["id"] for p in c.get("/plants", headers=bearer(corp_token)).json()} == {"PLANT-VAD", "PLANT-PUN"}
    al = c.get("/alerts", headers=bearer(corp_token)).json()
    assert al
    assert c.post(f"/alerts/{al[0]['id']}/acknowledge", json={"user": "x"}, headers=bearer(corp_token)).status_code == 403


# ---------------- webpush endpoints ----------------
def test_webpush_public_key_absent_by_default_and_present_when_configured(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    assert client(eng).get("/webpush/public-key").status_code == 404
    app = create_app(eng, vapid_public_key="fake-key-b64")
    assert TestClient(app).get("/webpush/public-key").json() == {"key": "fake-key-b64"}


def test_webpush_subscribe_requires_login(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    body = {"endpoint": "https://push.example/abc", "keys": {"p256dh": "x", "auth": "y"}}
    assert c.post("/webpush/subscribe", json=body).status_code == 401
    token = make_admin(c)
    ok = c.post("/webpush/subscribe", json=body, headers=bearer(token))
    assert ok.status_code == 200
    assert [s["endpoint_tail"] for s in c.get("/webpush/subscriptions", headers=bearer(token)).json()] == [body["endpoint"][-12:]]
    assert c.post("/webpush/unsubscribe", json={"endpoint": body["endpoint"]}, headers=bearer(token)).status_code == 200
    assert c.get("/webpush/subscriptions", headers=bearer(token)).json() == []
    assert c.post("/webpush/unsubscribe", json={"endpoint": "https://gone"}, headers=bearer(token)).status_code == 404


def test_webpush_subscribe_rejects_malformed_keys(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    c = client(eng)
    token = make_admin(c)
    bad = c.post("/webpush/subscribe", json={"endpoint": "https://push.example/x", "keys": {"p256dh": "only"}}, headers=bearer(token))
    assert bad.status_code == 400
