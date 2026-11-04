"""CLI pipeline: python -m backend --config configs/demo_factory.json --events out/events.jsonl [--truth ...] [--db ...]"""
from __future__ import annotations

import argparse
import json
import sys

from spi_common.schema import format_ts

from .derive import DeriveParams, derive_all
from .ingest import gaps, ingest_jsonl
from .kpi import downtime_events, line_kpi
from .scoring import score
from .store import open_store
from .topology import load_topology


def build_report(store: Store, topo, t0: int, t1: int, params: DeriveParams, min_downtime_s: int) -> tuple[dict, dict]:
    derived = derive_all(store, topo, t0, t1, params)
    downtime = {}
    for eid, ivs in derived.items():
        store.replace_intervals(eid, ivs)
        downtime[eid] = downtime_events(ivs, min_downtime_s)
        store.replace_downtime(eid, downtime[eid])
    store.commit()
    lines = {}
    for lid, line in topo.lines.items():
        k = line_kpi(store, line, derived, t0, t1)
        lines[lid] = {
            "good_output": k["good_output"], "target": k["target"], "achievement": k["achievement"],
            "bottleneck": {"candidate": k["bottleneck"]["candidate"], "clear": k["bottleneck"]["clear"]},
            "equipment": {eid: {"availability": e["availability"], "performance": e["performance"], "quality": e["quality"],
                                "oee": e["oee"], "count_total": e["count_total"], "unobserved_s": e["unobserved_s"],
                                "downtime_events": len(downtime[eid])}
                          for eid, e in k["equipment"].items()},
        }
    return {"window": [format_ts(t0), format_ts(t1)], "lines": lines}, derived


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="backend", description="Ingest edge events, derive states, compute KPIs (synthetic data unless real events are supplied)")
    ap.add_argument("--config", required=True, help="site JSON (topology)")
    ap.add_argument("--events", required=True, help="events JSONL")
    ap.add_argument("--truth", help="simulator truth JSONL: adds an accuracy score (simulator-vs-truth only)")
    ap.add_argument("--db", default=":memory:", help="path.db or postgresql://user:pw@host/db")
    ap.add_argument("--bypass-factor", type=float, default=2.0)
    ap.add_argument("--min-downtime", type=int, default=30, help="seconds")
    args = ap.parse_args(argv)
    try:
        topo = load_topology(args.config)
        store = open_store(args.db)
        rep = ingest_jsonl(store, args.events)
    except (OSError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    rng = store.time_range()
    if rng is None:
        print("error: no valid events ingested", file=sys.stderr)
        return 2
    t0, t1 = rng[0], rng[1] + 1
    report, derived = build_report(store, topo, t0, t1, DeriveParams(args.bypass_factor), args.min_downtime)
    report["ingest"] = {"accepted": rep.accepted, "duplicates": rep.duplicates, "rejected": rep.rejected, "reject_reasons": rep.reject_reasons,
                        "sequence_gaps": {g: r for g, r in gaps(store).items()}}
    if args.truth:
        with open(args.truth, encoding="utf-8") as fh:
            truth = [json.loads(x) for x in fh if x.strip()]
        sc = score(derived, truth, t0, t1)
        report["accuracy_vs_simulator_truth"] = {"agreement": sc["agreement"], "observable_seconds": sc["observable_seconds"],
                                                 "per_state": sc["per_state"]}
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
