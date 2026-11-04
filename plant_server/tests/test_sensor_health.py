import copy
import json

import pytest

from backend.live import LiveEngine
from backend.sensor_health import FAULTS, assess_all, assess_sensor
from backend.topology import topology_from_dict
from conftest import DEMO, new_store, run, sc
from test_live import feed

DEMO_RAW = json.load(open(DEMO))


def health(raw, duration, sensor="B.T"):
    sim, ev = run(raw, duration)
    eng = LiveEngine(new_store(), topology_from_dict(raw))
    feed(eng, ev)
    eq = next(e for e in eng.topo.equipment.values() if any(s.id == sensor for s in e.sensors))
    s = next(s for s in eq.sensors if s.id == sensor)
    return assess_sensor(eng.store, eq, s, eng.now), sim, eng


def varying_machine(cfg):
    cfg["scenarios"] += [sc("fault", 700, 100, equipment_id="B"), sc("fault", 1100, 100, equipment_id="B")]
    return cfg


def test_stuck_sensor_is_detected_with_evidence(base_cfg):
    base_cfg["scenarios"] = [sc("sensor_stuck", 300, 1500, equipment_id="B", sensor_id="B.T")]
    varying_machine(base_cfg)
    h, sim, _ = health(base_cfg, 1500)
    assert h["status"] == "STUCK" and h["severity"] == "medium" and h["basis"] == "detected"
    assert abs((h["since"] - h["current"]["ts"]) + h["duration_s"]) <= 1
    assert 290 <= h["since"] - _t0(sim) <= 320 and h["duration_s"] >= 600
    assert h["last_valid"]["value"] == h["current"]["value"]
    assert "while the machine changed state" in h["reason"]


def _t0(sim):
    from spi_common.schema import parse_ts
    return parse_ts(sim.iso(0))


def test_not_flagged_before_the_flat_run_is_long_enough(base_cfg):
    base_cfg["scenarios"] = [sc("sensor_stuck", 300, 1500, equipment_id="B", sensor_id="B.T")]
    varying_machine(base_cfg)
    h, *_ = health(base_cfg, 700)          # flat for only ~400 s
    assert h["status"] == "OK"


def test_healthy_sensors_are_never_flagged(base_cfg):
    varying_machine(base_cfg)
    h, *_ = health(base_cfg, 3600)
    assert h["status"] == "OK" and h["severity"] == "none"


def test_constant_reading_with_stable_process_is_not_a_fault(base_cfg):
    base_cfg["scenarios"] = [sc("sensor_stuck", 300, 1500, equipment_id="B", sensor_id="B.T")]   # machine B never changes state here
    h, *_ = health(base_cfg, 1500)
    assert h["status"] == "OK" and "process was stable" in h["flag"]


def test_impossible_values_high_severity_then_recover(base_cfg):
    base_cfg["scenarios"] = [sc("sensor_impossible", 400, 300, equipment_id="B", sensor_id="B.T")]
    h, *_ = health(base_cfg, 650)
    assert h["status"] == "IMPOSSIBLE" and h["severity"] == "high" and h["current"]["value"] == 9999.9
    assert h["last_valid"]["value"] < 200                              # the last plausible reading is reported
    h2, *_ = health(base_cfg, 900)
    assert h2["status"] == "OK"


def test_offline_sensor_while_gateway_alive(base_cfg):
    base_cfg["scenarios"] = [sc("sensor_offline", 400, 700, equipment_id="B", sensor_id="B.T")]
    h, sim, _ = health(base_cfg, 900)
    assert h["status"] == "OFFLINE" and h["duration_s"] >= 400
    assert h["last_valid"]["ts"] - _t0(sim) <= 400


def test_silent_gateway_and_unreachable_machine_are_not_sensor_faults():
    raw = copy.deepcopy(DEMO_RAW)
    raw["scenarios"] = [sc("network_outage", 1000, 800, gateway_id="GW-VAD-01"), sc("device_comm_loss", 1000, 800, equipment_id="CNC-01")]
    sim, ev = run(raw, 1500)
    ev = [e for e in ev if e["emitted_ts"] <= sim.iso(1500)]
    eng = LiveEngine(new_store(), topology_from_dict(raw))
    feed(eng, [e for e in ev if e["emitted_ts"] < sim.iso(1500)])
    res = {h["sensor_id"]: h["status"] for h in assess_all(eng.store, eng.topo, eng.now)}
    assert res["PRESS-01.TEMP"] == "GATEWAY_SILENT"                    # line 1: gateway in outage
    assert res["CNC-01.TEMP"] == "UNREACHABLE"                         # machine unreachable, gateway alive
    assert res["PRESS-03.TEMP"] == "OK"                                # other line unaffected
    assert not [s for s in res.values() if s in FAULTS]


def test_sensor_scenarios_do_not_change_the_physics(base_cfg):
    plain, _ = run(copy.deepcopy(base_cfg), 1500)
    base_cfg["scenarios"] = [sc("sensor_offline", 100, 300, equipment_id="B", sensor_id="B.T"), sc("sensor_impossible", 500, 200, equipment_id="B", sensor_id="B.T")]
    noisy, _ = run(base_cfg, 1500)
    phys = lambda s: [r for r in s.truth if r["kind"] == "state"]
    assert phys(plain) == phys(noisy)


def test_stuck_stays_stuck_for_the_whole_flat_run(base_cfg):
    """Regression: the process changes only early in a long flat run. Judging a sliding window alone made the verdict flap between STUCK and OK."""
    base_cfg["scenarios"] = [sc("sensor_stuck", 300, 3300, equipment_id="B", sensor_id="B.T"), sc("fault", 700, 100, equipment_id="B")]
    for T in (1500, 2500, 3500):            # 3500 s: the only state change (700 s) is far outside any short window
        h, *_ = health(copy.deepcopy(base_cfg), T)
        assert h["status"] == "STUCK", T
    assert h["duration_s"] >= 3000
