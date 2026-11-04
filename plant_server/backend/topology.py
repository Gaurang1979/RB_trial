"""Site topology (master data) loaded from the site JSON. Only the 'plants' section is used;
simulator-only sections ('simulation', 'scenarios') are ignored."""
from __future__ import annotations

import json
from dataclasses import dataclass


@dataclass(frozen=True)
class QueueT:
    id: str
    capacity: int


@dataclass(frozen=True)
class SensorT:
    id: str
    name: str
    unit: str
    equipment_id: str
    sample_interval_s: int = 10
    valid_min: float | None = None
    valid_max: float | None = None
    stuck_epsilon: float = 0.0  # readings within this spread count as "constant"


@dataclass(frozen=True)
class EquipmentT:
    id: str
    name: str
    line_id: str
    gateway_id: str
    standard_cycle_time_s: float
    up_queue: QueueT | None
    down_queue: QueueT | None
    sensors: tuple = ()


@dataclass(frozen=True)
class LineT:
    id: str
    name: str
    plant_id: str
    target_per_hour: float
    equipment: tuple[EquipmentT, ...]


class Topology:
    def __init__(self, lines: list[LineT], plants: dict | None = None, alert_rules: list | None = None):
        self.plants = plants or {}
        self.alert_rules = alert_rules
        self.lines = {ln.id: ln for ln in lines}
        self.equipment = {e.id: e for ln in lines for e in ln.equipment}


def load_topology(path: str) -> Topology:
    with open(path, encoding="utf-8") as fh:
        return topology_from_dict(json.load(fh))


def topology_from_dict(raw: dict) -> Topology:
    lines, plants = [], {}
    for p in raw["plants"]:
        plants[p["id"]] = p["name"]
        for a in p["areas"]:
            for ln in a["lines"]:
                qs = [QueueT(q["id"], int(q["capacity"])) for q in ln.get("queues", [])]
                eqs = []
                for i, e in enumerate(ln["equipment"]):
                    sens = tuple(SensorT(x["id"], x["name"], x.get("unit", ""), e["id"], int(x.get("sample_interval_s", 10)),
                                         (x.get("valid_range") or [None, None])[0], (x.get("valid_range") or [None, None])[1],
                                         float(x.get("stuck_epsilon", 0.0))) for x in e.get("sensors", []))
                    eqs.append(EquipmentT(e["id"], e["name"], ln["id"], ln["gateway_id"], float(e["standard_cycle_time_s"]),
                                          qs[i - 1] if i > 0 else None, qs[i] if i < len(qs) else None, sens))
                lines.append(LineT(ln["id"], ln["name"], p["id"], float(ln.get("target_per_hour", 0.0)), tuple(eqs)))
    return Topology(lines, plants, raw.get("alert_rules"))
