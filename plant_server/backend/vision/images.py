"""NG images (docs/vision-system/NG-REVIEW.md sections 1, 3, 4): the server asks, the station answers.

A station learns in its heartbeat answer which image ids the server wants, uploads a reduced JPEG for each, then says
done (or missing when it no longer has the part). The files live under one image folder, whose total size is limited:
the oldest images go first, their rows with them.

File names never come from a station: a file is named by a hash of (station, image id, camera), so no id or camera
name can reach another path, and an image sent again lands on the same file and replaces it.
"""
from __future__ import annotations

import hashlib
import os
import re
import tempfile
import time

from .ingest import IMAGE_ID, _settle

MAX_IMAGE = 307_200
MAX_WANTED = 10
EXPIRE_MS = 7 * 86400_000
DEFAULT_LIMIT = 2 * 1024**3
FILE = re.compile(r"[0-9a-f]{40}\.jpg")
MAX_CAMERAS = 8          # distinct camera names of one image id
STALE_PART_S = 3600      # an upload that died leaves a .part file; after an hour it is junk


class Bad(Exception):
    """A refused call; status is the HTTP answer (400, 404 or 413)."""

    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


class ImageStore:
    def __init__(self, store, folder: str, limit_bytes: int = DEFAULT_LIMIT):
        self.store, self.folder, self.limit = store, folder, limit_bytes
        self._swept = 0.0
        self.sweep()

    def sweep(self) -> None:
        """Remove *.part files older than an hour. They are not in the database, so they count for nothing against the limit."""
        self._swept = time.time()
        try:
            names = os.listdir(self.folder)
        except OSError:
            return
        for n in names:
            if n.endswith(".part"):
                path = os.path.join(self.folder, n)
                try:
                    if self._swept - os.stat(path).st_mtime > STALE_PART_S:
                        os.unlink(path)
                except OSError:
                    pass

    # ---- what the server asks for ---------------------------------------------------------------
    def wanted(self, device_id: str, now_ms: int) -> list[str]:
        """Up to 10 wanted image ids of this station, newest result first. A request not answered within 7 days
        becomes expired here and is not asked again."""
        store = self.store
        with store._lock:
            _settle(store)
            try:
                store._x("UPDATE vision_image_request SET state = 'expired', updated = ? WHERE device_id = ? AND state = 'wanted' AND requested < ?",
                         (now_ms, device_id, now_ms - EXPIRE_MS))
                rows = store._x("SELECT image_id FROM vision_image_request WHERE device_id = ? AND state = 'wanted' ORDER BY result_ts DESC, requested DESC, image_id "
                                f"LIMIT {MAX_WANTED}", (device_id,)).fetchall()
                store.db.commit()
            except Exception:
                store.db.rollback()    # a failed statement must not leave the shared connection unusable
                raise
        return [r[0] for r in rows]

    def mark(self, device_id: str, image_id: str, state: str, now_ms: int) -> None:
        """done or missing. 404 when no result of this station has the id; an OK part has no request, so nothing changes."""
        _check_id(image_id)
        store = self.store
        with store._lock:
            if not _has_result(store, device_id, image_id):
                raise Bad(404, "no result of this station has that image id")
            _settle(store)
            try:
                store._x("UPDATE vision_image_request SET state = ?, updated = ? WHERE device_id = ? AND image_id = ?", (state, now_ms, device_id, image_id))
                store.db.commit()
            except Exception:
                store.db.rollback()
                raise

    # ---- upload ----------------------------------------------------------------------------------
    def check_upload(self, device_id: str, image_id: str, camera: str | None) -> None:
        """Before the body is read: the names are safe and this station has a result with that image id."""
        _check_id(image_id)
        if not camera_ok(camera):
            raise Bad(400, "camera must be 1 to 64 printable characters, not only spaces, not . or ..")
        with self.store._lock:
            if not _has_result(self.store, device_id, image_id):
                raise Bad(404, "no result of this station has that image id")
            _cameras_ok(self.store, device_id, image_id, camera)

    def save(self, device_id: str, image_id: str, camera: str, data: bytes, now_ms: int) -> None:
        if len(data) > MAX_IMAGE:
            raise Bad(413, f"the image is over {MAX_IMAGE} bytes")
        if data[:3] != b"\xff\xd8\xff":
            raise Bad(400, "the body is not a JPEG")
        if not camera_ok(camera):
            raise Bad(400, "camera must be 1 to 64 printable characters, not only spaces, not . or ..")
        name = hashlib.sha256(f"{device_id}\0{image_id}\0{camera}".encode("utf-8", "replace")).hexdigest()[:40] + ".jpg"
        os.makedirs(self.folder, exist_ok=True)
        if time.time() - self._swept > 600:
            self.sweep()
        fd, tmp = tempfile.mkstemp(dir=self.folder, suffix=".part")
        try:
            with os.fdopen(fd, "wb") as fh:
                fh.write(data)
            store = self.store
            # The check, the row, the file's replacement, the trim and the unlinking all happen under one lock: no other upload's trim
            # can delete the file of a row written here, and a file is unlinked only when no row refers to it.
            with store._lock:
                _settle(store)
                try:
                    if not _has_result(store, device_id, image_id):
                        raise Bad(404, "no result of this station has that image id")
                    _cameras_ok(store, device_id, image_id, camera)
                    store._x("INSERT INTO vision_image(device_id, image_id, camera, file, size, received) VALUES (?,?,?,?,?,?) "
                             "ON CONFLICT(device_id, image_id, camera) DO UPDATE SET file = excluded.file, size = excluded.size, received = excluded.received",
                             (device_id, image_id, camera, name, len(data), now_ms))
                    gone = self._trim()
                    os.replace(tmp, os.path.join(self.folder, name))   # one file name per image: a resend replaces it
                    store.db.commit()
                except BaseException:
                    store.db.rollback()
                    raise
                for f in set(gone):
                    if store._x("SELECT 1 FROM vision_image WHERE file = ? LIMIT 1", (f,)).fetchone() is None:
                        _unlink(os.path.join(self.folder, f))
        finally:
            _unlink(tmp)    # already moved on success: nothing to remove

    def _trim(self) -> list[str]:
        """Inside the caller's transaction: delete the oldest rows until the folder is within its limit; returns their files."""
        store, gone = self.store, []
        total = store._x("SELECT COALESCE(SUM(size), 0) FROM vision_image").fetchone()[0]
        while total > self.limit:
            rows = store._x("SELECT id, file, size FROM vision_image ORDER BY received, id LIMIT 20").fetchall()
            if not rows:
                break
            for i, f, size in rows:
                if total <= self.limit:
                    break
                store._x("DELETE FROM vision_image WHERE id = ?", (i,))
                gone.append(f)
                total -= size
        return gone

    # ---- reading ---------------------------------------------------------------------------------
    def path_of(self, device_id: str, image_id: str, camera: str) -> str | None:
        with self.store._lock:
            r = self.store._x("SELECT file FROM vision_image WHERE device_id = ? AND image_id = ? AND camera = ?", (device_id, image_id, camera)).fetchone()
        if r is None or not FILE.fullmatch(r[0]):
            return None
        p = os.path.join(self.folder, r[0])
        return p if os.path.isfile(p) else None


def camera_ok(camera) -> bool:
    """Any printable text of 1 to 64 characters, nothing stripped: no control character (NUL included), not only whitespace, and not "." or ".." (they break urls).
    The name is never a path: the file's name is a hash."""
    return isinstance(camera, str) and 1 <= len(camera) <= 64 and camera.isprintable() and camera.strip() != "" and camera not in (".", "..")


def _cameras_ok(store, device_id: str, image_id: str, camera: str) -> None:
    """At most 8 distinct camera names per image id: a ninth new name is refused (a name already kept may be sent again)."""
    rows = store._x("SELECT camera FROM vision_image WHERE device_id = ? AND image_id = ?", (device_id, image_id)).fetchall()
    if len(rows) >= MAX_CAMERAS and camera not in {r[0] for r in rows}:
        raise Bad(400, f"an image id keeps at most {MAX_CAMERAS} cameras")


def _check_id(image_id: str) -> None:
    if not IMAGE_ID.fullmatch(image_id) or image_id.strip(".") == "":
        raise Bad(400, "image id must be 1 to 64 characters of A-Z a-z 0-9 . _ -")


def _has_result(store, device_id: str, image_id: str) -> bool:
    return store._x("SELECT 1 FROM vision_result WHERE device_id = ? AND image_id = ? LIMIT 1", (device_id, image_id)).fetchone() is not None


def _unlink(path: str) -> None:
    try:
        os.unlink(path)
    except OSError:
        pass
