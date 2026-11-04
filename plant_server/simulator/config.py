"""Simulator configuration: load + validate.

Customer/plant values live in JSON configs, never in code (spec section 47).
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime, timezone

SENSOR_KINDS = ("sensor_stuck", "sensor_offline", "sensor_impossible")
SCENARIO_KINDS = ("fault", "bypass", "device_comm_loss", "network_outage") + SENSOR_KINDS
NUM = (int, float)
_MISSING = object()


class ConfigError(ValueError):
    """Raised when a configuration is invalid."""


@dataclass(frozen=True)
class SensorCfg:
    id: str
    name: str
    unit: str
    base: float
    noise: float
    producing_delta: float
    sample_interval_s: int


@dataclass(frozen=True)
class EquipmentCfg:
    id: str
    name: str
    type: str
    standard_cycle_time_s: float
    cycle_time_cv: float
    reject_rate: float
    mtbf_s: float | None
    mttr_s: float
    sensors: tuple[SensorCfg, ...]


@dataclass(frozen=True)
class QueueCfg:
    id: str
    name: str
    capacity: int


@dataclass(frozen=True)
class LineCfg:
    id: str
    name: str
    plant_id: str
    area_id: str
    gateway_id: str
    target_per_hour: float
    equipment: tuple[EquipmentCfg, ...]
    queues: tuple[QueueCfg, ...]


@dataclass(frozen=True)
class ScenarioCfg:
    kind: str
    start_s: int
    duration_s: int
    equipment_id: str | None = None
    sensor_id: str | None = None
    gateway_id: str | None = None

    @property
    def key(self) -> str:
        if self.kind == "network_outage":
            return self.gateway_id
        if self.kind in SENSOR_KINDS:
            return f"{self.equipment_id}/{self.sensor_id}"
        return self.equipment_id


@dataclass(frozen=True)
class SimConfig:
    org_id: str
    org_name: str
    seed: int
    start_time: datetime
    duplicate_rate: float
    queue_sample_interval_s: int
    heartbeat_interval_s: int
    plants: dict
    areas: dict
    lines: tuple[LineCfg, ...]
    scenarios: tuple[ScenarioCfg, ...]


def _get(d: dict, key: str, ctx: str, typ, default=_MISSING):
    if not isinstance(d, dict):
        raise ConfigError(f"{ctx}: expected an object")
    if key not in d or d[key] is None:
        if default is _MISSING:
            raise ConfigError(f"{ctx}: missing required field '{key}'")
        return default
    v = d[key]
    if isinstance(v, bool) or not isinstance(v, typ):
        raise ConfigError(f"{ctx}: '{key}' has wrong type ({type(v).__name__})")
    return v


def _range(v, lo, hi, key, ctx, lo_open=False):
    bad = v <= lo if lo_open else v < lo
    if bad or (hi is not None and v > hi):
        raise ConfigError(f"{ctx}: '{key}'={v} out of range")
    return v


def _unique(seen: set, ident: str, what: str):
    if ident in seen:
        raise ConfigError(f"duplicate {what} id '{ident}'")
    seen.add(ident)


def _parse_sensor(d, eq_id, seen):
    ctx = f"sensor in {eq_id}"
    sid = _get(d, "id", ctx, str)
    _unique(seen, sid, "sensor")
    ctx = f"sensor {sid}"
    return SensorCfg(
        id=sid,
        name=_get(d, "name", ctx, str),
        unit=_get(d, "unit", ctx, str, ""),
        base=float(_get(d, "base", ctx, NUM)),
        noise=_range(float(_get(d, "noise", ctx, NUM, 0.0)), 0, None, "noise", ctx),
        producing_delta=float(_get(d, "producing_delta", ctx, NUM, 0.0)),
        sample_interval_s=int(_range(_get(d, "sample_interval_s", ctx, int, 10), 1, None, "sample_interval_s", ctx)),
    )


def _parse_equipment(d, seen_eq, seen_sensor):
    eid = _get(d, "id", "equipment", str)
    _unique(seen_eq, eid, "equipment")
    ctx = f"equipment {eid}"
    mtbf = _get(d, "mtbf_s", ctx, NUM, None)
    if mtbf is not None:
        _range(mtbf, 0, None, "mtbf_s", ctx, lo_open=True)
    return EquipmentCfg(
        id=eid,
        name=_get(d, "name", ctx, str),
        type=_get(d, "type", ctx, str, "generic"),
        standard_cycle_time_s=float(_range(_get(d, "standard_cycle_time_s", ctx, NUM), 0, None, "standard_cycle_time_s", ctx, lo_open=True)),
        cycle_time_cv=float(_range(_get(d, "cycle_time_cv", ctx, NUM, 0.05), 0, 1, "cycle_time_cv", ctx)),
        reject_rate=float(_range(_get(d, "reject_rate", ctx, NUM, 0.0), 0, 1, "reject_rate", ctx)),
        mtbf_s=None if mtbf is None else float(mtbf),
        mttr_s=float(_range(_get(d, "mttr_s", ctx, NUM, 300), 0, None, "mttr_s", ctx, lo_open=True)),
        sensors=tuple(_parse_sensor(s, eid, seen_sensor) for s in _get(d, "sensors", ctx, list, [])),
    )


def parse_config(raw: dict) -> SimConfig:
    sim = _get(raw, "simulation", "config", dict, {})
    org = _get(raw, "organization", "config", dict)
    try:
        start = datetime.fromisoformat(_get(sim, "start_time", "simulation", str, "2026-01-01T00:00:00Z").replace("Z", "+00:00"))
    except ValueError as exc:
        raise ConfigError(f"simulation: bad start_time ({exc})") from exc
    if start.tzinfo is None:
        start = start.replace(tzinfo=timezone.utc)

    plants, areas, lines = {}, {}, []
    seen = {k: set() for k in ("plant", "area", "line", "gateway", "equipment", "queue", "sensor")}
    for p in _get(raw, "plants", "config", list):
        pid = _get(p, "id", "plant", str)
        _unique(seen["plant"], pid, "plant")
        plants[pid] = _get(p, "name", f"plant {pid}", str)
        for a in _get(p, "areas", f"plant {pid}", list):
            aid = _get(a, "id", "area", str)
            _unique(seen["area"], aid, "area")
            areas[aid] = _get(a, "name", f"area {aid}", str)
            for ln in _get(a, "lines", f"area {aid}", list):
                lid = _get(ln, "id", "line", str)
                _unique(seen["line"], lid, "line")
                ctx = f"line {lid}"
                gw = _get(ln, "gateway_id", ctx, str)
                _unique(seen["gateway"], gw, "gateway")
                eq = tuple(_parse_equipment(e, seen["equipment"], seen["sensor"]) for e in _get(ln, "equipment", ctx, list))
                if not eq:
                    raise ConfigError(f"{ctx}: needs at least one equipment")
                qs = []
                for q in _get(ln, "queues", ctx, list, []):
                    qid = _get(q, "id", ctx, str)
                    _unique(seen["queue"], qid, "queue")
                    qs.append(QueueCfg(qid, _get(q, "name", ctx, str, qid),
                                       int(_range(_get(q, "capacity", ctx, int), 1, None, "capacity", ctx))))
                if len(qs) != len(eq) - 1:
                    raise ConfigError(f"{ctx}: {len(eq)} equipment need {len(eq) - 1} queues, got {len(qs)}")
                lines.append(LineCfg(lid, _get(ln, "name", ctx, str), pid, aid, gw,
                                     float(_get(ln, "target_per_hour", ctx, NUM, 0.0)), eq, tuple(qs)))

    sensors_of = {e.id: {s.id for s in e.sensors} for ln in lines for e in ln.equipment}
    scenarios = []
    for i, s in enumerate(_get(raw, "scenarios", "config", list, [])):
        ctx = f"scenario #{i}"
        kind = _get(s, "kind", ctx, str)
        if kind not in SCENARIO_KINDS:
            raise ConfigError(f"{ctx}: unknown kind '{kind}'")
        sc = ScenarioCfg(
            kind=kind,
            start_s=int(_range(_get(s, "start_s", ctx, int), 0, None, "start_s", ctx)),
            duration_s=int(_range(_get(s, "duration_s", ctx, int), 1, None, "duration_s", ctx)),
            equipment_id=_get(s, "equipment_id", ctx, str, None),
            sensor_id=_get(s, "sensor_id", ctx, str, None),
            gateway_id=_get(s, "gateway_id", ctx, str, None),
        )
        if kind == "network_outage":
            if sc.gateway_id not in seen["gateway"]:
                raise ConfigError(f"{ctx}: unknown gateway_id '{sc.gateway_id}'")
        else:
            if sc.equipment_id not in sensors_of:
                raise ConfigError(f"{ctx}: unknown equipment_id '{sc.equipment_id}'")
            if kind in SENSOR_KINDS and sc.sensor_id not in sensors_of[sc.equipment_id]:
                raise ConfigError(f"{ctx}: unknown sensor_id '{sc.sensor_id}' for {sc.equipment_id}")
        scenarios.append(sc)

    return SimConfig(
        org_id=_get(org, "id", "organization", str),
        org_name=_get(org, "name", "organization", str),
        seed=int(_get(sim, "seed", "simulation", int, 1)),
        start_time=start,
        duplicate_rate=float(_range(_get(sim, "duplicate_rate", "simulation", NUM, 0.0), 0, 1, "duplicate_rate", "simulation")),
        queue_sample_interval_s=int(_range(_get(sim, "queue_sample_interval_s", "simulation", int, 10), 1, None, "queue_sample_interval_s", "simulation")),
        heartbeat_interval_s=int(_range(_get(sim, "heartbeat_interval_s", "simulation", int, 30), 1, None, "heartbeat_interval_s", "simulation")),
        plants=plants, areas=areas, lines=tuple(lines), scenarios=tuple(scenarios),
    )


def load_config(path: str) -> SimConfig:
    with open(path, encoding="utf-8") as fh:
        return parse_config(json.load(fh))
