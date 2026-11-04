"""Live engine: keeps derived state current as events arrive.

Approach: after each batch, re-derive a trailing window (plus padding so bypass detection has context) with the same tested
batch rules, and commit it over the stored intervals. Late/buffered events widen the window back to their event time.
Expect the *latest cycle* to be relabelled as more data arrives (e.g. a bypass is only recognised after ~1-2 cycles).
"""
from __future__ import annotations

from .derive import UNKNOWN, DeriveParams, derive_equipment
from .ingest import IngestReport, ingest_events
from .insight import attention
from .kpi import counter_at, line_kpi
from .sensor_health import FAULTS, SensorParams, assess_all
from .store import Store
from .topology import Topology
from spi_common.schema import format_ts

OPEN, ACK = "OPEN", "ACKNOWLEDGED"


class LiveEngine:
    def __init__(self, store: Store, topo: Topology, params: DeriveParams = DeriveParams(), window_s: int = 900, kpi_window_s: int = 3600,
                 alerts: bool = False, notifiers: dict | None = None, webpush=None, sensor_params: SensorParams = SensorParams()):
        self.store, self.topo, self.params = store, topo, params
        self.sensor_params, self._sensor_cache = sensor_params, (None, [])
        self.window_s, self.kpi_window_s = window_s, kpi_window_s
        self.pad = int(max(120, 4 * max((e.standard_cycle_time_s for e in topo.equipment.values()), default=30)))
        self.now: int | None = None  # data time = latest event time seen
        self._dirty_from: int | None = None
        self.alerts = None
        if alerts:
            from .alerts import AlertEngine
            self.alerts = AlertEngine(self, notifiers=notifiers, webpush=webpush)

    def ingest(self, events) -> IngestReport:
        rep = ingest_events(self.store, events)
        if rep.accepted:
            self.now = rep.max_ts if self.now is None else max(self.now, rep.max_ts)
            self._dirty_from = rep.min_ts if self._dirty_from is None else min(self._dirty_from, rep.min_ts)
        return rep

    def refresh(self) -> None:
        if self.now is None:
            return
        t1 = self.now + 1
        ws = t1 - self.window_s
        if self._dirty_from is not None:
            ws = min(ws, self._dirty_from - 1)  # late/buffered data: go back to its event time
        self._dirty_from = None
        for eid, eq in self.topo.equipment.items():
            ivs = derive_equipment(self.store, eq, ws - self.pad, t1, self.params)
            out = []
            for iv in ivs:  # commit only [ws, t1); the padding is context, not output
                if iv.end <= ws:
                    continue
                iv.start = max(iv.start, ws)
                out.append(iv)
            self.store.commit_intervals(eid, out, ws)
        if self.alerts:
            self.alerts.evaluate()

    def sensor_health(self, line_id: str | None = None) -> list[dict]:
        if self.now is None:
            return []
        if self._sensor_cache[0] != self.now:
            self._sensor_cache = (self.now, assess_all(self.store, self.topo, self.now, None, self.sensor_params))
        return [h for h in self._sensor_cache[1] if not line_id or self.topo.equipment[h["equipment_id"]].line_id == line_id]

    # ---- read models -------------------------------------------------
    def _current(self, eid: str) -> dict:
        t = self.now
        ivs = self.store.intervals(eid, t, t + 1, clip=False) if t is not None else []
        if not ivs:
            return {"state": UNKNOWN, "since": None, "duration_s": 0, "confidence": 0.0, "evidence": "no data yet"}
        iv = ivs[-1]
        return {"state": iv.state, "since": format_ts(iv.start), "duration_s": t + 1 - iv.start,
                "confidence": round(iv.confidence, 2), "evidence": iv.evidence}

    def equipment_status(self, eid: str) -> dict:
        eq = self.topo.equipment[eid]
        cur = self._current(eid)
        c = counter_at(self.store, eid, self.now) if self.now is not None else {"total": 0, "good": 0, "reject": 0}
        return {"equipment_id": eid, "name": eq.name, "line_id": eq.line_id, "standard_cycle_time_s": eq.standard_cycle_time_s,
                **cur, "basis": "detected", "counters": c}

    def kpi_window(self) -> tuple[int, int]:
        t1 = self.now + 1
        first = self.store.time_range()[0]
        return max(first, t1 - self.kpi_window_s), t1

    def line_status(self, line_id: str) -> dict:
        line = self.topo.lines[line_id]
        if self.now is None:
            return {"line_id": line_id, "name": line.name, "as_of": None, "equipment": [], "queues": [], "kpi": None, "attention": [], "sensor_issues": [], "alerts": []}
        eqs = [self.equipment_status(e.id) for e in line.equipment]
        queues = []
        for e in line.equipment:
            q = e.down_queue
            if q:
                ev = self.store.last_before(["queue"], self.now + 1, subject=q.id)
                queues.append({"queue_id": q.id, "from": e.id, "capacity": q.capacity,
                               "level": ev["payload"]["level"] if ev else None, "as_of": format_ts(ev["ts"]) if ev else None})
        t0, t1 = self.kpi_window()
        derived = {e.id: self.store.intervals(e.id, t0, t1) for e in line.equipment}
        k = line_kpi(self.store, line, derived, t0, t1)
        kpi = {"window": [format_ts(t0), format_ts(t1)], "good_output": k["good_output"], "target": k["target"],
               "achievement": k["achievement"], "target_per_hour": line.target_per_hour, "bottleneck": k["bottleneck"],
               "equipment": {eid: {x: v[x] for x in ("availability", "availability_own", "performance", "quality", "oee", "count_total", "state_s")}
                             for eid, v in k["equipment"].items()},
               "basis": "calculated"}
        long_enough = (t1 - t0) >= 900  # avoid 'below target' noise right after start
        att = attention(line, {e["equipment_id"]: e for e in eqs}, k if long_enough else None)
        sensors = [{k: h[k] for k in ("sensor_id", "equipment_id", "status", "severity", "reason", "duration_s")}
                   for h in self.sensor_health(line_id) if h["status"] in FAULTS]
        alerts = self.alerts.list([OPEN, ACK], line_id) if self.alerts else []
        return {"line_id": line_id, "name": line.name, "plant_id": line.plant_id, "as_of": format_ts(self.now),
                "equipment": eqs, "queues": queues, "kpi": kpi, "attention": att, "sensor_issues": sensors, "alerts": alerts}
