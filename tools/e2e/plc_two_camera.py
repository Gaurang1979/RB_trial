"""A simulated PLC for the two-camera end-to-end test: Modbus TCP client (FC3 / FC16) of vs-runtime's
PLC server, doing what the line's PLC does with cameras at two places (PLC-INTERFACE.md 3.5, 3.7, 4.1):

    python3 tools/e2e/plc_two_camera.py <port> <parts> <truth.csv> [--batch "B.NO A2610307"] [--gap-ms 0]

- the two-way heartbeat every 100 ms and RunEnable;
- for each part n (1..parts): the text block with the batch line and TextTriggerId = n, PartID "P<n>",
  then camera 1's channel trigger with TriggerId n; wait for Done with the echo n, read Ok / Ng and
  the first failing tool; drop the trigger; then the same on camera 2's channel (the part reached it).
  (A real line triggers the second camera while the first one works on the next part; this test
  triggers one at a time, so every timing is one camera's own.)
- the part is rejected when either camera said Ng, did not answer in 5 s, or rejected the trigger.

Prints one CSV line per part (part, cam1, cam2, decision, expected, kind, cam1 ms, cam2 ms, cam1 reason,
cam2 reason) and a summary: bad parts passed (must be 0), good parts rejected, timings.
"""

import argparse
import csv
import socket
import statistics
import struct
import sys
import threading
import time

IN_REGS = 32          # main input block, registers 0-31
CAM_IN, CAM_OUT = 32, 228
TEXT = 500


class Plc:
    def __init__(self, port):
        self.s = socket.create_connection(("127.0.0.1", port))
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.lock = threading.Lock()
        self.tid = 0
        self.inb = bytearray(64)
        self.hb = 0
        threading.Thread(target=self._heartbeat, daemon=True).start()

    def _rx(self, n):
        b = b""
        while len(b) < n:
            c = self.s.recv(n - len(b))
            if not c:
                raise ConnectionError("closed")
            b += c
        return b

    def call(self, pdu):
        with self.lock:
            self.tid = (self.tid + 1) & 0xFFFF
            self.s.sendall(struct.pack(">HHHB", self.tid, 0, len(pdu) + 1, 1) + pdu)
            _, _, ln, _ = struct.unpack(">HHHB", self._rx(7))
            r = self._rx(ln - 1)
            if r[0] & 0x80:
                raise IOError(f"modbus exception fc={r[0]:#x} code={r[1]}")
            return r

    def write(self, start, regs):
        self.call(struct.pack(">BHHB", 16, start, len(regs), 2 * len(regs)) + struct.pack(f">{len(regs)}H", *regs))

    def read(self, start, n):
        r = self.call(struct.pack(">BHH", 3, start, n))
        return struct.unpack(f">{n}H", r[2:2 + 2 * n])

    def write_main(self):
        with self.lock:
            regs = [self.inb[2 * i] | self.inb[2 * i + 1] << 8 for i in range(IN_REGS)]
        self.write(0, regs)

    def _heartbeat(self):
        while True:
            time.sleep(0.1)
            try:
                self.hb = (self.hb + 1) & 0xFFFF
                struct.pack_into("<H", self.inb, 2, self.hb)
                self.write_main()
            except Exception:
                pass

    def run_enable(self, on=True):
        if on:
            self.inb[0] |= 1 << 3
        else:
            self.inb[0] &= ~(1 << 3) & 0xFF
        self.write_main()

    def part_id(self, text):
        p = text.encode()[:30]
        with self.lock:
            self.inb[16], self.inb[17] = 30, len(p)
            self.inb[18:48] = p.ljust(30, b"\0")
        self.write_main()

    def text_block(self, trigger_id, text, register=501):
        """The strings first (ASCII, high byte first, NUL padded), then TextTriggerId."""
        b = text.encode("ascii")
        b += b"\0" * (len(b) % 2 + 2)
        regs = [b[i] << 8 | b[i + 1] for i in range(0, len(b), 2)]
        self.write(register, regs)
        self.write(TEXT, [trigger_id])

    def cam_trigger(self, cam, trigger_id, on):
        words = list(self.read(CAM_IN, 4))
        words[1 + cam] = trigger_id
        words[0] = (words[0] | 1 << cam) if on else (words[0] & ~(1 << cam))
        self.write(CAM_IN, words)

    def cam_out(self, cam):
        r = self.read(CAM_OUT + 4 * cam, 4)
        status = r[0] & 0xFF      # low byte of the first register is the status byte
        return {"ready": bool(status & 1), "busy": bool(status & 2), "done": bool(status & 4), "ok": bool(status & 8),
                "ng": bool(status & 16), "rejected": bool(status & 32), "echo": r[1], "reason": r[2], "ms": r[3]}

    def wait(self, pred, timeout):
        t = time.perf_counter() + timeout
        while time.perf_counter() < t:
            o = pred()
            if o is not None:
                return o
            time.sleep(0.001)
        return None


def inspect(plc, cam, part):
    """One camera channel's four-phase handshake for part n: (verdict, ms from trigger to Done, reason)."""
    if plc.wait(lambda: True if plc.cam_out(cam)["ready"] else None, 5) is None:
        return "NOT_READY", 0.0, 0
    t0 = time.perf_counter()
    plc.cam_trigger(cam, part, True)
    o = plc.wait(lambda: (lambda x: x if (x["done"] and x["echo"] == part) or x["rejected"] else None)(plc.cam_out(cam)), 5)
    ms = (time.perf_counter() - t0) * 1000
    plc.cam_trigger(cam, part, False)
    plc.wait(lambda: True if not plc.cam_out(cam)["done"] else None, 2)
    if o is None:
        return "TIMEOUT", ms, 0
    if o["rejected"]:
        return "REJECTED", ms, 0
    return ("OK" if o["ok"] and not o["ng"] else "NG"), ms, o["reason"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("port", type=int)
    ap.add_argument("parts", type=int)
    ap.add_argument("truth")
    ap.add_argument("--batch", default="B.NO A2610307", help="the batch line the PLC sends in its text block")
    ap.add_argument("--gap-ms", type=int, default=0, help="time between parts")
    a = ap.parse_args()
    truth = {int(r["part"]): r for r in csv.DictReader(open(a.truth))}
    plc = Plc(a.port)
    time.sleep(0.3)
    plc.run_enable(True)
    rows, t1, t2 = [], [], []
    w = csv.writer(sys.stdout)
    w.writerow(["part", "cam1", "cam2", "decision", "expected", "kind", "cam1_ms", "cam2_ms", "cam1_reason", "cam2_reason"])
    for n in range(1, a.parts + 1):
        plc.text_block(n, a.batch)
        plc.part_id(f"P{n}")
        c1, ms1, r1 = inspect(plc, 0, n)
        c2, ms2, r2 = inspect(plc, 1, n)
        decision = "OK" if c1 == "OK" and c2 == "OK" else "REJECT"
        exp = truth[n]
        w.writerow([n, c1, c2, decision, exp["expected"], exp["kind"], f"{ms1:.0f}", f"{ms2:.0f}", r1, r2])
        sys.stdout.flush()
        rows.append((decision, exp["expected"], exp["kind"]))
        t1.append(ms1)
        t2.append(ms2)
        if a.gap_ms:
            time.sleep(a.gap_ms / 1000)
    plc.run_enable(False)
    passed_bad = [i + 1 for i, r in enumerate(rows) if r[0] == "OK" and r[1] == "NG"]
    rejected_good = [i + 1 for i, r in enumerate(rows) if r[0] == "REJECT" and r[1] == "OK"]
    caught = sum(1 for r in rows if r[0] == "REJECT" and r[1] == "NG")
    print(f"# parts {len(rows)}: bad {sum(r[1] == 'NG' for r in rows)}, caught {caught}, bad passed {len(passed_bad)} {passed_bad}, "
          f"good rejected {len(rejected_good)} {rejected_good}")
    for name, t in (("camera 1", t1), ("camera 2", t2)):
        s = sorted(t)
        print(f"# {name} trigger to Done: median {statistics.median(s):.0f} ms, p95 {s[int(0.95 * (len(s) - 1))]:.0f} ms, max {s[-1]:.0f} ms")


if __name__ == "__main__":
    main()
