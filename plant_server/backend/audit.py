"""The plant server's audit trail writer (Part 11; docs/vision-system/PART11-DESIGN.md section 5, D5, D6).

One entry format everywhere: a JSON object with the section 5 fields, stored as the exact canonical JSON bytes that
were hashed (sorted keys, no spaces, UTF-8: RFC 8785 for the strings, integers, booleans and nulls used here).
`hash` = SHA-256 of the entry without `hash`; each entry holds the hash of the one before (`prev_hash`), `seq` has
no gaps, and the first entry's `prev_hash` is the SHA-256 of `source` and the chain's start time. The store refuses
UPDATE and DELETE on the table. check() walks the chain in batches; a break reports the first bad seq and deletes
nothing.

This first part writes the sign-in entries (PT11-05, PT11-06, PT11-08, PT11-21): sign-in, sign-in.failed, lock,
sign-out, unlock and unlock.failed (the mobile PIN), user edit/disable (with reasons, PT11-09a), and the signed re-anchor. Upload from the line PCs, export and the
report are later items of build order step 2.

Anchor (D6 for the server's own chain, PT11-09): the chain alone cannot show that its newest entries were removed,
since what is left still links. An Anchor keeps the head (newest seq and hash) in a file outside the database,
rewritten after each entry; verify() then also needs that exact entry in the table. The anchor never moves back,
and once a break is found it stays where it was, so a restart finds the same break.

Re-anchor (PT11-09a b): after a break has been investigated, an Admin signs (sign(), a `sign` entry carrying the
signature record) and reanchor() writes a `chain.reanchor` entry that names the signature and records each break it
covers (its position, the row's seq, its stored hash and the hash of its bytes) and the old anchored head; the anchor then starts again at that
entry. Nothing is removed or rewritten: verify() still walks past each covered break and lists it under
"investigated" with the re-anchor that covers it. A covered break counts only while its row is exactly as recorded, so
a second change at the same place is a new break.
"""
from __future__ import annotations

import hashlib
import json
import os
import threading
import time
import uuid

SCHEMA = 1
SOURCE = {"site": None, "line": None, "station": None, "component": "plant-server", "device_id": None}


def canonical(obj) -> str:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def sha256(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()


def clean(text, limit: int = 200) -> str | None:
    """Control characters (NUL breaks PostgreSQL) out, length capped: these values come from the caller."""
    if text is None:
        return None
    return "".join(c for c in str(text) if c >= " " and c != "\x7f")[:limit]


def mask_username(typed: str) -> str:
    """A name that matches no account is often a password typed in the wrong field: keep only enough to spot a
    pattern (PART11-DESIGN 5: secrets never appear)."""
    t = clean(typed) or ""
    return f"{t[:2]}…({len(t)})"


def _iso(ts: float) -> tuple[str, str]:
    lt = time.localtime(ts)
    off = lt.tm_gmtoff
    sign = "+" if off >= 0 else "-"
    return (time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(ts)) + f".{int(ts * 1000) % 1000:03d}Z",
            f"{sign}{abs(off) // 3600:02d}:{abs(off) % 3600 // 60:02d}")


def user_of(store, u: dict | None) -> dict | None:
    if u is None:
        return None
    roles = [f"{a['role']}@{a['scope_level']}" + (f":{a['scope_id']}" if a["scope_id"] else "") for a in store.role_list(u["id"])]
    return {"kind": "person", "id": str(u["id"]), "username": u["username"], "full_name": u["name"], "roles": roles}


def write(store, action: str, *, user: dict | None = None, user_id: int | None = None, client: str | None = None,
          obj: dict | None = None, changes: list | None = None, reason: str | None = None, outcome: str = "ok",
          detail: dict | None = None, signature_id: str | None = None) -> dict:
    """Appends one entry; returns it (with seq and hash)."""
    now = time.time()
    ts, tz = _iso(now)

    def build(last_seq: int, last_hash: str | None):
        prev = last_hash if last_hash is not None else sha256(canonical(SOURCE) + ts)
        entry = {"schema": SCHEMA, "id": str(uuid.uuid4()), "source": SOURCE, "seq": last_seq + 1, "ts": ts, "tz_offset": tz,
                 "clock": "synced", "user": user, "client": clean(client), "action": action, "object": obj,
                 "changes": changes or [], "reason": reason, "signature_id": signature_id, "outcome": outcome,
                 "detail": detail, "prev_hash": prev}
        body = canonical(entry)
        h = sha256(body)
        row = {"seq": entry["seq"], "ts": int(now), "action": action, "outcome": outcome, "user_id": user_id,
               "object_type": (obj or {}).get("type"), "object_id": (obj or {}).get("id"), "entry": {**entry, "hash": h}}
        return row, body, h

    entry = store.audit_append(build)["entry"]
    anchor = getattr(store, "audit_anchor", None)
    if anchor is not None:
        anchor.moved(entry["seq"], entry["hash"])
    return entry


def entries(store, **kw) -> list[dict]:
    """Newest first, as written plus their hash."""
    return [{**json.loads(e), "hash": h} for e, h in store.audit_query(**kw)]


def check(store, investigated: dict | None = None) -> dict:
    """Walks the whole chain (PT11-23): {"ok", "entries" (the seq walked to), "first_bad_seq"}, and "investigated"
    ([{"seq", "reanchor_seq"}]) when it walked past breaks a re-anchor covers. investigated: {position: {"row_seq",
    "hash", "body_hash", "by"}} from investigated_breaks()."""
    got = _walk(store, investigated or {})
    out = {"ok": got["ok"], "entries": got["entries"], "first_bad_seq": got["first_bad_seq"]}
    return {**out, "investigated": got["investigated"]} if got["investigated"] else out


def _walk(store, investigated: dict, every: bool = False) -> dict:
    """every=True walks past every break and lists them all under "breaks" ({"seq" (where the chain broke),
    "row_seq", "hash", "body_hash" (the row found there: its stored hash and the hash of its bytes)})."""
    n, prev_hash, after, seen, breaks = 0, None, 0, [], []
    while True:
        batch = store.audit_batch(after)
        if not batch:
            bad = next((b["seq"] for b in breaks), None)
            return {"ok": bad is None, "entries": n, "first_bad_seq": bad, "investigated": seen, "breaks": breaks}
        for seq, body, h in batch:
            try:
                e = json.loads(body)
                start = sha256(canonical(e["source"]) + e["ts"]) if prev_hash is None else prev_hash
                good = seq == n + 1 and e["seq"] == seq and sha256(body) == h and e["prev_hash"] == start
            except (ValueError, KeyError, TypeError):
                good = False
            if not good:
                inv, body_hash = investigated.get(n + 1), sha256(body)
                if (inv is not None and inv["row_seq"] == seq and inv["hash"] == h and inv["body_hash"] == body_hash
                        and inv["by"] > seq):
                    seen.append({"seq": n + 1, "reanchor_seq": inv["by"]})
                elif every:
                    breaks.append({"seq": n + 1, "row_seq": seq, "hash": h, "body_hash": body_hash})
                else:
                    return {"ok": False, "entries": n, "first_bad_seq": n + 1, "investigated": seen, "breaks": []}
            n, prev_hash, after = (n + 1 if good else seq), h, seq


def investigated_breaks(store) -> dict:
    """{position: {"row_seq", "hash", "body_hash", "by" (the re-anchor's seq)}} from the chain.reanchor entries."""
    out = {}
    for body, _ in store.audit_query(action="chain.reanchor", limit=100_000):
        try:
            e = json.loads(body)
            if e["action"] != "chain.reanchor" or e["outcome"] != "ok":
                continue
            for b in e["detail"]["breaks"]:
                out.setdefault(int(b["seq"]), {"row_seq": int(b["row_seq"]), "hash": str(b["hash"]),
                                                "body_hash": str(b["body_hash"]), "by": int(e["seq"])})
        except (ValueError, KeyError, TypeError):
            continue
    return out


class Anchor:
    """The head of the server's chain, kept in a file outside the database (PT11-09 c). Best on another disk or
    account than the database: whoever can change both can hide a removal."""

    def __init__(self, path: str):
        self.path, self.frozen = path, False
        self._lock = threading.Lock()
        try:
            with open(path, encoding="utf-8") as fh:
                got = json.load(fh)
            self.head = {"seq": int(got["seq"]), "hash": str(got["hash"])}
        except FileNotFoundError:
            self.head = None
        except (ValueError, KeyError, TypeError):
            self.head = {"seq": None, "hash": None}          # unreadable: a break, never silently started afresh

    def moved(self, seq: int, h: str) -> None:
        with self._lock:
            if self.frozen or (self.head is not None and (self.head["seq"] is None or seq <= self.head["seq"])):
                return
            self._save(seq, h)

    def restart(self, seq: int, h: str) -> None:
        """Only reanchor() calls this: the anchor starts again at its chain.reanchor entry, back or forward."""
        with self._lock:
            self._save(seq, h)
            self.frozen = False

    def _save(self, seq: int, h: str) -> None:
        tmp = f"{self.path}.tmp"
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            fh.write(canonical({"seq": seq, "hash": h}))
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp, self.path)
        self.head = {"seq": seq, "hash": h}

    def check(self, store) -> dict:
        """{"ok", "seq" (the anchored head), "note"}. A missing file starts the anchor at the table's head."""
        if self.head is None:
            last = store.audit_head()
            if last is None:
                return {"ok": True, "seq": None, "note": "no entries yet"}
            self.moved(*last)
            return {"ok": True, "seq": last[0], "note": f"anchor started at seq {last[0]} (no anchor file before)"}
        seq = self.head["seq"]
        if seq is None:
            note = f"the anchor file {self.path} is unreadable"
        else:
            h = store.audit_hash(seq)
            if h == self.head["hash"]:
                return {"ok": True, "seq": seq, "note": None}
            note = (f"the entry at seq {seq} that the anchor holds is gone: newest entries removed" if h is None
                    else f"the entry at seq {seq} no longer has the anchored hash: rewritten")
        with self._lock:
            self.frozen = True
        return {"ok": False, "seq": seq, "note": note}


def use_anchor(store, path: str) -> Anchor:
    """Every write() to this store moves the anchor from now on."""
    store.audit_anchor = Anchor(path)
    return store.audit_anchor


def verify(store) -> dict:
    """check() (walking past the breaks a re-anchor covers) and, when the store has an anchor, the anchor too:
    {"ok", "entries", "first_bad_seq"[, "investigated"][, "anchor"]}."""
    got = check(store, investigated_breaks(store))
    anchor = getattr(store, "audit_anchor", None)
    if anchor is None:
        return got
    a = anchor.check(store)
    return {**got, "ok": got["ok"] and a["ok"], "anchor": a}


CHAIN = {"type": "audit_chain", "id": SOURCE["component"], "name": "plant server audit trail", "version": None}


def sign(store, u: dict, *, meaning: str, obj: dict, content: dict, reason: str, method: str = "password") -> dict:
    """An electronic signature (PART11-DESIGN 6) after the signer gave the secret again: a `sign` entry that carries
    the signature record, so the signature is inside the hash chain. Returns the record."""
    ts, tz = _iso(time.time())
    record = {"id": str(uuid.uuid4()), "user": {"id": str(u["id"]), "username": u["username"], "full_name": u["name"]},
              "meaning": meaning, "ts": ts, "tz_offset": tz, "object": obj, "content_hash": sha256(canonical(content)),
              "method": method, "reason": reason}
    write(store, "sign", user=user_of(store, u), user_id=u["id"], obj=obj, reason=reason, signature_id=record["id"],
          detail={"signature": record})
    return record


def reanchor(store, u: dict, reason: str) -> dict:
    """PT11-09a b: the signed re-anchor after a break was investigated. Raises ValueError when the chain has no break.
    Returns the chain.reanchor entry."""
    breaks = _walk(store, investigated_breaks(store), every=True)["breaks"]
    anchor = getattr(store, "audit_anchor", None)
    a = anchor.check(store) if anchor is not None else None
    if not breaks and (a is None or a["ok"]):
        raise ValueError("the audit chain has no break to re-anchor")
    findings = {"breaks": breaks, "anchor": None if a is None or a["ok"] else {**a, "hash": anchor.head["hash"]}}
    sig = sign(store, u, meaning="responsibility", obj=CHAIN, content=findings, reason=reason)
    entry = write(store, "chain.reanchor", user=user_of(store, u), user_id=u["id"], obj=CHAIN, reason=reason,
                  signature_id=sig["id"], detail={**findings, "note": "break investigated; the anchor starts again here, "
                                                  "nothing removed or rewritten"})
    if anchor is not None:
        anchor.restart(entry["seq"], entry["hash"])
    return entry
