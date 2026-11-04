"""Storage. One portable SQL implementation, two connections: SQLite (dev/tests/edge) and PostgreSQL (production).

Both run the identical test suite (see tests/conftest.py, env SPI_TEST_PG_DSN). All timestamps are epoch seconds (BIGINT).
Every public method takes an RLock so the ingestion thread and API threads can share one store safely.
"""
from __future__ import annotations

import json
import sqlite3
import threading
import uuid

from .derive import Interval

DDL = """
CREATE TABLE IF NOT EXISTS raw_event(
  event_id TEXT PRIMARY KEY, gateway_id TEXT NOT NULL, seq BIGINT NOT NULL, type TEXT NOT NULL,
  ts BIGINT NOT NULL, emitted_ts BIGINT NOT NULL, buffered INTEGER NOT NULL,
  plant_id TEXT, area_id TEXT, line_id TEXT, equipment_id TEXT, subject TEXT, payload TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_raw_eq ON raw_event(equipment_id, type, ts);
CREATE INDEX IF NOT EXISTS ix_raw_subject ON raw_event(subject, type, ts);
CREATE INDEX IF NOT EXISTS ix_raw_gw ON raw_event(gateway_id, seq);
CREATE INDEX IF NOT EXISTS ix_raw_gw_ts ON raw_event(gateway_id, ts);
CREATE TABLE IF NOT EXISTS rejected_event(id {AUTOID}, reason TEXT NOT NULL, raw TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS equipment_state_interval(
  equipment_id TEXT NOT NULL, state TEXT NOT NULL, start_ts BIGINT NOT NULL, end_ts BIGINT NOT NULL,
  confidence DOUBLE PRECISION NOT NULL, evidence TEXT NOT NULL, basis TEXT NOT NULL DEFAULT 'detected');
CREATE INDEX IF NOT EXISTS ix_int_eq ON equipment_state_interval(equipment_id, end_ts);
CREATE TABLE IF NOT EXISTS alert(
  id {AUTOID}, alert_key TEXT NOT NULL, rule_id TEXT NOT NULL, rule_type TEXT NOT NULL, severity TEXT NOT NULL, state TEXT NOT NULL,
  level INTEGER NOT NULL DEFAULT 0, plant_id TEXT, line_id TEXT, equipment_id TEXT, subject TEXT, title TEXT NOT NULL, detail TEXT NOT NULL,
  condition_text TEXT NOT NULL, threshold DOUBLE PRECISION, opened_at BIGINT NOT NULL, last_seen_at BIGINT NOT NULL, clear_since BIGINT,
  acked_at BIGINT, acked_by TEXT, resolved_at BIGINT, resolution TEXT, recipients TEXT NOT NULL, channels TEXT NOT NULL, evidence TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_alert_key ON alert(alert_key, state);
CREATE TABLE IF NOT EXISTS alert_event(
  id {AUTOID}, alert_id BIGINT NOT NULL, ts BIGINT NOT NULL, wall_ts DOUBLE PRECISION NOT NULL, kind TEXT NOT NULL, actor TEXT NOT NULL, detail TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_alert_event ON alert_event(alert_id);
CREATE TABLE IF NOT EXISTS notification(
  id {AUTOID}, alert_id BIGINT NOT NULL, ts BIGINT NOT NULL, channel TEXT NOT NULL, recipient TEXT NOT NULL, kind TEXT NOT NULL,
  status TEXT NOT NULL, detail TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_notification ON notification(alert_id);
CREATE TABLE IF NOT EXISTS downtime_event(
  equipment_id TEXT NOT NULL, start_ts BIGINT NOT NULL, end_ts BIGINT NOT NULL, duration_s BIGINT NOT NULL,
  state TEXT NOT NULL, category TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'automatic',
  basis TEXT NOT NULL DEFAULT 'detected', confidence DOUBLE PRECISION NOT NULL, evidence TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS spi_user(
  id {AUTOID}, username TEXT NOT NULL UNIQUE, name TEXT NOT NULL, email TEXT, password_hash TEXT NOT NULL,
  active INTEGER NOT NULL DEFAULT 1, created_at BIGINT NOT NULL, created_by TEXT);
CREATE TABLE IF NOT EXISTS role_assignment(
  id {AUTOID}, user_id BIGINT NOT NULL, role TEXT NOT NULL, scope_level TEXT NOT NULL, scope_id TEXT, created_at BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_role_user ON role_assignment(user_id);
CREATE TABLE IF NOT EXISTS user_session(
  token TEXT PRIMARY KEY, user_id BIGINT NOT NULL, created_at BIGINT NOT NULL, expires_at BIGINT NOT NULL, last_used_at BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_session_user ON user_session(user_id);
CREATE TABLE IF NOT EXISTS push_subscription(
  id {AUTOID}, user_id BIGINT NOT NULL, endpoint TEXT NOT NULL UNIQUE, p256dh TEXT NOT NULL, auth TEXT NOT NULL,
  device TEXT, created_at BIGINT NOT NULL, last_used_at BIGINT, last_error TEXT);
CREATE INDEX IF NOT EXISTS ix_push_user ON push_subscription(user_id);
CREATE TABLE IF NOT EXISTS audit_entry(
  id {AUTOID}, seq BIGINT NOT NULL UNIQUE, ts BIGINT NOT NULL, action TEXT NOT NULL, outcome TEXT NOT NULL, user_id BIGINT,
  object_type TEXT, object_id TEXT, entry TEXT NOT NULL, hash TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_audit_object ON audit_entry(object_type, object_id);
CREATE TABLE IF NOT EXISTS plant(id TEXT PRIMARY KEY, name TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'admin');
CREATE TABLE IF NOT EXISTS area(id TEXT PRIMARY KEY, plant_id TEXT NOT NULL, name TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'admin');
CREATE TABLE IF NOT EXISTS line(
  id TEXT PRIMARY KEY, plant_id TEXT NOT NULL, area_id TEXT, name TEXT NOT NULL, target_per_hour DOUBLE PRECISION,
  source TEXT NOT NULL DEFAULT 'admin');
CREATE TABLE IF NOT EXISTS device(
  id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, plant_id TEXT NOT NULL, line_id TEXT,
  maker TEXT, model TEXT, serial TEXT, token_hash TEXT UNIQUE, enabled INTEGER NOT NULL DEFAULT 1,
  created_at BIGINT NOT NULL, created_by TEXT, last_seen_at BIGINT, source TEXT NOT NULL DEFAULT 'admin');
CREATE INDEX IF NOT EXISTS ix_device_line ON device(line_id);
CREATE TABLE IF NOT EXISTS vision_queue(device_id TEXT NOT NULL, queue_id TEXT NOT NULL, max_seq BIGINT NOT NULL, PRIMARY KEY(device_id, queue_id));
CREATE TABLE IF NOT EXISTS vision_event(
  device_id TEXT NOT NULL, queue_id TEXT NOT NULL, seq BIGINT NOT NULL, ts BIGINT NOT NULL, kind TEXT NOT NULL, body TEXT NOT NULL,
  received BIGINT NOT NULL, PRIMARY KEY(device_id, queue_id, seq));
CREATE INDEX IF NOT EXISTS ix_vision_event_received ON vision_event(received);
CREATE INDEX IF NOT EXISTS ix_vision_event_kind ON vision_event(kind, received);
CREATE TABLE IF NOT EXISTS vision_gap(device_id TEXT NOT NULL, queue_id TEXT NOT NULL, from_seq BIGINT NOT NULL, to_seq BIGINT NOT NULL, found BIGINT NOT NULL);
CREATE TABLE IF NOT EXISTS vision_reject(
  device_id TEXT NOT NULL, queue_id TEXT NOT NULL, seq BIGINT NOT NULL, reason TEXT NOT NULL, body TEXT NOT NULL, received BIGINT NOT NULL,
  PRIMARY KEY(device_id, queue_id, seq));
CREATE TABLE IF NOT EXISTS vision_conflict(device_id TEXT NOT NULL, queue_id TEXT NOT NULL, seq BIGINT NOT NULL, body TEXT NOT NULL, found BIGINT NOT NULL);
CREATE TABLE IF NOT EXISTS vision_station_status(
  device_id TEXT PRIMARY KEY, last_seen BIGINT NOT NULL, agent_version TEXT NOT NULL, queue_id TEXT, last_seq BIGINT NOT NULL,
  queue_depth BIGINT NOT NULL, dropped BIGINT NOT NULL);
CREATE TABLE IF NOT EXISTS vision_latest(
  device_id TEXT NOT NULL, item TEXT NOT NULL, ts BIGINT NOT NULL, received BIGINT NOT NULL, body TEXT NOT NULL, PRIMARY KEY(device_id, item));
CREATE TABLE IF NOT EXISTS vision_result(
  id {AUTOID}, device_id TEXT NOT NULL, queue_id TEXT NOT NULL, seq BIGINT NOT NULL, plant_id TEXT NOT NULL, line_id TEXT NOT NULL,
  ts BIGINT NOT NULL, received BIGINT NOT NULL, ok INTEGER NOT NULL, camera TEXT, part_id TEXT, image_id TEXT, job TEXT, job_version INTEGER,
  reasons TEXT NOT NULL, cycle_ms DOUBLE PRECISION, verdict TEXT, note TEXT, reviewed_by TEXT, reviewed_at BIGINT, UNIQUE(device_id, queue_id, seq));
CREATE INDEX IF NOT EXISTS ix_vision_result_list ON vision_result(line_id, ok, ts);
CREATE INDEX IF NOT EXISTS ix_vision_result_ng ON vision_result(id) WHERE ok = 0;
CREATE INDEX IF NOT EXISTS ix_vision_result_pending ON vision_result(id) WHERE ok = 0 AND verdict IS NULL;
CREATE INDEX IF NOT EXISTS ix_vision_result_reviewed ON vision_result(id) WHERE ok = 0 AND verdict IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_vision_result_verdict ON vision_result(verdict, id) WHERE ok = 0;
CREATE INDEX IF NOT EXISTS ix_vision_result_ng_plant ON vision_result(plant_id, id) WHERE ok = 0;
CREATE INDEX IF NOT EXISTS ix_vision_result_ng_line ON vision_result(line_id, id) WHERE ok = 0;
CREATE INDEX IF NOT EXISTS ix_vision_result_ng_station ON vision_result(device_id, id) WHERE ok = 0;
CREATE INDEX IF NOT EXISTS ix_vision_result_ng_ts ON vision_result(ts, plant_id, line_id, verdict) WHERE ok = 0;
CREATE INDEX IF NOT EXISTS ix_vision_result_image ON vision_result(device_id, image_id);
CREATE TABLE IF NOT EXISTS vision_grade(
  device_id TEXT NOT NULL, queue_id TEXT NOT NULL, seq BIGINT NOT NULL, tool TEXT NOT NULL, plant_id TEXT NOT NULL, line_id TEXT NOT NULL, ts BIGINT NOT NULL,
  grade DOUBLE PRECISION, ungraded INTEGER NOT NULL, grade_min DOUBLE PRECISION, symbol_contrast_pct DOUBLE PRECISION, modulation DOUBLE PRECISION,
  params TEXT NOT NULL, PRIMARY KEY(device_id, queue_id, seq, tool));
CREATE INDEX IF NOT EXISTS ix_vision_grade_ts ON vision_grade(ts);
CREATE INDEX IF NOT EXISTS ix_vision_grade_series ON vision_grade(device_id, tool, ts);
CREATE TABLE IF NOT EXISTS vision_image_request(
  device_id TEXT NOT NULL, image_id TEXT NOT NULL, state TEXT NOT NULL, result_ts BIGINT NOT NULL, requested BIGINT NOT NULL, updated BIGINT NOT NULL,
  PRIMARY KEY(device_id, image_id));
CREATE INDEX IF NOT EXISTS ix_vision_image_request_state ON vision_image_request(device_id, state, result_ts);
CREATE TABLE IF NOT EXISTS vision_image(
  id {AUTOID}, device_id TEXT NOT NULL, image_id TEXT NOT NULL, camera TEXT NOT NULL, file TEXT NOT NULL, size BIGINT NOT NULL, received BIGINT NOT NULL,
  UNIQUE(device_id, image_id, camera));
CREATE INDEX IF NOT EXISTS ix_vision_image_received ON vision_image(received, id);
CREATE TABLE IF NOT EXISTS ng_review(
  id {AUTOID}, result_id BIGINT NOT NULL, verdict TEXT NOT NULL, note TEXT NOT NULL, by_user TEXT NOT NULL, user_id BIGINT, ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_ng_review_result ON ng_review(result_id, id);
CREATE TABLE IF NOT EXISTS vision_setting(
  id {AUTOID}, scope_level TEXT NOT NULL, scope_id TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, updated_by TEXT NOT NULL, updated_at BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_vision_setting_scope ON vision_setting(scope_level, scope_id, key, id);
CREATE TABLE IF NOT EXISTS vision_changeover(
  id {AUTOID}, station TEXT NOT NULL, plant TEXT NOT NULL, line TEXT NOT NULL, code TEXT NOT NULL, ask BIGINT NOT NULL, record TEXT NOT NULL,
  operator TEXT NOT NULL, changed_at BIGINT NOT NULL, asked_at BIGINT NOT NULL, state TEXT NOT NULL,
  decided_by TEXT, decided_name TEXT, decided_at BIGINT, note TEXT NOT NULL, UNIQUE(station, code, ask));
CREATE INDEX IF NOT EXISTS ix_vision_changeover_station ON vision_changeover(station, state);
CREATE TABLE IF NOT EXISTS vision_changeover_decision(
  id {AUTOID}, changeover_id BIGINT NOT NULL, user_id BIGINT, name TEXT NOT NULL, decision TEXT NOT NULL, note TEXT NOT NULL, at BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_vision_changeover_decision_request ON vision_changeover_decision(changeover_id, id);
"""
_SEL = "ts, seq, type, equipment_id, subject, payload"
ALERT_COLS = ["alert_key", "rule_id", "rule_type", "severity", "state", "level", "plant_id", "line_id", "equipment_id", "subject", "title", "detail",
              "condition_text", "threshold", "opened_at", "last_seen_at", "clear_since", "acked_at", "acked_by", "resolved_at", "resolution",
              "recipients", "channels", "evidence"]
_JSON_COLS = ("recipients", "channels", "evidence")


def _row(r) -> dict:
    return {"ts": r[0], "seq": r[1], "type": r[2], "equipment_id": r[3], "subject": r[4], "payload": json.loads(r[5])}


class Store:
    placeholder = "?"
    autoid = "INTEGER PRIMARY KEY AUTOINCREMENT"
    # The audit trail refuses UPDATE and DELETE (PART11-DESIGN D6, PT11-20).
    APPEND_ONLY = tuple(f"CREATE TRIGGER IF NOT EXISTS audit_entry_no_{op.lower()} BEFORE {op} ON audit_entry "
                        f"BEGIN SELECT RAISE(ABORT, 'the audit trail is append-only'); END" for op in ("UPDATE", "DELETE"))

    def __init__(self):
        self._lock = threading.RLock()
        self.db = None

    # ---- plumbing ----------------------------------------------------
    def _x(self, sql: str, args=()):
        return self.db.execute(sql.replace("?", self.placeholder) if self.placeholder != "?" else sql, args)

    def _init_schema(self) -> None:
        for stmt in DDL.replace("{AUTOID}", self.autoid).split(";"):
            if stmt.strip():
                self.db.execute(stmt)
        for stmt in self.APPEND_ONLY:
            self.db.execute(stmt)
        self.db.commit()

    def commit(self) -> None:
        with self._lock:
            self.db.commit()

    def close(self) -> None:
        with self._lock:
            self.db.close()

    # ---- ingestion ---------------------------------------------------
    def add_event(self, ev: dict) -> bool:
        """Insert; returns False if event_id already stored (duplicate)."""
        from spi_common.schema import parse_ts
        p = ev["payload"]
        subject = p.get("sensor_id") or p.get("queue_id") or (p.get("name") if ev["type"] == "signal" else None)
        with self._lock:
            cur = self._x("INSERT INTO raw_event VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(event_id) DO NOTHING",
                          (ev["event_id"], ev["gateway_id"], ev["seq"], ev["type"], parse_ts(ev["ts"]), parse_ts(ev["emitted_ts"]),
                           int(bool(ev["buffered"])), ev["plant_id"], ev["area_id"], ev["line_id"], ev["equipment_id"], subject,
                           json.dumps(p)))
            return cur.rowcount == 1

    def add_events(self, events: list[dict]) -> int:
        """Batch insert (one round trip); returns how many were new. Duplicates (same event_id) are skipped."""
        from spi_common.schema import parse_ts
        rows = []
        for ev in events:
            p = ev["payload"]
            subject = p.get("sensor_id") or p.get("queue_id") or (p.get("name") if ev["type"] == "signal" else None)
            rows.append((ev["event_id"], ev["gateway_id"], ev["seq"], ev["type"], parse_ts(ev["ts"]), parse_ts(ev["emitted_ts"]),
                         int(bool(ev["buffered"])), ev["plant_id"], ev["area_id"], ev["line_id"], ev["equipment_id"], subject, json.dumps(p)))
        if not rows:
            return 0
        sql = "INSERT INTO raw_event VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(event_id) DO NOTHING"
        with self._lock:
            cur = self.db.cursor()
            cur.executemany(sql.replace("?", self.placeholder) if self.placeholder != "?" else sql, rows)
            return cur.rowcount

    def add_rejected(self, raw, reason: str) -> None:
        with self._lock:
            self._x("INSERT INTO rejected_event(reason, raw) VALUES (?,?)", (reason, json.dumps(raw, default=str)))

    # ---- event queries -----------------------------------------------
    @staticmethod
    def _flt(types, equipment_id, subject):
        w, a = f"type IN ({','.join('?' * len(types))})", list(types)
        if equipment_id is not None:
            w += " AND equipment_id = ?"
            a.append(equipment_id)
        if subject is not None:
            w += " AND subject = ?"
            a.append(subject)
        return w, a

    def events(self, types, equipment_id=None, subject=None, t_min=None, t_max=None) -> list[dict]:
        """Events with t_min <= ts <= t_max ordered by (ts, seq).

        With t_min, also returns the latest earlier event of every series (type/equipment/subject) so the state in force
        at t_min is known even if it has not changed for hours (signals are sent on change only).
        """
        w, a = self._flt(types, equipment_id, subject)
        if t_max is not None:
            w += " AND ts <= ?"
            a.append(t_max)
        if t_min is None:
            q, args = f"SELECT {_SEL} FROM raw_event WHERE {w} ORDER BY ts, seq", a
        else:
            q = (f"SELECT {_SEL} FROM raw_event WHERE {w} AND ts >= ? UNION ALL "
                 f"SELECT {_SEL} FROM (SELECT {_SEL}, ROW_NUMBER() OVER (PARTITION BY type, equipment_id, subject "
                 f"ORDER BY ts DESC, seq DESC) AS rn FROM raw_event WHERE {w} AND ts < ?) x WHERE rn = 1 ORDER BY ts, seq")
            args = a + [t_min] + a + [t_min]
        with self._lock:
            return [_row(r) for r in self._x(q, args).fetchall()]

    def last_before(self, types, t_excl: int, equipment_id=None, subject=None):
        w, a = self._flt(types, equipment_id, subject)
        with self._lock:
            r = self._x(f"SELECT {_SEL} FROM raw_event WHERE {w} AND ts < ? ORDER BY ts DESC, seq DESC LIMIT 1", a + [t_excl]).fetchone()
        return _row(r) if r else None

    def counts(self) -> dict:
        with self._lock:
            return {"raw_events": self._x("SELECT COUNT(*) FROM raw_event").fetchone()[0],
                    "rejected": self._x("SELECT COUNT(*) FROM rejected_event").fetchone()[0]}

    def time_range(self):
        with self._lock:
            r = self._x("SELECT MIN(ts), MAX(ts) FROM raw_event").fetchone()
        return (r[0], r[1]) if r[0] is not None else None

    def max_ts(self, gateway_id: str):
        """Latest event time reported by a gateway (its data horizon), or None if it never reported."""
        with self._lock:
            return self._x("SELECT MAX(ts) FROM raw_event WHERE gateway_id=?", (gateway_id,)).fetchone()[0]

    def gateways(self) -> list[str]:
        with self._lock:
            return [r[0] for r in self._x("SELECT DISTINCT gateway_id FROM raw_event ORDER BY 1").fetchall()]

    def missing_seqs(self, gateway_id: str) -> list[tuple[int, int]]:
        """Inclusive (from, to) ranges of sequence numbers never received. Late arrivals close gaps."""
        gaps, expect = [], 1
        with self._lock:
            rows = self._x("SELECT seq FROM raw_event WHERE gateway_id=? ORDER BY seq", (gateway_id,)).fetchall()
        for (s,) in rows:
            if s > expect:
                gaps.append((expect, s - 1))
            expect = s + 1
        return gaps

    # ---- derived data ------------------------------------------------
    def replace_intervals(self, equipment_id: str, intervals) -> None:
        with self._lock:
            self._x("DELETE FROM equipment_state_interval WHERE equipment_id=?", (equipment_id,))
            self._insert_intervals(intervals)

    def _insert_intervals(self, intervals) -> None:
        sql = "INSERT INTO equipment_state_interval(equipment_id,state,start_ts,end_ts,confidence,evidence) VALUES (?,?,?,?,?,?)"
        if self.placeholder != "?":
            sql = sql.replace("?", self.placeholder)
        self.db.cursor().executemany(sql, [(i.equipment_id, i.state, i.start, i.end, round(i.confidence, 3), i.evidence) for i in intervals])

    def commit_intervals(self, equipment_id: str, new: list[Interval], window_start: int) -> None:
        """Live update: replace everything at/after window_start. A state that began earlier keeps its true start."""
        with self._lock:
            straddle = self._x("SELECT state,start_ts,confidence,evidence FROM equipment_state_interval "
                               "WHERE equipment_id=? AND start_ts < ? AND end_ts > ?", (equipment_id, window_start, window_start)).fetchone()
            self._x("DELETE FROM equipment_state_interval WHERE equipment_id=? AND end_ts > ?", (equipment_id, window_start))
            keep = []
            if straddle:
                if new and new[0].state == straddle[0] and new[0].start == window_start:
                    new[0].start = straddle[1]
                else:
                    keep.append(Interval(equipment_id, straddle[0], straddle[1], window_start, straddle[2], straddle[3]))
            self._insert_intervals(keep + new)
            self.db.commit()

    def intervals(self, equipment_id: str, t0: int, t1: int, clip: bool = True) -> list[Interval]:
        with self._lock:
            rows = self._x("SELECT state,start_ts,end_ts,confidence,evidence FROM equipment_state_interval "
                           "WHERE equipment_id=? AND end_ts > ? AND start_ts < ? ORDER BY start_ts", (equipment_id, t0, t1)).fetchall()
        return [Interval(equipment_id, s, max(a, t0) if clip else a, min(b, t1) if clip else b, c, e) for s, a, b, c, e in rows]

    def replace_downtime(self, equipment_id: str, rows) -> None:
        sql = "INSERT INTO downtime_event(equipment_id,start_ts,end_ts,duration_s,state,category,confidence,evidence) VALUES (?,?,?,?,?,?,?,?)"
        if self.placeholder != "?":
            sql = sql.replace("?", self.placeholder)
        with self._lock:
            self._x("DELETE FROM downtime_event WHERE equipment_id=?", (equipment_id,))
            self.db.cursor().executemany(sql, [(r["equipment_id"], r["start"], r["end"], r["duration_s"], r["state"], r["category"],
                                                r["confidence"], r["evidence"]) for r in rows])


    # ---- alerts (lifecycle + audit trail + notifications) --------------
    def _alert_row(self, r) -> dict:
        d = dict(zip(["id"] + ALERT_COLS, r))
        for c in _JSON_COLS:
            d[c] = json.loads(d[c])
        return d

    def alert_insert(self, f: dict) -> int:
        vals = [json.dumps(f[c]) if c in _JSON_COLS else f.get(c) for c in ALERT_COLS]
        with self._lock:
            r = self._x(f"INSERT INTO alert({','.join(ALERT_COLS)}) VALUES ({','.join('?' * len(ALERT_COLS))}) RETURNING id", vals).fetchone()
            self.db.commit()
            return r[0]

    def alert_update(self, alert_id: int, **f) -> None:
        cols = list(f)
        vals = [json.dumps(f[c]) if c in _JSON_COLS else f[c] for c in cols]
        with self._lock:
            self._x(f"UPDATE alert SET {', '.join(c + ' = ?' for c in cols)} WHERE id = ?", vals + [alert_id])
            self.db.commit()

    def alert_get(self, alert_id: int):
        with self._lock:
            r = self._x(f"SELECT id, {','.join(ALERT_COLS)} FROM alert WHERE id = ?", (alert_id,)).fetchone()
        return self._alert_row(r) if r else None

    def alert_list(self, states=None, line_id=None, limit: int = 200) -> list[dict]:
        w, a = [], []
        if states:
            w.append(f"state IN ({','.join('?' * len(states))})")
            a += list(states)
        if line_id:
            w.append("line_id = ?")
            a.append(line_id)
        sql = f"SELECT id, {','.join(ALERT_COLS)} FROM alert" + (" WHERE " + " AND ".join(w) if w else "") + " ORDER BY id DESC LIMIT ?"
        with self._lock:
            return [self._alert_row(r) for r in self._x(sql, a + [limit]).fetchall()]

    def alert_event_add(self, alert_id: int, ts: int, kind: str, actor: str, detail: str) -> None:
        import time
        with self._lock:
            self._x("INSERT INTO alert_event(alert_id, ts, wall_ts, kind, actor, detail) VALUES (?,?,?,?,?,?)", (alert_id, ts, time.time(), kind, actor, detail))
            self.db.commit()

    def alert_events(self, alert_id: int) -> list[dict]:
        with self._lock:
            rows = self._x("SELECT ts, wall_ts, kind, actor, detail FROM alert_event WHERE alert_id = ? ORDER BY id", (alert_id,)).fetchall()
        return [{"ts": r[0], "wall_ts": r[1], "kind": r[2], "actor": r[3], "detail": r[4]} for r in rows]

    def notification_add(self, alert_id: int, ts: int, channel: str, recipient: str, kind: str, status: str, detail: str = "") -> int:
        with self._lock:
            r = self._x("INSERT INTO notification(alert_id, ts, channel, recipient, kind, status, detail) VALUES (?,?,?,?,?,?,?) RETURNING id",
                        (alert_id, ts, channel, recipient, kind, status, detail)).fetchone()
            self.db.commit()
            return r[0]

    def notification_update(self, nid: int, status: str, detail: str) -> None:
        with self._lock:
            self._x("UPDATE notification SET status = ?, detail = ? WHERE id = ?", (status, detail, nid))
            self.db.commit()

    def notifications(self, alert_id: int) -> list[dict]:
        with self._lock:
            rows = self._x("SELECT id, ts, channel, recipient, kind, status, detail FROM notification WHERE alert_id = ? ORDER BY id", (alert_id,)).fetchall()
        return [dict(zip(["id", "ts", "channel", "recipient", "kind", "status", "detail"], r)) for r in rows]


    # ---- users, roles, sessions, push subscriptions (M4b) ---------------
    def user_create(self, username: str, name: str, password_hash: str, email: str | None, created_by: str | None, now: int) -> int:
        with self._lock:
            r = self._x("INSERT INTO spi_user(username, name, email, password_hash, created_at, created_by) VALUES (?,?,?,?,?,?) RETURNING id",
                        (username, name, email, password_hash, now, created_by)).fetchone()
            self.db.commit()
            return r[0]

    @staticmethod
    def _user_row(r) -> dict:
        return dict(zip(["id", "username", "name", "email", "password_hash", "active", "created_at", "created_by"], r)) if r else None

    def user_by_username(self, username: str):
        with self._lock:
            return self._user_row(self._x("SELECT id, username, name, email, password_hash, active, created_at, created_by FROM spi_user WHERE username = ?", (username,)).fetchone())

    def user_by_id(self, uid: int):
        with self._lock:
            return self._user_row(self._x("SELECT id, username, name, email, password_hash, active, created_at, created_by FROM spi_user WHERE id = ?", (uid,)).fetchone())

    def user_list(self) -> list[dict]:
        with self._lock:
            rows = self._x("SELECT id, username, name, email, password_hash, active, created_at, created_by FROM spi_user ORDER BY id").fetchall()
        return [self._user_row(r) for r in rows]

    def user_count(self) -> int:
        with self._lock:
            return self._x("SELECT COUNT(*) FROM spi_user").fetchone()[0]

    def user_set_active(self, uid: int, active: bool) -> None:
        with self._lock:
            self._x("UPDATE spi_user SET active = ? WHERE id = ?", (int(active), uid))
            self.db.commit()

    def user_set_password(self, uid: int, password_hash: str) -> None:
        with self._lock:
            self._x("UPDATE spi_user SET password_hash = ? WHERE id = ?", (password_hash, uid))
            self.db.commit()

    def role_add(self, user_id: int, role: str, scope_level: str, scope_id: str | None, now: int) -> int:
        with self._lock:
            r = self._x("INSERT INTO role_assignment(user_id, role, scope_level, scope_id, created_at) VALUES (?,?,?,?,?) RETURNING id",
                        (user_id, role, scope_level, scope_id, now)).fetchone()
            self.db.commit()
            return r[0]

    def role_remove(self, assignment_id: int) -> None:
        with self._lock:
            self._x("DELETE FROM role_assignment WHERE id = ?", (assignment_id,))
            self.db.commit()

    def role_list(self, user_id: int) -> list[dict]:
        with self._lock:
            rows = self._x("SELECT id, role, scope_level, scope_id FROM role_assignment WHERE user_id = ? ORDER BY id", (user_id,)).fetchall()
        return [dict(zip(["id", "role", "scope_level", "scope_id"], r)) for r in rows]

    def session_create(self, token: str, user_id: int, now: int, expires_at: int) -> None:
        with self._lock:
            self._x("INSERT INTO user_session(token, user_id, created_at, expires_at, last_used_at) VALUES (?,?,?,?,?)", (token, user_id, now, expires_at, now))
            self.db.commit()

    def session_get(self, token: str, now: int | None = None):
        with self._lock:
            r = self._x("SELECT token, user_id, created_at, expires_at, last_used_at FROM user_session WHERE token = ?", (token,)).fetchone()
            if r and now is not None:
                self._x("UPDATE user_session SET last_used_at = ? WHERE token = ?", (now, token))
                self.db.commit()
        return dict(zip(["token", "user_id", "created_at", "expires_at", "last_used_at"], r)) if r else None

    def session_touch(self, token: str, now: int) -> None:
        with self._lock:
            self._x("UPDATE user_session SET last_used_at = ? WHERE token = ?", (now, token))
            self.db.commit()

    def session_delete(self, token: str, last_used_at: int | None = None) -> bool:
        """True when this call ended the session (False: it was already gone), so an end is audited once. With
        last_used_at, only if the session was not used since (the sweep never ends a session someone just used)."""
        with self._lock:
            if last_used_at is None:
                cur = self._x("DELETE FROM user_session WHERE token = ?", (token,))
            else:
                cur = self._x("DELETE FROM user_session WHERE token = ? AND last_used_at = ?", (token, last_used_at))
            self.db.commit()
            return cur.rowcount == 1

    def session_list(self) -> list[dict]:
        with self._lock:
            rows = self._x("SELECT token, user_id, created_at, expires_at, last_used_at FROM user_session").fetchall()
        return [dict(zip(["token", "user_id", "created_at", "expires_at", "last_used_at"], r)) for r in rows]

    def session_delete_all_for_user(self, user_id: int) -> None:
        with self._lock:
            self._x("DELETE FROM user_session WHERE user_id = ?", (user_id,))
            self.db.commit()

    # Audit trail (PART11-DESIGN 5, backend/audit.py builds the entries): append-only. `entry` holds the exact
    # canonical JSON bytes that were hashed; the other columns are copies for searching. No update or delete method,
    # and the database refuses both (_init_append_only).
    def audit_append(self, build) -> dict:
        """build(last_seq, last_hash) -> (row, entry_json, hash), called under the lock so seq and the chain never fork."""
        with self._lock:
            last = self._x("SELECT seq, hash FROM audit_entry ORDER BY seq DESC LIMIT 1").fetchone()
            row, entry_json, h = build(last[0] if last else 0, last[1] if last else None)
            self._x("INSERT INTO audit_entry(seq, ts, action, outcome, user_id, object_type, object_id, entry, hash) VALUES (?,?,?,?,?,?,?,?,?)",
                    (row["seq"], row["ts"], row["action"], row["outcome"], row["user_id"], row["object_type"], row["object_id"], entry_json, h))
            self.db.commit()
            return row

    def audit_query(self, user_id: int | None = None, action: str | None = None, object_type: str | None = None,
                    object_id: str | None = None, since: int | None = None, limit: int = 200) -> list[tuple[str, str]]:
        """(entry_json, hash), newest first; since: written at or after this time (seconds)."""
        w, a = [], []
        for col, v in (("user_id", user_id), ("action", action), ("object_type", object_type), ("object_id", object_id)):
            if v is not None:
                w.append(f"{col} = ?")
                a.append(v)
        if since is not None:
            w.append("ts >= ?")
            a.append(since)
        sql = "SELECT entry, hash FROM audit_entry" + (" WHERE " + " AND ".join(w) if w else "") + " ORDER BY seq DESC LIMIT ?"
        with self._lock:
            return [tuple(r) for r in self._x(sql, a + [limit]).fetchall()]

    def audit_head(self) -> tuple[int, str] | None:
        """(seq, hash) of the newest entry, or None for an empty trail."""
        with self._lock:
            r = self._x("SELECT seq, hash FROM audit_entry ORDER BY seq DESC LIMIT 1").fetchone()
        return (r[0], r[1]) if r else None

    def audit_hash(self, seq: int) -> str | None:
        with self._lock:
            r = self._x("SELECT hash FROM audit_entry WHERE seq = ?", (seq,)).fetchone()
        return r[0] if r else None

    def audit_batch(self, after_seq: int, n: int = 1000) -> list[tuple[int, str, str]]:
        """(seq, entry_json, hash) in seq order after after_seq: the chain check walks the table in batches."""
        with self._lock:
            return [tuple(r) for r in self._x("SELECT seq, entry, hash FROM audit_entry WHERE seq > ? ORDER BY seq LIMIT ?", (after_seq, n)).fetchall()]

    def push_add(self, user_id: int, endpoint: str, p256dh: str, auth: str, device: str | None, now: int) -> int:
        with self._lock:
            self._x("DELETE FROM push_subscription WHERE endpoint = ?", (endpoint,))   # re-subscribing replaces the old row (browser may reuse the id)
            r = self._x("INSERT INTO push_subscription(user_id, endpoint, p256dh, auth, device, created_at) VALUES (?,?,?,?,?,?) RETURNING id",
                        (user_id, endpoint, p256dh, auth, device, now)).fetchone()
            self.db.commit()
            return r[0]

    def push_remove(self, endpoint: str) -> None:
        with self._lock:
            self._x("DELETE FROM push_subscription WHERE endpoint = ?", (endpoint,))
            self.db.commit()

    @staticmethod
    def _push_row(r) -> dict:
        return dict(zip(["id", "user_id", "endpoint", "p256dh", "auth", "device"], r))

    def push_list_for_users(self, user_ids) -> list[dict]:
        user_ids = list(user_ids)
        if not user_ids:
            return []
        with self._lock:
            rows = self._x(f"SELECT id, user_id, endpoint, p256dh, auth, device FROM push_subscription WHERE user_id IN ({','.join('?' * len(user_ids))})", user_ids).fetchall()
        return [self._push_row(r) for r in rows]

    def push_list_for_user(self, user_id: int) -> list[dict]:
        return self.push_list_for_users([user_id])

    def push_touch(self, sub_id: int, now: int) -> None:
        with self._lock:
            self._x("UPDATE push_subscription SET last_used_at = ?, last_error = NULL WHERE id = ?", (now, sub_id))
            self.db.commit()

    def push_mark_error(self, sub_id: int, error: str) -> None:
        with self._lock:
            self._x("UPDATE push_subscription SET last_error = ? WHERE id = ?", (error, sub_id))
            self.db.commit()


    # ---- plant master data and devices (PL1; docs/vision-system/PLANT-PLATFORM.md 2) ----
    def master_upsert(self, table: str, row: dict) -> None:
        """Insert or update one plant, area or line by id. Rows from the site JSON carry source
        'site_json'; an update never turns an admin-made row into a JSON one or back."""
        if table not in ("plant", "area", "line"):
            raise ValueError(f"unknown master table {table!r}")
        cols = list(row)
        sets = ", ".join(f"{c} = excluded.{c}" for c in cols if c not in ("id", "source"))
        sql = f"INSERT INTO {table}({', '.join(cols)}) VALUES ({', '.join('?' * len(cols))}) ON CONFLICT(id) DO UPDATE SET {sets}"
        with self._lock:
            self._x(sql, tuple(row[c] for c in cols))

    def master_list(self, table: str) -> list[dict]:
        cols = {"plant": ["id", "name", "source"], "area": ["id", "plant_id", "name", "source"],
                "line": ["id", "plant_id", "area_id", "name", "target_per_hour", "source"]}[table]
        with self._lock:
            return [dict(zip(cols, r)) for r in self._x(f"SELECT {', '.join(cols)} FROM {table} ORDER BY id").fetchall()]

    def master_get(self, table: str, row_id: str) -> dict | None:
        return next((r for r in self.master_list(table) if r["id"] == row_id), None)

    DEVICE_COLS = ["id", "kind", "name", "plant_id", "line_id", "maker", "model", "serial", "token_hash", "enabled",
                   "created_at", "created_by", "last_seen_at", "source"]

    def _device(self, r) -> dict:
        d = dict(zip(self.DEVICE_COLS, r))
        d["enabled"] = bool(d["enabled"])
        return d

    def device_create(self, d: dict) -> None:
        cols = [c for c in self.DEVICE_COLS if c in d]
        with self._lock:
            self._x(f"INSERT INTO device({', '.join(cols)}) VALUES ({', '.join('?' * len(cols))})", tuple(d[c] for c in cols))

    def device_get(self, device_id: str) -> dict | None:
        with self._lock:
            r = self._x(f"SELECT {', '.join(self.DEVICE_COLS)} FROM device WHERE id = ?", (device_id,)).fetchone()
        return self._device(r) if r else None

    def device_by_token_hash(self, token_hash: str) -> dict | None:
        with self._lock:
            r = self._x(f"SELECT {', '.join(self.DEVICE_COLS)} FROM device WHERE token_hash = ?", (token_hash,)).fetchone()
        return self._device(r) if r else None

    def device_list(self) -> list[dict]:
        with self._lock:
            return [self._device(r) for r in self._x(f"SELECT {', '.join(self.DEVICE_COLS)} FROM device ORDER BY id").fetchall()]

    def device_update(self, device_id: str, **f) -> None:
        bad = set(f) - {"token_hash", "enabled", "last_seen_at", "name", "maker", "model", "serial", "line_id"}
        if bad:
            raise ValueError(f"cannot update {sorted(bad)}")
        with self._lock:
            self._x(f"UPDATE device SET {', '.join(f'{k} = ?' for k in f)} WHERE id = ?", (*f.values(), device_id))

    # ---- vision settings (append-only: the newest row of a scope and key is in force) ----
    def setting_get(self, scope_level: str, scope_id: str, key: str) -> str | None:
        with self._lock:
            r = self._x("SELECT value FROM vision_setting WHERE scope_level = ? AND scope_id = ? AND key = ? ORDER BY id DESC LIMIT 1",
                        (scope_level, scope_id, key)).fetchone()
        return r[0] if r else None

    def setting_add(self, scope_level: str, scope_id: str, key: str, value: str, updated_by: str, now: int) -> None:
        with self._lock:
            try:
                self._x("INSERT INTO vision_setting(scope_level, scope_id, key, value, updated_by, updated_at) VALUES (?,?,?,?,?,?)",
                        (scope_level, scope_id, key, value, updated_by, now))
                self.db.commit()
            except Exception:
                self.db.rollback()
                raise

    def settings_in_force(self) -> list[dict]:
        with self._lock:
            rows = self._x("SELECT s.scope_level, s.scope_id, s.key, s.value, s.updated_by, s.updated_at FROM vision_setting s "
                           "WHERE s.id = (SELECT MAX(i.id) FROM vision_setting i WHERE i.scope_level = s.scope_level AND i.scope_id = s.scope_id "
                           "AND i.key = s.key) ORDER BY s.scope_level, s.scope_id, s.key").fetchall()
        return [dict(zip(("scope_level", "scope_id", "key", "value", "updated_by", "updated_at"), r)) for r in rows]


class SqliteStore(Store):
    def __init__(self, path: str = ":memory:"):
        super().__init__()
        self.db = sqlite3.connect(path, check_same_thread=False)
        self._init_schema()


class PostgresStore(Store):
    placeholder = "%s"
    autoid = "BIGSERIAL PRIMARY KEY"
    # Rules turn UPDATE and DELETE into nothing; the deployed app role also has no UPDATE/DELETE grant on the table.
    APPEND_ONLY = tuple(f"CREATE OR REPLACE RULE audit_entry_no_{op.lower()} AS ON {op} TO audit_entry DO INSTEAD NOTHING"
                        for op in ("UPDATE", "DELETE"))

    def __init__(self, dsn: str, schema: str | None = None):
        import psycopg
        super().__init__()
        self.schema = schema
        self.db = psycopg.connect(dsn)
        if schema:
            self.db.execute(f'CREATE SCHEMA IF NOT EXISTS "{schema}"')
            self.db.execute(f'SET search_path TO "{schema}"')
        self._init_schema()

    def drop_schema(self) -> None:
        if self.schema:
            self.db.rollback()
            self.db.execute(f'DROP SCHEMA IF EXISTS "{self.schema}" CASCADE')
            self.db.commit()


def open_store(url: str = ":memory:") -> Store:
    """':memory:' | path.db | sqlite:///path.db | postgresql://user:pw@host/db"""
    if url.startswith(("postgresql://", "postgres://")):
        return PostgresStore(url)
    return SqliteStore(url.removeprefix("sqlite:///"))


def temp_postgres_store(dsn: str) -> PostgresStore:
    return PostgresStore(dsn, schema=f"t_{uuid.uuid4().hex[:12]}")
