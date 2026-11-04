"""Rule-based 'attention' items for a line: what is wrong, where, how serious, why, what to check (spec s60).

Everything is derived from detected states and standard cycle times (basis: calculated). Nothing is guessed:
causes are limited to what the evidence supports, and wording is neutral (no blame on operators).
"""
from __future__ import annotations

from .derive import BLOCKED, BYPASSED, FAULT, NO_DATA, STARVED


def _mins(s: int) -> str:
    return f"{s // 60} min" if s >= 120 else f"{s} s"


def attention(line, states: dict, kpi: dict | None = None, min_stop_s: int = 60, min_wait_s: int = 120) -> list[dict]:
    """states: {equipment_id: {"state", "duration_s"}} for the line's equipment."""
    items, eqs = [], list(line.equipment)
    silent = [e for e in eqs if states.get(e.id, {}).get("state") == NO_DATA]
    if silent:
        d = max(states[e.id]["duration_s"] for e in silent)
        items.append({"severity": "medium", "equipment_id": None, "title": f"No data from the line gateway for {_mins(d)}",
                      "why": "network or gateway problem; machine states are unknown, not stopped", "effects": [],
                      "action": "Check gateway power and network. Buffered data will fill the gap when the link returns",
                      "estimated_loss_units": 0, "estimate_basis": "not estimated: production during the gap is unknown until data arrives"})
    names = {e.id: e.name for e in eqs}
    for i, e in enumerate(eqs):
        st = states.get(e.id)
        if not st:
            continue
        s, d = st["state"], st["duration_s"]
        if s in (FAULT, BYPASSED) and d >= min_stop_s:
            starved = [x.name for x in eqs[i + 1:] if states.get(x.id, {}).get("state") == STARVED]
            blocked = [x.name for x in eqs[:i] if states.get(x.id, {}).get("state") == BLOCKED]
            effects = ([f"downstream starved: {', '.join(starved)}"] if starved else []) + ([f"upstream blocked: {', '.join(blocked)}"] if blocked else [])
            loss = int(d // e.standard_cycle_time_s)
            if s == FAULT:
                title, sev, why = f"{e.name} stopped for {_mins(d)}", "high", "fault signal active"
                action = f"Inspect {e.name}; record the downtime reason"
            else:
                title, sev, why = f"{e.name}: production signal unavailable for {_mins(d)}", "medium", "running signal on but no cycles completed (signal inconsistency)"
                action = f"Check the cycle sensor / counter signal on {e.name}"
            items.append({"severity": sev, "equipment_id": e.id, "title": title, "why": why, "effects": effects, "action": action,
                          "estimated_loss_units": loss, "estimate_basis": f"up to {loss} units at the {e.standard_cycle_time_s:g} s standard cycle (calculated)"})
        elif s in (STARVED, BLOCKED) and d >= min_wait_s:
            rng = range(i - 1, -1, -1) if s == STARVED else range(i + 1, len(eqs))
            root = None
            for j in rng:
                sj = states.get(eqs[j].id, {}).get("state")
                if sj in (FAULT, BYPASSED):
                    root = eqs[j]
                    break
                if sj not in (STARVED, BLOCKED):
                    break
            if root is None:
                items.append({"severity": "low", "equipment_id": e.id,
                              "title": f"{e.name} {'waiting for material' if s == STARVED else 'waiting for downstream space'} for {_mins(d)}",
                              "why": "queue evidence; no upstream/downstream stop found" , "effects": [],
                              "action": "Check material flow around this machine", "estimated_loss_units": int(d // e.standard_cycle_time_s),
                              "estimate_basis": "calculated"})
    if kpi and kpi.get("achievement") is not None and kpi.get("target", 0) > 0 and kpi["achievement"] < 0.9:
        cand = kpi["bottleneck"]["candidate"] if kpi["bottleneck"]["clear"] else None
        items.append({"severity": "medium", "equipment_id": cand, "title": f"Line output {round((1 - kpi['achievement']) * 100)}% below target",
                      "why": f"{kpi['good_output']} good units vs target {kpi['target']} in the KPI window",
                      "effects": [], "action": (f"Look first at {names[cand]} (line constraint)" if cand else "Review the largest losses on this line"),
                      "estimated_loss_units": max(0, int(kpi["target"] - kpi["good_output"])), "estimate_basis": "target minus actual (calculated)"})
    order = {"high": 0, "medium": 1, "low": 2}
    return sorted(items, key=lambda x: order[x["severity"]])
