"""Edge agent: polls real equipment via edge.adapters and turns raw readings into spi.event.v1 events on an
EdgeGateway. One polling thread per equipment (fine at pilot-line scale) plus one heartbeat/uptime thread
per line. The translation follows the documented contract (docs/architecture/EVENT_MODEL.md) exactly, so the
same backend derivation code already tested against the simulator applies unchanged to real data:

  - digital points  -> `signal` events on change, plus a full resync at start and after a comm recovery
  - counter points  -> the PLC's own cumulative register is the source of truth (nothing is accumulated
                       locally, so a process restart never loses counts); an increase since the last poll
                       is reported as that many `cycle` events, each stamped at the current poll time with
                       cycle_time_s = elapsed_since_last_poll / increase (an AVERAGE, not each part's real
                       cycle time - a real cycle-pulse input, polled fast enough, gives finer timing; a
                       plain counter register, read once a poll, cannot). A register that goes backwards is
                       treated as a reset (re-baselined, no negative or fabricated events).
  - analog points   -> `sensor` events on their own sample interval, value = raw * scale + offset
  - queue_after     -> `queue` events (level, capacity) on its own sample interval
  - repeated read failures for an equipment (comm_fail_threshold consecutive) -> `comm_status ok=false`,
    and that equipment goes silent (no fabricated signals/counts) until a read succeeds again, exactly how
    the simulator represents device_comm_loss.
"""
from __future__ import annotations

import json
import threading
import time
from datetime import datetime, timezone

from spi_common.mqtt import new_client, token_properties

from .adapters import ReadError, create_source
from .config import ANALOG, COUNTER, DIGITAL, EdgeConfig, EquipmentEdge, LineEdge
from .gateway import EdgeGateway


def now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class EquipmentPoller:
    def __init__(self, cfg: EquipmentEdge, gateway: EdgeGateway, ctx: dict, clock=time.monotonic, sleep=time.sleep):
        self.cfg, self.gateway, self.ctx = cfg, gateway, ctx
        self._clock, self._sleep = clock, sleep
        self.source = create_source(cfg.protocol, cfg.connection)
        self._last_digital: dict[str, bool] = {}
        self._total, self._reject = None, None
        self._last_counter_poll: float | None = None
        self._next_due: dict[int, float] = {}   # point identity -> next-due monotonic time, for analog/queue cadence
        self._consec_fail = 0
        self._unreachable = False
        self._force_resync = True
        self._stop = threading.Event()
        self.last_error: str | None = None

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        try:
            self.source.connect()
        except ReadError as exc:
            self.last_error = str(exc)
        while not self._stop.is_set():
            t0 = self._clock()
            try:
                self.poll_once()
            except Exception as exc:  # a bug in one equipment's polling must never take the whole agent down
                self.last_error = repr(exc)
            elapsed = self._clock() - t0
            self._sleep(max(0.0, self.cfg.poll_interval_s - elapsed))
        self.source.close()

    def poll_once(self) -> None:
        now = now_iso()
        digitals = [p for p in self.cfg.points if p.kind == DIGITAL]
        counters = {p.role: p for p in self.cfg.points if p.kind == COUNTER}
        analogs = [p for p in self.cfg.points if p.kind == ANALOG]

        got_any, tried_any = False, False
        for p in digitals:
            tried_any = True
            try:
                v = bool(self.source.read(p))
            except ReadError as exc:
                self.last_error = str(exc)
                continue
            got_any = True
            if self._force_resync or self._last_digital.get(p.signal) != v:
                self.gateway.create(now, "signal", self.ctx, {"name": p.signal, "value": int(v)})
                self._last_digital[p.signal] = v

        if counters:
            tried_any = True
            try:
                self._poll_counters(counters, now)
                got_any = True
            except ReadError as exc:
                self.last_error = str(exc)

        for p in analogs:
            due = self._next_due.get(id(p), 0.0)
            if self._clock() < due and not self._force_resync:
                continue
            tried_any = True
            try:
                raw = self.source.read(p)
            except ReadError as exc:
                self.last_error = str(exc)
                continue
            got_any = True
            self._next_due[id(p)] = self._clock() + p.sample_interval_s
            value = round(raw * p.scale + p.offset, 4)
            self.gateway.create(now, "sensor", self.ctx, {"sensor_id": p.sensor_id, "name": p.name, "value": value, "unit": p.unit, "quality": "good"})

        self._update_reachability(now, tried_any, got_any)
        if got_any and not self._unreachable:
            self._force_resync = False

    def _poll_counters(self, counters: dict, now: str) -> None:
        total_p = counters.get("total")
        if total_p is None:
            return
        total_raw = self.source.read(total_p)
        reject_raw = self.source.read(counters["reject"]) if "reject" in counters else 0

        t = self._clock()
        elapsed = (t - self._last_counter_poll) if self._last_counter_poll is not None else None
        self._last_counter_poll = t
        first = self._total is None
        prev_total = self._total if self._total is not None else total_raw
        self._total, self._reject = total_raw, reject_raw
        delta = 0 if first else total_raw - prev_total
        counters_payload = {"total": total_raw, "good": max(0, total_raw - reject_raw), "reject": reject_raw}

        if self._force_resync:
            self.gateway.create(now, "counter", self.ctx, counters_payload)
        if delta > 0 and elapsed:
            per = round(elapsed / delta, 2)
            for _ in range(delta):
                self.gateway.create(now, "cycle", self.ctx, {"cycle_time_s": per, "result": "good", "counters": counters_payload})
        # delta < 0: the register went backwards (reset/rollover) - already re-baselined above, nothing emitted

    def _update_reachability(self, now: str, tried_any: bool, got_any: bool) -> None:
        if not tried_any:
            return
        if got_any:
            if self._unreachable:
                self._unreachable = False
                self._consec_fail = 0
                self._force_resync = True  # re-announce everything from the NEXT poll (this poll's fresh reads were already compared against pre-outage last-known values above, so a real change during the outage is still caught now; force_resync only adds a redundant re-announce of anything that happened to stay the same)
                self.gateway.create(now, "comm_status", self.ctx, {"ok": True})
            else:
                self._consec_fail = 0
            return
        self._consec_fail += 1
        if not self._unreachable and self._consec_fail >= self.cfg.comm_fail_threshold:
            self._unreachable = True
            self.gateway.create(now, "comm_status", self.ctx, {"ok": False})


class LineAgent:
    def __init__(self, cfg: LineEdge, gateway: EdgeGateway):
        self.cfg, self.gateway = cfg, gateway
        self.started = time.monotonic()
        ctx_base = {"plant_id": cfg.plant_id, "area_id": cfg.area_id, "line_id": cfg.id}
        self.pollers = []
        for eq in cfg.equipment:
            ctx = {**ctx_base, "equipment_id": eq.id}
            self.pollers.append(EquipmentPoller(eq, gateway, ctx))
        self._queue_pollers = [(eq, ctx_base) for eq in cfg.equipment if eq.queue_after]
        self._threads: list[threading.Thread] = []
        self._stop = threading.Event()

    def start(self) -> None:
        for p in self.pollers:
            th = threading.Thread(target=p.run, daemon=True, name=f"spi-edge-{p.cfg.id}")
            th.start()
            self._threads.append(th)
        self._threads.append(threading.Thread(target=self._run_queues, daemon=True, name=f"spi-edge-{self.cfg.id}-queues"))
        self._threads.append(threading.Thread(target=self._run_heartbeat, daemon=True, name=f"spi-edge-{self.cfg.id}-heartbeat"))
        for th in self._threads[-2:]:
            th.start()

    def stop(self) -> None:
        self._stop.set()
        for p in self.pollers:
            p.stop()
        for th in self._threads:
            th.join(timeout=5)

    def _run_queues(self) -> None:
        adapters = {}
        interval = min((eq.queue_after.sample_interval_s for eq, _ in self._queue_pollers), default=10)
        while not self._stop.is_set():
            for eq, ctx_base in self._queue_pollers:
                qa = eq.queue_after
                src = adapters.get(eq.id)
                if src is None:
                    try:
                        src = create_source(eq.protocol, eq.connection)
                        src.connect()
                    except ReadError:
                        continue
                    adapters[eq.id] = src
                try:
                    raw = src.read(qa.point)
                except ReadError:
                    continue
                level = round(raw * qa.point.scale + qa.point.offset)
                self.gateway.create(now_iso(), "queue", {**ctx_base, "equipment_id": None},
                                    {"queue_id": qa.queue_id, "level": int(level), "capacity": qa.capacity})
            self._stop.wait(interval)
        for src in adapters.values():
            src.close()

    def _run_heartbeat(self) -> None:
        ctx = {"plant_id": self.cfg.plant_id, "area_id": self.cfg.area_id, "line_id": self.cfg.id, "equipment_id": None}
        while not self._stop.is_set():
            self.gateway.create(now_iso(), "heartbeat", ctx, {"uptime_s": int(time.monotonic() - self.started), "buffered_events": self.gateway.buffered_events})
            self._stop.wait(self.cfg.heartbeat_interval_s)


class _MqttPublishSink:
    """The gateway's publish target: same topic scheme as simulator.mqtt_sink.MqttSink, but with a
    persistent session and real connect/disconnect callbacks so EdgeGateway.online reflects the actual link.
    Each message carries its gateway's device token (tokens: {gateway_id: token}) as an MQTT 5 user property."""

    def __init__(self, host: str, port: int, prefix: str, client_id: str, on_state_change, tokens: dict | None = None):
        self.prefix = prefix.rstrip("/")
        self.props = {gw: token_properties(t) for gw, t in (tokens or {}).items()}
        self.client, connect_args = new_client(client_id)
        self.client.on_connect = lambda c, u, f, rc, p=None: on_state_change(True)
        self.client.on_disconnect = lambda c, u, df, rc, p=None: on_state_change(False)
        self.client.reconnect_delay_set(min_delay=1, max_delay=30)
        self.client.connect(host, port, keepalive=30, **connect_args)
        self.client.loop_start()

    def emit(self, event: dict) -> None:
        topic = f"{self.prefix}/{event['plant_id']}/{event['line_id']}/{event['gateway_id']}/events"
        self.client.publish(topic, json.dumps(event, separators=(",", ":")), qos=1, properties=self.props.get(event["gateway_id"]))

    def close(self) -> None:
        self.client.loop_stop()
        self.client.disconnect()


class EdgeAgent:
    def __init__(self, cfg: EdgeConfig, buffer_dir: str | None = None, client_id: str | None = None, tokens: dict | None = None):
        self.cfg = cfg
        self.gateways: dict[str, EdgeGateway] = {}   # set before the sink connects: on_connect can fire immediately, on another thread
        self.sink = _MqttPublishSink(cfg.mqtt_host, cfg.mqtt_port, cfg.mqtt_prefix, client_id or "",
                                     on_state_change=self._on_state_change, tokens=tokens)
        self.gateways = {ln.id: EdgeGateway(ln.gateway_id, self.sink, buffer_file=f"{buffer_dir}/{ln.gateway_id}.jsonl" if buffer_dir else None)
                         for ln in cfg.lines}
        self.lines = [LineAgent(ln, self.gateways[ln.id]) for ln in cfg.lines]

    def _on_state_change(self, online: bool) -> None:
        now = now_iso()
        for gw in self.gateways.values():
            gw.set_online(online, now)

    def start(self) -> None:
        for ln in self.lines:
            ln.start()

    def stop(self) -> None:
        for ln in self.lines:
            ln.stop()
        self.sink.close()
