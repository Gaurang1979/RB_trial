"""Prototype: read faint QR codes by matching against the expected candidates (serial run gaps).

For each image: engine decode -> read codes (text, quad) + unread regions.
1. learn the printer's QR settings (EC level, mask) from read codes' sampled grids
2. candidates = numeric run [min-10, max+10] minus codes read
3. each region: own finder search -> quad -> 21x21 module means -> correlate with every candidate
   pattern; accept best if score and margin over the 2nd are high
Hold-out checks on READ codes: (a) true text among candidates -> rank/margin, (b) true text removed
-> best score (false-match risk).
"""
import sys, os, re, time, itertools
sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
sys.path.insert(0, os.path.dirname(__file__))
import cv2, numpy as np, segno, engine
from finders import find_finders, quad_from_finders
from gridfit2 import stretch, grid_values

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
N = 21


from qrcodegen import QrCode, QrSegment
SPLIT = 2   # learned from read codes: alphanumeric prefix + numeric serial
def pattern(text, ec=None, mask=-1):
    segs = [QrSegment.make_alphanumeric(text[:SPLIT]), QrSegment.make_numeric(text[SPLIT:])]
    q = QrCode.encode_segments(segs, QrCode.Ecc.MEDIUM, 1, 1, mask, False)
    return np.array([[1.0 if q.get_module(x, y) else 0.0 for x in range(21)] for y in range(21)], np.float32)


def zgrid(vals):
    v = -(vals - vals.mean()) / (vals.std() + 1e-6)   # dark -> positive
    return v


# data area only: finders + separators (9x9 / 9x8 corners incl. format info) and timing are the
# same for every candidate and would swamp the comparison
DATA = np.ones((21, 21), bool)
DATA[:9, :9] = False; DATA[:9, 13:] = False; DATA[13:, :9] = False; DATA[6, :] = False; DATA[:, 6] = False
def score(z, pat):
    zz = z[DATA]; zz = (zz - zz.mean()) / (zz.std() + 1e-6)
    p = pat[DATA]; p = (p - p.mean()) / (p.std() + 1e-6)
    return float((zz * p).mean())


def crop_up(img, cx, cy, half, f=4):
    x0, y0 = int(max(0, cx - half)), int(max(0, cy - half))
    c = img[y0:int(cy + half), x0:int(cx + half)]
    s = stretch(c)
    u = cv2.resize(cv2.GaussianBlur(s, (0, 0), 0.6), None, fx=f, fy=f, interpolation=cv2.INTER_CUBIC).astype(np.float32)
    return u, (x0, y0)


def best_grid(u, m):
    """quads from own finder search at module size m (in u pixels), with BR-corner jitter."""
    out = []
    for mm in (m, m * 0.9, m * 1.1):
        q = quad_from_finders(find_finders(u, mm), mm)
        if q is None: continue
        for dx in range(-8, 9, 4):
            for dy in range(-8, 9, 4):
                qq = q.copy(); qq[2] += (dx, dy)
                out.append(grid_values(u, qq, N))
    return out


PATS = {}
def pat_m(t, mk):
    k = (t, mk)
    if k not in PATS: PATS[k] = pattern(t, mask=mk)
    return PATS[k]

def match(grids, cands, pats):
    best = {}
    for g in grids:
        z = orient(zgrid(g))
        mk = mask_from_format(z)
        for t in cands:
            s = score(z, pat_m(t, mk))
            if s > best.get(t, -9): best[t] = s
    r = sorted(best.items(), key=lambda kv: -kv[1])
    return r


def main(path):
    img = cv2.imread(path, 0)
    codes, st = engine.decode(img, expected=int(os.environ.get('N', '0')), budget_ms=3000, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
    texts = [c[0] for c in codes]
    m = re.match(r'^(.*?)(\d+)$', texts[0]); pre, w = m.group(1), len(m.group(2))
    nums = sorted(int(t[len(pre):]) for t in texts if t.startswith(pre) and t[len(pre):].isdigit())
    sides = [np.mean([np.linalg.norm(c[1][(k + 1) % 4] - c[1][k]) for k in range(4)]) for c in codes]
    side = float(np.median(sides)); mod = side / N
    print(os.path.basename(path), 'read', len(codes), 'side', round(side, 1), 'px, module', round(mod, 2), 'px; run', nums[0], '-', nums[-1])

    ec, mk = "M", -1
    lo, hi = nums[0] - 10, nums[-1] + 10
    allc = [f'{pre}{n:0{w}d}' for n in range(lo, hi + 1)]
    pats = {t: pattern(t, ec, mk) for t in allc}
    readset = set(texts)

    # 2. hold-out on read codes: own finder quad (not the decoder's), true text among candidates
    t0 = time.time(); ranks = []; margins = []; neg = []
    for t, quad, *_ in codes:
        cx, cy = quad.mean(0)
        u, _ = crop_up(img, cx, cy, side * 1.0)
        grids = best_grid(u, mod * 4)
        if not grids: ranks.append(None); continue
        cands = [c for c in allc if c not in readset] + [t]
        r = match(grids, cands, pats)
        rk = [x[0] for x in r].index(t)
        ranks.append(rk); margins.append(r[0][1] - r[1][1] if rk == 0 else r[0][1] - dict(r)[t])
        r2 = [x for x in r if x[0] != t]
        neg.append(r2[0][1])
        if rk == 0: pos_s = r[0][1]
    ok = sum(1 for x in ranks if x == 0)
    print(f'  hold-out on {len(codes)} read codes: right {ok}, wrong {sum(1 for x in ranks if x not in (0, None))}, no quad {ranks.count(None)}'
          f'; best wrong-candidate score max {max(neg):.2f} median {np.median(neg):.2f}; {1000*(time.time()-t0)/len(codes):.0f} ms/code')
    tops = [match(best_grid(crop_up(img, *c[1].mean(0), side)[0], mod * 4), [c[0]], pats) for c in codes[:0]]

    # 3. unread regions
    for (x, y, w_, h_) in st.miss_rects:
        cx, cy = x + w_ / 2, y + h_ / 2
        u, _ = crop_up(img, cx, cy, max(w_, h_) * 0.75)
        grids = best_grid(u, mod * 4)
        if not grids: print('  miss', (x, y), 'no quad'); continue
        cands = [c for c in allc if c not in readset]
        r = match(grids, cands, pats)
        print(f'  miss {(x, y)}: best {r[0][0]} {r[0][1]:.2f}, 2nd {r[1][0]} {r[1][1]:.2f}')




# ---- orientation from the fixed patterns, mask from the format bits
FIXED = np.zeros((21, 21), bool)
FIXED[:8, :8] = FIXED[:8, 13:] = FIXED[13:, :8] = True; FIXED[6, 8:13] = FIXED[8:13, 6] = True
_ref = pattern('RJ0000000000', mask=0)

def orient(z):
    """the transform (rotation / transpose) of the sampled grid whose finders and timing fit best"""
    best = None
    for k in range(4):   # printed codes are never mirrored (a transpose keeps the finders in place)
        g = np.rot90(z, k)
        s = score_mask(g, _ref, FIXED)
        if best is None or s > best[0]: best = (s, g)
    return best[1]

def score_mask(z, pat, m):
    zz = z[m]; zz = (zz - zz.mean()) / (zz.std() + 1e-6)
    p = pat[m]; p = (p - p.mean()) / (p.std() + 1e-6)
    return float((zz * p).mean())

FMT_POS = [(8, c) for c in (0, 1, 2, 3, 4, 5, 7, 8)] + [(r, 8) for r in (7, 5, 4, 3, 2, 1, 0)]
FMT_POS2 = [(20 - i, 8) for i in range(7)] + [(8, 13 + i) for i in range(8)]

def mask_from_format(z):
    """soft-match both copies of the 15 format bits against the 8 masks of EC level M"""
    best = None
    for mk in range(8):
        p = pattern('RJ0000000000', mask=mk) * 2 - 1
        s = sum(z[r, c] * p[r, c] for r, c in FMT_POS + FMT_POS2)
        if best is None or s > best[0]: best = (s, mk)
    return best[1]


if __name__ == '__main__':
    for p in sys.argv[1:]: main(p)
