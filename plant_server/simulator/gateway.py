"""Edge gateway emulation: sequencing, store-and-forward buffer, duplicate injection."""
from __future__ import annotations

SCHEMA = "spi.event.v1"


class Gateway:
    def __init__(self, gateway_id: str, sink, dup_rng, duplicate_rate: float):
        self.id = gateway_id
        self.sink = sink
        self.dup_rng = dup_rng
        self.duplicate_rate = duplicate_rate
        self.seq = 0
        self.online = True
        self.buffer: list[dict] = []
        self.created = 0
        self.duplicates = 0

    def create(self, ts: str, etype: str, ctx: dict, payload: dict) -> None:
        self.seq += 1
        self.created += 1
        ev = {
            "schema": SCHEMA, "event_id": f"{self.id}:{self.seq}", "seq": self.seq,
            "gateway_id": self.id, "type": etype, "ts": ts, "emitted_ts": ts, "buffered": False,
            "plant_id": ctx["plant_id"], "area_id": ctx["area_id"], "line_id": ctx["line_id"],
            "equipment_id": ctx["equipment_id"], "payload": payload,
        }
        if self.online:
            self._send(ev)
        else:
            self.buffer.append(ev)

    def set_online(self, online: bool, now_ts: str) -> None:
        if online and not self.online:
            self.online = True
            for ev in self.buffer:  # ordered flush, original event timestamps kept
                ev["buffered"] = True
                ev["emitted_ts"] = now_ts
                self._send(ev)
            self.buffer.clear()
        elif not online and self.online:
            self.online = False

    def _send(self, ev: dict) -> None:
        self.sink.emit(ev)
        if self.duplicate_rate and self.dup_rng.random() < self.duplicate_rate:
            self.sink.emit(dict(ev))
            self.duplicates += 1
