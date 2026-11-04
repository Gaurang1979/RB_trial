"""Tiny isometric SVG toolkit for equipment 'renders' (flat-shaded 3D look with soft lighting)."""
import math, subprocess

C30, S30 = math.cos(math.radians(30)), 0.5
FONT = "IBM Plex Sans Var, IBM Plex Sans, DejaVu Sans, sans-serif"


def _rgb(h):
    h = h.lstrip("#")
    return [int(h[i:i + 2], 16) for i in (0, 2, 4)]


def shade(h, f):
    r, g, b = _rgb(h)
    if f <= 1:
        return "#%02x%02x%02x" % tuple(max(0, min(255, int(c * f))) for c in (r, g, b))
    k = f - 1
    return "#%02x%02x%02x" % tuple(max(0, min(255, int(c + (255 - c) * k))) for c in (r, g, b))


class Scene:
    def __init__(self, w, h, ox, oy, s=1.0, bg=None):
        self.w, self.h, self.ox, self.oy, self.s = w, h, ox, oy, s
        self.parts = []
        self.defs = []
        self.bg = bg
        self._gid = 0

    # ---- projection --------------------------------------------------
    def p(self, x, y, z=0):
        return (self.ox + (x - y) * C30 * self.s, self.oy + (x + y) * S30 * self.s - z * self.s)

    def add(self, s):
        self.parts.append(s)

    def gid(self):
        self._gid += 1
        return f"g{self._gid}"

    def poly(self, pts, fill, stroke=None, sw=1, op=1.0, extra=""):
        pp = " ".join(f"{a:.1f},{b:.1f}" for a, b in pts)
        st = f' stroke="{stroke}" stroke-width="{sw}" stroke-linejoin="round"' if stroke else ""
        self.add(f'<polygon points="{pp}" fill="{fill}"{st} opacity="{op}" {extra}/>')

    # ---- primitives --------------------------------------------------
    def box(self, x, y, z, w, d, h, c, edge=True, top=None, left=None, right=None, op=1.0):
        P = self.p
        e = shade(c, 0.45) if edge else None
        # right face (x = x+w), left face (y = y+d), top face
        self.poly([P(x + w, y, z), P(x + w, y + d, z), P(x + w, y + d, z + h), P(x + w, y, z + h)], right or shade(c, 0.62), e, 0.8, op)
        self.poly([P(x, y + d, z), P(x + w, y + d, z), P(x + w, y + d, z + h), P(x, y + d, z + h)], left or shade(c, 0.82), e, 0.8, op)
        self.poly([P(x, y, z + h), P(x + w, y, z + h), P(x + w, y + d, z + h), P(x, y + d, z + h)], top or shade(c, 1.18), e, 0.8, op)

    def face_left(self, x, y, z, w, h, fill, op=1.0, stroke=None):
        P = self.p
        self.poly([P(x, y, z), P(x + w, y, z), P(x + w, y, z + h), P(x, y, z + h)], fill, stroke, 1, op)

    def face_right(self, x, y, z, w, h, fill, op=1.0, stroke=None):
        P = self.p
        self.poly([P(x, y, z), P(x, y + w, z), P(x, y + w, z + h), P(x, y, z + h)], fill, stroke, 1, op)

    def face_top(self, x, y, z, w, d, fill, op=1.0, stroke=None):
        P = self.p
        self.poly([P(x, y, z), P(x + w, y, z), P(x + w, y + d, z), P(x, y + d, z)], fill, stroke, 1, op)

    def cyl(self, cx, cy, z, r, h, c, top=None):
        s = self.s
        px, py = self.p(cx, cy, z)
        rx, ry = r * math.sqrt(2) * C30 * s, r * math.sqrt(2) * S30 * s
        yb, yt = py, py - h * s
        gid = self.gid()
        self.defs.append(f'<linearGradient id="{gid}" x1="0" x2="1"><stop offset="0" stop-color="{shade(c, 0.6)}"/><stop offset="0.35" stop-color="{shade(c, 1.15)}"/>'
                         f'<stop offset="1" stop-color="{shade(c, 0.55)}"/></linearGradient>')
        self.add(f'<path d="M{px - rx:.1f},{yt:.1f} L{px - rx:.1f},{yb:.1f} A{rx:.1f},{ry:.1f} 0 0 0 {px + rx:.1f},{yb:.1f} L{px + rx:.1f},{yt:.1f} Z" fill="url(#{gid})" stroke="{shade(c, 0.4)}" stroke-width="0.8"/>')
        self.add(f'<ellipse cx="{px:.1f}" cy="{yt:.1f}" rx="{rx:.1f}" ry="{ry:.1f}" fill="{top or shade(c, 1.3)}" stroke="{shade(c, 0.45)}" stroke-width="0.8"/>')

    def shadow(self, x, y, w, d, op=0.35, grow=14):
        P = self.p
        pts = [P(x - grow, y - grow, 0), P(x + w + grow, y - grow, 0), P(x + w + grow, y + d + grow, 0), P(x - grow, y + d + grow, 0)]
        self.poly(pts, "#000000", op=op, extra='filter="url(#blur)"')

    def glow(self, x, y, z, r, color, op=0.55):
        px, py = self.p(x, y, z)
        gid = self.gid()
        self.defs.append(f'<radialGradient id="{gid}"><stop offset="0" stop-color="{color}" stop-opacity="{op}"/><stop offset="1" stop-color="{color}" stop-opacity="0"/></radialGradient>')
        self.add(f'<circle cx="{px:.1f}" cy="{py:.1f}" r="{r * self.s:.1f}" fill="url(#{gid})"/>')

    def floor(self, x0, y0, x1, y1, step=40, fill="#12203a", line="#1d2f52", z=0, glow=None):
        P = self.p
        self.poly([P(x0, y0, z), P(x1, y0, z), P(x1, y1, z), P(x0, y1, z)], fill)
        xs = range(int(x0), int(x1) + 1, step)
        ys = range(int(y0), int(y1) + 1, step)
        for xx in xs:
            a, b = P(xx, y0, z), P(xx, y1, z)
            self.add(f'<line x1="{a[0]:.1f}" y1="{a[1]:.1f}" x2="{b[0]:.1f}" y2="{b[1]:.1f}" stroke="{line}" stroke-width="1"/>')
        for yy in ys:
            a, b = P(x0, yy, z), P(x1, yy, z)
            self.add(f'<line x1="{a[0]:.1f}" y1="{a[1]:.1f}" x2="{b[0]:.1f}" y2="{b[1]:.1f}" stroke="{line}" stroke-width="1"/>')

    def stack_light(self, x, y, z, state_color, r=7):
        self.cyl(x, y, z, 3, 26, "#9aa4b5")
        cols = [("#ef4444", 0.25), ("#f59e0b", 0.25), ("#22c55e", 0.25)]
        for i, (cc, _) in enumerate(cols):
            lit = (cc == state_color)
            self.cyl(x, y, z + 26 + i * 11, r, 10, cc if lit else shade(cc, 0.32), top=shade(cc, 1.4) if lit else shade(cc, 0.4))
        if state_color:
            self.glow(x, y, z + 40, 60, state_color, 0.6)

    def text(self, x, y, s, size=14, fill="#e5e9f2", weight=500, anchor="start", op=1):
        s = s.replace("&", "&amp;")
        self.add(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{FONT}" font-size="{size}" font-weight="{weight}" fill="{fill}" text-anchor="{anchor}" opacity="{op}">{s}</text>')

    def raw(self, s):
        self.add(s)

    def svg(self):
        bg = ""
        if self.bg:
            gid = "bgrad"
            bg = (f'<defs><radialGradient id="{gid}" cx="50%" cy="45%" r="75%"><stop offset="0" stop-color="{self.bg[0]}"/><stop offset="1" stop-color="{self.bg[1]}"/></radialGradient></defs>'
                  f'<rect width="{self.w}" height="{self.h}" fill="url(#{gid})"/>')
        return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{self.w}" height="{self.h}" viewBox="0 0 {self.w} {self.h}">'
                f'<defs><filter id="blur" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="9"/></filter>{"".join(self.defs)}</defs>'
                f'{bg}{"".join(self.parts)}</svg>')

    def save(self, name, scale=1.0):
        path = f"/home/claude/deck/svg/{name}.svg"
        open(path, "w").write(self.svg())
        out = f"/home/claude/deck/img/{name}.png"
        subprocess.run(["rsvg-convert", "-z", str(scale), "-o", out, path], check=True)
        return out


def state_color(state):
    return {"PRODUCING": "#22c55e", "STARVED": "#f59e0b", "BLOCKED": "#fb923c", "FAULT": "#ef4444", "BYPASSED": "#a855f7"}.get(state)
