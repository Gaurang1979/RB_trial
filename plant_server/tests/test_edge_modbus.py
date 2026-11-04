"""Integration tests against a REAL Modbus TCP server (pymodbus) over a real socket on 127.0.0.1 - not a
mock of the adapter. Proves ModbusTcpSource genuinely speaks the protocol, and that EquipmentPoller correctly
turns live register changes into the right events end to end.
"""
import asyncio
import threading
import time

import pytest

from edge.adapters.modbus_tcp import ModbusTcpSource, decode_registers
from edge.agent import EquipmentPoller
from edge.config import EquipmentEdge, Point
from edge.gateway import EdgeGateway

pytest.importorskip("pymodbus")


class ModbusTestServer:
    """A real pymodbus TCP server on a free localhost port. Values are set through a second, plain Modbus
    client performing real write requests - the same way anything else would change these registers - not
    by reaching into server internals."""

    def __init__(self):
        from pymodbus.datastore import ModbusDeviceContext, ModbusSequentialDataBlock, ModbusServerContext
        co = ModbusSequentialDataBlock(1, [0] * 200)
        hr = ModbusSequentialDataBlock(1, [0] * 200)
        ctx = ModbusServerContext(devices=ModbusDeviceContext(co=co, hr=hr), single=True)
        self._loop = asyncio.new_event_loop()
        self._server = None
        self.port = None

        def run():
            asyncio.set_event_loop(self._loop)

            async def start():
                import socket
                from pymodbus.server import StartAsyncTcpServer
                s = socket.socket(); s.bind(("127.0.0.1", 0)); self.port = s.getsockname()[1]; s.close()
                self._server = asyncio.ensure_future(StartAsyncTcpServer(context=ctx, address=("127.0.0.1", self.port)))
                await self._server

            self._loop.run_until_complete(start())

        self._thread = threading.Thread(target=run, daemon=True)
        self._thread.start()
        for _ in range(100):
            if self.port is not None:
                try:
                    import socket
                    s = socket.create_connection(("127.0.0.1", self.port), timeout=0.2); s.close()
                    break
                except OSError:
                    pass
            time.sleep(0.05)
        else:
            raise RuntimeError("Modbus test server did not start")
        from pymodbus.client import ModbusTcpClient
        self._writer = ModbusTcpClient("127.0.0.1", port=self.port, timeout=2)
        self._writer.connect()

    def set_coil(self, addr, value):
        r = self._writer.write_coil(addr, bool(value))
        assert not r.isError(), r

    def set_holding_u16(self, addr, value):
        r = self._writer.write_register(addr, value & 0xFFFF)
        assert not r.isError(), r

    def set_holding_u32(self, addr, value):
        r = self._writer.write_registers(addr, [(value >> 16) & 0xFFFF, value & 0xFFFF])
        assert not r.isError(), r

    def close(self):
        self._writer.close()
        self._loop.call_soon_threadsafe(self._server.cancel)
        time.sleep(0.1)
        self._loop.call_soon_threadsafe(self._loop.stop)
        self._thread.join(timeout=2)


@pytest.fixture
def server():
    srv = ModbusTestServer()
    yield srv
    srv.close()


def test_decode_registers_all_encodings():
    assert decode_registers([42], "u16") == 42
    assert decode_registers([0xFFFF], "s16") == -1
    assert decode_registers([0x0001, 0x0000], "u32") == 0x00010000
    assert decode_registers([0xFFFF, 0xFFFF], "s32") == -1
    import struct
    hi, lo = struct.pack(">f", 3.5)[:2], struct.pack(">f", 3.5)[2:]
    regs = [int.from_bytes(hi, "big"), int.from_bytes(lo, "big")]
    assert decode_registers(regs, "f32") == 3.5


def test_real_coil_read_over_a_real_socket(server):
    server.set_coil(0, True)
    src = ModbusTcpSource("127.0.0.1", port=server.port, timeout=2)
    src.connect()
    v = src.read(Point(kind="digital", signal="x", address={"type": "coil", "addr": 0}))
    assert v is True
    server.set_coil(0, False)
    v2 = src.read(Point(kind="digital", signal="x", address={"type": "coil", "addr": 0}))
    assert v2 is False       # genuinely re-read live, not cached
    src.close()


def test_real_holding_register_u32_read_over_a_real_socket(server):
    server.set_holding_u32(100, 123456)
    src = ModbusTcpSource("127.0.0.1", port=server.port, timeout=2)
    src.connect()
    v = src.read(Point(kind="counter", role="total", address={"type": "holding", "addr": 100, "words": 2, "encoding": "u32"}))
    assert v == 123456
    src.close()


def test_read_failure_when_server_is_unreachable():
    from edge.adapters.base import ReadError
    src = ModbusTcpSource("127.0.0.1", port=1, timeout=0.5)   # nothing listens on port 1
    with pytest.raises(ReadError):
        src.connect()


def test_equipment_poller_end_to_end_against_a_real_modbus_server(server):
    """The full stack: real register changes -> real Modbus reads -> the right spi.event.v1 events."""
    server.set_coil(0, True)       # machine_on
    server.set_holding_u32(100, 50)  # total count baseline

    class FakeSink:
        def __init__(self): self.events = []
        def emit(self, ev): self.events.append(ev)

    cfg = EquipmentEdge(id="PRESS-01", protocol="modbus_tcp", connection={"host": "127.0.0.1", "port": server.port, "timeout": 2},
                        poll_interval_s=1, points=(
                            Point(kind="digital", signal="machine_on", address={"type": "coil", "addr": 0}),
                            Point(kind="counter", role="total", address={"type": "holding", "addr": 100, "words": 2, "encoding": "u32"}),
                        ))
    gw = EdgeGateway("GW", FakeSink())
    ctx = {"plant_id": "P", "area_id": "A", "line_id": "L", "equipment_id": "PRESS-01"}
    t = {"v": 0.0}
    poller = EquipmentPoller(cfg, gw, ctx, clock=lambda: t["v"], sleep=lambda s: None)
    poller.source.connect()

    poller.poll_once()   # baseline: machine_on=1 signal, counter snapshot, no cycles yet
    server.set_holding_u32(100, 55)   # 5 real parts made on the real server
    t["v"] = 20.0
    poller.poll_once()
    poller.source.close()

    evs = gw._inner.sink.events
    assert [e["payload"] for e in evs if e["type"] == "signal"] == [{"name": "machine_on", "value": 1}]
    cyc = [e["payload"] for e in evs if e["type"] == "cycle"]
    assert len(cyc) == 5 and cyc[-1]["counters"]["total"] == 55 and all(c["cycle_time_s"] == 4.0 for c in cyc)

    server.set_coil(0, False)         # a real state change on the real server
    poller.source.connect()
    poller.poll_once()
    sigs = [e["payload"] for e in gw._inner.sink.events if e["type"] == "signal"]
    assert sigs[-1] == {"name": "machine_on", "value": 0}
