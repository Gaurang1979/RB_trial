"""Reasons for activate and role changes, and the Admin's signed re-anchor of the audit chain (Part 11 PT11-09a;
PT11-21, PT11-22, PT11-23, PT11-31)."""
import json

from backend import audit
from backend.store import SqliteStore
from test_api_rbac import bearer, login, make_admin
from test_audit_alarms import drop_guards, remove_from, security_alerts, served


def new_user(c, admin, username="asha"):
    r = c.post("/admin/users", json={"username": username, "name": "Asha Rao", "password": "ashapass1"}, headers=bearer(admin))
    assert r.status_code == 200, r.text
    return r.json()["id"]


# ---------------- reasons for user changes (PT11-09a a) ----------------
def test_activate_needs_a_reason_and_is_audited(base_cfg):
    eng, _, c = served(base_cfg)
    admin = make_admin(c)
    uid = new_user(c, admin)
    c.post(f"/admin/users/{uid}/deactivate", json={"reason": "on leave"}, headers=bearer(admin))
    assert c.post(f"/admin/users/{uid}/activate", headers=bearer(admin)).status_code == 422
    assert c.post(f"/admin/users/{uid}/activate", json={"reason": " \x00 "}, headers=bearer(admin)).status_code == 422
    assert eng.store.user_by_id(uid)["active"] in (0, False)
    r = c.post(f"/admin/users/{uid}/activate", json={"reason": " back from leave\x07 "}, headers=bearer(admin))
    assert r.status_code == 200 and r.json()["active"] is True
    e = audit.entries(eng.store, action="edit", limit=1)[0]
    assert e["reason"] == "back from leave" and e["changes"] == [{"path": "/active", "old": False, "new": True}]
    assert e["object"]["id"] == str(uid) and e["user"]["username"] == "root"


def test_role_add_and_remove_need_a_reason_and_are_audited(base_cfg):
    eng, _, c = served(base_cfg)
    admin = make_admin(c)
    uid = new_user(c, admin)
    role = {"role": "factory_display", "scope_level": "org", "scope_id": None}
    assert c.post(f"/admin/users/{uid}/roles", json=role, headers=bearer(admin)).status_code == 422
    assert c.post(f"/admin/users/{uid}/roles", json={**role, "reason": "ab"}, headers=bearer(admin)).status_code == 422
    assert eng.store.role_list(uid) == []
    got = c.post(f"/admin/users/{uid}/roles", json={**role, "reason": "needs the wall view"}, headers=bearer(admin)).json()
    aid = got["roles"][0]["id"]
    e = audit.entries(eng.store, action="edit", limit=1)[0]
    assert e["reason"] == "needs the wall view" and e["changes"] == [{"path": f"/roles/{aid}", "new": "factory_display@org"}]
    url = f"/admin/users/{uid}/roles/{aid}"
    assert c.delete(url, headers=bearer(admin)).status_code == 422
    assert len(eng.store.role_list(uid)) == 1
    assert c.request("DELETE", url, json={"reason": "left the shift"}, headers=bearer(admin)).json()["roles"] == []
    e = audit.entries(eng.store, action="edit", limit=1)[0]
    assert e["reason"] == "left the shift" and e["changes"] == [{"path": f"/roles/{aid}", "old": "factory_display@org"}]
    assert audit.verify(eng.store)["ok"]


# ---------------- re-anchor (PT11-09a b) ----------------
def broken_by_removal(base_cfg, tmp_path):
    """A running server whose newest entries were removed: the anchor finds the break and stays fixed."""
    store = SqliteStore()
    eng, app, c = served(base_cfg, store, audit_anchor=str(tmp_path / "head"), session_sweep_s=0)
    admin = make_admin(c)
    for a in ("sign-in", "sign-out", "sign-in"):
        audit.write(store, a)
    head = store.audit_head()[0]
    drop_guards(store)
    remove_from(store, head - 1)
    got = c.get("/admin/audit/check", headers=bearer(admin)).json()
    assert not got["ok"] and "removed" in got["anchor"]["note"]
    return eng, app, c, admin, head


def reanchor(c, token, reason="copy of the database kept; IT found a restore by mistake", password="rootpass1"):
    return c.post("/admin/audit/reanchor", json={"reason": reason, "password": password}, headers=bearer(token))


def test_reanchor_after_newest_entries_were_removed(base_cfg, tmp_path):
    eng, _, c, admin, head = broken_by_removal(base_cfg, tmp_path)
    store = eng.store
    r = reanchor(c, admin)
    assert r.status_code == 200, r.text
    got = r.json()
    entry, check = got["reanchor"], got["check"]
    assert check["ok"] and check["anchor"]["ok"]
    assert entry["action"] == "chain.reanchor" and entry["reason"].startswith("copy of the database")
    assert entry["detail"]["anchor"]["seq"] == head and entry["detail"]["breaks"] == []
    sign = audit.entries(store, action="sign", limit=1)[0]
    rec = sign["detail"]["signature"]
    assert sign["signature_id"] == rec["id"] == entry["signature_id"] and rec["meaning"] == "responsibility"
    assert rec["user"]["username"] == "root" and rec["method"] == "password" and sign["seq"] < entry["seq"]
    assert json.loads(open(tmp_path / "head").read())["seq"] == entry["seq"] + 1   # moved on to the chain.check after it
    # the break stays in the history: the failed checks are still there and the anchor moves on from here
    assert any(e["outcome"] == "failed" for e in audit.entries(store, action="chain.check", limit=50))
    audit.write(store, "sign-in")
    assert audit.verify(store)["ok"] and json.loads(open(tmp_path / "head").read())["seq"] == store.audit_head()[0]
    assert security_alerts(eng, "audit_chain_broken")[0]["state"] == "OPEN"   # a person resolves the alert
    assert reanchor(c, admin).status_code == 409                               # nothing left to re-anchor
    # a new removal after the re-anchor is a new break, found as before
    remove_from(store, store.audit_head()[0])
    assert not audit.verify(store)["ok"]


def test_reanchor_after_an_entry_was_rewritten_keeps_the_break_listed(base_cfg, tmp_path):
    store = SqliteStore()
    eng, app, c = served(base_cfg, store, session_sweep_s=0)
    admin = make_admin(c)
    for a in ("sign-in", "sign-out", "sign-in"):
        audit.write(store, a)
    drop_guards(store)
    store.db.execute("UPDATE audit_entry SET entry = replace(entry, 'sign-out', 'sign-in') WHERE action = 'sign-out'")
    store.db.commit()
    bad = audit.check(store)["first_bad_seq"]
    assert bad is not None
    got = reanchor(c, admin).json()
    assert got["reanchor"]["detail"]["breaks"][0]["seq"] == bad
    assert got["check"]["ok"] and got["check"]["investigated"] == [{"seq": bad, "reanchor_seq": got["reanchor"]["seq"]}]
    assert audit.check(store)["ok"] is False                 # without the re-anchors the break is still there
    later = audit.verify(store)
    assert later["ok"] and later["investigated"][0]["seq"] == bad and later["entries"] == store.audit_head()[0]
    # a second change at the same place is a new break, not covered by the re-anchor
    store.db.execute(f"UPDATE audit_entry SET entry = replace(entry, '\"outcome\":\"ok\"', '\"outcome\":\"no\"') WHERE seq = {bad}")
    store.db.commit()
    assert audit.verify(store)["first_bad_seq"] == bad
    # and a new break elsewhere is found too
    store.db.execute(f"UPDATE audit_entry SET entry = entry || ' ' WHERE seq = {bad + 1}")
    store.db.commit()
    assert not audit.verify(store)["ok"]


def test_reanchor_refusals(base_cfg, tmp_path):
    eng, _, c, admin, _ = broken_by_removal(base_cfg, tmp_path)
    store = eng.store
    uid = new_user(c, admin)
    c.post(f"/admin/users/{uid}/roles", json={"role": "factory_display", "scope_level": "org", "reason": "wall view"}, headers=bearer(admin))
    asha = login(c, "asha", "ashapass1")
    assert reanchor(c, asha, password="ashapass1").status_code == 403                   # not an Admin
    assert audit.entries(store, action="refused", limit=1)[0]["object"]["type"] == "audit_chain"
    assert c.post("/admin/audit/reanchor", json={"reason": "investigated", "password": "x"}).status_code == 403   # no sign-in
    assert reanchor(c, admin, reason="  ").status_code == 422                           # no reason
    assert c.post("/admin/audit/reanchor", json={"reason": "investigated"}, headers=bearer(admin)).status_code == 422
    assert reanchor(c, admin, password="wrong").status_code == 403                      # wrong password
    f = audit.entries(store, action="sign.failed", limit=1)[0]
    assert f["outcome"] == "failed" and f["user"]["username"] == "root"
    assert audit.entries(store, action="chain.reanchor", limit=5) == []
    assert not audit.verify(store)["ok"]                                                # nothing changed
    for _ in range(4):                                                                  # wrong passwords count towards the lock
        reanchor(c, admin, password="wrong")
    assert reanchor(c, admin).status_code == 429
    assert audit.entries(store, action="lock", limit=1)[0]["user"]["username"] == "root"


def test_a_service_key_cannot_sign_the_reanchor(base_cfg, tmp_path):
    store = SqliteStore()
    audit.write(store, "sign-in")
    audit.write(store, "sign-out")
    drop_guards(store)
    remove_from(store, 2)
    audit.write(store, "sign-out")                            # the chain links again, but seq 2 is not the old one
    store.db.execute("UPDATE audit_entry SET entry = entry || ' ' WHERE seq = 2")
    store.db.commit()
    eng, _, c = served(base_cfg, store, api_key="k3y", session_sweep_s=0)
    assert c.post("/admin/audit/reanchor", json={"reason": "investigated", "password": "x"},
                  headers={"X-API-Key": "k3y"}).status_code == 403
    assert "service key" in audit.entries(store, action="refused", limit=1)[0]["detail"]["note"]
