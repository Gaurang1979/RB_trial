import copy
import json

from simulator.config import load_config
from simulator.engine import Simulation
from simulator.schema import validate_event
from simulator.sinks import MemorySink
from conftest import DEMO, run

DEMO_RAW = json.load(open(DEMO))


def sc(kind, start, dur, **kw):
    return {"kind": kind, "start_s": start, "duration_s": dur, **kw}


def states(sim, eq, state):
    return [r["ts"] for r in sim.truth if r["kind"] == "state" and r["equipment_id"] == eq and r["state"] == state]


def line_of(sim):
    return sim.lines[0]


# ---- determinism & schema -------------------------------------------------
def test_same_seed_is_identical_and_different_seed_differs():
    a = run(copy.deepcopy(DEMO_RAW), 900)[1]
    b = run(copy.deepcopy(DEMO_RAW), 900)[1]
    other = copy.deepcopy(DEMO_RAW)
    other["simulation"]["seed"] = 43
    c = run(other, 900)[1]
    assert a == b
    assert a != c


def test_every_event_matches_schema():
    events = run(copy.deepcopy(DEMO_RAW), 7200)[1]
    assert len(events) > 10000
    bad = [(e["event_id"], p) for e in events if (p := validate_event(e))]
    assert bad == []


# ---- physics --------------------------------------------------------------
def test_conservation_and_queue_bounds_every_tick(base_cfg):
    base_cfg["plants"][0]["areas"][0]["lines"][0]["equipment"][0]["reject_rate"] = 0.1
    base_cfg["scenarios"] = [sc("fault", 300, 200, equipment_id="B"), sc("fault", 900, 300, equipment_id="C")]
    from simulator.config import parse_config
    sim = Simulation(parse_config(base_cfg), MemorySink())
    line = line_of(sim)
    for _ in range(3000):
        sim.step()
        for i, q in enumerate(line.queues):
            assert 0 <= q.level <= q.cfg.capacity
            up, down = line.machines[i], line.machines[i + 1]
            assert up.good == down.taken + q.level + (1 if up.holding else 0)
    for m in line.machines:
        assert m.total == m.good + m.reject


def test_bottleneck_emerges_without_any_fault(base_cfg):
    sim, _ = run(base_cfg, 7200)
    pct = sim.summary()["lines"]["L"]["machines"]
    a, b, c = pct["A"], pct["B"], pct["C"]
    assert a.get("BLOCKED", 0) > 30          # fast upstream waits for the bottleneck
    assert c.get("STARVED", 0) > 15          # fast downstream waits for the bottleneck
    assert b.get("BLOCKED", 0) + b.get("STARVED", 0) < 3
    good = sim.summary()["lines"]["L"]["good_output"]
    assert 330 <= good <= 361                # ~3600*2/20 minus 1 s tick quantisation


def test_upstream_fault_starves_downstream(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 200, 300, equipment_id="A")]
    sim, _ = run(base_cfg, 900)
    lo, hi = sim.iso(200), sim.iso(500)
    assert any(lo < ts < hi for ts in states(sim, "B", "STARVED"))
    assert any(lo < ts for ts in states(sim, "C", "STARVED"))


def test_downstream_fault_blocks_upstream(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 200, 600, equipment_id="C")]
    sim, _ = run(base_cfg, 1000)
    lo, hi = sim.iso(200), sim.iso(800)
    assert any(lo < ts < hi for ts in states(sim, "B", "BLOCKED"))


def test_bypass_looks_running_but_produces_nothing(base_cfg):
    base_cfg["scenarios"] = [sc("bypass", 300, 200, equipment_id="B")]
    sim, ev = run(base_cfg, 700)
    lo, hi = sim.iso(300), sim.iso(500)
    assert any(lo <= ts < hi for ts in states(sim, "B", "BYPASSED"))
    assert [e for e in ev if e["type"] == "cycle" and e["equipment_id"] == "B" and lo <= e["ts"] < hi] == []
    running = [e for e in ev if e["type"] == "signal" and e["equipment_id"] == "B" and e["payload"]["name"] == "running" and e["ts"] <= sim.iso(400)]
    assert running[-1]["payload"]["value"] == 1  # signal says running while output is zero


def test_sensor_stuck_gives_constant_reading_only_inside_window(base_cfg):
    base_cfg["scenarios"] = [sc("sensor_stuck", 300, 400, equipment_id="B", sensor_id="B.T")]
    sim, ev = run(base_cfg, 1200)
    lo, hi = sim.iso(300), sim.iso(700)
    vals = [e["payload"]["value"] for e in ev if e["type"] == "sensor" and lo <= e["ts"] < hi]
    outside = [e["payload"]["value"] for e in ev if e["type"] == "sensor" and e["ts"] >= hi]
    assert len(vals) >= 30 and len(set(vals)) == 1
    assert len(set(outside)) > 3
    assert all(e["payload"]["quality"] == "good" for e in ev if e["type"] == "sensor")  # edge does not know


# ---- edge failures --------------------------------------------------------
def test_device_comm_loss_hides_equipment_and_counters_catch_up(base_cfg):
    base_cfg["scenarios"] = [sc("device_comm_loss", 300, 200, equipment_id="B")]
    sim, ev = run(base_cfg, 1000)
    lo, hi = sim.iso(300), sim.iso(500)
    silent = [e for e in ev if e["equipment_id"] == "B" and e["type"] in ("signal", "cycle", "sensor", "counter") and lo <= e["ts"] < hi]
    assert silent == []
    status = [(e["ts"], e["payload"]["ok"]) for e in ev if e["type"] == "comm_status"]
    assert status == [(lo, False), (hi, True)]
    totals = [(e["ts"], (e["payload"].get("counters") or e["payload"])["total"]) for e in ev
              if e["equipment_id"] == "B" and e["type"] in ("cycle", "counter")]
    before = max(t for ts, t in totals if ts < lo)
    after = min(t for ts, t in totals if ts >= hi)
    assert after - before >= 5                     # machine kept producing while unobserved
    assert totals[-1][1] == line_of(sim).machines[1].total


def test_network_outage_buffers_then_flushes_in_order_without_gaps(base_cfg):
    base_cfg["scenarios"] = [sc("network_outage", 300, 200, gateway_id="GW")]
    sim, ev = run(base_cfg, 900)
    assert [e["seq"] for e in ev] == list(range(1, len(ev) + 1))   # contiguous, ordered, no loss
    held = [e for e in ev if sim.iso(300) <= e["ts"] < sim.iso(500)]
    assert len(held) > 20
    assert all(e["buffered"] and e["emitted_ts"] == sim.iso(500) for e in held)
    assert not any(e["buffered"] for e in ev if e["ts"] < sim.iso(300) or e["ts"] >= sim.iso(500))


def test_duplicates_share_event_id(base_cfg):
    base_cfg["simulation"]["duplicate_rate"] = 0.2
    sim, ev = run(base_cfg, 900)
    gw = line_of(sim).gateway
    assert gw.duplicates > 0
    assert len(ev) - len({e["event_id"] for e in ev}) == gw.duplicates
    assert len({e["event_id"] for e in ev}) == gw.created


def test_edge_failures_never_change_the_physics(base_cfg):
    plain, _ = run(copy.deepcopy(base_cfg), 1500)
    base_cfg["scenarios"] = [
        sc("device_comm_loss", 200, 300, equipment_id="B"),
        sc("network_outage", 400, 300, gateway_id="GW"),
        sc("sensor_stuck", 100, 500, equipment_id="B", sensor_id="B.T"),
    ]
    noisy, _ = run(base_cfg, 1500)
    phys = lambda s: [r for r in s.truth if r["kind"] == "state"]
    assert phys(plain) == phys(noisy)


def test_random_faults_are_seeded_and_reproducible(base_cfg):
    eq = base_cfg["plants"][0]["areas"][0]["lines"][0]["equipment"][0]
    eq.update({"mtbf_s": 600, "mttr_s": 60})
    s1, _ = run(copy.deepcopy(base_cfg), 7200)
    s2, _ = run(copy.deepcopy(base_cfg), 7200)
    faults = [r for r in s1.truth if r.get("scenario") == "random_fault"]
    assert len(faults) >= 3
    assert faults == [r for r in s2.truth if r.get("scenario") == "random_fault"]
    assert all(r["key"] == "A" for r in faults)
