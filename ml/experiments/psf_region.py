"""R9 follow-up: PSF score of the unread regions on the data modules only (pixels whose module
falls in the data area), against the engine-style module-sample score. sigma: sharp (0.3 px),
the neighbours' kernel (from psf_qr: 1.4 px), 2 px, 3 px. Also: the fitted sigma of each candidate
on the region itself (the true code should fit with the neighbours' sigma; an impostor fits worse)."""
import sys, os, re, time, cv2, numpy as np
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml/experiments')); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import engine
from psf import render, blur, code_box, homography, levels_fit, SUB
from psf_qr import pattern
from match import crop_up, zgrid, orient, mask_from_format, score, DATA
from finders import find_finders, quad_from_finders
from gridfit2 import grid_values

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
NSIG, NG = 1.42, 0.10


def data_mask(quad, n, box):
    x0, y0, x1, y1 = box
    H = homography(quad, n); ys, xs = np.mgrid[y0:y1, x0:x1]
    uv = cv2.perspectiveTransform(np.stack([xs.ravel(), ys.ravel()], 1).astype(np.float32)[None], np.linalg.inv(H))[0] / SUB - 1
    u, v = np.floor(uv[:, 0]).astype(int), np.floor(uv[:, 1]).astype(int)
    inside = (u >= 0) & (u < n) & (v >= 0) & (v < n)
    m = np.zeros(len(u), bool)
    m[inside] = DATA[v[inside], u[inside]]
    return m.reshape(y1 - y0, x1 - x0)


def main():
    path, expected = sys.argv[1], int(sys.argv[2])
    g8 = cv2.imread(path, 0)
    codes, st = engine.decode(g8, fmt=1, expected=expected, budget_ms=20000, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
    texts = [c[0] for c in codes]
    m = re.match(r'^(.*?)(\d+)$', texts[0]); pre, w = m.group(1), len(m.group(2)); split = len(pre)
    from psf_qr import rms0
    best = None
    for kind in ('an+num', 'an', 'bytes'):
        for ecl in 'LMQH':
            tot = sum(min(rms0(g8, pattern(t, kind, ecl, mk, split), q)[0] for mk in range(8)) for t, q, *_ in codes[:3])
            if best is None or tot < best[0]: best = (tot, kind, ecl)
    _, kind, ecl = best
    n = pattern(texts[0], kind, ecl, 0, split).shape[0]
    print(f'encoding {kind} EC {ecl} {n} modules')
    nums = sorted(int(t[len(pre):]) for t in texts); miss = expected - len(texts)
    allc = [f'{pre}{k:0{w}d}' for k in range(nums[0] - miss - 1, nums[-1] + miss + 2)]
    gaps = [c for c in allc if c not in set(texts)]
    side = float(np.median([np.mean([np.linalg.norm(q[(k + 1) % 4] - q[k]) for k in range(4)]) for _, q, *_ in codes])); mod = side / n
    print(f'read {len(texts)}; gaps {[c[-3:] for c in gaps]}; module {mod:.2f} px')
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
        eng = {}; fmask = []
        for qq in quads:
            z = orient(zgrid(grid_values(u, qq, n))); mk = mask_from_format(z); fmask.append(mk)
            for t in gaps:
                s = score(z, pattern(t, kind, ecl, mk, split))
                if s > eng.get(t, -9): eng[t] = s
        eng = sorted(eng.items(), key=lambda kv: -kv[1])
        if eng[0][1] < 0.30: continue
        lead = eng[0][1] - eng[1][1]
        print(f'\nregion ({x},{y}) {w_}x{h_}: engine-style {[(t[-3:], round(s, 2)) for t, s in eng[:3]]} lead {lead:.2f}; format mask votes {sorted(set(fmask))}')
        oq = [np.float32(qq) / 4 + [X0, Y0] for qq in quads]
        mks = sorted(set(fmask))
        t0 = time.time()
        for sig in (0.3, NSIG, 2.0, 3.0):
            ps = {}
            for t in gaps:
                for mk in mks:
                    P = pattern(t, kind, ecl, mk, split)
                    for qd in oq:
                        box = code_box(qd, g8, mod); x0, y0, x1, y1 = box
                        if x1 - x0 < 8 or y1 - y0 < 8: continue
                        dm = data_mask(qd, n, box)
                        if dm.sum() < 50: continue
                        pix = g8[y0:y1, x0:x1].astype(np.float32)[dm]
                        a = (pix - pix.mean()) / (pix.std() + 1e-6)
                        for rot in range(4):
                            B = -blur(render(np.rot90(P, rot), qd, NG, box), sig)[dm]
                            b = (B - B.mean()) / (B.std() + 1e-6); v = float((a * b).mean())
                            if v > ps.get(t, -9): ps[t] = v
            ps = sorted(ps.items(), key=lambda kv: -kv[1])
            print(f'  PSF data-only sigma {sig:.2f}: {[(t[-3:], round(s, 2)) for t, s in ps[:3]]} lead {ps[0][1] - ps[1][1]:.2f}')
        print(f'  ({time.time() - t0:.0f} s for {len(gaps)} candidates x {len(mks)} masks x {len(oq)} quads x 4 sigmas)')


if __name__ == '__main__':
    main()
