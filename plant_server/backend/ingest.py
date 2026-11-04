"""Ingestion: validate, dedupe on event_id, quarantine invalid events, detect sequence gaps."""
from __future__ import annotations

import json
from dataclasses import dataclass, field

from spi_common.schema import parse_ts, validate_event

from .store import Store


@dataclass
class IngestReport:
    accepted: int = 0
    duplicates: int = 0
    rejected: int = 0
    reject_reasons: dict = field(default_factory=dict)
    min_ts: int | None = None  # event-time range of *accepted* events (drives live re-derivation)
    max_ts: int | None = None


def ingest_events(store: Store, events, report: IngestReport | None = None) -> IngestReport:
    """Idempotent: re-ingesting the same events changes nothing. Order of arrival does not matter."""
    rep = report or IngestReport()
    valid = []
    for ev in events:
        problems = validate_event(ev) if isinstance(ev, dict) else ["not a JSON object"]
        if problems:
            reason = "; ".join(problems)
            store.add_rejected(ev, reason)
            rep.rejected += 1
            rep.reject_reasons[reason] = rep.reject_reasons.get(reason, 0) + 1
        else:
            valid.append(ev)
    if valid:
        new = store.add_events(valid)
        rep.accepted += new
        rep.duplicates += len(valid) - new
        ts = [parse_ts(e["ts"]) for e in valid]
        rep.min_ts = min(ts) if rep.min_ts is None else min(rep.min_ts, min(ts))
        rep.max_ts = max(ts) if rep.max_ts is None else max(rep.max_ts, max(ts))
    store.commit()
    return rep


def ingest_jsonl(store: Store, path: str) -> IngestReport:
    def rows():
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line:
                    try:
                        yield json.loads(line)
                    except json.JSONDecodeError:
                        yield line  # quarantined as 'not a JSON object'
    return ingest_events(store, rows())


def gaps(store: Store) -> dict:
    """{gateway_id: [(from_seq, to_seq), ...]} for gateways with missing sequence numbers."""
    out = {g: store.missing_seqs(g) for g in store.gateways()}
    return {g: r for g, r in out.items() if r}
