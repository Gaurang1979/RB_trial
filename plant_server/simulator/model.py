"""Physical model: queues and machines. Produces *ground truth* states.

Truth states generated today: PRODUCING, STARVED, BLOCKED, FAULT, BYPASSED.
Time is discrete (1 s ticks), so each cycle is quantised up to whole seconds
(about +0.5 s on average). IDLE, OFF, MAINTENANCE are not simulated yet.
"""
from __future__ import annotations

import random

PRODUCING = "PRODUCING"
STARVED = "STARVED"
BLOCKED = "BLOCKED"
FAULT = "FAULT"
BYPASSED = "BYPASSED"


class Queue:
    def __init__(self, cfg):
        self.cfg = cfg
        self.level = 0

    def has_space(self) -> bool:
        return self.level < self.cfg.capacity


class Machine:
    def __init__(self, cfg, seed: int, upstream: Queue | None, downstream: Queue | None):
        self.cfg = cfg
        self.rng = random.Random(f"{seed}:{cfg.id}")  # own stream: physics independent of edge failures
        self.up = upstream
        self.down = downstream
        self.state: str | None = None
        self.in_cycle = False
        self.elapsed = 0
        self.target = 0.0
        self.holding = False  # finished good part waiting for downstream space
        self.taken = 0  # cycles started (parts consumed)
        self.total = self.good = self.reject = 0
        self.fault_until = -1
        self.bypass_until = -1
        self.cycle_done: tuple[int, bool] | None = None
        self.new_random_fault: tuple[int, int] | None = None

    def inject_fault(self, t: int, duration: int) -> None:
        self.fault_until = max(self.fault_until, t + duration)

    def inject_bypass(self, t: int, duration: int) -> None:
        self.bypass_until = max(self.bypass_until, t + duration)

    def step(self, t: int) -> None:
        self.cycle_done = None
        self.new_random_fault = None
        c = self.cfg
        if c.mtbf_s and t >= self.fault_until and self.rng.random() < 1.0 / c.mtbf_s:
            dur = min(max(10, round(self.rng.expovariate(1.0 / c.mttr_s))), round(4 * c.mttr_s))  # capped tail
            self.fault_until = t + dur
            self.new_random_fault = (t, dur)
        if t < self.fault_until:
            self.state = FAULT
            return
        if t < self.bypass_until:  # signals say running, nothing is produced
            self.state = BYPASSED
            return
        if self.holding:
            if self.down.has_space():
                self.down.level += 1
                self.holding = False
            else:
                self.state = BLOCKED
                return
        if not self.in_cycle:
            if self.up is not None:
                if self.up.level <= 0:
                    self.state = STARVED
                    return
                self.up.level -= 1
            self.taken += 1
            self.target = max(0.5 * c.standard_cycle_time_s,
                              self.rng.gauss(c.standard_cycle_time_s, c.cycle_time_cv * c.standard_cycle_time_s))
            self.elapsed = 0
            self.in_cycle = True
        self.state = PRODUCING
        self.elapsed += 1
        if self.elapsed >= self.target:
            good = self.rng.random() >= c.reject_rate
            self.total += 1
            self.good += good
            self.reject += not good
            self.in_cycle = False
            if good and self.down is not None:
                self.holding = True
            self.cycle_done = (self.elapsed, good)
