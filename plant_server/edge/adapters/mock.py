"""In-memory adapter for testing edge/agent.py's polling and event-generation logic without any real
protocol involved. A test sets `values[key]` and the agent picks it up on the next poll; setting a key to
a ReadError instance simulates a failed read for that one point.
"""
from __future__ import annotations

import threading

from .base import SignalSource


class MockSource(SignalSource):
    def __init__(self, values: dict | None = None):
        self.values = dict(values or {})
        self._lock = threading.Lock()
        self.connected = False
        self.connect_calls = 0
        self.close_calls = 0

    def connect(self) -> None:
        self.connect_calls += 1
        self.connected = True

    def close(self) -> None:
        self.close_calls += 1
        self.connected = False

    def set(self, key, value) -> None:
        with self._lock:
            self.values[key] = value

    def read(self, point: dict):
        key = point.address["key"]
        with self._lock:
            v = self.values[key]
        if isinstance(v, Exception):
            raise v
        return v
