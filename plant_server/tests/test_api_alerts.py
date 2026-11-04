import pytest
from fastapi.testclient import TestClient

from backend.api import create_app
from backend.alerts import MemoryNotifier
from backend.live import LiveEngine
from backend.topology import topology_from_dict
from conftest import new_store, run, sc
from test_live import feed

RULES = [{"id": "stop", "type": "equipment_stopped", "severity": "high", "threshold_s": 120, "recipients": ["production_manager"], "channels": ["in_app", "memory"],
          "escalations": [{"after_s": 300, "recipients": ["plant_manager"]}]},
         {"id": "sens", "type": "sensor_unhealthy", "severity": "medium", "threshold_s": 0, "recipients": ["maintenance_manager"], "channels": ["in_app"]}]


@pytest.fixture
def served(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 300, 2000, equipment_id="A"), sc("sensor_stuck", 300, 1500, equipment_id="B", sensor_id="B.T"),
                             sc("fault", 700, 100, equipment_id="B"), sc("fault", 1100, 100, equipment_id="B")]
    raw = {**base_cfg, "alert_rules": RULES}
    sim, ev = run(raw, 1500)
    eng = LiveEngine(new_store(), topology_from_dict(raw), alerts=True, notifiers={"memory": MemoryNotifier()})
    feed(eng, ev)
    return eng


def client(eng, key=None):
    return TestClient(create_app(eng, api_key=key))


def test_list_filter_and_detail_with_audit_trail(served):
    c = client(served)
    al = c.get("/alerts").json()
    assert {a["type"] for a in al} == {"equipment_stopped", "sensor_unhealthy"}
    assert [a["id"] for a in c.get("/alerts?state=open").json()] == [a["id"] for a in al if a["state"] == "OPEN"]
    assert c.get("/alerts?state=resolved").json() == []
    assert len(c.get("/alerts?state=active&line_id=L").json()) == 2
    assert c.get("/alerts?line_id=NOPE").status_code == 404
    assert c.get("/alerts?state=weird").status_code == 400
    stop = next(a for a in al if a["type"] == "equipment_stopped")
    d = c.get(f"/alerts/{stop['id']}").json()
    assert [e["kind"] for e in d["audit_trail"]][:1] == ["opened"] and d["notifications"] and d["basis"] == "detected"
    assert d["severity"] == "high" and d["recipients"] == ["production_manager"] and d["duration_s"] > 600
    assert c.get("/alerts/9999").status_code == 404


def test_acknowledge_validation_conflict_and_audit(served):
    c = client(served)
    a = next(x for x in c.get("/alerts").json() if x["type"] == "equipment_stopped")
    assert c.post(f"/alerts/{a['id']}/acknowledge", json={}).status_code == 422                  # a name is required for the audit trail
    assert c.post(f"/alerts/{a['id']}/acknowledge", json={"user": ""}).status_code == 422
    ok = c.post(f"/alerts/{a['id']}/acknowledge", json={"user": "asha", "note": "on my way"})
    assert ok.status_code == 200 and ok.json()["state"] == "ACKNOWLEDGED" and ok.json()["acked_by"] == "asha"
    assert c.post(f"/alerts/{a['id']}/acknowledge", json={"user": "ravi"}).status_code == 409
    assert c.post("/alerts/9999/acknowledge", json={"user": "x"}).status_code == 404
    trail = c.get(f"/alerts/{a['id']}").json()["audit_trail"]
    assert any(e["kind"] == "acknowledged" and e["actor"] == "asha" and e["detail"] == "on my way" for e in trail)


def test_manual_resolve_needs_a_reason(served):
    c = client(served)
    a = next(x for x in c.get("/alerts").json() if x["type"] == "sensor_unhealthy")
    assert c.post(f"/alerts/{a['id']}/resolve", json={"user": "asha", "reason": "x"}).status_code == 422
    r = c.post(f"/alerts/{a['id']}/resolve", json={"user": "asha", "reason": "sensor replaced"})
    assert r.status_code == 200 and r.json()["state"] == "RESOLVED" and "sensor replaced" in r.json()["resolution"]
    assert c.post(f"/alerts/{a['id']}/resolve", json={"user": "asha", "reason": "again"}).status_code == 409


def test_sensor_health_endpoints(served):
    c = client(served)
    h = c.get("/sensors/B.T/health").json()
    assert h["status"] == "STUCK" and h["basis"] == "detected" and h["last_valid"]["ts"].endswith("Z") and "while the machine changed state" in h["reason"]
    assert [x["sensor_id"] for x in c.get("/lines/L/sensors").json()] == ["B.T"]
    assert c.get("/sensors/NOPE/health").status_code == 404
    st = c.get("/lines/L/status").json()
    assert st["sensor_issues"][0]["sensor_id"] == "B.T" and len(st["alerts"]) == 2


def test_rules_are_readable_and_alerts_can_be_disabled(served, base_cfg):
    # a rule list naming no vision rule gets the default vision rules (PL2)
    assert [r["id"] for r in client(served).get("/alert-rules").json()] == ["stop", "sens", "vision-alarm", "vision-station-offline", "vision-grade-drift",
                                                                                   "repeated-locks", "audit-chain-broken"]
    off = LiveEngine(new_store(), topology_from_dict(base_cfg))
    assert client(off).get("/alerts").status_code == 404


def test_alert_endpoints_need_the_api_key(served):
    c = client(served, key="k")
    assert c.get("/alerts").status_code == 401
    assert c.post("/alerts/1/acknowledge", json={"user": "x"}).status_code == 401
    assert c.get("/alerts", headers={"X-API-Key": "k"}).status_code == 200


def test_pwa_files_are_served_without_a_key(served):
    c = client(served, key="k")
    m = c.get("/manifest.webmanifest")
    assert m.status_code == 200 and m.json()["short_name"] == "SPI" and m.headers["content-type"].startswith("application/manifest+json")
    sw = c.get("/sw.js")
    assert sw.status_code == 200 and "showNotification" in sw.text and sw.headers["service-worker-allowed"] == "/"
    assert c.get("/static-app/icon-192.png").headers["content-type"] == "image/png"
    assert c.get("/static-app/secret.txt").status_code == 404
