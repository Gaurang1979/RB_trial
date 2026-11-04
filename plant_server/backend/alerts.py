"""Alert engine (spec s26): configurable rules -> alerts with a lifecycle, audit trail and notifications.

Lifecycle:  OPEN -> ACKNOWLEDGED -> RESOLVED   (an alert resolves itself once its condition has stayed clear for `clear_after_s`)
Escalation: an alert still OPEN (not acknowledged) after `after_s` is escalated to the next recipients.
Vision alarms and silent vision stations (PL2) are alerts of the same kind, with their own rule types; they run on the
server's wall clock (vision/alerts.py), because vision stations send wall-clock times, never on production data time.
Security alerts (PT11-09: repeated locks of one user, a broken audit chain) are raised when the audit trail shows them, on the
wall clock too, and never resolve by themselves: a person resolves them.
Every step is written to `alert_event` (who, when, what); every send attempt to `notification`. Time is DATA time (event time), so replays
and tests are deterministic. Alerts use only detected states, calculated KPIs and measured readings: no guessing of causes.
"""
from __future__ import annotations

import json
import queue
import threading
import time
import urllib.request
from dataclasses import dataclass, field

from spi_common.schema import format_ts

from .auth import ROLES
from .derive import BYPASSED, COMM, FAULT, NO_DATA, UNKNOWN
from .insight import attention
from .kpi import line_kpi
from .sensor_health import FAULTS

OPEN, ACK, RESOLVED = "OPEN", "ACKNOWLEDGED", "RESOLVED"
PRODUCTION_TYPES = ("equipment_stopped", "bypass_detected", "equipment_unreachable", "gateway_silent", "sensor_unhealthy", "production_below_target", "queue_level")
VISION_TYPES = ("vision_alarm", "vision_station_offline", "vision_grade_drift")
SECURITY_TYPES = ("security_repeated_locks", "audit_chain_broken")
RULE_TYPES = PRODUCTION_TYPES + VISION_TYPES + SECURITY_TYPES

DEFAULT_RULES = [
    {"id": "equipment-stopped", "type": "equipment_stopped", "severity": "high", "threshold_s": 120, "recipients": ["plant_manager", "maintenance_manager"],
     "channels": ["in_app"], "escalations": [{"after_s": 900, "recipients": ["plant_manager"]}]},
    {"id": "bypass-suspected", "type": "bypass_detected", "severity": "medium", "threshold_s": 300, "recipients": ["production_manager", "maintenance_manager"], "channels": ["in_app"]},
    {"id": "gateway-silent", "type": "gateway_silent", "severity": "medium", "threshold_s": 120, "recipients": ["it_ot"], "channels": ["in_app"]},
    {"id": "equipment-unreachable", "type": "equipment_unreachable", "severity": "medium", "threshold_s": 120, "recipients": ["it_ot", "maintenance_manager"], "channels": ["in_app"]},
    {"id": "sensor-unhealthy", "type": "sensor_unhealthy", "severity": "medium", "threshold_s": 0, "recipients": ["maintenance_manager"], "channels": ["in_app"]},
    {"id": "below-target", "type": "production_below_target", "severity": "medium", "threshold": 0.8, "min_window_s": 1800, "recipients": ["plant_manager"], "channels": ["in_app"]},
]
# Each one whose type a rule list does not name is added to it, so a site file written before PL2 still alerts on vision alarms;
# "enabled": false is the only way to turn one off.
# A vision_alarm alert takes the alarm's own priority as its severity; it resolves as soon as the station clears the alarm.
DEFAULT_VISION_RULES = [
    {"id": "vision-alarm", "type": "vision_alarm", "threshold_s": 0, "clear_after_s": 0, "recipients": ["production_manager", "maintenance_manager"],
     "channels": ["in_app"], "escalations": [{"after_s": 900, "recipients": ["plant_manager"]}]},
    {"id": "vision-station-offline", "type": "vision_station_offline", "severity": "high", "threshold_s": 60, "clear_after_s": 30,
     "recipients": ["it_ot", "maintenance_manager"], "channels": ["in_app"]},
    # Print grades drifting toward the lowest grade (Req 17.3 PQ-03; vision/grades.py has the numbers and their defaults).
    {"id": "vision-grade-drift", "type": "vision_grade_drift", "severity": "medium", "threshold_s": 0, "clear_after_s": 300,
     "lowest_grade": 2.0, "margin": 0.5, "window": 20, "min_samples": 10, "max_age_s": 28800,
     "recipients": ["quality_manager", "production_manager"], "channels": ["in_app"], "escalations": [{"after_s": 1800, "recipients": ["plant_manager"]}]},
]

# Added like the vision rules when a rule list does not name their type (PT11-05, PT11-23, PT11-09).
DEFAULT_SECURITY_RULES = [
    {"id": "repeated-locks", "type": "security_repeated_locks", "severity": "high", "threshold": 3, "window_s": 3600,
     "recipients": ["system_administrator"], "channels": ["in_app"]},
    {"id": "audit-chain-broken", "type": "audit_chain_broken", "severity": "high", "recipients": ["system_administrator", "it_ot"],
     "channels": ["in_app"]},
]


def normalize_rule(r: dict) -> dict:
    if r.get("type") not in RULE_TYPES:
        raise ValueError(f"alert rule {r.get('id')!r}: unknown type {r.get('type')!r}; expected one of {RULE_TYPES}")
    if not r.get("id"):
        raise ValueError("alert rule needs an id")
    out = {"severity": "medium", "threshold_s": 0, "threshold": None, "recipients": [], "channels": ["in_app"], "clear_after_s": 60, "escalations": [], "enabled": True, **r}
    if not isinstance(out["enabled"], bool):
        raise ValueError(f"alert rule {r['id']!r}: enabled must be true or false")
    if out["severity"] not in ("high", "medium", "low"):
        raise ValueError(f"alert rule {r['id']!r}: severity must be high, medium or low")
    if out["type"] == "vision_grade_drift":
        from .vision.grades import check_rule
        check_rule(out)
    # Recipients are role names (auth.ROLES): an unknown one reaches no account, so the rule is refused, not left silent.
    for where, rcps in [("recipients", out["recipients"])] + [(f"escalation {i}", e.get("recipients", [])) for i, e in enumerate(out["escalations"], 1)]:
        bad = [x for x in rcps if x not in ROLES]
        if bad:
            raise ValueError(f"alert rule {r['id']!r}: {where} names unknown role(s) {bad}; expected roles from {ROLES}")
    return out


# ---------------- notification channels ----------------
class InAppNotifier:
    """In-app alerts are the stored alert list itself (dashboard, API); nothing to send."""
    def send(self, payload, done):
        done(True, "stored in alert list")


class MemoryNotifier:
    def __init__(self):
        self.sent = []

    def send(self, payload, done):
        self.sent.append(payload)
        done(True, "recorded")


class WebhookNotifier:
    """POST JSON to a URL (bridge to Slack, WhatsApp/SMS gateways, ticketing...). Runs on its own thread: a slow endpoint never blocks alerting."""
    def __init__(self, url: str, timeout: float = 3.0, headers: dict | None = None):
        self.url, self.timeout, self.headers = url, timeout, headers or {}
        self.q: queue.Queue = queue.Queue()
        threading.Thread(target=self._run, name="spi-webhook", daemon=True).start()

    def send(self, payload, done):
        self.q.put((payload, done))

    def _run(self):
        while True:
            payload, done = self.q.get()
            try:
                req = urllib.request.Request(self.url, data=json.dumps(payload).encode(), method="POST",
                                             headers={"Content-Type": "application/json", **self.headers})
                with urllib.request.urlopen(req, timeout=self.timeout) as r:
                    done(200 <= r.status < 300, f"HTTP {r.status}")
            except Exception as exc:  # delivery problems are recorded, never raised into the alert engine
                done(False, repr(exc)[:200])
            finally:
                self.q.task_done()

    def flush(self):
        self.q.join()


@dataclass
class Cond:
    rule: dict
    subject: str
    title: str
    detail: str
    condition: str
    line_id: str | None = None
    plant_id: str | None = None
    equipment_id: str | None = None
    evidence: dict = field(default_factory=dict)
    trigger: bool = True   # meets the rule's threshold (needed to OPEN); an already-open alert only needs the underlying state to persist
    severity: str | None = None   # overrides the rule's severity (a vision alarm's own priority)
    acked_by: str | None = None   # acknowledged at the source (a vision station's HMI): the alert is acknowledged too, so it stops escalating

    @property
    def key(self) -> str:
        return f"{self.rule['id']}|{self.subject}"


class AlertEngine:
    def __init__(self, engine, rules: list | None = None, notifiers: dict | None = None, webpush=None, wall_clock=lambda: int(time.time())):
        self.engine = engine
        rules = list(rules or engine.topo.alert_rules or DEFAULT_RULES)
        rules += [d for d in DEFAULT_VISION_RULES + DEFAULT_SECURITY_RULES if not any(r.get("type") == d["type"] for r in rules)]
        # A rule with "enabled": false is checked, then left out: the way to turn a default vision rule off.
        self.rules = [r for r in map(normalize_rule, rules) if r["enabled"]]
        self.by_id = {r["id"]: r for r in self.rules}
        self.notifiers = {"in_app": InAppNotifier(), **(notifiers or {})}
        self.webpush = webpush  # a webpush.WebPushNotifier, or None to leave the "webpush" channel unconfigured
        self.wall_clock = wall_clock  # seconds; the time of vision alerts
        self._lock = threading.RLock()

    # ---------------- conditions ----------------
    def _states(self, line):
        return {e.id: self.engine._current(e.id) for e in line.equipment}

    def _conditions(self, rule: dict, now: int) -> list[Cond]:
        t, thr, topo = rule["type"], rule["threshold_s"], self.engine.topo
        out: list[Cond] = []
        if t in ("equipment_stopped", "bypass_detected", "equipment_unreachable", "gateway_silent"):
            want = {"equipment_stopped": FAULT, "bypass_detected": BYPASSED, "equipment_unreachable": COMM, "gateway_silent": NO_DATA}[t]
            for line in topo.lines.values():
                states = self._states(line)
                items = attention(line, {k: {"state": v["state"], "duration_s": v["duration_s"]} for k, v in states.items()}, None, min_stop_s=0)
                silent = []
                for e in line.equipment:
                    st = states[e.id]
                    if st["state"] != want:
                        continue
                    trig = st["duration_s"] >= thr
                    ev = {"state": st["state"], "since": st["since"], "duration_s": st["duration_s"], "evidence": st["evidence"], "confidence": st["confidence"]}
                    if t in ("equipment_stopped", "bypass_detected"):
                        it = next((i for i in items if i["equipment_id"] == e.id), None)
                        if it is None:
                            continue
                        detail = (f"Since {st['since']}. Why: {it['why']}. " + (f"Effect: {'; '.join(it['effects'])}. " if it["effects"] else "") +
                                  f"Check: {it['action']}. Impact so far: {it['estimate_basis']}.")
                        title = f"{e.name} stopped" if t == "equipment_stopped" else f"{e.name}: production signal unavailable"
                        out.append(Cond(rule, e.id, title, detail, f"{want} for at least {thr} s", line.id, line.plant_id, e.id, ev, trig))
                    elif t == "equipment_unreachable":
                        out.append(Cond(rule, e.id, f"{e.name} unreachable",
                                        f"Since {st['since']}. The gateway cannot read this machine. Production may continue unobserved; counters reconcile when it returns.",
                                        f"communication error for at least {thr} s", line.id, line.plant_id, e.id, ev, trig))
                    else:
                        silent.append((e, st))
                if silent:
                    d = max(st["duration_s"] for _, st in silent)
                    out.append(Cond(rule, line.equipment[0].gateway_id, "No data from the line gateway",
                                    "Network or gateway problem. Machine states are unknown, not stopped. Buffered data will fill the gap when the link returns.",
                                    f"no gateway data for at least {thr} s", line.id, line.plant_id, None, {"duration_s": d}, d >= thr))
        elif t == "sensor_unhealthy":
            for h in self.engine.sensor_health():
                if h["status"] in FAULTS:
                    eq = topo.equipment[h["equipment_id"]]
                    out.append(Cond(rule, h["sensor_id"], f"Sensor {h['sensor_id']}: {h['status'].lower()}", f"Since {format_ts(h['since'])}. {h['reason']}. Readings from this sensor are unreliable; check the sensor and wiring.",
                                    f"sensor status {h['status']} for at least {thr} s", eq.line_id, topo.lines[eq.line_id].plant_id, eq.id,
                                    {k: h[k] for k in ("status", "since", "duration_s", "last_valid", "current", "confidence")}, h["duration_s"] >= thr))
        elif t == "production_below_target":
            t0, t1 = self.engine.kpi_window()
            if t1 - t0 >= rule.get("min_window_s", 1800):
                for line in topo.lines.values():
                    k = line_kpi(self.engine.store, line, {e.id: self.engine.store.intervals(e.id, t0, t1) for e in line.equipment}, t0, t1)
                    if k["achievement"] is not None and k["target"] > 0 and k["achievement"] < rule["threshold"]:
                        out.append(Cond(rule, line.id, f"{line.name} output {round((1 - k['achievement']) * 100)}% below target",
                                        f"{k['good_output']} good units against a target of {k['target']} in the last {(t1 - t0) // 60} min (calculated).",
                                        f"achievement below {rule['threshold']:.0%}", line.id, line.plant_id, None,
                                        {"good_output": k["good_output"], "target": k["target"], "achievement": k["achievement"]}))
        elif t == "queue_level":
            want_full = rule.get("condition", "full") == "full"
            for e in (x for x in topo.equipment.values() if x.down_queue):
                q = e.down_queue
                if rule.get("queue_id") and rule["queue_id"] != q.id:
                    continue
                s = self.engine.store.events(["queue"], subject=q.id, t_min=now - thr - 30, t_max=now)
                if len(s) >= 2 and s[0]["ts"] <= now - thr and all((x["payload"]["level"] >= q.capacity) if want_full else (x["payload"]["level"] <= 0) for x in s):
                    out.append(Cond(rule, q.id, f"{q.id} {'full' if want_full else 'empty'}",
                                    "Queue level measured by its sensor. A full queue blocks the machine before it; an empty queue starves the machine after it.",
                                    f"queue {'full' if want_full else 'empty'} for at least {thr} s", e.line_id, topo.lines[e.line_id].plant_id, e.id,
                                    {"queue_id": q.id, "capacity": q.capacity}))
        return out

    # ---------------- lifecycle ----------------
    def evaluate(self, now: int | None = None) -> None:
        """Production rules, on data time."""
        now = self.engine.now if now is None else now
        if now is None:
            return
        with self._lock:
            conds = [c for rule in self.rules if rule["type"] in PRODUCTION_TYPES for c in self._conditions(rule, now)]
            self._apply(conds, now, PRODUCTION_TYPES, self._state_unknown)

    def evaluate_vision(self, conditions, now: int | None = None, why_cleared=None) -> list[Cond]:
        """Vision rules, on wall-clock time. `conditions(rule, now)` gives a vision rule's conditions (vision/alerts.py);
        `why_cleared(alert)` says why a vision alert's condition went away. The caller holds `self._lock` while it reads
        the floor, so no older snapshot is ever applied after a newer one."""
        now = self.wall_clock() if now is None else now
        with self._lock:
            conds = [c for rule in self.rules if rule["type"] in VISION_TYPES for c in conditions(rule, now)]
            self._apply(conds, now, VISION_TYPES, lambda a: False, why_cleared)
            return conds

    def raise_security(self, rule_type: str, subject: str, title: str, detail: str, condition: str, evidence: dict,
                       now: int | None = None) -> dict | None:
        """Opens a security alert, or refreshes the one of this subject still open or acknowledged (so a condition that
        keeps holding is one alert). None when the rule is turned off."""
        rule = next((r for r in self.rules if r["type"] == rule_type), None)
        if rule is None:
            return None
        now = self.wall_clock() if now is None else now
        store, c = self.engine.store, Cond(rule, subject, title, detail, condition, evidence=evidence)
        with self._lock:
            a = next((a for a in store.alert_list([OPEN, ACK], limit=100000) if a["alert_key"] == c.key), None)
            if a is None:
                return self.public(store.alert_get(self._open(c, now)))
            store.alert_update(a["id"], last_seen_at=now, title=title, detail=detail, evidence=evidence)
            return self.public(store.alert_get(a["id"]))

    def repeated_locks(self, u: dict | None, now: int | None = None) -> dict | None:
        """After a lock of account u: an alert to Admin once it holds the rule's number of locks (default 3) within its
        window (default an hour), counted from the `lock` audit entries (PT11-05). None: below that, or no account."""
        rule = next((r for r in self.rules if r["type"] == "security_repeated_locks"), None)
        if rule is None or u is None:
            return None
        from . import audit
        now = self.wall_clock() if now is None else now
        need, window = int(rule.get("threshold") or 3), int(rule.get("window_s", 3600))
        locks = audit.entries(self.engine.store, user_id=u["id"], action="lock", since=now - window, limit=100)
        if len(locks) < need:
            return None
        hours = f"{window // 3600} h" if window % 3600 == 0 else f"{window // 60} min"
        seen = [{"ts": e["ts"], "client": e["client"], "address": (e["detail"] or {}).get("address"), "note": (e["detail"] or {}).get("note")}
                for e in reversed(locks)]
        detail = (f"{len(locks)} locks within {hours}, the latest at {seen[-1]['ts']}. Someone may be guessing this account's password "
                  "or PIN, or the person needs help signing in. Check the audit trail (action lock) and talk to the person.")
        return self.raise_security("security_repeated_locks", f"user:{u['id']}", f"{u['name']} ({u['username']}): {len(locks)} locks within {hours}",
                                   detail, f"at least {need} locks of one user within {hours}",
                                   {"user_id": u["id"], "username": u["username"], "locks": seen}, now)

    def _apply(self, cond_list: list[Cond], now: int, types: tuple, unknown, why_cleared=None) -> None:
        """Opens, keeps, clears and escalates the alerts of the given rule types only: production and vision run on different clocks."""
        store = self.engine.store
        conds = {c.key: c for c in cond_list}
        open_alerts = {a["alert_key"]: a for a in store.alert_list([OPEN, ACK], limit=100000) if a["rule_type"] in types}
        for key, c in conds.items():
            a = open_alerts.get(key)
            if a is None:
                if not c.trigger:
                    continue
                a = store.alert_get(self._open(c, now))
            else:
                sev = {"severity": c.severity} if c.severity else {}
                store.alert_update(a["id"], last_seen_at=now, clear_since=None, title=c.title, detail=c.detail, evidence=c.evidence, **sev)
                a.update(last_seen_at=now, clear_since=None)
            if c.acked_by and a["state"] == OPEN:
                store.alert_update(a["id"], state=ACK, acked_at=now, acked_by=c.acked_by)
                store.alert_event_add(a["id"], now, "acknowledged", c.acked_by, "acknowledged at the source")
        for key, a in open_alerts.items():
            if key in conds:
                continue
            if unknown(a):                             # cannot see the machine: the alert stays open, it is not "cleared"
                if a["clear_since"] is not None:
                    store.alert_update(a["id"], clear_since=None)
                continue
            rule = self.by_id.get(a["rule_id"], {"clear_after_s": 60})
            since = a["clear_since"] if a["clear_since"] is not None else now
            if a["clear_since"] is None:
                store.alert_update(a["id"], clear_since=now)
            if now - since >= rule["clear_after_s"]:
                self._resolve(a, now, "auto", "system", why_cleared(a) if why_cleared else f"condition cleared for {rule['clear_after_s']} s")
        for a in store.alert_list([OPEN], limit=100000):
            if a["rule_type"] not in types:
                continue
            rule = self.by_id.get(a["rule_id"])
            for i, esc in enumerate((rule or {}).get("escalations", []), 1):
                if a["level"] < i and now - a["opened_at"] >= esc["after_s"]:
                    store.alert_update(a["id"], level=i)
                    store.alert_event_add(a["id"], now, "escalated", "system", f"not acknowledged after {esc['after_s']} s: level {i}")
                    a["level"] = i
                    self._notify(a, "escalated", esc.get("recipients", []), rule["channels"], now)

    def _state_unknown(self, a: dict) -> bool:
        if a["rule_type"] not in ("equipment_stopped", "bypass_detected", "sensor_unhealthy", "queue_level") or not a["equipment_id"]:
            return False
        return self.engine._current(a["equipment_id"])["state"] in (NO_DATA, COMM, UNKNOWN)

    def _open(self, c: Cond, now: int) -> int:
        store, r = self.engine.store, c.rule
        aid = store.alert_insert({"alert_key": c.key, "rule_id": r["id"], "rule_type": r["type"], "severity": c.severity or r["severity"], "state": OPEN, "level": 0,
                                  "plant_id": c.plant_id, "line_id": c.line_id, "equipment_id": c.equipment_id, "subject": c.subject, "title": c.title,
                                  "detail": c.detail, "condition_text": c.condition, "threshold": r["threshold_s"] or r["threshold"], "opened_at": now,
                                  "last_seen_at": now, "clear_since": None, "acked_at": None, "acked_by": None, "resolved_at": None, "resolution": None,
                                  "recipients": r["recipients"], "channels": r["channels"], "evidence": c.evidence})
        store.alert_event_add(aid, now, "opened", "system", c.condition)
        self._notify(store.alert_get(aid), "opened", r["recipients"], r["channels"], now)
        return aid

    def _resolve(self, a: dict, now: int, how: str, actor: str, why: str) -> None:
        store = self.engine.store
        store.alert_update(a["id"], state=RESOLVED, resolved_at=now, resolution=f"{how}: {why}")
        store.alert_event_add(a["id"], now, "resolved", actor, f"{how}: {why}")
        self._notify(store.alert_get(a["id"]), "resolved", a["recipients"], a["channels"], now)

    def _notify(self, alert: dict, kind: str, recipients: list, channels: list, now: int) -> None:
        store = self.engine.store
        for ch in channels:
            if ch == "webpush":
                self._notify_webpush(alert, kind, recipients, now)
                continue
            for rcp in recipients:
                nid = store.notification_add(alert["id"], now, ch, rcp, kind, "queued")
                n = self.notifiers.get(ch)
                if n is None:
                    store.notification_update(nid, "skipped", "channel not configured")
                    continue
                payload = {"kind": kind, "recipient": rcp, "channel": ch, "alert": self.public(alert), "as_of": format_ts(now)}
                n.send(payload, lambda ok, detail, nid=nid: store.notification_update(nid, "sent" if ok else "failed", detail))

    def _notify_webpush(self, alert: dict, kind: str, recipients: list, now: int) -> None:
        """Resolve role-name recipients to actual accounts (scoped to the alert's plant/line) and push to
        each of their subscribed devices - unlike the other channels, this needs the user directory."""
        store = self.engine.store
        if self.webpush is None:
            for rcp in recipients:
                store.notification_add(alert["id"], now, "webpush", rcp, kind, "skipped", "web push is not configured on this server")
            return
        from .auth import users_with_role
        payload = {"title": (f"Escalated: {alert['title']}" if kind == "escalated" else alert["title"]), "body": alert["detail"][:180],
                  "tag": f"spi-alert-{alert['id']}", "url": "/", "severity": alert["severity"], "alert_id": alert["id"]}
        for rcp in recipients:
            users = users_with_role(store, rcp, alert["plant_id"], alert["line_id"])
            subs = store.push_list_for_users([u["id"] for u in users])
            if not subs:
                store.notification_add(alert["id"], now, "webpush", rcp, kind, "skipped", "no device subscribed for this role in this plant/line")
                continue
            for sub in subs:
                label = f"user:{sub['user_id']}:{sub['device'] or sub['endpoint'][-10:]}"
                nid = store.notification_add(alert["id"], now, "webpush", label, kind, "queued")

                def done(ok, detail, gone=False, nid=nid, sub=sub):
                    store.notification_update(nid, "sent" if ok else "failed", detail)
                    if gone:
                        store.push_remove(sub["endpoint"])
                    elif ok:
                        store.push_touch(sub["id"], int(time.time()))
                    else:
                        store.push_mark_error(sub["id"], detail)

                self.webpush.send_to_subscription(sub, payload, done)

    # ---------------- user actions ----------------
    def acknowledge(self, alert_id: int, user: str, note: str = "") -> dict:
        with self._lock:
            a = self.engine.store.alert_get(alert_id)
            if a is None:
                raise KeyError(alert_id)
            if a["state"] != OPEN:
                raise ValueError(f"alert is {a['state']}; only OPEN alerts can be acknowledged")
            now = self._now_for(a)
            self.engine.store.alert_update(alert_id, state=ACK, acked_at=now, acked_by=user)
            self.engine.store.alert_event_add(alert_id, now, "acknowledged", user, note or "acknowledged")
            return self.public(self.engine.store.alert_get(alert_id))

    def resolve(self, alert_id: int, user: str, reason: str) -> dict:
        with self._lock:
            a = self.engine.store.alert_get(alert_id)
            if a is None:
                raise KeyError(alert_id)
            if a["state"] == RESOLVED:
                raise ValueError("alert is already resolved")
            self._resolve(a, self._now_for(a), "manual", user, reason)
            return self.public(self.engine.store.alert_get(alert_id))

    def _now_for(self, a: dict) -> int:
        return self.wall_clock() if a["rule_type"] in VISION_TYPES + SECURITY_TYPES else self.engine.now

    # ---------------- read model ----------------
    def public(self, a: dict) -> dict:
        end = a["resolved_at"] if a["resolved_at"] is not None else a["last_seen_at"]
        f = lambda t: format_ts(t) if t is not None else None
        return {"id": a["id"], "rule_id": a["rule_id"], "type": a["rule_type"], "severity": a["severity"], "state": a["state"], "level": a["level"],
                "plant_id": a["plant_id"], "line_id": a["line_id"], "equipment_id": a["equipment_id"], "subject": a["subject"], "title": a["title"],
                "detail": a["detail"], "condition": a["condition_text"], "threshold": a["threshold"], "opened_at": f(a["opened_at"]),
                "last_seen_at": f(a["last_seen_at"]), "duration_s": end - a["opened_at"], "acked_at": f(a["acked_at"]), "acked_by": a["acked_by"],
                "resolved_at": f(a["resolved_at"]), "resolution": a["resolution"], "recipients": a["recipients"], "channels": a["channels"],
                "evidence": a["evidence"], "basis": "detected"}

    def list(self, states=None, line_id=None, limit=200) -> list[dict]:
        return [self.public(a) for a in self.engine.store.alert_list(states, line_id, limit)]

    def detail(self, alert_id: int) -> dict | None:
        a = self.engine.store.alert_get(alert_id)
        if a is None:
            return None
        ev = [{**e, "ts": format_ts(e["ts"])} for e in self.engine.store.alert_events(alert_id)]
        return {**self.public(a), "audit_trail": ev, "notifications": [{**n, "ts": format_ts(n["ts"])} for n in self.engine.store.notifications(alert_id)]}
