"""MQTT ingest checks each production gateway's device token (PL1 follow-up; docs/architecture/EDGE.md "Gateway token").
No broker needed: messages go straight into MqttIngest's worker path (authorise + ingest). test_mqtt.py covers the broker."""
import logging
from types import SimpleNamespace

from fastapi.testclient import TestClient

from backend.api import create_app
from backend.live import LiveEngine
from backend.master import issue_token, sync_site
from backend.mqtt_ingest import MqttIngest
from backend.topology import topology_from_dict
from conftest import new_store, run
from spi_common.mqtt import TOKEN_PROPERTY, message_token, token_properties


def setup(base_cfg, seconds=120):
    store = new_store()
    sync_site(store, base_cfg)                    # gateway GW of line L, plant P, without a token
    eng = LiveEngine(store, topology_from_dict(base_cfg))
    _, ev = run(base_cfg, seconds)
    return eng, MqttIngest(eng), ev


def deliver(ing, token, events, topic="spi/v1/P/L/GW/events"):
    """What MqttIngest's worker does with one batch."""
    ok = ing.authorise([(token, topic, e) for e in events])
    if ok:
        ing.engine.ingest(ok)
    return ok


def test_good_token_is_stored(base_cfg):
    eng, ing, ev = setup(base_cfg)
    token = issue_token(eng.store, "GW")
    deliver(ing, token, ev)
    assert eng.store.counts() == {"raw_events": len(ev), "rejected": 0}
    assert ing.refused == 0


def test_missing_wrong_and_unissued_tokens_are_refused_and_never_stored(base_cfg, caplog):
    eng, ing, ev = setup(base_cfg)
    caplog.set_level(logging.WARNING, logger="backend.mqtt_ingest")
    deliver(ing, None, ev)                         # no token (also: the site JSON's gateway has none issued yet)
    deliver(ing, "not-a-real-token", ev)
    issue_token(eng.store, "GW")
    deliver(ing, "", ev)
    deliver(ing, "x" * 10000, ev[:1])                # too long to be a token: refused without a lookup
    assert eng.store.counts() == {"raw_events": 0, "rejected": 0}   # refused messages are not even quarantined
    assert ing.refused == 3 * len(ev) + 1 and ing.refused_reasons == {"no valid gateway token": 3 * len(ev) + 1}
    assert len(caplog.records) == 1                # throttled: one line per reason per minute
    assert "not-a-real-token" not in caplog.text and "no valid gateway token" in caplog.text


def test_revoked_and_replaced_tokens_stop_at_once(base_cfg):
    eng, ing, ev = setup(base_cfg)
    old = issue_token(eng.store, "GW")
    deliver(ing, old, ev[:10])
    new = issue_token(eng.store, "GW")             # a new token replaces the old one
    deliver(ing, old, ev[10:20])
    assert eng.store.counts()["raw_events"] == 10
    eng.store.device_update("GW", enabled=0)       # disabled device: its token is revoked
    eng.store.commit()
    deliver(ing, new, ev[10:20])
    assert eng.store.counts()["raw_events"] == 10
    eng.store.device_update("GW", enabled=1)
    eng.store.commit()
    deliver(ing, new, ev[10:20])
    assert eng.store.counts()["raw_events"] == 20 and ing.refused == 20


def test_a_token_writes_only_for_its_own_gateway_plant_and_line(base_cfg):
    eng, ing, ev = setup(base_cfg)
    token = issue_token(eng.store, "GW")
    e = ev[0]
    for field, value in (("gateway_id", "GW-OTHER"), ("plant_id", "P2"), ("line_id", "L2")):
        assert deliver(ing, token, [{**e, field: value}]) == []
        assert ing.refused_reasons[f"{field} is not the token's gateway's"] == 1
    assert eng.store.counts() == {"raw_events": 0, "rejected": 0}


def test_a_gateway_cannot_write_for_another_gateways_equipment_sensors_or_queues(base_cfg):
    line = base_cfg["plants"][0]["areas"][0]["lines"][0]
    b = next(e for e in line["equipment"] if e.get("sensors"))
    other = {**b, "id": "X", "name": "X", "sensors": [{**b["sensors"][0], "id": "X.T"}]}
    base_cfg["plants"][0]["areas"][0]["lines"].append({**line, "id": "L2", "name": "L2", "gateway_id": "GW2", "queues": [], "equipment": [other]})
    eng, ing, ev = setup(base_cfg)
    token = issue_token(eng.store, "GW")
    e = next(x for x in ev if x["equipment_id"])
    q = next(x for x in ev if x["type"] == "queue")
    s = next(x for x in ev if x["type"] == "sensor")
    assert deliver(ing, token, [e, q, s]) == [e, q, s]            # its own equipment, queue and sensor
    deliver(ing, token, [{**e, "equipment_id": "X"}, {**e, "equipment_id": "NOT-IN-SITE"},
                         {**q, "payload": {**q["payload"], "queue_id": "NOPE"}}, {**s, "payload": {**s["payload"], "sensor_id": "X.T"}}])
    assert ing.refused_reasons == {"equipment is not the token's gateway's": 2, "queue is not the token's gateway's": 1,
                                  "sensor is not the token's gateway's": 1}
    assert eng.store.counts()["raw_events"] == 3


def test_a_production_gateway_needs_a_line(base_cfg):
    eng, ing, ev = setup(base_cfg)
    eng.store.device_update("GW", line_id=None)
    eng.store.commit()
    deliver(ing, issue_token(eng.store, "GW"), ev[:1])
    assert ing.refused_reasons == {"gateway has no plant and line in the device registry": 1}


def test_other_device_kinds_cannot_publish_production_data(base_cfg):
    eng, ing, ev = setup(base_cfg)
    eng.store.device_create({"id": "CAM-1", "kind": "vision_station", "name": "cam", "plant_id": "P", "line_id": "L", "enabled": 1,
                             "created_at": 0, "created_by": "t", "source": "admin"})
    token = issue_token(eng.store, "CAM-1")
    deliver(ing, token, [{**ev[0], "gateway_id": "CAM-1"}])
    assert ing.refused_reasons == {"token is not a production gateway's": 1}
    assert eng.store.counts()["raw_events"] == 0


def test_garbage_from_a_known_gateway_is_quarantined_as_before(base_cfg):
    eng, ing, _ = setup(base_cfg)
    token = issue_token(eng.store, "GW")
    deliver(ing, token, ["{not json"])
    deliver(ing, None, ["{not json"])
    assert eng.store.counts() == {"raw_events": 0, "rejected": 1} and ing.refused == 1


def test_without_token_check_everything_reaches_ingestion(base_cfg):
    eng, _, ev = setup(base_cfg)
    ing = MqttIngest(eng, require_token=False)
    deliver(ing, None, ev)
    assert eng.store.counts()["raw_events"] == len(ev)


def test_token_travels_as_an_mqtt5_user_property():
    props = token_properties("abc")
    assert props.UserProperty == [(TOKEN_PROPERTY, "abc")]
    assert message_token(SimpleNamespace(properties=props)) == "abc"
    assert token_properties(None) is None and token_properties("") is None
    assert message_token(SimpleNamespace(properties=None)) is None
    assert message_token(SimpleNamespace()) is None


def test_plants_lists_lines_added_in_the_master_tables(base_cfg):
    store = new_store()
    sync_site(store, base_cfg)
    c = TestClient(create_app(LiveEngine(store, topology_from_dict(base_cfg))))
    admin = c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"})
    assert admin.status_code == 200, admin.text
    h = {"Authorization": "Bearer " + c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]}
    assert c.post("/admin/lines", json={"id": "VIS-1", "plant_id": "P", "name": "Vision line"}, headers=h).status_code == 200
    assert c.post("/admin/plants", json={"id": "P2", "name": "Printing plant"}, headers=h).status_code == 200
    assert c.post("/admin/lines", json={"id": "TIJ-1", "plant_id": "P2", "name": "Printer line"}, headers=h).status_code == 200
    got = c.get("/plants", headers=h).json()
    assert got == [{"id": "P", "name": "P", "lines": [{"id": "L", "name": "L", "production": True},
                                                      {"id": "VIS-1", "name": "Vision line", "production": False}]},
                   {"id": "P2", "name": "Printing plant", "lines": [{"id": "TIJ-1", "name": "Printer line", "production": False}]}]
    lines = c.get("/plants/P2/lines", headers=h).json()
    assert lines == [{"id": "TIJ-1", "name": "Printer line", "target_per_hour": None, "equipment": [], "production": False}]
    assert c.get("/plants/NOPE/lines", headers=h).status_code == 404


def test_edge_reads_each_gateways_token_from_its_token_file(tmp_path):
    import pytest
    from edge.config import ConfigError, EdgeConfig, LineEdge, load_tokens
    f = tmp_path / "gw1.token"
    f.write_text("secret-1\n")
    cfg = EdgeConfig(org_id="O", mqtt_host="x", mqtt_port=1, mqtt_prefix="p",
                     lines=(LineEdge(id="L1", plant_id="P", area_id="A", gateway_id="GW1", equipment=(), token_file=str(f)),
                            LineEdge(id="L2", plant_id="P", area_id="A", gateway_id="GW2", equipment=())))
    assert load_tokens(cfg) == {"GW1": "secret-1"}
    f.write_text("  \n")
    with pytest.raises(ConfigError, match="empty"):
        load_tokens(cfg)
    f.unlink()
    with pytest.raises(ConfigError, match="cannot read token_file"):
        load_tokens(cfg)


# ---------------- refusals on the admin endpoint ----------------
def refusal_client(base_cfg):
    eng, ing, ev = setup(base_cfg)
    c = TestClient(create_app(eng, mqtt_ingest=ing))
    r = c.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"})
    assert r.status_code == 200, r.text
    admin = {"Authorization": "Bearer " + c.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]}
    return eng, ing, ev, c, admin


def test_refusal_report_counts_by_reason_and_last_per_gateway_without_tokens(base_cfg):
    eng, ing, ev, c, admin = refusal_client(base_cfg)
    deliver(ing, "secret-token-value", [dict(ev[0], gateway_id="GW")])
    deliver(ing, "secret-token-value", [dict(ev[0], gateway_id="OTHER")] * 2)
    r = c.get("/admin/mqtt/refusals", headers=admin)
    assert r.status_code == 200
    body = r.json()
    assert body["refused"] == 3 and body["by_reason"] == {"no valid gateway token": 3}
    assert {g["gateway_id"] for g in body["last_by_gateway"]} == {"GW", "OTHER"}
    assert all(g["reason"] == "no valid gateway token" and g["at"] and g["topic"] for g in body["last_by_gateway"])
    assert "secret-token-value" not in r.text


def test_refusal_report_is_admin_only_and_404_without_mqtt(base_cfg):
    eng, ing, ev, c, admin = refusal_client(base_cfg)
    assert c.get("/admin/mqtt/refusals").status_code in (401, 403)
    c.post("/admin/users", json={"username": "asha", "name": "Asha", "password": "password1",
                                 "roles": [{"role": "plant_manager", "scope_level": "plant", "scope_id": "P"}]}, headers=admin)
    tok = c.post("/auth/login", json={"username": "asha", "password": "password1"}).json()["token"]
    assert c.get("/admin/mqtt/refusals", headers={"Authorization": f"Bearer {tok}"}).status_code == 403
    plain = TestClient(create_app(eng))
    plain.post("/admin/users", json={"username": "root", "name": "Root", "password": "rootpass1"})
    t = plain.post("/auth/login", json={"username": "root", "password": "rootpass1"}).json()["token"]
    assert plain.get("/admin/mqtt/refusals", headers={"Authorization": f"Bearer {t}"}).status_code == 404


def test_last_refusal_table_is_bounded_whatever_gateway_ids_senders_claim(base_cfg):
    eng, ing, ev = setup(base_cfg)
    from backend.mqtt_ingest import MAX_GATEWAYS, MAX_GATEWAY_ID
    ing.authorise([(None, "t", dict(ev[0], gateway_id=f"{i}" + "x" * 500)) for i in range(MAX_GATEWAYS + 50)])
    rep = ing.refusal_report()
    assert len(rep["last_by_gateway"]) == MAX_GATEWAYS
    assert all(len(g["gateway_id"]) <= MAX_GATEWAY_ID for g in rep["last_by_gateway"])
    ing.authorise([(None, "t", "not an object")])
    assert any(g["gateway_id"] == "(none)" for g in ing.refusal_report()["last_by_gateway"])
