"""Signal-source adapters: one class per protocol, all implementing base.SignalSource."""
from __future__ import annotations

from .base import ReadError, SignalSource

_REGISTRY = {}


def _lazy():
    if _REGISTRY:
        return _REGISTRY
    from .mock import MockSource
    from .modbus_tcp import ModbusTcpSource
    from .opcua import OpcUaSource
    from .gpio import GpioSource
    _REGISTRY.update(mock=MockSource, modbus_tcp=ModbusTcpSource, opcua=OpcUaSource, gpio=GpioSource)
    return _REGISTRY


def create_source(protocol: str, connection: dict) -> SignalSource:
    reg = _lazy()
    if protocol not in reg:
        raise ValueError(f"unknown protocol '{protocol}'; expected one of {sorted(reg)}")
    return reg[protocol](**connection)


__all__ = ["SignalSource", "ReadError", "create_source"]
