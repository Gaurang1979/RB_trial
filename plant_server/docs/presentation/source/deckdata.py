"""Extract real numbers for the deck from the product itself (synthetic demo factory, 24 h)."""
import copy, json, sys
sys.path.insert(0, "/home/claude/production_measurement"); sys.path.insert(0, "/home/claude/production_measurement/tests")
from simulator.config import parse_config
from simulator.engine import Simulation
from simulator.sinks import MemorySink
from backend.store import SqliteStore
from backend.ingest import ingest_events
from backend.topology import topology_from_dict
from backend.derive import derive_all
from backend.kpi import equipment_kpi, line_kpi, downtime_events
from backend.live import LiveEngine
from spi_common.schema import parse_ts, format_ts

raw = json.load(open("/home/claude/production_measurement/configs/demo_factory.json"))
# deck variant: every machine gets random faults so 24 h shows realistic variation; scripted events kept in the first 2 h
for p in raw["plants"]:
    for a in p["areas"]:
        for ln in a["lines"]:
            for e in ln["equipment"]:
                e["mtbf_s"] = e.get("mtbf_s") or 5000; e["mttr_s"] = 420 if "PRESS" in e["id"] or "CNC" in e["id"] else 300
raw["simulation"]["seed"] = 11
DUR = 86400
cfg = parse_config(raw); sink = MemorySink(); sim = Simulation(cfg, sink); sim.run(DUR)
topo = topology_from_dict(raw)
st = SqliteStore(); ingest_events(st, sink.events)
t0 = parse_ts(sim.iso(0)); t1 = t0 + DUR
der = derive_all(st, topo, t0, t1)
out = {"t0": t0, "events": len(sink.events), "duration": DUR}
# hourly per line: good output, target, OEE avg
hourly = {}
for lid, line in topo.lines.items():
    rows = []
    for h in range(24):
        a, b = t0 + h*3600, t0 + (h+1)*3600
        k = line_kpi(st, line, {e.id: [i for i in der[e.id] if i.end > a and i.start < b] for e in line.equipment}, a, b)
        oees = [v["oee"] for v in k["equipment"].values() if v["oee"] is not None]
        rows.append({"h": h, "good": k["good_output"], "target": k["target"], "oee": round(sum(oees)/len(oees), 4) if oees else None})
    hourly[lid] = rows
out["hourly"] = hourly
# 24h KPIs per line + equipment
day = {}
for lid, line in topo.lines.items():
    k = line_kpi(st, line, der, t0, t1)
    day[lid] = {"plant": line.plant_id, "name": line.name, "good": k["good_output"], "target": k["target"], "ach": k["achievement"],
                "bottleneck": k["bottleneck"]["candidate"], "clear": k["bottleneck"]["clear"],
                "equipment": {eid: {x: v[x] for x in ("availability", "performance", "quality", "oee", "count_total", "state_s")} for eid, v in k["equipment"].items()}}
out["day"] = day
# loss seconds by state per plant
loss = {}
for lid, line in topo.lines.items():
    d = {}
    for e in line.equipment:
        for s, n in day[lid]["equipment"][e.id]["state_s"].items(): d[s] = d.get(s, 0) + n
    loss[lid] = d
out["loss"] = loss
# downtime events (>=60s) list for PRESS-02 and all fault durations for AMC view
dt = {eid: downtime_events(ivs, 60) for eid, ivs in der.items()}
out["downtime_counts"] = {eid: {s: sum(1 for r in rows if r["state"] == s) for s in ("FAULT", "BYPASSED", "STARVED", "BLOCKED")} for eid, rows in dt.items()}
out["fault_stats"] = {}
for eid, rows in dt.items():
    f = [r for r in rows if r["state"] == "FAULT"]
    tot = sum(r["duration_s"] for r in f)
    out["fault_stats"][eid] = {"n": len(f), "total_s": tot, "mttr_s": round(tot/len(f)) if f else None, "mtbf_s": round((DUR - tot)/len(f)) if f else None}
# equipment timeline PRESS-02 first 2h + 24h states
out["timeline"] = {eid: [(i.start - t0, i.end - t0, i.state) for i in der[eid] if i.start - t0 < 7200] for eid in ("PRESS-01", "PRESS-02", "ASSEMBLY-01", "TEST-01")}
# raw signals for the bypass/stuck illustration (PRESS-02): cycle times and temperature
cyc = st.events(["cycle"], equipment_id="PRESS-02", t_min=t0, t_max=t0 + 7200)
out["press02_cycles"] = [e["ts"] - t0 for e in cyc if e["ts"] >= t0]
out["press02_temp"] = [(e["ts"] - t0, e["payload"]["value"]) for e in st.events(["sensor"], subject="PRESS-02.TEMP", t_min=t0, t_max=t0 + 7200) if e["ts"] >= t0]
out["press02_vib"] = [(e["ts"] - t0, e["payload"]["value"]) for e in st.events(["sensor"], subject="PRESS-02.VIB", t_min=t0, t_max=t0 + 7200) if e["ts"] >= t0]
out["queues"] = {q: [(e["ts"] - t0, e["payload"]["level"]) for e in st.events(["queue"], subject=q, t_min=t0, t_max=t0 + 7200) if e["ts"] >= t0] for q in ("QUEUE-A", "QUEUE-B", "QUEUE-C")}
# live snapshot during PRESS-01 fault (data time 2100 s) for the line dashboard
live = LiveEngine(SqliteStore(), topo)
buf, edge = [], None
for e in sink.events:
    if parse_ts(e["ts"]) - t0 > 2160: break
    buf.append(e)
live.ingest(buf); live.refresh()
out["snapshot_2160"] = live.line_status("LINE-VAD-01")
out["snapshot_pune"] = None
json.dump(out, open("/home/claude/deck/deckdata.json", "w"))
print("events", out["events"]); 
for lid, d in day.items(): print(lid, "good", d["good"], "target", d["target"], "ach", d["ach"], "bn", d["bottleneck"], d["clear"])
print("faults", {k: v for k, v in out["fault_stats"].items()})
print("snapshot states", {e["equipment_id"]: (e["state"], e["duration_s"]) for e in out["snapshot_2160"]["equipment"]})
print("attention", [a["title"] for a in out["snapshot_2160"]["attention"]])
