"""Modbus TCP adapter (pymodbus). One point per read.

Point address schema: {"type": "coil"|"discrete"|"holding"|"input", "addr": int,
                        "words": int (holding/input only, default 1), "encoding": "u16"|"s16"|"u32"|"s32"|"f32" (default u16)}
Verified against a real pymodbus TCP server over a real socket (tests/test_edge_modbus.py), including a
live-changing register - not just a static mock.
"""
from __future__ import annotations

import struct

from .base import ReadError, SignalSource


def decode_registers(regs: list[int], encoding: str):
    if encoding == "u16":
        return regs[0]
    if encoding == "s16":
        return regs[0] - 0x10000 if regs[0] >= 0x8000 else regs[0]
    raw = struct.pack(">HH", regs[0] & 0xFFFF, regs[1] & 0xFFFF)
    if encoding == "u32":
        return struct.unpack(">I", raw)[0]
    if encoding == "s32":
        return struct.unpack(">i", raw)[0]
    if encoding == "f32":
        return struct.unpack(">f", raw)[0]
    raise ValueError(f"unknown Modbus encoding {encoding!r}")


class ModbusTcpSource(SignalSource):
    def __init__(self, host: str, port: int = 502, device_id: int = 1, timeout: float = 3.0):
        self.host, self.port, self.device_id, self.timeout = host, port, device_id, timeout
        self._client = None

    def connect(self) -> None:
        from pymodbus.client import ModbusTcpClient
        self._client = ModbusTcpClient(self.host, port=self.port, timeout=self.timeout)
        if not self._client.connect():
            raise ReadError(f"could not connect to Modbus TCP {self.host}:{self.port}")

    def close(self) -> None:
        if self._client:
            self._client.close()

    def read(self, point: dict):
        if self._client is None:
            raise ReadError("not connected")
        addr, typ = point.address["addr"], point.address["type"]
        try:
            if typ in ("coil", "discrete"):
                fn = self._client.read_coils if typ == "coil" else self._client.read_discrete_inputs
                r = fn(addr, count=1, device_id=self.device_id)
                if r.isError():
                    raise ReadError(str(r))
                return bool(r.bits[0])
            words = point.address.get("words", 1)
            fn = self._client.read_holding_registers if typ == "holding" else self._client.read_input_registers
            r = fn(addr, count=words, device_id=self.device_id)
            if r.isError():
                raise ReadError(str(r))
            return decode_registers(r.registers, point.address.get("encoding", "u16"))
        except ReadError:
            raise
        except Exception as exc:  # connection drop, timeout, malformed response - never a fabricated value
            raise ReadError(f"Modbus read failed ({typ} {addr}): {exc}") from exc
