"""Synthetic straightened text lines for the OCR line recogniser (ocrnet).

Each sample looks like what the C++ line finder (engine/src/ocr/lines.cpp) hands the recogniser:
32 px high, text dark on light after a 1..99 percentile stretch, the line roughly centred with
margins, rotation and slant already removed (small residuals remain). Four print models:

  laser   thin continuous strokes, often letter-spaced, very low contrast before the stretch
  tto     thermal transfer: solid, bolder strokes with small ribbon voids
  cij     continuous inkjet dot matrix (5x7 / 7x9 grids): dots missing, merged into blobs or
          smeared, a dead row, uneven and jittered, the pitch varying with conveyor speed; the
          line is often stretched or squeezed horizontally
  emboss  embossed or stamped characters: no ink, a relief lit from one side

Any of them can be faint (contrast of a few grey levels), on a textured surface, or bowed as the
line finder leaves a line printed on a curved surface (_bend).

Fonts come from the operating system (macOS paths below); the ruppee sign falls back to a font that
has it. Run `python -m ocr.synth_text out.png` from ml/ for a preview sheet.
"""

import glob
import os
import random

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

LINE_H = 32
CHARSET_PATH = os.path.join(os.path.dirname(__file__), "..", "models", "ocr_charset.txt")


def load_charset(path=CHARSET_PATH):
    with open(path, encoding="utf-8") as f:
        chars = [line.rstrip("\n").rstrip("\r") for line in f]
    return [c for c in chars if len(c) == 1]


CHARSET = load_charset()

_FONT_DIRS = ["/System/Library/Fonts", "/System/Library/Fonts/Supplemental", "/Library/Fonts",
              os.path.expanduser("~/Library/Fonts"), "C:/Windows/Fonts"]
_WANTED = ["Menlo", "Monaco", "Andale Mono", "Courier New", "PTMono", "SFNSMono", "Helvetica", "Geneva",
           "Arial", "Arial Narrow", "Verdana", "Tahoma", "Trebuchet MS", "DIN Alternate", "DIN Condensed",
           "Avenir Next Condensed", "Futura", "SFNS", "Kohinoor", "Consolas", "Lucida Console", "Gill Sans",
           "Arial Rounded Bold", "Courier"]
_SYMBOL_FALLBACK = ["Kohinoor", "Helvetica", "SFNS", "Arial Unicode", "Geneva", "Nirmala"]


def _find_fonts(names):
    found = []
    for n in names:
        for d in _FONT_DIRS:
            for ext in ("ttf", "ttc", "otf"):
                for p in glob.glob(os.path.join(d, f"{n}*.{ext}")):
                    if p not in found:
                        found.append(p)
    return found


FONTS = _find_fonts(_WANTED)
SYMBOL_FONTS = _find_fonts(_SYMBOL_FALLBACK)


class _FontCache:
    def __init__(self):
        self.cache = {}
        self.has = {}

    def get(self, path, size, index=0):
        k = (path, size, index)
        if k not in self.cache:
            try:
                self.cache[k] = ImageFont.truetype(path, size, index=index)
            except Exception:
                self.cache[k] = None
        return self.cache[k]

    def has_glyph(self, path, ch):
        k = (path, ch)
        if k not in self.has:
            f = self.get(path, 40)
            ok = False
            if f is not None:
                try:
                    a = f.getmask(ch).getbbox()
                    b = f.getmask("\uffff").getbbox()
                    ok = a is not None and (a != b or ch in " ")
                except Exception:
                    ok = False
            self.has[k] = ok
        return self.has[k]


FC = _FontCache()


# ---------------------------------------------------------------------------------------------
# text

def _rand_alnum(rng, n, upper=True, digits=True):
    pool = ""
    if upper:
        pool += "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    if digits:
        pool += "0123456789"
    return "".join(rng.choice(pool) for _ in range(n))


MONTHS = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]
WORDS = ["per", "ml", "gm", "g", "kg", "Rs", "MRP", "Incl", "of", "all", "taxes", "Batch", "No", "Mfd", "Exp",
         "Use", "by", "Best", "before", "LOT", "MFG", "EXP", "BN", "B.No", "PKD", "USP", "Net", "Wt", "Qty",
         "pcs", "tabs", "caps", "unit", "ltr", "L", "each", "Price", "Date", "Time", "Line", "Shift"]


def gen_text(rng: random.Random) -> str:
    d2 = lambda: f"{rng.randint(0, 99):02d}"
    mm = lambda: f"{rng.randint(1, 12):02d}"
    r = rng.random()
    sym = rng.choice(["\u20b9", "\u20b9", "Rs.", "MRP \u20b9", "MRP: \u20b9", "MRP Rs."])
    if r < 0.12:
        return f"{sym} {rng.randint(1, 999)}.{d2()}, {rng.choice(['', 'B.No.', 'LOT '])}{_rand_alnum(rng, rng.randint(3, 4), digits=False)}{rng.randint(10, 9999)}"
    if r < 0.22:
        return f"{sym} {rng.randint(0, 99)}.{d2()} per {rng.choice(['ml', 'gm', 'g', 'kg', 'unit', 'tab', 'pc'])}"
    if r < 0.34:
        a, b = rng.choice(["\u00a5", "#", "MFD", "MFG", "EXP", "Mfd.", "Exp.", "\u00a5"]), rng.choice(["#", "EXP", "Exp.", "#", "BB"])
        return f"{a} {mm()}/{d2()}, {b} {mm()}/{d2()} {rng.randint(0, 23):02d}:{rng.randint(0, 59):02d}"
    if r < 0.44:
        fmt = rng.choice(["{d}/{m}/{y}", "{d}.{m}.{y}", "{d}-{m}-{y}", "{M} {Y}", "{m}/{Y}", "{d} {M} {y}"])
        s = fmt.format(d=f"{rng.randint(1, 31):02d}", m=mm(), y=d2(), Y=str(rng.randint(2020, 2039)),
                       M=rng.choice(MONTHS))
        pre = rng.choice(["", "MFG ", "EXP ", "MFD: ", "EXP: ", "Use by ", "BEST BEFORE ", "PKD ", "Mfd. ", "Exp. "])
        return pre + s
    if r < 0.54:
        pre = rng.choice(["B.No. ", "BATCH ", "LOT ", "BN ", "B.NO:", "Batch No. ", "L"])
        return pre + _rand_alnum(rng, rng.randint(4, 10))
    if r < 0.62:
        return f"{rng.randint(0, 23):02d}:{rng.randint(0, 59):02d}" + (f":{rng.randint(0, 59):02d}" if rng.random() < 0.4 else "") \
            + (f" L{rng.randint(1, 9)}" if rng.random() < 0.3 else "")
    if r < 0.72:
        return " ".join(rng.choice(WORDS) + rng.choice(["", "", ".", ":", ","]) for _ in range(rng.randint(1, 4)))
    if r < 0.86:
        # random characters from the whole set, to cover every class
        n = rng.randint(3, 18)
        pool = [c for c in CHARSET if c != " "]
        s = "".join(rng.choice(pool) for _ in range(n))
        # a few spaces inside
        for _ in range(rng.randint(0, 2)):
            i = rng.randint(1, max(1, len(s) - 1))
            s = s[:i] + " " + s[i:]
        return s
    n = rng.randint(4, 14)
    return _rand_alnum(rng, n) if rng.random() < 0.7 else "".join(rng.choice("0123456789") for _ in range(n))


# ---------------------------------------------------------------------------------------------
# glyph rendering on a supersampled canvas (k x final scale). Returns ink map 0..1, text box.

K = 3


def _font_for(ch, main):
    if FC.has_glyph(main, ch):
        return main
    for f in SYMBOL_FONTS:
        if FC.has_glyph(f, ch):
            return f
    return None


def _cap_height(font):
    b = font.getbbox("H")
    return max(1, b[3] - b[1])


def render_solid(text, rng, ch_px, tracking, bold, thin, font_path):
    """Continuous strokes. ch_px = capital height in final px."""
    size = 20
    probe = FC.get(font_path, 100)
    if probe is None:
        return None
    size = max(6, int(round(100 * ch_px * K / _cap_height(probe))))
    font = FC.get(font_path, size)
    H = int(ch_px * K * 3)
    Wc = int(len(text) * ch_px * K * 1.6 + 8 * K * ch_px)
    img = Image.new("L", (Wc, H), 0)
    dr = ImageDraw.Draw(img)
    base = int(H * 0.66)
    x = float(2 * ch_px * K)
    sw = int(bold)
    for c in text:
        fp = _font_for(c, font_path) if c != " " else font_path
        if fp is None:
            return None
        f = font if fp == font_path else FC.get(fp, max(6, int(round(100 * ch_px * K / _cap_height(FC.get(fp, 100))))))
        if c != " ":
            dr.text((x, base), c, font=f, fill=255, anchor="ls", stroke_width=sw, stroke_fill=255)
        x += f.getlength(c) + tracking * ch_px * K + (sw * 1.5)
    a = np.asarray(img, np.float32) / 255.0
    if thin > 0:
        a = cv2.erode(a, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * thin + 1, 2 * thin + 1)))
    return a, base


def _dot_glyph(c, rows, rng, font_path):
    """Binary grid of one character for a dot-matrix printer: `rows` dots for the capital height
    plus two descender rows; width up to the printer's matrix (4 / 5 / 7 columns)."""
    fp = font_path if FC.has_glyph(font_path, c) else _font_for(c, font_path)
    if fp is None:
        return None
    f = FC.get(fp, 120)
    cap = _cap_height(f)
    im = Image.new("L", (300, 320), 0)
    ImageDraw.Draw(im).text((40, 220), c, font=f, fill=255, anchor="ls", stroke_width=3, stroke_fill=255)
    a = np.asarray(im, np.float32) / 255.0
    pitch = cap / (rows - 1)
    desc = 2
    top, bot = int(220 - cap - pitch / 2), int(220 + desc * pitch + pitch / 2)
    xs = np.nonzero(a[top:bot].max(axis=0) > 0.3)[0]
    if len(xs) == 0:
        return np.zeros((rows + desc, 2), np.uint8)
    x0, x1 = xs.min(), xs.max()
    maxc = {5: 4, 7: 5, 9: 7}[rows]
    cols = int(np.clip(round((x1 - x0) / pitch) + 1, 1, maxc))
    crop = a[top:bot, x0:x1 + 1]
    g = cv2.resize(crop, (cols, rows + desc), interpolation=cv2.INTER_AREA)
    return (g > rng.uniform(0.22, 0.38)).astype(np.uint8)


def render_dots(text, rng, ch_px, tracking, font_path):
    """CIJ dot matrix. Faults seen on lines: dots missing at random (drop), merged into blobs (radius
    up to 0.8 pitch, smeared along the print direction), one dead row of the matrix, the pitch varying
    with conveyor speed, uneven ink and small satellite drops."""
    rows = rng.choice([5, 7, 7, 7, 9])
    pitch = ch_px * K / (rows - 1)
    rad = pitch * (rng.uniform(0.32, 0.6) if rng.random() < 0.7 else rng.uniform(0.6, 0.8))
    smear = rng.uniform(1.0, 1.6) if rng.random() < 0.3 else 1.0
    drop = rng.uniform(0.0, 0.3) if rng.random() < 0.8 else 0.0
    if rng.random() < 0.15:
        drop = rng.uniform(0.3, 0.45)
    dead_row = rng.randrange(rows) if rng.random() < 0.1 else -1
    jit = pitch * rng.uniform(0.0, 0.15) if rng.random() < 0.8 else pitch * rng.uniform(0.15, 0.25)
    speed_var = rng.uniform(0.0, 0.12) if rng.random() < 0.4 else 0.0
    sats = rng.uniform(0.0, 0.08) if rng.random() < 0.3 else 0.0
    H = int(ch_px * K * 3)
    Wc = int(len(text) * (ch_px * K * 1.2 * (1 + speed_var) + tracking * ch_px * K) + 6 * ch_px * K)
    a = np.zeros((H, Wc), np.float32)
    top = int(H * 0.66 - ch_px * K)
    x = 2 * ch_px * K
    nr = np.random.default_rng(rng.randint(0, 1 << 30))
    for c in text:
        if c == " ":
            x += pitch * rng.uniform(3, 5)
            continue
        g = _dot_glyph(c, rows, rng, font_path)
        if g is None:
            return None
        px = pitch * (1 + nr.uniform(-speed_var, speed_var))
        for r in range(g.shape[0]):
            for k in range(g.shape[1]):
                if g[r, k] and nr.random() >= drop and r != dead_row:
                    cx = x + k * px + nr.normal(0, jit)
                    cy = top + r * pitch + nr.normal(0, jit)
                    rr = max(1.0, rad * nr.uniform(0.8, 1.15))
                    ax = (int(round(rr * smear)), int(round(rr)))
                    cv2.ellipse(a, (int(round(cx)), int(round(cy))), ax, 0, 0, 360, float(nr.uniform(0.5, 1.0)), -1, cv2.LINE_AA)
                    if sats and nr.random() < sats:
                        sx, sy = cx + nr.normal(0, pitch), cy + nr.normal(0, pitch)
                        cv2.circle(a, (int(round(sx)), int(round(sy))), max(1, int(round(rr * 0.4))), float(nr.uniform(0.4, 0.9)), -1, cv2.LINE_AA)
        x += (g.shape[1] + 1) * px + tracking * ch_px * K
    return a, int(H * 0.66)


def render_emboss(text, rng, ch_px, tracking, font_path):
    """Embossed or debossed (stamped) characters: no ink, only a relief lit from one side, so a stroke
    shows as a bright edge and a dark edge on the same grey. Returns a signed shading map (-1..1)."""
    r = render_solid(text, rng, ch_px, tracking, rng.choice([1, 2]) * K // 2, 0, font_path)
    if r is None:
        return None
    ink, base = r
    h = cv2.GaussianBlur(ink, (0, 0), K * rng.uniform(0.6, 1.4)) * rng.choice([1, -1])
    ang = np.deg2rad(rng.uniform(0, 360))
    gx = cv2.Sobel(h, cv2.CV_32F, 1, 0, ksize=3)
    gy = cv2.Sobel(h, cv2.CV_32F, 0, 1, ksize=3)
    s = gx * np.cos(ang) + gy * np.sin(ang)
    s /= max(1e-6, np.abs(s).max())
    s += ink * rng.uniform(-0.25, 0.25)            # the stamped face a little darker or lighter
    return s, base, ink


# ---------------------------------------------------------------------------------------------

def _bend(maps, rng, ch_px):
    """Residual curvature the line finder leaves on curved surfaces: the baseline bows (bottle shoulder,
    print following a cylinder at an angle) and characters crowd towards the ends of a cylinder."""
    ink = maps[0]
    ys, xs = np.nonzero(ink > 0.2)
    if len(xs) == 0:
        return maps
    x0, x1 = xs.min(), xs.max()
    h, w = ink.shape
    u = np.clip((np.arange(w, dtype=np.float32) - x0) / max(1, x1 - x0) * 2 - 1, -1.2, 1.2)
    amp = rng.uniform(0.05, 0.3) * ch_px * K * rng.choice([1, -1])
    dy = amp * (u ** 2 - 0.4) if rng.random() < 0.7 else amp * np.sin(u * np.pi * rng.uniform(0.5, 1.0))
    xmap = np.arange(w, dtype=np.float32)
    if rng.random() < 0.5:                         # cylinder seen from the front: ends compressed
        c = rng.uniform(0.0, 0.25)
        xmap = (x0 + x1) / 2 + (xmap - (x0 + x1) / 2) * (1 + c * u ** 2) / (1 + c)
    mx = np.ascontiguousarray(np.repeat(xmap[None, :], h, 0), np.float32)
    my = np.ascontiguousarray(np.arange(h, dtype=np.float32)[:, None] + dy[None, :], np.float32)
    return [None if m is None else cv2.remap(m, mx, my, cv2.INTER_LINEAR, borderValue=0) for m in maps]


def _finish(ink, rng, ch_px, stretch, rot, shear, laser, signal=None, curve=False):
    """Place the ink map like the line finder does, then add the camera. `signal` (same size as ink)
    replaces the ink as what the camera sees, for embossed print; the ink still places the line."""
    if curve:
        ink, signal = _bend([ink, signal], rng, ch_px)
    ys, xs = np.nonzero(ink > 0.2)
    if len(xs) == 0:
        return None
    # residual rotation / slant + horizontal stretch about the text centre
    cx, cy = (xs.min() + xs.max()) / 2, (ys.min() + ys.max()) / 2
    t = np.tan(np.deg2rad(shear))
    M = np.array([[stretch, -t, 0], [0, 1, 0]], np.float64)
    R = cv2.getRotationMatrix2D((cx, cy), rot, 1.0)
    A = np.vstack([R, [0, 0, 1]]) @ np.vstack([M, [0, 0, 1]])
    A[0, 2] += cx - (A[0, 0] * cx + A[0, 1] * cy)
    A[1, 2] += cy - (A[1, 0] * cx + A[1, 1] * cy)
    # keep the whole text on the canvas after the stretch
    corners = A[:2] @ np.array([[xs.min(), xs.max(), xs.min(), xs.max()], [ys.min(), ys.min(), ys.max(), ys.max()], [1, 1, 1, 1]], np.float64)
    pad = 2 * ch_px * K
    A[0, 2] += pad - corners[0].min()
    out_w = int(corners[0].max() - corners[0].min() + 2 * pad)
    ink = cv2.warpAffine(ink, A[:2], (out_w, ink.shape[0]))
    if signal is not None:
        signal = cv2.warpAffine(signal, A[:2], (out_w, signal.shape[0]))
    ys, xs = np.nonzero(ink > 0.2)
    if len(xs) == 0:
        return None
    y0, y1 = np.percentile(ys, 2), np.percentile(ys, 98)
    lh = max(y1 - y0 + 1, 0.6 * ch_px * K)
    mv = lh * rng.uniform(0.14, 0.24)
    mh = lh * rng.uniform(0.3, 0.8)
    dy = rng.uniform(-0.06, 0.06) * lh
    top, bot = y0 - mv + dy, y1 + mv + dy
    left, right = xs.min() - mh * rng.uniform(0.6, 1.4), xs.max() + mh * rng.uniform(0.6, 1.4)
    scale = LINE_H / (bot - top)
    W = int(np.ceil((right - left) * scale / 4.0)) * 4
    W = int(np.clip(W, 16, 2048))
    A2 = np.array([[scale, 0, -left * scale], [0, scale, -top * scale]], np.float64)
    # anti-alias: blur on the supersampled map before shrinking
    ink = cv2.GaussianBlur(ink, (0, 0), max(0.3, 0.45 / scale))
    small = cv2.warpAffine(ink, A2, (W, LINE_H), flags=cv2.INTER_AREA if scale < 1 else cv2.INTER_LINEAR)
    small = np.clip(small, 0, 1)
    sig = None
    if signal is not None:
        signal = cv2.GaussianBlur(signal, (0, 0), max(0.3, 0.45 / scale))
        sig = cv2.warpAffine(signal, A2, (W, LINE_H), flags=cv2.INTER_AREA if scale < 1 else cv2.INTER_LINEAR)
    # neighbouring lines peeking in at the top or bottom
    if rng.random() < 0.25:
        h = int(rng.uniform(1, 5))
        band = (np.random.default_rng(rng.randint(0, 1 << 30)).random((h, W)) < 0.35).astype(np.float32)
        band = cv2.GaussianBlur(band, (0, 0), 0.7)
        if rng.random() < 0.5:
            small[:h] = np.maximum(small[:h], band)
        else:
            small[-h:] = np.maximum(small[-h:], band)
    # camera: background, contrast, light gradient, blur, noise (in grey levels before the stretch)
    nr = np.random.default_rng(rng.randint(0, 1 << 30))
    bg = rng.uniform(20, 200)
    contrast = rng.uniform(6, 25) if (laser and rng.random() < 0.6) else rng.uniform(10, 140)
    if rng.random() < 0.15:                        # faint print, worn ribbon, dirty lens
        contrast = rng.uniform(4, 10)
    grad = np.linspace(rng.uniform(-1, 1), rng.uniform(-1, 1), W)[None, :] * rng.uniform(0, 0.4) * contrast
    if sig is not None:
        img = bg + grad + contrast * 0.6 * sig
    else:
        img = bg + grad + contrast * small * (1 if laser else -1) * rng.choice([1, 1, 1, -1])
    if rng.random() < 0.2:                         # textured surface: carton fibre, brushed metal
        tex = cv2.GaussianBlur(nr.normal(0, 1, img.shape).astype(np.float32), (0, 0), rng.uniform(0.8, 2.5))
        img = img + tex * contrast * rng.uniform(0.05, 0.25) / max(1e-6, tex.std())
    if rng.random() < 0.7:
        img = cv2.GaussianBlur(img.astype(np.float32), (0, 0), rng.uniform(0.3, 1.1))
    img = img + nr.normal(0, rng.uniform(0.4, 3.0), img.shape)
    if laser and rng.random() < 0.5:   # laser speckle on the ablated strokes
        img = img + small * nr.normal(0, contrast * 0.2, img.shape)
    # the line finder's stretch: 1..99 percentile -> 0..255, text dark on light
    lo, hi = np.percentile(img, (1, 99))
    hi = max(hi, lo + 8)
    s = np.clip((img - lo) / (hi - lo), 0, 1)
    # text must end dark: compare mean under the ink (embossed print is left as lit; the line finder
    # picks the polarity of its edges, which can be either)
    if sig is None and (s * small).sum() / max(small.sum(), 1e-6) > s.mean():
        s = 1 - s
    elif sig is not None and rng.random() < 0.5:
        s = 1 - s
    return (s * 255).astype(np.uint8)


KINDS = ["laser", "tto", "cij", "emboss"]
KIND_WEIGHTS = [0.35, 0.2, 0.35, 0.1]
CURVE_P = 0.25


def sample(rng: random.Random, text=None, kind=None):
    """One synthetic line: (uint8 image 32 x W, text). Retries on rendering failures."""
    for _ in range(10):
        t = text if text is not None else gen_text(rng)
        t = "".join(c for c in t if c in CHARSET)
        t = " ".join(t.split())
        if not t:
            continue
        k = kind or rng.choices(KINDS, KIND_WEIGHTS)[0]
        ch = rng.uniform(14, 22)
        font = rng.choice(FONTS)
        signal = None
        if k == "emboss":
            r = render_emboss(t, rng, ch, rng.uniform(0.05, 0.4), font)
            if r is not None:
                signal, r = r[0], (r[2], r[1])
            stretch = rng.uniform(0.85, 1.15)
        elif k == "cij":
            r = render_dots(t, rng, ch, rng.uniform(0.0, 0.6), font)
            stretch = rng.uniform(0.7, 1.5)
        else:
            tracking = rng.uniform(0.0, 0.7) if k == "laser" else rng.uniform(-0.05, 0.25)
            bold = 0 if k == "laser" else rng.choice([0, 1, 1, 2])
            thin = rng.choice([0, 0, 1, 2]) if k == "laser" else 0
            r = render_solid(t, rng, ch, tracking, bold * K // 2, thin, font)
            stretch = rng.uniform(0.85, 1.15)
            if r is not None and k == "tto" and rng.random() < 0.5:
                a = r[0]
                voids = (np.random.default_rng(rng.randint(0, 1 << 30)).random(a.shape) < rng.uniform(0.002, 0.02))
                voids = cv2.dilate(voids.astype(np.uint8), cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
                r = (a * (1 - voids), r[1])
        if r is None:
            continue
        img = _finish(r[0], rng, ch, stretch, rng.uniform(-1.5, 1.5), rng.uniform(-4, 4), k == "laser",
                      signal=signal, curve=rng.random() < CURVE_P)
        if img is not None:
            return img, t
    raise RuntimeError("could not render a line")


if __name__ == "__main__":
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else "ocr_synth_preview.png"
    rng = random.Random(3)
    rows = []
    for kind in ["laser", "laser", "tto", "tto", "cij", "cij", "cij", "cij", "emboss", "emboss"]:
        im, t = sample(rng, kind=kind)
        print(kind, im.shape, t)
        rows.append(im)
    W = max(r.shape[1] for r in rows)
    sheet = np.vstack([np.pad(r, ((2, 2), (0, W - r.shape[1])), constant_values=128) for r in rows])
    cv2.imwrite(out, sheet)
    print("fonts", len(FONTS), "symbol fonts", len(SYMBOL_FONTS))
