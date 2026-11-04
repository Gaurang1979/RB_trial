"""Run the live server: python -m backend.serve --config configs/demo_factory.json --demo --speed 120

  --demo         run the synthetic factory simulator in-process (data is SYNTHETIC)
  --mqtt H[:P]   subscribe to a broker instead of / in addition to the demo; each message needs an active production
                 gateway's device token (docs/architecture/EDGE.md, "Gateway token")
  --mqtt-no-token  LOCAL DEMO ONLY: accept MQTT messages without a gateway token (the docker-compose simulator)
  --db           :memory: (default) | path.db | postgresql://user:pw@host/db
  --audit-anchor  file that keeps the head of the audit chain outside the database (Part 11 PT11-09); default: next to a
                 SQLite file (<db>.audit-head); give one for PostgreSQL, best on another disk or account. Env SPI_AUDIT_ANCHOR.
  --vapid-key    path to persist the Web Push (VAPID) key pair; env SPI_VAPID_KEY_FILE also sets it.
                 Omit both to run without Web Push (in-app alerts and any configured webhook still work).
Demo mode replays scripted scenarios in the first two simulated hours (fault, starvation, bypass, sensor stuck, comm loss, outage).
Env: SPI_API_KEY (shared service key), SPI_ALERT_WEBHOOK (webhook alert channel). Accounts: create the first
user (who becomes administrator) via POST /admin/users once the server is running - see docs/architecture/AUTH.md.
"""
from __future__ import annotations

import argparse
import json
import os
import threading
import time

from simulator.config import load_config
from simulator.engine import Simulation
from simulator.sinks import MemorySink

from .api import create_app
from .auth import SESSION_IDLE_S
from .live import LiveEngine
from .master import sync_site
from .store import open_store
from .topology import load_topology


def run_demo(engine: LiveEngine, cfg, speed: float, stop: threading.Event, duration: int | None = None) -> Simulation:
    sink = MemorySink()
    sim = Simulation(cfg, sink)
    flush_every = max(1, int(speed // 2))            # ~2 engine updates per real second
    started = time.monotonic()
    while not stop.is_set() and (duration is None or sim.t < duration):
        sim.step()
        if sim.t % flush_every == 0:
            batch, sink.events = sink.events, []
            engine.ingest(batch)
            engine.refresh()
            delay = started + sim.t / speed - time.monotonic()
            if delay > 0:
                stop.wait(delay)
    return sim


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="backend.serve")
    ap.add_argument("--config", required=True)
    ap.add_argument("--db", default=":memory:")
    ap.add_argument("--demo", action="store_true")
    ap.add_argument("--speed", type=float, default=120.0, help="demo simulated seconds per real second")
    ap.add_argument("--mqtt", help="HOST[:PORT]")
    ap.add_argument("--mqtt-prefix", default="spi/v1")
    ap.add_argument("--mqtt-no-token", action="store_true", help="LOCAL DEMO ONLY: do not check gateway tokens on MQTT")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8000)
    ap.add_argument("--session-idle", type=int, default=SESSION_IDLE_S // 60, help="minutes without a call before a sign-in session ends (0: no idle limit)")
    ap.add_argument("--audit-anchor", default=os.environ.get("SPI_AUDIT_ANCHOR"), help="file keeping the audit chain's head outside the database")
    ap.add_argument("--vapid-key", default=os.environ.get("SPI_VAPID_KEY_FILE"), help="path to persist the Web Push key pair (enables push)")
    ap.add_argument("--vapid-contact", default=os.environ.get("SPI_VAPID_CONTACT", "mailto:ops@example.com"))
    args = ap.parse_args(argv)
    if args.session_idle < 0:
        ap.error("--session-idle must be 0 (no idle limit) or more minutes")
    import uvicorn

    notifiers = {}
    if os.environ.get("SPI_ALERT_WEBHOOK"):
        from .alerts import WebhookNotifier
        notifiers["webhook"] = WebhookNotifier(os.environ["SPI_ALERT_WEBHOOK"])   # list "webhook" in a rule's channels to use it
    webpush_notifier, vapid_public_key = None, None
    if args.vapid_key:
        from .webpush import VapidKeys, WebPushNotifier
        keys = VapidKeys.load_or_create(args.vapid_key)
        webpush_notifier = WebPushNotifier(keys, contact=args.vapid_contact)      # list "webpush" in a rule's channels to use it
        vapid_public_key = keys.public_key_b64
    store = open_store(args.db)
    anchor = args.audit_anchor
    if anchor is None and args.db != ":memory:" and "://" not in args.db:
        anchor = args.db + ".audit-head"
    if anchor is None and "://" in args.db:
        print("WARNING: no --audit-anchor: removing the newest audit entries from the database would not be found (PT11-09)")
    # The site JSON's plants, areas, lines and gateways also go into the shared master tables (PL1).
    with open(args.config, encoding="utf-8") as fh:
        sync_site(store, json.load(fh))
    engine = LiveEngine(store, load_topology(args.config), alerts=True, notifiers=notifiers, webpush=webpush_notifier)
    stop = threading.Event()
    mqtt = None
    if args.mqtt:
        from .mqtt_ingest import MqttIngest
        h, _, p = args.mqtt.partition(":")
        if args.mqtt_no_token:
            print("WARNING: --mqtt-no-token: MQTT gateway tokens are NOT checked; anyone on the broker can write production data")
        else:
            missing = [d["id"] for d in store.device_list() if d["kind"] == "production_gateway" and d["enabled"] and not d["token_hash"]]
            if missing:
                print(f"WARNING: production gateways without a token, whose MQTT messages are refused: {', '.join(missing)} "
                      "(issue one with POST /admin/devices/{id}/token; docs/architecture/EDGE.md)")
        mqtt = MqttIngest(engine, h, int(p or 1883), args.mqtt_prefix, require_token=not args.mqtt_no_token).start()
    if args.demo:
        if args.db != ":memory:":
            print("warning: demo events reuse event ids; use a fresh database or the demo is ignored where ids already exist")
        threading.Thread(target=run_demo, args=(engine, load_config(args.config), args.speed, stop), daemon=True, name="spi-demo").start()
    app = create_app(engine, api_key=os.environ.get("SPI_API_KEY"), demo=args.demo, vapid_public_key=vapid_public_key,
                     session_idle_s=args.session_idle * 60, mqtt_ingest=mqtt, audit_anchor=anchor)
    print(f"Dashboard: http://{args.host}:{args.port}/   API docs: http://{args.host}:{args.port}/docs")
    if engine.store.user_count() == 0:
        print("No accounts yet: create the first one (it becomes administrator) with POST /admin/users {username, name, password}")
    try:
        uvicorn.run(app, host=args.host, port=args.port, log_level="warning")
    finally:
        stop.set()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
