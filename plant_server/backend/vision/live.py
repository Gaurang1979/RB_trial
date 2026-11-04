"""Live state of every vision line (PL2), kept in memory from the stored events and rebuilt at start.

A port of the .NET plant server's LiveFloor (PR #49): counters summed over stations, the last 20 NG
parts (each with result_id, its row in vision_result, so a client can open it in the NG review) and the most frequent reason, open alarms (high first), parts in the last minute, stations
online by heartbeat, and the line shown "offline" when no station has sent a heartbeat for 15 s, so an
old state is never shown as live. A station's clock is capped at the server's now + 1 minute.
`version` goes up with every event or heartbeat applied, so the WebSocket (Req 17.3 LV-03) knows when to look again.
"""
from __future__ import annotations

import threading
import time
from collections import Counter, deque

OFFLINE_AFTER_MS = 15_000
CLOCK_ALLOWANCE_MS = 60_000
KEEP_NG, TOP_NG_OF = 20, 200
PRIORITY_ORDER = {"high": 0, "medium": 1, "low": 2}


def _natural(s: str):
    import re
    return [int(t) if t.isdigit() else t.lower() for t in re.split(r"(\d+)", s)]


class _Line:
    def __init__(self):
        self.state, self.reason, self.job = "unknown", None, None
        self.state_since = self.last_result = 0
        self.counters: dict[str, dict] = {}
        self.ng: deque = deque(maxlen=KEEP_NG)
        self.ng_reasons: deque = deque(maxlen=TOP_NG_OF)
        self.results: list[int] = []
        self.alarms: dict[tuple, dict] = {}
        self.stations: dict[str, dict] = {}


class VisionFloor:
    def __init__(self, clock=lambda: int(time.time() * 1000)):
        self._clock = clock
        self._lock = threading.Lock()
        self._lines: dict[tuple, _Line] = {}
        self.version = 0

    def _get(self, plant: str, line: str) -> _Line:
        return self._lines.setdefault((plant, line), _Line())

    def apply(self, e: dict) -> tuple[str, str]:
        """Applies one stored event; returns the (plant, line) it changed."""
        ts = min(int(e.get("ts", 0)), self._clock() + CLOCK_ALLOWANCE_MS)
        st, kind = e.get("station"), e.get("kind")
        with self._lock:
            self.version += 1
            ln = self._get(e["plant"], e["line"])
            if kind == "inspection.result":
                ln.last_result = max(ln.last_result, ts)
                ln.results.append(ts)
                if len(ln.results) > 100_000:
                    del ln.results[:50_000]
                if not e.get("ok"):
                    for r in e.get("reasons", []):
                        ln.ng.appendleft({"ts": ts, "station": st, "camera": e.get("camera"), "tool": r["tool"], "reason": r["reason"],
                                          "image_id": e.get("image_id"), "result_id": e.get("result_id")})
                        ln.ng_reasons.append(r["reason"])
                ln.job = e.get("job") or ln.job
            elif kind == "line.state" and ts >= ln.state_since:
                ln.state, ln.reason, ln.state_since = e["state"], e.get("reason"), ts
            elif kind == "counter.snapshot":
                old = ln.counters.get(st)
                if old is None or ts >= old["ts"]:
                    ln.counters[st] = {"ts": ts, "good": e["good"], "bad": e["bad"]}
            elif kind == "alarm.raised":
                ln.alarms[(st, e["code"])] = {"station": st, "code": e["code"], "priority": e.get("priority", "medium"), "since": ts,
                                              "acknowledged_by": None}
            elif kind == "alarm.acknowledged" and (st, e["code"]) in ln.alarms:
                ln.alarms[(st, e["code"])]["acknowledged_by"] = e.get("by")
            elif kind == "alarm.cleared":
                ln.alarms.pop((st, e["code"]), None)
            elif kind == "job.changed":
                ln.job = e["to"]
        return e["plant"], e["line"]

    def heartbeat(self, hb: dict, seen_ms: int) -> None:
        with self._lock:
            self.version += 1
            self._get(hb["plant"], hb["line"]).stations[hb["station"]] = {
                "station": hb["station"], "last_seen": seen_ms, "agent_version": hb.get("agent_version", ""),
                "last_seq": hb.get("seq", 0), "queue_depth": hb.get("queue_depth", 0), "dropped": hb.get("dropped", 0)}

    def _snapshot(self, key: tuple, ln: _Line) -> dict:
        now = self._clock()
        ln.results = [t for t in ln.results if t >= now - 60_000]
        stations = sorted(({**s, "connected": now - s["last_seen"] <= OFFLINE_AFTER_MS} for s in ln.stations.values()),
                          key=lambda s: _natural(s["station"]))
        good = sum(c["good"] for c in ln.counters.values())
        bad = sum(c["bad"] for c in ln.counters.values())
        total = good + bad
        top = sorted(Counter(ln.ng_reasons).items(), key=lambda kv: (-kv[1], kv[0]))
        state = "offline" if stations and not any(s["connected"] for s in stations) else ln.state
        return {"plant": key[0], "line": key[1], "state": state, "state_reason": ln.reason, "state_since": ln.state_since, "job": ln.job,
                "good": good, "bad": bad, "total": total, "yield_pct": round(100.0 * good / total, 2) if total else None,
                "parts_per_minute": sum(1 for t in ln.results if t <= now), "top_ng": top[0][0] if top else None,
                "last_ng": list(ln.ng), "alarms": sorted(ln.alarms.values(), key=lambda a: (PRIORITY_ORDER.get(a["priority"], 2), a["since"])),
                "stations": stations, "last_result": ln.last_result}

    def floor(self) -> list[dict]:
        with self._lock:
            return [self._snapshot(k, ln) for k, ln in sorted(self._lines.items(), key=lambda kv: (kv[0][0], _natural(kv[0][1])))]

    def line(self, plant: str, line: str) -> dict | None:
        with self._lock:
            ln = self._lines.get((plant, line))
            return self._snapshot((plant, line), ln) if ln else None
