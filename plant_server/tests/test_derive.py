import copy
import json

import pytest

from backend.derive import derive_all
from backend.scoring import score
from conftest import DEMO, pipeline, sc

DEMO_RAW = json.load(open(DEMO))


def scored(p):
    return score(p.derived, p.sim.truth, p.t0, p.t1)


def scenario_cfg(base_cfg):
    base_cfg["scenarios"] = [sc("fault", 600, 300, equipment_id="A"), sc("bypass", 1500, 300, equipment_id="B"),
                             sc("fault", 2400, 400, equipment_id="C")]
    return base_cfg


def test_derived_states_match_truth_on_scripted_scenarios(base_cfg):
    s = scored(pipeline(scenario_cfg(base_cfg), 3600))
    assert s["agreement"] >= 0.98
    for state in ("FAULT", "STARVED", "BLOCKED"):
        assert s["per_state"][state]["recall"] >= 0.95, state
    assert s["per_state"]["BYPASSED"]["recall"] >= 0.85


def test_fault_window_is_found_within_two_seconds(base_cfg):
    p = pipeline(scenario_cfg(base_cfg), 3600)
    faults = [(i.start - p.t0, i.end - p.t0) for i in p.derived["A"] if i.state == "FAULT"]
    assert len(faults) == 1
    assert abs(faults[0][0] - 600) <= 2 and abs(faults[0][1] - 900) <= 2


def test_bypass_window_overlaps_truth_and_uses_neutral_wording(base_cfg):
    p = pipeline(scenario_cfg(base_cfg), 3600)
    byp = [i for i in p.derived["B"] if i.state == "BYPASSED"]
    assert len(byp) == 1
    s, e = byp[0].start - p.t0, byp[0].end - p.t0
    inter = max(0, min(e, 1800) - max(s, 1500))
    union = max(e, 1800) - min(s, 1500)
    assert inter / union >= 0.85
    assert "signal inconsistency" in byp[0].evidence and "operator" not in byp[0].evidence.lower()


def test_bypass_shorter_than_one_cycle_is_not_detected(base_cfg):
    """Documented limit: bypass is only visible once the cycle gap exceeds factor x standard cycle."""
    base_cfg["scenarios"] = [sc("bypass", 500, 15, equipment_id="B")]
    p = pipeline(base_cfg, 1500)
    assert not [i for i in p.derived["B"] if i.state == "BYPASSED"]


@pytest.mark.parametrize("seed", [1, 2, 3])
def test_no_false_bypass_on_healthy_noisy_machines(base_cfg, seed):
    base_cfg["simulation"]["seed"] = seed
    for e in base_cfg["plants"][0]["areas"][0]["lines"][0]["equipment"]:
        e["cycle_time_cv"] = 0.2
    p = pipeline(base_cfg, 7200)
    assert not [i for ivs in p.derived.values() for i in ivs if i.state == "BYPASSED"]


def test_stuck_sensor_does_not_change_derived_states(base_cfg):
    plain = pipeline(copy.deepcopy(base_cfg), 1200)
    base_cfg["scenarios"] = [sc("sensor_stuck", 200, 600, equipment_id="B", sensor_id="B.T")]
    assert pipeline(base_cfg, 1200).derived == plain.derived


def test_state_before_first_event_is_unknown_not_guessed(base_cfg):
    p = pipeline(base_cfg, 300)
    early = derive_all(p.store, p.topo, p.t0 - 100, p.t1)["B"]
    assert (early[0].state, early[0].duration) == ("UNKNOWN", 100)


@pytest.mark.parametrize("seed", [1, 2, 3])
def test_demo_factory_meets_proposed_accuracy_threshold(seed):
    raw = copy.deepcopy(DEMO_RAW)
    raw["simulation"]["seed"] = seed
    s = scored(pipeline(raw, 7200))
    assert s["agreement"] >= 0.99
    for state in ("FAULT", "STARVED", "BLOCKED", "PRODUCING"):
        assert s["per_state"][state]["recall"] >= 0.95, state
    assert s["per_state"]["BYPASSED"]["recall"] >= 0.90


@pytest.mark.parametrize("queue_s,cv", [(60, 0.05), (10, 0.25), (30, 0.2)])
def test_accuracy_holds_with_sparse_queue_data_and_noisy_cycles(queue_s, cv):
    raw = copy.deepcopy(DEMO_RAW)
    raw["simulation"]["queue_sample_interval_s"] = queue_s
    for pl in raw["plants"]:
        for ar in pl["areas"]:
            for ln in ar["lines"]:
                for e in ln["equipment"]:
                    e["cycle_time_cv"] = cv
    assert scored(pipeline(raw, 7200))["agreement"] >= 0.98
