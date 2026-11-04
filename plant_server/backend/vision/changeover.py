"""Changeover verification (docs/vision-system/CHANGEOVER-VERIFY.md sections 2 and 3): a second person checks the values of a
changeover before the line may run.

The station's heartbeat carries its changeover record and an ask number; the answer carries the state of the check. A request is
one row per (station, code, ask), made while the check is on for the line. A decision is only ever appended, and the first one
decides. Every doubt is "not accepted": the answer is `accepted` only for a request a person with a reviewer role accepted.
"""
from __future__ import annotations

import hashlib
import json
import logging

from ..auth import Principal
from .events import changeover_problems
from .ingest import _settle
from .review import MAX_LIMIT, MAX_NOTE, Denied, can_review

KEY = "changeover_verify"
DEFAULT = True
STATES = ("pending", "accepted", "refused", "superseded")
FILTERS = ("pending", "accepted", "refused", "superseded", "all")
DECISIONS = {"accept": "accepted", "refuse": "refused"}
_COLS = ("id, station, plant, line, code, ask, record, operator, changed_at, asked_at, state, decided_by, decided_name, decided_at, note")
# What changed is shown for these first; the batch fields follow in the order sent.
_PSEUDO = (("job", "Job"), ("job_version", "Job version"), ("variant", "Variant"), ("job_digest", "Job file digest"))


# ---- the record and its code ------------------------------------------------------------------
log = logging.getLogger("vision")
LINE_NOTE = "Accepted at the line PC while the plant server could not be reached"


def _text(s: str) -> bytes:
    b = s.encode("utf-8")
    return str(len(b)).encode("ascii") + b":" + b


def canonical(job: str, job_version: int, variant: str, batch: list[dict], job_digest: str = "") -> bytes:
    """The bytes the code is made of: each text as its UTF-8 length, a colon and its bytes, joined with nothing between. The job file's
    digest ends it, only when there is one, so a record from a station without a digest has the code it always had (CV-11)."""
    out = [_text(job), _text(str(job_version)), _text(variant)]
    for f in batch:
        out += [_text(f["key"]), _text(f["label"]), _text(f["value"])]
    if job_digest:
        out.append(_text(job_digest))
    return b"".join(out)


def code_of(c: dict) -> str:
    return hashlib.sha256(canonical(c["job"], c["job_version"], c.get("variant", ""), c["batch"], c.get("job_digest", ""))).hexdigest()


def code_problem(c: dict) -> str | None:
    """None when the heartbeat's changeover object is valid and its code is the SHA-256 of its own record."""
    if p := changeover_problems(c):
        return "; ".join(p)
    if code_of(c) != c["code"]:
        return "changeover code does not match its record"
    return None


def _record(c: dict) -> dict:
    r = {"job": c["job"], "job_version": c["job_version"], "variant": c.get("variant", ""),
         "batch": [{"key": f["key"], "label": f["label"], "value": f["value"]} for f in c["batch"]]}
    if c.get("job_digest"):
        r["job_digest"] = c["job_digest"]
    return r


def changes(old: dict | None, new: dict) -> list[dict]:
    """[{key, label, old, new}] of what differs between two records; job, job_version and variant come as the pseudo keys of that name.
    old is None for a field that was not there, new is None for one that is gone. A label that changed adds label_old."""
    if old is None:
        return []

    def fields(r):
        seen: dict = {}
        out: dict = {}
        for key, label in _PSEUDO:
            if key == "job_digest" and not (old.get(key) or new.get(key)):
                continue    # neither record has one (older stations): nothing to show
            out[(key, 0)] = (label, str(r.get(key, "")))
        for f in r.get("batch", []):
            n = seen[f["key"]] = seen.get(f["key"], 0) + 1
            out[(f["key"], n)] = (f["label"], f["value"])
        return out
    a, b = fields(old), fields(new)
    out = []
    for k in list(b) + [k for k in a if k not in b]:
        before, after = a.get(k), b.get(k)
        if before == after:
            continue
        item = {"key": k[0], "label": (after or before)[0], "old": before[1] if before else None, "new": after[1] if after else None}
        if before and after and before[0] != after[0]:
            item["label_old"] = before[0]
        out.append(item)
    return out


# ---- who may decide ----------------------------------------------------------------------------
def decider(principal: Principal) -> tuple[str, str]:
    """(user name, display name) of the person who decides. A service key is nobody, and without any sign-in a person cannot be named."""
    if principal.kind != "user":
        raise Denied(403, "a decision names a person: sign in with your own account, not a service key")
    return principal.username, principal.name or principal.username


def _view_scope(principal: Principal) -> tuple[str, list] | None:
    """SQL limiting rows to the plants and lines the principal covers; '' for everything, None for nothing."""
    if principal.kind in ("service", "anon") or principal.is_admin():
        return "", []
    plants, lines = set(), set()
    for a in principal.assignments:
        if a["scope_level"] == "org":
            return "", []
        (plants if a["scope_level"] == "plant" else lines).add(a["scope_id"])
    parts, args = [], []
    for col, ids in (("plant", plants), ("line", lines)):
        if ids:
            parts.append(f"{col} IN ({','.join('?' * len(ids))})")
            args += sorted(ids)
    return (" AND (" + " OR ".join(parts) + ")", args) if parts else None


def _bool(v: str | None) -> bool | None:
    return None if v is None else v == "true"


class Changeover:
    def __init__(self, store, clock):
        self.store, self.clock = store, clock

    # ---- the setting -----------------------------------------------------------------------------
    def required(self, plant: str, line: str) -> bool:
        """A line's own setting beats its plant's; with neither the check is on."""
        for level, sid in (("line", line), ("plant", plant)):
            v = _bool(self.store.setting_get(level, sid, KEY))
            if v is not None:
                return v
        return DEFAULT

    def set_setting(self, plant: str, line: str | None, value: bool, by: str) -> None:
        level, sid = ("line", line) if line else ("plant", plant)
        self.store.setting_add(level, sid, KEY, "true" if value else "false", by, self.clock())

    def settings(self) -> list[dict]:
        lines = {r["id"]: r["plant_id"] for r in self.store.master_list("line")}
        out = []
        for r in self.store.settings_in_force():
            line = r["scope_id"] if r["scope_level"] == "line" else None
            plant = lines.get(line) if line else r["scope_id"]
            out.append({"key": r["key"], "plant": plant, "line": line, "value": r["value"] == "true",
                        "updated_by": r["updated_by"], "updated_at": r["updated_at"]})
        return out

    # ---- the heartbeat -----------------------------------------------------------------------------
    @staticmethod
    def _answer(required: bool, code: str, ask: int, state: str, by="", at=0, note="") -> dict:
        return {"required": required, "code": code, "ask": ask, "state": state, "by": by or "", "at": at or 0, "note": note or ""}

    def on_heartbeat(self, device: dict, c: dict, now: int) -> dict:
        """Keep the station's request (after the heartbeat is stored) and return the answer. `c` has passed code_problem."""
        plant, line, station = device["plant_id"], device["line_id"], device["id"]
        code, ask = c["code"], c["ask"]
        if not self.required(plant, line):
            return self._answer(False, code, ask, "not_needed")
        store = self.store
        with store._lock:
            _settle(store)
            try:
                key = (station, code, ask)
                row = store._x("SELECT id, state FROM vision_changeover WHERE station = ? AND code = ? AND ask = ?", key).fetchone()
                if row is None:
                    store._x("INSERT INTO vision_changeover(station, plant, line, code, ask, record, operator, changed_at, asked_at, state, note) "
                             "VALUES (?,?,?,?,?,?,?,?,?,'pending','')",
                             (station, plant, line, code, ask, json.dumps(_record(c), separators=(",", ":"), ensure_ascii=False),
                              c.get("operator", ""), c.get("changed_at", now), now))
                    row = store._x("SELECT id, state FROM vision_changeover WHERE station = ? AND code = ? AND ask = ?", key).fetchone()
                elif row[1] == "superseded":
                    # the station asks for it again (it went back to these values): it is the current request once more
                    store._x("UPDATE vision_changeover SET state = 'pending', asked_at = ? WHERE id = ? AND state = 'superseded'", (now, row[0]))
                # the current request supersedes the station's older ones that nobody decided
                store._x("UPDATE vision_changeover SET state = 'superseded' WHERE station = ? AND state = 'pending' AND id <> ?", (station, row[0]))
                # A second person accepted at the line PC while this server could not be reached (owner, 2026-11-01): kept as
                # their decision. One made here first (a refusal while only the station was cut off) stays: the first decides.
                if (la := c.get("accepted_at_line")) is not None:
                    st = store._x("SELECT state FROM vision_changeover WHERE id = ?", (row[0],)).fetchone()[0]
                    if st == "pending":
                        at = la.get("at") or now
                        store._x("INSERT INTO vision_changeover_decision(changeover_id, user_id, name, decision, note, at) VALUES (?,?,?,?,?,?)",
                                 (row[0], None, la.get("name") or la["by"], "accept_at_line", LINE_NOTE, at))
                        store._x("UPDATE vision_changeover SET state = 'accepted', decided_by = ?, decided_name = ?, decided_at = ?, note = ? "
                                 "WHERE id = ? AND state = 'pending'", ("line:" + la["by"], la.get("name") or la["by"], at, LINE_NOTE, row[0]))
                    elif st == "refused":
                        log.warning("vision: %s accepted changeover %s at the line PC, but it was refused here first", station, row[0])
                cur = store._x("SELECT state, decided_name, decided_by, decided_at, note FROM vision_changeover WHERE id = ?", (row[0],)).fetchone()
                store.db.commit()
            except Exception:
                store.db.rollback()
                raise
        state = cur[0] if cur[0] in ("pending", "accepted", "refused") else "pending"
        return self._answer(True, code, ask, state, cur[1] or cur[2], cur[3], cur[4])

    # ---- reading -----------------------------------------------------------------------------------
    def _items(self, rows) -> list[dict]:
        out = []
        for r in rows:
            record = json.loads(r[6])
            with self.store._lock:
                p = self.store._x(f"SELECT {_COLS} FROM vision_changeover WHERE station = ? AND state = 'accepted' AND id < ? ORDER BY id DESC LIMIT 1",
                                  (r[1], r[0])).fetchone()
            prev = json.loads(p[6]) if p else None
            out.append({"id": r[0], "station": r[1], "plant": r[2], "line": r[3], "code": r[4], "short_code": r[4][:8], "ask": r[5],
                        "record": record, "operator": r[7], "changed_at": r[8], "asked_at": r[9], "state": r[10],
                        "decided_by": r[11], "decided_name": r[12], "decided_at": r[13], "note": r[14],
                        "previous": None if p is None else {"id": p[0], "code": p[4], "short_code": p[4][:8], "record": prev, "decided_name": p[12],
                                                            "decided_at": p[13]},
                        "changes": changes(prev, record)})
        return out

    def rows(self, principal: Principal, state: str, plant: str | None, line: str | None, limit: int) -> list[dict]:
        """Requests newest first, at most `limit`, of the plants and lines the principal may view."""
        scope = _view_scope(principal)
        if scope is None:
            return []
        w, a = " WHERE 1 = 1" + scope[0], list(scope[1])
        if state != "all":
            w += " AND state = ?"
            a.append(state)
        for col, v in (("plant", plant), ("line", line)):
            if v:
                w += f" AND {col} = ?"
                a.append(v)
        with self.store._lock:
            got = self.store._x(f"SELECT {_COLS} FROM vision_changeover{w} ORDER BY id DESC LIMIT ?", a + [min(max(limit, 1), MAX_LIMIT)]).fetchall()
        return self._items([r for r in got if principal.can_view(r[2], r[3])])

    def _row(self, principal: Principal, cid: int):
        with self.store._lock:
            r = self.store._x(f"SELECT {_COLS} FROM vision_changeover WHERE id = ?", (cid,)).fetchone()
        if r is None or not principal.can_view(r[2], r[3]):
            raise Denied(404, f"no changeover request {cid}")      # outside the caller's view it is not said to exist
        return r

    def one(self, principal: Principal, cid: int) -> dict:
        item = self._items([self._row(principal, cid)])[0]
        with self.store._lock:
            hist = self.store._x("SELECT id, name, decision, note, at FROM vision_changeover_decision WHERE changeover_id = ? ORDER BY id DESC",
                                 (cid,)).fetchall()
        item["decisions"] = [{"id": h[0], "name": h[1], "decision": h[2], "note": h[3], "at": h[4]} for h in hist]
        return item

    # ---- deciding ------------------------------------------------------------------------------------
    def decide(self, principal: Principal, cid: int, decision: str, note: str) -> dict:
        username, name = decider(principal)
        r = self._row(principal, cid)
        if not can_review(principal, r[2], r[3]):
            raise Denied(403, "only an administrator, plant, production or quality manager of this line decides a changeover")
        if r[7] and r[7].strip().casefold() == username.strip().casefold():
            raise Denied(403, "the person who made the changeover cannot accept it: a second person checks")
        if decision not in DECISIONS:
            raise Denied(400, "decision must be accept or refuse")
        note = (note or "").strip()
        if decision == "refuse" and not note:
            raise Denied(400, "a refusal needs a note")
        if len(note) > MAX_NOTE:
            raise Denied(400, f"the note is over {MAX_NOTE} characters")
        store, now = self.store, self.clock()
        with store._lock:
            _settle(store)
            try:
                state = store._x("SELECT state FROM vision_changeover WHERE id = ?", (cid,)).fetchone()[0]
                if state != "pending":
                    raise Denied(409, f"this request is already {state}" if state != "superseded" else "this request was superseded by a newer one")
                # The decision row and the request's state change together, or not at all.
                store._x("INSERT INTO vision_changeover_decision(changeover_id, user_id, name, decision, note, at) VALUES (?,?,?,?,?,?)",
                         (cid, principal.user_id, name, decision, note, now))
                store._x("UPDATE vision_changeover SET state = ?, decided_by = ?, decided_name = ?, decided_at = ?, note = ? WHERE id = ? AND state = 'pending'",
                         (DECISIONS[decision], username, name, now, note, cid))
                store.db.commit()
            except Exception:
                store.db.rollback()
                raise
        return self.one(principal, cid)
