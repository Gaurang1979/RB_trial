"""R9 hold-out on the read codes of 11.bmp: for each read code, the finder-search quads of its own
region, candidates = gaps + its text; engine-style score vs PSF data-only score (neighbours' sigma).
(a) true text among the candidates: rank-1 rate and lead; (b) true text removed: best impostor score
(false-accept margin against 0.75 / 0.15)."""
import sys, os, re, time, cv2, numpy as np
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml/experiments')); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import engine
from psf import render, blur, code_box
from psf_qr import pattern, rms0
from psf_region import data_mask
from match import crop_up, zgrid, orient, mask_from_format, score
from finders import find_finders, quad_from_finders
from gridfit2 import grid_values

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
NSIG, NG = 1.42, 0.10


def scores(g8, u, X0, Y0, quads, cands, kind, ecl, split, n, mod):
    eng = {}; fmask = []
    for qq in quads:
        z = orient(zgrid(grid_values(u, qq, n))); mk = mask_from_format(z); fmask.append(mk)
        for t in cands:
            s = score(z, pattern(t, kind, ecl, mk, split))
            if s > eng.get(t, -9): eng[t] = s
    oq = [np.float32(qq) / 4 + [X0, Y0] for qq in quads]
    ps = {}
    for t in cands:
        for mk in sorted(set(fmask)):
            P = pattern(t, kind, ecl, mk, split)
            for qd in oq:
                box = code_box(qd, g8, mod); x0, y0, x1, y1 = box
                if x1 - x0 < 8 or y1 - y0 < 8: continue
                dm = data_mask(qd, n, box)
                if dm.sum() < 50: continue
                pix = g8[y0:y1, x0:x1].astype(np.float32)[dm]; a = (pix - pix.mean()) / (pix.std() + 1e-6)
                for rot in range(4):
                    B = -blur(render(np.rot90(P, rot), qd, NG, box), NSIG)[dm]
                    b = (B - B.mean()) / (B.std() + 1e-6); v = float((a * b).mean())
                    if v > ps.get(t, -9): ps[t] = v
    return sorted(eng.items(), key=lambda kv: -kv[1]), sorted(ps.items(), key=lambda kv: -kv[1])


def main():
    path, expected = sys.argv[1], int(sys.argv[2])
    g8 = cv2.imread(path, 0)
    codes, st = engine.decode(g8, fmt=1, expected=expected, budget_ms=20000, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
    texts = [c[0] for c in codes]
    m = re.match(r'^(.*?)(\d+)$', texts[0]); pre, w = m.group(1), len(m.group(2)); split = len(pre)
    best = None
    for kind in ('an+num', 'an', 'bytes'):
        for ecl in 'LMQH':
            tot = sum(min(rms0(g8, pattern(t, kind, ecl, mk, split), q)[0] for mk in range(8)) for t, q, *_ in codes[:3])
            if best is None or tot < best[0]: best = (tot, kind, ecl)
    _, kind, ecl = best
    n = pattern(texts[0], kind, ecl, 0, split).shape[0]
    nums = sorted(int(t[len(pre):]) for t in texts); miss = expected - len(texts)
    allc = [f'{pre}{k:0{w}d}' for k in range(nums[0] - miss - 1, nums[-1] + miss + 2)]
    gaps = [c for c in allc if c not in set(texts)]
    side = float(np.median([np.mean([np.linalg.norm(q[(k + 1) % 4] - q[k]) for k in range(4)]) for _, q, *_ in codes])); mod = side / n
    print(f'read {len(texts)}; encoding {kind} EC {ecl} {n} modules; gaps {len(gaps)}; module {mod:.2f} px')
    stats = dict(eng_ok=0, psf_ok=0, n=0, eng_lead=[], psf_lead=[], eng_true=[], psf_true=[], eng_imp=[], psf_imp=[])
    t0 = time.time()
    for t, q, *_ in codes:
        cx, cy = q.mean(0)
        u, (X0, Y0) = crop_up(g8, cx, cy, side * 1.0)
        quads = []
        for mm in (mod * 4, mod * 3.6, mod * 4.4):
            qq0 = quad_from_finders(find_finders(u, mm), mm)
            if qq0 is None: continue
            for dx in range(-8, 9, 4):
                for dy in range(-8, 9, 4):
                    qq = qq0.copy(); qq[2] += (dx, dy); quads.append(qq)
        if not quads: print(f'  {t[-3:]}: no quad'); continue
        eng, ps = scores(g8, u, X0, Y0, quads, gaps + [t], kind, ecl, split, n, mod)
        stats['n'] += 1
        de, dp = dict(eng), dict(ps)
        e_imp = max(s for c, s in eng if c != t); p_imp = max(s for c, s in ps if c != t)
        stats['eng_true'].append(de[t]); stats['psf_true'].append(dp[t]); stats['eng_imp'].append(e_imp); stats['psf_imp'].append(p_imp)
        stats['eng_lead'].append(de[t] - e_imp); stats['psf_lead'].append(dp[t] - p_imp)
        if eng[0][0] == t: stats['eng_ok'] += 1
        if ps[0][0] == t: stats['psf_ok'] += 1
        print(f'  {t[-3:]}: engine true {de[t]:.2f} impostor {e_imp:.2f} | PSF true {dp[t]:.2f} impostor {p_imp:.2f}')
    n_ = stats['n']
    print(f'\n{n_} codes, {(time.time() - t0) / max(1, n_):.1f} s/code')
    for k in ('eng', 'psf'):
        tr, im, ld = np.array(stats[k + '_true']), np.array(stats[k + '_imp']), np.array(stats[k + '_lead'])
        acc = ((tr >= 0.75) & (ld >= 0.15)).sum()
        print(f'  {k}: rank-1 {stats[k + "_ok"]}/{n_}; true score median {np.median(tr):.2f} min {tr.min():.2f}; lead median {np.median(ld):.2f} min {ld.min():.2f}; '
              f'accepted by 0.75/0.15: {acc}/{n_}; best impostor (true removed) max {im.max():.2f} median {np.median(im):.2f}; impostor >= 0.75: {(im >= 0.75).sum()}')


if __name__ == '__main__':
    main()
