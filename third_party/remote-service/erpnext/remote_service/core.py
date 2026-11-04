"""What the relay sends for one finished remote service session, checked and turned into a Remote Service Log and a draft
Timesheet (docs/DESIGN.md section 5). Plain Python: tested without ERPNext."""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import datetime, timezone

SESSION_ID = re.compile(r"^[0-9a-f]{8,32}$")
MACHINE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{1,40}$")
MAX_ACTIONS = 2000
MAX_DETAIL = 1000


class BadRecord(ValueError):
    """The record cannot be saved as it is; the relay keeps it and the reason is shown in its audit."""


@dataclass
class Session:
    session_id: str
    machine: str
    machine_name: str
    site: str
    licence: str
    engineer: str
    engineer_name: str
    employee: str
    role: str
    reason: str
    started: datetime   # UTC, naive
    ended: datetime     # UTC, naive
    end_reason: str
    actions: list[tuple[datetime, str, str]]
    ledger_head: str = ""

    @property
    def hours(self) -> float:
        return round(max((self.ended - self.started).total_seconds(), 0) / 3600, 2)


def utc(value: object, field: str) -> datetime:
    """An ISO time from the relay ("2026-11-04T10:00:00Z") as naive UTC."""
    if not isinstance(value, str) or not value:
        raise BadRecord(f"{field} is missing")
    try:
        t = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as e:
        raise BadRecord(f"{field} is not a time: {value!r}") from e
    if t.tzinfo is not None:
        t = t.astimezone(timezone.utc).replace(tzinfo=None)
    return t


def text(raw: dict, field: str, limit: int = 140, required: bool = False) -> str:
    v = raw.get(field, "")
    if not isinstance(v, str):
        raise BadRecord(f"{field} must be text")
    v = "".join(c for c in v.strip() if c.isprintable())[:limit]
    if required and not v:
        raise BadRecord(f"{field} is missing")
    return v


def parse(raw: object) -> Session:
    if not isinstance(raw, dict):
        raise BadRecord("the session must be an object")
    sid = text(raw, "session_id", 32, True)
    machine = text(raw, "machine", 41, True)
    if not SESSION_ID.match(sid):
        raise BadRecord("session_id is not a session id")
    if not MACHINE_ID.match(machine):
        raise BadRecord("machine is not a machine number")
    role = text(raw, "role", 10) or "service"
    if role not in ("service", "oem"):
        raise BadRecord("role must be service or oem")
    started, ended = utc(raw.get("started"), "started"), utc(raw.get("ended"), "ended")
    if ended < started:
        raise BadRecord("ended is before started")
    actions = []
    for a in (raw.get("actions") or [])[:MAX_ACTIONS]:
        if isinstance(a, dict):
            try:
                at = utc(a.get("time"), "action time")
            except BadRecord:
                at = started
            actions.append((at, text(a, "kind", 40), text(a, "detail", MAX_DETAIL)))
    return Session(sid, machine, text(raw, "machine_name"), text(raw, "site"), text(raw, "licence", 64), text(raw, "engineer", 64, True),
                   text(raw, "engineer_name"), text(raw, "employee", 64), role, text(raw, "reason", 200), started, ended,
                   text(raw, "end_reason", 20), actions, text(raw, "ledger_head", 128))


def log_doc(s: Session, customer: str | None, to_local) -> dict:
    """The Remote Service Log. `to_local` turns naive UTC into ERPNext's system time zone."""
    return {
        "doctype": "Remote Service Log", "session_id": s.session_id, "machine": s.machine, "machine_name": s.machine_name, "site": s.site,
        "licence": s.licence, "customer": customer, "engineer": s.engineer, "engineer_name": s.engineer_name, "employee": s.employee or None,
        "role": s.role, "reason": s.reason, "started": to_local(s.started), "ended": to_local(s.ended), "duration_hours": s.hours,
        "end_reason": s.end_reason, "ledger_head": s.ledger_head,
        "actions": [{"time": to_local(t), "kind": k, "detail": d} for t, k, d in s.actions],
    }


def timesheet_doc(s: Session, customer: str | None, activity_type: str | None, to_local) -> dict:
    """A draft Timesheet for the engineer: one time log for the session. The engineer adds the work note and submits it."""
    where = s.machine_name or s.machine
    description = f"Remote service on {where} ({s.machine}{', ' + s.site if s.site else ''}). Reason: {s.reason or '-'}. " \
                  f"{len(s.actions)} recorded actions; see Remote Service Log {s.session_id}."
    return {
        "doctype": "Timesheet", "employee": s.employee, "customer": customer, "note": description,
        "time_logs": [{
            "activity_type": activity_type, "from_time": to_local(s.started), "to_time": to_local(s.ended),
            "hours": s.hours, "description": description, "is_billable": 0,
        }],
    }
