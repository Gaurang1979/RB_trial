"""Event sinks. MQTT/HTTP sinks plug in here in Milestone 2."""
from __future__ import annotations

import json
import sys


class MemorySink:
    def __init__(self):
        self.events: list[dict] = []

    def emit(self, event: dict) -> None:
        self.events.append(event)

    def close(self) -> None:
        pass


class JsonlSink:
    """One JSON event per line. path '-' writes to stdout."""

    def __init__(self, path: str):
        self._fh = sys.stdout if path == "-" else open(path, "w", encoding="utf-8")
        self._own = path != "-"

    def emit(self, event: dict) -> None:
        self._fh.write(json.dumps(event, separators=(",", ":")) + "\n")

    def close(self) -> None:
        if self._own:
            self._fh.close()


class TeeSink:
    """Send every event to several sinks (e.g. JSONL file + MQTT)."""

    def __init__(self, *sinks):
        self.sinks = sinks

    def emit(self, event: dict) -> None:
        for s in self.sinks:
            s.emit(event)

    def close(self) -> None:
        for s in self.sinks:
            s.close()
