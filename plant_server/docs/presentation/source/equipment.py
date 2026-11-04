from iso import Scene, shade, state_color, C30, S30

BLUE, ORANGE = "#2F5FB3", "#F97316"


def press(S, x, y, state="PRODUCING"):
    S.shadow(x, y, 140, 110)
    S.box(x, y, 0, 140, 110, 14, "#4B5563")
    S.box(x + 18, y + 16, 14, 104, 78, 24, "#6B7280")
    S.box(x + 6, y + 6, 14, 16, 16, 152, BLUE)
    S.box(x + 118, y + 6, 14, 16, 16, 152, BLUE)
    S.box(x + 34, y + 26, 92, 72, 46, 20, "#1F2937")            # upper die
    S.box(x + 40, y + 30, 40, 60, 40, 10, "#9CA3AF")              # workpiece / lower die
    S.cyl(x + 70, y + 52, 112, 11, 56, "#C7CEDA")                # ram rod
    S.box(x + 8, y + 90, 14, 10, 10, 150, "#5B84D6")             # front tie rods
    S.box(x + 122, y + 90, 14, 10, 10, 150, "#5B84D6")
    S.box(x - 2, y - 2, 166, 144, 114, 36, BLUE)                 # crown
    S.cyl(x + 70, y + 55, 202, 26, 22, "#3F72CC")                # hydraulic cylinder
    S.cyl(x + 70, y + 55, 224, 12, 12, "#9CA3AF")
    S.face_right(x + 142, y + 30, 60, 44, 56, "#111827")         # control panel on side
    S.face_right(x + 142, y + 36, 66, 32, 22, "#0EA5E9")
    S.face_right(x + 142, y + 36, 92, 32, 10, "#22C55E", op=0.85)
    S.stack_light(x + 118, y + 12, 202, state_color(state))


def cnc(S, x, y, state="PRODUCING"):
    S.shadow(x, y, 150, 120)
    S.box(x, y, 0, 150, 120, 16, "#374151")
    S.box(x, y, 16, 150, 120, 104, "#E5E7EB", top="#F3F4F6")
    S.box(x + 12, y + 8, 120, 126, 100, 16, "#D1D5DB")            # roof
    S.face_left(x + 14, y + 120, 34, 104, 62, "#0B1220", stroke="#9CA3AF")        # window
    S.face_left(x + 20, y + 120, 40, 40, 50, "#38BDF8", op=0.22)
    S.face_left(x + 66, y + 120, 40, 30, 50, "#38BDF8", op=0.12)
    S.face_left(x + 14, y + 120, 24, 104, 6, "#9CA3AF")           # sill
    S.face_left(x + 122, y + 120, 34, 22, 62, "#4B5563")          # side door panel
    S.face_left(x + 126, y + 120, 84, 14, 8, "#F97316")
    S.box(x + 150, y + 30, 46, 10, 40, 50, "#1F2937")             # pendant
    S.face_right(x + 160, y + 33, 54, 34, 34, "#22D3EE", op=0.9)
    S.box(x + 44, y + 30, 136, 60, 50, 22, "#9CA3AF")             # spindle head
    S.cyl(x + 74, y + 55, 158, 9, 22, "#374151")
    S.stack_light(x + 130, y + 14, 136, state_color(state))


def assembly(S, x, y, state="PRODUCING"):
    S.shadow(x, y, 150, 110)
    S.box(x, y, 0, 150, 110, 50, "#64748B")
    S.box(x + 6, y + 6, 50, 138, 98, 6, "#94A3B8")
    S.box(x + 20, y + 30, 56, 70, 50, 8, "#334155")
    for i, c in enumerate(["#38BDF8", "#FBBF24", "#34D399"]):
        S.box(x + 28 + i * 20, y + 44, 64, 14, 14, 12, c)
    S.cyl(x + 118, y + 38, 56, 22, 22, ORANGE)
    S.box(x + 110, y + 30, 78, 16, 16, 58, ORANGE)
    S.cyl(x + 118, y + 38, 136, 11, 14, "#FDBA74")
    S.box(x + 52, y + 30, 132, 74, 16, 14, ORANGE)
    S.box(x + 44, y + 32, 112, 14, 12, 26, "#1F2937")
    S.box(x + 40, y + 30, 100, 6, 6, 14, "#9CA3AF"); S.box(x + 52, y + 30, 100, 6, 6, 14, "#9CA3AF")
    for px, py in ((2, 104), (144, 104), (144, 2)):
        S.box(x + px, y + py, 0, 5, 5, 118, "#F59E0B")
    S.face_left(x + 5, y + 109, 12, 139, 104, "#93C5FD", op=0.14)
    S.face_right(x + 149, y + 5, 12, 104, 104, "#93C5FD", op=0.14)
    S.stack_light(x + 8, y + 10, 118, state_color(state))


def test_station(S, x, y, state="PRODUCING"):
    S.shadow(x, y, 110, 90)
    S.box(x, y, 0, 110, 90, 80, "#334155")
    S.face_left(x + 8, y + 90, 10, 54, 56, "#1E293B")
    S.face_left(x + 12, y + 90, 14, 46, 8, "#22C55E", op=0.8)
    S.box(x + 14, y + 36, 80, 80, 8, 58, "#0B1220")               # monitor
    S.face_left(x + 19, y + 44, 86, 70, 46, "#0E7490")
    S.face_left(x + 28, y + 44, 100, 12, 8, "#4ADE80"); S.face_left(x + 36, y + 44, 96, 26, 8, "#4ADE80")   # check mark
    S.face_left(x + 26, y + 44, 122, 50, 4, "#67E8F9", op=0.8); S.face_left(x + 26, y + 44, 114, 34, 4, "#67E8F9", op=0.5)
    S.box(x + 84, y + 10, 80, 10, 10, 78, "#9CA3AF")
    S.box(x + 40, y + 10, 148, 54, 10, 10, "#9CA3AF")
    S.cyl(x + 44, y + 15, 118, 4, 30, "#E5E7EB")
    S.stack_light(x + 96, y + 70, 80, state_color(state))


def conveyor(S, x, y, length=260, level=4, cap=8):
    S.shadow(x, y, length, 50, op=0.25)
    for lx in (6, length - 14):
        for ly in (4, 38):
            S.box(x + lx, y + ly, 0, 8, 8, 24, "#475569")
    S.box(x, y, 24, length, 50, 10, "#64748B")
    S.box(x + 4, y + 5, 34, length - 8, 40, 4, "#1F2937")
    for i in range(int((length - 20) / 22)):
        S.face_top(x + 12 + i * 22, y + 6, 38.2, 3, 38, "#374151", op=0.9)
    S.box(x, y, 38, length, 4, 9, "#94A3B8")
    S.box(x, y + 46, 38, length, 4, 9, "#94A3B8")
    span = (length - 30) / max(cap, 1)
    cols = ["#38BDF8", "#FBBF24", "#34D399", "#F472B6"]
    for i in range(level):
        S.box(x + 14 + i * span, y + 14, 38, 20, 22, 18, cols[i % 4])


def gateway(S, x, y):
    S.shadow(x - 6, y - 4, 130, 66)
    S.box(x, y, 0, 120, 56, 84, "#CBD5E1", top="#E2E8F0")
    for i in range(6):
        S.face_left(x + 10 + i * 8, y + 56, 60, 4, 16, "#64748B")     # vents
    S.face_left(x + 10, y + 56, 26, 60, 22, "#0F172A")
    for i, c in enumerate(["#22C55E", "#22C55E", "#F59E0B", "#38BDF8"]):
        S.face_left(x + 16 + i * 12, y + 56, 34, 7, 7, c)
    S.face_right(x + 120, y + 10, 24, 14, 12, "#0F172A"); S.face_right(x + 120, y + 30, 24, 14, 12, "#0F172A")
    S.cyl(x + 100, y + 12, 84, 3, 62, "#111827"); S.cyl(x + 100, y + 12, 146, 5, 8, "#374151")
    # board
    bx, by = x + 160, y - 2
    S.shadow(bx, by, 112, 74, op=0.25)
    S.box(bx, by, 0, 112, 74, 6, "#1F8A4C", top="#2BAA61")
    S.box(bx + 44, by + 20, 6, 28, 28, 5, "#111827", top="#1F2937")
    S.box(bx + 14, by + 12, 6, 16, 16, 4, "#9CA3AF")
    S.box(bx + 92, by + 8, 6, 20, 18, 14, "#CBD5E1"); S.box(bx + 92, by + 34, 6, 20, 14, 14, "#CBD5E1")
    S.box(bx + 6, by + 62, 6, 92, 8, 9, "#111827")
    for i in range(20):
        S.box(bx + 8 + i * 4.4, by + 64, 15, 2, 2, 3, "#FBBF24")


def sensors(S, x, y):
    for dx, kind in ((0, "prox"), (95, "temp"), (190, "vib")):
        S.shadow(x + dx - 6, y - 6, 60, 60, op=0.25)
        S.box(x + dx - 6, y - 6, 0, 60, 60, 6, "#334155")
        cx, cy = x + dx + 24, y + 24
        if kind == "prox":
            S.cyl(cx, cy, 6, 12, 62, "#CBD5E1"); S.cyl(cx, cy, 68, 13, 8, "#94A3B8"); S.cyl(cx, cy, 76, 9, 10, "#334155")
            S.glow(cx, cy, 80, 40, "#22C55E", 0.7); S.cyl(cx, cy, 86, 3, 3, "#4ADE80")
        elif kind == "temp":
            S.cyl(cx, cy, 6, 3.5, 66, "#E5E7EB"); S.box(cx - 10, cy - 10, 72, 20, 20, 24, "#0EA5E9"); S.box(cx - 6, cy - 6, 96, 12, 12, 8, "#0369A1")
        else:
            S.cyl(cx, cy, 6, 15, 12, "#111827"); S.cyl(cx, cy, 18, 12, 26, "#94A3B8"); S.cyl(cx, cy, 44, 8, 8, "#E5E7EB")
            S.glow(cx, cy, 30, 34, "#A855F7", 0.5)


def tile(name, fn, w=900, h=675, ox=None, oy=None, s=1.25, glow="#22C55E", **kw):
    S = Scene(w, h, ox or w * 0.42, oy or h * 0.55, s, bg=("#16233f", "#0a1120"))
    S.floor(-70, -70, 250, 220, step=40, fill="#0f1a31", line="#1b2c50")
    S.glow(70, 60, 60, 260, glow, 0.16)
    fn(S, 0, 0, **kw)
    return S.save(name)


if __name__ == "__main__":
    tile("eq_press", press, oy=430, s=1.55)
    tile("eq_cnc", cnc, oy=440, s=1.5)
    tile("eq_assembly", assembly, oy=440, s=1.5)
    tile("eq_test", test_station, oy=440, s=1.7)
    S = Scene(900, 675, 200, 330, 1.7, bg=("#16233f", "#0a1120"))
    S.floor(-60, -60, 360, 120, step=40, fill="#0f1a31", line="#1b2c50")
    conveyor(S, 0, 0, 260, level=5, cap=8); S.save("eq_conveyor")
    S = Scene(900, 675, 150, 280, 1.5, bg=("#16233f", "#0a1120"))
    S.floor(-50, -30, 320, 160, step=40, fill="#0f1a31", line="#1b2c50")
    gateway(S, 0, 20); S.save("eq_gateway")
    S = Scene(900, 675, 200, 360, 1.5, bg=("#16233f", "#0a1120"))
    S.floor(-50, -30, 330, 160, step=40, fill="#0f1a31", line="#1b2c50")
    sensors(S, 0, 20); S.save("eq_sensors")
