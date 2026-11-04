"""Print grades on the plant server (Req 17.3 PQ-01..05; docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 3).

The line grades one part in N (Read code tool, PR #70) and sends the grades in the inspection result's `values` map, keyed by
the tool's name: `<tool>.grade` (the worst overall grade of the part's codes, 4 = A .. 0 = F), `<tool>.grade.<parameter>`,
`<tool>.symbol_contrast_pct`, `<tool>.modulation`, `<tool>.grade.ungraded` (codes that could not be graded) and, when the
station sends it, `<tool>.grade_min` (the tool's lowest grade). No change to the plant event schema.

  keep()        one vision_grade row per graded tool of a stored result, in the result's savepoint (ingest and backfill)
  trend()       the grade over time in buckets per station and tool, for the dashboard and the mobile app
  status()      the drift status of each station and tool from its latest samples
  conditions()  the `vision_grade_drift` alert conditions (vision/alerts.py runs them on the core alert lifecycle)

Drift: the mean of the latest `window` samples (an ungraded sample counts as F: doubt is never read as good) within `margin`
of the lowest grade, or falling fast enough (least squares over the samples) to reach the lowest grade within the next
`window` samples. The alarm only warns; the line's own lowest-grade check still decides each graded part (PQ-05).
"""
from __future__ import annotations

import json
import math

from ..alerts import Cond

MAX_TOOL = 128
MAX_BUCKETS = 2000
TOP = 4.0     # A
RULE_DEFAULTS = {"lowest_grade": 2.0, "margin": 0.5, "window": 20, "min_samples": 10, "max_age_s": 8 * 3600}


def letter(g: float) -> str:
    return "FDCBA"[int(min(TOP, max(0.0, math.floor(g + 1e-9))))]


def _grade(v) -> float | None:
    return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) and 0 <= v <= TOP else None


def grades_of(values) -> dict[str, dict]:
    """tool -> {grade, ungraded, grade_min, symbol_contrast_pct, modulation, params} for each tool with a grade or an ungraded
    count in `values`. A grade outside 0..4 is not a grade: the part counts as ungraded for that tool."""
    if not isinstance(values, dict):
        return {}
    tools = {k[:-len(".grade")] for k in values if k.endswith(".grade")} | \
            {k[:-len(".grade.ungraded")] for k in values if k.endswith(".grade.ungraded")}
    out = {}
    for t in tools:
        if not t or len(t) > MAX_TOOL or "\x00" in t:
            continue
        g = _grade(values.get(t + ".grade"))
        ungraded = values.get(t + ".grade.ungraded", 0)
        ungraded = int(ungraded) if isinstance(ungraded, (int, float)) and ungraded > 0 else 0
        if g is None and t + ".grade" in values:
            ungraded = max(ungraded, 1)
        pre = t + ".grade."
        params = {k[len(pre):]: float(v) for k, v in values.items() if k.startswith(pre) and k != t + ".grade.ungraded"}
        sc, mod = values.get(t + ".symbol_contrast_pct"), values.get(t + ".modulation")
        out[t] = {"grade": g, "ungraded": ungraded, "grade_min": _grade(values.get(t + ".grade_min")),
                  "symbol_contrast_pct": float(sc) if sc is not None else None, "modulation": float(mod) if mod is not None else None,
                  "params": params}
    return out


def keep(store, did: str, queue: str, seq: int, el: dict) -> None:
    """Runs inside the stored event's savepoint (vision/ingest._keep_result), so grades are never kept without their result."""
    for tool, g in grades_of(el.get("values")).items():
        store._x("INSERT INTO vision_grade(device_id, queue_id, seq, tool, plant_id, line_id, ts, grade, ungraded, grade_min, symbol_contrast_pct, "
                 "modulation, params) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(device_id, queue_id, seq, tool) DO NOTHING",
                 (did, queue, seq, tool, el["plant"], el["line"], el["ts"], g["grade"], g["ungraded"], g["grade_min"], g["symbol_contrast_pct"],
                  g["modulation"], json.dumps(g["params"], separators=(",", ":"))))


def rule_params(rule: dict | None) -> dict:
    return {k: (rule or {}).get(k, v) for k, v in RULE_DEFAULTS.items()}


def check_rule(r: dict) -> None:
    """Refuses a vision_grade_drift rule whose numbers make no sense (alerts.normalize_rule calls it)."""
    p = rule_params(r)
    num = lambda v: isinstance(v, (int, float)) and not isinstance(v, bool)   # noqa: E731
    if not (num(p["lowest_grade"]) and 0 < p["lowest_grade"] <= TOP):
        raise ValueError(f"alert rule {r['id']!r}: lowest_grade must be a grade above 0 (F) and at most 4 (A)")
    if not (num(p["margin"]) and 0 <= p["margin"] <= TOP):
        raise ValueError(f"alert rule {r['id']!r}: margin must be 0 to 4 grades")
    for k in ("window", "min_samples", "max_age_s"):
        if not isinstance(p[k], int) or isinstance(p[k], bool) or p[k] < 2:
            raise ValueError(f"alert rule {r['id']!r}: {k} must be a whole number of 2 or more")
    if p["min_samples"] > p["window"]:
        raise ValueError(f"alert rule {r['id']!r}: min_samples must not be more than window")


def drift(samples: list[tuple[float | None, float | None]], rule: dict | None = None) -> dict:
    """`samples`: (grade, grade_min) oldest first, a grade of None being ungraded. The station's own lowest grade (its latest)
    wins over the rule's."""
    p = rule_params(rule)
    gs = [0.0 if g is None else g for g, _ in samples]
    mins = [m for _, m in samples if m is not None]
    lowest = mins[-1] if mins else float(p["lowest_grade"])
    warn = lowest + p["margin"]
    n = len(gs)
    out = {"samples": n, "lowest": lowest, "lowest_from": "station" if mins else "rule", "warn_at": warn, "mean": None, "slope": None,
           "projected": None, "ungraded": sum(1 for g, _ in samples if g is None), "state": "too_few", "met": False, "near": False}
    if n == 0:
        return out
    mean = sum(gs) / n
    xm = (n - 1) / 2
    sxx = sum((i - xm) ** 2 for i in range(n))
    slope = sum((i - xm) * (g - mean) for i, g in enumerate(gs)) / sxx if sxx else 0.0
    projected = mean + slope * ((n - 1) - xm + p["window"])
    out.update(mean=round(mean, 3), slope=round(slope, 4), projected=round(projected, 3))
    if n < p["min_samples"]:
        return out
    met = mean <= warn or (slope < 0 and projected <= lowest)
    out.update(state="drift" if met else "ok", met=met, near=met or mean <= warn + p["margin"] / 2)
    return out


def _latest(store, max_age_ms_ago: int, window: int) -> dict[tuple, list]:
    """(plant, line, station, tool) -> its latest `window` samples, oldest first, for every tool graded since then."""
    with store._lock:
        keys = store._x("SELECT DISTINCT plant_id, line_id, device_id, tool FROM vision_grade WHERE ts >= ?", (max_age_ms_ago,)).fetchall()
        out = {}
        for plant, line, did, tool in keys:
            rows = store._x("SELECT ts, grade, grade_min FROM vision_grade WHERE device_id = ? AND tool = ? AND ts >= ? "
                            "ORDER BY ts DESC, seq DESC LIMIT ?", (did, tool, max_age_ms_ago, window)).fetchall()
            out[(plant, line, did, tool)] = rows[::-1]
    return out


def status(store, now_ms: int, rule: dict | None = None, can_view=None) -> list[dict]:
    p = rule_params(rule)
    out = []
    for (plant, line, did, tool), rows in sorted(_latest(store, now_ms - p["max_age_s"] * 1000, p["window"]).items()):
        if can_view is not None and not can_view(plant, line):
            continue
        d = drift([(g, m) for _, g, m in rows], rule)
        out.append({"plant": plant, "line": line, "station": did, "tool": tool, "last_ts": rows[-1][0],
                    **{k: v for k, v in d.items() if k not in ("met", "near")}})
    return out


def conditions(rule: dict, store, now: int, names: dict | None = None, active: set | None = None, stations: dict | None = None) -> list[Cond]:
    """`now` in seconds (the server's wall clock); the other arguments as for vision/alerts.conditions."""
    names, stations = names or {}, stations or {}
    p = rule_params(rule)
    out = []
    for (plant, line, did, tool), rows in sorted(_latest(store, (now - p["max_age_s"]) * 1000, p["window"]).items()):
        if active is not None and (plant, line, did) not in active:
            continue
        d = drift([(g, m) for _, g, m in rows], rule)
        if not d["near"]:
            continue
        lname, sname = names.get(line) or line, stations.get(did) or did
        lo = f"{letter(d['lowest'])} ({d['lowest']:g}, {'set at the station' if d['lowest_from'] == 'station' else 'the rule'})"
        detail = (f"Mean grade {d['mean']:.2f} ({letter(d['mean'])}) over the last {d['samples']} graded parts"
                  + (f", {d['ungraded']} of them not gradable (counted as F)" if d["ungraded"] else "")
                  + f"; trend {d['slope']:+.3f} grade per part, {d['projected']:.2f} expected in {p['window']} more. Lowest grade {lo}. "
                  "Check the printer (print head, ink, ribbon, focus) and the code's contrast before parts fail; "
                  "the line still judges each graded part on its own lowest grade.")
        out.append(Cond(rule, f"{plant}/{line}/{did}/{tool}", f"{lname} {sname} {tool}: print grade drifting toward {letter(d['lowest'])}", detail,
                        f"mean grade within {p['margin']:g} of the lowest grade, or falling to it within {p['window']} parts", line, plant, None,
                        {"station": did, "tool": tool, "samples": d["samples"], "mean": d["mean"], "slope": d["slope"], "projected": d["projected"],
                         "lowest": d["lowest"], "lowest_from": d["lowest_from"], "warn_at": d["warn_at"], "ungraded": d["ungraded"],
                         "last_ts_ms": rows[-1][0]},
                        trigger=d["met"]))
    return out


def trend(store, since: int, until: int, bucket_s: int, plant: str | None = None, line: str | None = None, station: str | None = None,
          tool: str | None = None, can_view=None) -> list[dict]:
    """One series per (plant, line, station, tool): buckets of `bucket_s` seconds from `since` to `until` (ms), each with the samples, the
    mean and worst grade of the graded ones, the ungraded count and the mean symbol contrast and modulation. Empty buckets are left out."""
    b = bucket_s * 1000
    where, args = ["ts >= ?", "ts < ?"], [since, until]
    for col, v in (("plant_id", plant), ("line_id", line), ("device_id", station), ("tool", tool)):
        if v is not None:
            where.append(f"{col} = ?")
            args.append(v)
    with store._lock:
        rows = store._x("SELECT plant_id, line_id, device_id, tool, (ts - ?) / ? AS bk, COUNT(*), AVG(grade), MIN(grade), "
                        "SUM(CASE WHEN grade IS NULL THEN 1 ELSE 0 END), AVG(symbol_contrast_pct), AVG(modulation) FROM vision_grade "
                        f"WHERE {' AND '.join(where)} GROUP BY plant_id, line_id, device_id, tool, bk ORDER BY plant_id, line_id, device_id, tool, bk",
                        (since, b, *args)).fetchall()
    series: dict[tuple, dict] = {}
    for pl, ln, did, tl, bk, n, mean, worst, ung, sc, mod in rows:
        if can_view is not None and not can_view(pl, ln):
            continue
        s = series.setdefault((pl, ln, did, tl), {"plant": pl, "line": ln, "station": did, "tool": tl, "buckets": []})
        r = lambda v, d=3: None if v is None else round(float(v), d)   # noqa: E731
        s["buckets"].append({"start": since + int(bk) * b, "samples": n, "mean": r(mean), "worst": r(worst), "ungraded": int(ung),
                             "symbol_contrast_pct": r(sc, 2), "modulation": r(mod, 2)})
    return list(series.values())
