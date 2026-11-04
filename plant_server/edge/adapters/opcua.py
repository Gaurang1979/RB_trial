"""OPC-UA adapter (asyncua). asyncua is async-only; this wraps it with a private event loop thread so the
rest of the agent - built around plain polling threads, one per equipment - can call it synchronously.

Point address schema: {"node_id": "ns=2;i=3"} or {"browse_path": ["2:Press01", "2:Temperature"]} (namespace-
index:name pairs, resolved from the server's Objects node). Verified against a real asyncua server over a
real socket (tests/test_edge_opcua.py), including a live-changing tag - not just a static mock.
"""
from __future__ import annotations

import asyncio
import threading

from .base import ReadError, SignalSource


class OpcUaSource(SignalSource):
    def __init__(self, url: str, timeout: float = 5.0):
        self.url, self.timeout = url, timeout
        self._loop = None
        self._thread = None
        self._client = None

    def connect(self) -> None:
        self._loop = asyncio.new_event_loop()
        self._thread = threading.Thread(target=self._loop.run_forever, daemon=True, name="spi-opcua-loop")
        self._thread.start()
        try:
            self._run(self._aconnect()).result(timeout=self.timeout)
        except Exception as exc:
            raise ReadError(f"could not connect to OPC-UA {self.url}: {exc}") from exc

    async def _aconnect(self) -> None:
        from asyncua import Client
        self._client = Client(url=self.url, timeout=self.timeout)
        await self._client.connect()

    def close(self) -> None:
        if self._client is not None and self._loop is not None:
            try:
                self._run(self._client.disconnect()).result(timeout=self.timeout)
            except Exception:
                pass
        if self._loop is not None:
            self._loop.call_soon_threadsafe(self._loop.stop)
        if self._thread is not None:
            self._thread.join(timeout=self.timeout)

    def read(self, point: dict):
        if self._client is None:
            raise ReadError("not connected")
        try:
            return self._run(self._aread(point.address)).result(timeout=self.timeout)
        except ReadError:
            raise
        except Exception as exc:
            raise ReadError(f"OPC-UA read failed ({point.address}): {exc}") from exc

    def _run(self, coro):
        return asyncio.run_coroutine_threadsafe(coro, self._loop)

    async def _aread(self, address: dict):
        if "node_id" in address:
            node = self._client.get_node(address["node_id"])
        else:
            node = await self._client.get_objects_node().get_child(address["browse_path"])
        return await node.get_value()
