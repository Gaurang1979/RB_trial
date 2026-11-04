import json
from dash import *

T2 = 7200
def clock(t): return f"{(6 + t // 3600) % 24:02d}:{t % 3600 // 60:02d}"


def equip_dash():
    s = Svg(1600, 900); s.header("Equipment · PRESS-02 · Press 02", "Line 01 · Assembly · Vadodara Plant  ·  first 2 hours of the day")
    e = D["day"]["LINE-VAD-01"]["equipment"]["PRESS-02"]
    for i, (lab, key) in enumerate((("Availability", "availability"), ("Performance", "performance"), ("Quality", "quality"), ("OEE", "oee"))):
        v = e[key]; s.kpi(24 + i * 396, 78, 380, 118, lab + " (24 h)", f"{v * 100:.0f}%", "calculated", "#22c55e" if v >= .85 else "#f59e0b")
    s.panel(24, 216, 1552, 150, "State timeline — detected from machine signals", "06:00 – 08:00")
    tl = D["timeline"]["PRESS-02"]
    timeline(s, 48, 262, 1504, 56, tl, T2)
    axis_time(s, 48, 322, 1504, T2, 900)
    lx = 900
    for st in ("PRODUCING", "STARVED", "BLOCKED", "FAULT", "BYPASSED"):
        s.rect(lx, 34 + 216 - 34 + 0, 0, 0, "#000"); lx += 0
    x = 690
    for st in ("PRODUCING", "STARVED", "BLOCKED", "FAULT", "BYPASSED"):
        s.rect(x, 230 - 4, 11, 11, ST[st], rx=5); s.text(x + 16, 240, LBL[st].title() if st != "BYPASSED" else "Bypass suspected", 12, MUT); x += 16 + len(LBL[st]) * 7.2 + 20
    # cycle time chart
    s.panel(24, 386, 760, 250, "Cycle time (s) vs 30 s standard")
    cy = D["press02_cycles"]; pts = [(cy[i], cy[i] - cy[i - 1]) for i in range(1, len(cy)) if cy[i] - cy[i - 1] < 500]
    gx, gy, gw, gh = 60, 424, 700, 170
    s.line(gx, gy + gh - 30 / 60 * gh, gx + gw, gy + gh - 30 / 60 * gh, "#fbbf24", 1.5, "6 4"); s.text(gx + gw, gy + gh - 30 / 60 * gh - 6, "standard 30 s", 11, "#fbbf24", 600, "end")
    for t, v in pts:
        s.rect(gx + t / T2 * gw - 1, gy + gh - min(v, 60) / 60 * gh, 2.5, 2.5, ACC, rx=1)
    axis_time(s, gx, gy + gh + 2, gw, T2, 1800)
    # temperature
    s.panel(804, 386, 772, 250, "Temperature sensor (°C)", "PRESS-02.TEMP")
    tp = D["press02_temp"]; vs = [v for _, v in tp]; lo, hi = min(vs) - 2, max(vs) + 2
    gx, gy, gw, gh = 840, 424, 712, 170
    pts = [(gx + t / T2 * gw, gy + gh - (v - lo) / (hi - lo) * gh) for t, v in tp[::2]]
    s.poly(pts, "#38bdf8", 1.8)
    sx0, sx1 = gx + 2400 / T2 * gw, gx + 6000 / T2 * gw
    s.rect(sx0, gy, sx1 - sx0, gh, "#a855f7", op=0.12); s.text((sx0 + sx1) / 2, gy + 18, "constant reading for 60 min → sensor malfunction suspected", 12, "#c4b5fd", 600, "middle")
    axis_time(s, gx, gy + gh + 2, gw, T2, 1800)
    # downtime table
    s.panel(24, 656, 1552, 230, "Downtime and loss events (≥ 60 s)", "automatic · cause not guessed")
    hdr = ["Start", "Duration", "State", "Category", "Evidence"]; xs = [48, 200, 340, 560, 880]
    for x, t in zip(xs, hdr): s.text(x, 700, t.upper(), 12, MUT, 700)
    s.line(48, 710, 1552, 710)
    ev = [(a, b, st) for a, b, st in tl if st in ("FAULT", "BYPASSED", "STARVED", "BLOCKED") and b - a >= 60][:4]
    cat = {"FAULT": "Unknown (fault signal)", "BYPASSED": "Production signal unavailable", "STARVED": "Upstream issue", "BLOCKED": "Downstream issue"}
    evid = {"FAULT": "fault=1", "BYPASSED": "running=1, no cycles for > 2× standard cycle", "STARVED": "running=0, upstream queue empty", "BLOCKED": "running=0, downstream queue full"}
    for i, (a, b, st) in enumerate(ev):
        yy = 742 + i * 34
        s.text(48, yy, clock(a), 15, TXT, 600); s.text(200, yy, f"{(b - a) // 60} min", 15, TXT); s.rect(340, yy - 13, 10, 10, ST[st], rx=5); s.text(358, yy, LBL[st].title() if st != "BYPASSED" else "Bypass suspected", 15, ST[st], 700)
        s.text(560, yy, cat[st], 15, TXT); s.text(880, yy, evid[st], 14, MUT)
    s.save("dash_equipment")


def truth():
    s = Svg(1600, 760); s.header("Digital truth · machine ON is not machine producing", "PRESS-02 · real signals from the simulated factory")
    tl = D["timeline"]["PRESS-02"]; cyc = D["press02_cycles"]
    # left: bypass window 5400..7500
    a0, a1 = 5400, 7200
    s.panel(24, 78, 780, 660, "Powered on, running signal active — but nothing is produced")
    gx, gw = 60, 720
    rows = [("Machine ON", 150), ("Running signal", 240), ("Cycles completed", 330), ("Detected state", 470)]
    for lab, yy in rows: s.text(gx, yy - 14, lab.upper(), 12, MUT, 700)
    s.rect(gx, 150, gw, 34, "#14532d", rx=4); s.text(gx + 10, 173, "ON", 14, "#86efac", 700)
    s.rect(gx, 240, gw, 34, "#14532d", rx=4); s.text(gx + 10, 263, "RUNNING = 1  (whole window)", 14, "#86efac", 700)
    for t in cyc:
        if a0 <= t < a1: s.line(gx + (t - a0) / (a1 - a0) * gw, 330, gx + (t - a0) / (a1 - a0) * gw, 372, "#38bdf8", 2)
    bx0, bx1 = gx + (6000 - a0) / (a1 - a0) * gw, gx + (6900 - a0) / (a1 - a0) * gw
    s.rect(bx0, 322, bx1 - bx0, 58, "#a855f7", op=0.15); s.text((bx0 + bx1) / 2, 356, "no cycles", 14, "#d8b4fe", 700, "middle")
    timeline(s, gx, 470, gw, 44, tl, a1, tmin=a0)
    s.text((bx0 + bx1) / 2, 540, "BYPASS SUSPECTED", 14, "#c4b5fd", 800, "middle")
    axis_time(s, gx, 560, gw, a1, 300, tmin=a0)
    s.text(gx, 640, "Signal inconsistency: the machine reports running while", 15, TXT, 600); s.text(gx, 664, "output stays at zero. Reported neutrally, with evidence,", 15, TXT, 600); s.text(gx, 688, "duration and confidence. Never as an accusation.", 15, TXT, 600)
    # right: sensor
    s.panel(824, 78, 752, 660, "Sensor malfunction — a constant value while the process changes")
    tp, vb = D["press02_temp"], D["press02_vib"]
    gx, gw = 860, 690
    for (lab, ser, top, col) in (("PRESS-02.TEMP  ·  °C", tp, 150, "#38bdf8"), ("PRESS-02.VIB  ·  mm/s", vb, 330, "#22c55e")):
        vs = [v for _, v in ser]; lo, hi = min(vs) - 0.5, max(vs) + 0.5
        s.text(gx, top - 14, lab.upper(), 12, MUT, 700)
        s.poly([(gx + t / T2 * gw, top + 130 - (v - lo) / (hi - lo) * 130) for t, v in ser[::2]], col, 1.6)
    sx0, sx1 = gx + 2400 / T2 * gw, gx + 6000 / T2 * gw
    s.rect(sx0, 150, sx1 - sx0, 130, "#a855f7", op=0.14)
    s.text((sx0 + sx1) / 2, 176, "flat: 60 min", 14, "#d8b4fe", 700, "middle")
    s.text(gx, 500, "DETECTED STATE — PRESS-02", 12, MUT, 700)
    timeline(s, gx, 512, gw, 40, tl, T2)
    axis_time(s, gx, 562, gw, T2, 900)
    s.text(gx, 640, "The temperature never moves while the machine works, stops", 15, TXT, 600); s.text(gx, 664, "and starts; vibration next to it does. The sensor is flagged", 15, TXT, 600); s.text(gx, 688, "unhealthy: no fake data, no fake production.", 15, TXT, 600)
    s.save("dash_truth")


def waterfall():
    s = Svg(1500, 700, bg="#0b1220")
    e = D["day"]["LINE-VAD-01"]["equipment"]["PRESS-02"]; st = e["state_s"]; W = 86400
    pr = st.get("PRODUCING", 0) / W
    steps = [("Planned time", 1.0, "tot"), ("Fault", -st.get("FAULT", 0) / W, "loss"), ("Bypass", -st.get("BYPASSED", 0) / W, "loss"), ("Starved", -st.get("STARVED", 0) / W, "loss"),
             ("Blocked", -st.get("BLOCKED", 0) / W, "loss"), ("Availability", pr, "sub"), ("Speed loss", -pr * (1 - e["performance"]), "loss"), ("Quality loss", -pr * e["performance"] * (1 - e["quality"]), "loss"), ("OEE", e["oee"], "tot")]
    s.text(40, 46, "PRESS-02 · 24 h · from planned time to OEE (calculated from detected states)", 18, TXT, 700)
    gx, gy, gw, gh = 70, 90, 1380, 500
    for i in range(6):
        yy = gy + gh - gh * i / 5; s.line(gx, yy, gx + gw, yy); s.text(gx - 10, yy + 4, f"{i * 20}%", 12, MUT, 500, "end")
    n = len(steps); bw = gw / n; run = 1.0
    for i, (lab, v, kind) in enumerate(steps):
        x = gx + i * bw + 14
        if kind in ("tot", "sub"):
            top, bot, col = v, 0, (ACC if kind == "tot" else "#0ea5e9"); run = v
        else:
            top, bot, col = run, run + v, {"Fault": "#ef4444", "Bypass": "#a855f7", "Starved": "#f59e0b", "Blocked": "#fb923c"}.get(lab, "#64748b"); run += v
        y1, y2 = gy + gh - top * gh, gy + gh - bot * gh
        s.rect(x, y1, bw - 28, max(2, y2 - y1), col, rx=4)
        s.text(x + (bw - 28) / 2, y1 - 8, (f"{v * 100:.0f}%" if kind != "loss" else f"−{abs(v) * 100:.1f}%"), 15, TXT, 700, "middle")
        s.text(x + (bw - 28) / 2, gy + gh + 26, lab, 14, MUT, 600, "middle")
    s.save("oee_waterfall", 1.0)


def cause_chain():
    s = Svg(1600, 640); s.header("How one stop spreads down the line", "LINE-VAD-01 · data from 06:25 to 06:50 · every state detected from raw signals")
    a0, a1 = 1500, 3000
    names = ["PRESS-01", "PRESS-02", "ASSEMBLY-01", "TEST-01"]
    for i, n in enumerate(names):
        y = 100 + i * 66
        s.text(24, y + 34, n, 15, TXT, 700)
        timeline(s, 170, y + 8, 1380, 40, D["timeline"][n], a1, tmin=a0)
    for i, (q, col) in enumerate((("QUEUE-A", "#38bdf8"), ("QUEUE-B", "#a78bfa"), ("QUEUE-C", "#34d399"))):
        ser = [(t, v) for t, v in D["queues"][q] if a0 <= t <= a1]
        y = 386 + i * 62
        s.text(24, y + 32, q, 15, TXT, 700)
        s.line(170, y + 52, 1550, y + 52, LINE)
        s.poly([(170 + (t - a0) / (a1 - a0) * 1380, y + 52 - v / 10 * 46) for t, v in ser], col, 2)
    axis_time(s, 170, 580, 1380, a1, 300, tmin=a0)
    fx = 170 + (1800 - a0) / (a1 - a0) * 1380
    s.line(fx, 90, fx, 574, "#ef4444", 1.5, "5 4"); s.pill(fx - 92, 70, "06:30 PRESS-01 FAULT", "#ef4444", 11, "#fff")
    s.save("cause_chain")


def mobile():
    s = Svg(1180, 900, bg="#0e1730")
    def phone(x, title):
        s.rect(x, 40, 380, 820, "#05080f", "#2a3654", 44, sw=3); s.rect(x + 14, 54, 352, 792, BG, None, 34)
        s.rect(x + 140, 66, 100, 22, "#05080f", rx=11)
        s.text(x + 190, 120, title, 13, MUT, 700, "middle")
    phone(140, "LOCK SCREEN")
    s.text(190, 170, "06:36", 64, TXT, 700); 
    s.rect(160, 210, 340, 210, "#151f38", "#2b3a5f", 18); s.text(180, 240, "SUNDARAM SPI  ·  now", 12, MUT, 700)
    s.text(180, 272, "Line 01 stopped", 20, TXT, 700); s.text(180, 300, "Equipment: PRESS-01", 15, "#cbd5e1"); s.text(180, 324, "Duration: 6 minutes", 15, "#cbd5e1")
    s.text(180, 348, "Reason: fault signal active", 15, "#cbd5e1"); s.text(180, 372, "Impact: est. 18 units of production loss", 15, "#fca5a5", 700)
    s.pill(180, 388, "HIGH", "#ef4444", 11, "#fff")
    phone(660, "ALERT DETAIL")
    s.text(690, 132, "Line 01 · Assembly", 14, MUT, 600); s.text(690, 172, "PRESS-01 stopped", 26, TXT, 700)
    s.rect(680, 194, 340, 6, "#ef4444", rx=3)
    for i, (k, v) in enumerate((("State", "FAULT (detected)"), ("Since", "06:30"), ("Effect", "Press 02, Assembly 01 starved"), ("Impact", "up to 18 units (calculated)"), ("Evidence", "fault = 1, running = 0"))):
        s.text(690, 236 + i * 46, k.upper(), 11, MUT, 700); s.text(690, 258 + i * 46, v, 16, TXT, 600)
    for i, (t, col) in enumerate((("View line", ACC), ("Acknowledge", "#22c55e"), ("Call maintenance", "#f59e0b"), ("View history", "#64748b"))):
        s.rect(690, 490 + i * 62, 320, 48, col, rx=10, op=0.95); s.text(850, 521 + i * 62, t, 17, "#04121f" if i < 3 else "#fff", 700, "middle")
    s.text(700, 760, "Mockup — mobile alerts are on the roadmap", 12, MUT)
    # third: line summary
    phone(1180 - 0, "LINE VIEW") if False else None
    s.save("dash_mobile", 1.0)


def amc():
    s = Svg(1600, 800); s.header("AMC / SLA intelligence · equipment provider performance evidence", "Vadodara Plant · Pune Plant · last 24 hours  ·  factual evidence for reviews and renewals, not vendor ratings")
    fs = D["fault_stats"]; sup = {"PRESS-01": "OEM A", "PRESS-02": "OEM A", "ASSEMBLY-01": "OEM B", "TEST-01": "OEM C", "PRESS-03": "OEM A", "ASSEMBLY-02": "OEM B", "TEST-02": "OEM C", "CNC-01": "OEM D", "CNC-02": "OEM D", "INSPECT-01": "OEM C"}
    agg = {}
    for eid, v in fs.items():
        a = agg.setdefault(sup[eid], {"n": 0, "tot": 0}); a["n"] += v["n"]; a["tot"] += v["total_s"]
    sla_mttr = 420
    hdr = ["Provider", "Breakdowns", "Downtime", "MTTR", "SLA target", "Compliance"]; xs = [48, 300, 480, 640, 800, 980]
    s.panel(24, 78, 1100, 330, "Repair performance by provider", "SLA target is a configurable input")
    for x, t in zip(xs, hdr): s.text(x, 128, t.upper(), 12, MUT, 700)
    s.line(48, 138, 1100, 138)
    for i, (p, a) in enumerate(sorted(agg.items())):
        y = 180 + i * 56; mttr = a["tot"] / a["n"]; ok = mttr <= sla_mttr
        s.text(48, y, p, 17, TXT, 700); s.text(300, y, str(a["n"]), 17); s.text(480, y, f"{a['tot'] / 3600:.1f} h", 17); s.text(640, y, f"{mttr / 60:.1f} min", 17)
        s.text(800, y, f"≤ {sla_mttr // 60} min", 17, MUT); s.pill(980, y - 18, "WITHIN SLA" if ok else "ABOVE SLA", "#166534" if ok else "#7f1d1d", 12, "#fff")
        s.line(48, y + 20, 1100, y + 20, LINE, 1, op=0.6)
    s.panel(1144, 78, 432, 330, "MTTR vs SLA", "minutes")
    for i, (p, a) in enumerate(sorted(agg.items())):
        y = 130 + i * 62; mttr = a["tot"] / a["n"] / 60
        s.text(1164, y + 16, p, 14, TXT, 700); s.rect(1240, y, 300, 22, "#1b2740", rx=4); s.rect(1240, y, min(300, 300 * mttr / 12), 22, "#22c55e" if mttr <= 7 else "#ef4444", rx=4)
        s.text(1246 + min(280, 300 * mttr / 12), y + 17, f"{mttr:.1f}", 13, "#04121f", 700)
    x7 = 1240 + 300 * 7 / 12; s.line(x7, 118, x7, 130 + 4 * 62, "#fbbf24", 1.5, "5 4"); s.text(x7, 112, "SLA 7 min", 11, "#fbbf24", 700, "middle")
    s.panel(24, 428, 1552, 340, "Equipment reliability", "MTBF / MTTR from detected fault intervals")
    hdr = ["Equipment", "Provider", "Breakdowns", "Total downtime", "MTTR", "MTBF"]; xs = [48, 300, 500, 720, 960, 1160]
    for x, t in zip(xs, hdr): s.text(x, 478, t.upper(), 12, MUT, 700)
    s.line(48, 488, 1552, 488)
    for i, eid in enumerate(("PRESS-03", "PRESS-01", "CNC-02", "PRESS-02", "ASSEMBLY-01")):
        v = fs[eid]; y = 524 + i * 46
        s.text(48, y, eid, 16, TXT, 700); s.text(300, y, sup[eid], 16); s.text(500, y, str(v["n"]), 16); s.text(720, y, f"{v['total_s'] / 3600:.1f} h", 16); s.text(960, y, f"{v['mttr_s'] / 60:.1f} min", 16); s.text(1160, y, f"{v['mtbf_s'] / 3600:.1f} h", 16)
        s.line(48, y + 16, 1552, y + 16, LINE, 1, op=0.6)
    s.save("dash_amc")


def architecture():
    s = Svg(1664, 640, bg="#0d162b")
    boxes = [("MACHINES & SENSORS", "Digital I/O · Modbus · OPC-UA\nPLC · counters · temperature\nvibration · queue levels", "#38bdf8"), ("EDGE GATEWAY", "Raspberry Pi / industrial PC\nlocal buffer · timestamps\nstore-and-forward · health", "#22c55e"),
             ("MESSAGE BROKER", "MQTT (QoS 1)\ndurable sessions\nno loss when backend is down", "#f59e0b"), ("PRODUCTION INTELLIGENCE", "ingestion · de-duplication\nstate derivation · KPI engine\nbottleneck · downtime", "#a78bfa"),
             ("DATA STORE", "PostgreSQL\nmaster data + events\nconfigurable retention", "#fb923c"), ("API + APPS", "REST · WebSocket\nweb dashboard · mobile\nreports · alerts", "#f472b6")]
    w, gap = 232, 36
    for i, (t, sub, col) in enumerate(boxes):
        x = 46 + i * (w + gap)
        s.rect(x, 170, w, 300, "#111a2e", col, 14, sw=2); s.rect(x, 170, w, 8, col, rx=4)
        s.text(x + w / 2, 218, t, 15, TXT, 700, "middle")
        for j, l in enumerate(sub.split("\n")): s.text(x + w / 2, 262 + j * 28, l, 14, MUT, 500, "middle")
        s.text(x + w / 2, 430, f"0{i + 1}", 34, col, 700, "middle", 0.9)
        if i < 5:
            s.line(x + w + 4, 320, x + w + gap - 6, 320, "#5b6b91", 2); s.poly([(x + w + gap - 14, 312), (x + w + gap - 5, 320), (x + w + gap - 14, 328)], "#5b6b91", 2)
    s.rect(24, 60, 1616, 70, "#0e1730", LINE, 12); s.text(48, 104, "OFFLINE-FIRST: machines keep producing data when the network is down; the gateway buffers locally and synchronises in order, without duplicates.", 17, "#bae6fd", 600)
    s.rect(24, 510, 1616, 96, "#0e1730", LINE, 12)
    s.text(48, 548, "SECURITY BY DESIGN", 13, "#f59e0b", 700); s.text(48, 582, "TLS in transit  ·  gateway and device authentication  ·  role-based access  ·  plant/area/line isolation  ·  audit trail  ·  PLCs never exposed to the internet", 17, TXT, 500)
    s.save("arch_flow", 1.0)


if __name__ == "__main__":
    equip_dash(); truth(); waterfall(); cause_chain(); mobile(); amc(); architecture()
    from PIL import Image
    names = ["dash_equipment", "dash_truth", "oee_waterfall", "cause_chain", "dash_mobile", "dash_amc"]
    ims = [Image.open(f"/home/claude/deck/img/{n}.png").convert("RGB") for n in names]
    sheet = Image.new("RGB", (1800, 1500))
    for i, im in enumerate(ims):
        im = im.resize((900, int(900 * im.height / im.width))); sheet.paste(im, ((i % 2) * 900, (i // 2) * 500))
    sheet.save("/tmp/sheet2.png")
    Image.open("/home/claude/deck/img/arch_flow.png").convert("RGB").resize((1000, 385)).save("/tmp/arch_s.png")
