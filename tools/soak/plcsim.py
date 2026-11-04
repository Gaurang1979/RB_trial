"""Simulated PLC for the camera channels (PLC-INTERFACE 3.5) at a fixed part rate.

One scan loop, like the PLC: every scan it reads the camera channel outputs, runs one CamStation
per camera (as plc/delta-as/VisionLine.st: a part that arrives while the station is not idle or
the channel is not Ready is NOT inspected = NG; no Done with the right echo within 1 s = timeout NG),
and writes the trigger word and ids. Parts arrive every 60/rate s with uniform jitter; both
cameras on one sensor (worst case: they inspect at the same time).

plcsim.py <port> <rate/min> <seconds> <out.csv> [jitter_ms] [cameras]

Each part is written to out.csv as it finishes (epoch time first), so tools/soak/sample.py can bin
trigger-to-Done per minute while the soak runs. From PR #148 (scratch soak225.py), kept here.
"""
import csv, random, socket, struct, sys, threading, time, statistics

IN_REGS, CAM_IN, CAM_OUT, MAIN_OUT = 32, 32, 228, 100


class Link:
    def __init__(self, port):
        self.s = socket.create_connection(("127.0.0.1", port))
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.lock = threading.Lock()
        self.tid = 0

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


class Station:
    """CamStation of VisionLine.st, camera delay 0."""
    def __init__(self, c):
        self.c, self.st, self.trigger, self.tid = c, 0, False, 0
        self.t_trig = 0.0

    def scan(self, start, part_id, status, echo, now, log):
        ready, done, ok = bool(status & 1), bool(status & 4), bool(status & 8)
        if start:
            if self.st == 0 and ready:
                self.st, self.tid = 1, part_id
            else:
                log.append((self.c, part_id, "MISSED", -1, -1, f"st={self.st} ready={int(ready)}"))
        if self.st == 1:
            self.trigger, self.st, self.t_trig = True, 2, now
        elif self.st == 2:
            if done and echo == self.tid:
                self.trigger, self.st = False, 3
                return ("OK" if ok else "NG", (now - self.t_trig) * 1000)
            if now - self.t_trig >= 1.0:
                self.trigger, self.st = False, 3
                return ("TIMEOUT", (now - self.t_trig) * 1000)
        elif self.st == 3 and not done:
            self.st = 0
        return None


def main():
    port, rate, seconds, out = int(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3]), sys.argv[4]
    jitter = float(sys.argv[5]) if len(sys.argv) > 5 else 20.0
    ncam = int(sys.argv[6]) if len(sys.argv) > 6 else 2
    L = Link(port)
    inb = [0] * IN_REGS
    inb[0] = 1 << 3          # RunEnable
    hb = [0]
    stop = [False]

    def heartbeat():
        while not stop[0]:
            time.sleep(0.1)
            hb[0] = (hb[0] + 1) & 0xFFFF
            inb[1] = hb[0]
            try: L.write(0, inb)
            except Exception: pass
    threading.Thread(target=heartbeat, daemon=True).start()
    time.sleep(0.6)
    st = L.read(MAIN_OUT, 4)
    if st[0] >> 5 & 1:     # Error: reset the alarm
        inb[0] |= 1 << 2; L.write(0, inb); time.sleep(0.05); inb[0] &= ~(1 << 2); L.write(0, inb); time.sleep(0.2)
    stations = [Station(c) for c in range(ncam)]
    period = 60.0 / rate
    t0 = time.perf_counter() + 0.5
    k, next_part = 0, t0
    rows, log = [], []
    nscan, last_scan, max_gap = 0, None, 0.0  # running scan stats (a 30-minute list would hold millions)
    f = open(out, "w", newline="")
    w = csv.writer(f)
    w.writerow(["epoch", "camera", "part", "verdict", "trigger_to_done_ms", "vision_cycle_ms", "ng_reason_or_note"])
    nlog = 0
    cam_words = [0] * 8
    while True:
        now = time.perf_counter()
        if now - t0 > seconds and all(s.st == 0 for s in stations):
            break
        regs = L.read(CAM_OUT, 4 * ncam)
        start = False
        if now >= next_part and now - t0 <= seconds:
            k += 1
            start = True
            next_part = t0 + k * period + random.uniform(-jitter, jitter) / 1000.0
        part_id = (k % 60000) + 1
        for s in stations:
            status, echo, ngr, cyc = regs[4 * s.c], regs[4 * s.c + 1], regs[4 * s.c + 2], regs[4 * s.c + 3]
            r = s.scan(start, part_id, status & 0xFF, echo, now, log)
            if r:
                verdict, ms = r
                row = (s.c, s.tid, verdict, round(ms, 1), cyc if verdict != "TIMEOUT" else -1, ngr)
                rows.append(row)
                w.writerow((f"{time.time():.3f}",) + row)
        while nlog < len(log):
            w.writerow((f"{time.time():.3f}",) + log[nlog]); nlog += 1
        if start: f.flush()
        word = sum(1 << s.c for s in stations if s.trigger)
        new = [word] + [s.tid for s in stations] + [0] * (7 - ncam)
        if new != cam_words:
            L.write(CAM_IN, new)
            cam_words = new
            # a station that set its trigger this scan: its clock starts at the write
            for s in stations:
                if s.trigger and s.st == 2 and s.t_trig == now: s.t_trig = time.perf_counter()
        if last_scan is not None: max_gap = max(max_gap, now - last_scan)
        nscan, last_scan = nscan + 1, now
        time.sleep(0.001)
    stop[0] = True
    f.close()
    rows += log
    print(f"parts {k}, scans {nscan}, scan mean {seconds * 1000 / max(1, nscan):.1f} ms, max {max_gap * 1000:.1f} ms")
    for c in range(ncam):
        done = [r for r in rows if r[0] == c and r[2] in ("OK", "NG")]
        ms = sorted(r[3] for r in done)
        n = lambda v: sum(1 for r in rows if r[0] == c and r[2] == v)
        p99 = ms[min(len(ms) - 1, int(0.99 * len(ms)))] if ms else -1
        print(f"camera {c + 1}: results {len(done)} (OK {n('OK')}, NG {n('NG')}), missed {n('MISSED')}, timeout {n('TIMEOUT')}, "
              f"trigger->Done median {statistics.median(ms) if ms else -1:.1f} p99 {p99:.1f} max {ms[-1] if ms else -1:.1f} ms, "
              f"late (> period {period*1000:.0f} ms) {sum(1 for v in ms if v > period*1000)}")


if __name__ == "__main__":
    main()
