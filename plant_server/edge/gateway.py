"""Real edge gateway: sequencing + store-and-forward buffering for a real MQTT connection.

Reuses simulator.gateway.Gateway's already-tested envelope/sequencing/buffering logic (event_id, seq,
buffered flag, ordered flush) - the same contract the backend's ingestion already understands - but adds
what a scripted simulation doesn't need: a lock (equipment are polled on their own threads, unlike the
simulator's single-threaded step loop) and optional disk persistence, so a buffered outage survives a
process restart, not just an in-process reconnect. `online` is driven by the real MQTT client's connect/
disconnect callbacks (see edge/agent.py), not by a scenario.
"""
from __future__ import annotations

import json
import threading
from pathlib import Path

from simulator.gateway import Gateway as _Gateway


class EdgeGateway:
    def __init__(self, gateway_id: str, sink, buffer_file: str | None = None):
        self._inner = _Gateway(gateway_id, sink, dup_rng=None, duplicate_rate=0.0)
        self._lock = threading.Lock()
        self.buffer_file = Path(buffer_file) if buffer_file else None
        if self.buffer_file and self.buffer_file.exists():
            with self.buffer_file.open(encoding="utf-8") as fh:
                for line in fh:
                    line = line.strip()
                    if line:
                        self._inner.buffer.append(json.loads(line))
            if self._inner.buffer:
                self._inner.online = False  # don't silently drop a buffer left over from a crash: flush it properly on the next connect

    @property
    def id(self) -> str:
        return self._inner.id

    @property
    def online(self) -> bool:
        return self._inner.online

    @property
    def created(self) -> int:
        return self._inner.created

    @property
    def buffered_events(self) -> int:
        return len(self._inner.buffer)

    def create(self, ts: str, etype: str, ctx: dict, payload: dict) -> None:
        with self._lock:
            self._inner.create(ts, etype, ctx, payload)
            self._persist_locked()

    def set_online(self, online: bool, now_ts: str) -> None:
        with self._lock:
            self._inner.set_online(online, now_ts)
            self._persist_locked()

    def _persist_locked(self) -> None:
        if not self.buffer_file:
            return
        tmp = self.buffer_file.with_suffix(self.buffer_file.suffix + ".tmp")
        with tmp.open("w", encoding="utf-8") as fh:
            for ev in self._inner.buffer:
                fh.write(json.dumps(ev) + "\n")
        tmp.replace(self.buffer_file)  # atomic on POSIX: a crash mid-write never corrupts the persisted buffer
