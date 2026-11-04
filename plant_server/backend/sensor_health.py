"""Sensor-health detection (spec s16). Judges a sensor only from what the evidence supports.

Statuses (basis: detected):
  OK               readings arrive and look plausible
  STUCK            identical readings for >= stuck_after_s while the machine went through producing / not-producing changes
  IMPOSSIBLE       readings outside the sensor's configured valid range
  OFFLINE          no readings although the gateway is alive and the machine is reachable
  GATEWAY_SILENT   the line gateway is silent: sensor state unknown, NOT a sensor fault
  UNREACHABLE      the equipment itself is unreachable (communication error): not a sensor fault
  NO_READINGS      never reported
A constant reading while the process is also stable is not flagged (a stable process can read constantly).
"""
from __future__ import annotations

from dataclasses import dataclass

from .derive import COMM, PRODUCING

OK, STUCK, IMPOSSIBLE, OFFLINE = "OK", "STUCK", "IMPOSSIBLE", "OFFLINE"
GATEWAY_SILENT, UNREACHABLE, NO_READINGS = "GATEWAY_SILENT", "UNREACHABLE", "NO_READINGS"
FAULTS = (STUCK, IMPOSSIBLE, OFFLINE)


@dataclass(frozen=True)
class SensorParams:
    stuck_after_s: int = 600
    min_samples: int = 5
    offline_factor: float = 5.0   # silent for > factor x sample interval ...
    min_offline_s: int = 60       # ... and at least this long
    stale_after_s: int = 90       # gateway silent longer than this: not the sensor's fault
    max_lookback_s: int = 86400   # how far back a constant run is followed


def _out_of_range(v, s) -> bool:
    return (s.valid_min is not None and v < s.valid_min) or (s.valid_max is not None and v > s.valid_max)


def assess_sensor(store, eq, sensor, now: int, params: SensorParams = SensorParams()) -> dict:
    base = {"sensor_id": sensor.id, "equipment_id": eq.id, "name": sensor.name, "unit": sensor.unit, "basis": "detected",
            "severity": "none", "since": None, "duration_s": 0, "last_valid": None, "current": None, "confidence": 1.0}
    h = store.max_ts(eq.gateway_id)
    if h is None or now - h > params.stale_after_s:
        return {**base, "status": GATEWAY_SILENT, "reason": "line gateway silent: sensor state unknown (not a sensor fault)", "confidence": 0.0}
    ivs = store.intervals(eq.id, now, now + 1, clip=False)
    if ivs and ivs[-1].state == COMM:
        return {**base, "status": UNREACHABLE, "reason": "equipment unreachable (communication error): not a sensor fault", "confidence": 0.0}
    window = 2 * params.stuck_after_s + 3 * sensor.sample_interval_s
    rd = store.events(["sensor"], subject=sensor.id, t_min=now - window, t_max=now)
    if not rd:
        last = store.last_before(["sensor"], now + 1, subject=sensor.id)
        rd = [last] if last else []
    if not rd:
        return {**base, "status": NO_READINGS, "reason": "no readings received yet", "confidence": 0.0}
    vals = [(e["ts"], e["payload"]["value"]) for e in rd]
    last_ts, last_v = vals[-1]
    cur = {"ts": last_ts, "value": last_v}

    gap = h - last_ts
    if gap > max(params.offline_factor * sensor.sample_interval_s, params.min_offline_s):
        return {**base, "status": OFFLINE, "severity": "medium", "since": last_ts + sensor.sample_interval_s, "duration_s": gap,
                "last_valid": cur, "current": None, "reason": f"no reading for {gap} s while the gateway is reporting"}

    if _out_of_range(last_v, sensor) and sum(_out_of_range(v, sensor) for _, v in vals[-3:]) >= min(2, len(vals)):
        i = len(vals) - 1
        while i > 0 and _out_of_range(vals[i - 1][1], sensor):
            i -= 1
        valid = next(((t, v) for t, v in reversed(vals[:i]) if not _out_of_range(v, sensor)), None)
        return {**base, "status": IMPOSSIBLE, "severity": "high", "since": vals[i][0], "duration_s": last_ts - vals[i][0],
                "last_valid": {"ts": valid[0], "value": valid[1]} if valid else None, "current": cur,
                "reason": f"reading {last_v:g} is outside the valid range [{sensor.valid_min}, {sensor.valid_max}]"}

    i = len(vals) - 1
    while i > 0 and abs(vals[i - 1][1] - last_v) <= sensor.stuck_epsilon:
        i -= 1
    while i == 0 and window < params.max_lookback_s:      # the flat run reaches the edge of the window: follow it back to its real start
        window *= 2
        more = [(e["ts"], e["payload"]["value"]) for e in store.events(["sensor"], subject=sensor.id, t_min=now - window, t_max=now)]
        if len(more) <= len(vals):
            break                                         # no earlier data exists
        vals = more
        i = len(vals) - 1
        while i > 0 and abs(vals[i - 1][1] - last_v) <= sensor.stuck_epsilon:
            i -= 1
    run_start, run_len = vals[i][0], len(vals) - i
    if run_len >= params.min_samples and last_ts - run_start >= params.stuck_after_s:
        states = {iv.state == PRODUCING for iv in store.intervals(eq.id, run_start, now + 1)}
        if len(states) == 2:   # producing AND not producing during the flat run: the process changed, the sensor did not
            return {**base, "status": STUCK, "severity": "medium", "since": run_start, "duration_s": last_ts - run_start,
                    "last_valid": {"ts": run_start, "value": vals[i][1]}, "current": cur,
                    "reason": f"reading constant at {last_v:g} for {(last_ts - run_start) // 60} min while the machine changed state",
                    "confidence": 0.9}
        return {**base, "status": OK, "current": cur, "flag": "constant reading but the process was stable: not flagged",
                "reason": "constant while process stable"}
    return {**base, "status": OK, "current": cur, "reason": "readings look plausible"}


def assess_all(store, topo, now: int, line_id: str | None = None, params: SensorParams = SensorParams()) -> list[dict]:
    out = []
    for eq in topo.equipment.values():
        if line_id and eq.line_id != line_id:
            continue
        for s in eq.sensors:
            out.append(assess_sensor(store, eq, s, now, params))
    return out
