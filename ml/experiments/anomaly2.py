"""Anomaly study, within the case: each slot against its siblings in the same image (same light,
same product), robust median / MAD per feature, a combined score (root mean square of the robust z
over the features) and the worst feature. Defects injected as in anomaly.py."""
import sys, os, csv, collections
import numpy as np, cv2
sys.path.insert(0, os.path.dirname(__file__))
from slots import fit
from anomaly import features, inject


def main():
    # codes and their sides from the engine itself (the slot pitch from their lattice)
    sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
    import engine
    M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
    folder = sys.argv[2]
    imgs = []
    for f in sorted(os.listdir(folder)):
        g = cv2.imread(os.path.join(folder, f), 0)
        if g is None: continue
        cs, _ = engine.decode(g, fmt=1 | 2, expected=0, budget_ms=3000, model=M + 'codenet.onnx')
        if len(cs) < 12: continue
        P = np.array([q.mean(0) for _, q, *_ in cs])
        sides = [np.mean([np.linalg.norm(q[(k + 1) % 4] - q[k]) for k in range(4)]) for _, q, *_ in cs]
        r = fit(P)
        if r is None or np.median(r[3]) > 0.15: continue
        imgs.append((f, g, P, r[4], float(np.median(sides))))
    rng = np.random.default_rng(3)
    norm_rms, norm_max, bad = [], [], []
    for f, g, P, pitch, side in imgs:
        F = [features(g, c, pitch, side) for c in P]
        ok = [i for i, v in enumerate(F) if v is not None]
        if len(ok) < 10: continue
        V = np.array([F[i] for i in ok]); med = np.median(V, 0); mad = np.median(np.abs(V - med), 0) * 1.4826 + 0.01
        def score(v):
            z = np.abs(v - med) / mad
            return np.sqrt(np.mean(z ** 2)), z.max()
        for v in V:
            a, b = score(v); norm_rms.append(a); norm_max.append(b)
        others = [x for x in imgs if x[0] != f]
        for kind in ('missing', 'torn', 'object', 'other'):
            for i in rng.choice(ok, size=min(4, len(ok)), replace=False):
                c = P[i]; donor = None
                if kind == 'other':
                    df, dg, dP, dp, _ = others[rng.integers(len(others))]
                    dc = dP[rng.integers(len(dP))]; hh = int(dp * 0.5)
                    if dc[1] - hh < 0 or dc[0] - hh < 0 or dc[1] + hh >= dg.shape[0] or dc[0] + hh >= dg.shape[1]: continue
                    donor = dg[int(dc[1]) - hh:int(dc[1]) + hh, int(dc[0]) - hh:int(dc[0]) + hh]
                b_img = inject(g, c, pitch, side, kind, rng, donor)
                if b_img is None: continue
                v = features(b_img, c, pitch, side)
                if v is None: continue
                a, m = score(v); bad.append((kind, a, m))
    nr, nm = np.array(norm_rms), np.array(norm_max)
    print(f'images {len(imgs)}, normal slots {len(nr)}')
    print('normal rms p99 %.1f p99.9 %.1f max %.1f | worst-feature p99 %.1f max %.1f' % (np.percentile(nr, 99), np.percentile(nr, 99.9), nr.max(), np.percentile(nm, 99), nm.max()))
    for name, idx, ths in (('rms', 1, (3, 4, 5, 6)), ('worst', 2, (8, 10, 12, 15))):
        base = nr if idx == 1 else nm
        for t in ths:
            line = f'{name} > {t:>2}: false alarms {np.mean(base > t) * 100:.2f}%;'
            for kind in ('missing', 'torn', 'object', 'other'):
                ks = [x[idx] for x in bad if x[0] == kind]
                if ks: line += f' {kind} {sum(s > t for s in ks)}/{len(ks)}'
            print(line)


if __name__ == '__main__':
    main()
