"""State derivation: raw edge events -> equipment state intervals (batch over a time window).

Rules (see docs/architecture/DERIVATION.md). Precedence per second:
  COMMUNICATION_ERROR > OFF > FAULT > (running: PRODUCING | BYPASSED) > (stopped: STARVED | BLOCKED | IDLE)
Uses event time `ts` only, so late/buffered/duplicate delivery cannot change the result.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

from .topology import EquipmentT

PRODUCING, STARVED, BLOCKED, FAULT, BYPASSED = "PRODUCING", "STARVED", "BLOCKED", "FAULT", "BYPASSED"
IDLE, OFF, UNKNOWN, COMM = "IDLE", "OFF", "UNKNOWN", "COMMUNICATION_ERROR"
NO_DATA = "NO_DATA"  # the line gateway itself has been silent (network/gateway problem): state genuinely unknown


@dataclass(frozen=True)
class DeriveParams:
    bypass_factor: float = 2.0  # running with no completed cycle for > factor x standard cycle => bypass suspected
    stale_after_s: int = 90     # gateway silent for longer than this => NO_DATA (default heartbeat is 30 s)


@dataclass
class Interval:
    equipment_id: str
    state: str
    start: int  # epoch s, inclusive
    end: int    # epoch s, exclusive
    confidence: float
    evidence: str

    @property
    def duration(self) -> int:
        return self.end - self.start


def _fill(series: list, t0: int, n: int) -> list:
    """Piecewise-constant series from (ts, value) pairs sorted by ts; None before the first observation."""
    out, cur, j, m = [None] * n, None, 0, len(series)
    for i in range(n):
        t = t0 + i
        while j < m and series[j][0] <= t:
            cur = series[j][1]
            j += 1
        out[i] = cur
    return out


def _bracket(samples: list, t0: int, n: int):
    """For each second: level of the last sample at/before it and of the first sample after it."""
    prev, nxt, cur, j, m = [None] * n, [None] * n, None, 0, len(samples)
    for i in range(n):
        t = t0 + i
        while j < m and samples[j][0] <= t:
            cur = samples[j][1]
            j += 1
        prev[i] = cur
        nxt[i] = samples[j][1] if j < m else None
    return prev, nxt


def _bypass(is_run: list, cycles: list, t0: int, n: int, ct: float, factor: float):
    """Mark seconds where the machine reports running but completes no cycles.

    In a running segment the time between two cycle completions is ct if healthy and ct + L if bypassed for L seconds.
    The bypass boundaries are only known to within one cycle, so each is placed at the midpoint (unbiased estimate).
    """
    mask, conf = [False] * n, [0.0] * n
    i = 0
    while i < n:
        if not is_run[i]:
            i += 1
            continue
        j = i
        while j < n and is_run[j]:
            j += 1
        rs, re = t0 + i, t0 + j
        marks = sorted({rs, *[c for c in cycles if rs <= c < re]})
        for k, a in enumerate(marks):
            closed = k + 1 < len(marks)
            b = marks[k + 1] if closed else re
            if b - a > factor * ct:
                s = a + ct / 2.0
                e = b - ct / 2.0 if closed else float(b)
                c = 0.9 if (b - a) >= 3 * ct else 0.75
                for x in range(max(i, math.ceil(s) - t0), min(j, math.ceil(e) - t0)):
                    mask[x], conf[x] = True, c
        i = j
    return mask, conf


def derive_equipment(store, eq: EquipmentT, t0: int, t1: int, params: DeriveParams = DeriveParams()) -> list[Interval]:
    """Derive states over [t0, t1) without ever extrapolating beyond what the equipment's gateway has reported.

    Silence is not evidence of a bypass: past the gateway's last event the previous state is carried forward for
    `stale_after_s`, then NO_DATA. Buffered events that arrive later repair the history (see LiveEngine).
    """
    if t1 <= t0:
        return []
    h = store.max_ts(eq.gateway_id)
    if h is None:
        return [Interval(eq.id, UNKNOWN, t0, t1, 0.0, "no data yet")]
    end = min(t1, max(t0, h + 1))
    out = _derive_core(store, eq, t0, end, params) if end > t0 else []
    if end < t1:
        stale_from = max(end, h + 1 + params.stale_after_s)
        if out and stale_from > end:
            out[-1].end = min(stale_from, t1)          # carry forward briefly
        if stale_from < t1:
            out.append(Interval(eq.id, NO_DATA, min(stale_from, t1) if out else t0, t1, 0.0,
                                f"no data from gateway {eq.gateway_id} for more than {params.stale_after_s} s"))
    return out


def _derive_core(store, eq: EquipmentT, t0: int, t1: int, params: DeriveParams) -> list[Interval]:
    n = t1 - t0
    if n <= 0:
        return []
    pad = 3600
    sig = store.events(["signal"], equipment_id=eq.id, t_min=t0, t_max=t1)
    series = {name: [(e["ts"], e["payload"]["value"]) for e in sig if e["payload"]["name"] == name]
              for name in ("machine_on", "running", "fault")}
    on, run, fault = (_fill(series[k], t0, n) for k in ("machine_on", "running", "fault"))
    comm_ev = store.events(["comm_status"], equipment_id=eq.id, t_min=t0, t_max=t1)
    comm = _fill([(e["ts"], not e["payload"]["ok"]) for e in comm_ev], t0, n)
    cycles = [e["ts"] for e in store.events(["cycle"], equipment_id=eq.id, t_min=t0, t_max=t1 - 1) if t0 <= e["ts"] < t1]

    def queue(q):
        if q is None:
            return None
        return _bracket([(e["ts"], e["payload"]["level"]) for e in store.events(["queue"], subject=q.id, t_min=t0, t_max=t1 + pad)], t0, n)

    upq, dnq = queue(eq.up_queue), queue(eq.down_queue)

    is_run = [not comm[i] and on[i] != 0 and fault[i] != 1 and run[i] == 1 for i in range(n)]
    byp, byp_conf = _bypass(is_run, cycles, t0, n, eq.standard_cycle_time_s, params.bypass_factor)

    out: list[Interval] = []

    def put(i, state, conf, evid):
        t = t0 + i
        if out and out[-1].state == state and out[-1].end == t and out[-1].evidence == evid:
            last = out[-1]
            last.confidence = (last.confidence * (t - last.start) + conf) / (t + 1 - last.start)
            last.end = t + 1
        else:
            out.append(Interval(eq.id, state, t, t + 1, conf, evid))

    for i in range(n):
        if comm[i]:
            put(i, COMM, 1.0, "comm_status ok=false")
        elif on[i] is None or run[i] is None:
            put(i, UNKNOWN, 0.0, "no data yet")
        elif on[i] == 0:
            put(i, OFF, 1.0, "machine_on=0")
        elif fault[i] == 1:
            put(i, FAULT, 1.0, "fault=1")
        elif run[i] == 1:
            if byp[i]:
                put(i, BYPASSED, byp_conf[i], f"running=1, no completed cycle for > {params.bypass_factor:g}x standard cycle (signal inconsistency)")
            else:
                put(i, PRODUCING, 1.0, "running=1, cycles observed")
        else:
            up = [v[i] for v in upq if v[i] is not None] if upq else []
            dn = [v[i] for v in dnq if v[i] is not None] if dnq else []
            starved = bool(up) and min(up) <= 0
            blocked = bool(dn) and max(dn) >= eq.down_queue.capacity
            if starved and not blocked:
                put(i, STARVED, 0.9 if all(v <= 0 for v in up) and len(up) == 2 else 0.7, "running=0, upstream queue empty")
            elif blocked and not starved:
                put(i, BLOCKED, 0.9 if all(v >= eq.down_queue.capacity for v in dn) and len(dn) == 2 else 0.7, "running=0, downstream queue full")
            elif starved and blocked:
                put(i, IDLE, 0.4, "running=0, queue evidence conflicting")
            else:
                put(i, IDLE, 0.5, "running=0, no fault, queues do not explain")
    return out


def state_at_seconds(intervals: list[Interval], t0: int, t1: int) -> list:
    arr = [None] * max(0, t1 - t0)
    for iv in intervals:
        for t in range(max(iv.start, t0), min(iv.end, t1)):
            arr[t - t0] = iv.state
    return arr


def derive_all(store, topo, t0: int, t1: int, params: DeriveParams = DeriveParams()) -> dict[str, list[Interval]]:
    return {eid: derive_equipment(store, eq, t0, t1, params) for eid, eq in topo.equipment.items()}
