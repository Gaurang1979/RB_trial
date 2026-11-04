"""Prototype: known-content Data Matrix decoding from n x n module means (canonical orientation).
Known data codewords = equal in every code of the product already read; anchors = L finder,
clock tracks and known codewords; a linear classifier on each module's 3 x 3 neighbourhood of
means, trained on the anchors; RS with the known codewords fixed, least certain others erased."""
import numpy as np, reedsolo
from dmenc import SIZES, ascii_cw, pad, encode
from dmsoft import placement_map
from dmfit import decode_bits
def known_cw(texts, n):
    nd, ne = SIZES[n]; cws = []
    for t in texts:
        c = ascii_cw(t)
        if len(c) <= nd: cws.append(pad(c, nd))
    if len(cws) < 2: return {}
    return {j: cws[0][j] for j in range(nd) if all(c[j] == cws[0][j] for c in cws)}
def fixed_pattern(n):
    f = -np.ones((n, n))
    f[:, 0] = 1; f[n - 1, :] = 1
    for i in range(n): f[0, i] = 1 if i % 2 == 0 else 0; f[i, n - 1] = 1 if (n - 1 - i) % 2 == 0 else 0
    f[n - 1, :] = 1; f[:, 0] = 1
    return f
def decode_known(v, n, kd):
    nd, ne = SIZES[n]; pos = placement_map(n)
    truth = fixed_pattern(n)
    for j, val in kd.items():
        for b in range(8):
            r, c = pos[(j, b)]; truth[r, c] = (val >> (7 - b)) & 1
    T = truth >= 0
    P = np.pad(v, 1, mode='edge')
    F = np.stack([P[1 + dy:1 + dy + n, 1 + dx:1 + dx + n] for dy in (-1, 0, 1) for dx in (-1, 0, 1)], -1)
    F = (F - F[T].mean()) / (F[T].std() + 1e-6)
    X = np.hstack([F.reshape(-1, 9), np.ones((n * n, 1))]); t = np.where(truth.ravel() > 0.5, 1.0, -1.0)
    m = T.ravel()
    for lam in (0.1, 1.0):
        w = np.linalg.solve(X[m].T @ X[m] + lam * np.eye(10), X[m].T @ t[m])
        s = (X @ w).reshape(n, n)
        cws, conf = [], []
        for k in range(nd + ne):
            val, cm = 0, 9.0
            for b in range(8):
                r, c = pos[(k, b)]; val |= int(s[r, c] > 0) << (7 - b); cm = min(cm, abs(s[r, c]))
            cws.append(val); conf.append(cm)
        for j, val in kd.items(): cws[j] = val; conf[j] = 1e9
        rs = reedsolo.RSCodec(ne, fcr=1, prim=0x12D, generator=2, c_exp=8)
        order = list(np.argsort(conf)); unk = sum(1 for x in conf if x < 1e9)
        for f in range(0, min(ne - 2, unk) + 1):
            er = sorted(int(i) for i in order[:f])
            try: dec, full, errata = rs.decode(bytearray(cws), erase_pos=er or None)
            except reedsolo.ReedSolomonError: continue
            e = len([p for p in errata if p not in er])
            if 2 * e + f > ne - 2 or any(full[j] != v_ for j, v_ in kd.items()): continue
            text = decode_bits(encode(None, n, cw=list(dec)))
            if text: return text
    return None
