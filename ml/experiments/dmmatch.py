"""Prototype: Data Matrix candidate matching (crate caps, serial run CLUK A 0607152xx).
Hold-out on read codes (true text among the gaps + itself), then the unread regions."""
import sys, os, re, time, itertools, cv2, numpy as np
sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.expanduser('~/code/Barcode_Reading/ml'))
import engine
from dmfit import code_squares, grid_values
from dmenc import encode

M = os.path.expanduser('~/code/Barcode_Reading/ml/models/')
N = 16
INNER = np.zeros((N, N), bool); INNER[1:-1, 1:-1] = True


def corr(z, p, m):
    a = z[m]; b = p[m].astype(np.float32)
    a = (a - a.mean()) / (a.std() + 1e-6); b = (b - b.mean()) / (b.std() + 1e-6)
    return float((a * b).mean())


def boxes_jittered(box, m):
    (cx, cy), (w, h), a = cv2.minAreaRect(np.float32(box)); s = min(w, h)
    for dx, dy in itertools.product((-0.5, 0, 0.5), repeat=2):
        for ds in (0.96, 1.0, 1.04):
            for da in (-3, 0, 3):
                yield cv2.boxPoints(((cx + dx * m, cy + dy * m), (s * ds, s * ds), a + da))


def match(crop, side, pats):
    c = cv2.resize(crop, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC); side = side * 2
    lo, hi = np.percentile(c, (1, 99.5)); c8 = np.clip((c.astype(np.float32) - lo) * 255 / max(4, hi - lo), 0, 255).astype(np.uint8)
    cf = c8.astype(np.float32); m = side / N
    best = {}
    for box in code_squares(c8, side):
        for b in boxes_jittered(box, m):
            v = grid_values(cf, b, N); z = -(v - v.mean()) / (v.std() + 1e-6)
            for k in range(4):
                zr = np.rot90(z, k)
                if corr(zr, PAT_FIXED, ~INNER) < 0.5: continue   # L finder + clocks must fit first
                for t, p in pats.items():
                    sc = corr(zr, p, INNER)
                    if sc > best.get(t, -9): best[t] = sc
    return sorted(best.items(), key=lambda kv: -kv[1])


PAT_FIXED = encode('CLUK A 000000000', N)

if __name__ == '__main__':
    g = cv2.imread(sys.argv[1], 0); expected = int(sys.argv[2])
    codes, st = engine.decode(g, fmt=2, expected=expected, budget_ms=3000, model=M + 'codenet.onnx', enhancer=M + 'enhancer.onnx')
    texts = [c[0] for c in codes]
    pre = re.match(r'^(.*?)(\d+)$', texts[0]).group(1); w = len(texts[0]) - len(pre)
    nums = sorted(int(t[len(pre):]) for t in texts)
    miss = expected - len(texts)
    allc = [f'{pre}{n:0{w}d}' for n in range(nums[0] - miss - 1, nums[-1] + miss + 2)]
    pats = {t: encode(t, N) for t in allc}
    side = float(np.median([np.mean([np.linalg.norm(q[(k + 1) % 4] - q[k]) for k in range(4)]) for _, q, *_ in codes]))
    rs = set(texts)
    print('read', len(texts), 'side', round(side, 1), 'candidates', len([c for c in allc if c not in rs]))
    t0 = time.time(); right = wrong = none = 0; neg = []
    for t, q, *_ in codes:
        cx, cy = q.mean(0); r = int(side * 1.6)
        crop = g[max(0, int(cy) - r):int(cy) + r, max(0, int(cx) - r):int(cx) + r]
        res = match(crop, side, {c: pats[c] for c in allc if c not in rs or c == t})
        if not res: none += 1; continue
        if res[0][0] == t: right += 1
        else: wrong += 1
        neg.append(max(s for c, s in res if c != t))
        print(f'  {t}: top {res[0][0]} {res[0][1]:.2f}, true {dict(res).get(t, -9):.2f}, best wrong {neg[-1]:.2f}')
    print(f'hold-out: right {right} wrong {wrong} no square {none}; {1000 * (time.time() - t0) / len(codes):.0f} ms/code')
    for x, y, w_, h_ in st.miss_rects:
        cx, cy = x + w_ / 2, y + h_ / 2; r = int(max(w_, h_) * 0.8)
        crop = g[max(0, int(cy) - r):int(cy) + r, max(0, int(cx) - r):int(cx) + r]
        res = match(crop, side, {c: pats[c] for c in allc if c not in rs})
        print('  miss', (x, y), res[:2] if res else 'no square')
