"""Study: do the codes of a case sit on a lattice (slots), and do unread codes fall on its empty
nodes? (2026-10-29; the fixturing / slot idea of NI Vision Builder and DALSA Sherlock.)

Per image, from the code centres the engine read (bench --codes): the two pitch vectors from the
nearest-neighbour differences, an affine lattice fitted by least squares, each code's distance to
its node in pitches, and the lattice's empty nodes inside the case (the bounding box of the read
codes, grown by one pitch where codes are missing).

    python slots.py <codes_all.csv> [<codes_short.csv> <misses_short.csv>] [truth.csv]
"""
import sys, csv, collections
import numpy as np


def load_codes(path):
    by = collections.defaultdict(list)
    for r in csv.DictReader(open(path)):
        by[r['file']].append((float(r['x']), float(r['y']), r['text']))
    return by


def basis(P):
    """two pitch vectors from nearest-neighbour differences (folded to one half-plane)"""
    d = P[:, None, :] - P[None, :, :]
    dist = np.linalg.norm(d, axis=2) + np.eye(len(P)) * 1e9
    vecs = []
    for i in range(len(P)):
        for j in np.argsort(dist[i])[:4]:
            v = d[j, i]
            if v[1] < 0 or (v[1] == 0 and v[0] < 0): v = -v           # fold
            vecs.append(v)
    vecs = np.array(vecs)
    nn = np.median(np.sort(dist, 1)[:, 0])
    vecs = vecs[np.linalg.norm(vecs, axis=1) < 1.6 * nn]
    ang = np.arctan2(vecs[:, 1], vecs[:, 0]) % np.pi
    # two clusters of directions: the densest angle, then the densest at least 30 degrees away
    def dens(a): return np.sum(np.abs(((ang - a + np.pi / 2) % np.pi) - np.pi / 2) < 0.12)
    cand = sorted(ang, key=lambda a: -dens(a))
    a1 = cand[0]
    a2 = next((a for a in cand if abs(((a - a1 + np.pi / 2) % np.pi) - np.pi / 2) > np.radians(30)), None)
    if a2 is None: return None
    out = []
    for a in (a1, a2):
        sel = np.abs(((ang - a + np.pi / 2) % np.pi) - np.pi / 2) < 0.12
        v = vecs[sel]
        ref = np.array([np.cos(a), np.sin(a)])
        v = np.where((v @ ref)[:, None] < 0, -v, v)
        out.append(np.median(v, 0))
    return np.array(out)


def fit(P):
    B = basis(P)
    if B is None: return None
    o = P[np.argmin(P.sum(1))]
    for _ in range(3):                                            # indices, then refit
        ij = np.round(np.linalg.solve(B.T, (P - o).T).T)
        A = np.hstack([np.ones((len(P), 1)), ij])
        coef, *_ = np.linalg.lstsq(A, P, rcond=None)
        o, B = coef[0], coef[1:]
    pred = A @ coef
    pitch = min(np.linalg.norm(B[0]), np.linalg.norm(B[1]))
    res = np.linalg.norm(P - pred, axis=1) / pitch
    return o, B, ij.astype(int), res, pitch


def nodes(o, B, ij):
    i0, j0 = ij.min(0); i1, j1 = ij.max(0)
    return [(i, j) for i in range(i0, i1 + 1) for j in range(j0, j1 + 1)]


def main():
    codes = load_codes(sys.argv[1])
    short = load_codes(sys.argv[2]) if len(sys.argv) > 3 else {}
    misses = collections.defaultdict(list)
    if len(sys.argv) > 3:
        for r in csv.DictReader(open(sys.argv[3])):
            misses[r['file']].append((float(r['x']) + float(r['w']) / 2, float(r['y']) + float(r['h']) / 2))
    print(f"{'image':30s} codes  grid   fill  res-med res-max  | short: read  empty-nodes  unread-on-empty  miss-regions-on-empty")
    for f in sorted(codes):
        P = np.array([(x, y) for x, y, _ in codes[f]])
        if len(P) < 6: print(f'{f:30s} {len(P):4d}  (too few)'); continue
        r = fit(P)
        if r is None: print(f'{f:30s} {len(P):4d}  (no lattice)'); continue
        o, B, ij, res, pitch = r
        nd = nodes(o, B, ij)
        dup = len(ij) - len({tuple(x) for x in ij})
        line = f'{f:30s} {len(P):4d}  {np.ptp(ij[:, 0]) + 1}x{np.ptp(ij[:, 1]) + 1:<3d} {len(set(map(tuple, ij))) / len(nd):5.2f}  {np.median(res):6.2f}  {res.max():6.2f}' + (f' dup {dup}' if dup else '')
        if f in short:
            S = {t for _, _, t in short[f]}
            unread = [(x, y) for x, y, t in codes[f] if t not in S]
            Sp = np.array([(x, y) for x, y, t in short[f]]) if short[f] else np.zeros((0, 2))
            occupied = set()
            for p in Sp:
                k = np.round(np.linalg.solve(B.T, p - o)); occupied.add((int(k[0]), int(k[1])))
            empty = [n for n in nd if n not in occupied]
            def node_of(p):
                k = np.linalg.solve(B.T, np.array(p) - o); n = np.round(k)
                return (int(n[0]), int(n[1])), np.linalg.norm((k - n) @ B) / pitch
            on = sum(1 for p in unread if node_of(p)[0] in empty and node_of(p)[1] < 0.35)
            mon = sum(1 for p in misses.get(f, []) if node_of(p)[0] in empty and node_of(p)[1] < 0.5)
            line += f'  | {len(S):3d}  {len(empty):3d}  {on}/{len(unread)}  {mon}/{len(misses.get(f, []))}'
        print(line)


if __name__ == '__main__':
    main()
