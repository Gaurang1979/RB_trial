import json, math, subprocess
D = json.load(open("/home/claude/deck/deckdata.json"))
SNAP = D["snapshot_2160"]
FONT = "IBM Plex Sans Var, IBM Plex Sans, DejaVu Sans, sans-serif"
BG, PANEL, LINE, TXT, MUT = "#0b1220", "#111a2e", "#1f2b46", "#e5e9f2", "#8fa0c0"
ST = {"PRODUCING": "#22c55e", "STARVED": "#f59e0b", "BLOCKED": "#fb923c", "FAULT": "#ef4444", "BYPASSED": "#a855f7", "IDLE": "#94a3b8", "COMMUNICATION_ERROR": "#38bdf8", "UNKNOWN": "#475569"}
LBL = {"PRODUCING": "PRODUCING", "STARVED": "STARVED", "BLOCKED": "BLOCKED", "FAULT": "FAULT", "BYPASSED": "BYPASS SUSPECTED", "IDLE": "IDLE"}
ACC = "#38bdf8"


class Svg:
    def __init__(s, w, h, bg=BG):
        s.w, s.h, s.p = w, h, [f'<rect width="{w}" height="{h}" fill="{bg}"/>']

    def rect(s, x, y, w, h, fill, stroke=None, rx=0, op=1, sw=1):
        st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
        s.p.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{max(w, 0):.1f}" height="{max(h, 0):.1f}" rx="{rx}" fill="{fill}" opacity="{op}"{st}/>')

    def text(s, x, y, t, size=14, fill=TXT, weight=500, anchor="start", op=1):
        t = str(t).replace("&", "&amp;").replace("<", "&lt;")
        s.p.append(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{FONT}" font-size="{size}" font-weight="{weight}" fill="{fill}" text-anchor="{anchor}" opacity="{op}">{t}</text>')

    def line(s, x1, y1, x2, y2, stroke=LINE, sw=1, dash=None, op=1):
        d = f' stroke-dasharray="{dash}"' if dash else ""
        s.p.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{sw}" opacity="{op}"{d}/>')

    def poly(s, pts, stroke, sw=2, fill="none", op=1):
        pp = " ".join(f"{a:.1f},{b:.1f}" for a, b in pts)
        s.p.append(f'<polyline points="{pp}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" stroke-linejoin="round" stroke-linecap="round" opacity="{op}"/>')

    def area(s, pts, base, color, op=0.18):
        pp = f"{pts[0][0]:.1f},{base:.1f} " + " ".join(f"{a:.1f},{b:.1f}" for a, b in pts) + f" {pts[-1][0]:.1f},{base:.1f}"
        s.p.append(f'<polygon points="{pp}" fill="{color}" opacity="{op}"/>')

    def panel(s, x, y, w, h, title=None, sub=None):
        s.rect(x, y, w, h, PANEL, LINE, 10)
        if title: s.text(x + 16, y + 26, title.upper(), 12.5, MUT, 700)
        if sub: s.text(x + w - 16, y + 26, sub, 12.5, MUT, 500, "end")

    def pill(s, x, y, t, color, size=13, fg="#04121f", pad=9):
        w = len(t) * size * 0.62 + pad * 2
        s.rect(x, y, w, size + 9, color, rx=(size + 9) / 2)
        s.text(x + pad, y + size + 1, t, size, fg, 700)
        return w

    def kpi(s, x, y, w, h, label, value, sub=None, color=TXT, bar=None):
        s.panel(x, y, w, h)
        s.text(x + 16, y + 26, label.upper(), 12, MUT, 700)
        s.text(x + 16, y + 68, value, 34, color, 700)
        if sub: s.text(x + 16, y + h - 14, sub, 13, MUT)
        if bar is not None:
            s.rect(x + 16, y + h - 34, w - 32, 6, "#1b2740", rx=3); s.rect(x + 16, y + h - 34, (w - 32) * min(1, bar), 6, ACC, rx=3)

    def header(s, title, sub, demo=True):
        s.rect(0, 0, s.w, 58, "#0e1730"); s.line(0, 58, s.w, 58, LINE)
        s.text(24, 26, "SUNDARAM SMART PRODUCTION INTELLIGENCE", 14, TXT, 700)
        s.text(24, 46, sub, 12.5, MUT)
        s.text(s.w / 2, 36, title, 18, TXT, 700, "middle")
        if demo: s.pill(s.w - 250, 16, "DEMO · SYNTHETIC DATA", "#3b2a05", 11, "#fbbf24")

    def save(s, name, scale=1.2):
        svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="{s.w}" height="{s.h}" viewBox="0 0 {s.w} {s.h}">{"".join(s.p)}</svg>'
        open(f"/home/claude/deck/svg/{name}.svg", "w").write(svg)
        subprocess.run(["rsvg-convert", "-z", str(scale), "-o", f"/home/claude/deck/img/{name}.png", f"/home/claude/deck/svg/{name}.svg"], check=True)


def scale(v, lo, hi, a, b):
    return a + (v - lo) / (hi - lo or 1) * (b - a)


def timeline(s, x, y, w, h, ivs, T, states=ST, tmin=0):
    for a, b, st in ivs:
        if b <= tmin or a >= T: continue
        a, b = max(a, tmin), min(b, T)
        s.rect(x + (a - tmin) / (T - tmin) * w, y, max(1, (b - a) / (T - tmin) * w), h, states.get(st, "#475569"))


def axis_time(s, x, y, w, T, step, tmin=0, fmt=lambda t: f"{t // 3600:02d}:{t % 3600 // 60:02d}", base=6):
    t = tmin
    while t <= T:
        px = x + (t - tmin) / (T - tmin) * w
        s.line(px, y, px, y + 5, MUT, 1); s.text(px, y + 20, f"{(base + t // 3600) % 24:02d}:{t % 3600 // 60:02d}", 12, MUT, 500, "middle")
        t += step


# =============== 1. Executive ===============
def exec_dash():
    s = Svg(1600, 900); s.header("Executive overview · all plants", "Vadodara Plant · Pune Plant · last 24 hours")
    day, hourly = D["day"], D["hourly"]
    good = sum(d["good"] for d in day.values()); tgt = sum(d["target"] for d in day.values())
    oees = [e["oee"] for d in day.values() for e in d["equipment"].values() if e["oee"] is not None]
    avs = [e["availability"] for d in day.values() for e in d["equipment"].values() if e["availability"] is not None]
    down_h = sum(sum(n for st, n in e["state_s"].items() if st in ("FAULT", "BYPASSED")) for d in day.values() for e in d["equipment"].values()) / 3600
    s.kpi(24, 78, 300, 118, "Good output", f"{good:,}", f"target {int(tgt):,}", TXT, good / tgt)
    s.kpi(340, 78, 300, 118, "Target achievement", f"{good / tgt * 100:.0f}%", "calculated", "#f59e0b")
    s.kpi(656, 78, 300, 118, "Average OEE", f"{sum(oees) / len(oees) * 100:.0f}%", "availability × performance × quality", TXT)
    s.kpi(972, 78, 300, 118, "Fault + bypass time", f"{down_h:.0f} h", "sum over 10 machines", "#ef4444")
    s.kpi(1288, 78, 288, 118, "Open alerts", "3", "1 high · 2 medium", "#f59e0b")
    # hourly output vs target
    s.panel(24, 216, 780, 330, "Hourly output vs target", "all lines")
    gx, gy, gw, gh = 60, 250, 720, 240
    tot = [sum(hourly[l][h]["good"] for l in hourly) for h in range(24)]; tg = [sum(hourly[l][h]["target"] for l in hourly) for h in range(24)]
    mx = max(max(tot), max(tg)) * 1.15
    for i in range(5):
        yy = gy + gh - gh * i / 4; s.line(gx, yy, gx + gw, yy); s.text(gx - 8, yy + 4, f"{int(mx * i / 4)}", 11, MUT, 500, "end")
    bw = gw / 24
    for h in range(24):
        s.rect(gx + h * bw + 3, gy + gh - tot[h] / mx * gh, bw - 6, tot[h] / mx * gh, ACC, rx=2)
    s.poly([(gx + h * bw + bw / 2, gy + gh - tg[h] / mx * gh) for h in range(24)], "#fbbf24", 2)
    for h in range(0, 24, 3): s.text(gx + h * bw + bw / 2, gy + gh + 22, f"{(6 + h) % 24:02d}:00", 11, MUT, 500, "middle")
    s.rect(470, 232, 12, 12, ACC, rx=2); s.text(488, 243, "Good output", 12, MUT); s.rect(580, 236, 20, 3, "#fbbf24"); s.text(606, 243, "Target", 12, MUT)
    # OEE trend
    s.panel(824, 216, 752, 330, "OEE trend by plant", "hourly average")
    gx, gy, gw, gh = 872, 250, 680, 240
    for i in range(5):
        yy = gy + gh - gh * i / 4; s.line(gx, yy, gx + gw, yy); s.text(gx - 8, yy + 4, f"{50 + i * 12.5:.0f}%", 11, MUT, 500, "end")
    def plant_series(pid):
        out = []
        for h in range(24):
            v = [hourly[l][h]["oee"] for l in hourly if day[l]["plant"] == pid and hourly[l][h]["oee"] is not None]
            out.append(sum(v) / len(v) if v else None)
        return out
    for pid, col, nm in (("PLANT-VAD", ACC, "Vadodara"), ("PLANT-PUN", "#a78bfa", "Pune")):
        ser = plant_series(pid); pts = [(gx + h / 23 * gw, gy + gh - (max(0.5, min(1.0, v)) - 0.5) / 0.5 * gh) for h, v in enumerate(ser) if v]
        s.poly(pts, col, 3)
    for h in range(0, 24, 3): s.text(gx + h / 23 * gw, gy + gh + 22, f"{(6 + h) % 24:02d}:00", 11, MUT, 500, "middle")
    s.rect(1230, 236, 20, 3, ACC); s.text(1256, 243, "Vadodara", 12, MUT); s.rect(1340, 236, 20, 3, "#a78bfa"); s.text(1366, 243, "Pune", 12, MUT)
    # loss pareto
    s.panel(24, 566, 520, 310, "Where production is lost", "machine-hours, all lines")
    tl = {}
    for l in D["loss"].values():
        for st, n in l.items(): tl[st] = tl.get(st, 0) + n
    rows = [(st, tl.get(st, 0) / 3600) for st in ("FAULT", "STARVED", "BLOCKED", "BYPASSED", "IDLE") if tl.get(st, 0) > 0]
    rows.sort(key=lambda r: -r[1]); mv = max(r[1] for r in rows)
    for i, (st, hrs) in enumerate(rows):
        yy = 612 + i * 50; s.text(44, yy + 20, LBL[st].title() if st != "BYPASSED" else "Bypass suspected", 14, TXT, 600)
        s.rect(210, yy + 4, 240, 22, "#1b2740", rx=4); s.rect(210, yy + 4, 240 * hrs / mv, 22, ST[st], rx=4); s.text(462, yy + 21, f"{hrs:.1f} h", 14, TXT, 700)
    # comparison table
    s.panel(564, 566, 1012, 310, "Line comparison", "24 h")
    hdr = ["Plant · line", "Output / target", "Achieve", "Avg OEE", "Constraint"]; xs = [584, 820, 1040, 1160, 1290]
    for x, t in zip(xs, hdr): s.text(x, 612, t.upper(), 12, MUT, 700)
    s.line(584, 622, 1556, 622)
    names = {"PLANT-VAD": "Vadodara", "PLANT-PUN": "Pune"}
    for i, (lid, d) in enumerate(day.items()):
        yy = 660 + i * 62; ov = [e["oee"] for e in d["equipment"].values() if e["oee"] is not None]
        s.text(584, yy, f"{names[d['plant']]} · {d['name']}", 16, TXT, 600)
        s.text(820, yy, f"{d['good']:,} / {int(d['target']):,}", 16, TXT, 500)
        col = "#22c55e" if d["ach"] >= .85 else "#f59e0b"; s.text(1040, yy, f"{d['ach'] * 100:.0f}%", 16, col, 700)
        s.text(1160, yy, f"{sum(ov) / len(ov) * 100:.0f}%", 16, TXT, 500)
        s.text(1290, yy, d["bottleneck"] + ("" if d["clear"] else " (candidate)"), 15, ACC, 600)
        s.line(584, yy + 22, 1556, yy + 22, LINE, 1, op=0.6)
    s.text(584, 850, "Losses are calculated from detected states; constraint = machine that rarely waits for material or space.", 12.5, MUT)
    s.save("dash_exec")


# =============== 2. Line dashboard (rendered from the live read model) ===============
def line_dash():
    s = Svg(1600, 900); s.header("Line 01 · Assembly · Vadodara Plant", "Live production flow  ·  data time 2026-09-21 06:36 UTC")
    k = SNAP["kpi"]
    s.kpi(24, 78, 380, 118, "Good output vs target", f"{k['good_output']}  /  {int(k['target'])}", f"last {int((k['window'] and 36))} min of data time · target {int(k['target_per_hour'])}/h", TXT, (k["achievement"] or 0))
    s.kpi(424, 78, 380, 118, "Target achievement", f"{(k['achievement'] or 0) * 100:.0f}%", "calculated", "#f59e0b")
    s.kpi(824, 78, 380, 118, "Line constraint", SNAP["equipment"][1]["name"], "clear bottleneck" if k["bottleneck"]["clear"] else "candidate only", ACC)
    s.kpi(1224, 78, 352, 118, "Needs attention", "1", "1 high item", "#ef4444")
    s.panel(24, 216, 1552, 330, "Production flow — equipment → queue → equipment")
    qs = {q["from"]: q for q in SNAP["queues"]}
    x = 48
    for e in SNAP["equipment"]:
        col = ST.get(e["state"], "#475569")
        s.rect(x, 262, 300, 250, "#0f1a31", LINE, 12); s.rect(x, 262, 300, 6, col, rx=3)
        s.text(x + 18, 302, e["equipment_id"], 20, TXT, 700); s.text(x + 18, 326, e["name"], 14, MUT)
        s.rect(x + 18, 350, 12, 12, col, rx=6); s.text(x + 38, 361, LBL.get(e["state"], e["state"]), 16, col, 700)
        d = e["duration_s"]; s.text(x + 18, 388, f"for {d // 60} min" if d >= 120 else f"for {d} s", 14, MUT)
        eo = k["equipment"][e["equipment_id"]]
        s.text(x + 18, 428, f"Count {e['counters']['total']}", 15, TXT, 600); s.text(x + 18, 456, f"OEE {eo['oee'] * 100:.0f}%" if eo["oee"] else "OEE –", 15, "#22c55e" if (eo['oee'] or 0) > .8 else "#f59e0b", 700)
        s.rect(x + 18, 476, 264, 6, "#1b2740", rx=3); s.rect(x + 18, 476, 264 * (eo["oee"] or 0), 6, ACC, rx=3)
        if e["equipment_id"] == k["bottleneck"]["candidate"] and k["bottleneck"]["clear"]: s.pill(x + 200, 246, "CONSTRAINT", "#0c4a6e", 11, "#bae6fd")
        q = qs.get(e["equipment_id"])
        if q:
            qx = x + 300
            s.text(qx + 40, 322, "→", 22, "#3b4c72", 500, "middle"); s.text(qx + 40, 352, q["queue_id"], 12, MUT, 700, "middle")
            s.rect(qx + 26, 364, 28, 84, "#1b2740", LINE, 4)
            lv = q["level"] or 0
            if lv: s.rect(qx + 26, 448 - 84 * lv / q["capacity"], 28, 84 * lv / q["capacity"], "#22c55e", rx=3)
            s.text(qx + 40, 470, f"{lv}/{q['capacity']}", 13, "#f59e0b" if lv == 0 else MUT, 700, "middle")
            s.text(qx + 40, 490, "empty" if lv == 0 else "", 12, "#f59e0b", 600, "middle")
        x += 300 + (80 if q else 0)
    # attention
    s.panel(24, 566, 760, 310, "What needs attention")
    a = SNAP["attention"][0]
    s.rect(44, 606, 720, 250, "#0f1a31", None, 8); s.rect(44, 606, 5, 250, "#ef4444")
    s.text(66, 634, "HIGH", 12, "#ef4444", 700); s.text(66, 664, a["title"], 22, TXT, 700)
    s.text(66, 700, "Why:  " + a["why"], 15, MUT); s.text(66, 728, "Effect:  " + "; ".join(a["effects"]), 15, MUT)
    s.text(66, 756, "Check:  " + a["action"], 15, "#cbd5e1"); s.text(66, 784, "Impact:  " + a["estimate_basis"], 15, MUT)
    s.text(66, 830, "Every figure carries its basis: detected · calculated · measured", 12.5, MUT)
    # equipment table
    s.panel(804, 566, 772, 310, "Equipment performance", "calculated")
    hdr = ["Equipment", "Avail.", "Perf.", "Quality", "OEE"]; xs = [824, 1150, 1260, 1370, 1490]
    for xx, t in zip(xs, hdr): s.text(xx, 612, t.upper(), 12, MUT, 700, "start" if xx == 824 else "end")
    s.line(824, 622, 1556, 622)
    for i, e in enumerate(SNAP["equipment"]):
        q = k["equipment"][e["equipment_id"]]; yy = 660 + i * 50
        s.text(824, yy, e["equipment_id"], 16, TXT, 700)
        for xx, key in zip(xs[1:], ("availability", "performance", "quality", "oee")):
            v = q[key]; c = "#22c55e" if (v or 0) >= .85 else "#f59e0b" if (v or 0) >= .6 else "#ef4444"
            s.text(xx + 30, yy, f"{v * 100:.0f}%" if v is not None else "–", 16, c, 700 if key == "oee" else 500, "end")
        s.line(824, yy + 18, 1556, yy + 18, LINE, 1, op=0.6)
    s.save("dash_line")


if __name__ == "__main__":
    exec_dash(); line_dash()
    from PIL import Image
    Image.open("/home/claude/deck/img/dash_exec.png").resize((1200, 675)).save("/tmp/d1.png")
    Image.open("/home/claude/deck/img/dash_line.png").resize((1200, 675)).save("/tmp/d2.png")
