import sys, glob, os, time, cv2, numpy as np, zxingcpp
sys.path.insert(0, os.path.dirname(__file__))
from gridfit2 import stretch, decode_bits, try_quad
K = 4  # control net K x K

def fixed_v1(n):
    f = -np.ones((n, n), np.int8)
    for cy, cx in [(0, 0), (0, n - 7), (n - 7, 0)]:
        for y in range(-1, 8):
            for x in range(-1, 8):
                yy, xx = cy + y, cx + x
                if 0 <= yy < n and 0 <= xx < n:
                    d = max(abs(x - 3), abs(y - 3))
                    f[yy, xx] = 0 if d in (2, 4) else 1
    for i in range(8, n - 8):
        f[6, i] = f[i, 6] = 1 if i % 2 == 0 else 0
    return f

def sample(img, H, n, disp):
    """module means with a smooth displacement field (K x K control points, module units)"""
    o = np.array([0.3, 0.5, 0.7])
    gx, gy = np.meshgrid(np.arange(n), np.arange(n))
    px = (gx[..., None, None] + o[None, None, :, None]).repeat(3, -1).ravel()
    py = (gy[..., None, None] + o[None, None, None, :]).repeat(3, -2).ravel()
    # bilinear displacement from the control net over [0, n]
    u = np.clip(px / n * (K - 1), 0, K - 1 - 1e-6); v = np.clip(py / n * (K - 1), 0, K - 1 - 1e-6)
    i0, j0 = u.astype(int), v.astype(int); fu, fv = u - i0, v - j0
    def interp(D):
        return (D[j0, i0] * (1 - fu) * (1 - fv) + D[j0, i0 + 1] * fu * (1 - fv) + D[j0 + 1, i0] * (1 - fu) * fv + D[j0 + 1, i0 + 1] * fu * fv)
    pts = np.stack([px + interp(disp[..., 0]), py + interp(disp[..., 1])], -1).astype(np.float32)[None]
    q = cv2.perspectiveTransform(pts, H)[0]
    vals = cv2.remap(img, q[:, 0].reshape(1, -1), q[:, 1].reshape(1, -1), cv2.INTER_LINEAR)
    return vals.reshape(n, n, 9).mean(-1)

def objective(vals, fixed):
    # neighbour contrast: an aligned grid samples each module inside it, so adjacent modules
    # differ strongly; a misaligned one straddles edges and averages them out
    sc = np.std(vals) + 1e-3
    nb = (np.abs(np.diff(vals, axis=0)).mean() + np.abs(np.diff(vals, axis=1)).mean()) / sc
    loc = cv2.blur(vals, (7, 7)); m = vals - loc; fx = fixed >= 0
    return nb + (np.where(fixed[fx] == 1, -m[fx], m[fx]) / sc).clip(-2, 2).mean()

def objective_old(vals, fixed):
    loc = cv2.blur(vals, (7, 7))
    m = (vals - loc)                               # >0 light, <0 dark relative to surroundings
    sc = np.std(vals) + 1e-3
    data = np.abs(m[fixed < 0]).mean() / sc        # data modules: clearly light or dark
    fx = fixed >= 0
    agree = (np.where(fixed[fx] == 1, -m[fx], m[fx]) / sc).clip(-2, 2).mean()  # fixed patterns right way round
    return data + 2 * agree

def refine_and_decode(img, quad, n=21, budget_s=30.0):
    global K
    for K in (3, 4):
        r = _refine(img, quad, n, budget_s)
        if r: return r
    return None

def _refine(img, quad, n=21, budget_s=1.0):
    src = np.float32([[0, 0], [n, 0], [n, n], [0, n]]); H = cv2.getPerspectiveTransform(src, np.float32(quad))
    fixed = fixed_v1(n); disp = np.zeros((K, K, 2), np.float32)
    def tries(vals):
        t = cv2.threshold(np.clip(vals, 0, 255).astype(np.uint8), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[0]
        for bits in (vals < t, vals < cv2.blur(vals, (7, 7)), vals < cv2.blur(vals, (5, 5)) - 2, vals < t - 10, vals < t + 10):
            r = decode_bits(bits)
            if r: return r
    best = objective(sample(img, H, n, disp), fixed); t0 = time.time()
    for step in (0.6, 0.35, 0.2, 0.1, 0.05):
        improved = True
        while improved and time.time() - t0 < budget_s:
            improved = False
            for j in range(K):
                for i in range(K):
                    for ax in (0, 1):
                        for s in (step, -step):
                            disp[j, i, ax] += s
                            o = objective(sample(img, H, n, disp), fixed)
                            if o > best + 1e-4:
                                best = o; improved = True; break
                            disp[j, i, ax] -= s
        r = tries(sample(img, H, n, disp))
        if r: return r
    return None

if __name__ == "__main__":
    det = cv2.QRCodeDetector(); aru = cv2.QRCodeDetectorAruco()
    files = sorted(glob.glob(sys.argv[1] + "/*.png")); straight = flex = 0; t0 = time.time()
    for p in files:
        c = cv2.imread(p, 0); s = stretch(c)
        u = cv2.resize(cv2.GaussianBlur(s, (0, 0), 0.6), None, fx=4, fy=4, interpolation=cv2.INTER_CUBIC).astype(np.float32)
        quad = None
        for d in (det, aru):
            try: r, pts = d.detect(u.astype(np.uint8))
            except cv2.error: r = False
            if r: quad = pts.reshape(-1, 2).astype(np.float32); break
        if quad is None: print(os.path.basename(p), "-"); continue
        a = try_quad(u, quad)
        b = None if a else refine_and_decode(u, quad)
        straight += bool(a); flex += bool(b)
        print(os.path.basename(p), "straight:", a or "", "| flexible:", b or "")
    print(f"straight grid {straight}, flexible grid adds {flex}, of {len(files)}  ({time.time() - t0:.1f}s)")
