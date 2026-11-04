"""Run the edge agent: python -m edge --config site_edge.json [--buffer-dir ./buffer] [--client-id GW-VAD-01]

--buffer-dir persists each line's store-and-forward buffer to disk, so an outage survives a process
restart, not just an in-process reconnect (see edge/gateway.py). --client-id sets a stable MQTT client id
(persistent session): without one, a restart gets a fresh anonymous session and cannot resume being sent
anything the broker queued for it, mirroring the same durability story already used on the backend side
(backend/mqtt_ingest.py) and documented in docs/architecture/EVENT_MODEL.md.

Each line's token_file holds its gateway's device token from the plant server (docs/architecture/EDGE.md,
"Gateway token"); the server refuses messages from a gateway without a valid token.
"""
from __future__ import annotations

import argparse
import signal
import threading

from .agent import EdgeAgent
from .config import ConfigError, load_config, load_tokens


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="edge", description="Poll real equipment and publish spi.event.v1 events over MQTT")
    ap.add_argument("--config", required=True)
    ap.add_argument("--buffer-dir", help="persist store-and-forward buffers here (recommended for a real deployment)")
    ap.add_argument("--client-id", help="stable MQTT client id for a persistent session")
    args = ap.parse_args(argv)
    try:
        cfg = load_config(args.config)
        tokens = load_tokens(cfg)
    except (ConfigError, OSError) as exc:
        print(f"config error: {exc}")
        return 2
    for ln in cfg.lines:
        if ln.gateway_id not in tokens:
            print(f"warning: line {ln.id} has no token_file: the plant server refuses gateway {ln.gateway_id}'s messages")

    agent = EdgeAgent(cfg, buffer_dir=args.buffer_dir, client_id=args.client_id, tokens=tokens)
    agent.start()
    print(f"edge agent running: {sum(len(ln.equipment) for ln in cfg.lines)} equipment across {len(cfg.lines)} line(s) -> {cfg.mqtt_host}:{cfg.mqtt_port}")
    stop = threading.Event()
    signal.signal(signal.SIGINT, lambda *a: stop.set())
    signal.signal(signal.SIGTERM, lambda *a: stop.set())
    stop.wait()
    agent.stop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
