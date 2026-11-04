"""Sends finished remote service sessions to ERPNext (DESIGN.md section 5).

Each session waits in the relay's outbox as one JSON file until ERPNext has taken it. ERPNext's
`remote_service.api.record_session` is idempotent by session id, so a session sent twice (a timeout after ERPNext
saved it) never makes a second log or Timesheet. A session is never dropped: a refused one stays in the outbox, with
the reason in the relay's audit, until someone fixes the cause (an unknown Employee, for example).
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import urllib.error
import urllib.request
from dataclasses import dataclass

from .store import SessionLog, Store

log = logging.getLogger("remote_service_relay.erpnext")
METHOD = "/api/method/remote_service.api.record_session"


@dataclass
class ErpNext:
    url: str
    api_key: str
    api_secret: str

    @staticmethod
    def from_env() -> "ErpNext | None":
        """ERPNEXT_URL, ERPNEXT_API_KEY, ERPNEXT_API_SECRET: an integration user's API key, never in a file."""
        url, key, secret = (os.environ.get(n, "").strip() for n in ("ERPNEXT_URL", "ERPNEXT_API_KEY", "ERPNEXT_API_SECRET"))
        if not (url and key and secret):
            return None
        if not url.startswith("https://") and not url.startswith("http://127.0.0.1") and not url.startswith("http://localhost"):
            raise ValueError("ERPNEXT_URL must be https://")
        return ErpNext(url.rstrip("/"), key, secret)

    def send(self, record: dict, timeout: float = 20.0) -> tuple[bool, str]:
        """(taken, why not). Taken: ERPNext saved it now or before."""
        body = json.dumps({"session": json.dumps(record)}).encode()
        req = urllib.request.Request(self.url + METHOD, data=body, method="POST", headers={
            "Content-Type": "application/json", "Accept": "application/json",
            "Authorization": f"token {self.api_key}:{self.api_secret}",
        })
        try:
            with urllib.request.urlopen(req, timeout=timeout) as res:
                answer = json.loads(res.read() or b"{}").get("message") or {}
                return bool(answer.get("ok")), str(answer.get("error", ""))
        except urllib.error.HTTPError as e:
            text = e.read()[:300].decode("utf-8", "replace")
            return False, f"ERPNext answered {e.code}: {text}"
        except (urllib.error.URLError, OSError, ValueError) as e:
            return False, f"ERPNext not reached: {e}"


async def outbox_worker(store: Store, sessions: SessionLog, erp: ErpNext | None, wake: asyncio.Event,
                        min_wait: float = 5.0, max_wait: float = 600.0) -> None:
    """Runs for the relay's life: sends every outbox file, oldest first, backing off while ERPNext is away."""
    if erp is None:
        log.warning("ERPNext not set (ERPNEXT_URL, ERPNEXT_API_KEY, ERPNEXT_API_SECRET): sessions wait in the outbox")
    wait = min_wait
    while True:
        try:
            await asyncio.wait_for(wake.wait(), timeout=wait)
        except asyncio.TimeoutError:
            pass
        wake.clear()
        if erp is None:
            continue
        failed = False
        for path in sessions.pending():
            try:
                record = json.loads(path.read_text())
            except (OSError, ValueError):
                continue
            ok, why = await asyncio.to_thread(erp.send, record)
            if ok:
                sessions.mark_sent(path)
                store.audit("relay", "erpnext_sent", record.get("machine", ""), record.get("session_id", ""))
            else:
                store.audit("relay", "erpnext_failed", record.get("machine", ""), f"{record.get('session_id', '')}: {why}"[:300])
                failed = True
                break   # keep the order; try again after the back-off
        wait = min(wait * 2, max_wait) if failed else min_wait
