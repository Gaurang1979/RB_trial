"""Event contract: envelope validation + timestamp helpers (see docs/architecture/EVENT_MODEL.md)."""
from __future__ import annotations

from datetime import datetime, timezone

TS_FORMAT = "%Y-%m-%dT%H:%M:%SZ"


def parse_ts(s: str) -> int:
    """ISO-8601 UTC ('...Z', second resolution) -> epoch seconds."""
    return int(datetime.strptime(s, TS_FORMAT).replace(tzinfo=timezone.utc).timestamp())


def format_ts(t: int) -> str:
    return datetime.fromtimestamp(t, timezone.utc).strftime(TS_FORMAT)

EVENT_TYPES = ("signal", "cycle", "counter", "sensor", "queue", "heartbeat", "comm_status")
ENVELOPE = ("schema", "event_id", "seq", "gateway_id", "type", "ts", "emitted_ts", "buffered",
            "plant_id", "area_id", "line_id", "equipment_id", "payload")
PAYLOAD = {
    "signal": ("name", "value"),
    "cycle": ("cycle_time_s", "result", "counters"),
    "counter": ("total", "good", "reject"),
    "sensor": ("sensor_id", "name", "value", "unit", "quality"),
    "queue": ("queue_id", "level", "capacity"),
    "heartbeat": ("uptime_s", "buffered_events"),
    "comm_status": ("ok",),
}


def validate_event(ev: dict) -> list[str]:
    problems = [f"missing '{k}'" for k in ENVELOPE if k not in ev]
    if problems:
        return problems
    if ev["type"] not in EVENT_TYPES:
        return [f"unknown type '{ev['type']}'"]
    if not isinstance(ev["payload"], dict):
        return ["payload must be an object"]
    problems += [f"payload missing '{k}'" for k in PAYLOAD[ev["type"]] if k not in ev["payload"]]
    if ev["type"] in ("signal", "cycle", "counter", "sensor", "comm_status") and not ev["equipment_id"]:
        problems.append("equipment_id required for this type")
    if ev["event_id"] != f"{ev['gateway_id']}:{ev['seq']}":
        problems.append("event_id must equal '<gateway_id>:<seq>'")
    try:
        if parse_ts(ev["emitted_ts"]) < parse_ts(ev["ts"]):
            problems.append("emitted_ts earlier than ts")
    except (ValueError, TypeError):
        problems.append("ts/emitted_ts must be ISO-8601 UTC like 2026-01-01T00:00:00Z")
    if not isinstance(ev["payload"], dict):
        problems.append("payload must be an object")
    return problems
