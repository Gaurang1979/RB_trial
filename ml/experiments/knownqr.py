"""Known-content QR decoding, generic: known codewords = data codewords shared by every code of
the product already read in the case (texts encoded as the printer does: digit runs numeric,
other runs byte); grid registered on known modules; patch classifier trained on them; RS with
the known codewords fixed and the least certain others erased."""
import sys, os; sys.path.insert(0, os.path.dirname(__file__))
import numpy as np, cv2, reedsolo, re
from qrcodegen import QrCode
from qrsoft import reference, placement, block_layout, function_mask, ECLS
from gridfit2 import decode_bits
class B:
    """bit writer"""
    def __init__(s): s.b = []
    def put(s, v, n): s.b += [(v >> (n - 1 - i)) & 1 for i in range(n)]

# ---- grid registered on the known modules (smooth K x K displacement field)
def sample(img, M, n, disp, K, sub=1):
    o = np.array([0.5]) if sub == 1 else np.array([0.35, 0.5, 0.65])
    gx, gy = np.meshgrid(np.arange(n), np.arange(n))
    px = (gx[..., None, None] + o[None, None, :, None]).repeat(len(o), -1).ravel()
    py = (gy[..., None, None] + o[None, None, None, :]).repeat(len(o), -2).ravel()
    u = np.clip(px / n * (K - 1), 0, K - 1 - 1e-6); w = np.clip(py / n * (K - 1), 0, K - 1 - 1e-6)
    i0, j0 = u.astype(int), w.astype(int); fu, fv = u - i0, w - j0
    def interp(D): return (D[j0, i0] * (1 - fu) * (1 - fv) + D[j0, i0 + 1] * fu * (1 - fv) + D[j0 + 1, i0] * (1 - fu) * fv + D[j0 + 1, i0 + 1] * fu * fv)
    pts = np.stack([px + interp(disp[..., 0]), py + interp(disp[..., 1])], -1).astype(np.float32)[None]
    q = cv2.perspectiveTransform(pts, M)[0]
    vals = cv2.remap(img, q[:, 0].reshape(1, -1), q[:, 1].reshape(1, -1), cv2.INTER_LINEAR)
    return vals.reshape(n, n, len(o) ** 2).mean(-1)
def score(v, truth):
    T = truth >= 0; a = v[T]; b = truth[T]
    return -np.corrcoef(a, b)[0, 1]          # dark = 1, low value: negative correlation is good
def fit(img, M, n, truth, K=5, steps=(0.4, 0.2, 0.1, 0.05)):
    disp = np.zeros((K, K, 2), np.float32); best = score(sample(img, M, n, disp, K), truth)
    for st in steps:
        imp = True
        while imp:
            imp = False
            for j in range(K):
                for i in range(K):
                    for ax in (0, 1):
                        for s in (st, -st):
                            disp[j, i, ax] += s
                            o = score(sample(img, M, n, disp, K), truth)
                            if o > best + 1e-5: best = o; imp = True; break
                            disp[j, i, ax] -= s
    return disp, best

# ---- linear classifier on the pixels around each module, trained on the known modules
def patches(img, M, n, disp, K, r=1, sub=3):
    """per module: values on a fine grid covering the module and its 8 neighbours (module units)"""
    offs = np.linspace(-r - 0.5 + 0.5 / sub, r + 0.5 - 0.5 / sub, (2 * r + 1) * sub)
    feats = []
    for dy in offs:
        for dx in offs:
            # sample with a constant shift: reuse sample() by shifting the displacement field
            d2 = disp.copy(); d2[..., 0] += dx; d2[..., 1] += dy
            feats.append(sample(img, M, n, d2, K))
    return np.stack(feats, -1)
def ridge_fit(X, y, lam):
    Xb = np.hstack([X, np.ones((len(X), 1))]); t = np.where(y > 0.5, 1.0, -1.0)
    return np.linalg.solve(Xb.T @ Xb + lam * np.eye(Xb.shape[1]), Xb.T @ t)
def ridge_score(X, w): return np.hstack([X, np.ones((len(X), 1))]) @ w
def cv_errors(X, y, lam, k=10, seed=0):
    idx = np.random.default_rng(seed).permutation(len(y)); err = 0
    for f in range(k):
        te = idx[f::k]; tr = np.setdiff1d(idx, te)
        w = ridge_fit(X[tr], y[tr], lam); err += ((ridge_score(X[te], w) > 0) != (y[te] > 0.5)).sum()
    return err


def data_codewords(text, version, ecl):
    b = B()
    for run in re.findall(r'\d+|\D+', text):
        if run.isdigit():
            b.put(1, 4); b.put(len(run), 10 if version < 10 else 12)
            for i in range(0, len(run), 3): ch = run[i:i + 3]; b.put(int(ch), {3: 10, 2: 7, 1: 4}[len(ch)])
        else:
            bs = run.encode('latin-1'); b.put(4, 4); b.put(len(bs), 8 if version < 10 else 16)
            for x in bs: b.put(x, 8)
    order, sizes = block_layout(version, ecl); nd = sum(a for a, _ in sizes)
    bits = b.b; cap = nd * 8
    if len(bits) > cap: return None
    bits += [0] * min(4, cap - len(bits)); bits += [0] * ((-len(bits)) % 8)
    i = 0
    while len(bits) < cap: v = (0xEC, 0x11)[i % 2]; bits += [(v >> (7 - j)) & 1 for j in range(8)]; i += 1
    return [int(''.join(map(str, bits[k:k + 8])), 2) for k in range(0, cap, 8)]
def known_from_texts(texts, version, ecl):
    cws = [data_codewords(t, version, ecl) for t in texts]; cws = [c for c in cws if c]
    if len(cws) < 2: return {}
    return {j: cws[0][j] for j in range(len(cws[0])) if all(c[j] == cws[0][j] for c in cws)}
def truth_for(known_data, version, ecl, m):
    n = 17 + 4 * version; order, sizes = block_layout(version, ecl)
    known = {}; off = 0
    for bi, (dl, el) in enumerate(sizes):
        d = [known_data.get(off + k) for k in range(dl)]
        for k in range(dl):
            if d[k] is not None: known[(bi, k)] = d[k]
        if all(x is not None for x in d):
            full = reedsolo.RSCodec(el, fcr=0, prim=0x11D, generator=2, c_exp=8).encode(bytearray(d))
            for k in range(el): known[(bi, dl + k)] = full[dl + k]
        off += dl
    ref = reference(version, ecl, m); pos = placement(ref)
    truth = -np.ones((n, n)); fm = function_mask(version)
    for i, bk in enumerate(order):
        if bk in known:
            for bit in range(8):
                y, x = pos[i * 8 + bit]; truth[y, x] = int(((known[bk] >> (7 - bit)) & 1) ^ (QrCode._MASK_PATTERNS[m](x, y) == 0))
    for y in range(n):
        for x in range(n):
            if fm[y, x]: truth[y, x] = int(ref._modules[y][x])
    return truth
def read_codewords(dark, conf, version, ecl, m):
    ref = reference(version, ecl, m); pos = placement(ref); order, sizes = block_layout(version, ecl); total = len(order)
    bits = [int(dark[y, x]) ^ (QrCode._MASK_PATTERNS[m](x, y) == 0) for (y, x) in pos[:total * 8]]
    cf = [conf[y, x] for (y, x) in pos[:total * 8]]
    blocks = [[0] * (a + b) for a, b in sizes]; bconf = [[0.0] * (a + b) for a, b in sizes]
    for i, (bi, k) in enumerate(order):
        blocks[bi][k] = int(''.join(map(str, bits[i * 8:i * 8 + 8])), 2); bconf[bi][k] = min(cf[i * 8:i * 8 + 8])
    return blocks, bconf, sizes
def rs_known(blocks, bconf, sizes, known_data):
    fixed = []; off = 0
    for bi, (dl, el) in enumerate(sizes):
        blk = list(blocks[bi]); cf = list(bconf[bi]); kn = set()
        for k in range(dl):
            if off + k in known_data: blk[k] = known_data[off + k]; cf[k] = 1e9; kn.add(k)
        off += dl
        rs = reedsolo.RSCodec(el, fcr=0, prim=0x11D, generator=2, c_exp=8)
        order = list(np.argsort(cf)); unk = len(blk) - len(kn); done = None
        for f in range(0, min(el - 2, unk) + 1):
            er = sorted(int(i) for i in order[:f])
            try: dec, full, errata = rs.decode(bytearray(blk), erase_pos=er or None)
            except reedsolo.ReedSolomonError: continue
            e = len([p for p in errata if p not in er])
            if 2 * e + f > el - 2 or any(full[k] != blk[k] for k in kn): continue
            done = list(full); break
        if done is None: return None
        fixed.append(done)
    return fixed
def redraw(fixed, version, ecl, m):
    n = 17 + 4 * version; ref = reference(version, ecl, m); pos = placement(ref); order, sizes = block_layout(version, ecl); total = len(order)
    cwf = [fixed[bi][k] for (bi, k) in order]; fm = function_mask(version)
    sym = np.array([[bool(ref._modules[y][x]) if fm[y, x] else False for x in range(n)] for y in range(n)])
    for k, (y, x) in enumerate(pos):
        bt = (cwf[k >> 3] >> (7 - (k & 7))) & 1 if k < total * 8 else 0
        sym[y, x] = bool(bt) ^ (QrCode._MASK_PATTERNS[m](x, y) == 0)
    return decode_bits(sym)
def format_of(v, n):
    """soft match of the format modules against all 32 (ecl, mask)"""
    t = np.median(v); z = (t - v) / (v.std() + 1e-6); best = None
    for ecl in ECLS:
        for m in range(8):
            q = reference((n - 17) // 4, ecl, m)
            fmt = np.zeros((n, n), bool); fmt[8, :9] = fmt[:9, 8] = True; fmt[8, n - 8:] = fmt[n - 7:, 8] = True; fmt[6, :] = fmt[:, 6] = False
            fmm = np.array([[1.0 if q._modules[y][x] else -1.0 for x in range(n)] for y in range(n)])
            s = float((z * fmm)[fmt].sum())
            if best is None or s > best[0]: best = (s, ecl, m)
    return best[1], best[2]
def decode_known(img, M, version, texts, K=5):
    """img float, M: 3x3 module -> pixel (canonical orientation)"""
    n = 17 + 4 * version
    v0 = sample(img, M, n, np.zeros((K, K, 2), np.float32), K)
    ecl, m = format_of(v0, n)
    kd = known_from_texts(texts, version, ecl)
    if not kd: return None
    truth = truth_for(kd, version, ecl, m); T = truth >= 0
    disp, _ = fit(img, M, n, truth, K)
    for r, sub, lam in ((2, 2, 0.1), (1, 3, 0.1), (1, 2, 1)):
        F = patches(img, M, n, disp, K, r, sub); X = (F - F[T].mean()) / F[T].std()
        w = ridge_fit(X[T], truth[T], lam); s = ridge_score(X.reshape(-1, X.shape[-1]), w).reshape(n, n)
        blocks, bconf, sizes = read_codewords(s > 0, np.abs(s), version, ecl, m)
        fx = rs_known(blocks, bconf, sizes, kd)
        if fx:
            t = redraw(fx, version, ecl, m)
            if t: return t
    return None
