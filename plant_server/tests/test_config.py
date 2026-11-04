import copy
import json

import pytest

from simulator.config import ConfigError, load_config, parse_config
from conftest import DEMO


def test_demo_config_loads():
    cfg = load_config(DEMO)
    assert len(cfg.plants) == 2
    assert len(cfg.lines) == 3
    assert sum(len(ln.equipment) for ln in cfg.lines) == 10
    assert "SYNTHETIC" in cfg.org_name  # demo data must be labelled (spec s54)


def test_duplicate_equipment_id_rejected(base_cfg):
    base_cfg["plants"][0]["areas"][0]["lines"][0]["equipment"][1]["id"] = "A"
    with pytest.raises(ConfigError, match="duplicate equipment"):
        parse_config(base_cfg)


def test_non_positive_cycle_time_rejected(base_cfg):
    base_cfg["plants"][0]["areas"][0]["lines"][0]["equipment"][0]["standard_cycle_time_s"] = 0
    with pytest.raises(ConfigError, match="out of range"):
        parse_config(base_cfg)


def test_queue_count_must_match_equipment(base_cfg):
    base_cfg["plants"][0]["areas"][0]["lines"][0]["queues"].pop()
    with pytest.raises(ConfigError, match="need 2 queues"):
        parse_config(base_cfg)


def test_scenario_unknown_equipment_rejected(base_cfg):
    base_cfg["scenarios"] = [{"kind": "fault", "equipment_id": "NOPE", "start_s": 0, "duration_s": 5}]
    with pytest.raises(ConfigError, match="unknown equipment_id"):
        parse_config(base_cfg)


def test_scenario_unknown_kind_rejected(base_cfg):
    base_cfg["scenarios"] = [{"kind": "explode", "equipment_id": "A", "start_s": 0, "duration_s": 5}]
    with pytest.raises(ConfigError, match="unknown kind"):
        parse_config(base_cfg)


def test_missing_field_message_names_the_field(base_cfg):
    del base_cfg["plants"][0]["areas"][0]["lines"][0]["gateway_id"]
    with pytest.raises(ConfigError, match="gateway_id"):
        parse_config(base_cfg)


def test_wrong_type_rejected(base_cfg):
    base_cfg["simulation"]["duplicate_rate"] = "lots"
    with pytest.raises(ConfigError, match="wrong type"):
        parse_config(base_cfg)
