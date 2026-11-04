"""Judge a soak from sample.py's per-minute record (tools/soak/README.md).

report.py <samples.csv> [warmup_minutes=5]

Prints the per-minute table as Markdown, then for each resource the least-squares slope after
warm-up (per hour) and the mean of the first and last third of that window, and the verdict:
- flat: no resource grows (slope projected over an hour under the limit below, and the last third
  not above the first by more than the noise allowance);
- no part missed, timed out or later than one part period (267 ms at 225/min);
- the resource guard at Normal with no restart recommended and no alarm, every minute.
Verdicts against the reference (no OK that should be NG) are analyze.py's.
"""
import csv, sys

# growth allowed per hour after warm-up, and the last-third vs first-third allowance
LIMITS = {"working_set_mb": (50, 25), "managed_heap_mb": (20, 15), "threads": (4, 4), "fds": (10, 6),
          "results_db_mb": (None, None), "ng_store_mb": (None, None)}


def slope(xs, ys):
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx if sxx else 0.0


def main():
    rows = list(csv.DictReader(open(sys.argv[1])))
    warm = int(sys.argv[2]) if len(sys.argv) > 2 else 5
    show = ["minute", "working_set_mb", "managed_heap_mb", "gc_committed_mb", "gen0", "gen1", "gen2", "threads", "fds",
            "results_db_mb", "ng_store_mb", "pressure", "alarms", "parts", "missed", "timeout", "late", "p50_ms", "p99_ms", "load1"]
    print("| " + " | ".join(show) + " |")
    print("|" + "---|" * len(show))
    for r in rows:
        print("| " + " | ".join(str(r.get(k, "")) for k in show) + " |")
    print()
    body = [r for r in rows if int(r["minute"]) > warm]
    ok = True
    if len(body) < 6:
        print(f"too short: {len(body)} minutes after a {warm}-minute warm-up")
        sys.exit(2)
    for k, (per_h, allow) in LIMITS.items():
        pts = [(int(r["minute"]), float(r[k])) for r in body if r.get(k) not in ("", None)]
        if len(pts) < 6:
            print(f"{k}: no data")
            continue
        xs, ys = zip(*pts)
        s = slope(xs, ys) * 60
        third = max(1, len(ys) // 3)
        first, last = sum(ys[:third]) / third, sum(ys[-third:]) / third
        verdict = "recorded"
        if per_h is not None:
            grows = s > per_h and last - first > allow
            verdict = "GROWS" if grows else "flat"
            ok &= not grows
        print(f"{k}: slope {s:+.1f}/h, first third {first:.1f}, last third {last:.1f}, min {min(ys):.1f}, max {max(ys):.1f}: {verdict}")
    tot = lambda k: sum(int(r[k] or 0) for r in rows)
    parts, missed, timeout, late = tot("parts"), tot("missed"), tot("timeout"), tot("late")
    print(f"parts {parts} (both cameras), NG {tot('ng')}, missed {missed}, timed out {timeout}, late {late}")
    ok &= missed == 0 and timeout == 0 and late == 0
    bad_guard = [r["minute"] for r in rows if r["pressure"] not in ("Normal", "NORMAL") or r["restart_recommended"] not in ("False", "false", "")
                 or r["alarms"] not in ("0", "")]
    print("resource guard Normal, no restart recommended, no alarm: every minute" if not bad_guard
          else f"resource guard or alarms not clear in minutes {', '.join(bad_guard)}")
    ok &= not bad_guard
    p99 = [float(r["p99_ms"]) for r in rows if r["p99_ms"]]
    if p99:
        print(f"trigger to Done p99 per minute: {min(p99):.1f} to {max(p99):.1f} ms")
    print("PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
