"""PL1: plant master data and devices (docs/vision-system/PLANT-PLATFORM.md section 2)."""
import time

from fastapi.testclient import TestClient

from backend.api import create_app
from backend.live import LiveEngine
from backend.master import device_for_token, issue_token, sync_site, token_hash
from backend.topology import topology_from_dict
from conftest import new_store


def setup(base_cfg):
    store = new_store()
    sync_site(store, base_cfg)
    eng = LiveEngine(store, topology_from_dict(base_cfg))
    c = TestClient(create_app(eng))
    r = c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"})
    assert r.status_code == 200, r.text
    tok = c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]
    return store, c, {"Authorization": f"Bearer {tok}"}


def test_site_json_fills_the_master_tables_and_its_gateways(base_cfg):
    store = new_store()
    n = sync_site(store, base_cfg)
    assert n == {"plants": 1, "areas": 1, "lines": 1, "gateways": 1}
    assert store.master_list("line") == [{"id": "L", "plant_id": "P", "area_id": "AR", "name": "L", "target_per_hour": 180.0, "source": "site_json"}]
    gw = store.device_get("GW")
    assert (gw["kind"], gw["line_id"], gw["token_hash"], gw["source"]) == ("production_gateway", "L", None, "site_json")


def test_syncing_again_changes_nothing_and_follows_renames(base_cfg):
    store = new_store()
    sync_site(store, base_cfg)
    base_cfg["plants"][0]["areas"][0]["lines"][0]["name"] = "Line 1"
    n = sync_site(store, base_cfg)
    assert n["gateways"] == 0
    assert store.master_get("line", "L")["name"] == "Line 1"
    assert len(store.device_list()) == 1


def test_admin_adds_a_vision_line_and_a_station_with_a_token_shown_once(base_cfg):
    store, c, h = setup(base_cfg)
    assert c.post("/admin/lines", headers=h, json={"id": "L4", "plant_id": "P", "area_id": "AR", "name": "Line 4"}).status_code == 200
    r = c.post("/admin/devices", headers=h, json={"id": "L4-S1", "kind": "vision_station", "name": "Line 4 top camera",
                                                  "plant_id": "P", "line_id": "L4", "maker": "Sundaram", "serial": "A123"})
    assert r.status_code == 200, r.text
    token = r.json()["token"]
    assert r.json()["device"]["has_token"] is True
    assert "token_hash" not in r.json()["device"]
    # only the hash is kept, and no listing ever shows the token again
    assert store.device_get("L4-S1")["token_hash"] == token_hash(token)
    listing = c.get("/admin/devices", headers=h).json()
    assert token not in str(listing) and all("token_hash" not in d for d in listing)
    assert device_for_token(store, token)["id"] == "L4-S1"


def test_a_new_token_replaces_the_old_and_disable_stops_it(base_cfg):
    store, c, h = setup(base_cfg)
    first = c.post("/admin/devices", headers=h, json={"id": "S1", "kind": "vision_station", "name": "S1", "plant_id": "P", "line_id": "L"}).json()["token"]
    second = c.post("/admin/devices/S1/token", headers=h).json()["token"]
    assert device_for_token(store, first) is None
    assert device_for_token(store, second)["id"] == "S1"
    assert c.post("/admin/devices/S1/disable", headers=h).json()["enabled"] is False
    assert device_for_token(store, second) is None
    c.post("/admin/devices/S1/enable", headers=h)
    assert device_for_token(store, second)["id"] == "S1"


def test_wrong_input_is_refused(base_cfg):
    _, c, h = setup(base_cfg)
    def dev(**kw):
        return c.post("/admin/devices", headers=h, json={"id": "S1", "kind": "vision_station", "name": "S1", "plant_id": "P", **kw}).status_code
    assert dev(kind="toaster") == 400
    assert dev(plant_id="nowhere") == 400
    assert dev(line_id="nowhere") == 400
    assert dev(id="bad id with spaces") == 400
    assert dev() == 200
    assert dev() == 409
    assert c.post("/admin/lines", headers=h, json={"id": "X", "plant_id": "P", "area_id": "nowhere", "name": "X"}).status_code == 400
    # Production's lines belong to the site JSON
    assert c.post("/admin/lines", headers=h, json={"id": "L", "plant_id": "P", "name": "renamed"}).status_code == 409
    assert c.post("/admin/devices/nope/token", headers=h).status_code == 404


def test_only_administrators_manage_devices(base_cfg):
    _, c, h = setup(base_cfg)
    c.post("/admin/users", headers=h, json={"username": "pmuser", "name": "PM", "password": "pmpass123",
                                            "roles": [{"role": "plant_manager", "scope_level": "plant", "scope_id": "P"}]})
    pm = {"Authorization": "Bearer " + c.post("/auth/login", json={"username": "pmuser", "password": "pmpass123"}).json()["token"]}
    assert c.get("/admin/devices", headers=pm).status_code == 403
    assert c.post("/admin/devices", headers=pm, json={"id": "S1", "kind": "vision_station", "name": "S1", "plant_id": "P"}).status_code == 403
    assert c.get("/admin/master", headers=pm).status_code == 403
    assert c.get("/admin/master", headers=h).json()["plants"][0]["id"] == "P"


def test_unknown_or_empty_tokens_find_nothing(base_cfg):
    store = new_store()
    sync_site(store, base_cfg)
    assert device_for_token(store, None) is None
    assert device_for_token(store, "") is None
    assert device_for_token(store, "not-a-token") is None
    store.device_create({"id": "S1", "kind": "vision_station", "name": "S1", "plant_id": "P", "enabled": 1, "created_at": int(time.time())})
    assert device_for_token(store, issue_token(store, "S1"))["id"] == "S1"
