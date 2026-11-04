"""Common interface for signal-source adapters. An adapter polls real hardware/a PLC for the current value
of one configured point; it never decides what that value means (raw vs. engineering units, on-change vs.
periodic emission, cycle detection) - that is edge/agent.py's job, so the same agent logic works over any
protocol.
"""
from __future__ import annotations

from abc import ABC, abstractmethod


class ReadError(Exception):
    """A point, or the whole connection, could not be read this poll. The agent treats this as silence for
    that point (never a fabricated value) and raises comm_status after enough consecutive misses."""


class SignalSource(ABC):
    @abstractmethod
    def connect(self) -> None:
        """Establish the connection. Raise ReadError on failure."""

    @abstractmethod
    def close(self) -> None:
        """Release the connection. Must not raise."""

    @abstractmethod
    def read(self, point: dict):
        """Read one configured point (an edge.config.Point - use point.address, not dict access). Raise ReadError on failure."""
