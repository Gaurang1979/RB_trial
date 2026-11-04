"""MQTT subscriber feeding the live engine. All DB work happens on one worker thread; the paho thread only enqueues.

Persistent session (fixed client_id, MQTT 5 session that never expires) + QoS 1: the broker queues events while the
backend is down, and at-least-once delivery duplicates are removed by ingestion (event_id).

Gateway tokens: each message must carry an active production gateway's device token (spi_common.mqtt.TOKEN_PROPERTY),
and its event must be that gateway's (gateway_id, and the gateway's plant and line when the registry sets them). Any other
message is refused: counted and logged (never the token or the body), and never stored, not even as a rejected event.
Tokens are looked up once per distinct token per batch, so a revoked or replaced token stops working at the next batch.
"""
from __future__ import annotations

import json
import logging
import queue
import threading
import time
from datetime import datetime, timezone

from spi_common.mqtt import message_token, new_client

from .master import device_for_token

log = logging.getLogger(__name__)
LOG_EVERY_S = 60.0   # one log line per reason per minute at most; the counts keep the rest
MAX_GATEWAYS = 200   # last refusals kept per claimed gateway_id (an attacker picks the ids)
MAX_GATEWAY_ID = 64
UNKNOWN_GATEWAY = "(none)"
MAX_TOKEN = 128      # issued tokens are 43 characters; anything longer is refused without a lookup


def owners(topo) -> dict:
    """{(kind, id): gateway_id} for every equipment, sensor and queue of the site, from the topology."""
    out = {}
    for ln in topo.lines.values():
        for e in ln.equipment:
            out[("equipment", e.id)] = e.gateway_id
            out.update({("sensor", x.id): e.gateway_id for x in e.sensors})
            out.update({("queue", q.id): e.gateway_id for q in (e.up_queue, e.down_queue) if q is not None})
    return out


def refusal(device: dict | None, event, owner: dict) -> str | None:
    """Why a message from the gateway a token names may not be stored, or None when it may. The event must be
    for the gateway's own plant and line, and every equipment, sensor and queue it names must be that gateway's
    in the site topology (derivation finds events by equipment and subject alone). A message that is not a
    JSON object passes here (its gateway is known) and is quarantined by ingestion as before."""
    if device is None:
        return "no valid gateway token"
    if device["kind"] != "production_gateway":
        return "token is not a production gateway's"
    if not device["plant_id"] or not device["line_id"]:
        return "gateway has no plant and line in the device registry"
    if not isinstance(event, dict):
        return None
    for field, own in (("gateway_id", device["id"]), ("plant_id", device["plant_id"]), ("line_id", device["line_id"])):
        if event.get(field) != own:
            return f"{field} is not the token's gateway's"
    payload = event.get("payload") if isinstance(event.get("payload"), dict) else {}
    named = [("equipment", event.get("equipment_id")), ("sensor", payload.get("sensor_id")), ("queue", payload.get("queue_id"))]
    for kind, sid in named:
        if sid is not None and (not isinstance(sid, str) or owner.get((kind, sid)) != device["id"]):
            return f"{kind} is not the token's gateway's"
    return None


class MqttIngest:
    def __init__(self, engine, host: str = "127.0.0.1", port: int = 1883, prefix: str = "spi/v1", client_id: str | None = "spi-backend",
                 batch_size: int = 500, flush_s: float = 0.3, refresh_s: float = 1.0,
                 require_token: bool = True):
        self.engine, self.host, self.port, self.prefix = engine, host, port, prefix.rstrip("/")
        self.client_id, self.batch_size, self.flush_s, self.refresh_s = client_id, batch_size, flush_s, refresh_s
        self.q: queue.Queue = queue.Queue()
        self.received = 0
        self.require_token = require_token
        self.refused = 0
        self.refused_reasons: dict[str, int] = {}
        self._last_log: dict[str, float] = {}
        self._lock = threading.Lock()
        self.last_by_gateway: dict[str, dict] = {}
        self._owner: dict | None = None
        self.errors: list[str] = []
        self._stop = threading.Event()
        self._worker: threading.Thread | None = None
        self.client = None

    def start(self) -> "MqttIngest":
        c, connect_args = new_client(self.client_id)
        c.on_connect = lambda cl, u, f, rc, p=None: cl.subscribe(f"{self.prefix}/#", qos=1)
        c.on_message = self._on_message
        c.connect(self.host, self.port, keepalive=30, **connect_args)
        c.loop_start()
        self.client = c
        self._worker = threading.Thread(target=self._run, name="spi-mqtt-ingest", daemon=True)
        self._worker.start()
        return self

    def _on_message(self, client, userdata, msg) -> None:
        self.received += 1
        try:
            event = json.loads(msg.payload)
        except (ValueError, UnicodeDecodeError):
            event = msg.payload.decode("utf-8", "replace")  # quarantined by ingestion as 'not a JSON object'
        self.q.put((message_token(msg), msg.topic, event))

    def authorise(self, batch: list) -> list:
        """The events of (token, topic, event) messages whose token allows them; the rest are refused."""
        if not self.require_token:
            return [ev for _, _, ev in batch]
        if self._owner is None:
            self._owner = owners(self.engine.topo)
        devices, out = {}, []
        for token, topic, ev in batch:
            if token not in devices:   # a token of the wrong shape is never looked up
                devices[token] = device_for_token(self.engine.store, token) if isinstance(token, str) and len(token) <= MAX_TOKEN else None
            reason = refusal(devices[token], ev, self._owner)
            if reason is None:
                out.append(ev)
            else:
                self._refuse(reason, topic, ev)
        return out

    def _refuse(self, reason: str, topic: str, event) -> None:
        claimed = event.get("gateway_id") if isinstance(event, dict) else None
        gateway = str(claimed)[:MAX_GATEWAY_ID] if isinstance(claimed, str) and claimed else UNKNOWN_GATEWAY
        with self._lock:
            self.refused += 1
            self.refused_reasons[reason] = self.refused_reasons.get(reason, 0) + 1
            count = self.refused_reasons[reason]
            self.last_by_gateway.pop(gateway, None)   # re-insert: the dict stays in order of last refusal
            self.last_by_gateway[gateway] = {"reason": reason, "topic": topic[:120], "at": datetime.now(timezone.utc).isoformat(timespec="seconds")}
            while len(self.last_by_gateway) > MAX_GATEWAYS:   # senders choose gateway_id: the table is bounded
                del self.last_by_gateway[next(iter(self.last_by_gateway))]
        now = time.monotonic()
        if now - self._last_log.get(reason, -LOG_EVERY_S) >= LOG_EVERY_S:   # keyed by reason only: senders cannot grow it
            self._last_log[reason] = now
            log.warning("MQTT message refused (%s): topic %r, gateway_id %r; %d refused so far for this reason",
                        reason, topic[:120], str(claimed)[:64], count)

    def refusal_report(self) -> dict:
        """Refused-message counts by reason and the last refusal per claimed gateway. Never holds a token."""
        with self._lock:
            return {"require_token": self.require_token, "received": self.received, "refused": self.refused,
                    "by_reason": dict(self.refused_reasons),
                    "last_by_gateway": [{"gateway_id": g, **v} for g, v in reversed(self.last_by_gateway.items())]}

    def _run(self) -> None:
        last_refresh = 0.0
        while not (self._stop.is_set() and self.q.empty()):
            batch, deadline = [], time.monotonic() + self.flush_s
            while len(batch) < self.batch_size and time.monotonic() < deadline:
                try:
                    batch.append(self.q.get(timeout=0.05))
                except queue.Empty:
                    if batch:
                        break
            try:
                events = self.authorise(batch) if batch else []
                if events:
                    self.engine.ingest(events)
                if time.monotonic() - last_refresh >= self.refresh_s:
                    self.engine.refresh()
                    last_refresh = time.monotonic()
            except Exception as exc:  # keep ingesting; surface the problem
                self.errors.append(repr(exc))

    def stop(self) -> None:
        if self.client:
            self.client.loop_stop()
            self.client.disconnect()
        self._stop.set()
        if self._worker:
            self._worker.join(timeout=20)
        try:
            self.engine.refresh()
        except Exception as exc:
            self.errors.append(repr(exc))
