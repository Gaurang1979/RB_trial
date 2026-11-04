"""R9 study, QR variant (11.bmp: 48 bottles, one bent cap unread). Same PSF fit as psf.py; the
printer's QR encoding (segment kind, EC level) learned from the read codes by the no-blur residual,
mask per code the same way. Unread regions: engine-style module-sample score (match.py) against the
PSF score through the neighbours' kernel."""
import sys, os, re, cv2, numpy as np
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml/experiments')); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import engine
from qrcodegen import QrCode, QrSegment
from psf import fit_psf, render, blur, code_box, levels_fit, homography, SUB
from match import crop_up, zgrid, orient, mask_from_format, score, DATA
from finders import find_finders, quad_from_finders
from gridfit2 import grid_values

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
ECLS = {'L': QrCode.Ecc.LOW, 'M': QrCode.Ecc.MEDIUM, 'Q': QrCode.Ecc.QUARTILE, 'H': QrCode.Ecc.HIGH}
_P = {}


def pattern(text, kind, ecl, mask, split):
    k = (text, kind, ecl, mask, split)
    if k in _P: return _P[k]
    if kind == 'an+num': segs = [QrSegment.make_alphanumeric(text[:split]), QrSegment.make_numeric(text[split:])]
    elif kind == 'an': segs = [QrSegment.make_alphanumeric(text)]
    else: segs = [QrSegment.make_bytes(text.encode())]
    q = QrCode.encode_segments(segs, ECLS[ecl], 1, 40, mask, False)
    n = q.get_size()
    _P[k] = np.array([[1.0 if q.get_module(x, y) else 0.0 for x in range(n)] for y in range(n)], np.float32)
    return _P[k]


def rms0(img, P, quad):
    n = P.shape[0]; mpx = np.linalg.norm(quad[1] - quad[0]) / n
    box = code_box(quad, img, 1.5 * mpx); x0, y0, x1, y1 = box
    pix = img[y0:y1, x0:x1].astype(np.float32)
    H = homography(quad, n); ys, xs = np.mgrid[y0:y1, x0:x1]
    uv = cv2.perspectiveTransform(np.stack([xs.ravel(), ys.ravel()], 1).astype(np.float32)[None], np.linalg.inv(H))[0] / SUB - 1
    mask = ((uv[:, 0] > -1) & (uv[:, 0] < n + 1) & (uv[:, 1] > -1) & (uv[:, 1] < n + 1)).reshape(pix.shape)
    out = []
    for rot in range(4):
        S = blur(render(np.rot90(P, rot), quad, 0.0, box), 0.8)   # a little blur: the sharp model never fits real pixels
        out.append((levels_fit(S, pix, mask)[0], rot))
    return min(out)


def main():
    path, expected = sys.argv[1], int(sys.argv[2]); budget = int(sys.argv[3]) if len(sys.argv) > 3 else 20000
    g8 = cv2.imread(path, 0)
    codes, st = engine.decode(g8, fmt=1, expected=expected, budget_ms=budget, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
    texts = [c[0] for c in codes]
    print(f'read {len(texts)} of {expected}; candidate regions {len(st.miss_rects)}')
    m = re.match(r'^(.*?)(\d+)$', texts[0]); pre, w = m.group(1), len(m.group(2)); split = len(pre)
    # the printer's encoding: kind and EC level by the no-blur residual on three read codes, over masks
    best = None
    for kind in ('an+num', 'an', 'bytes'):
        for ecl in 'LMQH':
            tot = 0
            for t, q, *_ in codes[:3]:
                tot += min(rms0(g8, pattern(t, kind, ecl, mk, split), q)[0] for mk in range(8))
            if best is None or tot < best[0]: best = (tot, kind, ecl)
    _, kind, ecl = best
    n = pattern(texts[0], kind, ecl, 0, split).shape[0]
    print(f'encoding: {kind}, EC {ecl}, {n} modules (residual sum {best[0]:.1f})')
    cx0, cy0 = g8.shape[1] / 2, g8.shape[0] / 2
    rows = []; masks = []
    for t, q, step, _ in codes:
        mk, rot = min(((rms0(g8, pattern(t, kind, ecl, mk, split), q), mk) for mk in range(8)))[0][1], None
        cand = min(((rms0(g8, pattern(t, kind, ecl, mk, split), q), mk) for mk in range(8)))
        (r0, rot), mk = cand
        P = pattern(t, kind, ecl, mk, split)
        r = fit_psf(g8, P, q, rots=[rot])
        c = q.mean(0); rad = np.hypot(c[0] - cx0, c[1] - cy0) / np.hypot(cx0, cy0)
        zone = (min(3, int(4 * c[1] / g8.shape[0])), min(3, int(4 * c[0] / g8.shape[1])))
        sx, sy, th, rms3 = r['ellipse']
        rows.append((t, c, rad, zone, r, mk)); masks.append(mk)
        print(f"{t}  ({c[0]:5.0f},{c[1]:5.0f}) r={rad:.2f} zone {zone} mask {mk} mpx {r['mpx']:.2f}  sigma {r['sigma']:.2f}px = {r['sigma'] / r['mpx']:.2f} mod  g {r['g']:.2f}  "
              f"rms {r['rms0']:.1f} -> {r['rms']:.1f} -> {rms3:.1f} ({sx:.2f},{sy:.2f},{np.degrees(th):4.0f} deg)  step {step}")
    sig = np.array([r['sigma'] / r['mpx'] for *_, r, _ in rows]); rad = np.array([x[2] for x in rows])
    print('\nsigma (modules) by radius band:')
    for lo, hi in ((0, 0.3), (0.3, 0.5), (0.5, 0.7), (0.7, 1.01)):
        sel = (rad >= lo) & (rad < hi)
        if sel.any(): print(f'  r {lo:.1f}-{hi:.1f}: n {sel.sum():2d} median {np.median(sig[sel]):.2f} min {sig[sel].min():.2f} max {sig[sel].max():.2f}')
    zs = {}
    for t, c, _, z, r, _ in rows: zs.setdefault(z, []).append(r['sigma'] / r['mpx'])
    print('per zone: ' + ', '.join(f'{z}: {np.median(v):.2f} (n{len(v)})' for z, v in sorted(zs.items())))
    print(f'growth median {np.median([r["g"] for *_, r, _ in rows]):.2f}; anisotropy median {np.median([max(r["ellipse"][:2]) / min(r["ellipse"][:2]) for *_, r, _ in rows]):.2f}; '
          f'corr(radius, sigma) {np.corrcoef(rad, sig)[0, 1]:.2f}; masks used {sorted(set(masks))}')

    nums = sorted(int(t[len(pre):]) for t in texts); miss = expected - len(texts)
    allc = [f'{pre}{k:0{w}d}' for k in range(nums[0] - miss - 1, nums[-1] + miss + 2)]
    gaps = [c for c in allc if c not in set(texts)]
    side = float(np.median([np.mean([np.linalg.norm(q[(k + 1) % 4] - q[k]) for k in range(4)]) for _, q, *_ in codes])); mod = side / n
    print(f'\ncandidates (gaps): {[c[-3:] for c in gaps]}')
    maj = max(set(masks), key=masks.count)
    for x, y, w_, h_ in st.miss_rects:
        mx, my = x + w_ / 2, y + h_ / 2
        u, (X0, Y0) = crop_up(g8, mx, my, max(w_, h_) * 0.75)
        quads = []
        for mm in (mod * 4, mod * 3.6, mod * 4.4):
            q = quad_from_finders(find_finders(u, mm), mm)
            if q is None: continue
            for dx in range(-8, 9, 4):
                for dy in range(-8, 9, 4):
                    qq = q.copy(); qq[2] += (dx, dy); quads.append(qq)
        if not quads: continue
        d = sorted(rows, key=lambda row: np.hypot(row[1][0] - mx, row[1][1] - my))[:6]
        nsig = float(np.median([row[4]['sigma'] for row in d])); ng = float(np.median([row[4]['g'] for row in d]))
        eng = {}; fmask = []
        for qq in quads:
            z = orient(zgrid(grid_values(u, qq, n))); mk = mask_from_format(z); fmask.append(mk)
            for t in gaps:
                s = score(z, pattern(t, kind, ecl, mk, split))
                if s > eng.get(t, -9): eng[t] = s
        eng = sorted(eng.items(), key=lambda kv: -kv[1])
        if eng[0][1] < 0.35: continue   # not a code-like region (the locator's false candidates)
        print(f'\nregion ({x},{y}) {w_}x{h_}: neighbours sigma {nsig:.2f}px g {ng:.2f}; engine-style top {[(t[-3:], round(s, 2)) for t, s in eng[:3]]} ({len(quads)} quads)')
        mks = sorted({maj, max(set(fmask), key=fmask.count)})
        ps = {}; own = {}
        oq = [np.float32(qq) / 4 + [X0, Y0] for qq in quads]
        for t in gaps:
            for mk in mks:
                P = pattern(t, kind, ecl, mk, split)
                for qd in oq:
                    for rot in range(4):
                        Pr = np.rot90(P, rot)
                        box = code_box(qd, g8, mod); x0, y0, x1, y1 = box
                        if x1 - x0 < 8 or y1 - y0 < 8: continue
                        S = render(Pr, qd, ng, box); pix = g8[y0:y1, x0:x1].astype(np.float32)
                        a = (pix - pix.mean()) / (pix.std() + 1e-6)
                        for s in (nsig, nsig * 1.5, nsig * 2.0, nsig * 3.0):
                            B = -blur(S, s); b = (B - B.mean()) / (B.std() + 1e-6); v = float((a * b).mean())
                            if s == nsig and v > ps.get(t, -9): ps[t] = v
                            if v > own.get(t, -9): own[t] = v
        ps = sorted(ps.items(), key=lambda kv: -kv[1]); own = sorted(own.items(), key=lambda kv: -kv[1])
        print(f'  PSF neighbours kernel: {[(t[-3:], round(s, 2)) for t, s in ps[:3]]};  PSF own sigma 1-3x: {[(t[-3:], round(s, 2)) for t, s in own[:3]]}')


if __name__ == '__main__':
    main()
