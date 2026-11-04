"""R9 study: measure the lens / print PSF from every read Data Matrix of one case, then re-score the
unread cap with its neighbours' kernel.

Per read code: the known module pattern (re-encoded text) is placed through the code's homography,
dark modules grown by g (module units), blurred by a Gaussian of sigma (pixels), scaled to the
light / dark levels (closed form); sigma and g by bounded search on the pixel residual. Elliptical
(sx, sy, theta) refined from the isotropic fit. Output: sigma vs radius from the image centre and per
4 x 4 zone.

Unread cap: the engine's own score (sampled module values vs the binary pattern, as dmmatch.py)
against the PSF score (pattern rendered through the neighbours' median kernel, correlated in pixel
space), for the true serial and the impostors."""
import sys, os, re, itertools, cv2, numpy as np
from scipy.optimize import minimize_scalar, minimize
HERE = os.path.dirname(os.path.abspath(__file__))
EXP = os.path.expanduser('~/code/Barcode_Reading/ml/experiments')
sys.path.insert(0, EXP); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import engine
from dmfit import code_squares, grid_values
from dmenc import encode
from dmmatch import corr, boxes_jittered, INNER, PAT_FIXED

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
N = 16
SUB = 8  # sub-samples per module for the sharp pattern


def sharp_pattern(P, g):
    """n x n binary pattern -> hi-res darkness image (SUB per module, 1 module margin), dark grown by g"""
    n = P.shape[0]
    img = np.zeros(((n + 2) * SUB, (n + 2) * SUB), np.float32)
    k = int(round(g * SUB))
    for y, x in zip(*np.nonzero(P)):
        y0, x0 = (y + 1) * SUB - k, (x + 1) * SUB - k
        img[max(0, y0):(y + 2) * SUB + k, max(0, x0):(x + 2) * SUB + k] = 1
    return img


def homography(quad, n):
    """module coords (u, v) in [-1, n+1] of the hi-res pattern (pixel units of SUB) -> image pixels"""
    src = np.float32([[SUB, SUB], [(n + 1) * SUB, SUB], [(n + 1) * SUB, (n + 1) * SUB], [SUB, (n + 1) * SUB]])
    return cv2.getPerspectiveTransform(src, np.float32(quad))


def render(P, quad, g, box):
    """sharp darkness image of the code over pixel box (x0, y0, x1, y1), via the homography"""
    x0, y0, x1, y1 = box
    H = homography(quad, P.shape[0])
    hi = sharp_pattern(P, g)
    # warp the hi-res pattern into the box; area sampling by INTER_AREA on a downscale is not
    # available through warpPerspective, so blur the hi-res image by half a pixel first
    Hs = np.float64([[1, 0, -x0], [0, 1, -y0], [0, 0, 1]]) @ H
    scale = np.linalg.norm(quad[1] - quad[0]) / ((P.shape[0]) * SUB)   # pixels per hi-res sample
    pre = max(0.3, 0.5 / scale)
    hi = cv2.GaussianBlur(hi, (0, 0), pre)
    return cv2.warpPerspective(hi, Hs, (x1 - x0, y1 - y0), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)


def gauss_kernel(sx, sy, th):
    r = int(np.ceil(3 * max(sx, sy))) + 1
    ys, xs = np.mgrid[-r:r + 1, -r:r + 1].astype(np.float32)
    c, s = np.cos(th), np.sin(th)
    u = c * xs + s * ys; v = -s * xs + c * ys
    k = np.exp(-0.5 * ((u / sx) ** 2 + (v / sy) ** 2))
    return k / k.sum()


def blur(img, sx, sy=None, th=0.0):
    if sy is None or abs(sx - sy) < 1e-3: return cv2.GaussianBlur(img, (0, 0), sx)
    return cv2.filter2D(img, -1, gauss_kernel(sx, sy, th), borderType=cv2.BORDER_REPLICATE)


def levels_fit(B, pix, mask):
    """pix ~ L - (L - D) B: closed-form a + b B; returns residual rms and (L, D)"""
    A = np.stack([np.ones(mask.sum()), B[mask]], 1)
    coef, *_ = np.linalg.lstsq(A, pix[mask], rcond=None)
    res = pix[mask] - A @ coef
    return float(np.sqrt(np.mean(res ** 2))), (coef[0], coef[0] + coef[1])


def code_box(quad, img, margin):
    x0, y0 = np.floor(quad.min(0) - margin).astype(int); x1, y1 = np.ceil(quad.max(0) + margin).astype(int)
    return max(0, x0), max(0, y0), min(img.shape[1], x1), min(img.shape[0], y1)


def fit_psf(img, P, quad, rots=range(4)):
    """returns dict: sigma, g, rms, rms0 (no blur), rot, ellipse (sx, sy, theta, rms)"""
    n = P.shape[0]
    mpx = np.linalg.norm(quad[1] - quad[0]) / n
    box = code_box(quad, img, 1.5 * mpx)
    x0, y0, x1, y1 = box
    pix = img[y0:y1, x0:x1].astype(np.float32)
    # mask: inside the code plus one module
    H = homography(quad, n)
    ys, xs = np.mgrid[y0:y1, x0:x1]
    pts = np.stack([xs.ravel(), ys.ravel()], 1).astype(np.float32)[None]
    uv = cv2.perspectiveTransform(pts, np.linalg.inv(H))[0] / SUB - 1
    mask = ((uv[:, 0] > -1) & (uv[:, 0] < n + 1) & (uv[:, 1] > -1) & (uv[:, 1] < n + 1)).reshape(pix.shape)
    best = None
    for rot in rots:
        Pr = np.rot90(P, rot)
        S0 = render(Pr, quad, 0.0, box)
        rms0, _ = levels_fit(S0, pix, mask)
        for g in (0.0, 0.05, 0.1, 0.15, 0.2, 0.3):
            S = render(Pr, quad, g, box) if g > 0 else S0
            f = lambda s: levels_fit(blur(S, s), pix, mask)[0]
            r = minimize_scalar(f, bounds=(0.3, 4.0 * mpx), method='bounded', options={'xatol': 0.02})
            if best is None or r.fun < best['rms']:
                best = dict(sigma=float(r.x), g=g, rms=float(r.fun), rms0=rms0, rot=rot, mpx=mpx, S=S, pix=pix, mask=mask)
    # elliptical refinement
    S, pix, mask = best['S'], best['pix'], best['mask']
    f3 = lambda p: levels_fit(blur(S, max(0.3, p[0]), max(0.3, p[1]), p[2]), pix, mask)[0]
    r3 = minimize(f3, [best['sigma'], best['sigma'], 0.0], method='Nelder-Mead', options={'xatol': 0.02, 'fatol': 1e-3, 'maxiter': 80})
    best['ellipse'] = (float(max(0.3, r3.x[0])), float(max(0.3, r3.x[1])), float(r3.x[2]), float(r3.fun))
    for k in ('S', 'pix', 'mask'): best.pop(k)
    return best


def psf_scores(img, crop_origin, quads, pats, sigma, g):
    """pixel-space normalised correlation of each candidate rendered through (sigma, g) at each quad"""
    out = {}
    for quad in quads:
        for t, P in pats.items():
            for rot in range(4):
                Pr = np.rot90(P, rot)
                box = code_box(quad, img, 1.0 * np.linalg.norm(quad[1] - quad[0]) / P.shape[0])
                x0, y0, x1, y1 = box
                if x1 - x0 < 8 or y1 - y0 < 8: continue
                B = blur(render(Pr, quad, g, box), sigma)
                pix = img[y0:y1, x0:x1].astype(np.float32)
                m = np.ones(pix.shape, bool)
                a = pix[m]; b = -B[m]
                a = (a - a.mean()) / (a.std() + 1e-6); b = (b - b.mean()) / (b.std() + 1e-6)
                sc = float((a * b).mean())
                if sc > out.get(t, -9): out[t] = sc
    return sorted(out.items(), key=lambda kv: -kv[1])


def main():
    path = sys.argv[1]; expected = int(sys.argv[2]); budget = int(sys.argv[3]) if len(sys.argv) > 3 else 20000
    g8 = cv2.imread(path, 0)
    codes, st = engine.decode(g8, fmt=2, expected=expected, budget_ms=budget, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
    texts = [c[0] for c in codes]
    print(f'read {len(texts)} of {expected}; unread regions {len(st.miss_rects)}')
    cx0, cy0 = g8.shape[1] / 2, g8.shape[0] / 2
    rows = []
    for t, q, step, _ in codes:
        P = encode(t, N)
        r = fit_psf(g8, P, q)
        c = q.mean(0)
        rad = np.hypot(c[0] - cx0, c[1] - cy0) / np.hypot(cx0, cy0)
        zone = (min(3, int(4 * c[1] / g8.shape[0])), min(3, int(4 * c[0] / g8.shape[1])))
        sx, sy, th, rms3 = r['ellipse']
        rows.append((t, c, rad, zone, r))
        print(f"{t}  centre ({c[0]:5.0f},{c[1]:5.0f}) r={rad:.2f} zone {zone}  mpx {r['mpx']:.2f}  sigma {r['sigma']:.2f}px = {r['sigma'] / r['mpx']:.2f} mod  g {r['g']:.2f}  "
              f"rms {r['rms0']:.1f} -> {r['rms']:.1f} -> ellipse {rms3:.1f} ({sx:.2f},{sy:.2f},{np.degrees(th):4.0f} deg)  step {step}")
    sig = np.array([r['sigma'] / r['mpx'] for *_, r in rows]); rad = np.array([x[2] for x in rows])
    print('\nsigma (modules) by radius band:')
    for lo, hi in ((0, 0.3), (0.3, 0.5), (0.5, 0.7), (0.7, 1.01)):
        sel = (rad >= lo) & (rad < hi)
        if sel.any(): print(f'  r {lo:.1f}-{hi:.1f}: n {sel.sum():2d} median {np.median(sig[sel]):.2f} min {sig[sel].min():.2f} max {sig[sel].max():.2f}')
    print('sigma (modules) per zone (row, col):')
    zs = {}
    for t, c, _, z, r in rows: zs.setdefault(z, []).append(r['sigma'] / r['mpx'])
    for z in sorted(zs): print(f'  {z}: n {len(zs[z])} median {np.median(zs[z]):.2f}')
    gs = [r['g'] for *_, r in rows]
    print(f'growth median {np.median(gs):.2f}; ellipse anisotropy median {np.median([max(r["ellipse"][:2]) / min(r["ellipse"][:2]) for *_, r in rows]):.2f}')
    if np.corrcoef(rad, sig)[0, 1] == np.corrcoef(rad, sig)[0, 1]: print(f'corr(radius, sigma) = {np.corrcoef(rad, sig)[0, 1]:.2f}')

    # the unread cap(s): candidates = the serial run's gaps
    pre = re.match(r'^(.*?)(\d+)$', texts[0]).group(1); w = len(texts[0]) - len(pre)
    nums = sorted(int(t[len(pre):]) for t in texts); miss = expected - len(texts)
    allc = [f'{pre}{n:0{w}d}' for n in range(nums[0] - miss - 1, nums[-1] + miss + 2)]
    gaps = [c for c in allc if c not in set(texts)]
    pats = {c: encode(c, N) for c in gaps}
    side = float(np.median([np.mean([np.linalg.norm(q[(k + 1) % 4] - q[k]) for k in range(4)]) for _, q, *_ in codes]))
    print(f'\ncandidates (gaps): {gaps}')
    for x, y, w_, h_ in st.miss_rects:
        mx, my = x + w_ / 2, y + h_ / 2; r = int(max(w_, h_) * 0.8)
        X0, Y0 = max(0, int(mx) - r), max(0, int(my) - r)
        crop = g8[Y0:int(my) + r, X0:int(mx) + r]
        # neighbours' kernel: the 6 nearest read codes
        d = sorted(rows, key=lambda row: np.hypot(row[1][0] - mx, row[1][1] - my))[:6]
        nsig = float(np.median([row[4]['sigma'] for row in d])); ng = float(np.median([row[4]['g'] for row in d]))
        print(f'\nunread at ({x},{y}) {w_}x{h_}: neighbours sigma {nsig:.2f}px g {ng:.2f} (from {[row[0][-3:] for row in d]})')
        # engine-style score (module samples vs binary pattern), as dmmatch.match
        c2 = cv2.resize(crop, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC)
        lo, hi = np.percentile(c2, (1, 99.5)); c8 = np.clip((c2.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.uint8)
        cf = c8.astype(np.float32); m2 = side * 2 / N
        eng = {}; quads = []
        for box in code_squares(c8, side * 2):
            for b in boxes_jittered(box, m2):
                v = grid_values(cf, b, N); z = -(v - v.mean()) / (v.std() + 1e-6)
                for k in range(4):
                    zr = np.rot90(z, k)
                    if corr(zr, PAT_FIXED, ~INNER) < 0.5: continue
                    quads.append(np.float32(b) / 2 + [X0, Y0])
                    for t, P in pats.items():
                        sc = corr(zr, P, INNER)
                        if sc > eng.get(t, -9): eng[t] = sc
        eng = sorted(eng.items(), key=lambda kv: -kv[1])
        print(f'  engine-style: {[(t[-3:], round(s, 2)) for t, s in eng[:4]]}  ({len(quads)} boxes)')
        if not quads: continue
        # keep distinct boxes (the engine keeps every jitter; PSF scoring is slower)
        uq = []
        for qd in quads:
            if all(np.abs(qd - u).max() > 0.5 for u in uq): uq.append(qd)
        ps = psf_scores(g8, (X0, Y0), uq[:200], pats, nsig, ng)
        print(f'  PSF (neighbours kernel): {[(t[-3:], round(s, 2)) for t, s in ps[:4]]}  ({len(uq[:200])} boxes)')
        # with a kernel fitted on the region itself for each candidate (the candidate's own best sigma)
        own = {}
        for t, P in pats.items():
            bestv = -9
            for qd in uq[:40]:
                for rot in range(4):
                    Pr = np.rot90(P, rot)
                    box = code_box(qd, g8, 1.0 * np.linalg.norm(qd[1] - qd[0]) / N)
                    x0, y0, x1, y1 = box
                    if x1 - x0 < 8 or y1 - y0 < 8: continue
                    S = render(Pr, qd, ng, box); pix = g8[y0:y1, x0:x1].astype(np.float32)
                    for s in (nsig * 0.5, nsig, nsig * 1.5, nsig * 2.0, nsig * 3.0):
                        B = -blur(S, s); a = (pix - pix.mean()) / (pix.std() + 1e-6); b = (B - B.mean()) / (B.std() + 1e-6)
                        bestv = max(bestv, float((a * b).mean()))
            own[t] = bestv
        own = sorted(own.items(), key=lambda kv: -kv[1])
        print(f'  PSF (own sigma 0.5-3x): {[(t[-3:], round(s, 2)) for t, s in own[:4]]}')


if __name__ == '__main__':
    main()
