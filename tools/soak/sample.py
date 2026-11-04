"""Per-minute record of vs-runtime during a soak (tools/soak/README.md).

sample.py <pid> <dataFolder> <plc.csv> <counters.csv> <out.csv> <status command...>

Every 60 s until the process ends (or the file <out.csv>.stop appears) one row: the process's
working set, threads, open file descriptors; the managed heap and GC counts (from the
dotnet-counters CSV that runs beside it); results.db and the NG image store on disk; the
resource guard's pressure and the runtime's alarms (from the status command); and the parts the
simulated PLC finished in that minute, with trigger to Done p50 / p99 and the missed, timed-out
and late ones.
"""
import csv, os, statistics, subprocess, sys, time

PERIOD_MS = 60000 / 225  # one part every 267 ms


def ps(pid, field):
    out = subprocess.run(["ps", "-o", f"{field}=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
    return out


def threads(pid):
    out = subprocess.run(["ps", "-M", "-p", str(pid)], capture_output=True, text=True).stdout.splitlines()
    return max(0, len(out) - 1)


def fds(pid):
    out = subprocess.run(["lsof", "-n", "-P", "-p", str(pid)], capture_output=True, text=True).stdout.splitlines()
    return max(0, len(out) - 1)


def du(path):
    total = 0
    for root, _, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def count_files(path):
    return sum(len(files) for _, _, files in os.walk(path))


def db_bytes(data):
    return sum(os.path.getsize(os.path.join(data, f)) for f in os.listdir(data) if f.startswith("results.db"))


def counters(path):
    """From the dotnet-counters CSV (Timestamp,Provider,Counter Name,Type,Value): the latest value of
    each gauge, and each rate counter ("... / 10 sec") summed over the run, so GC counts are totals."""
    last, total = {}, {}
    try:
        with open(path, newline="") as f:
            for row in csv.reader(f):
                if len(row) < 5 or row[0] == "Timestamp":
                    continue
                try:
                    v = float(row[4])
                except ValueError:
                    continue
                last[row[2]] = v
                if " sec)" in row[2]:
                    total[row[2]] = total.get(row[2], 0.0) + v
    except FileNotFoundError:
        pass
    return last, total


def heap_bytes(last):
    """Managed heap after the last GC: all generations, the large and pinned object heaps."""
    gens = [v for k, v in last.items() if k.startswith("dotnet.gc.last_collection.heap.size")]
    return sum(gens) if gens else None


def gc_count(total, gen):
    for k, v in total.items():
        if k.startswith("dotnet.gc.collections") and f"gen{gen}]" in k:
            return int(v)
    return ""


def status(cmd):
    out = subprocess.run(cmd, capture_output=True, text=True).stdout
    lines = out.splitlines()
    kv = dict(p.split("=", 1) for p in lines[0].split() if "=" in p) if lines else {}
    kv["alarm_text"] = " | ".join(l.strip() for l in lines[1:])
    return kv


def plc_minute(path, t0, t1):
    ms, n, missed, timeout, ng = [], 0, 0, 0, 0
    try:
        with open(path, newline="") as f:
            for r in csv.DictReader(f):
                try:
                    t = float(r["epoch"])
                except (KeyError, ValueError):
                    continue
                if not t0 <= t < t1:
                    continue
                v = r["verdict"]
                if v == "MISSED":
                    missed += 1
                    continue
                n += 1
                if v == "TIMEOUT":
                    timeout += 1
                else:
                    ms.append(float(r["trigger_to_done_ms"]))
                    ng += v == "NG"
    except FileNotFoundError:
        pass
    ms.sort()
    p = lambda q: ms[min(len(ms) - 1, int(q * len(ms)))] if ms else ""
    return dict(parts=n, ng=ng, missed=missed, timeout=timeout, late=sum(1 for v in ms if v > PERIOD_MS),
                p50=round(statistics.median(ms), 1) if ms else "", p99=p(0.99), max=ms[-1] if ms else "")


def main():
    pid, data, plc, ctr, out = int(sys.argv[1]), sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
    cmd = sys.argv[6:]
    cols = ["minute", "time", "working_set_mb", "managed_heap_mb", "gc_committed_mb", "gen0", "gen1", "gen2",
            "threads", "fds", "results_db_mb", "ng_store_mb", "ng_files", "pressure", "mem_used_pct",
            "restart_recommended", "frames_missed", "alarms", "parts", "ng", "missed", "timeout", "late",
            "p50_ms", "p99_ms", "max_ms", "load1", "alarm_text"]
    start = time.time()
    with open(out, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(cols)
        minute = 0
        while True:
            nxt = start + 60 * (minute + 1)
            while time.time() < nxt:
                if os.path.exists(out + ".stop") or not ps(pid, "pid"):
                    return
                time.sleep(1)
            minute += 1
            last, total = counters(ctr)
            s = status(cmd)
            m = plc_minute(plc, nxt - 60, nxt)
            heap = heap_bytes(last)
            commit = next((v for k, v in last.items() if k.startswith("dotnet.gc.last_collection.memory.committed_size")), None)
            heap_mb = round(heap / 2**20, 1) if heap is not None else ""
            commit_mb = round(commit / 2**20, 1) if commit is not None else ""
            w.writerow([minute, time.strftime("%H:%M:%S"), round(int(ps(pid, "rss") or 0) / 1024, 1), heap_mb, commit_mb,
                        gc_count(total, 0), gc_count(total, 1), gc_count(total, 2), threads(pid), fds(pid),
                        round(db_bytes(data) / 2**20, 2), round(du(os.path.join(data, "ng-images")) / 2**20, 1),
                        count_files(os.path.join(data, "ng-images")), s.get("pressure", ""), s.get("mem_used_pct", ""),
                        s.get("restart_recommended", ""), s.get("frames_missed", ""), s.get("alarms", ""),
                        m["parts"], m["ng"], m["missed"], m["timeout"], m["late"], m["p50"], m["p99"], m["max"],
                        round(os.getloadavg()[0], 2), s["alarm_text"]])
            f.flush()


if __name__ == "__main__":
    main()
