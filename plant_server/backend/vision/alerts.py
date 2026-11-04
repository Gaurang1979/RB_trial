"""Vision alarms and silent vision stations as core alerts (PL2; docs/vision-system/PLANT-PLATFORM.md 3).

The core alert engine (backend/alerts.py) owns the lifecycle, escalation, notifications and audit trail; this
module only says which vision conditions hold now, read from the live floor:

  vision_alarm            an alarm a station raised and has not cleared. The alert takes the alarm's priority as
                          its severity, is acknowledged when the station acknowledges it (so it stops escalating),
                          and resolves when the station clears it.
  vision_grade_drift      a station's print grades for one tool drifting toward the lowest grade (vision/grades.py, Req 17.3
                          PQ-03); it only warns, the line still judges each graded part.
  vision_station_offline  a station that has sent no heartbeat for the rule's threshold (never less than the
                          floor's 15 s). The station keeps its results and sends them when the link returns.
                          After a server start, a station gets the rule's threshold to call in first.

Titles name the line and the station by their names (master table, device name) and the alarm by its catalogue
title (vision/alarms.py), in the server's language; the evidence keeps the ids and the code.
Each raising of an alarm is its own alert (its key holds the time it was raised), so an alarm cleared and raised
again opens, notifies and escalates afresh. Stations whose device is disabled or no longer on that line are left out.
One thread evaluates (`run`); a batch or heartbeat only wakes it (`poke`), so station ingest never waits on alerting
and never fails because of it. With no thread running (tests), `poke` evaluates at once.

Acknowledging or resolving the alert on the plant server does not acknowledge the alarm on the station: the
server never commands a line PC. Time is the server's wall clock in seconds; the floor's times are milliseconds.
"""
from __future__ import annotations

import threading

from spi_common.schema import format_ts

from ..alerts import Cond
from . import alarms as catalogue
from . import grades
from .live import OFFLINE_AFTER_MS, VisionFloor


def _when(ms: int) -> str:
    return format_ts(ms // 1000)


class VisionAlerts:
    def __init__(self, engine, floor: VisionFloor, store, lang: str = "en"):
        self.engine, self.floor, self.store, self.lang = engine, floor, store, lang
        self.started = engine.wall_clock()
        self._wake = threading.Event()
        self._running = False

    def evaluate(self, now: int | None = None) -> list[Cond]:
        with self.engine._lock:   # read the floor under the engine's lock: an older snapshot never lands after a newer one
            lines = self.floor.floor()
            names = {r["id"]: r["name"] for r in self.store.master_list("line")}
            devices = [d for d in self.store.device_list() if d["kind"] == "vision_station"]
            active = {(d["plant_id"], d["line_id"], d["id"]) for d in devices if d["enabled"]}
            stations = {d["id"]: d["name"] for d in devices}
            seen = set()

            def conds(rule, t):
                if rule["type"] == "vision_grade_drift":
                    out = grades.conditions(rule, self.store, t, names, active, stations)
                else:
                    out = conditions(rule, lines, t, names, active, self.started, stations, self.lang)
                seen.update(c.subject for c in out)
                return out

            def why_cleared(a):
                st = (a["plant_id"], a["line_id"], (a["evidence"] or {}).get("station"))
                if st not in active:
                    return "station disabled or moved to another line"
                if a["rule_id"] not in self.engine.by_id:
                    return "rule turned off"
                if a["rule_type"] == "vision_grade_drift":
                    return "print grade back above the warning level, or no graded parts lately"
                return "alarm cleared at the station" if a["rule_type"] == "vision_alarm" else "station sending heartbeats again"

            return self.engine.evaluate_vision(conds, now, why_cleared)

    def poke(self) -> None:
        if self._running:
            self._wake.set()
            return
        try:
            self.evaluate()
        except Exception as exc:   # alerting never fails a station's batch or heartbeat
            print(f"vision alerts: {exc!r}")

    def run(self, stop: threading.Event, tick_s: float) -> None:
        self._running = True
        try:
            while not stop.is_set():
                self._wake.wait(tick_s)
                self._wake.clear()
                if stop.is_set():
                    break
                try:
                    self.evaluate()
                except Exception as exc:   # one failed pass is retried on the next; it never stops alerting
                    print(f"vision alerts: {exc!r}")
        finally:
            self._running = False


def conditions(rule: dict, lines: list[dict], now: int, names: dict | None = None, active: set | None = None,
               started: int | None = None, stations: dict | None = None, lang: str = "en") -> list[Cond]:
    """`names`: line id -> name; `active`: the (plant, line, station) of enabled stations (None: all); `started`: when
    the server started; `stations`: station (device) id -> device name; `lang`: the language of titles and details."""
    names, stations = names or {}, stations or {}
    out: list[Cond] = []
    for ln in lines:
        plant, line = ln["plant"], ln["line"]
        lname = names.get(line) or line
        if rule["type"] == "vision_alarm":
            for a in ln["alarms"]:
                if active is not None and (plant, line, a["station"]) not in active:
                    continue
                st, code = a["station"], a["code"]
                sname, txt = stations.get(st) or st, catalogue.text(code, lang)
                by = a["acknowledged_by"]
                detail = (f"Raised by station {sname} at {_when(a['since'])}, priority {a['priority']}, code {code}. "
                          + (f"{txt['fix']} " if txt["fix"] else "")
                          + (f"Acknowledged at the station by {by}. " if by else "")
                          + "The alert resolves when the station clears the alarm.")
                out.append(Cond(rule, f"{plant}/{line}/{st}/{code}/{a['since']}", f"{lname} {sname}: {txt['title']}", detail,
                                "alarm raised at the station and not cleared", line, plant, None,
                                {"station": st, "code": code, "priority": a["priority"], "since_ms": a["since"], "acknowledged_by": by},
                                severity=a["priority"], acked_by=f"{by} (at station {st})" if by else None))
        elif rule["type"] == "vision_station_offline":
            need = max(rule["threshold_s"], OFFLINE_AFTER_MS // 1000)
            for s in ln["stations"]:
                if active is not None and (plant, line, s["station"]) not in active:
                    continue
                silent = now - s["last_seen"] // 1000
                if silent * 1000 <= OFFLINE_AFTER_MS:
                    continue
                out.append(Cond(rule, f"{plant}/{line}/{s['station']}", f"{lname}: vision station {stations.get(s['station']) or s['station']} offline",
                                f"No heartbeat since {_when(s['last_seen'])}. The plant server cannot see this station's results; "
                                "the station keeps them and sends them when the link returns. Check the line PC and its network.",
                                f"no heartbeat for at least {need} s", line, plant, None,
                                {"station": s["station"], "last_seen_ms": s["last_seen"], "silent_s": silent,
                                 "queue_depth": s["queue_depth"]},
                                trigger=silent >= need and (started is None or now - started >= need)))
    return out
