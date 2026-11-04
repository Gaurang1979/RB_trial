import time

import pytest

from backend.auth import (ADMIN, CORPORATE, DISPLAY, IT_OT, MAINTENANCE_MANAGER, PLANT_MANAGER, Principal,
                          hash_password, login, logout, new_token, normalize_role, resolve_token,
                          users_with_role, verify_password)
from conftest import new_store


def make_user(store, username="asha", name="Asha Rao", password="correct horse", roles=()):
    now = int(time.time())
    uid = store.user_create(username, name, hash_password(password), None, None, now)
    for role, level, scope in roles:
        store.role_add(uid, role, level, scope, now)
    return uid


def test_password_hash_never_stores_the_plaintext_and_is_salted():
    h1, h2 = hash_password("hunter2"), hash_password("hunter2")
    assert "hunter2" not in h1 and h1 != h2                     # random salt: two hashes of the same password differ
    assert verify_password("hunter2", h1) and not verify_password("hunter3", h1)


def test_verify_password_rejects_garbage_without_raising():
    assert verify_password("x", "not-a-valid-hash") is False
    assert verify_password("x", "") is False


def test_login_logout_and_session_expiry():
    store = new_store()
    make_user(store, password="s3cret!")
    assert login(store, "asha", "wrong") is None
    assert login(store, "nobody", "s3cret!") is None
    p, token, expires = login(store, "asha", "s3cret!")
    assert p.username == "asha" and p.kind == "user" and expires > time.time()
    assert resolve_token(store, token).username == "asha"
    logout(store, token)
    assert resolve_token(store, token) is None
    assert resolve_token(store, "not-a-real-token") is None


def test_inactive_user_cannot_login_or_use_an_existing_session():
    store = new_store()
    uid = make_user(store, password="s3cret!")
    p, token, _ = login(store, "asha", "s3cret!")
    store.user_set_active(uid, False)
    assert resolve_token(store, token) is None
    assert login(store, "asha", "s3cret!") is None


def test_expired_session_is_rejected():
    store = new_store()
    uid = make_user(store)
    token = new_token()
    store.session_create(token, uid, int(time.time()) - 100, int(time.time()) - 1)   # already expired
    assert resolve_token(store, token) is None


def test_role_scope_validation():
    with pytest.raises(ValueError, match="unknown role"):
        normalize_role("ceo", "org", None)
    with pytest.raises(ValueError, match="scope_level"):
        normalize_role(PLANT_MANAGER, "galaxy", "P1")
    with pytest.raises(ValueError, match="needs a scope_id"):
        normalize_role(PLANT_MANAGER, "plant", None)
    with pytest.raises(ValueError, match="must not have a scope_id"):
        normalize_role(ADMIN, "org", "P1")
    assert normalize_role(PLANT_MANAGER, "plant", "P1") == (PLANT_MANAGER, "plant", "P1")


def test_view_and_act_are_scoped_to_plant_and_line():
    p = Principal(kind="user", assignments=[{"id": 1, "role": PLANT_MANAGER, "scope_level": "plant", "scope_id": "PLANT-VAD"}])
    assert p.can_view(plant_id="PLANT-VAD") and p.can_act(plant_id="PLANT-VAD")
    assert not p.can_view(plant_id="PLANT-PUN") and not p.can_act(plant_id="PLANT-PUN")
    line_scoped = Principal(kind="user", assignments=[{"id": 2, "role": MAINTENANCE_MANAGER, "scope_level": "line", "scope_id": "LINE-01"}])
    assert line_scoped.can_view(plant_id="P", line_id="LINE-01") and not line_scoped.can_view(plant_id="P", line_id="LINE-02")


def test_corporate_and_display_roles_can_view_but_not_act():
    corp = Principal(kind="user", assignments=[{"id": 1, "role": CORPORATE, "scope_level": "org", "scope_id": None}])
    disp = Principal(kind="user", assignments=[{"id": 2, "role": DISPLAY, "scope_level": "plant", "scope_id": "P1"}])
    assert corp.can_view(plant_id="anything") and not corp.can_act(plant_id="anything")
    assert disp.can_view(plant_id="P1") and not disp.can_act(plant_id="P1")


def test_admin_role_grants_everything_regardless_of_declared_scope():
    p = Principal(kind="user", assignments=[{"id": 1, "role": ADMIN, "scope_level": "org", "scope_id": None}])
    assert p.is_admin() and p.can_view(plant_id="X") and p.can_act(line_id="Y")


def test_service_and_anon_principals_are_fully_open():
    for kind in ("service", "anon"):
        p = Principal(kind=kind)
        assert p.can_view(plant_id="X") and p.can_act(line_id="Y") and p.visible_plant_ids({"A", "B"}) == {"A", "B"}


def test_visible_plant_and_line_ids_reflect_assignments():
    class FakeLine:
        def __init__(self, plant_id): self.plant_id = plant_id
    class FakeTopo:
        lines = {"L1": FakeLine("P1"), "L2": FakeLine("P1"), "L3": FakeLine("P2")}
    p_plant = Principal(kind="user", assignments=[{"id": 1, "role": PLANT_MANAGER, "scope_level": "plant", "scope_id": "P1"}])
    assert p_plant.visible_plant_ids({"P1", "P2"}) == {"P1"}
    assert p_plant.visible_line_ids(FakeTopo()) == {"L1", "L2"}
    p_line = Principal(kind="user", assignments=[{"id": 2, "role": IT_OT, "scope_level": "line", "scope_id": "L3"}])
    assert p_line.visible_line_ids(FakeTopo()) == {"L3"}
    p_org = Principal(kind="user", assignments=[{"id": 3, "role": CORPORATE, "scope_level": "org", "scope_id": None}])
    assert p_org.visible_line_ids(FakeTopo()) == {"L1", "L2", "L3"}


def test_actor_name_prefers_the_authenticated_username():
    user = Principal(kind="user", username="asha")
    assert user.actor_name("someone else typed this") == "asha"
    assert Principal(kind="service").actor_name("gate-guard") == "gate-guard"
    assert Principal(kind="anon").actor_name("anyone") == "anyone"


def test_users_with_role_matches_scope_and_skips_inactive():
    store = new_store()
    make_user(store, "asha", "Asha", "pw", [(PLANT_MANAGER, "plant", "PLANT-VAD")])
    make_user(store, "ravi", "Ravi", "pw", [(PLANT_MANAGER, "plant", "PLANT-PUN")])
    other_uid = make_user(store, "dead", "Dead Account", "pw", [(PLANT_MANAGER, "plant", "PLANT-VAD")])
    store.user_set_active(other_uid, False)
    got = {u["username"] for u in users_with_role(store, PLANT_MANAGER, "PLANT-VAD", None)}
    assert got == {"asha"}
    assert users_with_role(store, PLANT_MANAGER, "PLANT-OTHER", None) == []


def test_role_assignments_can_be_added_and_removed():
    store = new_store()
    uid = make_user(store)
    aid = store.role_add(uid, MAINTENANCE_MANAGER, "line", "L1", int(time.time()))
    assert [r["role"] for r in store.role_list(uid)] == [MAINTENANCE_MANAGER]
    store.role_remove(aid)
    assert store.role_list(uid) == []


def test_user_count_supports_first_admin_bootstrap():
    store = new_store()
    assert store.user_count() == 0
    make_user(store)
    assert store.user_count() == 1
