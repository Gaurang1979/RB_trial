import copy
import http.server
import json
import threading

import pytest

from backend.alerts import DEFAULT_RULES, MemoryNotifier, WebhookNotifier, normalize_rule
from backend.live import LiveEngine
from backend.topology import topology_from_dict
from conftest import DEMO, new_store, run, sc
from test_live import feed
from spi_common.schema import parse_ts

DEMO_RAW = json.load(open(DEMO))
STOP = {"id": "stop", "type": "equipment_stopped", "severity": "high", "threshold_s": 120, "recipients": ["production_manager"], "channels": ["in_app", "memory"],
        "clear_after_s": 60, "escalations": [{"after_s": 300, "recipients": ["plant_manager"]}]}


def build(raw, rules, notifiers=None):
    raw = {**copy.deepcopy(raw), "alert_rules": rules}
    return raw, LiveEngine(new_store(), topology_from_dict(raw), alerts=True, notifiers=notifiers)


def slice_(ev, sim, a, b):
    return [e for e in ev if sim.iso(a) < e["emitted_ts"] <= sim.iso(b)]


def all_alerts(eng):
    return list(reversed(eng.alerts.list(limit=1000)))          # oldest first


def kinds(eng, aid):
    return [e["kind"] for e in eng.alerts.detail(aid)["audit_trail"]]


def scenario(base_cfg, *sc_list):
    base_cfg["scenarios"] = list(sc_list)
    return base_cfg


def test_fault_opens_one_alert_after_threshold_escalates_and_auto_resolves(base_cfg):
    mem = MemoryNotifier()
    raw, eng = build(scenario(base_cfg, sc("fault", 300, 600, equipment_id="A")), [STOP], {"memory": mem})
    sim, ev = run(raw, 1500)
    feed(eng, ev)
    (a,) = all_alerts(eng)
    t0 = parse_ts(sim.iso(0))
    assert a["severity"] == "high" and a["state"] == "RESOLVED" and "stopped" in a["title"] and a["equipment_id"] == "A"
    assert 420 <= parse_ts(a["opened_at"]) - t0 <= 460                     # fault at 300 + 120 s threshold (+ refresh granularity)
    assert 960 <= parse_ts(a["resolved_at"]) - t0 <= 1000                  # fault ends 900 + 60 s clear hysteresis
    assert "starved" in a["detail"] and "calculated" in a["detail"]
    assert kinds(eng, a["id"]) == ["opened", "escalated", "resolved"]      # nobody acknowledged: escalated once
    sent = [(m["kind"], m["recipient"]) for m in mem.sent]
    assert ("opened", "production_manager") in sent and ("escalated", "plant_manager") in sent and ("resolved", "production_manager") in sent


def test_acknowledged_alert_is_not_escalated_and_audit_records_who(base_cfg):
    raw, eng = build(scenario(base_cfg, sc("fault", 300, 900, equipment_id="A")), [STOP], {"memory": MemoryNotifier()})
    sim, ev = run(raw, 1700)
    feed(eng, slice_(ev, sim, -1, 480))
    (a,) = eng.alerts.list([ "OPEN"])
    got = eng.alerts.acknowledge(a["id"], "asha", "looking at it")
    assert got["state"] == "ACKNOWLEDGED" and got["acked_by"] == "asha"
    with pytest.raises(ValueError):
        eng.alerts.acknowledge(a["id"], "ravi")                             # already acknowledged
    feed(eng, slice_(ev, sim, 480, 1700))
    d = eng.alerts.detail(a["id"])
    assert [e["kind"] for e in d["audit_trail"]] == ["opened", "acknowledged", "resolved"]      # no escalation after ack
    assert d["audit_trail"][1]["actor"] == "asha" and d["audit_trail"][1]["detail"] == "looking at it"
    assert d["acked_by"] == "asha" and d["state"] == "RESOLVED"


def test_notifications_are_recorded_per_channel_and_recipient(base_cfg):
    raw, eng = build(scenario(base_cfg, sc("fault", 300, 600, equipment_id="A")), [STOP], {"memory": MemoryNotifier()})
    sim, ev = run(raw, 1500)
    feed(eng, ev)
    (a,) = all_alerts(eng)
    ns = eng.alerts.detail(a["id"])["notifications"]
    assert {(n["channel"], n["recipient"], n["kind"]) for n in ns} >= {("in_app", "production_manager", "opened"), ("memory", "production_manager", "opened"), ("memory", "plant_manager", "escalated")}
    assert {n["status"] for n in ns} == {"sent"}


def test_flapping_does_not_reopen_but_a_real_gap_does(base_cfg):
    for gap, expect in ((20, 1), (200, 2)):
        cfg = scenario(copy.deepcopy(base_cfg), sc("fault", 300, 200, equipment_id="A"), sc("fault", 500 + gap, 300, equipment_id="A"))
        raw, eng = build(cfg, [dict(STOP, escalations=[])], {"memory": MemoryNotifier()})
        sim, ev = run(raw, 1500)
        feed(eng, ev)
        assert len(all_alerts(eng)) == expect, gap


def test_evaluate_is_idempotent(base_cfg):
    raw, eng = build(scenario(base_cfg, sc("fault", 300, 600, equipment_id="A")), [STOP], {"memory": MemoryNotifier()})
    sim, ev = run(raw, 700)
    feed(eng, ev)
    n_alerts, n_notes = len(all_alerts(eng)), len(eng.alerts.detail(all_alerts(eng)[0]["id"])["notifications"])
    for _ in range(3):
        eng.alerts.evaluate()
    assert len(all_alerts(eng)) == n_alerts
    assert len(eng.alerts.detail(all_alerts(eng)[0]["id"])["notifications"]) == n_notes


def test_bypass_alert_is_neutral(base_cfg):
    rule = {"id": "byp", "type": "bypass_detected", "severity": "medium", "threshold_s": 300, "recipients": ["production_manager"], "channels": ["in_app"]}
    raw, eng = build(scenario(base_cfg, sc("bypass", 500, 900, equipment_id="B")), [rule])
    sim, ev = run(raw, 1500)
    feed(eng, ev)
    (a,) = all_alerts(eng)
    text = (a["title"] + a["detail"]).lower()
    assert "production signal unavailable" in text and "signal inconsistency" in text
    assert not any(w in text for w in ("operator", "blame", "misconduct", "cheat"))


def test_gateway_silent_alert_hits_only_the_affected_line_and_resolves_after_flush():
    rule = {"id": "gw", "type": "gateway_silent", "severity": "medium", "threshold_s": 120, "recipients": ["it_ot"], "channels": ["in_app"]}
    raw = copy.deepcopy(DEMO_RAW)
    raw["scenarios"] = [sc("network_outage", 1000, 800, gateway_id="GW-VAD-01")]
    raw, eng = build(raw, [rule])
    sim, ev = run(raw, 3000)
    feed(eng, slice_(ev, sim, -1, 1500))
    open_now = eng.alerts.list(["OPEN"])
    assert [a["line_id"] for a in open_now] == ["LINE-VAD-01"] and "No data from the line gateway" in open_now[0]["title"]
    feed(eng, slice_(ev, sim, 1500, 3000))
    (a,) = all_alerts(eng)
    assert a["state"] == "RESOLVED" and "condition cleared" in a["resolution"]


def test_sensor_alert_from_a_stuck_sensor(base_cfg):
    rule = {"id": "sens", "type": "sensor_unhealthy", "severity": "medium", "threshold_s": 0, "recipients": ["maintenance_manager"], "channels": ["in_app"]}
    cfg = scenario(base_cfg, sc("sensor_stuck", 300, 1500, equipment_id="B", sensor_id="B.T"), sc("fault", 700, 100, equipment_id="B"), sc("fault", 1100, 100, equipment_id="B"))
    raw, eng = build(cfg, [rule])
    sim, ev = run(raw, 1500)
    feed(eng, ev)
    (a,) = all_alerts(eng)
    assert a["type"] == "sensor_unhealthy" and a["subject"] == "B.T" and a["evidence"]["status"] == "STUCK" and a["state"] == "OPEN"


def test_below_target_alert(base_cfg):
    rule = {"id": "tgt", "type": "production_below_target", "severity": "medium", "threshold": 0.8, "min_window_s": 1800, "recipients": ["production_manager"], "channels": ["in_app"]}
    raw, eng = build(scenario(base_cfg, sc("fault", 300, 2200, equipment_id="B")), [rule])
    sim, ev = run(raw, 3600)
    feed(eng, ev)
    assert any("below target" in a["title"] for a in all_alerts(eng))


def test_queue_full_rule(base_cfg):
    rule = {"id": "qf", "type": "queue_level", "condition": "full", "queue_id": "QA", "threshold_s": 300, "severity": "low", "recipients": ["production_manager"], "channels": ["in_app"]}
    raw, eng = build(base_cfg, [rule])                                    # A is faster than B, so QA stays full
    sim, ev = run(raw, 1500)
    feed(eng, ev)
    (a,) = all_alerts(eng)
    assert a["subject"] == "QA" and a["state"] == "OPEN"


def test_default_rules_are_quiet_on_a_healthy_line(base_cfg):
    eng = LiveEngine(new_store(), topology_from_dict(base_cfg), alerts=True)
    sim, ev = run(base_cfg, 3600)
    feed(eng, ev)
    assert eng.alerts.list() == [] and len(DEFAULT_RULES) >= 5


class _Hook(http.server.BaseHTTPRequestHandler):
    got = []

    def do_POST(self):
        _Hook.got.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
        self.send_response(200)
        self.end_headers()

    def log_message(self, *a):
        pass


def test_webhook_delivery_and_failure_are_recorded_never_raised(base_cfg):
    _Hook.got = []
    srv = http.server.HTTPServer(("127.0.0.1", 0), _Hook)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    good = WebhookNotifier(f"http://127.0.0.1:{srv.server_port}/hook")
    bad = WebhookNotifier("http://127.0.0.1:9/hook", timeout=0.5)
    rule = dict(STOP, channels=["good", "bad"], escalations=[])
    raw, eng = build(scenario(base_cfg, sc("fault", 300, 400, equipment_id="A")), [rule], {"good": good, "bad": bad})
    sim, ev = run(raw, 900)
    feed(eng, ev)
    good.flush(); bad.flush()
    srv.shutdown()
    assert any(p["kind"] == "opened" and p["recipient"] == "production_manager" and p["alert"]["title"].endswith("stopped for 2 min") or "stopped" in p["alert"]["title"] for p in _Hook.got)
    (a,) = all_alerts(eng)
    ns = eng.alerts.detail(a["id"])["notifications"]
    assert {n["status"] for n in ns if n["channel"] == "good"} == {"sent"}
    assert {n["status"] for n in ns if n["channel"] == "bad"} == {"failed"}
    assert a["state"] == "RESOLVED"                                         # alerting carried on despite the dead endpoint


def test_unknown_channel_is_recorded_as_skipped(base_cfg):
    raw, eng = build(scenario(base_cfg, sc("fault", 300, 400, equipment_id="A")), [dict(STOP, channels=["sms"], escalations=[])])
    sim, ev = run(raw, 900)
    feed(eng, ev)
    ns = eng.alerts.detail(all_alerts(eng)[0]["id"])["notifications"]
    assert {(n["status"], n["detail"]) for n in ns} == {("skipped", "channel not configured")}


def test_rules_are_validated():
    with pytest.raises(ValueError, match="unknown type"):
        normalize_rule({"id": "x", "type": "explode"})
    with pytest.raises(ValueError, match="severity"):
        normalize_rule({"id": "x", "type": "gateway_silent", "severity": "catastrophic"})
    with pytest.raises(ValueError, match="needs an id"):
        normalize_rule({"type": "gateway_silent"})
    with pytest.raises(ValueError, match="recipients names unknown role"):
        normalize_rule({"id": "x", "type": "gateway_silent", "recipients": ["it"]})
    with pytest.raises(ValueError, match="escalation 2 names unknown role.*plant_head"):
        normalize_rule({"id": "x", "type": "gateway_silent", "recipients": ["it_ot"],
                        "escalations": [{"after_s": 60, "recipients": ["plant_manager"]}, {"after_s": 900, "recipients": ["plant_head"]}]})


def test_every_default_rule_names_only_real_roles():
    """Every recipient and escalation recipient of the built-in rules is a role an account can hold (auth.ROLES)."""
    from backend.alerts import DEFAULT_SECURITY_RULES, DEFAULT_VISION_RULES
    from backend.auth import ROLES
    for r in DEFAULT_RULES + DEFAULT_VISION_RULES + DEFAULT_SECURITY_RULES:
        rule = normalize_rule(r)
        for rcp in rule["recipients"] + [x for e in rule["escalations"] for x in e["recipients"]]:
            assert rcp in ROLES, (r["id"], rcp)


def test_open_alert_stays_open_while_the_machine_cannot_be_seen():
    """Regression: a gateway outage during a long stop used to resolve the alert and then open a second one when data returned."""
    raw = copy.deepcopy(DEMO_RAW)
    raw["scenarios"] = [sc("fault", 1000, 2500, equipment_id="PRESS-01"), sc("network_outage", 1500, 1000, gateway_id="GW-VAD-01")]
    raw, eng = build(raw, [dict(STOP, equipment_id=None, escalations=[], channels=["in_app"])])
    sim, ev = run(raw, 4000)
    feed(eng, ev)
    stops = [a for a in all_alerts(eng) if a["equipment_id"] == "PRESS-01"]
    t0 = parse_ts(sim.iso(0))
    assert len(stops) == 1                                                  # one incident, not two
    assert parse_ts(stops[0]["resolved_at"]) - t0 >= 3500                   # resolved only after the fault really ended
