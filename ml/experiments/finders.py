import sys, glob, os, time, itertools, cv2, numpy as np
sys.path.insert(0, os.path.dirname(__file__))
from gridfit2 import stretch, try_quad
import flexgrid

def finder_template(m, ang):
    """7x7-module finder (dark ring, light ring, dark 3x3) at module size m px, rotated."""
    s = int(np.ceil(9 * m * 1.5)) | 1; c = s / 2
    t = np.zeros((s, s), np.float32)
    yy, xx = np.mgrid[0:s, 0:s].astype(np.float32) - c + 0.5
    a = np.deg2rad(ang); u = (xx * np.cos(a) + yy * np.sin(a)) / m; v = (-xx * np.sin(a) + yy * np.cos(a)) / m
    d = np.maximum(np.abs(u), np.abs(v))
    t[d <= 4.5] = 1.0                        # light quiet ring around
    t[d <= 3.5] = 0.0                        # dark outer ring
    t[d <= 2.5] = 1.0                        # light ring
    t[d <= 1.5] = 0.0                        # dark centre
    mask = (d <= 4.5).astype(np.float32)
    return t, mask

def find_finders(img, m, k=8):
    best = None
    for ang in range(0, 90, 10):
        t, mk = finder_template(m, ang)
        r = cv2.matchTemplate(img, t, cv2.TM_CCORR_NORMED, mask=mk) if False else cv2.matchTemplate(img, (t - t[mk > 0].mean()) * mk, cv2.TM_CCORR)
        r /= (np.sqrt(cv2.matchTemplate(img * img, mk, cv2.TM_CCORR) - cv2.matchTemplate(img, mk, cv2.TM_CCORR) ** 2 / mk.sum()) + 1e-3)
        best = r if best is None else np.maximum(best, r)
    off = finder_template(m, 0)[0].shape[0] // 2
    pk = []
    b = best.copy()
    for _ in range(k):
        _, mv, _, ml = cv2.minMaxLoc(b)
        pk.append((ml[0] + off, ml[1] + off, mv))
        cv2.circle(b, ml, int(5 * m), -1e9, -1)
    return pk

def quad_from_finders(pk, m, n=21):
    """three peaks forming a right isosceles triangle with legs (n-7) modules -> 4 outer corners"""
    L = (n - 7) * m; best = None
    for a, b, c in itertools.permutations(pk[:6], 3):
        A, B, C = (np.array(p[:2], np.float32) for p in (a, b, c))   # A = corner finder (top-left)
        ab, ac = B - A, C - A
        la, lc = np.linalg.norm(ab), np.linalg.norm(ac)
        if not (0.7 * L < la < 1.3 * L and 0.7 * L < lc < 1.3 * L): continue
        cos = abs(np.dot(ab, ac)) / (la * lc)
        if cos > 0.25: continue
        if ab[0] * ac[1] - ab[1] * ac[0] < 0: continue                                # B to the right, C below
        score = a[2] + b[2] + c[2] - 2 * cos - abs(la - lc) / L
        if best is None or score > best[0]: best = (score, A, B, C)
    if best is None: return None
    _, A, B, C = best
    ex, ey = (B - A) / (n - 7), (C - A) / (n - 7)                       # one module along each axis
    tl = A - 3.5 * ex - 3.5 * ey
    return np.float32([tl, tl + n * ex, tl + n * ex + n * ey, tl + n * ey])

if __name__ == "__main__":
    files = sorted(glob.glob(sys.argv[1] + "/*.png")); ok = 0; t0 = time.time()
    for p in files:
        c = cv2.imread(p, 0); s = stretch(c)
        u = cv2.resize(cv2.GaussianBlur(s, (0, 0), 0.6), None, fx=4, fy=4, interpolation=cv2.INTER_CUBIC).astype(np.float32)
        text = None
        # module size: these labels' codes are about 21 modules across ~65 px (x4 here)
        for m in (11.0, 12.5, 14.0, 9.5):
            pk = find_finders(u, m)
            q = quad_from_finders(pk, m)
            if q is None: continue
            text = try_quad(u, q) or flexgrid.refine_and_decode(u, q)
            if text: break
        ok += bool(text); print(os.path.basename(p), text or "")
    print(f"own finder detection: decoded {ok}/{len(files)} ({time.time() - t0:.1f}s)")
