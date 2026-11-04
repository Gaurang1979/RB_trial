"""Digital I/O adapter for a Raspberry Pi's GPIO header (gpiozero, which wraps RPi.GPIO/lgpio - whichever
backend is installed). Point address schema: {"pin": int, "pull_up": bool (default False)}.

NOT TESTABLE IN THIS ENVIRONMENT: gpiozero needs a real GPIO character device, which only exists on actual
Raspberry Pi hardware (or a pin emulator no substitute for the real thing). The import is guarded so the rest
of the edge package still loads on ordinary machines; using this adapter off a Pi raises ReadError with a
clear message rather than failing in a confusing way. Confirm this adapter against real pins before relying
on it - unlike the Modbus and OPC-UA adapters, it has not been exercised against anything real here.
"""
from __future__ import annotations

from .base import ReadError, SignalSource

try:
    from gpiozero import DigitalInputDevice
except Exception:  # gpiozero absent, or present but no GPIO device on this machine
    DigitalInputDevice = None


class GpioSource(SignalSource):
    def __init__(self):
        if DigitalInputDevice is None:
            raise ReadError("gpiozero is not usable here (no GPIO device) - the GPIO adapter only runs on a Raspberry Pi")
        self._pins: dict[int, DigitalInputDevice] = {}

    def connect(self) -> None:
        pass  # pins are opened lazily per point, on first read

    def close(self) -> None:
        for dev in self._pins.values():
            dev.close()
        self._pins.clear()

    def read(self, point: dict):
        addr = point.address
        pin = addr["pin"]
        dev = self._pins.get(pin)
        if dev is None:
            try:
                dev = DigitalInputDevice(pin, pull_up=addr.get("pull_up", False))
            except Exception as exc:
                raise ReadError(f"could not open GPIO pin {pin}: {exc}") from exc
            self._pins[pin] = dev
        return bool(dev.value)
