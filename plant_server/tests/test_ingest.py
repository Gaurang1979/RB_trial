import copy
import json
import random

from backend.ingest import gaps, ingest_events
from backend.kpi import equipment_kpi
from conftest import new_store, pipeline, run, sc


def test_reingesting_is_idempotent(base_cfg):
    _, ev = run(base_cfg, 600)
    st = new_store()
    r1 = ingest_events(st, ev)
    r2 = ingest_events(st, ev)
    assert r1.accepted == len(ev) and r1.duplicates == 0
    assert r2.accepted == 0 and r2.duplicates == len(ev)
    assert st.counts()["raw_events"] == len(ev)


def test_invalid_events_are_quarantined_with_reason_not_dropped(base_cfg):
    _, ev = run(base_cfg, 60)
    bad_missing = {k: v for k, v in ev[0].items() if k != "gateway_id"}
    bad_ts = {**ev[1], "event_id": "GW:x", "ts": "yesterday"}
    st = new_store()
    rep = ingest_events(st, [ev[2], bad_missing, bad_ts, "not-an-object"])
    assert (rep.accepted, rep.rejected) == (1, 3)
    assert st.counts() == {"raw_events": 1, "rejected": 3}
    assert any("missing 'gateway_id'" in r for r in rep.reject_reasons)


def test_injected_duplicates_are_removed(base_cfg):
    base_cfg["simulation"]["duplicate_rate"] = 0.2
    sim, ev = run(base_cfg, 900)
    st = new_store()
    rep = ingest_events(st, ev)
    gw = sim.lines[0].gateway
    assert rep.duplicates == gw.duplicates > 0
    assert st.counts()["raw_events"] == gw.created


def test_delivery_order_does_not_change_derived_states(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 200, 200, equipment_id="A"), sc("bypass", 500, 300, equipment_id="B")]
    p = pipeline(base_cfg, 1200)
    shuffled = list(p.events)
    random.Random(3).shuffle(shuffled)
    st = new_store()
    ingest_events(st, shuffled)
    from backend.derive import derive_all
    assert derive_all(st, p.topo, p.t0, p.t1) == p.derived


def test_sequence_gaps_reported_and_closed_by_late_arrival(base_cfg):
    _, ev = run(base_cfg, 300)
    lost = [e for e in ev if 10 <= e["seq"] <= 12]
    kept = [e for e in ev if not 10 <= e["seq"] <= 12]
    st = new_store()
    ingest_events(st, kept)
    assert gaps(st) == {"GW": [(10, 12)]}
    ingest_events(st, lost)
    assert gaps(st) == {}


def test_network_outage_end_to_end_gives_identical_results(base_cfg):
    plain = pipeline(copy.deepcopy(base_cfg), 1500)
    cfg = copy.deepcopy(base_cfg)
    cfg["scenarios"] = [sc("network_outage", 400, 500, gateway_id="GW")]
    out = pipeline(cfg, 1500)
    assert any(e["buffered"] for e in out.events)
    assert gaps(out.store) == {}
    assert out.derived == plain.derived
    eq = out.topo.equipment["B"]
    assert equipment_kpi(out.store, eq, out.derived["B"], out.t0, out.t1) == \
           equipment_kpi(plain.store, eq, plain.derived["B"], plain.t0, plain.t1)


def test_device_comm_loss_becomes_communication_error_interval(base_cfg):
    base_cfg["scenarios"] = [sc("device_comm_loss", 300, 200, equipment_id="B")]
    p = pipeline(base_cfg, 900)
    comm = [(i.start - p.t0, i.end - p.t0) for i in p.derived["B"] if i.state == "COMMUNICATION_ERROR"]
    assert comm == [(300, 500)]
