import copy
import json

from backend.kpi import downtime_events, equipment_kpi, line_kpi
from backend.topology import topology_from_dict
from conftest import pipeline, sc


def kpi(p, eid, t0=None, t1=None):
    return equipment_kpi(p.store, p.topo.equipment[eid], p.derived[eid], t0 or p.t0, t1 or p.t1)


def test_unobserved_production_is_reconciled_from_cumulative_counters(base_cfg):
    base_cfg["scenarios"] = [sc("device_comm_loss", 300, 200, equipment_id="B")]
    p = pipeline(base_cfg, 1000)
    k = kpi(p, "B")
    assert k["count_total"] == p.sim.lines[0].machines[1].total   # nothing lost
    assert k["count_unobserved"] >= 5 and k["unobserved_s"] == 200
    assert k["count_observed"] + k["count_unobserved"] == k["count_total"]


def test_healthy_line_kpis_are_consistent(base_cfg):
    p = pipeline(base_cfg, 7200)
    k = kpi(p, "B")                                    # the bottleneck
    assert k["quality"] == 1.0                         # reject_rate 0 in this config
    assert k["availability_own"] >= 0.97
    assert 0.9 <= k["performance"] <= 1.0              # <1 mainly from whole-second cycle rounding in the simulator
    assert abs(k["oee"] - k["availability"] * k["performance"] * k["quality"]) < 1e-3
    assert sum(k["state_s"].values()) == k["window_s"]


def test_bottleneck_identified_when_clear(base_cfg):
    p = pipeline(base_cfg, 7200)
    lk = line_kpi(p.store, p.topo.lines["L"], p.derived, p.t0, p.t1)
    assert lk["bottleneck"]["candidate"] == "B" and lk["bottleneck"]["clear"]


def test_line_output_and_target(base_cfg):
    p = pipeline(base_cfg, 3600)
    lk = line_kpi(p.store, p.topo.lines["L"], p.derived, p.t0, p.t1)
    assert lk["good_output"] == p.sim.lines[0].machines[-1].good
    assert lk["target"] == 180.0
    assert lk["achievement"] == round(lk["good_output"] / 180.0, 4)


def test_performance_is_capped_at_one_and_raw_value_kept(base_cfg):
    slow = copy.deepcopy(base_cfg)
    slow["plants"][0]["areas"][0]["lines"][0]["equipment"][1]["standard_cycle_time_s"] = 40   # master data says 40 s, machine does 20 s
    p = pipeline(base_cfg, 3600, topo_raw=slow)
    k = kpi(p, "B")
    assert k["performance"] == 1.0 and k["performance_raw"] > 1.5


def test_downtime_events_are_categorised_without_guessing_causes(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 600, 300, equipment_id="A"), sc("fault", 1500, 10, equipment_id="A"),
                             sc("fault", 1800, 600, equipment_id="C")]
    p = pipeline(base_cfg, 3000)
    ev = downtime_events(p.derived["A"], min_s=30)
    faults = [e for e in ev if e["state"] == "FAULT"]
    assert len(faults) == 1                                   # the 10 s stop is below the 30 s filter
    assert abs(faults[0]["duration_s"] - 300) <= 2
    assert faults[0]["category"] == "Unknown (fault signal)"
    starved = [e for e in downtime_events(p.derived["B"]) if e["state"] == "STARVED"]
    assert starved and starved[0]["category"] == "Upstream issue"
    blocked = [e for e in downtime_events(p.derived["B"]) if e["state"] == "BLOCKED"]   # C fault backs up B
    assert blocked and blocked[0]["category"] == "Downstream issue"
