"""Prototype: QR inside-out (lattice): module pitch and grid angle from the texture, the lattice of
module centres sampled, every 21 x 21 window scored by the QR fixed pattern (finders, separators,
timing) in 4 rotations, best windows refined and decoded hard, then soft (erasures)."""
import numpy as np, cv2
from lattice import pitch_and_angle, rotate, grid_values_c, rl
from gridfit2 import decode_bits, thresholds
from qrsoft import soft_decode, function_mask

def fixed_pattern(n=21):
    f = np.full((n, n), np.nan)
    def finder(y0, x0):
        for y in range(-1, 8):
            for x in range(-1, 8):
                yy, xx = y0 + y, x0 + x
                if 0 <= yy < n and 0 <= xx < n:
                    d = max(abs(y - 3), abs(x - 3))
                    f[yy, xx] = 1.0 if d in (0, 1, 3) else 0.0
    finder(0, 0); finder(0, n - 7); finder(n - 7, 0)
    for i in range(8, n - 8): f[6, i] = f[i, 6] = 1.0 if i % 2 == 0 else 0.0
    return f

FP = fixed_pattern()
FM = ~np.isnan(FP)
FPZ = (FP[FM] - FP[FM].mean()) / FP[FM].std()

def window_scores(M, n=21):
    out = []
    H, W = M.shape
    for y in range(H - n + 1):
        for x in range(W - n + 1):
            w = M[y:y + n, x:x + n]
            for k in range(4):
                g = np.rot90(w, k)
                z = -(g - g.mean()) / (g.std() + 1e-6)
                zz = z[FM]; zz = (zz - zz.mean()) / (zz.std() + 1e-6)
                out.append((float((zz * FPZ).mean()), y, x, k))
    out.sort(reverse=True)
    return out

def read_qr(crop, side, n=21, version=1):
    m = side / n; up = max(1.0, 6.0 / m)
    c = cv2.resize(crop, None, fx=up, fy=up, interpolation=cv2.INTER_CUBIC) if up > 1 else crop.copy()
    lo, hi = np.percentile(c, (1, 99.5)); c = np.clip((c.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.float32)
    ang, p = pitch_and_angle(c, m * up)
    for sig in (0, 0.3, 0.45):
        img = c if sig == 0 else rl(c, sig * p)
        r = rotate(img, ang)
        best = None
        for oy in np.arange(0, p, p / 8):
            for ox in np.arange(0, p, p / 8):
                xs = np.arange(ox + p / 2, r.shape[1] - p / 2, p); ys = np.arange(oy + p / 2, r.shape[0] - p / 2, p)
                X, Y = np.meshgrid(xs, ys)
                v = cv2.remap(r, X.astype(np.float32), Y.astype(np.float32), cv2.INTER_LINEAR)
                if best is None or v.std() > best[0]: best = (v.std(), v, ox, oy)
        _, M, ox, oy = best
        if min(M.shape) < n: continue
        for sc, wy, wx, k in window_scores(M, n)[:4]:
            if sc < 0.5: break
            for dy in (0, -0.25, 0.25, -0.5, 0.5):
                for dx in (0, -0.25, 0.25, -0.5, 0.5):
                    X0, Y0 = ox + (wx + dx) * p, oy + (wy + dy) * p
                    q = np.float32([[X0, Y0], [X0 + n * p, Y0], [X0 + n * p, Y0 + n * p], [X0, Y0 + n * p]])
                    v = np.rot90(grid_values_c(r, q, n), k)
                    for b in thresholds(v):
                        t = decode_bits(b)
                        if t: return t, 'hard'
                    t = soft_decode(v, version)
                    if t: return t, 'soft'
    return None, None
