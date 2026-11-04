"""Station ingest (PL2): one batch of a station's stored events, in one transaction.

The contract is the station agent's (dotnet/StationAgent/PlantLink.cs): the answer's stored_up_to is
the highest seq N of the batch such that N and every earlier event of the batch are stored, a kept
reject, or a duplicate. A batch is refused whole (and nothing is written) when it is malformed or names
another station; a number sent again with other content is a conflict, answered 409 and never
acknowledged (a restored disk image or a copied queue file).
"""
from __future__ import annotations

import json
import logging
import re
import sqlite3

from . import events, grades

MAX_BATCH = 1000


class Refused(Exception):
    """The whole batch is refused: 400, or 403 when it names another station."""

    def __init__(self, message: str, status: int = 400):
        super().__init__(message)
        self.status = status


class Conflict(Exception):
    """A sequence number came again with other content: 409, recorded in vision_conflict."""


def _same(stored: str, el) -> bool:
    try:
        return json.loads(stored) == el
    except ValueError:
        return False


def _data_error(e: Exception) -> bool:
    # Content the database cannot hold (PostgreSQL class 22, e.g. a NUL in text): a reject, not a failure.
    # An OverflowError or ValueError is a number or text the driver could not bind: the same kind of content.
    return isinstance(e, (sqlite3.DataError, OverflowError, ValueError)) or str(getattr(e, "sqlstate", "") or "").startswith("22")


def ingest(store, device: dict, batch, now_ms: int) -> dict:
    if not isinstance(batch, list):
        raise Refused("the batch is not a JSON array")
    if not batch:
        raise Refused("the batch is empty")
    items, queue, prev = [], None, 0
    for el in batch[:MAX_BATCH]:
        if not isinstance(el, dict):
            raise Refused("an element is not an object")
        seq = el.get("seq")
        if not events._int(seq) or seq <= 0:
            raise Refused("an element has no seq, or one beyond a 64-bit number")
        q = el.get("queue_id")
        if why := events.queue_id_problem(q):
            raise Refused(why)
        queue = queue or q
        if q != queue:
            raise Refused("a batch holds one queue_id")
        if seq <= prev:
            raise Refused("seq must rise through the batch")
        prev = seq
        # The token names the device: a station never writes for another (or for another line).
        for field, want in (("plant", device["plant_id"]), ("line", device["line_id"]), ("station", device["id"])):
            if el.get(field) != want:
                raise Refused(f"an event's {field} is not this station's", status=403)
        items.append((seq, el))

    did = device["id"]
    stored = dups = rejected = 0
    gaps, fresh = [], []
    with store._lock:
        # The connection is shared with the other modules: write out anything they left pending, so the
        # rollback of a refused batch below undoes this batch only.
        _settle(store)
        # One transaction for the whole batch. SQLite would otherwise make the first savepoint its own
        # transaction and commit it on release, so a conflict could not undo the events before it.
        if isinstance(store.db, sqlite3.Connection) and not store.db.in_transaction:
            store.db.execute("BEGIN")
        try:
            row = store._x("SELECT max_seq FROM vision_queue WHERE device_id = ? AND queue_id = ?", (did, queue)).fetchone()
            if row is None:
                store._x("INSERT INTO vision_queue(device_id, queue_id, max_seq) VALUES (?,?,0)", (did, queue))
            max_seq = row[0] if row else 0
            for seq, el in items:
                raw = json.dumps(el, separators=(",", ":"), ensure_ascii=False)
                if seq <= max_seq:
                    old = store._x("SELECT body FROM vision_event WHERE device_id = ? AND queue_id = ? AND seq = ?", (did, queue, seq)).fetchone() \
                        or store._x("SELECT body FROM vision_reject WHERE device_id = ? AND queue_id = ? AND seq = ?", (did, queue, seq)).fetchone()
                    if old is not None:
                        if _same(old[0], el):
                            dups += 1
                            continue
                        raise Conflict(f"seq {seq} of queue {queue} was stored before with other content; "
                                       f"this station's queue file was restored or copied")
                    # inside a recorded gap: a late arrival, stored below
                elif seq > max_seq + 1:
                    gaps.append((max_seq + 1, seq - 1))
                    store._x("INSERT INTO vision_gap VALUES (?,?,?,?,?)", (did, queue, max_seq + 1, seq - 1, now_ms))
                    max_seq = seq
                else:
                    max_seq = seq
                why = "; ".join(events.problems(el)) or ("a heartbeat is not a stored event" if el.get("kind") == "heartbeat" else "")
                if not why:
                    store._x("SAVEPOINT ev")
                    try:
                        store._x("INSERT INTO vision_event VALUES (?,?,?,?,?,?,?)", (did, queue, seq, el["ts"], el["kind"], raw, now_ms))
                        live = el
                        if el["kind"] == "inspection.result":
                            _keep_result(store, did, queue, seq, el, now_ms)
                            # The live floor's NG strip opens the part by the server's own id, never one the station sent.
                            live = {**el, "result_id": None if el["ok"] else _result_id(store, did, queue, seq)}
                        store._x("RELEASE SAVEPOINT ev")
                        stored += 1
                        fresh.append(live)
                        _keep_latest(store, did, el, raw, now_ms)
                        continue
                    except Exception as e:  # noqa: BLE001 - only data errors become rejects; anything else re-raises
                        store._x("ROLLBACK TO SAVEPOINT ev")
                        store._x("RELEASE SAVEPOINT ev")
                        if not _data_error(e):
                            raise
                        why = f"the database refused it: {e}"
                rejected += 1
                store._x("INSERT INTO vision_reject VALUES (?,?,?,?,?,?) ON CONFLICT DO NOTHING",
                         (did, queue, seq, why, raw.replace("\x00", "\\u0000"), now_ms))
            store._x("UPDATE vision_queue SET max_seq = ? WHERE device_id = ? AND queue_id = ?", (max_seq, did, queue))
            store._x("UPDATE device SET last_seen_at = ? WHERE id = ?", (now_ms // 1000, did))
            store.db.commit()
        except Conflict:
            store.db.rollback()
            store._x("INSERT INTO vision_conflict VALUES (?,?,?,?,?)", (did, queue, seq, raw.replace("\x00", "\\u0000"), now_ms))
            store.db.commit()
            raise
        except Exception:
            store.db.rollback()
            raise
    return {"stored_up_to": items[-1][0], "stored": stored, "duplicates": dups, "rejected": rejected, "gaps": len(gaps), "fresh": fresh}


def _settle(store) -> None:
    if getattr(store.db, "in_transaction", False) or _pg_busy(store.db):
        store.db.commit()


def _pg_busy(db) -> bool:
    info = getattr(db, "info", None)   # psycopg 3
    return info is not None and getattr(info, "transaction_status", 0) != 0


IMAGE_ID = re.compile(r"[A-Za-z0-9._-]{1,64}")


def _text(v):
    # PostgreSQL text cannot hold a NUL; the event itself is kept whole in vision_event.body.
    return v.replace("\x00", "") if isinstance(v, str) else None


def _keep_result(store, did: str, queue: str, seq: int, el: dict, now_ms: int, ask: bool = True) -> int:
    """One vision_result row per stored inspection.result (OK and NG), and a 'wanted' image request for an NG with an image id.
    It runs inside the event's savepoint, so a result is never stored without its event, nor an event twice with its row:
    a refused or conflicting batch rolls both back, and a duplicate never reaches here. `ask` is False for the backfill of old events."""
    image_id = _text(el.get("image_id")) or None
    cur = store._x("INSERT INTO vision_result(device_id, queue_id, seq, plant_id, line_id, ts, received, ok, camera, part_id, image_id, job, job_version, "
             "reasons, cycle_ms) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(device_id, queue_id, seq) DO NOTHING",
             (did, queue, seq, el["plant"], el["line"], el["ts"], now_ms, int(el["ok"]), _text(el.get("camera")), _text(el.get("part_id")), image_id,
              _text(el.get("job")), el.get("job_version"), json.dumps(el.get("reasons", []), separators=(",", ":"), ensure_ascii=False),
              float(el.get("cycle_ms") or 0)))
    if cur.rowcount == 1:
        grades.keep(store, did, queue, seq, el)   # the print grades for the trend and the drift alarm (Req 17.3 PQ-01)
    # An id the station could not upload (a name outside the rule) is never asked for.
    if ask and not el["ok"] and image_id and IMAGE_ID.fullmatch(image_id):
        store._x("INSERT INTO vision_image_request(device_id, image_id, state, result_ts, requested, updated) VALUES (?,?,?,?,?,?) "
                 "ON CONFLICT(device_id, image_id) DO NOTHING", (did, image_id, "wanted", el["ts"], now_ms, now_ms))
    return 1 if cur.rowcount == 1 else 0


def _result_id(store, did: str, queue: str, seq: int) -> int | None:
    row = store._x("SELECT id FROM vision_result WHERE device_id = ? AND queue_id = ? AND seq = ?", (did, queue, seq)).fetchone()
    return row[0] if row else None


BACKFILL = "results_backfill"
log = logging.getLogger("vision")


def _meta_get(store, key: str):
    r = store._x("SELECT value FROM vision_meta WHERE key = ?", (key,)).fetchone()
    return r[0] if r else None


def _meta_set(store, key: str, value: str) -> None:
    store._x("INSERT INTO vision_meta(key, value) VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", (key, value))


def backfill_step(store, batch: int = 500) -> tuple[int, bool]:
    """One batch of the backfill (rows made, finished). Events stored before vision_result existed are read in the order of
    vision_event's primary key, so each batch is an index range scan however many events there are (the kind is looked at in
    Python: a filter on it would force a sort). The place reached is kept in vision_meta in the same transaction as the rows,
    so a stop at any moment resumes where it left off, and the last batch sets the done flag. The lock is held for this batch
    only; a result a station sent live in the meantime is not made twice (ON CONFLICT DO NOTHING). No image is asked for an
    old part: the station sent its images, or lost them, long ago."""
    made = 0
    with store._lock:
        _settle(store)
        try:
            state = _meta_get(store, BACKFILL)
            if state == "done":
                return 0, True
            after = tuple(json.loads(state)) if state else ("", "", 0)
            rows = store._x("SELECT device_id, queue_id, seq, kind, received, body FROM vision_event "
                            "WHERE (device_id, queue_id, seq) > (?,?,?) ORDER BY device_id, queue_id, seq LIMIT ?", (*after, batch)).fetchall()
            for did, queue, seq, kind, received, body in rows:
                if kind != "inspection.result":
                    continue
                try:
                    el = json.loads(body)
                    if events.problems(el):
                        continue
                    store._x("SAVEPOINT bf")
                    try:
                        made += _keep_result(store, did, queue, seq, el, received, ask=False)
                        store._x("RELEASE SAVEPOINT bf")
                    except Exception as e:  # noqa: BLE001
                        store._x("ROLLBACK TO SAVEPOINT bf")
                        store._x("RELEASE SAVEPOINT bf")
                        if not _data_error(e):
                            raise
                except ValueError:
                    continue
            done = len(rows) < batch
            _meta_set(store, BACKFILL, "done" if done else json.dumps(list(rows[-1][:3])))
            store.db.commit()
        except Exception:
            store.db.rollback()
            raise
    return made, done


def backfill_results(store, batch: int = 500, stop=None, pause: float = 0.02, max_batches: int | None = None) -> int:
    """Run the backfill batch by batch until it is done, `stop` (a threading.Event) is set, or max_batches have run; returns the rows
    made. It runs once: the done flag is kept, and an interrupted run resumes on the next call."""
    with store._lock:
        _settle(store)
        try:
            store._x("CREATE TABLE IF NOT EXISTS vision_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)")
            store.db.commit()
        except Exception:
            store.db.rollback()
            raise
    total, n = 0, 0
    while not (stop is not None and stop.is_set()) and (max_batches is None or n < max_batches):
        made, done = backfill_step(store, batch)
        total += made
        n += 1
        if done:
            break
        if stop is not None:
            stop.wait(pause)      # between batches the lock is free for ingest and heartbeats
    return total


def _keep_latest(store, did: str, el: dict, raw: str, now_ms: int) -> None:
    """What the live state needs from before the replay window, one row per item: the last line state
    and job change of each station and its open alarms (raise, then acknowledgement). A cleared alarm
    leaves nothing, so the replay at start stays small however long the plant has run."""
    kind = el["kind"]
    if kind in ("line.state", "job.changed"):
        item = kind
    elif kind == "alarm.raised":
        store._x("DELETE FROM vision_latest WHERE device_id = ? AND item = ?", (did, "ack:" + el["code"]))
        item = "alarm:" + el["code"]
    elif kind == "alarm.acknowledged":
        if store._x("SELECT 1 FROM vision_latest WHERE device_id = ? AND item = ?", (did, "alarm:" + el["code"])).fetchone() is None:
            return
        item = "ack:" + el["code"]
    elif kind == "alarm.cleared":
        store._x("DELETE FROM vision_latest WHERE device_id = ? AND item IN (?, ?)", (did, "alarm:" + el["code"], "ack:" + el["code"]))
        return
    else:
        return
    # The newest by the station's time wins, as in the live state; a late, older event changes nothing.
    store._x("INSERT INTO vision_latest VALUES (?,?,?,?,?) ON CONFLICT(device_id, item) DO UPDATE SET "
             "ts = excluded.ts, received = excluded.received, body = excluded.body WHERE vision_latest.ts <= excluded.ts",
             (did, item, el["ts"], now_ms, raw))


def save_heartbeat(store, device: dict, hb: dict, now_ms: int) -> None:
    """hb has passed events.problems: its numbers fit a BIGINT and its texts are texts."""
    with store._lock:
        _settle(store)
        try:
            args = (now_ms, hb.get("agent_version", ""), hb.get("queue_id"), hb.get("seq", 0),
                    hb.get("queue_depth", 0), hb.get("dropped", 0), device["id"])
            cur = store._x("UPDATE vision_station_status SET last_seen = ?, agent_version = ?, queue_id = ?, last_seq = ?, queue_depth = ?, "
                           "dropped = ? WHERE device_id = ?", args)
            if cur.rowcount == 0:
                store._x("INSERT INTO vision_station_status(last_seen, agent_version, queue_id, last_seq, queue_depth, dropped, device_id) "
                         "VALUES (?,?,?,?,?,?,?)", args)
            store._x("UPDATE device SET last_seen_at = ? WHERE id = ?", (now_ms // 1000, device["id"]))
            store.db.commit()
        except Exception:
            store.db.rollback()
            raise


def station_status(store) -> list[dict]:
    """The last heartbeat of every station, with its plant and line, for the live state at start."""
    with store._lock:
        rows = store._x("SELECT d.plant_id, d.line_id, s.device_id, s.last_seen, s.agent_version, s.last_seq, s.queue_depth, s.dropped "
                        "FROM vision_station_status s JOIN device d ON d.id = s.device_id WHERE d.line_id IS NOT NULL").fetchall()
    return [{"plant": r[0], "line": r[1], "station": r[2], "last_seen": r[3], "agent_version": r[4], "seq": r[5],
             "queue_depth": r[6], "dropped": r[7]} for r in rows]


def count(store, table: str) -> int:
    if table not in ("vision_event", "vision_gap", "vision_reject", "vision_conflict"):
        raise ValueError(table)
    with store._lock:
        return store._x(f"SELECT COUNT(*) FROM {table}").fetchone()[0]


def replay(store, since_ms: int) -> list[dict]:
    """The events that rebuild the live state at start: first what vision_latest keeps from any time (the
    last line state and job of each station, its open alarms; an open alarm is never forgotten), then
    every event received since since_ms, oldest first by the time the server received them (a station's
    clock is not trusted). Applying an event twice leaves the same state. An inspection result carries the
    id of its vision_result row (result_id, null for an OK part or one the backfill has not reached yet)."""
    with store._lock:
        latest = store._x("SELECT body FROM vision_latest "
                          "ORDER BY received, device_id, CASE WHEN substr(item, 1, 4) = 'ack:' THEN 1 ELSE 0 END, item").fetchall()
        recent = store._x("SELECT e.body, r.id, r.ok FROM vision_event e LEFT JOIN vision_result r "
                          "ON r.device_id = e.device_id AND r.queue_id = e.queue_id AND r.seq = e.seq "
                          "WHERE e.received >= ? ORDER BY e.received, e.device_id, e.seq", (since_ms,)).fetchall()
    out = [json.loads(r[0]) for r in latest]
    for body, rid, ok in recent:
        e = json.loads(body)
        if e.get("kind") == "inspection.result":
            e["result_id"] = rid if rid is not None and not ok else None
        out.append(e)
    return out
