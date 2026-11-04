"""Study: anomaly detection per case slot (DALSA Astrocyte's idea), 2026-10-29. No real defective
cases exist, so real rigid-case images get defects injected into chosen slots: the product missing
(the slot painted with the case floor's tone), a torn label (a third of the label gone), a foreign
object (a dark blob over part of the slot), a different product (the slot replaced by a slot of
another product's image). Features ignore the code's content: the ring around the code (tone
histogram, edges, brightness) and whether a code-like texture is there.

Model per product: the median and the robust spread (MAD) of each feature over the normal slots
of the other images of the product (leave-one-image-out); a slot's score is its largest robust z.

    python anomaly.py <codes.csv from bench --codes> <image folder>
"""
import sys, os, csv, collections
import numpy as np, cv2
sys.path.insert(0, os.path.dirname(__file__))
from slots import fit


def features(img, c, pitch, side):
    """features of the slot at centre c in three zones: the code (its own side), the label ring
    around it (out to 1.6 sides), and the rest of the slot (out to half the pitch). Tone histogram,
    mean and spread of the label ring and the surroundings; texture of the code and of the ring"""
    h = int(pitch * 0.5)
    x, y = int(c[0]), int(c[1])
    if x - h < 0 or y - h < 0 or x + h >= img.shape[1] or y + h >= img.shape[0]: return None
    p = img[y - h:y + h, x - h:x + h].astype(np.float32)
    yy, xx = np.mgrid[-h:h, -h:h]
    d = np.maximum(np.abs(xx), np.abs(yy))                       # square rings around the centre
    k1, k2 = side * 0.5, min(side * 0.8, h - 1)
    code, ring, rest = p[d < k1], p[(d >= k1) & (d < k2)], p[d >= k2]
    gx = cv2.Sobel(p, cv2.CV_32F, 1, 0); gy = cv2.Sobel(p, cv2.CV_32F, 0, 1); g = np.hypot(gx, gy)
    gc, gr = g[d < k1], g[(d >= k1) & (d < k2)]
    hist = np.histogram(rest, bins=6, range=(0, 256))[0] / max(1, rest.size)
    return np.concatenate([hist, [ring.mean() / 255, ring.std() / 255, rest.mean() / 255, rest.std() / 255,
                                  code.std() / 255, gc.mean() / 255, gr.mean() / 255,
                                  (np.percentile(code, 90) - np.percentile(code, 10)) / 255]])


def inject(img, c, pitch, side, kind, rng, donor=None):
    h = int(pitch * 0.5); x, y = int(c[0]), int(c[1])
    if x - h < 0 or y - h < 0 or x + h >= img.shape[1] or y + h >= img.shape[0]: return None
    out = img.copy()
    sl = (slice(y - h, y + h), slice(x - h, x + h))
    if kind == 'missing':                                          # the case floor's tone, a little texture
        floor = np.percentile(img, 10)
        out[sl] = np.clip(floor + rng.normal(0, 6, (2 * h, 2 * h)), 0, 255)
    elif kind == 'torn':                                           # a third of the label gone (floor tone)
        floor = np.percentile(img, 10)
        a = out[sl].astype(np.float32); a[:, : (2 * h) // 3] = floor + rng.normal(0, 6, (2 * h, (2 * h) // 3))
        out[sl] = np.clip(a, 0, 255)
    elif kind == 'object':                                         # a dark blob over part of the slot
        cv2.circle(out, (x + int(rng.uniform(-h / 2, h / 2)), y + int(rng.uniform(-h / 2, h / 2))), int(h * 0.45), 15, -1)
    elif kind == 'other' and donor is not None:                    # another product's slot
        out[sl] = cv2.resize(donor, (2 * h, 2 * h))
    return out


def main():
    codes = collections.defaultdict(list)
    for r in csv.DictReader(open(sys.argv[1])): codes[r['file']].append((float(r['x']), float(r['y'])))
    folder = sys.argv[2]
    # products: images of the same size and code count on a lattice
    imgs = {}
    for f, pts in codes.items():
        P = np.array(pts)
        if len(P) < 12: continue
        r = fit(P)
        if r is None or np.median(r[3]) > 0.15: continue
        g = cv2.imread(os.path.join(folder, f), 0)
        if g is None: continue
        imgs[f] = (g, P, r[4])
    groups = collections.defaultdict(list)
    for f, (g, P, pitch) in imgs.items(): groups[(len(P), round(pitch / 20))].append(f)
    rng = np.random.default_rng(3)
    donors = [(f, g, P, pitch) for f, (g, P, pitch) in imgs.items()]
    tp = collections.Counter(); n_inj = collections.Counter(); fp = 0; n_norm = 0
    scores_norm, scores_bad = [], []
    for key, files in groups.items():
        if len(files) < 2: continue
        for test in files:
            # the model from the other images of the product
            train = []
            for f in files:
                if f == test: continue
                g, P, pitch = imgs[f]
                side = pitch * 0.55
                train += [v for v in (features(g, c, pitch, side) for c in P) if v is not None]
            T = np.array(train); med = np.median(T, 0); mad = np.median(np.abs(T - med), 0) * 1.4826 + 0.02
            g, P, pitch = imgs[test]; side = pitch * 0.55
            for c in P:                                            # normal slots
                v = features(g, c, pitch, side)
                if v is None: continue
                s = np.max(np.abs(v - med) / mad); scores_norm.append(s); n_norm += 1
            for kind in ('missing', 'torn', 'object', 'other'):    # injected defects, a few slots each
                for c in P[rng.choice(len(P), size=min(4, len(P)), replace=False)]:
                    donor = None
                    if kind == 'other':
                        df, dg, dP, dp = donors[rng.integers(len(donors))]
                        if df in files: continue
                        dc = dP[rng.integers(len(dP))]; hh = int(dp * 0.5)
                        if dc[1] - hh < 0 or dc[0] - hh < 0 or dc[1] + hh >= dg.shape[0] or dc[0] + hh >= dg.shape[1]: continue
                        donor = dg[int(dc[1]) - hh:int(dc[1]) + hh, int(dc[0]) - hh:int(dc[0]) + hh]
                    bad = inject(g, c, pitch, side, kind, rng, donor)
                    if bad is None: continue
                    v = features(bad, c, pitch, side)
                    if v is None: continue
                    s = np.max(np.abs(v - med) / mad); scores_bad.append((kind, s)); n_inj[kind] += 1
    sn = np.array(scores_norm)
    print(f'products with 2+ images: {sum(1 for f in groups.values() if len(f) >= 2)}; normal slots {len(sn)}')
    print('normal slot scores: p50 %.1f p99 %.1f p99.9 %.1f max %.1f' % (np.median(sn), np.percentile(sn, 99), np.percentile(sn, 99.9), sn.max()))
    for thr in (6, 8, 10, 12):
        line = f'threshold {thr:>2}: false alarms {np.mean(sn > thr) * 100:.2f}% of normal slots;'
        for kind in ('missing', 'torn', 'object', 'other'):
            ks = [s for k, s in scores_bad if k == kind]
            if ks: line += f' {kind} {sum(s > thr for s in ks)}/{len(ks)}'
        print(line)


if __name__ == '__main__':
    main()
