import json
from iso import Scene, shade, state_color, FONT
import equipment as E

D = json.load(open("/home/claude/deck/deckdata.json"))
SNAP = D["snapshot_2160"]
COL = {"PRODUCING": "#22C55E", "STARVED": "#F59E0B", "BLOCKED": "#FB923C", "FAULT": "#EF4444", "BYPASSED": "#A855F7"}
LABEL = {"PRODUCING": "PRODUCING", "STARVED": "STARVED", "BLOCKED": "BLOCKED", "FAULT": "FAULT", "BYPASSED": "BYPASS SUSPECTED"}
# line layout (world units along +x)
ITEMS = [("PRESS-01", E.press, 0, 0, 140, 110, 202), ("QUEUE-A", None, 140, 30, 110, 50, 60), ("PRESS-02", E.press, 250, 0, 140, 110, 202),
         ("QUEUE-B", None, 390, 30, 110, 50, 60), ("ASSEMBLY-01", E.assembly, 500, 0, 150, 110, 140), ("QUEUE-C", None, 650, 30, 110, 50, 60),
         ("TEST-01", E.test_station, 760, 10, 110, 90, 150)]


def pill(S, x, y, text, color, size=15, pad=10, fg="#04121f"):
    w = len(text) * size * 0.62 + pad * 2
    S.raw(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{size + 10}" rx="{(size + 10) / 2}" fill="{color}"/>')
    S.text(x + pad, y + size + 2, text, size, fg, 700)
    return w


def card(S, cx, cy, title, sub, color, w=190):
    x, y = cx - w / 2, cy - 62
    S.raw(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w}" height="58" rx="10" fill="#0d162b" fill-opacity="0.92" stroke="{color}" stroke-width="1.6"/>')
    S.raw(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w}" height="6" rx="3" fill="{color}"/>')
    S.text(x + 14, y + 30, title, 17, "#E5E9F2", 700)
    S.text(x + 14, y + 50, sub, 14, color, 700)
    S.raw(f'<line x1="{cx:.1f}" y1="{cy - 4:.1f}" x2="{cx:.1f}" y2="{cy + 22:.1f}" stroke="{color}" stroke-width="1.4" stroke-dasharray="3 3"/>')
    S.raw(f'<circle cx="{cx:.1f}" cy="{cy + 24:.1f}" r="3.5" fill="{color}"/>')


def line_scene(name, w, h, ox, oy, s, states, levels, mode="hero", bg=("#15213c", "#080d19")):
    S = Scene(w, h, ox, oy, s, bg=bg)
    S.floor(-120, -80, 990, 200, step=50, fill="#0e182e", line="#1a2a4c")
    for code, fn, x, y, dx, dy, zt in ITEMS:
        st = states.get(code)
        if st: S.glow(x + dx / 2, y + dy / 2, 20, 190, COL[st], 0.22)
    for code, fn, x, y, dx, dy, zt in ITEMS:
        if fn is None:
            lv = levels.get(code, 0)
            E.conveyor(S, x, y, dx, level=min(lv, 6), cap=6)
        else:
            fn(S, x, y, state=states.get(code))
    # overlays
    for code, fn, x, y, dx, dy, zt in ITEMS:
        px, py = S.p(x + dx / 2, y + dy / 2, zt)
        if fn is not None:
            st = states.get(code, "PRODUCING")
            card(S, px, py - 44, code, LABEL[st], COL[st])
        elif mode == "hero":
            pass
    return S


def hero():
    states = {e["equipment_id"]: e["state"] for e in SNAP["equipment"]}
    levels = {q["queue_id"]: q["level"] for q in SNAP["queues"]}
    print("hero states", states, levels)
    S = line_scene("hero_line", 1920, 1080, 905, 372, 1.2, states, levels)
    # alert card near PRESS-01 (real attention item)
    a = SNAP["attention"][0]
    x, y = 940, 900
    S.raw(f'<rect x="{x}" y="{y}" width="520" height="118" rx="14" fill="#1a0f14" fill-opacity="0.94" stroke="#EF4444" stroke-width="2"/>')
    S.text(x + 22, y + 34, "HIGH  ·  " + a["title"], 22, "#FECACA", 700)
    S.text(x + 22, y + 64, "Downstream starved: Press 02, Assembly 01", 18, "#E5E9F2", 500)
    S.text(x + 22, y + 92, f"Estimated loss: up to {a['estimated_loss_units']} units at standard cycle", 18, "#FCA5A5", 500)
    S.save("hero_line")


def line_map():
    states = {"PRESS-01": "PRODUCING", "PRESS-02": "PRODUCING", "ASSEMBLY-01": "PRODUCING", "TEST-01": "PRODUCING"}
    levels = {"QUEUE-A": 2, "QUEUE-B": 5, "QUEUE-C": 1}
    S = Scene(1664, 760, 535, 318, 0.9, bg=("#15213c", "#0a1120"))
    S.floor(-120, -80, 990, 200, step=50, fill="#0e182e", line="#1a2a4c")
    for code, fn, x, y, dx, dy, zt in ITEMS:
        if fn is None: E.conveyor(S, x, y, dx, level=levels[code], cap=6)
        else: fn(S, x, y, state="PRODUCING")
    # equipment code tags + signal points
    sig = {"PRESS-01": ["Machine ON", "Cycle pulse", "Fault", "Output count", "Temp", "Vibration"], "PRESS-02": ["Machine ON", "Cycle pulse", "Fault", "Output count", "Temp", "Vibration"],
           "ASSEMBLY-01": ["Machine ON", "Cycle pulse", "Fault", "Output count", "Temp"], "TEST-01": ["Machine ON", "Cycle pulse", "Fault", "Output count", "Temp"]}
    for code, fn, x, y, dx, dy, zt in ITEMS:
        px, py = S.p(x + dx / 2, y + dy / 2, zt)
        if fn is not None:
            card(S, px, py - 30, code, "%d signals mapped" % len(sig[code]), "#38BDF8", 200)
        else:
            bx, by = S.p(x + dx / 2, y + dy / 2, 20)
            S.raw(f'<rect x="{bx - 62:.1f}" y="{by + 62:.1f}" width="124" height="52" rx="9" fill="#0d162b" stroke="#22C55E" stroke-width="1.4"/>')
            S.text(bx, by + 84, code, 15, "#E5E9F2", 700, "middle"); S.text(bx, by + 104, "level sensor", 13, "#86EFAC", 600, "middle")
    S.save("line_map")


if __name__ == "__main__":
    hero(); line_map()
    from PIL import Image
    im = Image.open("/home/claude/deck/img/hero_line.png"); im.resize((960, 540)).save("/tmp/hero_s.png")
    im = Image.open("/home/claude/deck/img/line_map.png"); im.resize((1000, 457)).save("/tmp/map_s.png")
