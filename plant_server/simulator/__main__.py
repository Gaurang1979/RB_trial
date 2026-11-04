"""CLI: python -m simulator --config configs/demo_factory.json --duration 3600 --out out/events.jsonl"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import replace

from .config import ConfigError, load_config
from .engine import Simulation
from .sinks import JsonlSink, TeeSink


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="simulator", description="SPI factory/edge simulator (demo data is synthetic)")
    ap.add_argument("--config", required=True)
    ap.add_argument("--duration", type=int, default=3600, help="simulated seconds")
    ap.add_argument("--out", default=None, help="events JSONL path, or '-' for stdout (default when no --mqtt)")
    ap.add_argument("--mqtt", help="publish to an MQTT broker, HOST[:PORT]")
    ap.add_argument("--mqtt-prefix", default="spi/v1")
    ap.add_argument("--mqtt-tokens", help="JSON file {gateway_id: device token} from the plant server's device registry")
    ap.add_argument("--seed", type=int, help="override config seed")
    ap.add_argument("--speed", type=float, help="real-time multiplier (e.g. 60 = 1 sim minute per second); omit for fast-forward")
    args = ap.parse_args(argv)
    try:
        cfg = load_config(args.config)
        tokens = None
        if args.mqtt_tokens:
            with open(args.mqtt_tokens, encoding="utf-8") as fh:
                tokens = json.load(fh)
            if not isinstance(tokens, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in tokens.items()):
                raise ConfigError(f"{args.mqtt_tokens}: expected a JSON object {{gateway_id: token}}")
    except (ConfigError, OSError, json.JSONDecodeError) as exc:
        print(f"config error: {exc}", file=sys.stderr)
        return 2
    if args.seed is not None:
        cfg = replace(cfg, seed=args.seed)
    out = args.out if args.out is not None else (None if args.mqtt else "-")
    sinks = [JsonlSink(out)] if out else []
    if args.mqtt:
        from .mqtt_sink import MqttSink
        host, _, port = args.mqtt.partition(":")
        sinks.append(MqttSink(host, int(port or 1883), args.mqtt_prefix, tokens=tokens))
    sink = sinks[0] if len(sinks) == 1 else TeeSink(*sinks)
    sim = Simulation(cfg, sink)
    try:
        sim.run(args.duration, args.speed)
    finally:
        sink.close()
    if out and out != "-":
        with open(out + ".truth.jsonl", "w", encoding="utf-8") as fh:
            for row in sim.truth:
                fh.write(json.dumps(row, separators=(",", ":")) + "\n")
    print(json.dumps(sim.summary(), indent=2), file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
