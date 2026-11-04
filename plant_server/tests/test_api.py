import json

import pytest
from fastapi.testclient import TestClient

from backend.api import create_app
from backend.live import LiveEngine
from backend.topology import topology_from_dict
from conftest import new_store, run, sc
from test_live import feed


@pytest.fixture
def served(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 300, 400, equipment_id="A"), sc("device_comm_loss", 900, 120, equipment_id="C")]
    sim, ev = run(base_cfg, 1500)
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg))
    feed(eng, ev)
    return eng, sim, ev


def client(eng, key=None):
    return TestClient(create_app(eng, api_key=key))


def test_hierarchy_and_line_status(served):
    eng, *_ = served
    c = client(eng)
    assert c.get("/plants").json() == [{"id": "P", "name": "P", "lines": [{"id": "L", "name": "L", "production": True}]}]
    assert c.get("/plants/P/lines").json()[0]["equipment"] == ["A", "B", "C"]
    s = c.get("/lines/L/status").json()
    assert [e["equipment_id"] for e in s["equipment"]] == ["A", "B", "C"]
    assert s["kpi"]["target_per_hour"] == 180 and s["kpi"]["basis"] == "calculated"
    assert {e["basis"] for e in s["equipment"]} == {"detected"}
    assert c.get("/lines/L/equipment").json()[0]["equipment_id"] == "A"
    assert c.get("/equipment/B/status").json()["state"] in ("PRODUCING", "STARVED", "BLOCKED")


def test_production_buckets_add_up_and_reconcile_unobserved(served):
    eng, sim, _ = served
    r = client(eng).get("/equipment/C/production?bucket=minute").json()
    assert sum(b["total"] for b in r["buckets"]) == r["observed_total"]
    assert r["reconciled_total"] == sim.lines[0].machines[2].total > r["observed_total"]   # comm loss: counters reconcile
    assert r["basis"] == "measured"


def test_states_downtime_bottleneck_oee_and_sensor(served):
    eng, *_ = served
    c = client(eng)
    st = c.get("/equipment/A/states").json()["intervals"]
    assert any(i["state"] == "FAULT" and abs(i["duration_s"] - 400) <= 2 for i in st)
    dt = c.get("/equipment/A/downtime").json()["events"]
    assert any(e["state"] == "FAULT" and e["category"] == "Unknown (fault signal)" for e in dt)
    assert "candidate" in c.get("/lines/L/bottlenecks").json()
    oee = c.get("/reports/oee?line_id=L").json()
    assert set(oee["equipment"]) == {"A", "B", "C"}
    rd = c.get("/sensors/B.T/readings?limit=5").json()
    assert len(rd["readings"]) == 5 and rd["readings"][0]["unit"] == "C"
    assert c.get("/ingest/stats").json()["sequence_gaps"] == {}


def test_errors_are_clear(served):
    eng, *_ = served
    c = client(eng)
    assert c.get("/lines/NOPE/status").status_code == 404
    assert c.get("/equipment/NOPE/status").status_code == 404
    assert c.get("/equipment/A/production?bucket=shift").status_code == 400
    assert c.get("/equipment/A/states?from=yesterday").status_code == 400
    assert c.get("/sensors/NOPE/readings").status_code == 404


def test_api_before_any_data_is_graceful(base_cfg):
    c = client(LiveEngine(new_store(), topology_from_dict(base_cfg)))
    assert c.get("/health").json()["data_time"] is None
    assert c.get("/lines/L/status").json()["equipment"] == []
    assert c.get("/equipment/A/states").status_code == 404


def test_api_key_protects_data_but_not_probe_or_dashboard(served):
    eng, *_ = served
    c = client(eng, key="s3cret")
    assert c.get("/health").status_code == 200
    assert c.get("/").status_code == 200
    assert c.get("/lines/L/status").status_code == 401
    assert c.get("/lines/L/status", headers={"X-API-Key": "wrong"}).status_code == 401
    assert c.get("/lines/L/status", headers={"X-API-Key": "s3cret"}).status_code == 200
    with pytest.raises(Exception):
        with c.websocket_connect("/stream/lines/L"):
            pass
    with c.websocket_connect("/stream/lines/L?api_key=s3cret&interval=0.2") as ws:
        assert ws.receive_json()["line_id"] == "L"


def test_websocket_streams_line_status(served):
    eng, *_ = served
    with client(eng).websocket_connect("/stream/lines/L?interval=0.2") as ws:
        first = ws.receive_json()
        assert first["line_id"] == "L" and len(first["equipment"]) == 3
        assert ws.receive_json()["as_of"] == first["as_of"]
    with pytest.raises(Exception):
        with client(eng).websocket_connect("/stream/lines/NOPE"):
            pass


def test_dashboard_page_is_served(served):
    r = client(served[0]).get("/")
    assert r.status_code == 200 and "Production Intelligence" in r.text
