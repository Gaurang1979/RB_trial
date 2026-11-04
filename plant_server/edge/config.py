"""Edge site configuration: which real equipment to poll, over which protocol, and how each raw point maps
to the spi.event.v1 signals/counters/sensors the backend already understands (see docs/architecture/EDGE.md).
Deliberately separate from simulator/config.py's schema (that one describes a *simulated* factory's physics;
this one describes *real* wiring) - the two meet only at the event contract in spi_common/schema.py.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field

DIGITAL, COUNTER, ANALOG = "digital", "counter", "analog"
POINT_KINDS = (DIGITAL, COUNTER, ANALOG)
COUNTER_ROLES = ("total", "reject")


class ConfigError(ValueError):
    pass


def _get(d, key, ctx, default="__required__"):
    if key not in d or d[key] is None:
        if default == "__required__":
            raise ConfigError(f"{ctx}: missing required field '{key}'")
        return default
    return d[key]


@dataclass(frozen=True)
class Point:
    kind: str
    address: dict
    signal: str | None = None          # digital: signal name ("machine_on", "running", "fault", or custom)
    role: str | None = None            # counter: "total" | "reject"
    sensor_id: str | None = None       # analog: sensor id as the backend knows it (e.g. "PRESS-01.TEMP")
    name: str = ""                     # analog: display name ("temperature")
    unit: str = ""
    scale: float = 1.0
    offset: float = 0.0
    sample_interval_s: int = 10        # analog only
    valid_range: tuple[float, float] | None = None


@dataclass(frozen=True)
class QueuePoint:
    queue_id: str
    capacity: int
    point: Point
    sample_interval_s: int = 10


@dataclass(frozen=True)
class EquipmentEdge:
    id: str
    protocol: str
    connection: dict
    poll_interval_s: float
    points: tuple[Point, ...]
    queue_after: QueuePoint | None = None
    comm_fail_threshold: int = 3       # consecutive failed polls before comm_status ok=false


@dataclass(frozen=True)
class LineEdge:
    id: str
    plant_id: str
    area_id: str
    gateway_id: str
    equipment: tuple[EquipmentEdge, ...]
    heartbeat_interval_s: int = 30
    token_file: str | None = None      # file holding the gateway's device token from the plant server (not the token itself)


@dataclass(frozen=True)
class EdgeConfig:
    org_id: str
    mqtt_host: str
    mqtt_port: int
    mqtt_prefix: str
    lines: tuple[LineEdge, ...]


def _parse_point(d: dict, ctx: str, require_sensor_id: bool = True) -> Point:
    kind = _get(d, "kind", ctx)
    if kind not in POINT_KINDS:
        raise ConfigError(f"{ctx}: unknown point kind '{kind}'; expected one of {POINT_KINDS}")
    address = _get(d, "address", ctx)
    if not isinstance(address, dict):
        raise ConfigError(f"{ctx}: 'address' must be an object")
    vr = d.get("valid_range")
    if vr is not None and (not isinstance(vr, list) or len(vr) != 2):
        raise ConfigError(f"{ctx}: 'valid_range' must be [min, max]")
    p = Point(kind=kind, address=address, signal=d.get("signal"), role=d.get("role"), sensor_id=d.get("sensor_id"),
             name=d.get("name", ""), unit=d.get("unit", ""), scale=float(d.get("scale", 1.0)), offset=float(d.get("offset", 0.0)),
             sample_interval_s=int(d.get("sample_interval_s", 10)), valid_range=tuple(vr) if vr else None)
    if kind == DIGITAL and not p.signal:
        raise ConfigError(f"{ctx}: digital point needs 'signal'")
    if kind == COUNTER and p.role not in COUNTER_ROLES:
        raise ConfigError(f"{ctx}: counter point needs 'role' in {COUNTER_ROLES}")
    if kind == ANALOG and require_sensor_id and not p.sensor_id:
        raise ConfigError(f"{ctx}: analog point needs 'sensor_id'")
    return p


def _parse_equipment(d: dict, seen: set) -> EquipmentEdge:
    eid = _get(d, "id", "equipment")
    if eid in seen:
        raise ConfigError(f"duplicate equipment id '{eid}'")
    seen.add(eid)
    ctx = f"equipment {eid}"
    points = tuple(_parse_point(p, f"{ctx} point #{i}") for i, p in enumerate(_get(d, "points", ctx)))
    if not points:
        raise ConfigError(f"{ctx}: needs at least one point")
    qa = d.get("queue_after")
    queue_after = None
    if qa:
        qctx = f"{ctx} queue_after"
        queue_after = QueuePoint(queue_id=_get(qa, "id", qctx), capacity=int(_get(qa, "capacity", qctx)),
                                 point=_parse_point(_get(qa, "level_point", qctx), f"{qctx} level_point", require_sensor_id=False),
                                 sample_interval_s=int(qa.get("sample_interval_s", 10)))
    return EquipmentEdge(id=eid, protocol=_get(d, "protocol", ctx), connection=_get(d, "connection", ctx, {}),
                         poll_interval_s=float(_get(d, "poll_interval_s", ctx, 1.0)), points=points, queue_after=queue_after,
                         comm_fail_threshold=int(d.get("comm_fail_threshold", 3)))


def parse_config(raw: dict) -> EdgeConfig:
    org = _get(raw, "organization", "config")
    mqtt = _get(raw, "mqtt", "config", {})
    lines, seen_line, seen_gw, seen_eq = [], set(), set(), set()
    for ld in _get(raw, "lines", "config"):
        lid = _get(ld, "id", "line")
        if lid in seen_line:
            raise ConfigError(f"duplicate line id '{lid}'")
        seen_line.add(lid)
        gw = _get(ld, "gateway_id", f"line {lid}")
        if gw in seen_gw:
            raise ConfigError(f"duplicate gateway id '{gw}'")
        seen_gw.add(gw)
        eq = tuple(_parse_equipment(e, seen_eq) for e in _get(ld, "equipment", f"line {lid}"))
        if not eq:
            raise ConfigError(f"line {lid}: needs at least one equipment")
        lines.append(LineEdge(id=lid, plant_id=_get(ld, "plant_id", f"line {lid}"), area_id=_get(ld, "area_id", f"line {lid}"),
                              gateway_id=gw, equipment=eq, heartbeat_interval_s=int(ld.get("heartbeat_interval_s", 30)),
                              token_file=ld.get("token_file")))
    return EdgeConfig(org_id=_get(org, "id", "organization"), mqtt_host=_get(mqtt, "host", "mqtt"),
                      mqtt_port=int(mqtt.get("port", 1883)), mqtt_prefix=mqtt.get("prefix", "spi/v1"), lines=tuple(lines))


def load_tokens(cfg: EdgeConfig) -> dict[str, str]:
    """{gateway_id: token} read from each line's token_file. A line without one gets no token, and the plant
    server refuses its messages; a token_file that cannot be read or is empty is a configuration error."""
    out = {}
    for ln in cfg.lines:
        if not ln.token_file:
            continue
        try:
            with open(ln.token_file, encoding="utf-8") as fh:
                token = fh.read().strip()
        except OSError as exc:
            raise ConfigError(f"line {ln.id}: cannot read token_file: {exc.strerror}") from None
        if not token:
            raise ConfigError(f"line {ln.id}: token_file {ln.token_file} is empty")
        out[ln.gateway_id] = token
    return out


def load_config(path: str) -> EdgeConfig:
    with open(path, encoding="utf-8") as fh:
        return parse_config(json.load(fh))
