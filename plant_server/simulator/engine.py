"""Simulation engine: physics (model.py) + edge behaviour (signals, sensors, gateway).

Two outputs, kept strictly separate:
  * events  -> what the edge gateway sends (raw signals; what production code will see)
  * truth   -> what really happened (used only to validate the future derivation engine)

Edge failures (device comm loss, network outage, stuck sensor) never change the
physics: they only change what is observed. Tests enforce this.
"""
from __future__ import annotations

import random
import time
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import timedelta

from .config import SimConfig
from .gateway import Gateway
from .model import BYPASSED, FAULT, PRODUCING, Machine, Queue


@dataclass
class _Edge:
    sensor_rng: random.Random
    comm: bool = False
    force: bool = True  # emit a full snapshot (first tick, and after comm recovery)
    last_signals: dict = field(default_factory=dict)
    last_value: dict = field(default_factory=dict)
    stuck: dict = field(default_factory=dict)
    truth_state: str | None = None


@dataclass
class _Line:
    cfg: object
    machines: list
    queues: list
    gateway: Gateway


class Simulation:
    def __init__(self, cfg: SimConfig, sink):
        self.cfg = cfg
        self.sink = sink
        self.t = 0
        self.truth: list[dict] = []
        self.state_seconds: dict = defaultdict(lambda: defaultdict(int))
        self._windows = defaultdict(list)
        self._starts = defaultdict(list)
        for s in cfg.scenarios:
            self._windows[(s.kind, s.key)].append((s.start_s, s.start_s + s.duration_s))
            self._starts[s.start_s].append(s)
        dup_rng = random.Random(f"{cfg.seed}:dup")
        self.lines: list[_Line] = []
        self._edge: dict[str, _Edge] = {}
        self._machine: dict[str, Machine] = {}
        for lc in cfg.lines:
            queues = [Queue(q) for q in lc.queues]
            machines = []
            for i, ec in enumerate(lc.equipment):
                m = Machine(ec, cfg.seed, queues[i - 1] if i > 0 else None, queues[i] if i < len(queues) else None)
                machines.append(m)
                self._machine[ec.id] = m
                self._edge[ec.id] = _Edge(sensor_rng=random.Random(f"{cfg.seed}:{ec.id}:sensors"))
            self.lines.append(_Line(lc, machines, queues, Gateway(lc.gateway_id, sink, dup_rng, cfg.duplicate_rate)))

    # ---- helpers -------------------------------------------------------
    def iso(self, t: int) -> str:
        return (self.cfg.start_time + timedelta(seconds=t)).strftime("%Y-%m-%dT%H:%M:%SZ")

    def _in(self, kind: str, key: str, t: int) -> bool:
        return any(a <= t < b for a, b in self._windows.get((kind, key), ()))

    # ---- run loop ------------------------------------------------------
    def run(self, duration_s: int, speed: float | None = None) -> None:
        for _ in range(duration_s):
            self.step()
            if speed:
                time.sleep(1.0 / speed)

    def step(self) -> None:
        t = self.t
        for s in self._starts.get(t, ()):
            if s.kind == "fault":
                self._machine[s.equipment_id].inject_fault(t, s.duration_s)
            elif s.kind == "bypass":
                self._machine[s.equipment_id].inject_bypass(t, s.duration_s)
            self.truth.append({"ts": self.iso(t), "kind": "scenario", "scenario": s.kind, "key": s.key,
                               "start_ts": self.iso(t), "end_ts": self.iso(t + s.duration_s)})
        for line in self.lines:
            self._step_line(line, t)
        self.t += 1

    def _step_line(self, line: _Line, t: int) -> None:
        lc, gw, now = line.cfg, line.gateway, self.iso(t)
        gw.set_online(not self._in("network_outage", gw.id, t), now)
        for m in reversed(line.machines):  # downstream first so freed space is usable
            m.step(t)
        base = {"plant_id": lc.plant_id, "area_id": lc.area_id, "line_id": lc.id}
        if t % self.cfg.heartbeat_interval_s == 0:
            gw.create(now, "heartbeat", {**base, "equipment_id": None},
                      {"uptime_s": t, "buffered_events": len(gw.buffer)})
        for m in line.machines:
            self._step_edge(gw, m, t, now, {**base, "equipment_id": m.cfg.id})
        if t % self.cfg.queue_sample_interval_s == 0:
            for q in line.queues:
                gw.create(now, "queue", {**base, "equipment_id": None},
                          {"queue_id": q.cfg.id, "level": q.level, "capacity": q.cfg.capacity})

    def _step_edge(self, gw: Gateway, m: Machine, t: int, now: str, ctx: dict) -> None:
        eid, ed = m.cfg.id, self._edge[m.cfg.id]
        self.state_seconds[eid][m.state] += 1
        if m.new_random_fault:
            s, d = m.new_random_fault
            self.truth.append({"ts": now, "kind": "scenario", "scenario": "random_fault", "key": eid,
                               "start_ts": self.iso(s), "end_ts": self.iso(s + d)})
        if m.state != ed.truth_state:
            ed.truth_state = m.state
            self.truth.append({"ts": now, "kind": "state", "equipment_id": eid, "state": m.state})

        readings = self._sample_sensors(m, ed, t)  # always sampled -> RNG use independent of comm state
        comm = self._in("device_comm_loss", eid, t)
        if comm != ed.comm:
            ed.comm = comm
            gw.create(now, "comm_status", ctx, {"ok": not comm})
            if not comm:
                ed.force = True
        if comm:
            return  # equipment unreachable: gateway sees nothing from it

        signals = {"machine_on": 1, "running": int(m.state in (PRODUCING, BYPASSED)), "fault": int(m.state == FAULT)}
        for name, val in signals.items():
            if ed.force or ed.last_signals.get(name) != val:
                gw.create(now, "signal", ctx, {"name": name, "value": val})
                ed.last_signals[name] = val
        if ed.force:
            gw.create(now, "counter", ctx, {"total": m.total, "good": m.good, "reject": m.reject})
            ed.force = False
        if m.cycle_done:
            ct, good = m.cycle_done
            gw.create(now, "cycle", ctx, {"cycle_time_s": ct, "result": "good" if good else "reject",
                                          "counters": {"total": m.total, "good": m.good, "reject": m.reject}})
        for s, v in readings:
            gw.create(now, "sensor", ctx, {"sensor_id": s.id, "name": s.name, "value": v, "unit": s.unit, "quality": "good"})

    def _sample_sensors(self, m: Machine, ed: _Edge, t: int) -> list:
        out = []
        for s in m.cfg.sensors:
            if t % s.sample_interval_s:
                continue
            v = round(s.base + (s.producing_delta if m.state == PRODUCING else 0.0) + ed.sensor_rng.gauss(0, s.noise), 1)
            key = f"{m.cfg.id}/{s.id}"
            if self._in("sensor_offline", key, t):
                continue                                                   # sensor silent (RNG already drawn: physics unchanged)
            if self._in("sensor_impossible", key, t):
                v = 9999.9                                                 # physically impossible reading, quality still 'good'
            elif self._in("sensor_stuck", key, t):
                v = ed.stuck.setdefault(s.id, ed.last_value.get(s.id, v))  # frozen reading, quality still 'good'
            else:
                ed.stuck.pop(s.id, None)
            ed.last_value[s.id] = v
            out.append((s, v))
        return out

    # ---- reporting -----------------------------------------------------
    def summary(self) -> dict:
        elapsed = self.t
        out = {"elapsed_s": elapsed, "lines": {}}
        for line in self.lines:
            last = line.machines[-1]
            out["lines"][line.cfg.id] = {
                "good_output": last.good,
                "target": round(line.cfg.target_per_hour * elapsed / 3600.0, 1),
                "machines": {
                    m.cfg.id: {st: round(100.0 * n / elapsed, 1) for st, n in sorted(self.state_seconds[m.cfg.id].items())}
                    for m in line.machines
                },
            }
        return out
