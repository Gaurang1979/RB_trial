"""Prototype: Data Matrix grid fit for codes whose quiet zone is broken (label borders) and whose
print bleeds. Find the code square (dense dark block of the known size), lay an n x n grid on it,
average each module, threshold, redraw a clean symbol and decode it (Reed-Solomon rejects wrong
guesses). Tries the 4 corner orders (the L finder decides) and a few grid sizes."""
import sys, glob, os, time, itertools, cv2, numpy as np, zxingcpp

F = zxingcpp.BarcodeFormat.DataMatrix


def render(bits, cell=8, qz=3):
    n_r, n_c = bits.shape
    img = np.full(((n_r + 2 * qz) * cell, (n_c + 2 * qz) * cell), 255, np.uint8)
    img[qz * cell:(qz + n_r) * cell, qz * cell:(qz + n_c) * cell] = 255 - np.kron(bits.astype(np.uint8), np.ones((cell, cell), np.uint8)) * 255
    return img


def decode_bits(bits):
    r = zxingcpp.read_barcodes(render(bits), formats=F, is_pure=True, try_rotate=False, try_downscale=False,
                               binarizer=zxingcpp.Binarizer.BoolCast)
    return r[0].text if r else None


def grid_values(img, quad, n, sub=3):
    src = np.float32([[0, 0], [n, 0], [n, n], [0, n]])
    H = cv2.getPerspectiveTransform(src, np.float32(quad))
    o = (np.arange(sub) + 0.5) / sub * 0.6 + 0.2
    gx, gy = np.meshgrid(np.arange(n), np.arange(n))
    px = (gx[..., None, None] + o[None, None, :, None]).repeat(sub, -1)
    py = (gy[..., None, None] + o[None, None, None, :]).repeat(sub, -2)
    pts = np.stack([px.ravel(), py.ravel()], -1).astype(np.float32)[None]
    q = cv2.perspectiveTransform(pts, H)[0]
    vals = cv2.remap(img, q[:, 0].reshape(1, -1), q[:, 1].reshape(1, -1), cv2.INTER_LINEAR)
    return vals.reshape(n, n, sub * sub).mean(-1)


def thresholds(v):
    t = cv2.threshold(np.clip(v, 0, 255).astype(np.uint8), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[0]
    for d in (0, -10, 10, -20):
        yield v < t + d
    yield v < cv2.blur(v, (5, 5))


def code_squares(img, side, n_guess=18, debug=None):
    """candidate code squares: dark modules on bright label paper, merged into a block about
    side x side; thin label borders are removed by an opening of about one and a half modules"""
    m = side / n_guess
    s = cv2.GaussianBlur(img, (0, 0), 1).astype(np.float32)
    paper = cv2.dilate(s, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (int(3 * m) | 1,) * 2))
    paper = cv2.GaussianBlur(paper, (0, 0), m)
    bright = paper > 0.5 * np.percentile(s, 99)
    dark = ((s < 0.62 * paper) & bright).astype(np.uint8) * 255
    kc = int(1.6 * m) | 1
    block = cv2.morphologyEx(dark, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (kc, kc)))
    # thick blocks only (the code): an opening of a quarter code removes border lines and text
    ko = int(0.25 * side) | 1
    core = cv2.morphologyEx(block, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (ko, ko)))
    if debug is not None: debug.extend([dark, block, core])
    out = []
    cnts, _ = cv2.findContours(core, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    for c0 in cnts:
        # the square from the closed block near this core: keeps the sharp corners the opening rounded
        m0 = np.zeros_like(core); cv2.drawContours(m0, [c0], -1, 255, -1)
        near = cv2.dilate(m0, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (ko, ko))) & block
        pts = cv2.findNonZero(near)
        if pts is None: continue
        hull = cv2.convexHull(pts)
        (cx, cy), (w, h), ang = cv2.minAreaRect(pts)
        c = hull
        if min(w, h) < 0.6 * side or max(w, h) > 1.5 * side or max(w, h) > 1.4 * min(w, h): continue
        if cv2.contourArea(c) / (w * h + 1e-6) < 0.75: continue
        box = cv2.boxPoints(((cx, cy), (w, h), ang))
        out.append(box)
        # a label border merged on one side makes the box long: the square code sits at one end
        if max(w, h) > 1.06 * min(w, h):
            e01, e12 = box[1] - box[0], box[2] - box[1]
            long_ = e01 if np.linalg.norm(e01) > np.linalg.norm(e12) else e12
            u = long_ / np.linalg.norm(long_); d = (max(w, h) - min(w, h)) / 2
            for sgn in (-1, 1):
                out.append(cv2.boxPoints(((cx + sgn * d * u[0], cy + sgn * d * u[1]), (min(w, h), min(w, h)), ang)))
    return out


def try_square(img, box, sizes):
    box = np.float32(box)
    for n in sizes:
        for shift in itertools.product((-0.5, 0, 0.5), repeat=2):
            for grow in (0, 0.03, -0.03):
                c = box.mean(0)
                b = c + (box - c) * (1 + grow) + np.float32(shift) * (np.linalg.norm(box[1] - box[0]) / n)
                for rot in range(4):
                    q = np.roll(b, rot, axis=0)
                    for mirror in (False, True):
                        qq = q[[0, 3, 2, 1]] if mirror else q
                        v = grid_values(img, qq, n)
                        for bits in thresholds(v):
                            t = decode_bits(bits)
                            if t: return t
    return None


# ---- fit the square to the Data Matrix fixed pattern: solid L (left column, bottom row) and the
# alternating clock tracks (top row, right column)
def dm_fixed(n):
    f = np.full((n, n), np.nan, np.float32)
    f[:, 0] = 1; f[n - 1, :] = 1
    f[0, :] = [1 if x % 2 == 0 else 0 for x in range(n)]
    f[:, n - 1] = [1 if (n - 1 - y) % 2 == 0 else 0 for y in range(n)]
    f[n - 1, :] = 1; f[:, 0] = 1
    return f

def dm_score(v, n):
    z = -(v - v.mean()) / (v.std() + 1e-6)
    f = dm_fixed(n); m = ~np.isnan(f); best = -9
    for k in range(4):
        g = np.rot90(f, k); mk = ~np.isnan(g)
        p = g[mk]; p = (p - p.mean()) / (p.std() + 1e-6)
        zz = z[mk]; zz = (zz - zz.mean()) / (zz.std() + 1e-6)
        best = max(best, float((zz * p).mean()))
    return best

def box_of(cx, cy, s, a):
    return cv2.boxPoints(((cx, cy), (s, s), a))

def refine(img, box, n, iters=3):
    (cx, cy), (w, h), a = cv2.minAreaRect(np.float32(box))
    s = min(w, h); m = s / n
    p = [cx, cy, s, a]
    cur = dm_score(grid_values(img, box_of(*p), n), n)
    for step in (1.0, 0.5, 0.25):
        improved = True
        while improved:
            improved = False
            for i, d in ((0, m), (1, m), (2, m), (3, 2.0)):
                for sg in (-1, 1):
                    q = list(p); q[i] += sg * d * step
                    sc = dm_score(grid_values(img, box_of(*q), n), n)
                    if sc > cur + 1e-4: cur, p, improved = sc, q, True
    return box_of(*p), cur


if __name__ == '__main__':
    files = sorted(glob.glob(sys.argv[1]))
    side = float(sys.argv[2])            # code side in these crops' pixels
    sizes = [int(x) for x in sys.argv[3].split(',')]
    ok = 0; t0 = time.time()
    for p in files:
        c = cv2.imread(p, 0)
        lo, hi = np.percentile(c, (1, 99.5)); c = np.clip((c.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.uint8)
        text = None; boxes = code_squares(c, side)
        cf = c.astype(np.float32); scored = []
        for b in boxes:
            for n in sizes:
                rb, sc = refine(cf, b, n)
                scored.append((sc, n, rb))
        scored.sort(key=lambda x: -x[0])
        for sc, n, rb in scored[:4]:
            text = try_square(cf, rb, [n])
            if text: break
        if scored: print('   best fit score', round(scored[0][0], 2), 'n', scored[0][1])
        ok += bool(text)
        print(os.path.basename(p), len(boxes), 'squares', text or '')
    print(f'decoded {ok}/{len(files)} in {time.time() - t0:.1f}s')
