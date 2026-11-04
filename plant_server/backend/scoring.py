"""Score derived states against the simulator truth log (simulator-vs-truth only; not plant validation)."""
from __future__ import annotations

from collections import Counter, defaultdict

from spi_common.schema import parse_ts

from .derive import state_at_seconds


def truth_seconds(truth_rows: list[dict], equipment_id: str, t0: int, t1: int):
    """Per-second true state, plus per-second flag: True when the gateway could not observe the machine."""
    changes = sorted((parse_ts(r["ts"]), r["state"]) for r in truth_rows if r["kind"] == "state" and r["equipment_id"] == equipment_id)
    n = t1 - t0
    states, cur, j = [None] * n, None, 0
    for i in range(n):
        while j < len(changes) and changes[j][0] <= t0 + i:
            cur = changes[j][1]
            j += 1
        states[i] = cur
    unobs = [False] * n
    for r in truth_rows:
        if r["kind"] == "scenario" and r["scenario"] == "device_comm_loss" and r["key"] == equipment_id:
            for t in range(max(parse_ts(r["start_ts"]), t0), min(parse_ts(r["end_ts"]), t1)):
                unobs[t - t0] = True
    return states, unobs


def score(derived: dict, truth_rows: list[dict], t0: int, t1: int) -> dict:
    conf = defaultdict(Counter)  # conf[truth][derived]
    per_eq = {}
    for eid, ivs in derived.items():
        ts_, unobs = truth_seconds(truth_rows, eid, t0, t1)
        ds = state_at_seconds(ivs, t0, t1)
        ok = tot = 0
        for tr, d, u in zip(ts_, ds, unobs):
            if tr is None or u:
                continue
            conf[tr][d] += 1
            tot += 1
            ok += tr == d
        per_eq[eid] = round(ok / tot, 4) if tot else None
    total = sum(sum(c.values()) for c in conf.values())
    correct = sum(c[s] for s, c in conf.items())
    per_state = {}
    derived_totals = Counter()
    for c in conf.values():
        derived_totals.update(c)
    for s, c in sorted(conf.items()):
        truth_n = sum(c.values())
        per_state[s] = {"truth_s": truth_n, "recall": round(c[s] / truth_n, 4),
                        "precision": round(c[s] / derived_totals[s], 4) if derived_totals[s] else None}
    return {"observable_seconds": total, "agreement": round(correct / total, 4) if total else None,
            "per_state": per_state, "per_equipment": per_eq,
            "confusion": {t: dict(c) for t, c in sorted(conf.items())}}
