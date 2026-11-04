"""Prototype: discrete solver for under-resolved QR codes (below about 2 px per module), in our
own form of the process in Cognex US11151346.

Model: each pixel is the light level minus the contrast times the blurred area of the dark modules
it overlaps, I_p = L(x, y) - C * sum_k A_pk d_k, with d_k in {0, 1} and A_pk the area of module k
inside pixel p (pixel aperture, Gaussian blur sigma). Quiet zone modules are light.

1. geometry: homography from the three finders, refined on the pixels the function modules
   (finders, separators, quiet zone) explain; light level (plane) and contrast by least squares
2. modules: known modules fixed (function patterns, format once found, codewords shared with the
   case's codes already read); the others start from least squares and are then solved as binary:
   for each unknown module the 3 x 3 neighbourhood patterns (up to 512) are tested against the
   pixels they cover, the best kept, sweeping until nothing changes
3. confidence of a module: the smallest rise in squared error when it is flipped, over the nine
   3 x 3 neighbourhoods that contain it (all eight neighbours' tests must agree); the least
   certain codewords are erasures for Reed-Solomon (two check symbols in reserve)
4. the corrected symbol is redrawn and must parse

Results (2026-10-27): synthetic version 1-M (contrast 20, noise sd 2.5, corners off by up to 0.5 px)
10/10 read at 2.0, 1.75 and 1.5 px/module, 3/10 at 1.25, 0/10 at 1.0, never wrongly; nothing from
random images. TN excise image (1.9 px/module): the four codes no engine step reads all decode
(RS corrects 2-5 codewords): one new serial and three second labels of serials read elsewhere in
the image (the rectified images correlate 0.69-0.88 with those labels, under 0.15-0.61 with the
others). Shrink-wrap crops (2.3 px/module): 12 of 23, none against the engine's reads.
Known content (knownqr) assumes digit runs in numeric mode; the TN printer encodes otherwise, so the
engine version imposes shared codewords only when the solved modules agree with them."""
import sys, os; sys.path.insert(0, os.path.dirname(__file__))
import itertools
import numpy as np, cv2
from scipy.ndimage import gaussian_filter
from qrcodegen import QrCode
from qrsoft import reference, function_mask, ECLS
from knownqr import known_from_texts, truth_for, read_codewords, rs_known, redraw

SUB = 4                                                       # sub-samples per pixel side


def overlap(H, n, shape, sigma, qz=2.0):
    """H: 3x3 module -> pixel. Returns pixel coordinates (N x 2) and A (N x n*n): blurred area of
    each module in each pixel; pixels whose centre lies outside the code plus qz modules are left out."""
    corners = cv2.perspectiveTransform(np.float32([[[-qz, -qz], [n + qz, -qz], [n + qz, n + qz], [-qz, n + qz]]]), H)[0]
    x0, y0 = np.floor(corners.min(0)).astype(int) - 2; x1, y1 = np.ceil(corners.max(0)).astype(int) + 3
    x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, shape[1]), min(y1, shape[0])
    w, h = x1 - x0, y1 - y0
    Hi = np.linalg.inv(H)
    o = (np.arange(SUB) + 0.5) / SUB - 0.5
    ys, xs = np.mgrid[y0:y1, x0:x1]
    fx = (xs[..., None, None] + o[None, None, None, :]).repeat(SUB, -2)
    fy = (ys[..., None, None] + o[None, None, :, None]).repeat(SUB, -1)
    uv = cv2.perspectiveTransform(np.stack([fx.ravel(), fy.ravel()], -1).astype(np.float64)[None], Hi)[0]
    mu, mv = np.floor(uv[:, 0]).astype(int), np.floor(uv[:, 1]).astype(int)
    inside = (mu >= 0) & (mu < n) & (mv >= 0) & (mv < n)
    k = np.where(inside, mv * n + mu, n * n)                   # n*n = light (quiet zone, label)
    pix = np.repeat(np.arange(w * h), SUB * SUB)
    box = np.zeros((w * h, n * n + 1), np.float32)
    np.add.at(box, (pix, k), 1.0 / (SUB * SUB))
    A = box[:, :n * n].reshape(h, w, n * n)
    if sigma > 0.05:
        A = gaussian_filter(A, (sigma, sigma, 0), mode='constant', truncate=3.0)
    cuv = cv2.perspectiveTransform(np.stack([xs.ravel(), ys.ravel()], -1).astype(np.float64)[None], Hi)[0]
    keep = (cuv[:, 0] > -qz) & (cuv[:, 0] < n + qz) & (cuv[:, 1] > -qz) & (cuv[:, 1] < n + qz)
    P = np.stack([xs.ravel(), ys.ravel()], -1)[keep]
    return P, A.reshape(w * h, n * n)[keep]


def fit_levels(I, P, Ad):
    """I = a0 + a1 x + a2 y - c * Ad (least squares). Returns model, params"""
    c0 = P.mean(0)
    X = np.stack([np.ones(len(I)), (P[:, 0] - c0[0]) / 20, (P[:, 1] - c0[1]) / 20, -Ad], -1)
    th = np.linalg.lstsq(X, I, rcond=None)[0]
    return X @ th, th


# ---- locating (prototype only: the engine has its own homography)
def locate(img, cx, cy, half=70, mods=(1.7, 1.9, 2.1, 2.3)):
    """rough homography (module -> pixel) of a version 1 code near (cx, cy), from the finders"""
    import finders
    x0, y0 = max(0, int(cx) - half), max(0, int(cy) - half)
    c = img[y0:y0 + 2 * half, x0:x0 + 2 * half].astype(np.float32)
    lo, hi = np.percentile(c, (1, 99.5)); s = np.clip((c - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.float32)
    u = cv2.resize(cv2.GaussianBlur(s, (0, 0), 0.6), None, fx=4, fy=4, interpolation=cv2.INTER_CUBIC)
    out = []
    for m in mods:
        pk = finders.find_finders(u, 4 * m)
        q = finders.quad_from_finders(pk, 4 * m)
        if q is None: continue
        q = (q + 0.5) / 4 - 0.5 + [x0, y0]
        out.append(cv2.getPerspectiveTransform(np.float32([[0, 0], [21, 0], [21, 21], [0, 21]]), np.float32(q)))
    return out


class Solver:
    def __init__(s, img, H, n=21, sigma=0.6):
        s.img = img.astype(np.float64); s.H = H.astype(np.float64); s.n = n; s.sigma = sigma
        v = (n - 17) // 4
        ref = reference(v, QrCode.Ecc.MEDIUM, 0)
        s.known = -np.ones((n, n))                              # -1 unknown, else 0 / 1
        fm = function_mask(v)
        fmt = np.zeros((n, n), bool); fmt[8, :9] = fmt[:9, 8] = True; fmt[8, n - 8:] = fmt[n - 7:, 8] = True
        fmt[6, :] = fmt[:, 6] = False; fmt[n - 8, 8] = False      # dark module stays known
        s.fmt = fmt
        for y in range(n):
            for x in range(n):
                if fm[y, x] and not fmt[y, x]: s.known[y, x] = int(ref._modules[y][x])
        s.build()

    def build(s):
        s.P, s.A = overlap(s.H, s.n, s.img.shape, s.sigma)
        s.I = s.img[s.P[:, 1], s.P[:, 0]]

    def residual(s, d):
        model, th = fit_levels(s.I, s.P, s.A @ d.ravel())
        return s.I - model, th

    # -- geometry: corners and blur refined on pixels the known modules explain
    def refine_geometry(s, d=None, iters=2):
        from scipy.optimize import least_squares
        n = s.n; src = np.float32([[0, 0], [n, 0], [n, n], [0, n]])
        q0 = cv2.perspectiveTransform(src[None].astype(np.float64), s.H)[0]
        if d is None:
            d = np.where(s.known >= 0, s.known, 0.5)
        dd = d.ravel(); only_known = (np.asarray(d) == 0.5).any()

        def res(p):
            q = q0 + p[:8].reshape(4, 2)
            H = cv2.getPerspectiveTransform(src, np.float32(q)).astype(np.float64)
            P, A = overlap(H, n, s.img.shape, max(0.05, p[8]))
            # fixed pixel set: sample at the reference pixels (missing ones count as light model error)
            I = s.img[P[:, 1], P[:, 0]]
            r, _ = fit_levels(I, P, A @ dd)
            r = I - r
            if only_known:
                r = r * (A[:, (s.known.ravel() < 0)].sum(1) < 0.15)
            out = np.zeros(nref); key = P[:, 1] * 100000 + P[:, 0]
            idx = np.searchsorted(refkey, key); ok = (idx < nref) & (refkey[np.minimum(idx, nref - 1)] == key)
            out[idx[ok]] = r[ok]
            return out
        # reference pixel set: a generous box
        P0, _ = overlap(s.H, n, s.img.shape, s.sigma, qz=3.5)
        refkey = np.sort(P0[:, 1] * 100000 + P0[:, 0]); nref = len(refkey)
        p = np.zeros(9); p[8] = s.sigma
        r = least_squares(res, p, diff_step=[0.05] * 8 + [0.05], bounds=([-4] * 8 + [0.05], [4] * 8 + [2.0]))
        q = q0 + r.x[:8].reshape(4, 2)
        s.H = cv2.getPerspectiveTransform(src, np.float32(q)).astype(np.float64); s.sigma = r.x[8]
        s.build()
        return r.cost

    # -- modules
    def init_lsq(s, reg=0.3):
        """continuous darkness of the unknown modules by ridge least squares"""
        k = s.known.ravel(); U = np.where(k < 0)[0]
        d = np.where(k >= 0, k, 0.5).astype(np.float64)
        for _ in range(3):
            _, th = s.residual(d.reshape(s.n, s.n))
            c = th[3]
            base = s.I - (np.stack([np.ones(len(s.I)), (s.P[:, 0] - s.P[:, 0].mean()) / 20, (s.P[:, 1] - s.P[:, 1].mean()) / 20], -1) @ th[:3])
            rhs = -(base) / max(c, 1e-6) - s.A[:, k >= 0] @ k[k >= 0]
            AU = s.A[:, U]
            x = np.linalg.solve(AU.T @ AU + reg * np.eye(len(U)), AU.T @ rhs + reg * 0.5)
            d[U] = np.clip(x, 0, 1)
        return d.reshape(s.n, s.n)

    def solve(s, d, sweeps=8):
        """binary modules by 3x3 neighbourhood pattern tests; returns d (0/1), confidence (n x n)"""
        n = s.n; k = s.known.ravel()
        d = np.where(k >= 0, k, (d.ravel() > 0.5)).astype(np.float64)
        unk = np.where(k < 0)[0]
        r, th = s.residual(d.reshape(n, n)); c = th[3]
        blocks = {}
        for m in unk:
            y, x = divmod(m, n)
            blk = [yy * n + xx for yy in range(y - 1, y + 2) for xx in range(x - 1, x + 2)
                   if 0 <= yy < n and 0 <= xx < n and k[yy * n + xx] < 0]
            rows = np.where(s.A[:, blk].max(1) > 1e-3)[0]
            pats = np.array(list(itertools.product((0.0, 1.0), repeat=len(blk))))
            blocks[m] = (blk, rows, pats)
        # order: work inward from the known modules (most known neighbours first)
        kn = (s.known >= 0).astype(np.float32)
        nk = cv2.blur(kn, (3, 3), borderType=cv2.BORDER_CONSTANT).ravel()
        order = sorted(unk, key=lambda m: -nk[m])
        for it in range(sweeps):
            changed = 0
            for m in order:
                blk, rows, pats = blocks[m]
                cur = d[blk]
                Ab = s.A[np.ix_(rows, blk)]                          # |rows| x |blk|
                # residual r = I - model; model has -c * A d, so changing d by delta changes r by + c * A delta
                R = r[rows, None] + c * Ab @ (pats - cur).T
                e = (R * R).sum(0); b = int(np.argmin(e))
                if (pats[b] != cur).any():
                    delta = pats[b] - cur
                    r[rows] += c * Ab @ delta; d[blk] = pats[b]; changed += 1
            r, th = s.residual(d.reshape(n, n)); c = th[3]
            if not changed: break
        # confidence: smallest rise in squared error when the module is flipped, over the blocks containing it
        var = max(1e-6, (r * r).mean())
        conf = np.full(n * n, 1e9)
        for m in order:
            blk, rows, pats = blocks[m]
            cur = d[blk]
            Ab = s.A[np.ix_(rows, blk)]
            R = r[rows, None] + c * Ab @ (pats - cur).T
            e = (R * R).sum(0); e0 = (r[rows] ** 2).sum()
            for j, mm in enumerate(blk):
                fl = pats[:, j] != cur[j]
                conf[mm] = min(conf[mm], (e[fl].min() - e0) / (2 * var))
        conf[k >= 0] = 1e9
        s.c, s.var = c, var
        return d.reshape(n, n), conf.reshape(n, n)

    def find_format(s, d, conf, ecls=ECLS):
        """best (ecl, mask) for the solved format modules, weighted by confidence"""
        n = s.n; best = []
        sg = np.where(d > 0.5, 1.0, -1.0) * np.minimum(np.maximum(conf, 0), 20)
        for ecl in ecls:
            for m in range(8):
                q = reference((n - 17) // 4, ecl, m)
                fmm = np.array([[1.0 if q._modules[y][x] else -1.0 for x in range(n)] for y in range(n)])
                best.append((float((sg * fmm)[s.fmt].sum()), ecl, m))
        best.sort(key=lambda b: -b[0])
        return best

    def set_format(s, ecl, m):
        q = reference((s.n - 17) // 4, ecl, m)
        for y, x in zip(*np.where(s.fmt)): s.known[y, x] = int(q._modules[y][x])


def decode(img, H, texts=(), sigma=0.6, verbose=False, ecls=ECLS, n=21, max_formats=2):
    """img grey, H module -> pixel. Returns text or None."""
    S = Solver(img, H, n, sigma)
    S.refine_geometry()
    d = S.init_lsq(); S.refine_geometry(d)
    d = S.init_lsq()
    d, conf = S.solve(d)
    fmts = S.find_format(d, conf, ecls)
    base_known = S.known.copy()
    for _, ecl, m in fmts[:max_formats]:
        S.known = base_known.copy(); S.set_format(ecl, m)
        kd = known_from_texts(texts, (n - 17) // 4, ecl) if len(texts) >= 2 else {}
        if kd:
            t = truth_for(kd, (n - 17) // 4, ecl, m)
            S.known = np.where(t >= 0, t, S.known)
        d2 = S.init_lsq(); S.refine_geometry(d2); d2 = S.init_lsq()
        d2, conf2 = S.solve(d2)
        blocks, bconf, sizes = read_codewords(d2 > 0.5, conf2, (n - 17) // 4, ecl, m)
        fx = rs_known(blocks, bconf, sizes, kd)
        if verbose: print(f"  format {ecl.ordinal}/{m}: c={S.c:.1f} sd={np.sqrt(S.var):.2f} sigma={S.sigma:.2f} known={int((S.known >= 0).sum())} rs={'ok' if fx else 'fail'}")
        if fx:
            t = redraw(fx, (n - 17) // 4, ecl, m)
            if t: return t
    return None
