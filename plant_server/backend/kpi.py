"""KPI + downtime engine over derived state intervals (definitions: docs/architecture/DERIVATION.md).

Every number here is `calculated` from measured events and `detected` states (spec s53), never AI inference.
"""
from __future__ import annotations

from collections import Counter

from .derive import BLOCKED, BYPASSED, COMM, FAULT, IDLE, NO_DATA, OFF, PRODUCING, STARVED, UNKNOWN, Interval

DOWNTIME_STATES = (FAULT, BYPASSED, STARVED, BLOCKED, IDLE, OFF)
# Configurable mapping (spec s13). Causes are NOT guessed: a fault signal is 'Unknown' until someone assigns a reason.
DOWNTIME_CATEGORY = {FAULT: "Unknown (fault signal)", BYPASSED: "Production signal unavailable", STARVED: "Upstream issue",
                     BLOCKED: "Downstream issue", IDLE: "Unknown", OFF: "Off"}


def _ratio(a, b):
    return round(a / b, 4) if b else None


def state_seconds(intervals: list[Interval], t0: int, t1: int) -> Counter:
    c = Counter()
    for iv in intervals:
        c[iv.state] += max(0, min(iv.end, t1) - max(iv.start, t0))
    return c


def counter_at(store, equipment_id: str, t: int) -> dict:
    """Cumulative counters as last reported at/before t (cycle events and counter snapshots both carry them)."""
    e = store.last_before(["cycle", "counter"], t + 1, equipment_id=equipment_id)
    best = e["payload"].get("counters", e["payload"]) if e else {"total": 0, "good": 0, "reject": 0}
    return {k: best[k] for k in ("total", "good", "reject")}


def equipment_kpi(store, eq, intervals: list[Interval], t0: int, t1: int) -> dict:
    st = state_seconds(intervals, t0, t1)
    window = t1 - t0
    unobserved = st[COMM] + st[UNKNOWN] + st[NO_DATA]
    planned = window - unobserved  # 24x7 planned time (shifts/planned stops arrive with shift config)
    producing = st[PRODUCING]
    own_base = planned - st[STARVED] - st[BLOCKED]  # machine's own time: external waits excluded

    cyc = [e["payload"] for e in store.events(["cycle"], equipment_id=eq.id, t_min=t0, t_max=t1 - 1) if e["ts"] >= t0]
    n_obs = len(cyc)
    good_obs = sum(1 for c in cyc if c["result"] == "good")
    end, start = counter_at(store, eq.id, t1 - 1), counter_at(store, eq.id, t0 - 1)
    total, good, reject = (end[k] - start[k] for k in ("total", "good", "reject"))

    perf_raw = (eq.standard_cycle_time_s * n_obs / producing) if producing else None
    avail = _ratio(producing, planned)
    perf = None if perf_raw is None else round(min(1.0, perf_raw), 4)
    qual = _ratio(good_obs, n_obs)
    oee = round(avail * perf * qual, 4) if None not in (avail, perf, qual) else None
    return {
        "window_s": window, "observed_s": planned, "unobserved_s": unobserved,
        "state_s": {k: v for k, v in sorted(st.items()) if v},
        "count_total": total, "count_good": good, "count_reject": reject,
        "count_observed": n_obs, "count_unobserved": total - n_obs,
        "availability": avail, "availability_own": _ratio(producing, own_base),
        "performance": perf, "performance_raw": None if perf_raw is None else round(perf_raw, 4),
        "quality": qual, "oee": oee,
        "basis": "calculated",
    }


def line_kpi(store, line, derived: dict, t0: int, t1: int, margin: float = 0.05) -> dict:
    eqk = {e.id: equipment_kpi(store, e, derived[e.id], t0, t1) for e in line.equipment}
    last = eqk[line.equipment[-1].id]
    target = line.target_per_hour * (t1 - t0) / 3600.0
    # Bottleneck: the machine that almost never waits for material or space, among time it was able to work.
    util = {}
    for e in line.equipment:
        s = eqk[e.id]["state_s"]
        able = s.get(PRODUCING, 0) + s.get(STARVED, 0) + s.get(BLOCKED, 0) + s.get(IDLE, 0)
        util[e.id] = _ratio(s.get(PRODUCING, 0), able)
    ranked = sorted((u, i) for i, u in util.items() if u is not None)[::-1]
    clear = len(ranked) >= 2 and ranked[0][0] - ranked[1][0] >= margin
    return {
        "good_output": last["count_good"], "target": round(target, 1),
        "achievement": _ratio(last["count_good"], target),
        "bottleneck": {"candidate": ranked[0][1] if ranked else None, "clear": clear,
                       "utilization": util, "basis": "calculated",
                       "note": "highest share of able-to-work time spent producing; 'clear' needs a margin over the next machine"},
        "equipment": eqk,
    }


def downtime_events(intervals: list[Interval], min_s: int = 30) -> list[dict]:
    rows = []
    for iv in intervals:
        if iv.state in DOWNTIME_STATES and iv.duration >= min_s:
            rows.append({"equipment_id": iv.equipment_id, "start": iv.start, "end": iv.end, "duration_s": iv.duration,
                         "state": iv.state, "category": DOWNTIME_CATEGORY[iv.state],
                         "confidence": round(iv.confidence, 3), "evidence": iv.evidence})
    return rows
