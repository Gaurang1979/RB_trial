import copy
import json

from backend.derive import Interval, derive_all
from backend.live import LiveEngine
from backend.topology import topology_from_dict
from conftest import DEMO, new_store, run, sc
from spi_common.schema import parse_ts

DEMO_RAW = json.load(open(DEMO))


def feed(engine, events, step=30, refresh=True):
    """Deliver events in emission order in chunks of `step` seconds of emission time."""
    chunk, edge = [], None
    for ev in events:
        t = parse_ts(ev["emitted_ts"])
        edge = t + step if edge is None else edge
        if t >= edge:
            engine.ingest(chunk)
            if refresh:
                engine.refresh()
            chunk, edge = [], t + step
        chunk.append(ev)
    engine.ingest(chunk)
    engine.refresh()


def seconds(store, topo, t0, t1):
    out = {}
    for eid in topo.equipment:
        arr = {}
        for iv in store.intervals(eid, t0, t1):
            for t in range(iv.start, iv.end):
                arr[t] = iv.state
        out[eid] = arr
    return out


def mismatch(a, b):
    bad = tot = 0
    for eid in a:
        for t in set(a[eid]) | set(b[eid]):
            tot += 1
            bad += a[eid].get(t) != b[eid].get(t)
    return bad / tot


def test_live_result_matches_batch_derivation(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 600, 300, equipment_id="A"), sc("bypass", 1500, 500, equipment_id="B"),
                             sc("fault", 2600, 400, equipment_id="C")]
    sim, ev = run(base_cfg, 3600)
    topo = topology_from_dict(base_cfg)
    t0 = parse_ts(sim.iso(0))
    live = LiveEngine(new_store(), topo, window_s=600)
    feed(live, ev)
    batch = new_store()
    from backend.ingest import ingest_events
    ingest_events(batch, ev)
    for eid, ivs in derive_all(batch, topo, t0, live.now + 1).items():
        batch.replace_intervals(eid, ivs)
    assert mismatch(seconds(live.store, topo, t0, live.now + 1), seconds(batch, topo, t0, live.now + 1)) < 0.002


def test_late_buffered_events_repair_history(base_cfg):
    """A network outage delivers 600 s of events late; the live result must end up identical to the no-outage run."""
    plain_sim, plain_ev = run(copy.deepcopy(base_cfg), 2400)
    base_cfg["scenarios"] = [sc("fault", 500, 300, equipment_id="A"), sc("network_outage", 900, 600, gateway_id="GW")]
    plain_cfg = copy.deepcopy(base_cfg)
    plain_cfg["scenarios"] = [sc("fault", 500, 300, equipment_id="A")]
    sim, ev = run(base_cfg, 2400)
    ref_sim, ref_ev = run(plain_cfg, 2400)
    topo = topology_from_dict(base_cfg)
    t0 = parse_ts(sim.iso(0))
    live, ref = LiveEngine(new_store(), topo, window_s=300), LiveEngine(new_store(), topo, window_s=300)
    feed(live, ev)
    feed(ref, ref_ev)
    assert mismatch(seconds(live.store, topo, t0, live.now + 1), seconds(ref.store, topo, t0, ref.now + 1)) < 0.002


def test_commit_keeps_history_before_window_and_merges_same_state():
    st = new_store()
    st.replace_intervals("X", [Interval("X", "PRODUCING", 0, 100, 1.0, "e"), Interval("X", "FAULT", 100, 160, 1.0, "e")])
    st.commit_intervals("X", [Interval("X", "FAULT", 130, 200, 1.0, "e"), Interval("X", "PRODUCING", 200, 260, 1.0, "e")], 130)
    got = [(i.state, i.start, i.end) for i in st.intervals("X", 0, 300)]
    assert got == [("PRODUCING", 0, 100), ("FAULT", 100, 200), ("PRODUCING", 200, 260)]   # FAULT kept its true start (100)


def test_status_reports_fault_with_effects_and_estimated_loss(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 200, 900, equipment_id="A")]
    sim, ev = run(base_cfg, 700)
    topo = topology_from_dict(base_cfg)
    live = LiveEngine(new_store(), topo)
    feed(live, ev)
    s = live.line_status("L")
    a = next(e for e in s["equipment"] if e["equipment_id"] == "A")
    assert a["state"] == "FAULT" and a["duration_s"] >= 450
    top = s["attention"][0]
    assert top["severity"] == "high" and top["equipment_id"] == "A"
    assert any("starved" in x and "B" in x for x in top["effects"])
    assert abs(top["estimated_loss_units"] - a["duration_s"] // 10) <= 1
    assert [q["queue_id"] for q in s["queues"]] == ["QA", "QB"]
    assert s["kpi"]["basis"] == "calculated"
    assert not any(i["equipment_id"] == "B" and "waiting" in i["title"] for i in s["attention"])  # root cause listed once


def test_healthy_line_has_no_high_severity_attention(base_cfg):
    sim, ev = run(base_cfg, 1800)
    live = LiveEngine(new_store(), topology_from_dict(base_cfg))
    feed(live, ev)
    assert not [i for i in live.line_status("L")["attention"] if i["severity"] == "high"]


def test_status_before_any_data_is_empty_not_error(base_cfg):
    live = LiveEngine(new_store(), topology_from_dict(base_cfg))
    live.refresh()
    assert live.line_status("L")["equipment"] == []


def test_silent_gateway_shows_no_data_never_a_false_bypass_and_history_is_repaired():
    """Regression: a network outage on one gateway used to look like 'running but no cycles' (bypass) while other gateways kept time moving."""
    raw = copy.deepcopy(DEMO_RAW)
    raw["scenarios"] = [sc("network_outage", 2000, 700, gateway_id="GW-VAD-01")]
    plain = copy.deepcopy(raw)
    plain["scenarios"] = []
    sim, ev = run(raw, 4000)
    ref_sim, ref_ev = run(plain, 4000)
    topo = topology_from_dict(raw)
    t0 = parse_ts(sim.iso(0))
    live, ref = LiveEngine(new_store(), topo), LiveEngine(new_store(), topo)
    l1 = [e.id for e in topo.lines["LINE-VAD-01"].equipment]
    during, chunk, edge = [], [], None
    for e in ev:
        t = parse_ts(e["emitted_ts"])
        edge = t + 60 if edge is None else edge
        if t >= edge:
            live.ingest(chunk)
            live.refresh()
            chunk, edge = [], t + 60
            if 2000 <= live.now - t0 < 2700:
                during.append((live.now - t0, {i: live.equipment_status(i)["state"] for i in l1},
                               [a["title"] for a in live.line_status("LINE-VAD-01")["attention"]]))
        chunk.append(e)
    live.ingest(chunk)
    live.refresh()
    feed(ref, ref_ev)
    assert during
    assert not any("BYPASSED" in s.values() for _, s, _ in during)                       # no false bypass while silent
    assert any(set(s.values()) == {"NO_DATA"} for t, s, _ in during if t > 2000 + 100)   # honest 'unknown' once stale
    assert any("No data from the line gateway" in " ".join(a) for _, _, a in during)
    assert mismatch(seconds(live.store, topo, t0, live.now + 1), seconds(ref.store, topo, t0, ref.now + 1)) < 0.002   # repaired after flush


def test_batch_derivation_does_not_extrapolate_past_gateway_data(base_cfg):
    from backend.derive import derive_all
    from conftest import pipeline
    p = pipeline(base_cfg, 600)
    d = derive_all(p.store, p.topo, p.t0, p.t0 + 1500)          # window runs 900 s past the last event
    b = d["B"]
    assert b[-1].state == "NO_DATA" and b[-1].end - b[-1].start >= 800
    assert not [i for i in b if i.state == "BYPASSED"]
