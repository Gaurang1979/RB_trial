"""Prototype: PowerGrid-style ('inside-out') Data Matrix decoding. No finder or quiet zone needed:
1. module pitch and grid angle from the texture (autocorrelation peaks of the dark-module image)
2. rotate the region so the grid is axis-aligned; find the grid phase (sample points in module
   centres maximise the spread of the samples)
3. sample every module of the lattice -> a binary matrix larger than the code
4. every n x n window (4 rotations) whose L finder fits -> decode; Reed-Solomon rejects wrong ones."""
import sys, glob, os, time, cv2, numpy as np, zxingcpp
sys.path.insert(0, os.path.dirname(__file__))
from dmfit import render, decode_bits

F = zxingcpp.BarcodeFormat.DataMatrix


def edge_spectrum(patch, m):
    """strength, pitch and phase of the periodic module edges along x (columns)"""
    gx = np.abs(cv2.Sobel(patch, cv2.CV_32F, 1, 0, ksize=3))
    prof = gx.sum(0); prof = prof - prof.mean()
    n = len(prof); spec = np.fft.rfft(prof * np.hanning(n))
    freqs = np.fft.rfftfreq(n)
    band = (freqs > 1 / (1.15 * m)) & (freqs < 1 / (0.85 * m))   # learned module size +-15%
    if not band.any(): return 0, m, 0
    i = np.argmax(np.abs(spec) * band)
    # parabolic refinement of the peak
    if 0 < i < len(spec) - 1:
        a, b_, c = np.abs(spec[i - 1:i + 2]); d = 0.5 * (a - c) / (a - 2 * b_ + c + 1e-9)
    else: d = 0
    f = freqs[i] + d / n
    return np.abs(spec[i]) / (np.abs(spec).sum() + 1e-9), 1 / f, np.angle(spec[i])


def rotate(img, ang):
    h, w = img.shape
    R = cv2.getRotationMatrix2D((w / 2, h / 2), ang, 1.0)
    return cv2.warpAffine(img, R, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)


def pitch_and_angle(img, m_guess):
    """grid angle where the module edges are most periodic along both axes; the pitch from the peak"""
    best = None
    for ang in np.arange(0, 90, 1.0):
        r = rotate(img, ang)
        sx, px, _ = edge_spectrum(r, m_guess); sy, py, _ = edge_spectrum(r.T.copy(), m_guess)
        if best is None or sx + sy > best[0]: best = (sx + sy, ang, (px + py) / 2)
    a0 = best[1]
    for ang in np.arange(a0 - 1, a0 + 1.01, 0.25):
        r = rotate(img, ang)
        sx, px, _ = edge_spectrum(r, m_guess); sy, py, _ = edge_spectrum(r.T.copy(), m_guess)
        if sx + sy > best[0]: best = (sx + sy, ang, (px + py) / 2)
    return best[1], best[2]


def lattice(img, ang, p):
    """rotate so the grid is axis-aligned, find the phase, sample module centres -> means matrix"""
    h, w = img.shape
    r = rotate(img.astype(np.float32), ang)
    best = None
    for oy in np.arange(0, p, p / 8):
        for ox in np.arange(0, p, p / 8):
            xs = np.arange(ox + p / 2, w - p / 2, p); ys = np.arange(oy + p / 2, h - p / 2, p)
            X, Y = np.meshgrid(xs, ys)
            v = cv2.remap(r, X.astype(np.float32), Y.astype(np.float32), cv2.INTER_LINEAR)
            sc = v.std()
            if best is None or sc > best[0]: best = (sc, v, ox, oy)
    return best[1], best[2], best[3], r


def window_scores(M, n):
    """n x n windows (in 4 rotations of the lattice) ranked by how well the L finder and the clock
    tracks fit"""
    t = cv2.threshold(np.clip(M, 0, 255).astype(np.uint8), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[0]
    D = M < t
    out = []
    H, W = D.shape
    for y in range(H - n + 1):
        for x in range(W - n + 1):
            w_ = D[y:y + n, x:x + n]
            for k in range(4):
                g = np.rot90(w_, k)
                sc = (g[:, 0].mean() + g[n - 1, :].mean() + np.mean([g[0, i] == (i % 2 == 0) for i in range(n)]) +
                      np.mean([g[i, n - 1] == ((n - 1 - i) % 2 == 0) for i in range(n)]))
                out.append((sc, y, x, k))
    out.sort(reverse=True)
    return out


from dmfit import grid_values, thresholds
from dmsoft import soft_decode
SOFT = True

def grid_values_c(img, quad, n, sub=3, frac=0.4):
    """module means over the central `frac` of each module (bleeding print stays out)"""
    src = np.float32([[0, 0], [n, 0], [n, n], [0, n]])
    H = cv2.getPerspectiveTransform(src, np.float32(quad))
    o = (np.arange(sub) + 0.5) / sub * frac + (1 - frac) / 2
    gx, gy = np.meshgrid(np.arange(n), np.arange(n))
    px = (gx[..., None, None] + o[None, None, :, None]).repeat(sub, -1)
    py = (gy[..., None, None] + o[None, None, None, :]).repeat(sub, -2)
    pts = np.stack([px.ravel(), py.ravel()], -1).astype(np.float32)[None]
    qq = cv2.perspectiveTransform(pts, H)[0]
    vals = cv2.remap(img, qq[:, 0].reshape(1, -1), qq[:, 1].reshape(1, -1), cv2.INTER_LINEAR)
    return vals.reshape(n, n, sub * sub).mean(-1)


def refine_decode(r, x0, y0, p, n, k):
    """the window's square on the rotated image, jittered in position and scale; module means and
    threshold variants -> decode"""
    for dy in (0, -0.25, 0.25, -0.5, 0.5):
        for dx in (0, -0.25, 0.25, -0.5, 0.5):
            for ds in (1.0, 0.98, 1.02):
                s_ = n * p * ds
                X0, Y0 = x0 + dx * p, y0 + dy * p
                q = np.float32([[X0, Y0], [X0 + s_, Y0], [X0 + s_, Y0 + s_], [X0, Y0 + s_]])
                v = np.rot90(grid_values_c(r, q, n), k)
                for bits in list(thresholds(v)) + [v < np.percentile(v, q_) for q_ in (50, 42, 58)]:
                    t = decode_bits(bits)
                    if t: return t
                if SOFT:
                    sr = soft_decode(v, n)
                    if sr: return sr[0]
    return None


def windows_decode(M, n, r=None, ox=0, oy=0, p=1):
    for sc, y, x, k in window_scores(M, n)[:6]:
        if sc < 3.0: break
        t = refine_decode(r, ox + x * p, oy + y * p, p, n, k)
        if t: return t
    return None


def rl(img, sigma, it=10):
    u = img.astype(np.float32) / 255 + 1e-3; est = u.copy()
    for _ in range(it):
        conv = cv2.GaussianBlur(est, (0, 0), sigma) + 1e-6
        est = est * cv2.GaussianBlur(u / conv, (0, 0), sigma)
    return np.clip(est * 255, 0, 255).astype(np.float32)


def read(crop, side, sizes):
    m = side / sizes[0]
    up = 1.0 if m >= 6 else 6.0 / m
    c = cv2.resize(crop, None, fx=up, fy=up, interpolation=cv2.INTER_CUBIC) if up > 1 else crop
    lo, hi = np.percentile(c, (1, 99.5)); c = np.clip((c.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255)
    from dmfit import code_squares
    c8 = c.astype(np.uint8); boxes = code_squares(c8, side * up)
    if boxes:
        (cx, cy), (bw, bh), _ = cv2.minAreaRect(np.float32(boxes[0])); r = int(max(bw, bh) * 0.85)
    else:   # no clear block: the region centre, a code and a half across
        cy, cx = c.shape[0] / 2, c.shape[1] / 2; r = int(side * up * 0.85)
    x0, y0 = max(0, int(cx) - r), max(0, int(cy) - r)
    patch = c[y0:int(cy) + r, x0:int(cx) + r].astype(np.float32)
    ang, p = pitch_and_angle(patch, m * up)
    M, ox, oy, rot = lattice(patch, ang, p)
    for sig in (0, 0.3, 0.45):   # deblur (Richardson-Lucy) in module units: lens blur at the corners
        img = patch if sig == 0 else rl(patch, sig * p)
        M, ox, oy, rot = lattice(img, ang, p)
        for n in sizes:
            t = windows_decode(M, n, rot, ox, oy, p)
            if t: return t, ang, p
    return None, ang, p


if __name__ == '__main__':
    files = sorted(glob.glob(sys.argv[1])); side = float(sys.argv[2]); sizes = [int(x) for x in sys.argv[3].split(',')]
    ok = 0; t0 = time.time()
    for f in files:
        c = cv2.imread(f, 0)
        t, ang, p = read(c, side, sizes); ok += bool(t)
        print(os.path.basename(f), f'angle {ang:.1f} pitch {p:.2f}', t or '')
    print(f'decoded {ok}/{len(files)} in {time.time() - t0:.1f}s')
