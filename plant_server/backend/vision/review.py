"""NG review (docs/vision-system/NG-REVIEW.md section 5): the people's side of the NG parts.

Every query is scoped by the principal: a part is seen when principal.can_view(plant, line), and a verdict is given
only by a person whose assignment for that line holds one of the reviewer roles. Verdicts are only ever appended; the
newest of a part is its verdict. A verdict never changes what the line did.
"""
from __future__ import annotations

import csv
import io
import json
from urllib.parse import quote

from ..auth import ADMIN, PLANT_MANAGER, PRODUCTION_MANAGER, QUALITY_MANAGER, Principal
from .ingest import _settle

VERDICTS = ("confirmed", "overruled", "unsure")
FILTERS = ("pending",) + VERDICTS + ("all",)
REVIEW_ROLES = {ADMIN, PLANT_MANAGER, PRODUCTION_MANAGER, QUALITY_MANAGER}
MAX_NOTE = 500
MAX_LIMIT = 200
# The newest verdict of a part sits on its row (ng_review keeps the history), so no query joins or looks for a maximum.
_COLS = ("r.id, r.device_id, r.plant_id, r.line_id, r.ts, r.camera, r.part_id, r.image_id, r.job, r.job_version, r.reasons, r.cycle_ms, "
         "r.verdict, r.note, r.reviewed_by, r.reviewed_at")
_FROM = "FROM vision_result r"
SUMMARY_WINDOW_MS = 7 * 86400_000


class Denied(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


def can_review(principal: Principal, plant: str | None, line: str | None) -> bool:
    if principal.kind in ("service", "anon") or principal.is_admin():
        return True
    return any(a["role"] in REVIEW_ROLES and principal._covers(a, plant, line) for a in principal.assignments)


def reviewer_name(principal: Principal) -> str:
    """Who a verdict names. A service key is nobody; 'anon' only exists when the server has no authentication at all."""
    if principal.kind == "user":
        return principal.username
    if principal.kind == "anon":
        return "anonymous"
    raise Denied(403, "a verdict names a person: sign in with your own account, not a service key")


def _scope(principal: Principal, roles=None) -> tuple[str, list] | None:
    """SQL limiting rows to the plants and lines the principal covers (with one of `roles`, when given); '' for everything, None for nothing."""
    if principal.kind in ("service", "anon") or principal.is_admin():
        return "", []
    plants, lines = set(), set()
    for a in principal.assignments:
        if roles is not None and a["role"] not in roles:
            continue
        if a["scope_level"] == "org":
            return "", []
        (plants if a["scope_level"] == "plant" else lines).add(a["scope_id"])
    parts, args = [], []
    for col, ids in (("r.plant_id", plants), ("r.line_id", lines)):
        if ids:
            parts.append(f"{col} IN ({','.join('?' * len(ids))})")
            args += sorted(ids)
    return (" AND (" + " OR ".join(parts) + ")", args) if parts else None


def _like(s: str) -> str:
    return s.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


class Review:
    def __init__(self, store, images, clock):
        self.store, self.images, self.clock = store, images, clock

    # ---- reading ---------------------------------------------------------------------------------
    def _where(self, principal, f: dict, roles=None) -> tuple[str, list] | None:
        scope = _scope(principal, roles)
        if scope is None:
            return None
        w, a = " WHERE r.ok = 0" + scope[0], list(scope[1])
        for col, key in (("r.plant_id", "plant"), ("r.line_id", "line"), ("r.device_id", "station")):
            if f.get(key):
                w += f" AND {col} = ?"
                a.append(f[key])
        if f.get("since") is not None:
            w += " AND r.ts >= ?"
            a.append(f["since"])
        if f.get("until") is not None:
            w += " AND r.ts <= ?"
            a.append(f["until"])
        verdict = f.get("verdict", "all")
        if verdict == "pending":
            w += " AND r.verdict IS NULL"
        elif verdict in VERDICTS:
            w += " AND r.verdict = ?"
            a.append(verdict)
        if f.get("reason"):
            # narrows in SQL (case-blind in SQLite); the exact match is checked on the parsed reasons in rows()
            w += " AND r.reasons LIKE ? ESCAPE '\\'"
            a.append("%" + _like('"reason":' + json.dumps(f["reason"], ensure_ascii=False)) + "%")
        return w, a

    @staticmethod
    def list_sql(w: str, a: list, before, limit: int) -> tuple[str, list]:
        """One page of NG parts, newest first by id; the partial indexes of vision_result serve it (the tests read the plan)."""
        q, args = f"SELECT {_COLS} {_FROM}{w}", list(a)
        if before is not None:
            q += " AND r.id < ?"
            args.append(before)
        return q + " ORDER BY r.id DESC LIMIT ?", args + [limit]

    def rows(self, principal, f: dict, limit: int, roles=None) -> list[dict]:
        """NG parts newest first (by id), at most `limit`, after f['before_id']."""
        built = self._where(principal, f, roles)
        if built is None:
            return []
        w, a = built
        out, before = [], f.get("before_id")
        while len(out) < limit:
            q, args = self.list_sql(w, a, before, limit)
            with self.store._lock:
                got = self.store._x(q, args).fetchall()
            for r in got:
                if not principal.can_view(r[2], r[3]):   # the SQL scope already did this; the principal has the last word
                    continue
                if f.get("reason") and not any(isinstance(x, dict) and x.get("reason") == f["reason"] for x in json.loads(r[10])):
                    continue
                out.append(r)
                if len(out) == limit:
                    break
            if len(got) < limit:
                break
            before = got[-1][0]
        return self._items(out)

    def _items(self, rows) -> list[dict]:
        images, requests = {}, {}
        by_dev: dict[str, set] = {}
        for r in rows:
            if r[7]:
                by_dev.setdefault(r[1], set()).add(r[7])
        with self.store._lock:
            for dev, ids in by_dev.items():
                ids = sorted(ids)
                marks = ",".join("?" * len(ids))
                for iid, cam in self.store._x(f"SELECT image_id, camera FROM vision_image WHERE device_id = ? AND image_id IN ({marks}) ORDER BY camera",
                                              [dev, *ids]).fetchall():
                    images.setdefault((dev, iid), []).append(cam)
                for iid, state in self.store._x(f"SELECT image_id, state FROM vision_image_request WHERE device_id = ? AND image_id IN ({marks})",
                                                [dev, *ids]).fetchall():
                    requests[(dev, iid)] = state
        out = []
        for r in rows:
            cams = images.get((r[1], r[7]), [])
            out.append({"id": r[0], "plant": r[2], "line": r[3], "station": r[1], "ts": r[4], "camera": r[5], "part_id": r[6], "image_id": r[7],
                        "job": r[8], "job_version": r[9], "reasons": json.loads(r[10]), "cycle_ms": r[11],
                        "verdict": r[12] or "pending", "note": r[13], "reviewed_by": r[14], "reviewed_at": r[15],
                        "cameras": cams, "images": [{"camera": c, "url": f"/vision/ng/{r[0]}/image/{quote(c, safe='')}"} for c in cams],
                        "image_state": _image_state(r[7], cams, requests.get((r[1], r[7])))})
        return out

    def one(self, principal, result_id: int) -> dict:
        """One NG part with its verdict history (newest first). 404 for an OK part, and also for one outside the caller's view: it is not said to exist."""
        with self.store._lock:
            r = self.store._x(f"SELECT {_COLS} {_FROM} WHERE r.id = ? AND r.ok = 0", (result_id,)).fetchone()
        if r is None or not principal.can_view(r[2], r[3]):
            raise Denied(404, f"no NG part {result_id}")
        item = self._items([r])[0]
        with self.store._lock:
            hist = self.store._x("SELECT id, verdict, note, by_user, ts FROM ng_review WHERE result_id = ? ORDER BY id DESC", (result_id,)).fetchall()
        item["history"] = [{"id": h[0], "verdict": h[1], "note": h[2], "by": h[3], "ts": h[4]} for h in hist]
        return item

    def image_path(self, principal, result_id: int, camera: str) -> str:
        item = self.one(principal, result_id)
        p = self.images.path_of(item["station"], item["image_id"], camera) if item["image_id"] else None
        if p is None:
            raise Denied(404, "no image of that camera for this part")
        return p

    def summary_sql(self, principal, f: dict) -> tuple[str, list] | None:
        """The last 7 days unless since/until say otherwise (a window ending at `until` when only that is given)."""
        g = {k: f.get(k) for k in ("plant", "line", "station", "since", "until")}
        if g["since"] is None:
            g["since"] = (g["until"] if g["until"] is not None else self.clock()) - SUMMARY_WINDOW_MS
        built = self._where(principal, g)
        if built is None:
            return None
        w, a = built
        return f"SELECT r.plant_id, r.line_id, r.verdict, COUNT(*) {_FROM}{w} GROUP BY r.plant_id, r.line_id, r.verdict", a

    def summary(self, principal, f: dict) -> list[dict]:
        built = self.summary_sql(principal, f)
        if built is None:
            return []
        with self.store._lock:
            rows = self.store._x(*built).fetchall()
        lines: dict = {}
        for plant, line, verdict, n in rows:
            if not principal.can_view(plant, line):
                continue
            s = lines.setdefault((plant, line), {"plant": plant, "line": line, "ng": 0, "reviewed": 0, "pending": 0, **{v: 0 for v in VERDICTS}})
            s["ng"] += n
            s[verdict or "pending"] += n
            if verdict:
                s["reviewed"] += n
        for s in lines.values():
            s["overrule_rate"] = round(s["overruled"] / s["reviewed"], 4) if s["reviewed"] else None
        return [lines[k] for k in sorted(lines)]

    # ---- giving a verdict -----------------------------------------------------------------------
    def give(self, principal, result_id: int, verdict: str, note: str) -> dict:
        by = reviewer_name(principal)
        item = self.one(principal, result_id)
        if not can_review(principal, item["plant"], item["line"]):
            raise Denied(403, "only an administrator, plant, production or quality manager of this line gives a verdict")
        store, now = self.store, self.clock()
        with store._lock:
            _settle(store)
            try:
                # The history row and the part's newest verdict change together, or not at all.
                store._x("INSERT INTO ng_review(result_id, verdict, note, by_user, user_id, ts) VALUES (?,?,?,?,?,?)",
                         (result_id, verdict, note, by, principal.user_id, now))
                store._x("UPDATE vision_result SET verdict = ?, note = ?, reviewed_by = ?, reviewed_at = ? WHERE id = ?", (verdict, note, by, now, result_id))
                store.db.commit()
            except Exception:
                store.db.rollback()
                raise
        return self.one(principal, result_id)

    # ---- export ----------------------------------------------------------------------------------
    def export(self, principal, f: dict):
        """CSV of the reviewed parts of the lines the caller may review, oldest first, in pages so a long history is never held whole."""
        if _scope(principal, REVIEW_ROLES) is None:
            raise Denied(403, "only reviewers export verdicts")
        f = {**f, "verdict": f.get("verdict") if f.get("verdict") in VERDICTS else "all"}
        head = ["id", "plant", "line", "station", "ts", "part_id", "image_id", "cameras", "reasons", "verdict", "note", "reviewed_by", "reviewed_at"]

        def gen():
            yield _csv(head)
            after = 0
            while True:
                built = self._where(principal, f, REVIEW_ROLES)
                if built is None:
                    return
                w, a = built
                w += " AND r.verdict IS NOT NULL AND r.id > ?"
                with self.store._lock:
                    got = self.store._x(f"SELECT {_COLS} {_FROM}{w} ORDER BY r.id LIMIT 500", a + [after]).fetchall()
                if not got:
                    return
                after = got[-1][0]
                for it in self._items([r for r in got if can_review(principal, r[2], r[3])]):
                    yield _csv([it["id"], it["plant"], it["line"], it["station"], it["ts"], it["part_id"], it["image_id"], " ".join(it["cameras"]),
                                "; ".join(f"{x.get('tool')}: {x.get('reason')}" for x in it["reasons"] if isinstance(x, dict)),
                                it["verdict"], it["note"], it["reviewed_by"], it["reviewed_at"]])
        return gen()


def _image_state(image_id, cams, request) -> str:
    """none (the part has no image id), ready, waiting (asked, not yet sent), missing (the line PC no longer has it),
    expired (not answered within 7 days) or removed (it was there, the folder's size limit deleted it)."""
    if not image_id:
        return "none"
    if cams:
        return "ready"
    return {"wanted": "waiting", "missing": "missing", "expired": "expired", "done": "removed"}.get(request or "", "none")


def cell(v):
    """A text that starts like a formula would run in a spreadsheet: a quote in front makes it plain text."""
    if isinstance(v, str) and v[:1] in ("=", "+", "-", "@", "\t", "\r"):
        return "'" + v
    return "" if v is None else v


def _csv(row) -> str:
    out = io.StringIO()
    csv.writer(out, lineterminator="\r\n").writerow([cell(v) for v in row])
    return out.getvalue()
