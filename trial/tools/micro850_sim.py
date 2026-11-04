#!/usr/bin/env python3
"""Micro850 stand-in for a bench check of the vision PC's PLC link (TRIAL; standard library only).

Acts as the line program HARPIK08042026_Final_141026 does towards the vision PC:
  - Modbus TCP server, holding registers 400001-400050 (offsets 0-49), functions 3, 6 and 16;
  - register 3 (PASS) and 4 (FAIL): counted and cleared after 50 ms / 100 ms, as the PLC does;
  - register 43 bit 1 (vision heartbeat): unchanged for 5 s -> prints "SOFTWARE CRASH" (the PLC stops the line);
  - register 25 counts up like the encoder.
It does not fire cameras or reject: it only proves the link, the heartbeat and the result writes.

  python micro850_sim.py [--port 502] [--bind 0.0.0.0] [--base 0 | 4096 for the Delta DVP map]
Then set Micro850:PlcIp to this PC's address in vs-runtime.json.
"""
import argparse, socketserver, struct, threading, time

BASE = 0
regs = [0] * 50
for k, v in {0: 30, 1: 500, 2: 40, 28: 200, 39: 10, 40: 5, 41: 5, 42: 5, 44: 400, 46: 35, 47: 170, 48: 1, 49: 200}.items():
    regs[k] = v
lock = threading.Lock()
stats = {"pass": 0, "fail": 0}
hb = {"value": None, "since": time.monotonic(), "crashed": False}


def on_write(addr, value):
    if addr == 3 and value >= 1:
        stats["pass"] += 1
        threading.Timer(0.05, lambda: clear(3)).start()
    if addr == 4 and value >= 1:
        stats["fail"] += 1
        threading.Timer(0.10, lambda: clear(4)).start()
    if addr == 43:
        bit = (value >> 1) & 1
        if bit != hb["value"]:
            hb["value"], hb["since"] = bit, time.monotonic()
            if hb["crashed"]:
                hb["crashed"] = False
                print("heartbeat back")


def clear(addr):
    with lock:
        regs[addr] = 0


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        print("vision PC connected from", self.client_address[0])
        s = self.request
        while True:
            head = recv(s, 7)
            if not head:
                break
            tid, _, length, unit = struct.unpack(">HHHB", head)
            pdu = recv(s, length - 1)
            if not pdu:
                break
            fc = pdu[0]
            with lock:
                if fc == 3:
                    addr, n = struct.unpack(">HH", pdu[1:5])
                    addr -= BASE
                    if addr < 0 or addr + n > 50:
                        out = bytes([0x83, 2])
                    else:
                        out = bytes([3, 2 * n]) + b"".join(struct.pack(">H", regs[addr + i] & 0xFFFF) for i in range(n))
                elif fc == 6:
                    addr, v = struct.unpack(">HH", pdu[1:5])
                    addr -= BASE
                    if not 0 <= addr < 50:
                        out = bytes([0x86, 2])
                    else:
                        regs[addr] = v
                        on_write(addr, v)
                        out = pdu[:5]
                elif fc == 16:
                    addr, n, _ = struct.unpack(">HHB", pdu[1:6])
                    addr -= BASE
                    for i in range(n):
                        v = struct.unpack(">H", pdu[6 + 2 * i:8 + 2 * i])[0]
                        regs[addr + i] = v
                        on_write(addr + i, v)
                    out = pdu[:5]
                else:
                    out = bytes([fc | 0x80, 1])
            s.sendall(struct.pack(">HHHB", tid, 0, len(out) + 1, unit) + out)
        print("vision PC disconnected")


def recv(s, n):
    b = b""
    while len(b) < n:
        k = s.recv(n - len(b))
        if not k:
            return None
        b += k
    return b


def ticker():
    while True:
        time.sleep(1)
        with lock:
            regs[25] = (regs[25] + 37) & 0x7FFF
        if not hb["crashed"] and time.monotonic() - hb["since"] > 5:
            hb["crashed"] = True
            print("SOFTWARE CRASH: vision heartbeat (register 43 bit 1) unchanged for 5 s - the real PLC stops the line")
        print(f"pass={stats['pass']} fail={stats['fail']} heartbeat_bit={hb['value']} setpoints={regs[0]},{regs[1]},{regs[2]},{regs[44]},{regs[46]},{regs[47]}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=502)
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--base", type=int, default=0, help="Modbus address of register 0: 0 Micro850, 4096 Delta DVP (D0)")
    a = ap.parse_args()
    BASE = a.base
    threading.Thread(target=ticker, daemon=True).start()
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer((a.bind, a.port), Handler) as srv:
        print(f"Micro850 stand-in on {a.bind}:{a.port}")
        srv.serve_forever()
